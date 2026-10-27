using System.Runtime.InteropServices;

using AAEmu.Zone.Scene;

namespace AAEmu.Zone.Native.Engine;

public sealed partial class ZoneNativeEngine
{
    /// <summary>
    /// Resolves authored metadata against the actual WorldSystem registry already loaded by
    /// this CSystem. This does not activate Seamless, create terrain or announce scene readiness.
    /// The returned capability belongs to this engine generation and exposes no native pointers.
    /// </summary>
    public ZoneNativeWorldLayout ResolveWorldLayout(ZoneWorldMapLayout layout)
    {
        using var operation = EnterOperation();
        ArgumentNullException.ThrowIfNull(layout);
        ValidateWorldLayoutName(layout.World.Header.Name);
        ValidateWorldLayoutName(layout.Zone.Name);
        var worldSystem = Marshal.ReadIntPtr(_system + 0x150);
        var seamless = Marshal.ReadIntPtr(_system + 0x158);
        RequireWorldLayoutServices(worldSystem, seamless);
        // The lookup takes the XML-authored name; RequestedWorldName remains input provenance.
        var world = WorldLayoutLookup(worldSystem, 0x20, 0x63c70, layout.World.Header.Name);
        if (world == 0) throw new InvalidOperationException("CSystem did not load the configured authored World.");
        RequireWorldLayoutTable(world, 0x120658);
        var zones = ReadWorldLayoutList(world, 0x240, layout.World.Zones.Count);
        if (zones.Length != layout.World.Zones.Count)
            throw new InvalidDataException("The native World's Zone rows differ from the configured source.");
        var zone = zones[layout.Zone.SourceIndex];
        RequireWorldLayoutTable(zone, 0x1204b8);
        var byName = WorldLayoutLookup(worldSystem, 0x90, 0x63d70, layout.Zone.Name);
        var idLookup = BindWorldLayout<WorldLayoutIdCall>(worldSystem, 0x80, 0x63e20);
        var byId = InvokeWorldLayout(() => idLookup(worldSystem, layout.Zone.Id));
        if (byName != zone || byId != zone)
            throw new InvalidDataException("Native global Zone lookup does not identify the configured World row.");
        var cells = ReadWorldLayoutList(zone, 0x440, layout.Cells.Count);
        var selection = new ZoneNativeWorldLayout(this, layout, worldSystem, seamless, world, zone, cells);
        ValidateWorldLayout(selection);
        return selection;
    }

    internal void ValidateWorldLayout(ZoneNativeWorldLayout selection)
    {
        if (!_operationActive || !ReferenceEquals(selection.Owner, this))
            throw new InvalidOperationException("World layout validation requires its engine's active operation.");
        VerifyAvailable();
        RequireWorldLayoutServices(selection.WorldSystem, selection.Seamless);
        var layout = selection.Layout;
        var world = selection.World;
        var zone = selection.Zone;
        // Prove registry membership again before dereferencing the previously captured objects.
        // A rebuilt registry must not turn an old metadata capability into a stale native read.
        if (WorldLayoutLookup(selection.WorldSystem, 0x20, 0x63c70, layout.World.Header.Name) != world ||
            WorldLayoutLookup(selection.WorldSystem, 0x90, 0x63d70, layout.Zone.Name) != zone)
            throw new InvalidDataException("The configured native World or Zone registration was replaced.");
        var idLookup = BindWorldLayout<WorldLayoutIdCall>(selection.WorldSystem, 0x80, 0x63e20);
        if (InvokeWorldLayout(() => idLookup(selection.WorldSystem, layout.Zone.Id)) != zone)
            throw new InvalidDataException("Native Zone ID lookup no longer identifies the captured source row.");
        RequireWorldLayoutTable(world, 0x120658);
        RequireWorldLayoutTable(zone, 0x1204b8);
        // 36566AC0/365676F0 store the authored string and physical grid properties.
        RequireWorldLayoutText(Marshal.ReadIntPtr(world + 0x10), layout.World.Header.Name);
        if (Marshal.ReadInt32(world + 0x264) != layout.World.Header.Resolution ||
            Marshal.ReadInt32(world + 0x268) != layout.World.Header.UnitSize ||
            Marshal.ReadInt32(world + 0x26c) != layout.World.Header.CellXCount ||
            Marshal.ReadInt32(world + 0x270) != layout.World.Header.CellYCount ||
            (Marshal.ReadByte(world + 9) != 0) != layout.World.Header.IsInstance ||
            (Marshal.ReadByte(world + 0x260) != 0) != layout.World.Header.IsReleaseBranch ||
            unchecked((uint)Marshal.ReadInt32(world + 0x27c)) != layout.World.Header.NextLayerId ||
            unchecked((uint)Marshal.ReadInt32(world + 0x280)) != layout.World.Header.NextSurfaceId ||
            Marshal.ReadInt32(world + 0x274) != BitConverter.SingleToInt32Bits(layout.World.Header.OceanLevel) ||
            Marshal.ReadInt32(world + 0x278) != BitConverter.SingleToInt32Bits(layout.World.Header.MaxTerrainHeight))
            throw new InvalidDataException("Native World grid or scene properties differ from the configured content.");
        // Zone virtual+78/3653FFB0 returns its owner field+8, not storage at+78.
        if (Marshal.ReadIntPtr(zone + 8) != world || Marshal.ReadInt32(zone + 0x14) != layout.Zone.Id)
            throw new InvalidDataException("The selected native Zone has another World owner or ID.");
        RequireWorldLayoutText(Marshal.ReadIntPtr(zone + 0x428), layout.Zone.Name);
        if (Marshal.ReadInt32(zone + 0x430) != layout.Origin.X ||
            Marshal.ReadInt32(zone + 0x434) != layout.Origin.Y ||
            Marshal.ReadInt32(zone + 0x438) != layout.Zone.LastCell.X ||
            Marshal.ReadInt32(zone + 0x43c) != layout.Zone.LastCell.Y)
            throw new InvalidDataException("The native Zone origin or cell bounds differ from the configured layout.");
        var nativeZones = ReadWorldLayoutList(world, 0x240, layout.World.Zones.Count);
        if (nativeZones.Length != layout.World.Zones.Count || nativeZones[layout.Zone.SourceIndex] != zone)
            throw new InvalidDataException("The configured native Zone row was replaced.");
        var nativeCells = ReadWorldLayoutList(zone, 0x440, layout.Cells.Count);
        if (!nativeCells.SequenceEqual(selection.Cells) || nativeCells.Length != layout.Cells.Count)
            throw new InvalidDataException("Native Zone cell membership or order changed.");
        var grid = Marshal.ReadIntPtr(world + 0x220);
        var end = Marshal.ReadIntPtr(world + 0x228);
        var expectedCells = checked((long)layout.World.Header.CellXCount * layout.World.Header.CellYCount);
        if (grid == 0 || end - grid != checked(expectedCells * 8))
            throw new InvalidDataException("The native World cell grid has an inconsistent allocation.");
        for (var index = 0; index < nativeCells.Length; index++)
        {
            var cell = nativeCells[index];
            var authored = layout.Cells[index].Cell.Coordinate;
            RequireWorldLayoutTable(cell, 0x120408);
            if (Marshal.ReadIntPtr(cell + 8) != world ||
                Marshal.ReadInt32(cell + 0x31c) != authored.X ||
                Marshal.ReadInt32(cell + 0x320) != authored.Y ||
                Marshal.ReadIntPtr(grid + checked((authored.X * layout.World.Header.CellYCount + authored.Y) * 8)) != cell)
                throw new InvalidDataException("A native Zone cell does not match its authored World grid identity.");
        }
    }

    private void RequireWorldLayoutServices(nint worldSystem, nint seamless)
    {
        if (worldSystem == 0 || seamless == 0 ||
            Marshal.ReadIntPtr(_system + 0x150) != worldSystem ||
            Marshal.ReadIntPtr(_environment + 0x100) != worldSystem ||
            Marshal.ReadIntPtr(_system + 0x158) != seamless ||
            Marshal.ReadIntPtr(_environment + 0x108) != seamless)
            throw new InvalidOperationException("The WorldSystem/Seamless services do not belong to this CSystem generation.");
        RequireWorldLayoutTable(worldSystem, 0x120788);
        RequireWorldLayoutTable(seamless, 0x11f478);
    }

    private nint WorldLayoutLookup(nint system, int slot, int rva, string name)
    {
        var lookup = BindWorldLayout<WorldLayoutNameCall>(system, slot, rva);
        var text = Marshal.StringToCoTaskMemUTF8(name);
        try { return InvokeWorldLayout(() => lookup(system, text)); }
        finally { Marshal.FreeCoTaskMem(text); }
    }

    private T InvokeWorldLayout<T>(Func<T> call)
    {
        try { var result = call(); VerifyAvailable(); return result; }
        catch (Exception error) { Poison(error); throw; }
    }

    private T BindWorldLayout<T>(nint receiver, int slot, int rva) where T : Delegate
    {
        var function = VirtualAddress(receiver, slot);
        if (function != _systemModule + rva)
            throw new InvalidDataException("The native World lookup implementation changed.");
        return Marshal.GetDelegateForFunctionPointer<T>(function);
    }

    private void RequireWorldLayoutTable(nint pointer, int rva)
    {
        if (pointer == 0 || Marshal.ReadIntPtr(pointer) != _systemModule + rva)
            throw new InvalidDataException("A native World registry object has an unexpected implementation.");
    }

    private static void ValidateWorldLayoutName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 4096 || name.Any(character => character is '\0' or > '\x7f'))
            throw new NotSupportedException("Native World selection requires a bounded nonempty ASCII name.");
    }

    private static void RequireWorldLayoutText(nint pointer, string expected)
    {
        if (pointer == 0) throw new InvalidDataException("A native World identity string is absent.");
        for (var index = 0; index < expected.Length; index++)
            if (Marshal.ReadByte(pointer + index) != expected[index])
                throw new InvalidDataException("Native World/Zone names differ from the configured content.");
        if (Marshal.ReadByte(pointer + expected.Length) != 0)
            throw new InvalidDataException("A native World identity string has an unexpected suffix.");
    }

    // These are the proved native std::list layouts. Traverse without mutating the native
    // per-container iteration cursor, and validate the sentinel/count/backlinks before return.
    private static nint[] ReadWorldLayoutList(nint owner, int offset, int maximumCount)
    {
        var sentinel = Marshal.ReadIntPtr(owner + offset);
        var count = Marshal.ReadInt64(owner + offset + 8);
        if (sentinel == 0 || count < 0 || count > maximumCount)
            throw new InvalidDataException("A native World list exceeds the configured source bounds.");
        var result = new nint[(int)count];
        var visited = new HashSet<nint>();
        var values = new HashSet<nint>();
        var previous = sentinel;
        var node = Marshal.ReadIntPtr(sentinel);
        for (var index = 0; index < result.Length; index++)
        {
            if (node == 0 || node == sentinel || !visited.Add(node) || Marshal.ReadIntPtr(node + 8) != previous)
                throw new InvalidDataException("Native World list links do not match their count.");
            result[index] = Marshal.ReadIntPtr(node + 0x10);
            if (result[index] == 0 || !values.Add(result[index]))
                throw new InvalidDataException("Native World list contains a missing or repeated object.");
            previous = node;
            node = Marshal.ReadIntPtr(node);
        }
        if (node != sentinel || Marshal.ReadIntPtr(sentinel + 8) != previous)
            throw new InvalidDataException("Native World list sentinel does not close the captured rows.");
        return result;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint WorldLayoutNameCall(nint system, nint name);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint WorldLayoutIdCall(nint system, int id);
}

/// <summary>Engine-issued metadata capability. Resolution alone is not scene activation.</summary>
public sealed class ZoneNativeWorldLayout
{
    public ZoneWorldMapLayout Layout { get; }
    internal ZoneNativeEngine Owner { get; }
    internal nint WorldSystem { get; }
    internal nint Seamless { get; }
    internal nint World { get; }
    internal nint Zone { get; }
    internal IReadOnlyList<nint> Cells { get; }

    internal ZoneNativeWorldLayout(ZoneNativeEngine owner, ZoneWorldMapLayout layout,
        nint worldSystem, nint seamless, nint world, nint zone, nint[] cells)
    {
        Owner = owner; Layout = layout; WorldSystem = worldSystem; Seamless = seamless;
        World = world; Zone = zone; Cells = Array.AsReadOnly(cells);
    }
}

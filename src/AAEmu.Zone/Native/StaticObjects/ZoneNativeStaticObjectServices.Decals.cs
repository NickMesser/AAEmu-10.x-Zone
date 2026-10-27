using System.Buffers.Binary;
using System.Runtime.InteropServices;

using AAEmu.Zone.Content;

namespace AAEmu.Zone.Native.StaticObjects;

public sealed unsafe partial class ZoneNativeStaticObjectServices
{
    private readonly List<Decal> _decals = [];

    /// <summary>
    /// Calls _LoadDecal30222F10 on one exact 111-byte payload, excluding its type tag.
    /// The native loader adds (engineCellX &lt;&lt; 10, engineCellY &lt;&lt; 10, 0), applies its
    /// quality/configuration gates, resolves the low-24-bit material table index, configures the
    /// real CDecalRenderNode and registers it. Type nine has no model-table reference.
    /// Native mutation affects an owned copy; the authored payload and the caller's table remain
    /// unchanged. Null means the actual native gate skipped this row, not that the cell is ready.
    /// The native StreamMaterial owns its copied name and resolved/pending material references.
    /// Temporary resource-table references may be retired after this call.
    /// </summary>
    public Decal? CreateDecal(int engineCellX, int engineCellY, ReadOnlySpan<byte> rawPayload,
        IReadOnlyList<Material?> materials)
    {
        using var operation = Enter();
        ArgumentNullException.ThrowIfNull(materials);
        _lease.RequireTerrainEntityGrid();
        if ((uint)engineCellX >= 8 || (uint)engineCellY >= 8)
            throw new ArgumentOutOfRangeException(nameof(engineCellX), "A loaded engine cell must lie in the eight-by-eight window.");
        if (rawPayload.Length != 111)
            throw new ArgumentException("A native decal payload must contain exactly 111 bytes.", nameof(rawPayload));
        var authored = rawPayload.ToArray();
        var index = (int)(BinaryPrimitives.ReadUInt32LittleEndian(authored.AsSpan(0x6b)) & 0xffffff);
        // This is the existing content reader's managed resource-table admission bound.
        // Count and indexers are caller code, so snapshot them only under the shared busy gate.
        var count = materials.Count;
        if (count < 0 || count > ZoneCellObjectContent.MaximumFileBytes / 260 || index >= count)
            throw new ArgumentException("The decal's material index must address a bounded, complete resource table.", nameof(materials));
        var pointers = new nint[count];
        Material? selected = null;
        for (var i = 0; i < count; i++)
        {
            var material = materials[i];
            if (material is not null)
            {
                if (!ReferenceEquals(material.Owner, this) || material.Retired)
                    throw new InvalidOperationException("Every decal material must be a live reference from this service.");
                RequireTable(material.Pointer, 0x297378);
                pointers[i] = material.Pointer;
            }
            if (i == index) selected = material;
        }
        if (materials.Count != count)
            throw new InvalidOperationException("The decal material table changed while it was being captured.");
        RequireCellPublicationAvailable();
        ValidateCellPublicationService();
        RequireIdleBrushWorld();

        var value = new Decal(this, engineCellX, engineCellY, authored, index, selected);
        _decals.Add(value); // Root all partial native ownership before invoking any native callback.
        try
        {
            value.RecordPointer = Allocate(authored);
            value.CursorPointer = Marshal.AllocHGlobal(8);
            Marshal.WriteIntPtr(value.CursorPointer, value.RecordPointer);
            value.MaterialTablePointer = Marshal.AllocHGlobal(checked(count * 8));
            pointers.AsSpan().CopyTo(new Span<nint>((void*)value.MaterialTablePointer, count));
            value.MaterialTableHolder = Marshal.AllocHGlobal(8);
            Marshal.WriteIntPtr(value.MaterialTableHolder, value.MaterialTablePointer);
            value.Pointer = Native(() => Terrain<CreateDecalCall>(0x222f10)(
                engineCellX, engineCellY, value.CursorPointer, value.MaterialTableHolder, 0));
            if (value.Pointer != 0)
                value.StreamMaterialPointer = Marshal.ReadIntPtr(value.Pointer + 0x80);
            Require(Marshal.ReadIntPtr(value.CursorPointer) == value.RecordPointer + 111,
                "The native decal factory did not consume exactly one serialized row.");
            if (value.Pointer == 0)
            {
                Retire(value);
                return null;
            }
            ValidateDecal(value);
            return value;
        }
        catch (Exception error)
        {
            if (_poisoned) throw; // Uncertain native completion retains node, buffers and engine.
            try { Retire(value); }
            catch (Exception cleanup) { throw new AggregateException(error, cleanup); }
            throw;
        }
    }

    /// <summary>
    /// Completes 30225050's serialized-cell metadata after native decal registration.
    /// The caller then adds the owned result to its managed cell collection in source order.
    /// This does not insert another reference into the native serialized-cell ownership vector.
    /// </summary>
    public void CompleteCellDecal(Decal value)
    {
        using var operation = Enter();
        ArgumentNullException.ThrowIfNull(value);
        if (!ReferenceEquals(value.Owner, this) || value.Retired)
            throw new InvalidOperationException("Cell publication requires a live decal from this service.");
        if (value.IsCellPublicationComplete)
            throw new InvalidOperationException("This decal already completed cell publication metadata.");
        RequireCellPublicationAvailable();
        ValidateCellPublicationService();
        ValidateDecal(value);
        var state = Native(() => _cellPublicationByte!(_cellPublicationService));
        ValidateCellPublicationService();
        ValidateDecal(value);
        Native(() =>
        {
            Marshal.WriteByte(value.Pointer + 0x39, (byte)(Marshal.ReadByte(value.Pointer + 0x39) | 1));
            Marshal.WriteByte(value.Pointer + 0x38, state);
        });
        value.NativeCellPublicationByte = state;
        value.IsCellPublicationComplete = true;
    }

    private void ValidateDecal(Decal value)
    {
        RequireTable(value.Pointer, 0x2805e8);
        Require(Marshal.ReadInt32(value.Pointer + 0x44) == 0 &&
            Marshal.ReadIntPtr(value.Pointer + 0x80) == value.StreamMaterialPointer,
            "The owned serialized decal's IEntity or material association changed outside this service.");
        if (value.StreamMaterialPointer != 0)
        {
            RequireTable(value.StreamMaterialPointer, 0x297320);
            Require(Marshal.ReadInt32(value.StreamMaterialPointer + 8) > 0 &&
                Marshal.ReadIntPtr(value.StreamMaterialPointer + 0x30) == 0,
                "The serialized decal no longer owns its native StreamMaterial without an external listener.");
        }
    }

    private void DisposeDecal(Decal value)
    {
        if (value.Retired) return;
        using var operation = Enter();
        Retire(value);
    }

    private void Retire(Decal value)
    {
        if (value.Retired) return;
        if (value.Pointer != 0)
        {
            RequireIdleBrushWorld();
            ValidateDecal(value);
            // 3005E590 -> 3005E3A0 unregisters the node (+1F8), destroys generated decal
            // resources (3005D270), performs engine+5C8 cleanup, frees its vector, then releases
            // StreamMaterial. Its301C1B40 destructor cancels request+28 via3011F5B0 before
            // releasing its native name/material. Do not replace this with a render stub.
            Native(() => Bind<DeleteBrushCall>(value.Pointer, 0, 0x5e590)(value.Pointer, 1));
            value.Pointer = value.StreamMaterialPointer = 0;
        }
        Marshal.FreeHGlobal(value.RecordPointer);
        Marshal.FreeHGlobal(value.CursorPointer);
        Marshal.FreeHGlobal(value.MaterialTablePointer);
        Marshal.FreeHGlobal(value.MaterialTableHolder);
        value.RecordPointer = value.CursorPointer = value.MaterialTablePointer = value.MaterialTableHolder = 0;
        value.Retired = true;
        _decals.Remove(value);
    }

    // The central service Dispose must call this before releasing material/model references.
    private void RetireDecalResources()
    {
        foreach (var value in _decals.ToArray()) Retire(value);
    }

    /// <summary>
    /// One real native decal node. Disposal must precede the service and its CSystem generation;
    /// uncertain native failures keep this node and all its call inputs pinned by that service.
    /// Generated rendering resources remain native-owned. This wrapper supplies no collision stub.
    /// </summary>
    public sealed class Decal : IDisposable
    {
        private readonly byte[] _authoredPayload;
        internal ZoneNativeStaticObjectServices Owner { get; }
        internal nint Pointer, StreamMaterialPointer, RecordPointer, CursorPointer;
        internal nint MaterialTablePointer, MaterialTableHolder;
        internal bool Retired;
        public int EngineCellX { get; }
        public int EngineCellY { get; }
        public int MaterialTableIndex { get; }
        public string? MaterialPath { get; }
        public bool MaterialWasFallback { get; }
        public ReadOnlySpan<byte> AuthoredPayload => _authoredPayload;
        public bool IsCellPublicationComplete { get; internal set; }
        public byte? NativeCellPublicationByte { get; internal set; }
        public bool IsDisposed => Retired;
        internal Decal(ZoneNativeStaticObjectServices owner, int x, int y, byte[] authored,
            int materialIndex, Material? material)
        {
            Owner = owner; EngineCellX = x; EngineCellY = y; _authoredPayload = authored;
            MaterialTableIndex = materialIndex; MaterialPath = material?.Path;
            MaterialWasFallback = material?.IsFallback ?? false;
        }
        public void Dispose() => Owner.DisposeDecal(this);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint CreateDecalCall(int cellX, int cellY, nint cursor, nint materialTable, byte skip);
}

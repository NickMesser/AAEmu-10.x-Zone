using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;

using AAEmu.Zone.Content;

namespace AAEmu.Zone.Native.StaticObjects;

public sealed unsafe partial class ZoneNativeStaticObjectServices
{
    private readonly List<WaterVolume> _waterVolumes = [];

    /// <summary>
    /// Calls the real serialized type-eleven factory30223480 with a private copy of the complete
    /// 119-byte header and both authored Vec3 arrays. Native code owns quality/configuration gates,
    /// volume-ID deduplication, plane projection, triangulation, material references, registration
    /// and physical-area creation. The caller's payload and temporary material table stay unchanged.
    /// Null is a native skipped row; a returned render node can have no physical area.
    /// This does not establish complete cell or actor readiness.
    /// </summary>
    public WaterVolume? CreateWaterVolume(int engineCellX, int engineCellY, ReadOnlySpan<byte> rawPayload,
        IReadOnlyList<Material?> materials)
    {
        using var operation = Enter();
        ArgumentNullException.ThrowIfNull(materials);
        _lease.RequireTerrainEntityGrid();
        if ((uint)engineCellX >= 8 || (uint)engineCellY >= 8)
            throw new ArgumentOutOfRangeException(nameof(engineCellX), "A loaded engine cell must lie in the eight-by-eight window.");
        if (rawPayload.Length < 119 || rawPayload.Length > ZoneCellObjectContent.MaximumFileBytes)
            throw new ArgumentException("A water payload must contain its complete bounded header and vertices.", nameof(rawPayload));
        var authored = rawPayload.ToArray();
        var renderCount = BinaryPrimitives.ReadInt32LittleEndian(authored.AsSpan(0x67));
        var physicsCount = BinaryPrimitives.ReadInt32LittleEndian(authored.AsSpan(0x73));
        if (renderCount < 0 || physicsCount < 0 || 119L + 12L * ((long)renderCount + physicsCount) != authored.Length)
            throw new ArgumentException("The water payload's two vertex counts must match its exact length.", nameof(rawPayload));
        if (authored[0x27] is not (2 or 3))
            throw new NotSupportedException("This owner admits the native lake and river families present in the configured cell assets.");
        var materialIndex = BinaryPrimitives.ReadInt32LittleEndian(authored.AsSpan(0x33));
        var count = materials.Count;
        if (count < 0 || count > ZoneCellObjectContent.MaximumFileBytes / 260 || materialIndex >= count)
            throw new ArgumentException("The water material index must address a bounded complete resource table.", nameof(materials));
        var pointers = new nint[count];
        Material? selected = null;
        for (var index = 0; index < count; index++)
        {
            var material = materials[index];
            if (material is not null)
            {
                if (!ReferenceEquals(material.Owner, this) || material.Retired)
                    throw new InvalidOperationException("Every water material must be a live reference from this service.");
                RequireTable(material.Pointer, 0x297378);
                pointers[index] = material.Pointer;
            }
            if (index == materialIndex) selected = material;
        }
        if (materials.Count != count)
            throw new InvalidOperationException("The water material table changed while it was being captured.");
        RequireCellPublicationAvailable();
        ValidateCellPublicationService();
        RequireIdleWaterWorld();
        var renderer = Marshal.ReadIntPtr(_lease.Environment + 0x18);
        var mapHead = Marshal.ReadIntPtr(_lease.TerrainModule + 0x33d6a8);
        if (renderer == 0 || mapHead == 0)
            throw new NotSupportedException("Native water requires the initialized renderer and volume-ID map.");
        Require(Marshal.ReadIntPtr(_lease.TerrainModule + 0x2fa888) == renderer &&
            Marshal.ReadIntPtr(_lease.TerrainModule + 0x2fa8a0) == _lease.PhysicsWorld,
            "The native water factory services do not belong to this engine generation.");
        var value = new WaterVolume(this, engineCellX, engineCellY, authored, materialIndex, selected,
            renderer, Marshal.ReadIntPtr(renderer), mapHead);
        _waterVolumes.Add(value); // Root partial native ownership before the factory can callback.
        try
        {
            value.RecordPointer = Allocate(authored);
            value.CursorPointer = Marshal.AllocHGlobal(8);
            Marshal.WriteIntPtr(value.CursorPointer, value.RecordPointer);
            value.MaterialTablePointer = Marshal.AllocHGlobal(checked(count * 8));
            pointers.AsSpan().CopyTo(new Span<nint>((void*)value.MaterialTablePointer, count));
            value.MaterialTableHolder = Marshal.AllocHGlobal(8);
            Marshal.WriteIntPtr(value.MaterialTableHolder, value.MaterialTablePointer);
            value.Pointer = Native(() => Terrain<CreateWaterVolumeCall>(0x223480)(
                engineCellX, engineCellY, value.CursorPointer, value.MaterialTableHolder, 0));
            if (value.Pointer != 0)
            {
                value.PhysicalAreaPointer = Marshal.ReadIntPtr(value.Pointer + 0x168);
                value.MaterialPointer = Marshal.ReadIntPtr(value.Pointer + 0x120);
                value.IntoMaterialPointer = Marshal.ReadIntPtr(value.Pointer + 0x128);
                value.OutOfMaterialPointer = Marshal.ReadIntPtr(value.Pointer + 0x130);
            }
            Require(Marshal.ReadIntPtr(value.CursorPointer) == value.RecordPointer + authored.Length,
                "The native water factory did not consume exactly one complete serialized row.");
            if (value.Pointer == 0)
            {
                Retire(value);
                return null;
            }
            value.NativeVolumeType = Marshal.ReadInt32(value.Pointer + 0x5c);
            value.NativeVolumeId = unchecked((ulong)Marshal.ReadInt64(value.Pointer + 0x60));
            // The native destructor erases the node's configured ID. A failed geometry setup
            // which leaves the constructor ID cannot be treated as a safely owned publication.
            Require(value.NativeVolumeType == authored[0x27] && value.NativeVolumeId == value.AuthoredVolumeId,
                "Native water geometry setup did not establish the authored type and retirement ID; retain the engine.");
            Require(value.MaterialPointer == (selected?.Pointer ?? 0),
                "Native water did not retain the selected material reference.");
            ValidateWaterVolume(value);
            value.CreatedPhysicalArea = value.PhysicalAreaPointer != 0;
            value.NativeBoundsMin = *(Vector3*)(value.Pointer + 0x1b0);
            value.NativeBoundsMax = *(Vector3*)(value.Pointer + 0x1bc);
            value.NativePlane = *(Vector4*)(value.Pointer + 0xac);
            var contour = Marshal.ReadIntPtr(value.Pointer + 0x160);
            if (contour != 0)
            {
                value.NativePhysicsVertexCount = ReadWaterVectorCount(contour, 12);
                value.NativePhysicsTriangleCount = ReadWaterVectorCount(contour + 0x40, 4) / 3;
            }
            return value;
        }
        catch (Exception error)
        {
            if (_poisoned) throw; // Unknown native completion pins the node, inputs and engine.
            try { Retire(value); }
            catch (Exception cleanup) { throw new AggregateException(error, cleanup); }
            throw;
        }
    }

    /// <summary>
    /// Applies30225050's real serialized-cell metadata after native water registration.
    /// The caller owns the returned node in its cell collection before invoking this method.
    /// </summary>
    public void CompleteCellWaterVolume(WaterVolume value)
    {
        using var operation = Enter();
        ArgumentNullException.ThrowIfNull(value);
        if (!ReferenceEquals(value.Owner, this) || value.Retired)
            throw new InvalidOperationException("Cell publication requires a live water volume from this service.");
        if (value.IsCellPublicationComplete)
            throw new InvalidOperationException("This water volume already completed cell publication metadata.");
        RequireCellPublicationAvailable();
        ValidateCellPublicationService();
        ValidateWaterVolume(value);
        var state = Native(() => _cellPublicationByte!(_cellPublicationService));
        ValidateCellPublicationService();
        ValidateWaterVolume(value);
        Native(() =>
        {
            Marshal.WriteByte(value.Pointer + 0x39, (byte)(Marshal.ReadByte(value.Pointer + 0x39) | 1));
            Marshal.WriteByte(value.Pointer + 0x38, state);
        });
        value.NativeCellPublicationByte = state;
        value.IsCellPublicationComplete = true;
    }

    private void RequireIdleWaterWorld()
    {
        RequireIdleBrushWorld();
        var world = _lease.PhysicsWorld;
        if (Marshal.ReadInt32(world + 0x6d0) != 0)
            throw new InvalidOperationException("Native water ownership requires the idle loading phase.");
        foreach (var offset in new[] { 0x57c8, 0x57dc, 0x57e0, 0x57e8, 0x57ec, 0x57f0 })
            if (Marshal.ReadInt32(world + offset) != 0)
                throw new InvalidOperationException($"Native water-area guard +0x{offset:X} is busy; retain its resources.");
        var table = Marshal.ReadIntPtr(world);
        Require(Marshal.ReadIntPtr(table + 0x298) == _lease.PhysicsModule + 0x47c80 &&
            Marshal.ReadIntPtr(table + 0x2a0) == _lease.PhysicsModule + 0x41be0 &&
            Marshal.ReadIntPtr(table + 0xc0) == _lease.PhysicsModule + 0x83830,
            "The native water-area creation or retirement implementation changed.");
    }

    private void ValidateWaterVolume(WaterVolume value)
    {
        Require(Marshal.ReadIntPtr(_lease.Environment + 0x18) == value.RendererPointer &&
            Marshal.ReadIntPtr(_lease.TerrainModule + 0x2fa888) == value.RendererPointer &&
            Marshal.ReadIntPtr(_lease.TerrainModule + 0x2fa8a0) == _lease.PhysicsWorld &&
            Marshal.ReadIntPtr(_lease.TerrainModule + 0x33d6a8) == value.MapHead,
            "The native water renderer, world or volume-ID map changed generation.");
        Require(Marshal.ReadIntPtr(value.RendererPointer) == value.RendererVtable,
            "The native water renderer implementation changed.");
        RequireTable(value.Pointer, 0x289858);
        Require(Marshal.ReadInt32(value.Pointer + 0x44) == 0 &&
            Marshal.ReadInt32(value.Pointer + 0x5c) == value.NativeVolumeType &&
            unchecked((ulong)Marshal.ReadInt64(value.Pointer + 0x60)) == value.NativeVolumeId &&
            Marshal.ReadIntPtr(value.Pointer + 0x120) == value.MaterialPointer &&
            Marshal.ReadIntPtr(value.Pointer + 0x128) == value.IntoMaterialPointer &&
            Marshal.ReadIntPtr(value.Pointer + 0x130) == value.OutOfMaterialPointer &&
            Marshal.ReadIntPtr(value.Pointer + 0x168) == value.PhysicalAreaPointer,
            "The owned water node's identity, material or physical area changed.");
        if (value.MaterialPointer != 0) RequireTable(value.MaterialPointer, 0x297378);
        foreach (var stream in new[] { value.IntoMaterialPointer, value.OutOfMaterialPointer })
            if (stream != 0)
            {
                RequireTable(stream, 0x297320);
                Require(Marshal.ReadInt32(stream + 8) > 0 && Marshal.ReadIntPtr(stream + 0x30) == 0,
                    "The water fog material acquired an unowned external listener.");
            }
        if (value.PhysicalAreaPointer != 0)
        {
            var area = value.PhysicalAreaPointer;
            // CPhysArea is a separate class-five owner. Its world is at+70, not a static
            // body's+78; its removal marker/list replace the full-static class-seven contract.
            Require(Marshal.ReadIntPtr(area) == _lease.PhysicsModule + 0x1ef598 &&
                Marshal.ReadIntPtr(area + 0x70) == _lease.PhysicsWorld &&
                Marshal.ReadByte(area + 0x47) == 5 && Marshal.ReadInt32(area + 0x50) == 0 &&
                Marshal.ReadIntPtr(area + 0x20) == value.Pointer && Marshal.ReadInt16(area + 0x28) == 8,
                "The water node no longer owns its exact native physical area.");
        }
    }

    private int ReadWaterVectorCount(nint vector, int stride)
    {
        var begin = Marshal.ReadIntPtr(vector);
        var end = Marshal.ReadIntPtr(vector + 8);
        var capacity = Marshal.ReadIntPtr(vector + 16);
        Require(begin <= end && end <= capacity && (end - begin) % stride == 0 &&
            (end - begin) / stride <= int.MaxValue,
            "Native water geometry returned an invalid vector.");
        return (int)((end - begin) / stride);
    }

    private void DisposeWaterVolume(WaterVolume value)
    {
        if (value.Retired) return;
        using var operation = Enter();
        Retire(value);
    }

    private void Retire(WaterVolume value)
    {
        if (value.Retired) return;
        if (value.Pointer != 0)
        {
            RequireIdleWaterWorld();
            ValidateWaterVolume(value);
            if (value.PhysicalAreaPointer != 0)
            {
                var area = value.PhysicalAreaPointer;
                if (unchecked((uint)Marshal.ReadInt32(area + 0x40)) > 0x1ffff)
                    throw new InvalidOperationException("The water area has pending native requests; retain its node and engine.");
                // Native300F59A0 -> World+C0 -> class-five RemoveArea35041BE0 removes active
                // and grid links, queues the area-change event, marks+50=2 and retains storage
                // on world+51D0. It does not clear the area's foreign pointer or set class seven.
                Native(() => Bind<DephysicalizeCall>(value.Pointer, 0x208, 0xf59a0)(value.Pointer, 0));
                Require(Marshal.ReadIntPtr(value.Pointer + 0x168) == 0 &&
                    Marshal.ReadInt32(area + 0x50) == 2 &&
                    Marshal.ReadIntPtr(_lease.PhysicsWorld + 0x51d0) == area,
                    "Native water removal did not detach its area into the delayed-retirement owner.");
                try
                {
                    using var entities = new NativeGuard(_lease.PhysicsWorld + 0x57dc);
                    using var world = new NativeGuard(_lease.PhysicsWorld + 0x57c4);
                    using var queries = new NativeGuard(_lease.PhysicsWorld + 0x5804);
                    //35079B30 drops queued events for any entity with a nonzero+50 marker,
                    // including this area's deletion event, before its foreign node is freed.
                    Native(() => _purgeRetiredEvents(_lease.PhysicsWorld));
                }
                catch (Exception error) { _poisoned = true; _lease.Poison(error); throw; }
                value.PhysicalAreaPointer = 0;
            }
            //300FA930 erases the configured volume ID in native order, even for a river
            // whose ID was already shared.300F7800 then releases render/contour resources and
            // performs engine+5C8 cleanup. The destructor releases both fog StreamMaterials
            // (including native request cancellation) and its retained selected material.
            Native(() => Bind<DeleteBrushCall>(value.Pointer, 0, 0xfbd00)(value.Pointer, 1));
            value.Pointer = value.MaterialPointer = value.IntoMaterialPointer = value.OutOfMaterialPointer = 0;
        }
        Marshal.FreeHGlobal(value.RecordPointer);
        Marshal.FreeHGlobal(value.CursorPointer);
        Marshal.FreeHGlobal(value.MaterialTablePointer);
        Marshal.FreeHGlobal(value.MaterialTableHolder);
        value.RecordPointer = value.CursorPointer = value.MaterialTablePointer = value.MaterialTableHolder = 0;
        value.Retired = true;
        _waterVolumes.Remove(value);
    }

    // The central service Dispose must call this before table-reference retirement.
    private void RetireWaterResources()
    {
        foreach (var value in _waterVolumes.ToArray()) Retire(value);
    }

    /// <summary>
    /// A real native water node with its own retained materials and optional class-five area.
    /// Disposal removes active area participation and purges retired events before deleting the
    /// node. PhysicalWorld retains delayed area storage until its native retirement/shutdown.
    ///350415F0 destroys that storage without following the old foreign node.
    /// </summary>
    public sealed class WaterVolume : IDisposable
    {
        private readonly byte[] _authoredPayload;
        internal ZoneNativeStaticObjectServices Owner { get; }
        internal nint Pointer, PhysicalAreaPointer, MaterialPointer, IntoMaterialPointer, OutOfMaterialPointer;
        internal nint RecordPointer, CursorPointer, MaterialTablePointer, MaterialTableHolder;
        internal nint RendererPointer { get; }
        internal nint RendererVtable { get; }
        internal nint MapHead { get; }
        internal bool Retired;
        public int EngineCellX { get; }
        public int EngineCellY { get; }
        public int MaterialTableIndex { get; }
        public string? MaterialPath { get; }
        public bool MaterialWasFallback { get; }
        public ulong AuthoredVolumeId { get; }
        public int NativeVolumeType { get; internal set; }
        public ulong NativeVolumeId { get; internal set; }
        public Vector3 NativeBoundsMin { get; internal set; }
        public Vector3 NativeBoundsMax { get; internal set; }
        public Vector4 NativePlane { get; internal set; }
        public int NativePhysicsVertexCount { get; internal set; }
        public int NativePhysicsTriangleCount { get; internal set; }
        public bool CreatedPhysicalArea { get; internal set; }
        public bool HasPhysicalArea => !Retired && PhysicalAreaPointer != 0;
        public ReadOnlySpan<byte> AuthoredPayload => _authoredPayload;
        public bool IsCellPublicationComplete { get; internal set; }
        public byte? NativeCellPublicationByte { get; internal set; }
        public bool IsDisposed => Retired;

        internal WaterVolume(ZoneNativeStaticObjectServices owner, int x, int y, byte[] authored,
            int materialIndex, Material? material, nint renderer, nint rendererVtable, nint mapHead)
        {
            Owner = owner; EngineCellX = x; EngineCellY = y; _authoredPayload = authored;
            MaterialTableIndex = materialIndex; MaterialPath = material?.Path;
            MaterialWasFallback = material?.IsFallback ?? false;
            AuthoredVolumeId = BinaryPrimitives.ReadUInt64LittleEndian(authored.AsSpan(0x2b));
            RendererPointer = renderer; RendererVtable = rendererVtable; MapHead = mapHead;
        }
        public void Dispose() => Owner.DisposeWaterVolume(this);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint CreateWaterVolumeCall(int cellX, int cellY, nint cursor, nint materialTable, byte skip);
}

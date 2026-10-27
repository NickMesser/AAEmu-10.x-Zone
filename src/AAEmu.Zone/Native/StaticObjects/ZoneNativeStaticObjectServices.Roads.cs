using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;

using AAEmu.Zone.Content;

namespace AAEmu.Zone.Native.StaticObjects;

public sealed unsafe partial class ZoneNativeStaticObjectServices
{
    private readonly List<Road> _roads = [];

    /// <summary>
    /// Owns the actual serialized road creation phase: native30222AA0 copies translated vertices,
    /// retains the selected material, registers a CRoadRenderNode and queues it for compilation.
    /// This method requires the current loading state with no native CTerrain. Native frame/drain
    /// callers30143AA0/30144340 also leave the road queue untouched in that state.
    /// The 63-byte header and every counted Vec3 remain intact in AuthoredPayload.
    /// This is not road mesh compilation or cell readiness. Retire all these owners before
    /// installing a native CTerrain or enabling another road task producer.
    /// </summary>
    public Road? CreateQueuedRoad(int engineCellX, int engineCellY, ReadOnlySpan<byte> rawPayload,
        IReadOnlyList<Material?> materials)
    {
        using var operation = Enter();
        ArgumentNullException.ThrowIfNull(materials);
        ValidateTerrainCell(engineCellX, engineCellY);
        _lease.RequireTerrainEntityGrid();
        RequireAbsentRoadTerrain();
        RequireIdleBrushWorld();
        ValidateRoadQueue(true);
        if (rawPayload.Length < 63 || rawPayload.Length > ZoneCellObjectContent.MaximumFileBytes)
            throw new ArgumentException("A serialized road requires its complete bounded 63-byte header and vertices.", nameof(rawPayload));
        var authored = rawPayload.ToArray();
        var vertexCount = BinaryPrimitives.ReadInt32LittleEndian(authored.AsSpan(0x27));
        if (vertexCount < 0 || 63L + 12L * vertexCount != authored.Length)
            throw new ArgumentException("The road payload must contain exactly its signed vertex count.", nameof(rawPayload));
        var materialIndex = (int)(BinaryPrimitives.ReadUInt32LittleEndian(authored.AsSpan(0x3b)) & 0xffffff);
        // Snapshot caller code only inside the existing shared engine busy gate.
        var count = materials.Count;
        if (count < 0 || count > ZoneCellObjectContent.MaximumFileBytes / 260 || materialIndex >= count)
            throw new ArgumentException("The road's material index must address a bounded, complete resource table.", nameof(materials));
        var pointers = new nint[count];
        Material? selected = null;
        for (var i = 0; i < count; i++)
        {
            var material = materials[i];
            if (material is not null)
            {
                if (!ReferenceEquals(material.Owner, this) || material.Retired)
                    throw new InvalidOperationException("Every road material must be a live reference from this service.");
                RequireTable(material.Pointer, 0x297378);
                pointers[i] = material.Pointer;
            }
            if (i == materialIndex) selected = material;
        }
        if (materials.Count != count)
            throw new InvalidOperationException("The road material table changed while it was being captured.");
        RequireCellPublicationAvailable();
        ValidateCellPublicationService();

        var value = new Road(this, engineCellX, engineCellY, authored, vertexCount, materialIndex, selected);
        _roads.Add(value); // Root partial ownership before the native factory or callbacks.
        try
        {
            value.RecordPointer = Allocate(authored);
            value.CursorPointer = Marshal.AllocHGlobal(8);
            Marshal.WriteIntPtr(value.CursorPointer, value.RecordPointer);
            value.MaterialTablePointer = Marshal.AllocHGlobal(checked(count * 8));
            pointers.AsSpan().CopyTo(new Span<nint>((void*)value.MaterialTablePointer, count));
            value.MaterialTableHolder = Marshal.AllocHGlobal(8);
            Marshal.WriteIntPtr(value.MaterialTableHolder, value.MaterialTablePointer);
            value.Pointer = Native(() => Terrain<CreateRoadCall>(0x222aa0)(
                engineCellX, engineCellY, value.CursorPointer, value.MaterialTableHolder, 0));
            Require(Marshal.ReadIntPtr(value.CursorPointer) == value.RecordPointer + authored.Length,
                "The native road factory did not consume exactly the complete serialized row.");
            RequireAbsentRoadTerrain();
            ValidateRoadQueue();
            if (value.Pointer == 0)
            {
                Retire(value);
                return null; // Actual native skip/quality/e_roads gate.
            }
            value.VertexPointer = Marshal.ReadIntPtr(value.Pointer + 0x68);
            ValidateRoad(value);
            value.NativeVertices = Array.AsReadOnly(new ReadOnlySpan<Vector3>(
                (void*)value.VertexPointer, value.VertexCount).ToArray());
            value.WasQueuedForCompilation = true;
            return value;
        }
        catch (Exception error)
        {
            if (_poisoned) throw;
            try { Retire(value); }
            catch (Exception cleanup) { throw new AggregateException(error, cleanup); }
            throw;
        }
    }

    /// <summary>
    /// Completes30225050's actual cell byte/flag metadata, after native queue publication and
    /// registration. The caller retains this wrapper in its cell collection in source order.
    /// </summary>
    public void CompleteCellRoad(Road value)
    {
        using var operation = Enter();
        ArgumentNullException.ThrowIfNull(value);
        if (!ReferenceEquals(value.Owner, this) || value.Retired)
            throw new InvalidOperationException("Cell publication requires a live road from this service.");
        if (value.IsCellPublicationComplete)
            throw new InvalidOperationException("This road already completed cell publication metadata.");
        RequireAbsentRoadTerrain();
        RequireIdleBrushWorld();
        ValidateRoad(value);
        ValidateRoadQueue();
        RequireCellPublicationAvailable();
        ValidateCellPublicationService();
        var state = Native(() => _cellPublicationByte!(_cellPublicationService));
        RequireAbsentRoadTerrain();
        ValidateRoad(value);
        ValidateRoadQueue();
        ValidateCellPublicationService();
        Native(() =>
        {
            Marshal.WriteByte(value.Pointer + 0x39, (byte)(Marshal.ReadByte(value.Pointer + 0x39) | 1));
            Marshal.WriteByte(value.Pointer + 0x38, state);
        });
        value.NativeCellPublicationByte = state;
        value.IsCellPublicationComplete = true;
    }

    private void RequireAbsentRoadTerrain()
    {
        if (Marshal.ReadIntPtr(_lease.TerrainModule + 0x2fa8a8) == 0) return;
        if (_roads.Any(value => value.Pointer != 0))
            Require(false, "Native CTerrain appeared while this service owns uncompiled roads; their task/mesh lifetime is no longer admitted.");
        throw new NotSupportedException("Queued road ownership currently requires the captured loading state with no native CTerrain; compiled roads need a matching terrain and task owner.");
    }

    private void ValidateRoad(Road value)
    {
        RequireTable(value.Pointer, 0x29b6b8);
        Require(Marshal.ReadInt32(value.Pointer + 0x44) == 0 &&
            Marshal.ReadIntPtr(value.Pointer + 0x60) == value.MaterialPointer,
            "The serialized road's IEntity or material association changed outside this owner.");
        Require(Marshal.ReadIntPtr(value.Pointer + 0x58) == 0 &&
            Marshal.ReadIntPtr(value.Pointer + 0xa0) == 0 && Marshal.ReadIntPtr(value.Pointer + 0xa8) == 0,
            "The queued road acquired a mesh, physical entity or physical geometry outside its admitted loading phase.");
        var begin = Marshal.ReadIntPtr(value.Pointer + 0x68);
        var end = Marshal.ReadIntPtr(value.Pointer + 0x70);
        var capacity = Marshal.ReadIntPtr(value.Pointer + 0x78);
        Require(begin == value.VertexPointer && end >= begin && capacity >= end &&
            (end - begin) == (nint)((long)value.VertexCount * 12) && (capacity - begin) % 12 == 0 &&
            (value.VertexCount == 0 || begin != 0),
            "The queued road no longer owns its exact translated vertex vector.");
    }

    private void ValidateRoadQueue(bool initialAdmission = false)
    {
        var expected = _roads.Where(value => value.Pointer != 0).Select(value => value.Pointer).ToArray();
        var begin = Marshal.ReadIntPtr(_lease.ThreeDEngine + 0x5db0);
        var count = Marshal.ReadInt32(_lease.ThreeDEngine + 0x5db8);
        var capacity = Marshal.ReadInt32(_lease.ThreeDEngine + 0x5dbc);
        Require(count >= 0 && capacity >= count && capacity <= int.MaxValue / 8 &&
            (count == 0 || begin != 0), "The native road compilation queue is invalid.");
        if (initialAdmission && expected.Length == 0 && count != 0)
            throw new InvalidOperationException("The native road queue already belongs to another producer.");
        Require(count == expected.Length, "The native road queue changed outside its owner.");
        for (var i = 0; i < expected.Length; i++)
            Require(Marshal.ReadIntPtr(begin + i * 8) == expected[i],
                "The native road queue lost its exact owned identity/order.");
    }

    private void DisposeRoad(Road value)
    {
        if (value.Retired) return;
        using var operation = Enter();
        Retire(value);
    }

    private void Retire(Road value)
    {
        if (value.Retired) return;
        if (value.Pointer != 0)
        {
            RequireAbsentRoadTerrain();
            RequireIdleBrushWorld();
            ValidateRoad(value);
            ValidateRoadQueue();
            // 3023C150 ->30239280 ->302381F0 performs actual renderer+648 release (null in this
            // phase), engine+5C8 cleanup/unregistration and removal of every matching road from
            // engine+5DB0. It then frees the vertex vector and releases its material reference.
            //30238020's physical-geometry/entity branch is excluded by ValidateRoad above.
            Native(() => Bind<DeleteBrushCall>(value.Pointer, 0, 0x23c150)(value.Pointer, 1));
            value.Pointer = value.VertexPointer = 0;
            ValidateRoadQueue(); // Checks values only, never dereferences the deleted road.
        }
        Marshal.FreeHGlobal(value.RecordPointer);
        Marshal.FreeHGlobal(value.CursorPointer);
        Marshal.FreeHGlobal(value.MaterialTablePointer);
        Marshal.FreeHGlobal(value.MaterialTableHolder);
        value.RecordPointer = value.CursorPointer = value.MaterialTablePointer = value.MaterialTableHolder = 0;
        value.Retired = true;
        _roads.Remove(value);
    }

    // Central service Dispose must invoke this before material/model references and lease release.
    private void RetireRoadResources()
    {
        foreach (var value in _roads.ToArray()) Retire(value);
    }

    /// <summary>
    /// A real registered, queued road in the captured absent-CTerrain loading phase.
    /// WasQueuedForCompilation records creation, not a completed mesh. Compiler302398F0 directly
    /// samples native CTerrain heights/holes and uses real renderer resources; it is not invoked
    /// by this owner. Dispose before installing CTerrain or permitting an external task producer.
    /// </summary>
    public sealed class Road : IDisposable
    {
        private readonly byte[] _authoredPayload;
        internal ZoneNativeStaticObjectServices Owner { get; }
        internal nint Pointer, VertexPointer, RecordPointer, CursorPointer, MaterialTablePointer, MaterialTableHolder;
        internal readonly nint MaterialPointer;
        internal bool Retired;
        public int EngineCellX { get; }
        public int EngineCellY { get; }
        public int VertexCount { get; }
        public int MaterialTableIndex { get; }
        public string? MaterialPath { get; }
        public bool MaterialWasFallback { get; }
        public ReadOnlySpan<byte> AuthoredPayload => _authoredPayload;
        public IReadOnlyList<Vector3> NativeVertices { get; internal set; } = Array.Empty<Vector3>();
        public bool WasQueuedForCompilation { get; internal set; }
        public bool IsCellPublicationComplete { get; internal set; }
        public byte? NativeCellPublicationByte { get; internal set; }
        public bool IsDisposed => Retired;
        internal Road(ZoneNativeStaticObjectServices owner, int x, int y, byte[] authored,
            int count, int materialIndex, Material? material)
        {
            Owner = owner; EngineCellX = x; EngineCellY = y; _authoredPayload = authored;
            VertexCount = count; MaterialTableIndex = materialIndex; MaterialPointer = material?.Pointer ?? 0;
            MaterialPath = material?.Path; MaterialWasFallback = material?.IsFallback ?? false;
        }
        public void Dispose() => Owner.DisposeRoad(this);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint CreateRoadCall(int cellX, int cellY, nint cursor, nint materialTable, byte skip);
}

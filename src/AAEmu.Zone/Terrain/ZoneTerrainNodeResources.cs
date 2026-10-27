using System.Numerics;

using AAEmu.Zone.Content;
using AAEmu.Zone.Scene;

namespace AAEmu.Zone.Terrain;

public sealed record ZoneTerrainNodeSurfaceResourcesSnapshot(int Count, int Capacity,
    IReadOnlyList<byte> SurfaceSlots);

/// <summary>
/// The imported surface-record vectors from 300E9600/300E94E0 and their retirement through
/// 300E8110/300E8A90. Each native 0x1A8 record starts entirely zero (300E9360); import assigns
/// only its surface reference. This owner admits no render-mesh or procedural-object producer.
/// It owns active and retired managed vectors, preserving counts, capacity and current surface
/// bindings. Texture/sample/procedural retirement and the complete 300E8C20 ordering belong
/// to the enclosing terrain owner. This does not provide native render resources or scheduling.
/// </summary>
public sealed class ZoneTerrainNodeResources : IDisposable
{
    private readonly ZoneTerrainNodeTree _tree;
    private readonly Dictionary<ZoneTerrainNode, SurfaceVector> _active = [];
    private readonly List<SurfaceVector> _retired = [];
    private int _retiredRecordCount;
    private bool _disposed;

    internal ZoneTerrainNodeResources(ZoneTerrainNodeTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        _tree = tree;
    }

    public int RetiredVectorCount { get { lock (_tree.Gate) return _retired.Count; } }
    public int RetiredRecordCount { get { lock (_tree.Gate) return _retiredRecordCount; } }

    /// <summary>
    /// Replaces each imported node's active records before sample publication. Native clear
    /// preserves allocation capacity; growth is max(required, oldCapacity + oldCapacity/2).
    /// Admission and managed allocation finish before publication. Later header-24 surface
    /// overrides remain visible because records identify the cell slot, not a copied binding.
    /// </summary>
    internal void LoadCell(ZoneWorldMapCellPlacement placement, ZoneTerrainHeightmapContent content)
    {
        ArgumentNullException.ThrowIfNull(placement);
        ArgumentNullException.ThrowIfNull(content);
        var cell = _tree.GetCellRoot(placement.EngineCell.X, placement.EngineCell.Y);
        lock (_tree.Gate)
        {
            ThrowIfDisposed();
            var replacements = new List<(ZoneTerrainNode Node, SurfaceVector Vector)>(content.Nodes.Count);
            foreach (var authored in content.Nodes)
            {
                var level = BitOperations.Log2((uint)(authored.SizeMeters / 64));
                var node = _tree.GetNode(level, (cell.X + authored.LocalX) / authored.SizeMeters,
                    (cell.Y + authored.LocalY) / authored.SizeMeters);
                var count = authored.SurfaceTypes.Length;
                foreach (var slot in authored.SurfaceTypes)
                    if (slot >= 32) throw new InvalidDataException("An imported terrain surface refers outside its cell's 32 slots.");
                _active.TryGetValue(node, out var previous);
                previous?.RequireNoMeshes();
                var capacity = previous?.Records.Length ?? 0;
                if (capacity < count) capacity = checked((int)Math.Max(count, (long)capacity + capacity / 2));
                var records = new SurfaceRecord[capacity];
                previous?.Records.CopyTo(records, 0);
                // 300E2230 releases/zeros the old three mesh fields; every admitted field is
                // null. 300E9360 then zero-constructs the new live records before slot binding.
                for (var i = 0; i < count; i++) records[i] = new(authored.SurfaceTypes[i]);
                replacements.Add((node, new(records, count)));
            }
            _active.EnsureCapacity(checked(_active.Count + replacements.Count));
            foreach (var replacement in replacements) _active[replacement.Node] = replacement.Vector;
        }
    }

    public ZoneTerrainNodeSurfaceResourcesSnapshot GetSnapshot(ZoneTerrainNode node)
    {
        lock (_tree.Gate)
        {
            ValidateNode(node);
            if (!_active.TryGetValue(node, out var vector)) return new(0, 0, Array.Empty<byte>());
            var slots = new byte[vector.Count];
            for (var i = 0; i < slots.Length; i++) slots[i] = vector.Records[i].Slot;
            return new(vector.Count, vector.Records.Length, Array.AsReadOnly(slots));
        }
    }

    public ZoneTerrainSurfaceBinding GetSurfaceBinding(ZoneTerrainNode node, int index)
    {
        lock (_tree.Gate)
        {
            ValidateNode(node);
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            if (!_active.TryGetValue(node, out var vector) || index >= vector.Count)
                throw new ArgumentOutOfRangeException(nameof(index));
            return _tree.RequireSurfaces().GetBinding(node.X / 1024, node.Y / 1024, vector.Records[index].Slot);
        }
    }

    /// <summary>
    /// The surface-record part of 300EDCD0. 300E2230 invokes renderer+648 for all three mesh
    /// fields, even null. Configured CNULLRenderer+648/380415F0 returns for null; a nonnull
    /// mesh instead calls its virtual+10 Release. That ownership requires a real producer and
    /// cannot be substituted with a blanket dedicated no-op, so nonnull fields are rejected.
    /// </summary>
    internal void ReleaseSurfaceMeshes(ZoneTerrainNode node)
    {
        lock (_tree.Gate)
        {
            ValidateNode(node);
            if (_active.TryGetValue(node, out var vector)) vector.RequireNoMeshes();
        }
    }

    /// <summary>
    /// 300E8A90 moves the entire allocation into the terrain's retired-vector collection and
    /// leaves an empty zero-capacity node vector. Even an empty vector is appended. This is
    /// node-local: the owner chooses the native quadrant order and retirement interleaving.
    /// </summary>
    internal void RetireNode(ZoneTerrainNode node)
    {
        lock (_tree.Gate)
        {
            ValidateNode(node);
            var vector = _active.GetValueOrDefault(node) ?? new SurfaceVector([], 0);
            vector.RequireNoMeshes();
            _retired.Add(vector); // Retain ownership before removing the active reference.
            _active.Remove(node);
            _retiredRecordCount = unchecked(_retiredRecordCount + vector.Count);
        }
    }

    // Current cell-engine admission has only imported, zero-mesh records. Retire this vector
    // leaf in quadrant order after external node resources and before clearing sample payloads.
    // A future nonzero mesh/procedural producer must first join full per-node retirement.
    internal void RetireCell(ZoneWorldMapCellPlacement placement)
    {
        var root = _tree.GetCellRoot(placement.EngineCell.X, placement.EngineCell.Y);
        lock (_tree.Gate)
        {
            ThrowIfDisposed();
            Retire(root);
        }
        void Retire(ZoneTerrainNode node)
        {
            ReleaseSurfaceMeshes(node);
            RetireNode(node);
            foreach (var child in node.Children) Retire(child);
        }
    }

    public void Dispose()
    {
        lock (_tree.Gate)
        {
            if (_disposed) return;
            foreach (var vector in _active.Values) vector.RequireNoMeshes();
            foreach (var vector in _retired) vector.RequireNoMeshes();
            // These vectors contain managed data and slot references only. Native cache drain
            // or reuse during a running terrain frame is not exposed without its producer proof.
            _active.Clear();
            _retired.Clear();
            _retiredRecordCount = 0;
            _disposed = true;
        }
    }

    private void ValidateNode(ZoneTerrainNode node)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(node);
        if (!ReferenceEquals(node.Owner, _tree)) throw new ArgumentException("The terrain node belongs to another tree.", nameof(node));
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private readonly struct SurfaceRecord(byte slot)
    {
        internal readonly byte Slot = slot;
        // Native +8/+10/+18. No producer admitted by this owner can make these nonnull.
        internal readonly nint MeshX = 0, MeshY = 0, MeshZ = 0;
    }

    private sealed class SurfaceVector(SurfaceRecord[] records, int count)
    {
        internal SurfaceRecord[] Records { get; } = records;
        internal int Count { get; } = count;

        internal void RequireNoMeshes()
        {
            for (var i = 0; i < Count; i++)
                if (Records[i].MeshX != 0 || Records[i].MeshY != 0 || Records[i].MeshZ != 0)
                    throw new NotSupportedException("Native terrain meshes require an owned render-resource producer and release service.");
        }
    }
}

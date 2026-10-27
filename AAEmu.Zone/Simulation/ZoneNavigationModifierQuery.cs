using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace AAEmu.Zone.Simulation;

/// <summary>
/// The graph-dependent 311B4514 surface-node query. Zero means no suitable node.
/// Implementations must query the surface graph belonging to this modifier owner's
/// current generation, and must not return a node from a retired or different graph.
/// </summary>
public interface IZoneNavigationModifierSurfaceNodes
{
    uint FindBestSurfaceNodeInModifier(Vector3 position, ZoneNavigationRuntimeModifier modifier);
}

public readonly record struct ZoneNavigationTypeSelection(int NavigationType, int RegionId);

/// <summary>
/// 31133D7C exact-type selection and 310515F8 capability classification over the
/// current dedicated modifier owner. This does not supply the surface graph query.
/// Calls to this query, its owner and the supplied graph service must be serialized
/// on the navigation dispatcher; these owners are not thread-safe.
/// </summary>
public sealed class ZoneNavigationModifierQuery
{
    private readonly ZoneNavigationModifierRuntime _owner;
    private readonly IZoneNavigationModifierSurfaceNodes? _surfaceNodes;
    private readonly ConditionalWeakTable<ZoneNavigationRuntimeModifier, Geometry> _geometry = new();
    private bool _classifying;

    public ZoneNavigationModifierQuery(ZoneNavigationModifierRuntime owner,
        IZoneNavigationModifierSurfaceNodes? surfaceNodes = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _owner = owner;
        _surfaceNodes = surfaceNodes;
    }

    /// <summary>Matches the graph loader's Func&lt;Vector3,int,int?&gt; callback: type4 requests0; type8 requests4.</summary>
    public int? ResolveModifierId(Vector3 position, int nativeType)
    {
        foreach (var modifier in _owner.Modifiers)
            if (modifier.NativeType == unchecked((uint)nativeType) && Contains(modifier, position))
                return modifier.RuntimeId;
        return null;
    }

    public bool Contains(ZoneNavigationRuntimeModifier modifier, Vector3 position)
    {
        ArgumentNullException.ThrowIfNull(modifier);
        return _owner.IsCurrent(modifier) && _geometry.GetValue(modifier, static value => new(value)).Contains(position);
    }

    public int GetNavigationType(Vector3 position, uint capabilityMask) => Classify(position, capabilityMask).NavigationType;

    /// <summary>
    /// Native passes select types0,4,1,2,5 in that priority, using capability bits
    /// 4,8,0x20,0x10,0x100. Within a pass the first matching byte-name wins.
    /// </summary>
    public ZoneNavigationTypeSelection Classify(Vector3 position, uint capabilityMask)
    {
        if (_classifying) throw new InvalidOperationException("A navigation modifier classification is already active.");
        _classifying = true;
        try
        {
            // All published dedicated records already hold pool IDs. Native's initial
            // lazy assignment loop only changes records with negative IDs, which this
            // owner does not admit from authored data.
            var modifiers = _owner.Modifiers.ToArray();
            foreach (var (bit, type) in Passes)
            {
                if ((capabilityMask & bit) == 0) continue;
                foreach (var modifier in modifiers)
                {
                    if (modifier.NativeType != type || !Contains(modifier, position)) continue;
                    if (bit == 8)
                    {
                        if (_surfaceNodes is null)
                            throw new NotSupportedException("Navigation type8 requires the actual surface-node query (311B4514).");
                        var nodeId = _surfaceNodes.FindBestSurfaceNodeInModifier(position, modifier);
                        if (!_owner.IsCurrent(modifier))
                            throw new InvalidOperationException("The modifier owner changed during its surface-node query.");
                        if (nodeId == 0) continue;
                    }
                    return new((int)bit, bit == 0x10 ? -1 : modifier.RuntimeId);
                }
            }
            return new((capabilityMask & 2) != 0 ? 2 : (capabilityMask & 0x100) != 0 ? 0x100 : 1, -1);
        }
        finally { _classifying = false; }
    }

    private static readonly (uint Bit, uint Type)[] Passes = [(4, 0), (8, 4), (0x20, 1), (0x10, 2), (0x100, 5)];

    private sealed class Geometry
    {
        private static readonly float HeightThreshold = BitConverter.Int32BitsToSingle(0x3727c5ac);
        private readonly IReadOnlyList<Vector3> _points;
        private readonly Vector3 _minimum;
        private readonly Vector3 _maximum;
        private readonly float _heightCheck;
        private readonly float _minimumHeight;
        private readonly float _maximumHeight;

        internal Geometry(ZoneNavigationRuntimeModifier modifier)
        {
            _points = modifier.Source.Points;
            var parameters = modifier.Source.ParameterBytes;
            _heightCheck = ReadSingle(parameters[17..]);
            _minimumHeight = ReadSingle(parameters[29..]);
            _maximumHeight = ReadSingle(parameters[25..]);
            var minimum = new Vector3(9_999_999f);
            var maximum = new Vector3(-9_999_999f);
            foreach (var point in _points)
            {
                // 310430D0 retains the incoming value on equality/unordered compares.
                if (!(point.X > minimum.X)) minimum.X = point.X;
                if (!(point.Y > minimum.Y)) minimum.Y = point.Y;
                if (!(point.Z > minimum.Z)) minimum.Z = point.Z;
                if (!(maximum.X > point.X)) maximum.X = point.X;
                if (!(maximum.Y > point.Y)) maximum.Y = point.Y;
                if (!(maximum.Z > point.Z)) maximum.Z = point.Z;
            }
            _minimum = minimum;
            _maximum = maximum;
        }

        internal bool Contains(Vector3 position)
        {
            // 31133D2C: inclusive authored Z limits only above the exact threshold.
            // Preserve the native ordered comparisons: NaN does not force rejection.
            if (_heightCheck > HeightThreshold &&
                (position.Z < _minimumHeight || _maximumHeight < position.Z)) return false;
            // 3102D2B4 uses unpadded modifier bounds and half-open XY maxima.
            if (!(position.X >= _minimum.X && position.X < _maximum.X &&
                position.Y >= _minimum.Y && position.Y < _maximum.Y)) return false;
            var inside = false;
            for (var index = 0; index < _points.Count; index++)
            {
                var first = _points[index];
                var second = _points[index + 1 == _points.Count ? 0 : index + 1];
                if (!(second.Y <= position.Y && position.Y < first.Y ||
                      first.Y <= position.Y && position.Y < second.Y)) continue;
                var crossing = (first.X - second.X) * (position.Y - second.Y) /
                    (first.Y - second.Y) + second.X;
                if (position.X < crossing) inside = !inside;
            }
            return inside;
        }

        private static float ReadSingle(ReadOnlySpan<byte> bytes) =>
            BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes));
    }
}

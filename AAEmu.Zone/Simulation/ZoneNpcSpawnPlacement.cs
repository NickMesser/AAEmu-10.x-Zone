using System.Numerics;
using AAEmu.Zone.Content;

namespace AAEmu.Zone.Simulation;

public sealed record ZoneNpcSpawnPoint(Vector3 Position, float ZRotation);
public sealed record ZoneNpcSpawnTriangle(Vector3 V1, Vector3 V2, Vector3 V3, float AreaRate);

/// <summary>3934B7A0 authored points and triInfos, copied without reordering or inferred geometry.</summary>
public sealed class ZoneNpcSpawnPlacementData
{
    public IReadOnlyList<ZoneNpcSpawnPoint> Points { get; }
    public IReadOnlyList<ZoneNpcSpawnTriangle> Triangles { get; }

    public ZoneNpcSpawnPlacementData(IEnumerable<ZoneNpcSpawnPoint> points, IEnumerable<ZoneNpcSpawnTriangle> triangles)
    {
        ArgumentNullException.ThrowIfNull(points); ArgumentNullException.ThrowIfNull(triangles);
        Points = Array.AsReadOnly(points.Select(point => point ?? throw new ArgumentException("A spawn point cannot be null.", nameof(points))).ToArray());
        Triangles = Array.AsReadOnly(triangles.Select(triangle => triangle ?? throw new ArgumentException("A spawn triangle cannot be null.", nameof(triangles))).ToArray());
    }
}

public interface IZoneNpcSpawnRandom
{
    float NextRandomFloat();
    uint NextRandomUInt32();
}

public sealed record ZoneNpcSpawnMemberSelection(int Index, ZoneNpcSpawnerMember Member);
public sealed record ZoneNpcSpawnPointCandidate(int PartIndex, Vector3 Position, float ZRotation);
public sealed class ZoneNpcSpawnTriangleSelection
{
    internal ZoneNpcSpawnPlacement Placement { get; }
    public int Index { get; }
    public ZoneNpcSpawnTriangle Triangle { get; }
    internal ZoneNpcSpawnTriangleSelection(ZoneNpcSpawnPlacement placement, int index, ZoneNpcSpawnTriangle triangle)
    { Placement = placement; Index = index; Triangle = triangle; }
}

/// <summary>
///3934A410 weight normalization and39348AF0 member/point/triangle arithmetic over one live
/// registration. Representative/model validation belongs between member and position selection;
/// terrain, water, separation and collision admission remain separate required phases.
/// </summary>
public sealed class ZoneNpcSpawnPlacement
{
    private static readonly float MinimumPositiveNormal = BitConverter.UInt32BitsToSingle(0x00800000);
    private static readonly float DegreesToRadians = BitConverter.UInt32BitsToSingle(0x3C8EFA35);
    private readonly IZoneNpcSpawnRandom _random;
    private readonly object _generation;
    public ZoneNpcSpawnerRegistration Owner { get; }
    public ZoneNpcSpawnPlacementData Data { get; }
    public IReadOnlyList<float> NormalizedMemberWeights { get; }

    public ZoneNpcSpawnPlacement(ZoneNpcSpawnerRegistration owner, ZoneNpcSpawnPlacementData data, IZoneNpcSpawnRandom random)
    {
        Owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _generation = owner.ContentGeneration;
        Data = data ?? throw new ArgumentNullException(nameof(data));
        _random = random ?? throw new ArgumentNullException(nameof(random));
        if (owner.IsRetired || owner.Scheduler.IsDisposed) throw new ArgumentException("The spawner registration has retired.", nameof(owner));
        if (owner.Content.Members.Count == 0) throw new InvalidDataException("Member selection requires resolved spawner members.");
        if (data.Points.Count != owner.PartCount || data.Triangles.Count != owner.AuthoredTriangleCount)
            throw new InvalidDataException("Authored placement vectors must match their registration's source indices.");
        if (owner.UsesTriangles ? data.Triangles.Count == 0 : data.Points.Count == 0)
            throw new InvalidDataException("The selected placement mode requires its authored points or triangles.");
        var total = 0f;
        foreach (var member in owner.Content.Members) total = member.Weight + total;
        // Native intentionally has no zero/nonfinite weight guard.
        NormalizedMemberWeights = Array.AsReadOnly(owner.Content.Members.Select(member => member.Weight / total).ToArray());
    }

    internal bool IsLive => !Owner.IsRetired && !Owner.Reinitializing && Owner.ContentInitialized &&
        !Owner.Scheduler.IsDisposed && ReferenceEquals(_generation, Owner.ContentGeneration);
    private bool IsCurrent(Func<bool> current) => IsLive && current() && IsLive;

    public ZoneNpcSpawnMemberSelection? SelectMember(Func<bool> current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (!IsCurrent(current)) return null;
        var members = Owner.Content.Members;
        if (Owner.Persistent && Owner.OriginalTemplateId != 0)
            for (var index = 0; index < members.Count; index++)
                if (members[index].Kind == ZoneNpcSpawnerMemberKind.Npc && members[index].Npc?.Id == Owner.OriginalTemplateId)
                    return new(index, members[index]);
        var selected = 0;
        if (Owner.Scheduler.Population == 0)
        {
            var random = _random.NextRandomFloat();
            if (!IsCurrent(current)) return null;
            var cumulative = 0f;
            selected = members.Count - 1;
            for (var index = 0; index < members.Count; index++)
            {
                cumulative += NormalizedMemberWeights[index];
                if (cumulative >= random) { selected = index; break; }
            }
        }
        else
        {
            var best = MinimumPositiveNormal;
            var population = (float)Owner.Scheduler.Population;
            for (var index = 0; index < members.Count; index++)
            {
                var score = NormalizedMemberWeights[index] - (float)Owner.MemberCounts[index] / population;
                if (score > best) { selected = index; best = score; }
            }
        }
        return new(selected, members[selected]);
    }

    public ZoneNpcSpawnPointCandidate? SelectPointCandidate(Func<bool> current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (!IsCurrent(current)) return null;
        if (Owner.UsesTriangles) throw new InvalidOperationException("This registration uses triangle placement.");
        var random = _random.NextRandomUInt32();
        if (!IsCurrent(current)) return null;
        var index = (int)(((ulong)random * (uint)Data.Points.Count) >> 32);
        var point = Data.Points[index];
        return new(index, point.Position, point.ZRotation);
    }

    public ZoneNpcSpawnTriangleSelection? SelectTriangle(Func<bool> current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (!IsCurrent(current)) return null;
        if (!Owner.UsesTriangles) throw new InvalidOperationException("This registration uses point placement.");
        var selected = 0;
        var best = MinimumPositiveNormal;
        for (var index = 0; index < Data.Triangles.Count; index++)
        {
            var score = Data.Triangles[index].AreaRate;
            if (Owner.Scheduler.Population > 0)
                score -= (float)Owner.TriangleCounts[index] / (float)Owner.Scheduler.Population;
            var random = _random.NextRandomFloat();
            if (!IsCurrent(current)) return null;
            // Store the unjittered score; later near-equal rows can replace this choice.
            if (random * 0.001f + score > best) { best = score; selected = index; }
        }
        return new(this, selected, Data.Triangles[selected]);
    }

    public Vector3? SampleTrianglePosition(ZoneNpcSpawnTriangleSelection selection, Func<bool> current)
    {
        ArgumentNullException.ThrowIfNull(selection); ArgumentNullException.ThrowIfNull(current);
        if (!ReferenceEquals(selection.Placement, this)) throw new ArgumentException("The selected triangle belongs to another placement owner.", nameof(selection));
        if (!IsCurrent(current)) return null;
        var low = _random.NextRandomFloat();
        if (!IsCurrent(current)) return null;
        var high = _random.NextRandomFloat();
        if (!IsCurrent(current)) return null;
        if (high < low) (low, high) = (high, low);
        var middle = high - low;
        var tail = 1f - high;
        var triangle = selection.Triangle;
        // Native X has a different addition order from Y/Z; keep each scalar operation.
        return new(
            (triangle.V1.X * low + triangle.V2.X * middle) + triangle.V3.X * tail,
            triangle.V3.Y * tail + (triangle.V1.Y * low + triangle.V2.Y * middle),
            triangle.V3.Z * tail + (triangle.V1.Z * low + triangle.V2.Z * middle));
    }

    /// <summary>Sample only after the area candidate loop accepts or exhausts its native budget.</summary>
    public float? SampleTriangleYaw(Func<bool> current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (!IsCurrent(current)) return null;
        if (!Owner.UsesTriangles) throw new InvalidOperationException("This registration uses point placement.");
        var random = _random.NextRandomUInt32();
        if (!IsCurrent(current)) return null;
        return (float)(((ulong)random * 360u) >> 32) * DegreesToRadians;
    }
}

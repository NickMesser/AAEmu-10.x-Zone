using System.Numerics;
using AAEmu.Zone.Content;

namespace AAEmu.Zone.Simulation;

public interface IZoneNpcGroupFormationServices
{
    //3934CA40: engine terrain virtual428, with the native final argument fixed to zero.
    float GetTerrainHeight(float x, float y);
    //3934BFB0: actual AI virtual610. False leaves the candidate unchanged.
    bool AdjustPosition(Vector3 candidate, float radius, bool raisedAnchor, out Vector3 adjusted);
}

/// <summary>
/// Native3934CA40/3934BFB0 formation state. Its captured movement leader controls the terrain
/// adjustment and every member's AI query; individual member models do not change these flags.
/// </summary>
public sealed class ZoneNpcGroupFormation : IZoneNpcGroupFormation
{
    private readonly NpcGroupContent _group;
    private readonly IZoneNpcGroupFormationServices _services;
    private readonly Func<bool> _current;
    private bool _querying;
    public uint GroupType => _group.Definition.Id;
    public Vector3 Position { get; }
    public Quaternion Rotation { get; }
    public Vector3 AnchorOffset { get; }
    public bool FlyMode { get; }
    public bool UnderwaterCreature { get; }
    public float Radius { get; }
    public bool RaisedAnchor { get; }

    private ZoneNpcGroupFormation(NpcGroupContent group, Vector3 position, Quaternion rotation,
        Vector3 anchorOffset, ZoneNpcSpawnBriefData? brief, bool raisedAnchor,
        IZoneNpcGroupFormationServices services, Func<bool> current)
    {
        _group = group; _services = services; _current = current;
        Position = position; Rotation = rotation; AnchorOffset = anchorOffset;
        FlyMode = brief?.FlyMode ?? false; UnderwaterCreature = brief?.UnderwaterCreature ?? false;
        Radius = brief?.Radius ?? 1; RaisedAnchor = raisedAnchor;
    }

    /// <summary>Missing group or retired caller returns null; all engine queries remain required.</summary>
    public static ZoneNpcGroupFormation? Create(ZoneNpcContent content, uint groupType, Vector3 position,
        float yaw, int anchorMember, IZoneNpcGroupFormationServices services, Func<bool> current)
    {
        ArgumentNullException.ThrowIfNull(content); ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(current);
        if (!current() || !content.Groups.TryGetValue(groupType, out var group)) return null;
        ArgumentOutOfRangeException.ThrowIfNegative(anchorMember);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(anchorMember, group.Members.Count);
        var angle = yaw * .5f;
        var cosine = MathF.Cos(angle);
        var sine = MathF.Sin(angle);
        var anchor = group.Members[anchorMember];
        var brief = ZoneNpcSpawnCollision.GetBriefData(content.Templates[anchor.NpcTemplateId], content);
        var raised = false;
        if (brief is { FlyMode: false, UnderwaterCreature: false })
        {
            var terrainHeight = services.GetTerrainHeight(position.X, position.Y);
            if (!current()) return null;
            if (terrainHeight - .1f <= position.Z)
            {
                position = new(position.X, position.Y, position.Z + .4f);
                raised = true;
            }
        }
        return current() ? new(group, position, new(0, 0, sine, cosine), anchor.FormationOffset,
            brief, raised, services, current) : null;
    }

    public Vector3? TryGetMemberPosition(int memberIndex)
    {
        if (!_current()) return null;
        ArgumentOutOfRangeException.ThrowIfNegative(memberIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(memberIndex, _group.Members.Count);
        if (_querying) throw new InvalidOperationException("A group formation cannot query its members recursively.");
        _querying = true;
        try
        {
            var offset = _group.Members[memberIndex].FormationOffset;
            var rotated = Rotate(new(offset.X - AnchorOffset.X, offset.Y - AnchorOffset.Y,
                offset.Z - AnchorOffset.Z), Rotation);
            var candidate = new Vector3(Position.X + rotated.X, Position.Y + rotated.Y, Position.Z + rotated.Z);
            if (!FlyMode && !UnderwaterCreature)
            {
                var accepted = _services.AdjustPosition(candidate, Radius, RaisedAnchor, out var adjusted);
                if (!_current()) return null;
                if (accepted) candidate = adjusted;
            }
            return _current() ? candidate : null;
        }
        finally { _querying = false; }
    }

    public Vector3 GetMemberPosition(byte memberIndex) => TryGetMemberPosition(memberIndex) ??
        throw new InvalidOperationException("The caller owning this group formation has retired.");

    //390ADC00 uses scalar operations and (cross + input) + cross, without normalization.
    private static Vector3 Rotate(Vector3 value, Quaternion rotation)
    {
        var tx = (value.Z * rotation.Y - value.Y * rotation.Z) + value.X * rotation.W;
        var ty = (value.X * rotation.Z - rotation.X * value.Z) + rotation.W * value.Y;
        var tz = (rotation.X * value.Y - value.X * rotation.Y) + rotation.W * value.Z;
        var cx = rotation.Y * tz - rotation.Z * ty;
        var cy = rotation.Z * tx - rotation.X * tz;
        var cz = rotation.X * ty - rotation.Y * tx;
        return new((cx + value.X) + cx, (cy + value.Y) + cy, (cz + value.Z) + cz);
    }
}

using System.Numerics;
using AAEmu.Zone.Protocol.Messages;

namespace AAEmu.Zone.Simulation;

public interface IZoneNpcAreaSpawnServices
{
    // Native integer cvar 3A607CE0 != 0, after the scheduler's attempt-clock sample.
    bool SpawningDisabled { get; }
    // Engine virtual428, with its final native argument zero.
    float GetTerrainHeight(float x, float y);
    // Engine virtual238(candidate, 0, radius, 2, 0).
    float GetWaterLevel(Vector3 candidate, float radius);
    bool IsForbiddenTemplate(uint templateId);
    void ReportInvalidPlacement(ZoneNpcSpawnerRegistration owner);
    /// <summary>
    /// A named refusal for one placement whose spawn separation could not be evaluated (missing NPC
    /// entity script tables). Counted and reported at readiness; the placement is rejected, never
    /// admitted unseparated.
    /// </summary>
    void ReportSeparationRefusal(ZoneNpcSpawnerRegistration owner, string reason);
}

/// <summary>
/// The authored-triangle branch of39348AF0, including environmental rejection, bounded proximity
/// retries and direct or group admission. The scheduler and registry retain their native clock,
/// population and pending-request ownership. Scene callback retirement is a managed ownership policy.
/// </summary>
public sealed class ZoneNpcAreaSpawner
{
    private readonly ZoneNpcSpawnerRegistry _spawners;
    private readonly ZoneNpcSpawnPlacement _placement;
    private readonly ZoneNpcSpawnProximity _proximity;
    private readonly ZoneNpcSpawnCollision _collision;
    private readonly IZoneNpcAreaSpawnServices _services;
    private readonly ZoneNpcGroupSpawner? _groups;
    private bool _attempting;
    // Separation refusals are a NAMED, PERMANENT dependency gap (no authored NPC spawn-script
    // source), not a transient block: the first live host run retried every rejected placement
    // after 1 s and produced ~1,800 refusals per minute (307 KB of log in 90 s). The retry
    // therefore backs off per spawner from 5 s to 60 s and then holds, while the refusal stays
    // counted and visible at readiness.
    private uint _separationBackoffMilliseconds = 5_000;
    public ZoneNpcSpawnerRegistration Owner { get; }

    public ZoneNpcAreaSpawner(ZoneNpcSpawnerRegistry spawners, ZoneNpcSpawnPlacement placement,
        ZoneNpcSpawnProximity proximity, ZoneNpcSpawnCollision collision, IZoneNpcAreaSpawnServices services,
        ZoneNpcGroupSpawner? groups = null)
    {
        _spawners = spawners ?? throw new ArgumentNullException(nameof(spawners));
        _placement = placement ?? throw new ArgumentNullException(nameof(placement));
        _proximity = proximity ?? throw new ArgumentNullException(nameof(proximity));
        _collision = collision ?? throw new ArgumentNullException(nameof(collision));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _groups = groups;
        Owner = placement.Owner;
        if (!_spawners.IsAccepting(Owner)) throw new ArgumentException("The placement requires its accepting spawner registry.", nameof(placement));
        if (!Owner.UsesTriangles) throw new ArgumentException("Area spawning requires authored triangle placement.", nameof(placement));
        if (!ReferenceEquals(proximity.Owner, Owner)) throw new ArgumentException("Proximity belongs to another spawner instance.", nameof(proximity));
        if (!ReferenceEquals(collision.Owner, Owner)) throw new ArgumentException("Collision admission belongs to another spawner instance.", nameof(collision));
        if (groups is not null && !ReferenceEquals(groups.Owner, Owner))
            throw new ArgumentException("Group expansion belongs to another spawner instance.", nameof(groups));
        if (Owner.Content.Members.Any(member => member.Group is not null) && groups is null)
            throw new ArgumentException("This area spawner requires its actual group formation and allocation services.", nameof(groups));
    }

    private bool IsCurrent() => _spawners.IsAccepting(Owner) && _placement.IsLive;

    /// <summary>Call from AttemptSpawn after its native clock sample; below-minimum attempts are forced.</summary>
    public ZoneNpcSpawnAttempt Attempt(bool force, ZoneNpcSpawnOverrides? overrides = null)
    {
        if (!IsCurrent()) return new(false, 1000);
        if (_attempting) throw new InvalidOperationException("An area spawn attempt cannot run recursively.");
        _attempting = true;
        try { return AttemptCore(force, overrides ?? ZoneNpcSpawnOverrides.Empty); }
        finally { _attempting = false; }
    }

    private ZoneNpcSpawnAttempt AttemptCore(bool force, ZoneNpcSpawnOverrides overrides)
    {
        var disabled = _services.SpawningDisabled;
        if (!IsCurrent() || disabled) return new(false, 1000);
        var member = _placement.SelectMember(IsCurrent);
        if (member is null || !IsCurrent()) return new(false, 1000);
        var brief = ZoneNpcSpawnCollision.GetBriefData(member.Member, _spawners.NpcContent);
        if (brief is null) return new(false, 60000);
        var triangle = _placement.SelectTriangle(IsCurrent);
        if (triangle is null || !IsCurrent()) return new(false, 1000);

        var ground = !brief.FlyMode && !brief.UnderwaterCreature;
        var budget = ground ? 50 : 5;
        var work = 0;
        var blocked = true;
        var position = Vector3.Zero;
        while (work < budget)
        {
            var sampled = _placement.SampleTrianglePosition(triangle, IsCurrent);
            if (sampled is null || !IsCurrent()) return new(false, 1000);
            position = sampled.Value;
            if (ground)
            {
                var terrain = _services.GetTerrainHeight(position.X, position.Y);
                if (!IsCurrent()) return new(false, 1000);
                position.Z = terrain + .1f;
            }
            else
            {
                var water = _services.GetWaterLevel(position, brief.Radius);
                if (!IsCurrent()) return new(false, 1000);
                //39349149/JBE puts equal and unordered values on the same branch.
                var above = water > position.Z;
                var invalid = above ? !brief.UnderwaterCreature && brief.FlyMode : brief.UnderwaterCreature;
                if (invalid)
                {
                    _services.ReportInvalidPlacement(Owner);
                    return new(false, IsCurrent() ? 600000u : 1000u);
                }
                if (above ? brief.UnderwaterCreature : brief.FlyMode) position.Z += .1f;
            }

            work = unchecked(work + 1);
            var additionalWork = 0;
            blocked = _proximity.HasNearbyCharacter(position, IsCurrent);
            if (!IsCurrent()) return new(false, 1000);
            if (!blocked)
            {
                try
                {
                    blocked = _proximity.HasNearbySpawnedNpc(position, ref additionalWork, IsCurrent);
                }
                catch (InvalidOperationException separation)
                {
                    // Separation is unevaluable without the NPC entity script tables (the managed
                    // host does not publish per-NPC Lua tables yet). That is a NAMED refusal for
                    // THIS spawn — the placement is rejected, never admitted unseparated — and the
                    // zone keeps running: the first live World connect (R5, 2026-09-23) proved that
                    // letting this escape retires the whole zone seconds after ZWZoneLoaded.
                    _services.ReportSeparationRefusal(Owner, separation.Message);
                    var next = _separationBackoffMilliseconds;
                    _separationBackoffMilliseconds = next >= 60_000 ? next : Math.Min(next * 2, 60_000);
                    return new(false, next);
                }
                if (!IsCurrent()) return new(false, 1000);
            }
            work = unchecked(work + additionalWork);
            if (!blocked) break;
        }
        // Force still performs every environmental/proximity check and keeps the last candidate.
        if (blocked && !force) return new(false, 0);
        var yaw = _placement.SampleTriangleYaw(IsCurrent);
        if (yaw is null || !IsCurrent()) return new(false, 1000);

        if (member.Member.Group is { } group)
        {
            //39348AF0 narrows the group triangle through a byte; physical counters use its full index.
            var seed = new NpcSpawnRequest(Owner.Id, Owner.SpawnerType, unchecked((byte)member.Index),
                0, unchecked((byte)triangle.Index), 0, new(group.Definition.Id, 0, 0), position,
                yaw.Value, 0, overrides.Creator, "", "", overrides.FactionId, overrides.Reason,
                overrides.DespawnOnCreatorDeath, overrides.UseSummonerAggroTarget, overrides.LifeTime, false);
            var accepted = _groups!.Spawn(seed, member.Index, triangle.Index, IsCurrent);
            return new(accepted, accepted ? 0u : 1000u);
        }

        var npc = member.Member.Npc!;
        position.Z += .4f;
        var request = new NpcSpawnRequest(Owner.Id, Owner.SpawnerType, unchecked((byte)member.Index),
            0, unchecked((ushort)triangle.Index), npc.Id, default, position, yaw.Value, 0,
            overrides.Creator, "", "", overrides.FactionId, overrides.Reason,
            overrides.DespawnOnCreatorDeath, overrides.UseSummonerAggroTarget, overrides.LifeTime, false);
        var acceptedPosition = _collision.ValidateArea(request, IsCurrent);
        if (!IsCurrent() || acceptedPosition is null) return new(false, 1000);
        request = request with { Position = acceptedPosition.Value };
        var forbidden = _services.IsForbiddenTemplate(npc.Id);
        if (!IsCurrent() || forbidden) return new(false, 1000);
        var sent = _spawners.SendInitial(Owner, request, member.Index, triangle.Index);
        return new(sent, sent ? 0u : 1000u);
    }
}

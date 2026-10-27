using AAEmu.Zone.Protocol.Messages;
using AAEmu.Zone.Protocol.Units;

namespace AAEmu.Zone.Simulation;

/// <summary>The optional spawn-context fields copied by 39348AF0, excluding authored placement.</summary>
public sealed record ZoneNpcSpawnOverrides(NpcSpawnReason Reason, UnitIdentity Creator, uint FactionId,
    bool DespawnOnCreatorDeath, bool UseSummonerAggroTarget, float LifeTime)
{
    public static ZoneNpcSpawnOverrides Empty { get; } = new(new(0), new(ZoneUnitKind.Character), 0, false, false, 0);
}

public interface IZoneNpcPointSpawnServices : IZoneNpcSpawnRandom, IZoneNpcSpawnCharacterProximityServices
{
    // Native integer cvar 3A607CE0 != 0, sampled after the scheduler records the attempt clock.
    bool SpawningDisabled { get; }
    // The NPC manager's actual forbidden-template set, checked after collision admission.
    bool IsForbiddenTemplate(uint templateId);
}

/// <summary>
/// Authored point attempts through39348AF0, with direct capsule admission or native group expansion.
/// The existing scheduler owns
/// attempt clocks and total population; the registry owns outgoing requests and member counts.
/// </summary>
public sealed class ZoneNpcPointSpawner
{
    private readonly ZoneNpcSpawnerRegistry _spawners;
    private readonly ZoneNpcSpawnPlacement _placement;
    private readonly ZoneNpcSpawnCollision _collision;
    private readonly ZoneNpcSpawnProximity _proximity;
    private readonly IZoneNpcPointSpawnServices _services;
    private readonly ZoneNpcGroupSpawner? _groups;
    private bool _attempting;
    public ZoneNpcSpawnerRegistration Owner { get; }
    public int SuspendedAttempts { get => Owner.SuspendedAttempts; private set => Owner.SuspendedAttempts = value; }

    public ZoneNpcPointSpawner(ZoneNpcSpawnerRegistry spawners, ZoneNpcSpawnPlacement placement,
        ZoneNpcSpawnCollision collision, IZoneNpcPointSpawnServices services, ZoneNpcGroupSpawner? groups = null)
    {
        _spawners = spawners ?? throw new ArgumentNullException(nameof(spawners));
        _placement = placement ?? throw new ArgumentNullException(nameof(placement));
        _collision = collision ?? throw new ArgumentNullException(nameof(collision));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _groups = groups;
        Owner = placement.Owner;
        if (!_spawners.IsAccepting(Owner)) throw new ArgumentException("The placement requires its accepting spawner registry.", nameof(placement));
        if (Owner.UsesTriangles) throw new ArgumentException("Point spawning requires authored point placement.", nameof(placement));
        if (!ReferenceEquals(collision.Owner, Owner)) throw new ArgumentException("Collision admission belongs to another spawner instance.", nameof(collision));
        if (groups is not null && !ReferenceEquals(groups.Owner, Owner))
            throw new ArgumentException("Group expansion belongs to another spawner instance.", nameof(groups));
        if (Owner.Content.Members.Any(member => member.Group is not null) && groups is null)
            throw new ArgumentException("This point spawner requires its actual group formation and allocation services.", nameof(groups));
        _proximity = new(_spawners.Registry, Owner, services);
    }

    private bool IsCurrent() => _spawners.IsAccepting(Owner) && _placement.IsLive;

    /// <summary>Call from AttemptSpawn after its native clock sample; below-minimum attempts are forced.</summary>
    public ZoneNpcSpawnAttempt Attempt(bool force, ZoneNpcSpawnOverrides? overrides = null)
    {
        if (!IsCurrent()) return new(false, 1000);
        if (_attempting) throw new InvalidOperationException("A point spawn attempt cannot run recursively.");
        _attempting = true;
        try { return AttemptCore(force, overrides ?? ZoneNpcSpawnOverrides.Empty); }
        finally { _attempting = false; }
    }

    private ZoneNpcSpawnAttempt AttemptCore(bool force, ZoneNpcSpawnOverrides overrides)
    {
        var disabled = _services.SpawningDisabled;
        if (!IsCurrent() || disabled) return new(false, 1000);
        var selected = _placement.SelectMember(IsCurrent);
        if (selected is null || !IsCurrent()) return new(false, 1000);
        var brief = ZoneNpcSpawnCollision.GetBriefData(selected.Member, _spawners.NpcContent);
        if (brief is null) return new(false, 60000);
        var candidate = _placement.SelectPointCandidate(IsCurrent);
        if (candidate is null || !IsCurrent()) return new(false, 1000);
        var point = candidate;
        if (!force && _proximity.HasNearbyCharacter(point.Position, IsCurrent))
        {
            if (!IsCurrent()) return new(false, 1000);
            SuspendedAttempts = unchecked(SuspendedAttempts + 1);
            var definition = Owner.Content.Definition;
            if (SuspendedAttempts <= definition.SuspendSpawnCount)
            {
                var random = _services.NextRandomFloat();
                if (!IsCurrent()) return new(false, 1000);
                var milliseconds = (random * (definition.SpawnDelayMaximumSeconds - definition.SpawnDelayMinimumSeconds) +
                    definition.SpawnDelayMinimumSeconds) * 1000f;
                return new(false, unchecked((uint)ZoneStatMath.ToInt64(milliseconds)));
            }
        }
        if (!IsCurrent()) return new(false, 1000);
        SuspendedAttempts = 0;
        if (selected.Member.Group is { } group)
        {
            var seed = new NpcSpawnRequest(Owner.Id, Owner.SpawnerType, unchecked((byte)selected.Index),
                unchecked((byte)point.PartIndex), 0, 0, new(group.Definition.Id, 0, 0), point.Position,
                point.ZRotation, 0, overrides.Creator, "", "", overrides.FactionId, overrides.Reason,
                overrides.DespawnOnCreatorDeath, overrides.UseSummonerAggroTarget, overrides.LifeTime, false);
            var accepted = _groups!.Spawn(seed, selected.Index, 0, IsCurrent);
            return new(accepted, accepted ? 0u : 1000u);
        }
        var npc = selected.Member.Npc!;
        var request = new NpcSpawnRequest(Owner.Id, Owner.SpawnerType, unchecked((byte)selected.Index),
            unchecked((byte)point.PartIndex), 0, npc.Id, default,
            new(point.Position.X, point.Position.Y, point.Position.Z + .4f), point.ZRotation, 0,
            overrides.Creator, "", "", overrides.FactionId, overrides.Reason,
            overrides.DespawnOnCreatorDeath, overrides.UseSummonerAggroTarget, overrides.LifeTime, false);
        var position = _collision.ValidatePoint(request, IsCurrent);
        if (!IsCurrent() || position is null) return new(false, 1000);
        request = request with { Position = position.Value };
        var forbidden = _services.IsForbiddenTemplate(npc.Id);
        if (!IsCurrent() || forbidden) return new(false, 1000);
        return new(_spawners.SendInitial(Owner, request, selected.Index, 0), 0);
    }
}

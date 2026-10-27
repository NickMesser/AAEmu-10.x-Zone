using AAEmu.Zone.Protocol.Messages;

namespace AAEmu.Zone.Npcs;

public interface IZoneNpcGroupSpawnServices : IZoneNpcGroupFormationServices
{
    bool IsForbiddenTemplate(uint templateId);
    //39344350 makes one actual pool allocation: negative is exhaustion; zero is the invalid ID.
    int AllocateGroupId();
    // Release an acquired positive ID only before registry ownership has transferred.
    // This must address the same pool as IZoneNpcGroupServices.FreeGroupId after transfer.
    void ReleaseGroupId(uint groupId);
    void ReportGroupAllocationFailure();
}

/// <summary>
///39344530 initial group expansion. Formation precedes allocation; each fresh forbidden lookup
/// and position query precedes that member's saved request and queued packet. No capsule checks
/// belong to this branch. The registry owns admitted groups, requests and population counts.
/// </summary>
public sealed class ZoneNpcGroupSpawner
{
    private readonly ZoneNpcSpawnerRegistry _spawners;
    private readonly IZoneNpcGroupSpawnServices _services;
    private bool _spawning;
    private readonly object _generation;
    public ZoneNpcSpawnerRegistration Owner { get; }

    public ZoneNpcGroupSpawner(ZoneNpcSpawnerRegistry spawners, ZoneNpcSpawnerRegistration owner,
        IZoneNpcGroupSpawnServices services)
    {
        _spawners = spawners ?? throw new ArgumentNullException(nameof(spawners));
        Owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _generation = owner.ContentGeneration;
        _services = services ?? throw new ArgumentNullException(nameof(services));
        if (!spawners.IsAccepting(owner)) throw new ArgumentException("The group spawner requires its accepting registry.", nameof(owner));
    }

    public bool Spawn(NpcSpawnRequest seed, int sourceMember, int sourceTriangle, Func<bool> current)
    {
        ArgumentNullException.ThrowIfNull(seed); ArgumentNullException.ThrowIfNull(current);
        if (seed.SpawnerId != Owner.Id || seed.SpawnerType != Owner.SpawnerType)
            throw new ArgumentException("The request belongs to another spawner instance.", nameof(seed));
        bool IsOwned() => _spawners.IsAccepting(Owner) && ReferenceEquals(_generation, Owner.ContentGeneration);
        bool IsCurrent() => IsOwned() && current() && IsOwned();
        if (!IsCurrent()) return false;
        if (_spawning) throw new InvalidOperationException("An initial NPC group cannot spawn recursively.");
        _spawning = true;
        try { return SpawnCore(seed, sourceMember, sourceTriangle, IsCurrent); }
        finally { _spawning = false; }
    }

    private bool SpawnCore(NpcSpawnRequest seed, int sourceMember, int sourceTriangle, Func<bool> current)
    {
        if (!_spawners.NpcContent.Groups.TryGetValue(seed.Group.Type, out var group)) return false;
        if (group.Members.Count > 20) throw new InvalidDataException("NPC groups have a fixed native capacity of 20 members.");
        var anchor = -1;
        for (var index = 0; index < group.Members.Count; index++)
        {
            var member = group.Members[index];
            var forbidden = _services.IsForbiddenTemplate(member.NpcTemplateId);
            if (!current()) return false;
            if (!forbidden && member.IsMoveLeader) anchor = index; // Last eligible movement leader.
        }
        if (anchor < 0) return false;
        var formation = ZoneNpcGroupFormation.Create(_spawners.NpcContent, group.Definition.Id,
            seed.Position, seed.Yaw, anchor, _services, current);
        if (formation is null || !current()) return false;

        var allocated = _services.AllocateGroupId();
        var acquiredId = allocated > 0 ? (uint)allocated : 0;
        try
        {
            if (!current()) return false;
            if (allocated < 0)
            {
                _services.ReportGroupAllocationFailure();
                if (!current()) return false;
            }
            seed = seed with { Group = seed.Group with { Id = acquiredId } };
            using var admission = _spawners.BeginInitialGroup(Owner, acquiredId, seed, sourceMember, sourceTriangle);
            if (admission is null) return false;
            acquiredId = 0; // Registry now owns a positive allocation, including cancellation.
            if (!current()) return false;
            for (var index = 0; index < group.Members.Count; index++)
            {
                var member = group.Members[index];
                var forbidden = _services.IsForbiddenTemplate(member.NpcTemplateId);
                if (!current()) return false;
                if (forbidden) continue;
                var position = formation.TryGetMemberPosition(index);
                if (position is null || !current()) return false;
                var request = seed with { TemplateId = member.NpcTemplateId, Position = position.Value,
                    Group = seed.Group with { MemberIndex = (byte)index } };
                if (!_spawners.AppendInitialGroup(admission, request) || !current()) return false;
            }
            // Native still succeeds when allocation returned invalid ID0 or every second-pass
            // member became forbidden. The full group mask is never reduced to the sent subset.
            return _spawners.CompleteInitialGroup(admission);
        }
        finally { if (acquiredId != 0) _services.ReleaseGroupId(acquiredId); }
    }
}

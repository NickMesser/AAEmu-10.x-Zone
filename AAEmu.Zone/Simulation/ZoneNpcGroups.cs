using System.Numerics;
using AAEmu.Zone.Content;
using AAEmu.Zone.Protocol;
using AAEmu.Zone.Protocol.Messages;
using AAEmu.Zone.Protocol.Units;

namespace AAEmu.Zone.Simulation;

/// <summary>The live formation/physics calculation at 3934CA40/3934BFB0, owned by the scene.</summary>
public interface IZoneNpcGroupFormation
{
    Vector3 GetMemberPosition(byte memberIndex);
}

public interface IZoneNpcGroupServices
{
    uint TimeMilliseconds { get; }
    float NextRandomFloat();
    // Missing entity, AI object or Puppet is represented by null. State 2 blocks respawning.
    int? GetPuppetAlertness(ZoneNpc npc);
    // Native AI +448 -> returned object +208 -> metadata byte +8. This is live scene state,
    // not the content's first leader or movement leader.
    byte? GetFormationAnchorMember(uint groupId);
    Quaternion GetRotation(ZoneNpc npc);
    Vector3 GetPosition(ZoneNpc npc);
    IZoneNpcGroupFormation CreateFormation(uint groupType, Vector3 position, float yaw, byte anchorMember);
    void Send(ZoneFrame frame);
    void FreeGroupId(uint groupId);
    void DrawSphere(Vector3 position, float radius, uint color, bool shaded);
    void DrawText(Vector3 position, float scale, string text);
}

public enum ZoneNpcGroupAcknowledgement { Ignored, InitialSpawn, Respawn }
public enum ZoneNpcGroupRemoval { Ignored, GroupRetained, GroupRetired }

/// <summary>An exact group allocation lifetime; retaining this handle cannot retarget a reused group ID.</summary>
public sealed class ZoneNpcGroupRegistration
{
    internal sealed class Request(NpcSpawnRequest value)
    {
        internal NpcSpawnRequest Value { get; } = value;
    }
    internal readonly NpcSpawnRequest[] Saved;
    internal readonly Request?[] Pending;
    internal readonly ZoneNpc?[] Owners;
    internal bool Respawning;
    public uint Id { get; }
    public uint GroupType { get; }
    public int MemberCount => Saved.Length;
    public bool RespawnEnabled { get; }
    public float MinimumDelaySeconds { get; }
    public float DelayRangeSeconds { get; }
    public uint RequestedMask { get; internal set; }
    public bool HasRespawned { get; internal set; }
    // 3934C110/39344350 leave the timestamp untouched. Delay zero makes its initial bits irrelevant.
    public uint? LastResetTime { get; internal set; }
    public uint DelayMilliseconds { get; internal set; }
    public bool IsRetired { get; internal set; }
    internal uint FullMask => (1u << MemberCount) - 1;

    internal ZoneNpcGroupRegistration(uint id, NpcGroupContent content, float minimum, float range)
    {
        Id = id; GroupType = content.Definition.Id; RespawnEnabled = content.Definition.EnableRespawn;
        MinimumDelaySeconds = minimum; DelayRangeSeconds = range;
        Saved = new NpcSpawnRequest[content.Members.Count];
        Pending = new Request?[Saved.Length]; Owners = new ZoneNpc?[Saved.Length];
        // 39344350 fills all 20 native records with these defaults. Invalid IDs are zero-filled
        // read-only globals 3A607D70/74/DA4/DA8/DB0/DB4/DB8. Unused native capacity has no API slot.
        Array.Fill(Saved, new(0, 0, 0, 0, 0, 0, default, Vector3.Zero, 0, 0,
            new(ZoneUnitKind.Character), "", "", 0, new(), false, false, 0, false));
        RequestedMask = FullMask;
    }

    public NpcSpawnRequest GetSavedRequest(byte index) => Saved[index];
    public uint GetNpcId(byte index) => Owners[index]?.Id ?? 0;
    public bool IsRequested(byte index)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((int)index, MemberCount);
        return (RequestedMask & (1u << index)) != 0;
    }
}

/// <summary>
/// Owned NpcGroupSupporter state (39344350/39343310/39347F90/39343460). IDs must come from the
/// actual group allocator. This implements saved requests and member/timer ownership; live formation,
/// Puppet state, physics and rendering remain required scene services.
/// </summary>
public sealed class ZoneNpcGroups : IZoneNpcGroupPhase, IDisposable
{
    private readonly ZoneNpcSimulation _npcs;
    private readonly ZoneNpcContent _content;
    private readonly IZoneNpcGroupServices _services;
    private readonly SortedDictionary<uint, ZoneNpcGroupRegistration> _groups = [];
    private bool _updating;
    public bool IsDisposed { get; private set; }
    public int Count => _groups.Count;
    private bool IsActive => !IsDisposed && !_npcs.IsStopped;

    public ZoneNpcGroups(ZoneNpcSimulation npcs, ZoneNpcContent content, IZoneNpcGroupServices services)
    {
        _npcs = npcs ?? throw new ArgumentNullException(nameof(npcs));
        _content = content ?? throw new ArgumentNullException(nameof(content));
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    public ZoneNpcGroupRegistration Register(uint allocatedGroupId, uint groupType, float minimumDelaySeconds, float delayRangeSeconds)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (!IsActive) throw new InvalidOperationException("The NPC scene has stopped accepting groups.");
        ArgumentOutOfRangeException.ThrowIfZero(allocatedGroupId);
        if (!_content.Groups.TryGetValue(groupType, out var content))
            throw new InvalidDataException($"Missing NPC group content {groupType}.");
        if (content.Members.Count > 20) throw new InvalidDataException("NPC groups have a fixed native capacity of 20 members.");
        var group = new ZoneNpcGroupRegistration(allocatedGroupId, content, minimumDelaySeconds, delayRangeSeconds);
        _groups.Add(group.Id, group);
        return group;
    }

    public ZoneNpcGroupRegistration? Find(uint id) => _groups.GetValueOrDefault(id);
    private bool IsCurrent(ZoneNpcGroupRegistration group) => IsActive && !group.IsRetired &&
        _groups.TryGetValue(group.Id, out var current) && ReferenceEquals(group, current);
    private bool IsCurrent(ZoneNpc npc) => !npc.Activation.IsDisposed &&
        _npcs.Npcs.TryGetValue(npc.Id, out var current) && ReferenceEquals(npc, current);

    /// <summary>39344530 saves the complete initial request before sending it; the full mask already exists.</summary>
    public bool SaveRequest(ZoneNpcGroupRegistration group, byte memberIndex, NpcSpawnRequest request)
    {
        ArgumentNullException.ThrowIfNull(group); ArgumentNullException.ThrowIfNull(request);
        if (!IsCurrent(group)) return false;
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((int)memberIndex, group.MemberCount);
        // Replacement admission validation: supplied content and request identity must describe this slot.
        if (request.Group != new NpcSpawnGroup(group.GroupType, group.Id, memberIndex) ||
            request.TemplateId != _content.Groups[group.GroupType].Members[memberIndex].NpcTemplateId)
            throw new InvalidDataException("The saved NPC request does not match its group member content.");
        request.ToFrame(); // Validate the actual complete wire body before taking ownership.
        group.Saved[memberIndex] = request;
        group.Pending[memberIndex] = new(request);
        return true;
    }

    public ZoneNpcGroupAcknowledgement Acknowledge(ZoneNpcGroupRegistration group, ZoneNpc npc)
    {
        ArgumentNullException.ThrowIfNull(group); ArgumentNullException.ThrowIfNull(npc);
        var index = npc.Spawn.Group.MemberIndex;
        if (!IsCurrent(group) || !IsCurrent(npc) || index >= group.MemberCount ||
            npc.Spawn.Group.Id != group.Id || group.Pending[index] is not { } pending ||
            group.Owners[index] is not null || !Matches(npc, pending.Value)) return ZoneNpcGroupAcknowledgement.Ignored;
        // Native 39347F90 writes NPC identity (not ServerUnit object ID) and leaves the request mask alone.
        group.Owners[index] = npc;
        return group.HasRespawned ? ZoneNpcGroupAcknowledgement.Respawn : ZoneNpcGroupAcknowledgement.InitialSpawn;
    }

    public ZoneNpcGroupAcknowledgement Acknowledge(ZoneNpc npc)
    {
        ArgumentNullException.ThrowIfNull(npc);
        return Find(npc.Spawn.Group.Id) is { } group ? Acknowledge(group, npc) : ZoneNpcGroupAcknowledgement.Ignored;
    }

    /// <summary>
    /// Native 39347F90 also admits World replay without an outstanding request. The exact group
    /// allocation and content-valid current NPC must already exist; this never fabricates saved
    /// requests or changes the mask. The wire has no request generation, so replay provenance
    /// cannot be distinguished from an identical delayed acknowledgment after ID reuse.
    /// </summary>
    public ZoneNpcGroupAcknowledgement AcknowledgeReplay(ZoneNpcGroupRegistration group, ZoneNpc npc)
    {
        ArgumentNullException.ThrowIfNull(group); ArgumentNullException.ThrowIfNull(npc);
        var index = npc.Spawn.Group.MemberIndex;
        if (!IsCurrent(group) || !IsCurrent(npc) || index >= group.MemberCount || group.Owners[index] is not null ||
            npc.Spawn.Group != new NpcSpawnGroup(group.GroupType, group.Id, index) ||
            npc.TemplateId != _content.Groups[group.GroupType].Members[index].NpcTemplateId)
            return ZoneNpcGroupAcknowledgement.Ignored;
        group.Owners[index] = npc;
        return group.HasRespawned ? ZoneNpcGroupAcknowledgement.Respawn : ZoneNpcGroupAcknowledgement.InitialSpawn;
    }

    private static bool Matches(ZoneNpc npc, NpcSpawnRequest request) => npc.TemplateId == request.TemplateId &&
        (npc.Spawn with { SpawningEffectTime = 0 }) == NpcStateMetadata.FromRequest(request);

    public ZoneNpcGroupRemoval SpawnFailed(NpcSpawnRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Find(request.Group.Id) is { } group ? SpawnFailed(group, request) : ZoneNpcGroupRemoval.GroupRetired;
    }

    public ZoneNpcGroupRemoval SpawnFailed(ZoneNpcGroupRegistration group, NpcSpawnRequest request)
    {
        ArgumentNullException.ThrowIfNull(group); ArgumentNullException.ThrowIfNull(request);
        if (!IsCurrent(group)) return ZoneNpcGroupRemoval.GroupRetired;
        var index = request.Group.MemberIndex;
        if (index >= group.MemberCount || group.Owners[index] is not null ||
            group.Pending[index] is not { } pending ||
            !pending.Value.ToFrame().Body.Span.SequenceEqual(request.ToFrame().Body.Span))
            return ZoneNpcGroupRemoval.Ignored;
        return RemoveMember(group, index);
    }

    public ZoneNpcGroupRemoval RemoveMember(ZoneNpcGroupRegistration group, ZoneNpc npc)
    {
        ArgumentNullException.ThrowIfNull(group); ArgumentNullException.ThrowIfNull(npc);
        if (!IsCurrent(group)) return ZoneNpcGroupRemoval.GroupRetired;
        var index = npc.Spawn.Group.MemberIndex;
        if (index >= group.MemberCount || !ReferenceEquals(group.Owners[index], npc)) return ZoneNpcGroupRemoval.Ignored;
        return RemoveMember(group, index);
    }

    public ZoneNpcGroupRemoval RemoveMember(ZoneNpc npc)
    {
        ArgumentNullException.ThrowIfNull(npc);
        return Find(npc.Spawn.Group.Id) is { } group ? RemoveMember(group, npc) : ZoneNpcGroupRemoval.GroupRetired;
    }

    private ZoneNpcGroupRemoval RemoveMember(ZoneNpcGroupRegistration group, byte index)
    {
        group.Owners[index] = null; group.Pending[index] = null; group.RequestedMask &= ~(1u << index);
        if (group.RequestedMask != 0) return ZoneNpcGroupRemoval.GroupRetained;
        // Native frees the ID and erases the map entry. Managed retirement precedes the allocator
        // callback so a reentrant allocation cannot be erased by this older registration.
        _groups.Remove(group.Id); group.IsRetired = true;
        _services.FreeGroupId(group.Id);
        return ZoneNpcGroupRemoval.GroupRetired;
    }

    /// <summary>
    /// Explicit managed instance teardown: retire this exact allocation without requesting physical
    /// NPC removal. Ordinary native member retirement remains RemoveMember's last-mask-bit path.
    /// </summary>
    public bool Retire(ZoneNpcGroupRegistration group)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (IsDisposed || group.IsRetired || !_groups.TryGetValue(group.Id, out var current) || !ReferenceEquals(group, current)) return false;
        group.IsRetired = true; _groups.Remove(group.Id);
        Array.Clear(group.Owners); Array.Clear(group.Pending);
        _services.FreeGroupId(group.Id);
        return true;
    }

    /// <summary>393422D0: only Puppet alertness 2 blocks a partially populated, enabled group.</summary>
    public bool IsReady(ZoneNpcGroupRegistration group)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (!IsCurrent(group) || !group.RespawnEnabled || group.RequestedMask == group.FullMask) return false;
        for (byte index = 0; index < group.MemberCount; index++)
        {
            var npc = group.Owners[index];
            if (npc is null || !IsCurrent(npc)) continue;
            var alertness = _services.GetPuppetAlertness(npc);
            if (!IsCurrent(group)) return false;
            if (ReferenceEquals(group.Owners[index], npc) && IsCurrent(npc) && alertness == 2) return false;
        }
        return true;
    }

    public void Update()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (!IsActive) return;
        if (_updating) throw new InvalidOperationException("NPC group updates cannot run recursively.");
        _updating = true;
        try
        {
            // Managed callback policy: retain exact owners for this traversal; new registrations
            // wait until the next pass. Native's tree iterator assumes these callbacks cannot erase it.
            foreach (var group in _groups.Values.ToArray())
            {
                if (!IsCurrent(group)) continue;
                var ready = IsReady(group);
                if (!IsCurrent(group)) continue;
                var now = _services.TimeMilliseconds;
                if (!IsCurrent(group)) continue;
                if (!ready)
                {
                    group.LastResetTime = now;
                    var sample = _services.NextRandomFloat();
                    if (!IsCurrent(group)) continue;
                    var seconds = sample * group.DelayRangeSeconds + group.MinimumDelaySeconds;
                    group.DelayMilliseconds = unchecked((uint)ZoneStatMath.ToInt64(seconds * 1000f));
                }
                else if (group.DelayMilliseconds == 0 || unchecked(now - group.LastResetTime!.Value) >= group.DelayMilliseconds)
                    Respawn(group);
            }
        }
        finally { _updating = false; }
    }

    private (IZoneNpcGroupFormation Formation, float Yaw)? CreateFormation(ZoneNpcGroupRegistration group)
    {
        var index = _services.GetFormationAnchorMember(group.Id);
        if (!IsCurrent(group) || index is null || index >= group.MemberCount) return null;
        var npc = group.Owners[index.Value];
        if (npc is null || !IsCurrent(npc)) return null;
        var rotation = _services.GetRotation(npc);
        if (!IsCurrent(group) || !ReferenceEquals(group.Owners[index.Value], npc) || !IsCurrent(npc)) return null;
        // 39341EF0/393423A0 use the actual anchor quaternion without normalization.
        var yaw = MathF.Atan2(-((rotation.Y * rotation.X - rotation.W * rotation.Z) * 2f),
            (rotation.Y * rotation.Y + rotation.W * rotation.W) * 2f - 1f);
        var position = _services.GetPosition(npc);
        if (!IsCurrent(group) || !ReferenceEquals(group.Owners[index.Value], npc) || !IsCurrent(npc)) return null;
        var formation = _services.CreateFormation(group.Saved[0].Group.Type, position, yaw, index.Value);
        if (!IsCurrent(group) || !ReferenceEquals(group.Owners[index.Value], npc) || !IsCurrent(npc)) return null;
        return (formation ?? throw new InvalidOperationException("The scene did not create the required group formation."), yaw);
    }

    public void Respawn(ZoneNpcGroupRegistration group)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (!IsCurrent(group)) return;
        if (group.Respawning) throw new InvalidOperationException("The same NPC group cannot respawn recursively.");
        group.Respawning = true;
        try { RespawnCore(group); }
        finally { group.Respawning = false; }
    }

    private void RespawnCore(ZoneNpcGroupRegistration group)
    {
        var context = CreateFormation(group);
        if (context is null) return;
        for (byte index = 0; index < group.MemberCount; index++)
        {
            if (!IsCurrent(group)) return;
            if (group.IsRequested(index)) continue;
            var saved = group.Saved[index];
            var previous = group.Pending[index];
            var previousOwner = group.Owners[index];
            var position = context.Value.Formation.GetMemberPosition(index);
            if (!IsCurrent(group)) return;
            if (!ReferenceEquals(group.Saved[index], saved) || !ReferenceEquals(group.Pending[index], previous) ||
                !ReferenceEquals(group.Owners[index], previousOwner) || group.IsRequested(index)) continue;
            var request = saved with { Position = position, Yaw = context.Value.Yaw, Reason = new(5) };
            var pending = new ZoneNpcGroupRegistration.Request(request);
            group.Pending[index] = pending;
            _services.Send(request.ToFrame());
            if (!IsCurrent(group)) return;
            // Native sends before setting the bit. A managed synchronous failure/removal retires
            // this particular request; the resumed sender must not resurrect it or a reused slot.
            if (!ReferenceEquals(group.Pending[index], pending)) continue;
            group.RequestedMask |= 1u << index;
            // Native validates these diagnostic lookups after sending and stops at the first miss.
            if (!_content.Groups.ContainsKey(request.Group.Type) || !_content.Templates.ContainsKey(request.TemplateId)) break;
        }
        if (IsCurrent(group)) group.HasRespawned = true;
    }

    public void DrawDebug()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        foreach (var group in _groups.Values.ToArray())
        {
            if (!IsCurrent(group)) continue;
            var context = CreateFormation(group);
            // Native stops the entire debug traversal when the formation or anchor is missing.
            if (context is null) return;
            for (byte index = 0; index < group.MemberCount; index++)
            {
                var position = context.Value.Formation.GetMemberPosition(index);
                if (!IsCurrent(group)) return;
                _services.DrawSphere(position, 1f, 0xa5559955, true);
                if (!IsCurrent(group)) return;
                _services.DrawText(position, 1.3f, $"GROUP RESPAWN({index})");
                if (!IsCurrent(group)) return;
            }
        }
    }

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        foreach (var group in _groups.Values)
        {
            group.IsRetired = true;
            Array.Clear(group.Owners); Array.Clear(group.Pending);
        }
        _groups.Clear(); // 39F76A50 clears storage; it does not emit removals or individually free IDs.
    }
}

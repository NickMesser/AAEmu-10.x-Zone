using System.Numerics;
using AAEmu.Zone.Combat;
using AAEmu.Zone.Content;
using AAEmu.Zone.Protocol;
using AAEmu.Zone.Protocol.Messages;
using AAEmu.Zone.Units;

namespace AAEmu.Zone.Npcs;

public interface IZoneNpcSpawnerLifecycleServices
{
    uint TimeMilliseconds { get; }
    float NextRandomFloat();
    // 39344AA0: if an entity/AI object exists, set its actual AI group to the empty group.
    void ClearActorGroup(ZoneNpc npc);
}

/// <summary>An exact native spawner instance and its transferred scheduler lifetime.</summary>
public sealed class ZoneNpcSpawnerRegistration
{
    internal sealed class Batch(byte member, byte part, ushort table, ZoneNpcGroupRegistration? group,
        int? sourceMember = null, int? sourceTriangle = null)
    {
        internal byte Member { get; } = member;
        internal byte Part { get; } = part;
        internal ushort Table { get; } = table;
        internal ZoneNpcGroupRegistration? Group { get; } = group;
        internal int SourceMember { get; } = sourceMember ?? member;
        internal int SourceTriangle { get; } = sourceTriangle ?? table;
        internal bool PopulationRetired;
        internal object Generation = null!;
    }
    internal sealed class Request(NpcSpawnRequest value, Batch batch, ReadOnlyMemory<byte> body)
    {
        internal NpcSpawnRequest Value { get; } = value;
        internal Batch Batch { get; } = batch;
        internal ReadOnlyMemory<byte> Body { get; } = body;
    }
    internal readonly List<Request> Outstanding = [];
    internal readonly List<Request> RetiredRequests = [];
    internal List<ZoneNpc>[] Members;
    internal List<ZoneNpc>[] Triangles;
    internal int[] MemberCounts, TriangleCounts;
    internal object ContentGeneration { get; private set; } = new();
    internal bool Reinitializing;
    internal bool ContentInitialized = true;
    internal int OccupancyFailures, SuspendedAttempts;
    private ZoneNpcSpawnerType? _content;
    public uint Id { get; }
    public uint SpawnerType => Scheduler.SpawnerType;
    public ZoneNpcSpawnerType Content => _content ?? throw new InvalidOperationException("This spawner has no resolved content after reinitialization.");
    public ZoneNpcSpawnerState Scheduler { get; }
    public int PartCount { get; private set; }
    public int AuthoredTriangleCount { get; private set; }
    public bool UsesTriangles { get; private set; }
    public bool Persistent { get; internal set; }
    public uint OriginalTemplateId { get; private set; }
    public ZoneNpcInitialSpawnContext InitialBehavior { get; private set; }
    public ZoneNpcSpawnerEvents? Events { get; internal set; }
    public bool IsRetired { get; internal set; }
    public int OutstandingRequestCount => Outstanding.Count;
    public int MemberCount => Members.Length;
    public int TriangleCount => Triangles.Length;

    internal ZoneNpcSpawnerRegistration(uint id, ZoneNpcSpawnerType content, ZoneNpcSpawnerState scheduler,
        int partCount, int triangleCount, bool usesTriangles, bool persistent, uint originalTemplate,
        ZoneNpcInitialSpawnContext initialBehavior)
    {
        Id = id; _content = content; Scheduler = scheduler; PartCount = partCount; UsesTriangles = usesTriangles;
        AuthoredTriangleCount = triangleCount;
        Persistent = persistent; OriginalTemplateId = originalTemplate; InitialBehavior = initialBehavior;
        Members = Enumerable.Range(0, content.Members.Count).Select(_ => new List<ZoneNpc>()).ToArray();
        Triangles = Enumerable.Range(0, usesTriangles ? triangleCount : 0).Select(_ => new List<ZoneNpc>()).ToArray();
        MemberCounts = new int[Members.Length]; TriangleCounts = new int[Triangles.Length];
    }

    public int GetMemberPopulation(byte member) => MemberCounts[member];
    public int GetTrianglePopulation(ushort triangle) => TriangleCounts[triangle];
    public int GetMemberPopulation(int sourceMember) => MemberCounts[sourceMember];
    public int GetTrianglePopulation(int sourceTriangle) => TriangleCounts[sourceTriangle];
    public IReadOnlyList<uint> GetMemberNpcIds(byte member) => Members[member].Select(npc => npc.Id).ToArray();
    public IReadOnlyList<uint> GetTriangleNpcIds(ushort triangle) => Triangles[triangle].Select(npc => npc.Id).ToArray();
    internal void SetOriginalTemplateId(uint templateId) => OriginalTemplateId = templateId;

    internal void ReplaceContent(ZoneNpcSpawnerType? content, uint type, int partCount, int triangleCount,
        bool usesTriangles, ZoneNpcInitialSpawnContext behavior)
    {
        ContentGeneration = new();
        Scheduler.ReplaceContent(type);
        _content = content; PartCount = partCount; UsesTriangles = usesTriangles; InitialBehavior = behavior;
        AuthoredTriangleCount = triangleCount;
        Members = Enumerable.Range(0, content?.Members.Count ?? 0).Select(_ => new List<ZoneNpc>()).ToArray();
        //3934A410 reaches runtime triangle construction only after a nonempty member list and
        // only in area mode. Authored triangle vectors still survive the base parameter copy.
        Triangles = Enumerable.Range(0, usesTriangles && Members.Length > 0 ? triangleCount : 0).Select(_ => new List<ZoneNpc>()).ToArray();
        MemberCounts = new int[Members.Length]; TriangleCounts = new int[Triangles.Length];
        ContentInitialized = content is { Members.Count: > 0 } && (!usesTriangles || triangleCount > 0);
    }
}

/// <summary>An unfinished39344530 expansion. Dispose cancels only its exact group and queued requests.</summary>
public sealed class ZoneNpcInitialGroupAdmission : IDisposable
{
    internal ZoneNpcSpawnerRegistry Registry { get; }
    internal ZoneNpcSpawnerRegistration.Batch Batch { get; }
    internal NpcSpawnRequest Seed { get; }
    internal int PreviousMember = -1;
    internal bool Finished;
    public ZoneNpcSpawnerRegistration Owner { get; }
    public ZoneNpcGroupRegistration? Group => Batch.Group;

    internal ZoneNpcInitialGroupAdmission(ZoneNpcSpawnerRegistry registry, ZoneNpcSpawnerRegistration owner,
        ZoneNpcSpawnerRegistration.Batch batch, NpcSpawnRequest seed)
    {
        Registry = registry; Owner = owner; Batch = batch; Seed = seed;
    }

    public void Dispose() => Registry.CancelInitialGroup(this);
}

/// <summary>
/// Concrete instance/member ownership around 39347F90/39344900/39344AA0. Physical selection calls
/// SendInitial/SendInitialGroup after producing actual requests; the scheduler owns its one accepted
/// population increment. Acknowledgments append actors without incrementing population again.
/// </summary>
public sealed class ZoneNpcSpawnerRegistry : IDisposable
{
    private sealed class Binding(ZoneNpcSpawnerRegistration owner, ZoneNpcSpawnerRegistration.Batch batch)
    {
        internal ZoneNpcSpawnerRegistration Owner { get; } = owner;
        internal ZoneNpcSpawnerRegistration.Batch Batch { get; } = batch;
        internal bool Detaching;
        internal List<ZoneNpc>[] Members { get; } = owner.Members;
        internal List<ZoneNpc>[] Triangles { get; } = owner.Triangles;
        internal bool UsesTriangles { get; } = owner.UsesTriangles;
    }
    private readonly ZoneNpcSpawnerContent _content;
    private readonly IZoneNpcSpawnerLifecycleServices _scene;
    private readonly ZoneNpcInitialSpawnBehavior _behavior;
    private readonly Dictionary<uint, ZoneNpcSpawnerRegistration> _registrations = [];
    private readonly HashSet<ZoneNpcSpawnerState> _schedulers = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<ZoneNpcInitialGroupAdmission> _initialGroups = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ZoneNpc, Binding> _owners = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ZoneNpcGroupRegistration, (ZoneNpcSpawnerRegistration Owner, ZoneNpcSpawnerRegistration.Batch Batch)> _groupOwners =
        new(ReferenceEqualityComparer.Instance);
    public ZoneUnitRegistry Registry { get; }
    public ZoneNpcSimulation Npcs { get; }
    public ZoneNpcContent NpcContent { get; }
    public ZoneNpcSpawnerContent Content => _content;
    public ZoneNpcGroups Groups { get; }
    public bool IsDisposed { get; private set; }
    public bool IsShuttingDown { get; private set; }
    public int Count => _registrations.Count;

    public ZoneNpcSpawnerRegistry(ZoneUnitRegistry registry, ZoneNpcContent npcContent, ZoneNpcSpawnerContent spawnerContent,
        IZoneNpcGroupServices groupScene, IZoneNpcSpawnerLifecycleServices scene, ZoneNpcInitialSpawnBehavior initialBehavior)
    {
        Registry = registry ?? throw new ArgumentNullException(nameof(registry));
        Npcs = registry.Npcs ?? throw new ArgumentException("Spawner ownership requires this registry's NPC simulation.", nameof(registry));
        NpcContent = npcContent ?? throw new ArgumentNullException(nameof(npcContent));
        ArgumentNullException.ThrowIfNull(groupScene);
        _content = spawnerContent ?? throw new ArgumentNullException(nameof(spawnerContent));
        _scene = scene ?? throw new ArgumentNullException(nameof(scene));
        _behavior = initialBehavior ?? throw new ArgumentNullException(nameof(initialBehavior));
        ObjectDisposedException.ThrowIf(initialBehavior.IsDisposed, initialBehavior);
        if (!initialBehavior.BelongsTo(Npcs)) throw new ArgumentException("The initial behavior belongs to another NPC scene.", nameof(initialBehavior));
        Groups = new(Npcs, npcContent, new GroupScene(this, groupScene));
    }

    public ZoneNpcSpawnerRegistration Register(uint instanceId, ZoneNpcSpawnerState scheduler, int partCount,
        int triangleCount = 0, bool usesTriangles = false, bool persistent = false, uint originalTemplateId = 0,
        ZoneNpcInitialSpawnContext? initialBehavior = null)
    {
        EnsureAccepting(); ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentOutOfRangeException.ThrowIfZero(instanceId);
        ArgumentOutOfRangeException.ThrowIfNegative(partCount); ArgumentOutOfRangeException.ThrowIfNegative(triangleCount);
        ObjectDisposedException.ThrowIf(scheduler.IsDisposed, scheduler);
        if (!scheduler.BelongsTo(Npcs)) throw new ArgumentException("The scheduler belongs to another NPC scene.", nameof(scheduler));
        if (_schedulers.Contains(scheduler)) throw new ArgumentException("The scheduler already belongs to an instance.", nameof(scheduler));
        if (!_content.Spawners.TryGetValue(scheduler.SpawnerType, out var content) || content.Members.Count == 0)
            throw new InvalidDataException("The spawner requires resolved member content.");
        if (usesTriangles && triangleCount == 0) throw new InvalidDataException("A triangle spawner requires its actual triangulation.");
        var owner = new ZoneNpcSpawnerRegistration(instanceId, content, scheduler, partCount, triangleCount,
            usesTriangles, persistent, originalTemplateId, initialBehavior ?? ZoneNpcInitialSpawnContext.Empty);
        _registrations.Add(instanceId, owner); _schedulers.Add(scheduler);
        return owner;
    }

    public ZoneNpcSpawnerRegistration? Find(uint instanceId) => _registrations.GetValueOrDefault(instanceId);
    public bool IsCurrent(ZoneNpcSpawnerRegistration owner) => !IsDisposed && !owner.IsRetired &&
        _registrations.TryGetValue(owner.Id, out var current) && ReferenceEquals(current, owner);
    private bool IsCurrent(ZoneNpc npc) => !npc.Activation.IsDisposed && ReferenceEquals(Npcs.FindByObjectId(npc.ObjectId), npc);
    public bool IsAccepting(ZoneNpcSpawnerRegistration owner) => CanRetirePopulation(owner) && !owner.Reinitializing && owner.ContentInitialized;
    private bool CanRetirePopulation(ZoneNpcSpawnerRegistration owner) => IsCurrent(owner) && !IsShuttingDown && !Npcs.IsStopped && !owner.Scheduler.IsDisposed;

    internal void RunDeferredAiCommand(ZoneNpc npc, string text, uint parameter, Func<bool> current)
    {
        ArgumentNullException.ThrowIfNull(npc); ArgumentNullException.ThrowIfNull(text); ArgumentNullException.ThrowIfNull(current);
        bool Current() => !IsDisposed && !IsShuttingDown && !Npcs.IsStopped && IsCurrent(npc);
        if (Current() && current() && Current())
            _behavior.RunDeferredAiCommand(npc, text, parameter, () => Current() && current() && Current());
    }

    /// <summary>39349DC0 stores the saved NPC template before event3, without validating its content ID.</summary>
    public bool TrySetOriginalTemplateId(ZoneNpcSpawnerRegistration owner, uint templateId)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!IsAccepting(owner) || !owner.Persistent) return false;
        owner.SetOriginalTemplateId(templateId);
        return true;
    }

    public bool AttachEvents(ZoneNpcSpawnerRegistration owner, ZoneNpcSpawnerEvents events)
    {
        ArgumentNullException.ThrowIfNull(owner); ArgumentNullException.ThrowIfNull(events);
        if (!ReferenceEquals(events.Owner, owner)) throw new ArgumentException("Events belong to another spawner instance.", nameof(events));
        if (!IsAccepting(owner)) return false;
        if (owner.Events is not null) throw new InvalidOperationException("This spawner already owns its event handler.");
        owner.Events = events;
        return true;
    }

    public bool HandleEvent(NpcSpawnerEventMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var owner = Find(message.SpawnerId);
        if (owner is null || !IsAccepting(owner)) return false;
        return (owner.Events ?? throw new NotSupportedException("Spawner events require this instance's configured event handler.")).Handle(message);
    }

    /// <summary>39349DC0 visits the retained World snapshot in source order after local Zone loading.</summary>
    public int ApplyPersistentSpawners(IReadOnlyList<PersistentSpawner> saved)
    {
        ArgumentNullException.ThrowIfNull(saved);
        var applied = 0;
        foreach (var entry in saved)
        {
            var owner = Find(entry.Id);
            if (owner is null || !IsAccepting(owner) || !owner.Persistent) continue;
            var events = owner.Events ?? throw new NotSupportedException("Persistent restoration requires the spawner's actual event handler.");
            if (events.Restore(entry)) applied++;
        }
        return applied;
    }
    private void EnsureAccepting()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (IsShuttingDown || Npcs.IsStopped) throw new InvalidOperationException("Spawner ownership is shutting down.");
    }

    /// <summary>Retire local instance ownership; physical NPC removal remains the caller's explicit normal path.</summary>
    public bool Remove(ZoneNpcSpawnerRegistration owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!IsCurrent(owner)) return false;
        owner.IsRetired = true; owner.Events = null; _registrations.Remove(owner.Id); _schedulers.Remove(owner.Scheduler);
        owner.Outstanding.Clear(); owner.RetiredRequests.Clear(); owner.Scheduler.Dispose(); Registry.DiscardNotifications(owner);
        // Retired instances retain no member lists. Exact live bindings alone keep their owner
        // until normal NPC cleanup; historical registrations need no registry-wide retention.
        foreach (var members in owner.Members) members.Clear();
        foreach (var members in owner.Triangles) members.Clear();
        List<Exception>? errors = null;
        foreach (var admission in _initialGroups.Where(value => ReferenceEquals(value.Owner, owner)).ToArray())
        {
            try { CancelInitialGroup(admission); }
            catch (Exception error) { (errors ??= []).Add(error); }
        }
        foreach (var group in _groupOwners.Where(pair => ReferenceEquals(pair.Value.Owner, owner)).Select(pair => pair.Key).ToArray())
        {
            _groupOwners.Remove(group);
            try { Groups.Retire(group); }
            catch (Exception error) { (errors ??= []).Add(error); }
        }
        if (errors is not null) throw new AggregateException("Spawner group retirement failed.", errors);
        return true;
    }

    /// <summary>3934A780 storage boundary, after actual physical removals have run.</summary>
    internal bool ReplaceContent(ZoneNpcSpawnerRegistration owner, ZoneNpcSpawnerType? content, uint type,
        int partCount, int triangleCount, bool usesTriangles, ZoneNpcInitialSpawnContext behavior)
    {
        if (!CanRetirePopulation(owner) || !owner.Reinitializing) return false;
        foreach (var request in owner.Outstanding)
            if (!Registry.HasQueuedNotification(owner, request.Body)) owner.RetiredRequests.Add(request);
        owner.Outstanding.Clear(); Registry.DiscardNotifications(owner);
        owner.Scheduler.ClearPending();
        foreach (var members in owner.Members) members.Clear();
        foreach (var members in owner.Triangles) members.Clear();
        owner.ReplaceContent(content, type, partCount, triangleCount, usesTriangles, behavior);
        foreach (var admission in _initialGroups.Where(value => ReferenceEquals(value.Owner, owner)).ToArray())
            CancelInitialGroup(admission);
        foreach (var group in _groupOwners.Where(pair => ReferenceEquals(pair.Value.Owner, owner)).Select(pair => pair.Key).ToArray())
        {
            _groupOwners.Remove(group);
            Groups.Retire(group);
            if (!CanRetirePopulation(owner)) return false;
        }
        return CanRetirePopulation(owner);
    }

    public bool SendInitial(ZoneNpcSpawnerRegistration owner, NpcSpawnRequest request,
        int? sourceMemberIndex = null, int? sourceTriangleIndex = null)
    {
        ArgumentNullException.ThrowIfNull(owner); ArgumentNullException.ThrowIfNull(request);
        if (!IsAccepting(owner)) return false;
        var source = ValidateRequest(owner, request, grouped: false, sourceMemberIndex, sourceTriangleIndex);
        var frame = request.ToFrame();
        var batch = new ZoneNpcSpawnerRegistration.Batch(request.MemberIndex, request.PartIndex, request.TableIndex, null, source.Member, source.Triangle);
        CountAdmission(owner, batch);
        owner.Outstanding.Add(new(request, batch, frame.Body));
        Registry.QueueNotification(frame, owner);
        return true;
    }

    public ZoneNpcGroupRegistration? SendInitialGroup(ZoneNpcSpawnerRegistration owner, uint allocatedGroupId,
        IReadOnlyList<NpcSpawnRequest> requests, int? sourceMemberIndex = null, int? sourceTriangleIndex = null)
    {
        ArgumentNullException.ThrowIfNull(owner); ArgumentNullException.ThrowIfNull(requests);
        if (!IsAccepting(owner)) return null;
        if (requests.Count == 0) throw new ArgumentException("Initial group spawning requires an actual selected member.", nameof(requests));
        var first = requests[0]; var frames = new List<ZoneFrame>(requests.Count);
        var source = ValidateRequest(owner, first, grouped: true, sourceMemberIndex, sourceTriangleIndex);
        var previous = -1;
        foreach (var request in requests)
        {
            ValidateRequest(owner, request, grouped: true, source.Member, source.Triangle);
            if (request.Group.Id != allocatedGroupId || request.Group.Type != first.Group.Type ||
                request.MemberIndex != first.MemberIndex || request.PartIndex != first.PartIndex || request.TableIndex != first.TableIndex ||
                request.Group.MemberIndex <= previous)
                throw new InvalidDataException("Group requests must describe one ordered physical spawn selection.");
            previous = request.Group.MemberIndex; frames.Add(request.ToFrame());
        }
        var definition = owner.Content.Definition;
        var group = Groups.Register(allocatedGroupId, first.Group.Type, definition.SpawnDelayMinimumSeconds,
            definition.SpawnDelayMaximumSeconds - definition.SpawnDelayMinimumSeconds);
        var batch = new ZoneNpcSpawnerRegistration.Batch(first.MemberIndex, first.PartIndex, first.TableIndex, group, source.Member, source.Triangle);
        _groupOwners.Add(group, (owner, batch)); CountAdmission(owner, batch);
        for (var index = 0; index < requests.Count; index++)
        {
            var request = requests[index];
            Groups.SaveRequest(group, request.Group.MemberIndex, request);
            owner.Outstanding.Add(new(request, batch, frames[index].Body));
            Registry.QueueNotification(frames[index], owner);
        }
        return group;
    }

    /// <summary>
    /// Register the native full mask before per-member formation callbacks. A positive allocation
    /// transfers to this registry only when a nonnull handle returns; ID0 deliberately has no group.
    /// Population admission waits until the complete second pass, including an empty successful pass.
    /// </summary>
    public ZoneNpcInitialGroupAdmission? BeginInitialGroup(ZoneNpcSpawnerRegistration owner, uint allocatedGroupId,
        NpcSpawnRequest seed, int sourceMemberIndex, int sourceTriangleIndex)
    {
        ArgumentNullException.ThrowIfNull(owner); ArgumentNullException.ThrowIfNull(seed);
        if (!IsAccepting(owner)) return null;
        var source = ValidatePlacement(owner, seed, grouped: true, sourceMemberIndex, sourceTriangleIndex);
        var content = owner.Content.Members[source.Member].Group;
        if (content is null || content.Definition.Id != seed.Group.Type || seed.Group.Id != allocatedGroupId ||
            seed.Group.MemberIndex != 0 || seed.TemplateId != 0)
            throw new InvalidDataException("The group seed does not match its spawner's selected group content.");
        seed.ToFrame();
        var definition = owner.Content.Definition;
        var group = allocatedGroupId == 0 ? null : Groups.Register(allocatedGroupId, seed.Group.Type,
            definition.SpawnDelayMinimumSeconds, definition.SpawnDelayMaximumSeconds - definition.SpawnDelayMinimumSeconds);
        var batch = new ZoneNpcSpawnerRegistration.Batch(seed.MemberIndex, seed.PartIndex, seed.TableIndex, group, source.Member, source.Triangle);
        batch.Generation = owner.ContentGeneration;
        var admission = new ZoneNpcInitialGroupAdmission(this, owner, batch, seed);
        if (group is not null) _groupOwners.Add(group, (owner, batch));
        _initialGroups.Add(admission);
        return admission;
    }

    public bool AppendInitialGroup(ZoneNpcInitialGroupAdmission admission, NpcSpawnRequest request)
    {
        ArgumentNullException.ThrowIfNull(admission); ArgumentNullException.ThrowIfNull(request);
        if (!IsAccepting(admission)) return false;
        var seed = admission.Seed;
        ValidateRequest(admission.Owner, request, grouped: true, admission.Batch.SourceMember,
            admission.Batch.SourceTriangle, allowInvalidGroupId: seed.Group.Id == 0);
        if (request.Group.MemberIndex <= admission.PreviousMember ||
            (request with { TemplateId = seed.TemplateId, Position = seed.Position, Group = seed.Group }) != seed ||
            request.Group.Id != seed.Group.Id || request.Group.Type != seed.Group.Type)
            throw new InvalidDataException("Group expansion must preserve its seed and visit source members in order.");
        var frame = request.ToFrame();
        admission.PreviousMember = request.Group.MemberIndex;
        //39344530 continues formation/filter work when allocation failed or group storage vanished.
        // An ID reused by another admission never inherits the old managed admission's requests.
        if (admission.Group is not { } group || group.IsRetired || !ReferenceEquals(Groups.Find(group.Id), group)) return true;
        if (!Groups.SaveRequest(group, request.Group.MemberIndex, request)) return true;
        admission.Owner.Outstanding.Add(new(request, admission.Batch, frame.Body));
        return Registry.QueueNotification(frame, admission.Owner, admission);
    }

    public bool CompleteInitialGroup(ZoneNpcInitialGroupAdmission admission)
    {
        ArgumentNullException.ThrowIfNull(admission);
        if (!IsAccepting(admission)) return false;
        admission.Finished = true; _initialGroups.Remove(admission);
        CountAdmission(admission.Owner, admission.Batch);
        // An empty-success expansion can outlive its group storage. Only actual outstanding
        // requests or NPC bindings may retain that retired batch; a replacement ID owns itself.
        if (admission.Group is { } group && (group.IsRetired || !ReferenceEquals(Groups.Find(group.Id), group)))
            _groupOwners.Remove(group);
        return true;
    }

    private bool IsAccepting(ZoneNpcInitialGroupAdmission admission)
    {
        if (!ReferenceEquals(admission.Registry, this)) throw new ArgumentException("The group admission belongs to another registry.", nameof(admission));
        return !admission.Finished && _initialGroups.Contains(admission) && IsAccepting(admission.Owner);
    }

    internal void CancelInitialGroup(ZoneNpcInitialGroupAdmission admission)
    {
        if (admission.Finished || !_initialGroups.Remove(admission)) return;
        admission.Finished = true; admission.Batch.PopulationRetired = true;
        admission.Owner.Outstanding.RemoveAll(value => ReferenceEquals(value.Batch, admission.Batch));
        Registry.DiscardNotifications(admission.Owner, admission);
        if (admission.Group is { } group)
        {
            _groupOwners.Remove(group);
            Groups.Retire(group);
        }
    }

    private static void CountAdmission(ZoneNpcSpawnerRegistration owner, ZoneNpcSpawnerRegistration.Batch batch)
    {
        batch.Generation = owner.ContentGeneration;
        //39349672 increments the full selected source indices. Later ack/failure/removal
        // use the native narrowed wire indices; these intentionally need not be equal.
        owner.MemberCounts[batch.SourceMember] = unchecked(owner.MemberCounts[batch.SourceMember] + 1);
        if (owner.UsesTriangles) owner.TriangleCounts[batch.SourceTriangle] = unchecked(owner.TriangleCounts[batch.SourceTriangle] + 1);
    }

    private static bool ValidIndices(ZoneNpcSpawnerRegistration owner, byte member, byte part, ushort table, bool failure) =>
        member < owner.MemberCount && (!failure || part < owner.PartCount) && (!owner.UsesTriangles || table < owner.TriangleCount);

    private static bool MatchesContent(ZoneNpcSpawnerRegistration owner, int member, uint templateId, NpcSpawnGroup group,
        bool allowInvalidGroupId = false)
    {
        if ((uint)member >= owner.MemberCount) return false;
        var content = owner.Content.Members[member];
        if (content.Npc is { } npc) return group.Type == 0 && group.Id == 0 && templateId == npc.Id;
        return content.Group is { } definition && group.Type == definition.Definition.Id && (group.Id != 0 || allowInvalidGroupId) &&
            group.MemberIndex < definition.Members.Count && templateId == definition.Members[group.MemberIndex].NpcTemplateId;
    }

    private static (int Member, int Triangle) ValidateRequest(ZoneNpcSpawnerRegistration owner, NpcSpawnRequest request, bool grouped,
        int? sourceMemberIndex = null, int? sourceTriangleIndex = null, bool allowInvalidGroupId = false)
    {
        var source = ValidatePlacement(owner, request, grouped, sourceMemberIndex, sourceTriangleIndex);
        if (!MatchesContent(owner, source.Member, request.TemplateId, request.Group, allowInvalidGroupId) || (request.Group.Type != 0) != grouped)
            throw new InvalidDataException("The NPC request does not match its spawner's content and placement ownership.");
        return source;
    }

    private static (int Member, int Triangle) ValidatePlacement(ZoneNpcSpawnerRegistration owner, NpcSpawnRequest request, bool grouped,
        int? sourceMemberIndex, int? sourceTriangleIndex)
    {
        var member = sourceMemberIndex ?? request.MemberIndex;
        var triangle = sourceTriangleIndex ?? request.TableIndex;
        if (request.SpawnerId != owner.Id || request.SpawnerType != owner.SpawnerType ||
            !ValidIndices(owner, request.MemberIndex, request.PartIndex, request.TableIndex, false) ||
            //39348AF0 area sends Part0 without reading authored points.39344900 failure
            // retains its separate unconditional point-count gate in ValidIndices.
            (request.PartIndex >= owner.PartCount && !(owner.UsesTriangles && request.PartIndex == 0)) ||
            (uint)member >= owner.MemberCount || unchecked((byte)member) != request.MemberIndex ||
            (owner.UsesTriangles ? (uint)triangle >= owner.TriangleCount : triangle != 0) ||
            (grouped ? unchecked((ushort)(byte)triangle) : unchecked((ushort)triangle)) != request.TableIndex)
            throw new InvalidDataException("The NPC request does not match its spawner's content and placement ownership.");
        return (member, triangle);
    }

    /// <summary>
    /// After actual actor initialization, before spatial admission. Native accepts valid World replay
    /// without pending requests; replay neither invents saved requests nor increments population.
    /// The wire lacks a request generation: identical outstanding requests are consumed in FIFO order.
    /// </summary>
    public bool Acknowledge(ZoneNpcSpawnerRegistration owner, ZoneNpc npc)
    {
        ArgumentNullException.ThrowIfNull(owner); ArgumentNullException.ThrowIfNull(npc);
        if (IsCurrent(owner) && IsCurrent(npc) && !_owners.ContainsKey(npc) &&
            owner.RetiredRequests.FirstOrDefault(request => Matches(npc, request.Value)) is { } retired)
        { owner.RetiredRequests.Remove(retired); return false; }
        if (!IsAccepting(owner) || !IsCurrent(npc) || _owners.ContainsKey(npc) || npc.SpawnerId != owner.Id ||
            !ValidIndices(owner, npc.Spawn.MemberIndex, npc.Spawn.PartIndex, npc.Spawn.TableIndex, false)) return false;
        var generation = owner.ContentGeneration;
        var pending = owner.Outstanding.FirstOrDefault(request => Matches(npc, request.Value));
        if (!MatchesContent(owner, pending?.Batch.SourceMember ?? npc.Spawn.MemberIndex, npc.TemplateId, npc.Spawn.Group)) return false;
        var group = pending?.Batch.Group ?? (npc.Spawn.Group.Id == 0 ? null : Groups.Find(npc.Spawn.Group.Id));
        ZoneNpcSpawnerRegistration.Batch? existingBatch = null;
        if (group is not null && _groupOwners.TryGetValue(group, out var existingOwner))
        {
            existingBatch = existingOwner.Batch;
            if (!ReferenceEquals(existingOwner.Owner, owner) || existingBatch.Member != npc.Spawn.MemberIndex ||
                existingBatch.Part != npc.Spawn.PartIndex || existingBatch.Table != npc.Spawn.TableIndex) return false;
        }
        var groupAck = ZoneNpcGroupAcknowledgement.InitialSpawn;
        if (group is not null)
        {
            groupAck = Groups.Acknowledge(group, npc);
            if (!IsAccepting(owner) || !IsCurrent(npc) || !ReferenceEquals(generation, owner.ContentGeneration)) return false;
            if (groupAck == ZoneNpcGroupAcknowledgement.Ignored) groupAck = Groups.AcknowledgeReplay(group, npc);
            if (!IsAccepting(owner) || !IsCurrent(npc) || !ReferenceEquals(generation, owner.ContentGeneration)) return false;
            if (groupAck == ZoneNpcGroupAcknowledgement.Ignored) return false;
        }
        var batch = pending?.Batch;
        batch ??= existingBatch;
        batch ??= new(npc.Spawn.MemberIndex, npc.Spawn.PartIndex, npc.Spawn.TableIndex, group);
        batch.Generation ??= owner.ContentGeneration;
        if (group is not null) _groupOwners.TryAdd(group, (owner, batch));
        if (pending is not null) owner.Outstanding.Remove(pending);
        var binding = new Binding(owner, batch); _owners.Add(npc, binding);
        owner.Members[npc.Spawn.MemberIndex].Add(npc);
        if (owner.UsesTriangles) owner.Triangles[npc.Spawn.TableIndex].Add(npc);
        if (groupAck != ZoneNpcGroupAcknowledgement.Respawn)
            _behavior.Apply(owner.Scheduler, npc, owner.InitialBehavior, () => IsAccepting(owner) && ReferenceEquals(batch.Generation, owner.ContentGeneration) && IsCurrent(npc) && !binding.Detaching);
        if (!IsAccepting(owner) || !ReferenceEquals(batch.Generation, owner.ContentGeneration) || !IsCurrent(npc) || !_owners.TryGetValue(npc, out var current) || !ReferenceEquals(binding, current)) return true;
        if (owner.Persistent)
            Registry.QueueNotification(new SaveInstanceSpawner(owner.Id, npc.Id, owner.OriginalTemplateId != npc.TemplateId).ToFrame(), owner);
        return true;
    }

    private static bool Matches(ZoneNpc npc, NpcSpawnRequest request) => npc.TemplateId == request.TemplateId &&
        (npc.Spawn with { SpawningEffectTime = 0 }) == NpcStateMetadata.FromRequest(request);

    public bool SpawnFailed(NpcSpawnRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var owner = Find(request.SpawnerId);
        var body = request.ToFrame().Body;
        if (owner is not null && owner.RetiredRequests.FirstOrDefault(item => item.Body.Span.SequenceEqual(body.Span)) is { } retired)
        { owner.RetiredRequests.Remove(retired); return false; }
        if (owner is null || !IsAccepting(owner) || !ValidIndices(owner, request.MemberIndex, request.PartIndex, request.TableIndex, true)) return false;
        // Replacement duplicate policy: native has no request tokens, so consume the first exact
        // current body. A byte-identical stale packet cannot be distinguished after identity reuse.
        var pending = owner.Outstanding.FirstOrDefault(item => item.Body.Span.SequenceEqual(body.Span));
        if (pending is null) return false;
        owner.Outstanding.Remove(pending);
        var result = pending.Batch.Group is { } group ? Groups.SpawnFailed(group, request) : ZoneNpcGroupRemoval.GroupRetired;
        if (result == ZoneNpcGroupRemoval.GroupRetired && IsAccepting(owner)) CompletePopulation(owner, pending.Batch, failed: true);
        return true;
    }

    public bool Detach(ZoneNpc npc)
    {
        ArgumentNullException.ThrowIfNull(npc);
        if (!_owners.TryGetValue(npc, out var binding) || binding.Detaching) return false;
        binding.Detaching = true;
        var owner = binding.Owner; var batch = binding.Batch;
        var errors = new List<Exception>();
        var result = ZoneNpcGroupRemoval.GroupRetired;
        try
        {
            if (!IsShuttingDown && !Npcs.IsStopped && batch.Group is { } group)
                Cleanup(() => result = Groups.RemoveMember(group, npc));
            Cleanup(() => _scene.ClearActorGroup(npc));
            binding.Members[npc.Spawn.MemberIndex].Remove(npc);
            if (binding.UsesTriangles) binding.Triangles[npc.Spawn.TableIndex].Remove(npc);
            _owners.Remove(npc);
            Cleanup(() => _behavior.Remove(npc));
            if (result == ZoneNpcGroupRemoval.GroupRetired && CanRetirePopulation(owner))
                Cleanup(() => CompletePopulation(owner, batch, failed: false));
        }
        finally { _owners.Remove(npc); }
        if (errors.Count == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(errors[0]).Throw();
        if (errors.Count > 1) throw new AggregateException("Spawner member cleanup failed.", errors);
        return true;

        void Cleanup(Action action) { try { action(); } catch (Exception error) { errors.Add(error); } }
    }

    private void CompletePopulation(ZoneNpcSpawnerRegistration owner, ZoneNpcSpawnerRegistration.Batch batch, bool failed)
    {
        // Group/actor callbacks can retire the scheduler while keeping the NPC available for
        // normal cleanup. Retire no counters after that managed ownership boundary.
        if (batch.PopulationRetired || !CanRetirePopulation(owner) || !ReferenceEquals(batch.Generation, owner.ContentGeneration)) return;
        batch.PopulationRetired = true;
        owner.MemberCounts[batch.Member] = unchecked(owner.MemberCounts[batch.Member] - 1);
        if (owner.UsesTriangles) owner.TriangleCounts[batch.Table] = unchecked(owner.TriangleCounts[batch.Table] - 1);
        owner.Scheduler.RecordPopulationRemoved();
        if (batch.Group is { } group) _groupOwners.Remove(group);
        if (owner.Persistent && !failed) { owner.Scheduler.Active = false; return; }
        // 393438D0 samples random before the clock; the sorted queue's second word is always1000.
        var definition = owner.Content.Definition;
        var random = _scene.NextRandomFloat();
        if (!CanRetirePopulation(owner) || !ReferenceEquals(batch.Generation, owner.ContentGeneration)) return;
        var milliseconds = (random * (definition.SpawnDelayMaximumSeconds - definition.SpawnDelayMinimumSeconds) +
            definition.SpawnDelayMinimumSeconds) * 1000f;
        var now = _scene.TimeMilliseconds;
        if (CanRetirePopulation(owner) && ReferenceEquals(batch.Generation, owner.ContentGeneration))
            owner.Scheduler.QueueSpawn(unchecked(now + (uint)ZoneStatMath.ToInt64(milliseconds)), 1000);
    }

    private void SendGroupFrame(ZoneFrame frame)
    {
        var request = NpcSpawnRequest.Read(frame.Body.Span);
        var group = Groups.Find(request.Group.Id);
        if (group is null || !_groupOwners.TryGetValue(group, out var binding) || !IsAccepting(binding.Owner))
            throw new InvalidOperationException("Group respawn requires restored saved requests and a current spawner owner.");
        ValidateRequest(binding.Owner, request, grouped: true, binding.Batch.SourceMember, binding.Batch.SourceTriangle);
        binding.Owner.Outstanding.Add(new(request, binding.Batch, frame.Body));
        Registry.QueueNotification(frame, binding.Owner);
    }

    public void BeginShutdown()
    {
        if (IsShuttingDown) return;
        IsShuttingDown = true;
        Groups.Dispose();
        foreach (var admission in _initialGroups.ToArray()) CancelInitialGroup(admission);
        foreach (var owner in _registrations.Values)
        {
            owner.Scheduler.Dispose();
            Registry.DiscardNotifications(owner);
        }
    }

    public void Dispose()
    {
        if (IsDisposed) return;
        BeginShutdown(); IsDisposed = true;
        foreach (var owner in _registrations.Values)
        {
            owner.IsRetired = true; owner.Events = null; owner.Outstanding.Clear(); owner.RetiredRequests.Clear(); owner.Scheduler.Dispose();
            foreach (var members in owner.Members) members.Clear();
            foreach (var members in owner.Triangles) members.Clear();
        }
        Groups.Dispose(); _behavior.Dispose();
        _owners.Clear(); _groupOwners.Clear(); _registrations.Clear(); _schedulers.Clear();
    }

    private sealed class GroupScene(ZoneNpcSpawnerRegistry owner, IZoneNpcGroupServices scene) : IZoneNpcGroupServices
    {
        public uint TimeMilliseconds => scene.TimeMilliseconds;
        public float NextRandomFloat() => scene.NextRandomFloat();
        public int? GetPuppetAlertness(ZoneNpc npc) => scene.GetPuppetAlertness(npc);
        public byte? GetFormationAnchorMember(uint groupId) => scene.GetFormationAnchorMember(groupId);
        public Quaternion GetRotation(ZoneNpc npc) => scene.GetRotation(npc);
        public Vector3 GetPosition(ZoneNpc npc) => scene.GetPosition(npc);
        public IZoneNpcGroupFormation CreateFormation(uint type, Vector3 position, float yaw, byte anchor) => scene.CreateFormation(type, position, yaw, anchor);
        public void Send(ZoneFrame frame) => owner.SendGroupFrame(frame);
        public void FreeGroupId(uint id) => scene.FreeGroupId(id);
        public void DrawSphere(Vector3 position, float radius, uint color, bool shaded) => scene.DrawSphere(position, radius, color, shaded);
        public void DrawText(Vector3 position, float scale, string text) => scene.DrawText(position, scale, text);
    }
}

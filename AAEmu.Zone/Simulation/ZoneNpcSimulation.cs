using System.Collections.ObjectModel;
using System.Numerics;
using AAEmu.Zone.Content;
using AAEmu.Zone.Protocol;
using AAEmu.Zone.Protocol.Messages;
using AAEmu.Zone.Protocol.Units;

namespace AAEmu.Zone.Simulation;

/// <summary>Connection-owned NPC identities, reverse threat membership and native packet routing.</summary>
public sealed class ZoneNpcSimulation : IZoneAggroWorld
{
    private readonly ZoneUnitRegistry _units;
    private readonly ZoneStatContent _content;
    private readonly ZoneNpcContent _npcContent;
    private readonly ZoneStatCalculator _stats;
    private readonly IZoneNpcWorld _world;
    private readonly Dictionary<uint, ZoneNpc> _npcs = [];
    private readonly Dictionary<uint, SortedSet<uint>> _reverse = [];
    private readonly Dictionary<ZoneUnit, (ZoneVitalStats? Stamp, Dictionary<uint, long> Values)> _attributes = [];
    private bool _synchronizingGroup;
    private byte _nextUpdatePhase;
    public IReadOnlyDictionary<uint, ZoneNpc> Npcs { get; }
    public ZoneNpcDiagnostics Diagnostics { get; }
    public ZoneNpcSpawnerRegistry? Spawners { get; private set; }
    public bool IsStopped { get; private set; }

    internal ZoneNpcSimulation(ZoneUnitRegistry units, ZoneStatContent content, IZoneNpcWorld world, ZoneNpcContent npcContent)
    {
        _units = units; _content = content; _stats = new(content); _world = world; _npcContent = npcContent;
        Npcs = new ReadOnlyDictionary<uint, ZoneNpc>(_npcs);
        Diagnostics = new(this);
    }

    /// <summary>Attach the scene's concrete spawner registry once, transferring its shutdown lifetime.</summary>
    public void AttachSpawners(ZoneNpcSpawnerRegistry spawners)
    {
        EnsureActive();
        ArgumentNullException.ThrowIfNull(spawners);
        ObjectDisposedException.ThrowIf(spawners.IsDisposed, spawners);
        if (spawners.IsShuttingDown)
            throw new InvalidOperationException("A shutting-down spawner registry cannot accept scene ownership.");
        if (!ReferenceEquals(spawners.Registry, _units))
            throw new ArgumentException("The spawner registry belongs to another NPC scene.", nameof(spawners));
        if (Spawners is not null)
            throw new InvalidOperationException("This NPC scene already owns its spawner registry.");
        Spawners = spawners;
    }

    public ZoneNpc? FindByObjectId(uint objectId) => _units.Units.TryGetValue(objectId, out var unit) &&
        unit.Snapshot.Identity.Kind == ZoneUnitKind.Npc && _npcs.TryGetValue(unit.Snapshot.Identity.NpcObjectId, out var npc) &&
        ReferenceEquals(npc.Unit, unit) ? npc : null;

    public IReadOnlyList<uint> GetNpcIdsTargeting(uint objectId) =>
        _reverse.TryGetValue(objectId, out var ids) ? ids.ToArray() : [];

    public bool TryDequeue(out ZoneFrame frame) => _units.TryDequeueNotification(out frame);

    public NpcCreationResult Create(ReadOnlySpan<byte> body)
    {
        EnsureActive();
        var snapshot = NpcStateSnapshot.Read(body);
        // Replacement ownership policy: decode first, then preserve either existing identity.
        // Native NPC creation can overwrite the unit array; that does not make duplicate ownership valid.
        if (_units.Units.ContainsKey(snapshot.Unit.ObjectId) || _npcs.ContainsKey(snapshot.Unit.Identity.NpcObjectId))
            return NpcCreationResult.Duplicate;
        ZoneNpcSpawnerRegistration? spawner = null;
        if (snapshot.Metadata.SpawnerId != 0)
        {
            spawner = RequireSpawners().Find(snapshot.Metadata.SpawnerId);
            if (spawner is null || !RequireSpawners().IsAccepting(spawner)) return NpcCreationResult.UnknownSpawner;
        }
        if (!_content.Npcs.ContainsKey(snapshot.Unit.Identity.TemplateId) ||
            !_npcContent.Templates.TryGetValue(snapshot.Unit.Identity.TemplateId, out var template)) return NpcCreationResult.UnknownTemplate;
        var model = _npcContent.ModelForNpc(template.Id) ?? throw new NotSupportedException(
            $"NPC template {template.Id} requires its configured model definition {template.ModelId}.");
        _world.ValidateCreation(snapshot);
        EnsureActive();
        // Validation may synchronously publish either identity for another owner.
        if (_units.Units.ContainsKey(snapshot.Unit.ObjectId) || _npcs.ContainsKey(snapshot.Unit.Identity.NpcObjectId))
            return NpcCreationResult.Duplicate;
        if (spawner is not null && !RequireSpawners().IsAccepting(spawner))
            return NpcCreationResult.UnknownSpawner;
        var unit = _units.Prepare(snapshot.Unit);
        var creator = ResolveCreator(snapshot.Metadata.Creator);
        // Managed allocation policy: distribute owners across phases instead of using a native pointer byte.
        var npc = new ZoneNpc(unit, snapshot.Metadata, creator?.Snapshot.ObjectId ?? 0, this, template, model, _nextUpdatePhase,
            _world, id => _units.Units.GetValueOrDefault(id), frame => { if (!IsStopped) _units.QueueNotification(frame, this); }, _npcContent.Signals, Diagnostics);
        _nextUpdatePhase = unchecked((byte)(_nextUpdatePhase + 1));
        var published = false;
        try
        {
            EnsureActive();
            if (_units.Units.ContainsKey(unit.Snapshot.ObjectId) || _npcs.ContainsKey(npc.Id))
                return NpcCreationResult.Duplicate;
            if (spawner is not null && !RequireSpawners().IsAccepting(spawner))
                return NpcCreationResult.UnknownSpawner;
            _units.AddPrepared(unit);
            // Scheduler allocation/clock callbacks can publish the same NPC ID under another
            // unit ID. Keep that owner and retire only our still-uninitialized unit registration.
            if (!ReferenceEquals(_units.Units.GetValueOrDefault(unit.Snapshot.ObjectId), unit) || _npcs.ContainsKey(npc.Id))
                return NpcCreationResult.Duplicate;
            if (spawner is not null && !RequireSpawners().IsAccepting(spawner))
                return NpcCreationResult.UnknownSpawner;
            // 39345000 schedules removal during construction, before the NPC owner is registered.
            // No scene callback occurs between these final identity checks and publication.
            if (snapshot.Metadata.LifeTime > 0.001f)
                _units.QueueNotification(new NpcRemoval(npc.Id, RemovalDelay(snapshot.Metadata.LifeTime)).ToFrame(), this);
            _npcs.Add(npc.Id, npc);
            published = true;
        }
        finally
        {
            if (!published)
            {
                _units.RemovePrepared(unit); // Exact reference check preserves a nested same-ID owner.
                npc.Activation.Dispose();
            }
        }
        try
        {
            _world.Initialize(npc, creator);
            // 39348440 calls 39347F90 after actor initialization, before spatial admission.
            // Retiring a required spawner during initialization follows the existing fatal
            // initialization policy; callback removal of the NPC itself ends admission normally.
            if (IsCurrent(npc) && spawner is not null)
            {
                if (!RequireSpawners().IsAccepting(spawner))
                    throw new InvalidOperationException("The NPC spawner stopped accepting during initialization.");
                RequireSpawners().Acknowledge(spawner, npc);
                if (IsCurrent(npc) && !RequireSpawners().IsAccepting(spawner))
                    throw new InvalidOperationException("The NPC spawner stopped accepting during spawn acknowledgement.");
            }
            // 39348440 publishes spatial membership after actor initialization and spawner placement.
            if (IsCurrent(npc)) _units.RegisterSpatial(unit);
        }
        catch (Exception creationError)
        {
            // Actor initialization is a session-fatal failure. Release every owned actor rather
            // than leaving a partly initialized owner available to subsequent packets.
            try { Stop(); }
            catch (Exception cleanupError) { throw new AggregateException(creationError, cleanupError); }
            throw;
        }
        return NpcCreationResult.Created;
    }

    public void SpawnFailed(ReadOnlySpan<byte> body)
    {
        EnsureActive();
        var request = NpcSpawnRequest.Read(body);
        if (request.SpawnerId == 0) return; // Failed direct/summoned requests have no ordinary spawner owner.
        RequireSpawners().SpawnFailed(request);
    }

    private ZoneNpcSpawnerRegistry RequireSpawners() => Spawners ?? throw new NotSupportedException(
        "Spawner-owned NPC operations require the scene's configured spawner registry.");

    public void SpawnerEvent(ReadOnlySpan<byte> body)
    {
        EnsureActive();
        var message = NpcSpawnerEventMessage.Read(body);
        RequireSpawners().HandleEvent(message);
    }

    public bool UpdateAggro(ReadOnlySpan<byte> body)
    {
        EnsureActive();
        var message = AggroUpdate.Read(body);
        if (!_units.Units.TryGetValue(message.SkillTargetId, out var target) ||
            !_units.Units.TryGetValue(message.UnitInChargeId, out var charge)) return false;
        _units.Units.TryGetValue(message.SourceId, out var source);
        // 39322500 and 39321830 both put WZUpdateAggro's amount in component 2 (direct).
        if (message.Hostile)
        {
            if (FindByObjectId(message.SkillTargetId) is { } npc) Route(npc, source, charge, message.Amount);
            if (!ReferenceEquals(target, charge) && !ReferenceEquals(target, source))
                foreach (var id in _world.GetLinkedAggroNpcIds(target, source, charge))
                    if (_npcs.TryGetValue(id, out var linked)) Route(linked, source ?? charge, charge, 1);
        }
        else
        {
            // Native copies the ordered set: additions/removals during routing cannot change this traversal.
            foreach (var id in GetNpcIdsTargeting(message.SkillTargetId))
                if (_npcs.TryGetValue(id, out var npc)) Route(npc, source, charge, message.Amount);
        }
        return true;
    }

    public bool ResetAggro(ReadOnlySpan<byte> body)
    {
        EnsureActive(); var message = AggroReset.Read(body);
        if (!_npcs.TryGetValue(message.NpcId, out var npc)) return false;
        npc.Aggro.Reset(message.DamageSelector, message.HealingSelector, message.DirectSelector, message.Value);
        return true;
    }

    public bool CopyAggro(ReadOnlySpan<byte> body)
    {
        EnsureActive(); var message = AggroCopy.Read(body);
        // The packet handler guards self-copy even though the lower-level table method does not.
        if (message.SourceId == message.DestinationId || FindByObjectId(message.SourceId) is not { } source ||
            FindByObjectId(message.DestinationId) is not { } destination) return false;
        destination.Aggro.CopyFrom(source.Aggro);
        return true;
    }

    public bool FeignDeath(ReadOnlySpan<byte> body)
    {
        EnsureActive(); var message = FakeDeath.Read(body);
        if (!_units.Units.ContainsKey(message.ObjectId)) return false;
        foreach (var id in GetNpcIdsTargeting(message.ObjectId))
            if (_npcs.TryGetValue(id, out var npc)) npc.Aggro.Remove(message.ObjectId);
        return true;
    }

    public bool Interact(ReadOnlySpan<byte> body, bool end = false)
    {
        EnsureActive(); var message = NpcInteractionRequest.Read(body);
        // 39363940 / 39363960 use the NPC manager's identity index, not the ServerUnit array.
        if (!_npcs.TryGetValue(message.NpcId, out var npc)) return false;
        if (end) npc.Talks.End(message.PlayerObjectId);
        else npc.Talks.Begin(message.PlayerObjectId);
        return true;
    }

    public bool SetInteractable(uint npcId, bool interactable)
    {
        EnsureActive();
        if (!_npcs.TryGetValue(npcId, out var npc)) return false;
        npc.Talks.SetInteractable(interactable);
        return true;
    }

    public void ProcessSignal(uint npcObjectId, uint targetObjectId, string signal)
    {
        EnsureActive(); ArgumentNullException.ThrowIfNull(signal);
        // 39326B40 starts with the ServerUnit array, then resolves that NPC's owner identity.
        FindByObjectId(npcObjectId)?.Signals.Process(targetObjectId, signal);
    }

    internal void Died(ZoneUnit unit)
    {
        if (FindByObjectId(unit.Snapshot.ObjectId) is not { } npc) return;
        // 393452D0 detaches shared aggro BEFORE clearing, then notifies AI and its spawner.
        npc.SharesAggro = false;
        npc.Aggro.Clear(15);
        _world.Died(npc);
        DetachSpawner(npc);
    }

    internal void ForcedTargetCleared(ZoneUnit unit, uint previousTargetId)
    {
        FindByObjectId(unit.Snapshot.ObjectId)?.Aggro.Remove(previousTargetId);
    }

    internal void FactionChanged(ZoneUnit unit, bool clearAggro)
    {
        _world.UpdateFaction(unit);
        // Direct SetAIFaction during independence retains threat; WZUnitFactionChanged clears it.
        if (clearAggro) FindByObjectId(unit.Snapshot.ObjectId)?.Aggro.Clear(3);
    }

    private readonly HashSet<ZoneUnit> _removingUnits = [];
    internal bool IsRemoving(ZoneUnit unit) => _removingUnits.Contains(unit);

    private bool Owns(ZoneNpc npc) => _npcs.TryGetValue(npc.Id, out var current) && ReferenceEquals(current, npc) &&
        _units.Units.TryGetValue(npc.ObjectId, out var unit) && ReferenceEquals(unit, npc.Unit);

    internal void Removing(ZoneUnit unit)
    {
        if (!_removingUnits.Add(unit)) return;
        // Capture both identities before invoking scene services. Managed callbacks may stop
        // the scene or request another removal; they cannot take over this cleanup operation.
        var npc = FindByObjectId(unit.Snapshot.ObjectId);
        if (npc is not null && !ReferenceEquals(npc.Unit, unit)) npc = null;
        npc?.Activation.Dispose();
        var errors = new List<Exception>();
        try
        {
            Cleanup(() => RemoveEntityReferences(unit, npc));
            if (npc is not null)
            {
                // Dispose the owned script state once, before actor destruction. Calling
                // ActorRemoved first and Dispose later would release that state twice.
                Cleanup(() => npc.AiSignals.Dispose());
                // 39345E90 -> 39345220 -> 393441B0 -> base unit-owner destruction.
                if (Owns(npc)) Cleanup(() => DetachSpawner(npc));
                if (Owns(npc)) Cleanup(() => npc.Aggro.Clear(16));
                if (Owns(npc)) Cleanup(() => _world.Remove(npc));
                npc.Talks.Dispose();
                npc.Signals.Dispose();
                if (_npcs.TryGetValue(npc.Id, out var current) && ReferenceEquals(current, npc)) _npcs.Remove(npc.Id);
            }
        }
        finally
        {
            _attributes.Remove(unit);
            if (_units.Units.TryGetValue(unit.Snapshot.ObjectId, out var current) && ReferenceEquals(current, unit))
                _reverse.Remove(unit.Snapshot.ObjectId);
            _removingUnits.Remove(unit);
        }
        // Other tables retain their target ID entry until their native refresh pass sees it missing.
        if (errors.Count == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(errors[0]).Throw();
        if (errors.Count > 1) throw new AggregateException("NPC owner cleanup failed.", errors);

        void Cleanup(Action action)
        {
            try { action(); }
            catch (Exception error) { errors.Add(error); }
        }
    }

    private void RemoveEntityReferences(ZoneUnit unit, ZoneNpc? removedNpc)
    {
        // Resolve before actor destruction invalidates the mapping. Native AI removal
        // (31006E0C / 31170B2C) erases queued references in every remaining actor.
        if (IsStopped || _world.AiRuntime.GetEntityId(unit) is not { } entityId || entityId == 0 || IsStopped) return;
        foreach (var npc in _npcs.Values.ToArray())
        {
            if (!Owns(npc)) continue;
            npc.AiSignals.RemoveSender(entityId);
            if (ReferenceEquals(npc, removedNpc) || IsRemoving(npc.Unit)) continue;
            var actorEntityId = _world.AiRuntime.GetEntityId(npc.Unit);
            if (Owns(npc) && !IsStopped && actorEntityId == entityId) npc.AiSignals.ActorRemoved();
        }
    }

    /// <summary>
    /// Explicit maintenance pass: refresh targets, enforce return rules, prune abusers and
    /// select the target. This does not supply the native frame cadence or actor activation gate.
    /// </summary>
    public void RefreshAggro()
    {
        EnsureActive();
        foreach (var npc in _npcs.Values)
        {
            if (npc.Unit.IsDead) continue;
            npc.Aggro.RefreshTargets();
            npc.Aggro.ProcessReturn(npc.Template);
            npc.Aggro.RefreshAbusers();
            npc.Aggro.UpdateSelection();
        }
    }

    public void Update(uint frame) => Update(frame, true);

    /// <summary>The complete manager supplies its movement gate before visiting NPC owners.</summary>
    public void Update(uint frame, bool canReplicateMovement)
    {
        EnsureActive();
        // 3933B210 walks the NPC manager's tree in unsigned NPC-ID order. Snapshot ownership
        // makes managed callback removal safe; newly created owners join the next frame.
        foreach (var npc in _npcs.OrderBy(pair => pair.Key).Select(pair => pair.Value).ToArray())
        {
            if (!IsCurrent(npc) || !_world.HasAiActor(npc)) continue;
            // Native caches death before any callbacks. Death during selection affects the next frame.
            foreach (var step in ZoneNpcUpdateSchedule.Steps(frame, npc.UpdatePhase, npc.Unit.IsDead, npc.AggroEnabled))
            {
                if (!IsCurrent(npc)) break;
                switch (step)
                {
                    case ZoneNpcUpdateStep.RefreshAggro: npc.Aggro.RefreshTargets(); break;
                    case ZoneNpcUpdateStep.ProcessReturn: npc.Aggro.ProcessReturn(npc.Template); break;
                    case ZoneNpcUpdateStep.RefreshAbusers: npc.Aggro.RefreshAbusers(); break;
                    case ZoneNpcUpdateStep.SelectTarget: npc.Aggro.UpdateSelection(); break;
                    case ZoneNpcUpdateStep.AiTimeEvent: npc.Timers.UpdateAiTimeEvent(() => npc.AiSignals.Send("OnAITimeEvent")); break;
                    case ZoneNpcUpdateStep.AcquireTargets: _world.AcquireAggroTargets(npc); break;
                    case ZoneNpcUpdateStep.Proximity:
                    case ZoneNpcUpdateStep.Activation:
                        var services = _world.GetActivationServices(npc) ?? throw new InvalidOperationException("The NPC scene returned no activation services.");
                        if (!IsCurrent(npc)) break;
                        if (step == ZoneNpcUpdateStep.Proximity) npc.Activation.UpdateProximity(services, _units.Spatial!);
                        else npc.Activation.UpdateActivation(services);
                        break;
                    case ZoneNpcUpdateStep.Cooldowns: npc.Signals.UpdateCooldowns(); break;
                    case ZoneNpcUpdateStep.IgnoredTargets: npc.Timers.ExpireIgnoredTargets(); break;
                    case ZoneNpcUpdateStep.Creator: CheckCreator(npc); break;
                    case ZoneNpcUpdateStep.Talks: npc.Talks.Update(); break;
                    case ZoneNpcUpdateStep.FollowTarget: _world.UpdateFollowTarget(npc); break;
                    case ZoneNpcUpdateStep.CrowdBuff: _world.UpdateCrowdBuff(npc); break;
                    case ZoneNpcUpdateStep.Position: _world.CheckPosition(npc); break;
                }
            }
            if (IsCurrent(npc) && canReplicateMovement && _world.MovementReplicationEnabled && IsCurrent(npc))
                _world.ReplicateMovement(npc);
        }
    }

    /// <summary>Runs the actor proxy signal phase. The scene supplies its ordering relative to physics and AI updates.</summary>
    public void UpdateAiSignals()
    {
        EnsureActive();
        foreach (var npc in _npcs.Values.ToArray())
            if (IsCurrent(npc) && _world.HasAiActor(npc)) npc.AiSignals.Update();
    }

    /// <summary>Releases actor scripts and owned signals, and removes sender references from other actors.</summary>
    public void AiActorRemoved(uint entityId)
    {
        if (entityId == 0 || IsStopped) return;
        foreach (var npc in _npcs.Values.ToArray())
        {
            if (!Owns(npc)) continue;
            npc.AiSignals.RemoveSender(entityId);
            // Unit cleanup already owns script release, including scene callbacks that
            // report the physical actor's removal while its owner is still published.
            if (IsRemoving(npc.Unit)) continue;
            var actorEntityId = _world.AiRuntime.GetEntityId(npc.Unit);
            if (!IsStopped && Owns(npc) && !IsRemoving(npc.Unit) && actorEntityId == entityId)
                npc.AiSignals.ActorRemoved();
        }
    }

    private bool IsCurrent(ZoneNpc npc) => !IsStopped && _npcs.TryGetValue(npc.Id, out var current) && ReferenceEquals(current, npc);

    private void CheckCreator(ZoneNpc npc)
    {
        // 3933C3B0 repeatedly requests removal until World removes the owner; zero means no creator.
        if (npc.CreatorObjectId != 0 && npc.Spawn.DespawnOnCreatorDeath &&
            (!_units.Units.TryGetValue(npc.CreatorObjectId, out var creator) || creator.IsDead))
            _units.QueueNotification(new NpcRemoval(npc.Id, 0).ToFrame(), this);
    }

    public void Stop()
    {
        if (IsStopped) return;
        IsStopped = true;
        // End the connection's actors and ownership, retaining ordinary unit snapshots for inspection.
        var errors = new List<Exception>();
        try
        {
            Cleanup(() => Spawners?.BeginShutdown());
            foreach (var npc in _npcs.Values.ToArray())
            {
                // An outer removal owns its remaining callbacks and final detach. Do not
                // unpublish it underneath that stack when a callback starts shutdown.
                if (!Owns(npc) || IsRemoving(npc.Unit)) continue;
                try { Cleanup(() => Removing(npc.Unit)); }
                finally { _units.RemovePrepared(npc.Unit); }
            }
        }
        finally
        {
            Cleanup(() => Spawners?.Dispose());
            _units.DiscardNotifications(this);
            _attributes.Clear();
            _reverse.Clear();
            Diagnostics.Retire();
        }
        if (errors.Count != 0) throw new AggregateException("NPC session cleanup failed.", errors);

        void Cleanup(Action action)
        {
            try { action(); }
            catch (Exception error) { errors.Add(error); }
        }
    }

    private void Route(ZoneNpc npc, ZoneUnit? source, ZoneUnit charge, uint amount)
    {
        if (source?.Snapshot.Identity.Kind == ZoneUnitKind.Slave)
        {
            npc.Aggro.Add(charge.Snapshot.ObjectId, new(0, 0, amount));
            npc.Aggro.Add(source.Snapshot.ObjectId, default);
        }
        else if (source?.Snapshot.Identity.Kind == ZoneUnitKind.Mate)
        {
            npc.Aggro.Add(source.Snapshot.ObjectId, new(0, 0, amount));
            npc.Aggro.Add(charge.Snapshot.ObjectId, default);
        }
        else npc.Aggro.Add(charge.Snapshot.ObjectId, new(0, 0, amount));
        var character = charge.Snapshot.Identity.Kind is ZoneUnitKind.Slave or ZoneUnitKind.Mate
            ? FindCharacter(charge.Snapshot.Identity.OwnerId) : charge;
        if (character?.Snapshot.Identity.Kind == ZoneUnitKind.Character)
            foreach (var target in _world.GetAdditionalAggroTargetIds(npc, character)) npc.Aggro.Add(target, default);
    }

    private ZoneUnit? FindCharacter(long id) => _units.Units.Values.SingleOrDefault(unit =>
        unit.Snapshot.Identity.Kind == ZoneUnitKind.Character && unit.Snapshot.Identity.PersistentId == id);

    private ZoneUnit? ResolveCreator(UnitIdentity creator) => creator.Kind switch
    {
        ZoneUnitKind.Character when creator.PersistentId != 0 => FindCharacter(creator.PersistentId),
        ZoneUnitKind.Npc => _npcs.GetValueOrDefault(creator.NpcObjectId)?.Unit,
        ZoneUnitKind.Housing => _units.Units.Values.SingleOrDefault(unit =>
            unit.Snapshot.Identity.Kind == ZoneUnitKind.Housing && unit.Snapshot.Identity.Timeline == creator.Timeline),
        _ => null // 39360CE0 has no unit-owner lookup for slave, mate, transfer or shipyard
    };

    private void DetachSpawner(ZoneNpc npc)
    {
        if (npc.SpawnerId == 0) return;
        RequireSpawners().Detach(npc);
        npc.SpawnerId = 0;
    }

    private int Attribute(ZoneUnit unit, uint attribute)
    {
        if (!_attributes.TryGetValue(unit, out var cached) || !ReferenceEquals(cached.Stamp, unit.Stats))
            _attributes[unit] = cached = (unit.Stats, []);
        if (!cached.Values.TryGetValue(attribute, out var value))
        {
            var result = _stats.CalculateAttributes(unit.Snapshot, [attribute], unit.Buffs, unit.Progression, unit.Equipment, unit.AttributeOverrides);
            if (!result.IsComplete) throw new NotSupportedException($"NPC threat requires complete attribute {attribute}: {string.Join(", ", result.UnresolvedSources)}.");
            value = result.Values[attribute];
            cached.Values.Add(attribute, value);
        }
        // Native callers retain the low signed 32 bits of the attribute getter.
        return unchecked((int)value);
    }

    private static int RemovalDelay(float seconds)
    {
        var milliseconds = seconds * 1000f;
        // x64 CVTTSS2SI returns the i64 indefinite value for overflow, whose low 32 bits are zero.
        return !float.IsFinite(milliseconds) || milliseconds >= 9223372036854775808f ? 0 : unchecked((int)(long)milliseconds);
    }

    private ZoneNpc Owner(ZoneAggroTable table) => _npcs[table.NpcId];
    private void EnsureActive() { if (IsStopped) throw new InvalidOperationException("NPC simulation belongs to a closed Zone session."); }
    uint IZoneAggroWorld.TimeMilliseconds => _world.TimeMilliseconds;
    ZoneAggroTarget? IZoneAggroWorld.FindTarget(uint id) => _units.Units.TryGetValue(id, out var unit)
        ? new(id, _world.GetPosition(unit), Attribute(unit, 90), _world.IsSleeping(unit)) : null;
    Vector3 IZoneAggroWorld.GetEncounterOrigin(ZoneAggroTable table) => _world.GetEncounterOrigin(Owner(table));
    Vector3 IZoneAggroWorld.GetOwnerPosition(ZoneAggroTable table) => _world.GetPosition(Owner(table).Unit);
    Vector3 IZoneAggroWorld.GetSpawnPosition(ZoneAggroTable table) => _world.GetSpawnPosition(Owner(table));
    void IZoneAggroWorld.RelocateOwner(ZoneAggroTable table, Vector3 position) => _world.Relocate(Owner(table), position);
    bool IZoneAggroWorld.ReturnChecksEnabled => _world.ReturnChecksEnabled;
    bool IZoneAggroWorld.IsInHousingArea(Vector3 position) => _world.IsInHousingArea(position);
    bool IZoneAggroWorld.IsOutsideTerritory(ZoneAggroTable table, Vector3 position) => _world.IsOutsideTerritory(Owner(table), position);
    bool IZoneAggroWorld.IsNavigationAbuser(ZoneAggroTarget target) => _world.IsNavigationAbuser(_units.Units[target.ObjectId]);
    float IZoneAggroWorld.GetTerrainHeight(Vector3 position) => _world.GetTerrainHeight(position);
    int IZoneAggroWorld.GetIncomingModifier(ZoneAggroTable table) => Attribute(Owner(table).Unit, 62);
    bool IZoneAggroWorld.CanAcquire(ZoneAggroTable table, ZoneAggroTarget target) =>
        _units.Units.TryGetValue(target.ObjectId, out var unit) && !unit.IsDead && (unit.Flags & 4) == 0 && _world.CanAcquire(Owner(table), unit);
    bool IZoneAggroWorld.CanRetain(ZoneAggroTable table, ZoneAggroTarget target) =>
        _units.Units.TryGetValue(target.ObjectId, out var unit) && !unit.IsDead && (unit.Flags & 4) == 0 && _world.CanRetain(Owner(table), unit);
    bool IZoneAggroWorld.ShouldCancel(ZoneAggroTable table, ZoneAggroEntry entry, ZoneAggroTarget target, uint now) =>
        _world.ShouldCancel(Owner(table), entry, _units.Units[target.ObjectId], now);
    void IZoneAggroWorld.EntryCreated(ZoneAggroTable table, ZoneAggroEntry entry)
    {
        if (!_reverse.TryGetValue(entry.TargetId, out var ids)) _reverse.Add(entry.TargetId, ids = []);
        ids.Add(table.NpcId);
        if (!IsStopped) _units.QueueNotification(new AggroTargetHostile(table.NpcId, entry.TargetId).ToFrame(), this);
    }
    void IZoneAggroWorld.EntryRemoved(ZoneAggroTable table, uint targetId)
    {
        if (!_reverse.TryGetValue(targetId, out var ids)) return;
        ids.Remove(table.NpcId);
        if (ids.Count == 0) _reverse.Remove(targetId);
    }
    void IZoneAggroWorld.EntryChanged(ZoneAggroTable table, uint targetId, bool synchronizeGroup)
    {
        var owner = Owner(table);
        _world.AggroChanged(owner, targetId);
        if (!synchronizeGroup || !owner.SharesAggro || _synchronizingGroup || IsStopped) return;
        _synchronizingGroup = true;
        try
        {
            foreach (var id in _world.GetSharedAggroNpcIds(owner))
                if (id != owner.Id && _npcs.TryGetValue(id, out var member)) member.Aggro.MirrorFrom(table, targetId);
        }
        finally { _synchronizingGroup = false; }
    }
    void IZoneAggroWorld.TargetChanged(ZoneAggroTable table, uint previousTargetId)
    {
        var owner = Owner(table);
        if (!owner.SharesAggro) { _world.AggroTargetChanged(owner, previousTargetId); return; }
        foreach (var id in _world.GetSharedAggroNpcIds(owner))
            if (_npcs.TryGetValue(id, out var member)) _world.AggroTargetChanged(member, previousTargetId);
    }
    void IZoneAggroWorld.CheckAbusers(ZoneAggroTable table, ZoneAggroTarget target) => _world.CheckAbusers(Owner(table), _units.Units[target.ObjectId]);
    void IZoneAggroWorld.AbuserRegistered(ZoneAggroTable table, uint targetId, int category)
    { if (!IsStopped) _units.QueueNotification(new NpcAbuserRegistered(table.OwnerObjectId, targetId).ToFrame(), this); }
    void IZoneAggroWorld.AbusersCleared(ZoneAggroTable table)
    { if (!IsStopped) _units.QueueNotification(new NpcAbusersCleared(table.OwnerObjectId).ToFrame(), this); }
    void IZoneAggroWorld.AbusersUnregistered(ZoneAggroTable table, IReadOnlyList<uint> targetIds)
    {
        if (IsStopped) return;
        foreach (var batch in targetIds.Chunk(NpcAbusersUnregistered.Capacity))
            _units.QueueNotification(new NpcAbusersUnregistered(table.OwnerObjectId, batch).ToFrame(), this);
    }
}

using System.Collections.ObjectModel;
using System.Numerics;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Combat;
using AAEmu.Zone.Content;
using AAEmu.Zone.Npcs;
using AAEmu.Zone.Protocol.Messages;
using AAEmu.Zone.Scene;

namespace AAEmu.Zone.Units;

public enum UnitCreationResult { Created, Duplicate }

/// <summary>
/// Units received for one Zone session, with content-derived vital stats when supplied.
/// Native physics, buff execution and spatial presence remain unfinished systems.
/// All access belongs to the session's sequential dispatch loop.
/// </summary>
public sealed partial class ZoneUnitRegistry
{
    private readonly Dictionary<uint, ZoneUnit> _units = new();
    private readonly Queue<(Protocol.ZoneFrame Frame, object? Owner, object? Scope)> _notifications = [];
    private bool _notificationsClosed;
    private readonly int _capacity;
    private readonly ZoneStatCalculator? _stats;
    private readonly ZoneBuffContent? _buffContent;
    private readonly ZoneStatContent? _content;
    private Func<ZoneUnit, Vector3>? _modelPosition;
    private IZoneUnitMovementServices? _movementServices;
    private readonly ZonePlayerActorFactory? _playerActors;
    private bool _movementStopped;
    internal bool IsMovementStopped => _movementStopped;
    private bool _receivingMovement;
    public IReadOnlyDictionary<uint, ZoneUnit> Units { get; }
    public ZoneNpcSimulation? Npcs { get; private set; }
    public ZoneUnitUpdateScheduler? Updates { get; private set; }
    public ZoneUnitSpatialIndex? Spatial { get; private set; }

    /// <summary>Queue complete simulation notifications on the owning scene thread, preserving cross-phase order.</summary>
    public bool QueueNotification(Protocol.ZoneFrame frame) => QueueNotification(frame, null);

    internal bool QueueNotification(Protocol.ZoneFrame frame, object? owner, object? scope = null)
    {
        if (_notificationsClosed) return false;
        _notifications.Enqueue((frame, owner, scope));
        return true;
    }

    public bool TryDequeueNotification(out Protocol.ZoneFrame frame)
    {
        if (_notifications.TryDequeue(out var notification)) { frame = notification.Frame; return true; }
        frame = default; return false;
    }

    // ReadOnlyMemory equality compares the exact retained buffer and range, not its bytes.
    internal bool HasQueuedNotification(object owner, ReadOnlyMemory<byte> body) =>
        _notifications.Any(value => ReferenceEquals(value.Owner, owner) && value.Frame.Body.Equals(body));

    internal void DiscardNotifications(object owner)
        => DiscardNotifications(owner, null, allScopes: true);

    internal void DiscardNotifications(object owner, object scope)
        => DiscardNotifications(owner, scope, allScopes: false);

    private void DiscardNotifications(object owner, object? scope, bool allScopes)
    {
        var count = _notifications.Count;
        for (var index = 0; index < count; index++)
        {
            var notification = _notifications.Dequeue();
            if (!ReferenceEquals(notification.Owner, owner) || (!allScopes && !ReferenceEquals(notification.Scope, scope)))
                _notifications.Enqueue(notification);
        }
    }
    internal void CloseNotifications() { _notificationsClosed = true; _notifications.Clear(); }

    public ZoneUnitRegistry(int capacity = 100_000, ZoneStatContent? statContent = null, IZoneNpcWorld? npcWorld = null,
        ZoneNpcContent? npcContent = null, IZoneUnitUpdateServices? unitUpdateServices = null,
        ZoneUnitSpatialIndex? spatialIndex = null, Func<ZoneUnit, Vector3>? modelPosition = null,
        IZoneUnitMovementServices? movementServices = null, ZonePlayerActorFactory? playerActors = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        if ((spatialIndex is null) != (modelPosition is null))
            throw new ArgumentException("A spatial index and actual model-position provider must be supplied together.");
        if (spatialIndex is not null)
        {
            ObjectDisposedException.ThrowIf(spatialIndex.IsDisposed, spatialIndex);
            if (spatialIndex.Count != 0) throw new ArgumentException("A new registry requires an empty spatial index.", nameof(spatialIndex));
        }
        _capacity = capacity; // Replacement resource policy; not a recovered native object-ID limit.
        _stats = statContent is null ? null : new(statContent);
        _buffContent = statContent?.Buffs;
        _content = statContent;
        Spatial = spatialIndex;
        _modelPosition = modelPosition;
        _movementServices = movementServices;
        _playerActors = playerActors;
        Units = new ReadOnlyDictionary<uint, ZoneUnit>(_units);
        if (unitUpdateServices is not null) Updates = new(unitUpdateServices);
        if (npcWorld is not null)
        {
            if (Spatial is null) throw new ArgumentException("NPC simulation requires a configured spatial index.", nameof(spatialIndex));
            Npcs = new(this, statContent ?? throw new ArgumentException("NPC simulation requires configured stat content.", nameof(statContent)), npcWorld,
                npcContent ?? throw new ArgumentException("NPC simulation requires configured NPC definitions.", nameof(npcContent)));
        }
        Spatial?.Claim(this);
    }

    // The coordinated path constructs the core first so real providers can retain this exact
    // registry. Native manager construction precedes the post-ZoneLoaded spatial allocation.
    internal void InitializeSceneServices(ZoneSceneRegistryServices services, ZoneNpcContent npcContent)
    {
        if (_content is null || Npcs is not null || Updates is not null || Spatial is not null || _units.Count != 0)
            throw new InvalidOperationException("Scene services require a fresh configured registry.");
        _modelPosition = services.ModelPosition;
        _movementServices = services.Movement;
        Updates = new(services.Updates);
        Npcs = new(this, _content, services.Npcs, npcContent);
    }

    internal void InitializeSceneSpatialIndex(ZoneSpatialRegionBounds bounds)
    {
        if (Spatial is not null || _modelPosition is null)
            throw new InvalidOperationException("Scene spatial initialization requires configured model services and no existing index.");
        var index = new ZoneUnitSpatialIndex(ZoneUnitSpatialIndex.LoadedWorldCellSize, bounds);
        index.Claim(this);
        Spatial = index;
    }

    public UnitCreationResult Create(ReadOnlySpan<byte> body)
    {
        // FUN_3936B560 -> FUN_3936B3C0: decode before handler, ignore an existing object ID.
        var snapshot = UnitStateSnapshot.Read(body);
        if (_units.ContainsKey(snapshot.ObjectId))
            return UnitCreationResult.Duplicate;
        var unit = Prepare(snapshot);
        AddPrepared(unit);
        if (snapshot.Identity.Kind == Protocol.Units.ZoneUnitKind.Character)
        {
            try
            {
                // Player actor admission (entity, physical body, flow-binding dependency gate) runs
                // inside the factory; spatial registration remains this registry's exact-owner stage.
                // With no factory wired the behavior is unchanged.
                _playerActors?.Admit(this, unit, snapshot);
                RegisterSpatial(unit);
            }
            catch (Exception error)
            {
                // A model callback may have replaced this numeric ID. Retire only our own admission:
                // the factory's exact-owner rollback never detaches a replacement unit, and the
                // removal below re-verifies IsCurrent before detaching anything.
                List<Exception>? rollbackErrors = null;
                try { _playerActors?.RollbackAdmitted(unit); }
                catch (Exception rollbackError) { (rollbackErrors ??= []).Add(rollbackError); }
                try { if (IsCurrent(unit)) RemovePrepared(unit); }
                catch (Exception removeError) { (rollbackErrors ??= []).Add(removeError); }
                if (rollbackErrors is null) throw; // original failure with its original stack
                rollbackErrors.Insert(0, error);
                throw new AggregateException("Character admission failed; its exact-owner rollback reported additional errors.", rollbackErrors);
            }
        }
        return UnitCreationResult.Created;
    }

    internal ZoneUnit Prepare(UnitStateSnapshot snapshot)
    {
        if (Npcs?.IsStopped == true) throw new InvalidOperationException("The NPC scene has stopped accepting units.");
        if (_units.Count == _capacity)
            throw new InvalidDataException("Zone unit registry reached its configured capacity.");
        var progression = ZoneProgressionState.FromSnapshot(snapshot, _content);
        var stats = _stats?.Calculate(snapshot, progression: progression);
        var unit = new ZoneUnit(snapshot, stats, progression, _content?.Skills);
        unit.SetBuffTimingEnabled(_buffContent is not null &&
            ZoneBuffTiming.RequiresUpdate(snapshot.Identity.Kind, snapshot.Buffs, _buffContent));
        // 39450870 applies initial effects in good/bad/hidden order after model creation.
        // Each scale buff retargets from the current multiplier; this does not combine their scales.
        if (_buffContent is not null)
            foreach (var buff in unit.Buffs.Good.Concat(unit.Buffs.Bad).Concat(unit.Buffs.Hidden))
                if (_buffContent.Templates.TryGetValue(buff.TemplateId, out var template) && template.Scale != 1f)
                    unit.Scale.SetTarget(template.Scale, template.ScaleDuration);
        return unit;
    }

    internal void AddPrepared(ZoneUnit unit)
    {
        if (Npcs?.IsStopped == true) throw new InvalidOperationException("The NPC scene has stopped accepting units.");
        if (_units.ContainsKey(unit.Snapshot.ObjectId)) throw new InvalidOperationException("The unit object ID is already registered.");
        if (_units.Count >= _capacity) throw new InvalidDataException("Zone unit registry reached its configured capacity.");
        Updates?.Register(unit);
        try
        {
            // Allocation-key and clock callbacks may publish another owner after Prepare's checks.
            // Preserve that owner, and retire only this registration if publication can no longer succeed.
            if (Npcs?.IsStopped == true) throw new InvalidOperationException("The NPC scene has stopped accepting units.");
            if (_units.ContainsKey(unit.Snapshot.ObjectId))
                throw new InvalidOperationException("The unit object ID was registered during initialization.");
            if (_units.Count >= _capacity) throw new InvalidDataException("Zone unit registry reached its configured capacity.");
            _units.Add(unit.Snapshot.ObjectId, unit);
        }
        catch
        {
            Updates?.Remove(unit);
            throw;
        }
    }

    internal void RemovePrepared(ZoneUnit unit)
    {
        if (!IsCurrent(unit)) return;
        _units.Remove(unit.Snapshot.ObjectId);
        RetireCastingUnit(unit);
        Spatial?.Remove(unit);
        Updates?.Remove(unit);
        unit.MovementModel?.Retire();
    }

    internal bool IsCurrent(ZoneUnit unit) => _units.TryGetValue(unit.Snapshot.ObjectId, out var current) && ReferenceEquals(current, unit);
    internal bool IsMovementCurrent(ZoneUnit unit) => !_movementStopped && IsCurrent(unit);

    public void BindMovementModel(IZoneUnitMovementModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var owner = model.Owner;
        if (_movementStopped) throw new InvalidOperationException("The movement scene has stopped.");
        if (!IsCurrent(owner)) throw new ArgumentException("A movement model requires its exact registered owner.", nameof(model));
        if (owner.MovementModel is not null && !ReferenceEquals(owner.MovementModel, model))
            throw new InvalidOperationException("This unit already owns a movement model.");
        owner.MovementModel = model;
    }

    public bool ReceiveMovement(ReadOnlySpan<byte> body)
    {
        var message = UnitMovement.Read(body); // Native decoding precedes every handler guard.
        if (_receivingMovement) throw new InvalidOperationException("Movement dispatch cannot reenter the owning Zone scene.");
        _receivingMovement = true;
        try { return ReceiveMovementCore(message); }
        finally { _receivingMovement = false; }
    }

    private bool ReceiveMovementCore(UnitMovement message)
    {
        if (_movementStopped) return false;
        var services = _movementServices;
        if (services is null)
        {
            if (!_units.ContainsKey(message.ObjectId)) return false;
            throw new NotSupportedException("Unit movement requires the actual scene switch, clock and recorder.");
        }
        var serverControlled = services.ServerControlledMovement;
        if (_movementStopped || (serverControlled && message.Movement.Kind != 5)) return false;
        if (!_units.TryGetValue(message.ObjectId, out var unit)) return false;
        var now = services.ServerMilliseconds;
        if (_movementStopped || !IsCurrent(unit) || now < unit.MovementBlockedUntil) return false;
        var model = unit.MovementModel ?? throw new NotSupportedException("Unit movement requires an initialized movement model.");
        model.Receive(message.Movement);
        if (_movementStopped || !IsCurrent(unit)) return false;
        services.RecordMovement(unit, message.Movement);
        return true;
    }

    public void StopMovement()
    {
        if (_movementStopped) return;
        _movementStopped = true;
        List<Exception>? errors = null;
        foreach (var unit in _units.Values.ToArray())
        {
            try { unit.MovementModel?.Retire(); }
            catch (Exception error) { (errors ??= []).Add(error); }
        }
        if (errors is not null) throw new AggregateException("Movement models failed during scene retirement.", errors);
    }

    // Character owners publish before registration; NPCs register after actor/spawner placement.
    internal bool RegisterSpatial(ZoneUnit unit)
    {
        if (Spatial is null || !IsCurrent(unit)) return false;
        var position = _modelPosition!(unit);
        return IsCurrent(unit) && Spatial.Register(unit, position);
    }

    /// <summary>Rolling spatial phase: cache the live model position for this exact registered owner.</summary>
    public bool UpdateSpatialMembership(ZoneUnit unit)
    {
        if (Spatial is null || !IsCurrent(unit) || Spatial.GetMembership(unit) is null) return false;
        var position = _modelPosition!(unit);
        return IsCurrent(unit) && Spatial.Update(unit, position);
    }

    public bool ResetCooldowns(SkillCooldownReset message)
    {
        if (!_units.TryGetValue(message.ObjectId, out var unit)) return false;
        unit.Cooldowns.Reset(message);
        return true;
    }

    public bool ReduceCooldowns(SkillCooldownReduce message)
    {
        if (!_units.TryGetValue(message.ObjectId, out var unit)) return false;
        unit.Cooldowns.Reduce(message);
        return true;
    }

    public bool ChangeChargeCooldown(ChargeSkillCooldownChange message)
    {
        if (!_units.TryGetValue(message.ObjectId, out var unit)) return false;
        unit.Cooldowns.ChangeCharge(message);
        return true;
    }

    /// <summary>39456630: World supplies the resolved cooldown when a matching casting timeline stops.</summary>
    public bool StartStoppedCastingCooldown(CastingStopped message)
        => ApplyCastingStopped(message);

    public bool UpdatePoints(ReadOnlySpan<byte> body)
    {
        var points = UnitPoints.Read(body);
        if (!_units.TryGetValue(points.ObjectId, out var unit))
            return false; // native 39364C30 ignores missing units
        unit.Apply(points);
        return true;
    }

    public bool Kill(ReadOnlySpan<byte> body)
    {
        var message = UnitDeath.Read(body);
        if (!_units.TryGetValue(message.ObjectId, out var unit)) return false;
        var stats = _stats?.Calculate(unit.Snapshot, unit.Buffs, unit.Progression, unit.Equipment, unit.AttributeOverrides);
        unit.Die(stats);
        Npcs?.Died(unit);
        return true;
    }

    public bool Resurrect(ReadOnlySpan<byte> body)
    {
        var message = UnitResurrection.Read(body);
        if (!_units.TryGetValue(message.ObjectId, out var unit)) return false;
        var stats = _stats?.Calculate(unit.Snapshot, unit.Buffs, unit.Progression, unit.Equipment, unit.AttributeOverrides);
        unit.Resurrect(message, stats);
        return true;
    }

    public bool EngageCombat(ReadOnlySpan<byte> body)
    {
        var message = CombatEngaged.Read(body);
        if (!_units.TryGetValue(message.ObjectId, out var unit)) return false;
        unit.SetCombat(true);
        return true;
    }

    public bool ClearCombat(ReadOnlySpan<byte> body)
    {
        var message = CombatCleared.Read(body);
        if (!_units.TryGetValue(message.ObjectId, out var unit)) return false;
        unit.SetCombat(false);
        return true;
    }

    public bool SetForceAttack(ReadOnlySpan<byte> body)
    {
        var message = ForceAttackSet.Read(body);
        if (!_units.TryGetValue(message.ObjectId, out var unit)) return false;
        unit.SetForceAttack(message.Enabled);
        return true;
    }

    public bool ChangeFlyingState(ReadOnlySpan<byte> body)
    {
        var message = UnitFlyingStateChanged.Read(body);
        if (!_units.TryGetValue(message.ObjectId, out var unit)) return false;
        // 39258C90 also notifies the unit's physics actor (vfunc +296); that seam is not modelled yet.
        unit.IsFlying = message.Flying;
        return true;
    }

    public bool ChangeTarget(ReadOnlySpan<byte> body)
    {
        var message = TargetChanged.Read(body);
        if (!_units.TryGetValue(message.ObjectId, out var unit)) return false;
        // Native 3936E170 writes the ID even if the target is not in this Zone.
        var previousTarget = unit.TargetId;
        unit.SetTarget(message.TargetId);
        if (message.ForceByWorld && message.TargetId == 0)
            Npcs?.ForcedTargetCleared(unit, previousTarget);
        return true;
    }

    public bool ChangeDuel(ReadOnlySpan<byte> body)
    {
        var message = UnitDuelState.Read(body);
        if (!_units.TryGetValue(message.ObjectId, out var unit)) return false;
        unit.SetDuel(message);
        return true;
    }

    public bool ApplyGmCommand(ReadOnlySpan<byte> body)
    {
        var command = GmCommand.Read(body);
        if (command.Command == GmCommand.ChangeMode)
        {
            if (!command.TryReadMode(out var mode, out var value) || !_units.TryGetValue(command.ObjectId, out var unit)) return false;
            unit.SetGmMode(mode, value);
            return true;
        }
        if (command.Command is GmCommand.SetAttribute or GmCommand.ClearAttribute)
        {
            if (!command.TryReadAttributeChange(out var attribute, out var value) || !_units.TryGetValue(command.ObjectId, out var unit)) return false;
            var overrides = unit.AttributeOverrides.Change(attribute, value);
            var stats = _stats?.Calculate(unit.Snapshot, unit.Buffs, unit.Progression, unit.Equipment, overrides);
            unit.CommitAttributeOverrides(overrides, stats);
            return true;
        }
        // 393639D0 parses before lookup and silently ignores other selectors.
        if (!GmCommandData.TryRead(command.Parameters, out var data) || !_units.TryGetValue(command.ObjectId, out var gm)) return false;
        if (command.Command is 49 or 142 or 143 or 144)
            return ApplyDeferredGm(command.Command, data!, gm);
        return false;
    }

    /// <summary>
    /// Selectors 49/142/143/144 of 393639D0 (handlers 39454180/39454400/39454230/39454260). Native arity
    /// and unit-kind checks are reproduced; the effects need subsystems this zone lacks (skill fire path,
    /// ZWDumpNpcPacket reply, NPC state setter, NPC teleport) and are logged by name, never thrown.
    /// Returns false: no state changed.
    /// </summary>
    private bool ApplyDeferredGm(byte selector, GmCommandData data, ZoneUnit gm)
    {
        var isCharacter = gm.Snapshot.Identity.Kind == Protocol.Units.ZoneUnitKind.Character; // native unit+24 == 1
        var count = data.Children.Count;
        switch (selector)
        {
            case 49: // HandleGmSkill: needs >= 1 argument; fires skill via the IGame skill sub-object.
                if (count < 1) return false;
                if (!isCharacter) return false;
                Console.WriteLine($"GM skill {data.Children[0].Int32Value} (selector 49) not applied: no skill fire path (TODO(gm-skill)).");
                return false;
            case 142: // HandleGmSearchNpc: 1-2 arguments, replies with ZWDumpNpcPacket.
                if (count is < 1 or > 2) return false;
                Console.WriteLine($"GM search NPC {data.Children[0].Int32Value} (selector 142) not applied: no NPC search and ZWDumpNpcPacket reply (TODO(gm-search-npc)).");
                return false;
            case 143: // 39454230: applies mode 14 to the Character's targeted NPC (393200D0).
                if (!isCharacter || gm.TargetId == 0 || Npcs?.FindByObjectId(gm.TargetId) is null) return false;
                Console.WriteLine($"GM selector 143 on target {gm.TargetId} not applied: NPC mode 14 setter (393200D0) is not ported (TODO(gm-npc-mode)).");
                return false;
            default: // 144 HandleGmMoveNpc: exactly 3 arguments (x, y, z) and a Character caller.
                if (count != 3 || !isCharacter) return false;
                Console.WriteLine($"GM move NPC to ({data.Children[0].Int32Value},{data.Children[1].Int32Value},{data.Children[2].Int32Value}) " +
                    "(selector 144) not applied: no NPC teleport path (TODO(gm-move-npc)).");
                return false;
        }
    }

    public bool ChangeFaction(ReadOnlySpan<byte> body)
    {
        var message = UnitFactionChanged.Read(body);
        if (!_units.TryGetValue(message.ObjectId, out var unit) || unit.FactionId != message.OldFactionId) return false;
        SetFaction(unit, message.NewFactionId, message.Temporary, clearAggro: true);
        return true;
    }

    internal void SetFaction(ZoneUnit unit, uint factionId, bool temporary, bool clearAggro)
    {
        unit.SetFaction(factionId, temporary);
        Npcs?.FactionChanged(unit, clearAggro);
    }

    public bool ChangeExpedition(ReadOnlySpan<byte> body)
    {
        var message = UnitExpeditionChanged.Read(body);
        if (!_units.TryGetValue(message.ObjectId, out var unit) || unit.ExpeditionId != message.OldExpeditionId) return false;
        unit.SetExpedition(message.NewExpeditionId);
        return true;
    }

    public bool ChangeEquipment(ReadOnlySpan<byte> body)
    {
        var message = EquipmentChanged.Read(body);
        if (!_units.TryGetValue(message.ObjectId, out var unit)) return false;
        var equipment = ZoneEquipmentLifecycle.Change(unit.Equipment, message);
        var stats = _stats?.Calculate(unit.Snapshot, unit.Buffs, unit.Progression, equipment, unit.AttributeOverrides);
        unit.CommitEquipment(equipment, stats);
        return true;
    }

    public bool ChangeEquipmentActivation(ReadOnlySpan<byte> body)
    {
        var message = EquipmentActivationChanged.Read(body);
        if (!_units.TryGetValue(message.ObjectId, out var unit)) return false;
        var equipment = unit.Equipment with { CharacterFlags = message.Flags };
        var stats = _stats?.Calculate(unit.Snapshot, unit.Buffs, unit.Progression, equipment, unit.AttributeOverrides);
        unit.CommitEquipment(equipment, stats);
        return true;
    }

    public bool CreateBuff(ReadOnlySpan<byte> body)
    {
        var message = BuffCreated.Read(body);
        if (!_units.TryGetValue(message.TargetId, out var unit)) return false;
        var content = RequireBuffContent();
        // 39450740 reports an invalid template and returns without creating an instance.
        if (!content.Templates.TryGetValue(message.TemplateId, out var template)) return false;
        var next = ZoneBuffLifecycle.Create(unit.Buffs, message, template);
        if (next is null) return false;
        var progression = unit.Progression with
        { Passives = unit.Progression.Passives.Grant(template, _content!, unit.Snapshot, unit.Progression.Level) };
        if (!CommitBuffs(unit, next, progression)) return false;
        // 39450740 invokes 3944FEB0 after successful creation or refresh, including at category capacity.
        if (template.Scale != 1f) unit.Scale.SetTarget(template.Scale, template.ScaleDuration);
        return true;
    }

    public bool UpdateBuff(ReadOnlySpan<byte> body)
    {
        var message = BuffUpdated.Read(body);
        if (!_units.TryGetValue(message.TargetId, out var unit)) return false;
        var next = ZoneBuffLifecycle.Update(unit.Buffs, message, RequireBuffContent());
        return CommitBuffs(unit, next);
    }

    public bool RemoveBuff(ReadOnlySpan<byte> body)
    {
        var message = BuffRemoved.Read(body);
        if (!_units.TryGetValue(message.TargetId, out var unit)) return false;
        var content = RequireBuffContent();
        var previous = unit.Buffs.Good.Concat(unit.Buffs.Bad).Concat(unit.Buffs.Hidden).FirstOrDefault(buff => buff.Index == message.Index);
        if (previous is null) return false;
        var progression = unit.Progression with
        { Passives = unit.Progression.Passives.RemoveGrant(content.Template(previous.TemplateId), content) };
        if (!CommitBuffs(unit, ZoneBuffLifecycle.Remove(unit.Buffs, message.Index), progression)) return false;
        var removedTemplate = content.Template(previous.TemplateId);
        // 39450420 restores one when any scaling instance is removed, even if another scaling buff remains.
        if (removedTemplate.Scale != 1f) unit.Scale.SetTarget(1f, removedTemplate.ScaleDuration);
        return true;
    }

    private bool CommitBuffs(ZoneUnit unit, Protocol.Units.UnitBuffs? buffs, ZoneProgressionState? progression = null)
    {
        if (buffs is null) return false;
        progression ??= unit.Progression;
        var stats = _stats!.Calculate(unit.Snapshot, buffs, progression, unit.Equipment, unit.AttributeOverrides);
        unit.CommitBuffs(buffs, progression, stats);
        unit.SetBuffTimingEnabled(ZoneBuffTiming.RequiresUpdate(unit.Snapshot.Identity.Kind, buffs, RequireBuffContent()));
        return true;
    }

    private readonly HashSet<ZoneUnit> _advancingBuffs = [];

    /// <summary>
    /// Apply the Zone's buff timing phase for an elapsed millisecond delta. World still supplies removals;
    /// the actor/physics phase must separately handle native remove-on-land requests.
    /// </summary>
    public bool AdvanceBuffTiming(uint objectId, uint elapsedMilliseconds)
    {
        if (!_units.TryGetValue(objectId, out var unit)) return false;
        var content = RequireBuffContent();
        if (!unit.BuffTimingEnabled || !_advancingBuffs.Add(unit)) return false;
        try
        {
            var timing = ZoneBuffTiming.Advance(unit.Buffs, content, elapsedMilliseconds);
            var stats = timing.InvalidatedAttributes.Count == 0 ? unit.Stats :
                _stats!.Calculate(unit.Snapshot, timing.Buffs, unit.Progression, unit.Equipment, unit.AttributeOverrides);
            unit.CommitBuffTiming(timing.Buffs, stats);
            return true;
        }
        finally { _advancingBuffs.Remove(unit); }
    }

    /// <summary>
    /// 3944FC90: advance each good/bad/hidden buff, commit its stat invalidations, then request landing
    /// removal before visiting the next buff. The caller supplies the per-unit unchecked clock delta.
    /// Returns false for absent, gated-off or already-updating units. Once entered, completed work remains
    /// committed if a callback changes ownership or throws; callback changes retire the remaining pass.
    /// </summary>
    public bool AdvanceBuffs(uint objectId, uint elapsedMilliseconds, IZoneBuffLandingPhysics physics,
        Action<BuffRemovalRequest> send)
    {
        ArgumentNullException.ThrowIfNull(physics);
        ArgumentNullException.ThrowIfNull(send);
        if (!_units.TryGetValue(objectId, out var unit)) return false;
        var content = RequireBuffContent();
        if (!unit.BuffTimingEnabled || !_advancingBuffs.Add(unit)) return false;
        var expected = unit.Buffs;
        try
        {
            // The native iterator captures category ends and reads live slots. Managed callbacks can
            // synchronously dispatch packets; retire on collection/owner replacement instead of applying
            // the old pass to shifted or reused slots. This retirement is replacement ownership policy.
            for (var category = 0; category < 3; category++)
            {
                var count = Select(expected, category).Count;
                for (var offset = 0; offset < count; offset++)
                {
                    if (!IsCurrent()) return true;
                    var before = Select(expected, category)[offset];
                    var timing = ZoneBuffTiming.Advance(before, content, elapsedMilliseconds);
                    if (ReferenceEquals(timing.Buff, before)) continue; // Missing native template: untouched.
                    var changed = Select(expected, category).ToArray();
                    changed[offset] = timing.Buff;
                    var values = Array.AsReadOnly(changed);
                    expected = category switch
                    {
                        0 => expected with { Good = values },
                        1 => expected with { Bad = values },
                        _ => expected with { Hidden = values }
                    };
                    var stats = timing.InvalidatedAttributes.Count == 0 ? unit.Stats :
                        _stats!.Calculate(unit.Snapshot, expected, unit.Progression, unit.Equipment, unit.AttributeOverrides);
                    unit.CommitBuffTiming(expected, stats);
                    var request = ZoneBuffLandingRemoval.Evaluate(objectId, unit.Snapshot.Identity.Kind,
                        timing.Buff, content, physics);
                    if (!IsCurrent()) return true;
                    if (request is { } removal)
                    {
                        send(removal);
                        if (!IsCurrent()) return true;
                    }
                }
            }
            return true;
        }
        finally { _advancingBuffs.Remove(unit); }

        bool IsCurrent() => _units.TryGetValue(objectId, out var current) && ReferenceEquals(current, unit) &&
            ReferenceEquals(unit.Buffs, expected);

        static IReadOnlyList<Protocol.Units.UnitBuff> Select(Protocol.Units.UnitBuffs buffs, int category) => category switch
        { 0 => buffs.Good, 1 => buffs.Bad, _ => buffs.Hidden };
    }

    public bool LearnBuff(ReadOnlySpan<byte> body)
    {
        var message = BuffLearned.Read(body);
        if (!_units.TryGetValue(message.ObjectId, out var unit)) return false;
        var content = RequireBuffContent();
        if (!content.Passives.ContainsKey(message.PassiveId)) return false;
        var passives = unit.Progression.Passives.Learn(message.PassiveId, content);
        // The native handler invalidates stats for known definitions even if insertion is a duplicate.
        return CommitProgression(unit, unit.Progression with { Passives = passives });
    }

    public bool ResetSkills(ReadOnlySpan<byte> body)
    {
        var message = SkillsReset.Read(body);
        if (!_units.TryGetValue(message.ObjectId, out var unit)) return false;
        RequireBuffContent();
        return CommitProgression(unit, unit.Progression.Reset(message.AbilityId, _content!));
    }

    public bool AddSwapPassives(ReadOnlySpan<byte> body)
    {
        var message = AddSwapPassiveBuffs.Read(body);
        if (!_units.TryGetValue(message.ObjectId, out var unit)) return false;
        RequireBuffContent();
        var passives = unit.Progression.Passives.AddSwap(message.BuffTemplateId, _content!, unit.Snapshot, unit.Progression.Level);
        if (ReferenceEquals(passives, unit.Progression.Passives)) return false;
        return CommitProgression(unit, unit.Progression with { Passives = passives });
    }

    public bool ChangeLevel(ReadOnlySpan<byte> body)
    {
        var message = LevelChanged.Read(body);
        if (!_units.TryGetValue(message.ObjectId, out var unit)) return false;
        // 39364270 -> ServerUnit vft[11] = 3925A830: only character/mate, levels 0..100.
        if (unit.Snapshot.Identity.Kind is not (Protocol.Units.ZoneUnitKind.Character or Protocol.Units.ZoneUnitKind.Mate) || message.Level > 100)
            return false;
        // The native setter does not invalidate stats or clamp points. Later invalidation uses the new level.
        unit.CommitProgression(unit.Progression with { Level = message.Level }, unit.Stats);
        return true;
    }

    private bool CommitProgression(ZoneUnit unit, ZoneProgressionState progression)
    {
        var stats = _stats!.Calculate(unit.Snapshot, unit.Buffs, progression, unit.Equipment, unit.AttributeOverrides);
        unit.CommitProgression(progression, stats);
        return true;
    }

    private ZoneBuffContent RequireBuffContent() => _buffContent ??
        throw new NotSupportedException("Buff changes require configured game content.");

    private readonly HashSet<ZoneUnit> _removingUnits = [];

    public bool Remove(ReadOnlySpan<byte> body)
    {
        // FUN_39364A80: absent IDs are a no-op. Never delete persistent World player data.
        var reader = new Protocol.ZoneReader(body);
        var objectId = reader.ReadBc();
        reader.EnsureFullyConsumed();
        if (!_units.TryGetValue(objectId, out var unit) || Npcs?.IsRemoving(unit) == true || !_removingUnits.Add(unit)) return false;
        // Managed callback ownership policy: keep this owner published until its cleanup
        // finishes, reject recursive removal, and never detach a later owner of the same ID.
        try
        {
            Npcs?.Removing(unit);
            return true;
        }
        finally
        {
            try { RemovePrepared(unit); }
            finally { _removingUnits.Remove(unit); }
        }
    }
}

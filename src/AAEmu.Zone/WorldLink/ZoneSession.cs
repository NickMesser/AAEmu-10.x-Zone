using AAEmu.Zone.Actors;
using AAEmu.Zone.Content;
using AAEmu.Zone.Hosting;
using AAEmu.Zone.Npcs;
using AAEmu.Zone.Protocol;
using AAEmu.Zone.Protocol.Messages;
using AAEmu.Zone.Scene;
using AAEmu.Zone.Units;
using AAEmu.Zone.WorldState;

namespace AAEmu.Zone.WorldLink;

/// <summary>
/// One World connection's startup and unit state. The stream remains caller-owned.
/// Reconnect creates a fresh session; snapshots from an old connection are never reused.
/// </summary>
public sealed partial class ZoneSession
{
    private readonly Stream _connection;
    private readonly ZoneSceneCoordinator? _scene;
    private readonly ZoneSceneFrameCoordinator? _frame;
    private bool PreserveOwnerContext => _scene?.Owner is not null;
    private int _receiving;
    public ZoneStartupState Startup { get; }
    public ZoneUnitRegistry Registry { get; }
    public ZoneFactionState Factions { get; }
    public ZoneCombatRelations CombatRelations { get; } = new();
    public ZoneUnitRelationships? Relationships { get; }
    public IZoneRelationshipWorld? RelationshipWorld { get; }
    public ZoneWorldContentPolicy? ContentPolicy { get; }
    public ZoneAchievementIndex? Achievements { get; }
    public ZoneLootIndex? Loot { get; }
    public ZoneGameScheduleController? GameSchedules { get; private set; }
    public bool IsClosed { get; private set; }
    /// <summary>
    /// Opcodes this wave does not implement, counted per opcode. An unknown packet is a NAMED,
    /// COUNTED refusal — never a session fault: the first live World connect (R5, 2026-09-23)
    /// proved that throwing on one unimplemented opcode (0x0036) retires the whole zone seconds
    /// after <c>ZWZoneLoaded</c>, and World then logs "Zone TCP disconnect". The opcode stays
    /// unimplemented and visible; the rest of the stream keeps running.
    /// </summary>
    public IReadOnlyDictionary<ushort, int> UnimplementedOpcodes => _unimplementedOpcodes;
    private readonly Dictionary<ushort, int> _unimplementedOpcodes = [];

    /// <summary>
    /// Player admissions refused by a named dependency gate, counted per refusal. The unit is not
    /// created and the session continues; the first message is kept for the readiness evidence.
    /// </summary>
    public int AdmissionRefusalCount { get; private set; }

    /// <summary>The first named admission refusal, reported verbatim.</summary>
    public string? FirstAdmissionRefusal { get; private set; }
    internal bool BelongsToScene(ZoneSceneCoordinator scene) => ReferenceEquals(_scene, scene);

    /// <summary>Attach the exact scene's packet-driven controller; readiness follows its factory post-load gate.</summary>
    public void AttachGameSchedules(ZoneGameScheduleController controller)
    {
        _scene?.Owner?.VerifyAccess();
        ArgumentNullException.ThrowIfNull(controller);
        ObjectDisposedException.ThrowIf(IsClosed, this); ObjectDisposedException.ThrowIf(controller.IsDisposed, controller);
        if (!controller.BelongsTo(Registry)) throw new ArgumentException("The schedule controller belongs to another Zone session.", nameof(controller));
        if (GameSchedules is not null && !ReferenceEquals(GameSchedules, controller))
            throw new InvalidOperationException("This session already owns a schedule controller.");
        GameSchedules = controller;
    }

    private ZoneSession(Stream connection, ZoneStartupState startup, ZoneStatContent? statContent, IZoneNpcWorld? npcWorld,
        bool? battlefieldZone, ZoneNpcContent? npcContent, Func<ZoneJoinResponse, IZoneRelationshipWorld>? relationshipWorldFactory,
        ZoneWorldContent? worldContent, ZoneAchievementContent? achievementContent, ZoneLootContent? lootContent,
        IZoneUnitUpdateServices? unitUpdateServices, ZoneUnitSpatialIndex? spatialIndex,
        Func<ZoneUnit, System.Numerics.Vector3>? modelPosition, IZoneUnitMovementServices? movementServices,
        ZoneSceneCoordinator? scene = null, ZoneSceneFrameCoordinator? frame = null)
    {
        _connection = connection;
        _scene = scene;
        _frame = frame;
        Startup = startup;
        if (scene is null && worldContent is null && startup.ContentFilter!.Categories.Count != 0)
            throw new ArgumentException("A filtered Zone session requires the configured World content catalog.", nameof(worldContent));
        ContentPolicy = scene?.ContentPolicy ?? (worldContent is null ? null : new(worldContent, startup.ContentFilter!));
        if (achievementContent is not null) Achievements = ContentPolicy?.ApplyTo(achievementContent) ?? new(achievementContent);
        if (lootContent is not null) Loot = ContentPolicy?.ApplyTo(lootContent) ?? new(lootContent);
        if (scene is null && ContentPolicy is not null && statContent is not null) statContent = ContentPolicy.ApplyTo(statContent);
        // Complete fallible relationship admission before the registry claims the caller's index.
        // A failed factory or missing catalog must leave that live index available for another join.
        if (relationshipWorldFactory is not null)
        {
            if (npcContent is null)
                throw new ArgumentException("Unit relationships require NPC definitions.", nameof(npcContent));
            if (statContent is null)
                throw new ArgumentException("Unit relationships require buff content.", nameof(statContent));
            RelationshipWorld = relationshipWorldFactory(startup.JoinResponse!) ??
                throw new ArgumentException("The relationship scene factory returned no scene.", nameof(relationshipWorldFactory));
        }
        Registry = scene?.Registry ?? new(statContent: statContent, npcWorld: npcWorld, npcContent: npcContent, unitUpdateServices: unitUpdateServices,
            spatialIndex: spatialIndex, modelPosition: modelPosition, movementServices: movementServices);
        Factions = new(Registry, startup.Factions.Values, startup.Relations.Values, battlefieldZone);
        if (RelationshipWorld is not null)
            Relationships = new(Registry, Factions, CombatRelations, npcContent!, statContent!.Buffs,
                startup.JoinResponse!.Battlefield, RelationshipWorld);
        // A snapshot-only session already owns its stream's receive state. It cannot also be
        // repurposed as a coordinated scene while that original registry remains attached.
        if (scene is null) startup.ClaimScene(connection, startup.JoinRequest!.Value.ZoneKey);
    }

    /// <summary>
    /// Adopts an already synchronized, fully loaded exact scene. It neither joins nor sends
    /// ZoneLoaded again. Its receive loop owns scene retirement; the coordinator also allows
    /// explicit retirement before that loop starts. No service or snapshot is copied.
    /// </summary>
    public static ZoneSession AdoptScene(ZoneSceneCoordinator scene, bool? battlefieldZone = null,
        Func<ZoneJoinResponse, IZoneRelationshipWorld>? relationshipWorldFactory = null,
        ZoneAchievementContent? achievementContent = null, ZoneLootContent? lootContent = null,
        ZoneSceneFrameCoordinator? frame = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        scene.ClaimSession();
        try
        {
            var session = new ZoneSession(scene.Connection, scene.Startup, scene.StatContent, null,
                battlefieldZone, scene.NpcContent, relationshipWorldFactory, null, achievementContent, lootContent,
                null, null, null, null, scene, frame);
            session.AttachGameSchedules(scene.GameSchedules);
            return session;
        }
        catch (Exception failure)
        {
            try { scene.Dispose(); }
            catch (Exception cleanup) { throw new AggregateException(failure, cleanup); }
            throw;
        }
    }

    public static async Task<ZoneSession> JoinAsync(Stream connection, ZoneJoin join, TimeSpan timeout,
        CancellationToken cancellationToken = default, ZoneStatContent? statContent = null, IZoneNpcWorld? npcWorld = null,
        bool? battlefieldZone = null, ZoneNpcContent? npcContent = null,
        Func<ZoneJoinResponse, IZoneRelationshipWorld>? relationshipWorldFactory = null,
        WorldContentNameComparer? contentNameComparer = null, ZoneWorldContent? worldContent = null,
        ZoneAchievementContent? achievementContent = null, ZoneLootContent? lootContent = null,
        IZoneUnitUpdateServices? unitUpdateServices = null, ZoneUnitSpatialIndex? spatialIndex = null,
        Func<ZoneUnit, System.Numerics.Vector3>? modelPosition = null, IZoneUnitMovementServices? movementServices = null)
    {
        var startup = await ZoneWorldClient.SynchronizeAsync(connection, join, timeout, cancellationToken, contentNameComparer,
            localGameMilliseconds: unitUpdateServices is null ? null : () => unitUpdateServices.TimeMilliseconds).ConfigureAwait(false);
        try
        {
            return new(connection, startup, statContent, npcWorld, battlefieldZone, npcContent, relationshipWorldFactory,
                worldContent, achievementContent, lootContent, unitUpdateServices, spatialIndex, modelPosition, movementServices);
        }
        catch
        {
            startup.CloseClocks();
            throw;
        }
    }

    /// <summary>
    /// Consumes complete frames until EOF or cancellation. No ZoneLoaded notification is sent:
    /// snapshot ownership does not yet provide the simulation required for native readiness.
    /// A validated simulation transfers to this receive loop and is disposed after producers stop.
    /// The caller retains ownership of the stream and frame source.
    /// </summary>
    public async Task ReceiveAsync(CancellationToken cancellationToken = default, IZoneFrameSource? frames = null,
        ZoneSimulationTick? simulation = null)
    {
        if (_scene is not null)
        {
            _scene.EnsureCompleted();
            if (simulation is not null && !ReferenceEquals(simulation, _scene.Simulation))
                throw new ArgumentException("The adopted scene owns its exact simulation tick.", nameof(simulation));
            simulation = _scene.Simulation;
        }
        if (simulation is not null)
        {
            if (frames is null) throw new ArgumentException("Simulation updates require a frame source.", nameof(frames));
            ObjectDisposedException.ThrowIf(simulation.IsDisposed, simulation);
            if (!ReferenceEquals(simulation.Registry, Registry))
                throw new ArgumentException("The simulation belongs to a different Zone session.", nameof(simulation));
        }
        if (frames is not null && simulation is null && Registry.Updates is not null)
            throw new InvalidOperationException("Scheduled unit updates require the complete configured simulation tick.");
        if (frames is not null && simulation is null && Registry.Npcs is null)
            throw new InvalidOperationException("Scheduled NPC updates require an NPC scene.");
        if (Interlocked.Exchange(ref _receiving, 1) != 0)
            throw new InvalidOperationException("The Zone session receive loop can only run once.");
        using var sceneStop = _scene is null ? null : CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _scene.BeginReceive());
        var receiveCancellation = sceneStop?.Token ?? cancellationToken;
        Exception? receiveError = null;
        try
        {
            if (frames is not null)
                await ReceiveScheduledAsync(frames, simulation, receiveCancellation).ConfigureAwait(PreserveOwnerContext);
            else while (await ZoneFrame.ReadAsync(_connection, receiveCancellation).ConfigureAwait(PreserveOwnerContext) is { } frame)
            {
                receiveCancellation.ThrowIfCancellationRequested();
                Receive(frame);
                await FlushNotificationsAsync(receiveCancellation).ConfigureAwait(PreserveOwnerContext);
            }
        }
        catch (OperationCanceledException) when (_scene?.IsRetired == true && !cancellationToken.IsCancellationRequested) { }
        catch (Exception error) { receiveError = error; throw; }
        finally
        {
            // Cancellation can consume part of a frame. A terminated connection must not resume
            // or dispatch new frames into the old session, even if its snapshots are inspected.
            IsClosed = true;
            MarkDisconnect(receiveError, receiveCancellation);
            Registry.CloseNotifications();
            List<Exception>? cleanupErrors = null;
            if (_scene is not null)
            {
                try { await _scene.EndReceiveAsync().ConfigureAwait(PreserveOwnerContext); }
                catch (Exception error) { (cleanupErrors ??= []).Add(error); }
            }
            else
            {
                Cleanup(() => GameSchedules?.Dispose());
                Cleanup(() => Registry.Npcs?.Stop());
                Cleanup(Registry.StopMovement);
                Cleanup(() => simulation?.Dispose());
                Cleanup(() => Registry.Updates?.Dispose());
                Cleanup(() => Registry.Spatial?.Dispose());
                Cleanup(Startup.CloseClocks);
            }
            if (cleanupErrors is not null)
            {
                if (receiveError is not null) cleanupErrors.Insert(0, receiveError);
                if (cleanupErrors.Count == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cleanupErrors[0]).Throw();
                throw new AggregateException("Zone session shutdown failed.", cleanupErrors);
            }
            void Cleanup(Action action)
            {
                try { action(); } catch (Exception error) { (cleanupErrors ??= []).Add(error); }
            }
        }
    }

    private async Task ReceiveScheduledAsync(IZoneFrameSource frames, ZoneSimulationTick? simulation, CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<ZoneFrame?>? incoming = null;
        Task<uint?>? tick = null;
        Exception? processingError = null;
        var worldPacketProcessed = false;
        try
        {
            while (true)
            {
                incoming ??= ZoneFrame.ReadAsync(_connection, stop.Token).AsTask();
                tick ??= frames.ReadAsync(stop.Token).AsTask();
                await Task.WhenAny(incoming, tick).ConfigureAwait(PreserveOwnerContext);
                cancellationToken.ThrowIfCancellationRequested();
                // Explicit event-loop policy: a completed packet precedes a simultaneous tick.
                // Process at most one of each per iteration so packet traffic cannot starve updates.
                if (incoming.IsCompleted)
                {
                    var message = await incoming.ConfigureAwait(PreserveOwnerContext);
                    incoming = null;
                    if (message is null) return;
                    Receive(message.Value);
                    worldPacketProcessed = true;
                    if (_scene?.IsRetired == true) return;
                    await FlushNotificationsAsync(stop.Token).ConfigureAwait(PreserveOwnerContext);
                }
                if (tick.IsCompleted)
                {
                    var frame = await tick.ConfigureAwait(PreserveOwnerContext);
                    tick = null;
                    if (frame is null) return;
                    cancellationToken.ThrowIfCancellationRequested();
                    if (simulation is null) Registry.Npcs!.Update(frame.Value);
                    else
                    {
                        // Frame-aware tick branch (registered member scope): with a frame
                        // coordinator the ONE global frame (PrePhase → StepPhysics → PostPhase →
                        // Pump → ZoneSimulationTick) replaces the bare simulation.Update inside
                        // the same guarded dispatch — packet-before-tick, retirement checks and
                        // the flush below stay byte-for-byte the original order. The RUNNER supplies
                        // the simulation tick (ZoneSceneRunner), which is what makes this branch
                        // reachable at all; without a tick a scene keeps the bare NPC update.
                        if (_scene is null) simulation.Update(frame.Value);
                        else if (_frame is not null) _scene.Dispatch(() => _frame.RunFrame(frame.Value, worldPacketProcessed), ticking: true);
                        else _scene.Dispatch(() => simulation.Update(frame.Value), ticking: true);
                        if (_scene?.IsRetired == true) return;
                        if (simulation.IsDisposed) return;
                    }
                    await FlushNotificationsAsync(stop.Token).ConfigureAwait(PreserveOwnerContext);
                    worldPacketProcessed = false;
                }
            }
        }
        catch (Exception error) { processingError = error; throw; }
        finally
        {
            // Finish both producers before releasing actors. A partial packet read cannot outlive
            // this connection or dispatch into a replacement session. Preserve the initiating error.
            Exception? cancellationError = null;
            try { await stop.CancelAsync().ConfigureAwait(PreserveOwnerContext); }
            catch (Exception error) { cancellationError = error; }
            await ObserveAsync(incoming).ConfigureAwait(PreserveOwnerContext);
            await ObserveAsync(tick).ConfigureAwait(PreserveOwnerContext);
            if (cancellationError is not null)
            {
                if (processingError is not null)
                    throw new AggregateException("Zone processing and producer cancellation failed.", processingError, cancellationError);
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cancellationError).Throw();
            }
        }

        static async Task ObserveAsync(Task? pending)
        {
            if (pending is null) return;
            try { await pending.ConfigureAwait(false); }
            catch (Exception) { /* The terminal receive/clock error is propagated by the main loop. */ }
        }
    }

    private async Task FlushNotificationsAsync(CancellationToken cancellationToken)
    {
        while (Registry.TryDequeueNotification(out var notification))
            await _connection.WriteAsync(notification.Encode(), cancellationToken).ConfigureAwait(PreserveOwnerContext);
    }

    private void Receive(ZoneFrame frame)
    {
        if (_scene is null) ReceiveCore(frame);
        else _scene.Dispatch(() => ReceiveCore(frame));
    }

    private void ReceiveCore(ZoneFrame frame)
    {
        // Native WZ handlers (registered by 3936F740) always return 1: log and drop. A per-packet failure
        // is counted per opcode and never ends the session. Cancellation and retirement still propagate.
        try
        {
            if (!PassesLevelGate(frame)) return;
            DispatchCore(frame);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (!IsClosed && _scene?.IsRetired != true)
        {
            RecordHandlerFault(frame.Opcode, error);
        }
    }

    private void DispatchCore(ZoneFrame frame)
    {
        switch (frame.Opcode)
        {
            case WzOpcodes.GmCommand:
                Registry.ApplyGmCommand(frame.Body.Span);
                break;
            case WzOpcodes.DominionData:
            case WzOpcodes.DominionDeleted:
            case WzOpcodes.SiegeState:
            case WzOpcodes.SiegeMember:
            case WzOpcodes.SiegeSecondHalf:
                (RelationshipWorld ?? throw new NotSupportedException("Siege updates require a configured relationship scene."))
                    .SiegeState.Apply(frame);
                break;
            case WzOpcodes.ConflictZoneState:
                var conflict = ConflictZoneState.Read(frame.Body.Span);
                (RelationshipWorld ?? throw new NotSupportedException("Conflict updates require a configured relationship scene."))
                    .ChangeConflictState(conflict);
                break;
            case WzOpcodes.CvFCombatRelationship:
                CombatRelations.ApplyCharacterChanges(frame.Body.Span);
                break;
            case WzOpcodes.FvFCombatRelationship:
                CombatRelations.ApplyFactionChanges(frame.Body.Span);
                break;
            case WzOpcodes.FactionCreated:
                Factions.Create(frame.Body.Span);
                break;
            case WzOpcodes.FactionSponsorChanged:
                Factions.ChangeSponsor(frame.Body.Span);
                break;
            case WzOpcodes.FactionDismissed:
                Factions.Dismiss(frame.Body.Span);
                break;
            case WzOpcodes.FactionSetRelationState:
                Factions.ChangeRelation(frame.Body.Span);
                break;
            case WzOpcodes.UnitFactionChanged:
                Registry.ChangeFaction(frame.Body.Span);
                break;
            case WzOpcodes.UnitExpeditionChanged:
                Registry.ChangeExpedition(frame.Body.Span);
                break;
            case WzOpcodes.ExpeditionWarState:
                Factions.ChangeWar(frame.Body.Span);
                break;
            case WzOpcodes.FactionIndependence:
                Factions.DeclareIndependence(frame.Body.Span);
                break;
            case WzOpcodes.CreateDoodad:
                Doodads.Create(frame.Body.Span);
                break;
            case WzOpcodes.RemoveDoodad:
                Doodads.Remove(frame.Body.Span);
                break;
            case WzOpcodes.DoodadChangePhase:
                Doodads.ChangePhase(frame.Body.Span);
                break;
            case WzOpcodes.VegetationCutdown:
                Doodads.CutDownVegetation(frame.Body.Span);
                break;
            case WzOpcodes.NpcState:
                RequireNpcs().Create(frame.Body.Span);
                break;
            case WzOpcodes.NpcSpawnFailed:
                RequireNpcs().SpawnFailed(frame.Body.Span);
                break;
            case WzOpcodes.NpcSpawnerEvent:
                RequireNpcs().SpawnerEvent(frame.Body.Span);
                break;
            case WzOpcodes.GameScheduleStart:
            case WzOpcodes.GameScheduleContinue:
            case WzOpcodes.GameScheduleEnd:
                var schedule = GameScheduleMessage.Read(frame.Opcode, frame.Body.Span);
                GameSchedules?.Handle(schedule); // Native ignores these before its post-load readiness gate.
                break;
            case WzOpcodes.TowerDefReload: TowerDefs?.Reload(TowerDefReload.Read(frame.Body.Span)); break;
            case WzOpcodes.TowerDefQueryPlayability:
                if (TowerDefs?.QueryPlayability(TowerDefQueryPlayability.Read(frame.Body.Span)) is { } playability) Registry.QueueNotification(playability);
                break;
            case WzOpcodes.TowerDefStart: TowerDefs?.Start(TowerDefSpotMessage.Read(frame.Body.Span)); break;
            case WzOpcodes.TowerDefEnd: TowerDefs?.End(TowerDefSpotMessage.Read(frame.Body.Span)); break;
            case WzOpcodes.TowerDefWaveStart: TowerDefs?.WaveStart(TowerDefWaveStart.Read(frame.Body.Span)); break;
            case WzOpcodes.UnitMovement:
                // Movement-model admission seam (registered member scope): nothing in production
                // constructed ZoneActorMovementController before this host, and the registry's
                // model gate refuses by name (ZoneUnitRegistry.cs:254). Bind the Character's
                // controller first (idempotent, frame coordinator), then the real receive —
                // decode-then-receive mirrors the registry's own decode-first order.
                var movementMessage = UnitMovement.Read(frame.Body.Span);
                if (Registry.Units.TryGetValue(movementMessage.ObjectId, out var movingUnit))
                    _scene?.Frame?.EnsureMovementModel(movingUnit);
                Registry.ReceiveMovement(frame.Body.Span);
                break;
            case WzOpcodes.UpdateAggro:
                RequireNpcs().UpdateAggro(frame.Body.Span);
                break;
            case WzOpcodes.AggroReset:
                RequireNpcs().ResetAggro(frame.Body.Span);
                break;
            case WzOpcodes.AggroCopy:
                RequireNpcs().CopyAggro(frame.Body.Span);
                break;
            case WzOpcodes.FakeDeath:
                RequireNpcs().FeignDeath(frame.Body.Span);
                break;
            case WzOpcodes.InteractNpc:
            case WzOpcodes.InteractNpcEnd:
                RequireNpcs().Interact(frame.Body.Span, frame.Opcode == WzOpcodes.InteractNpcEnd);
                break;
            case WzOpcodes.UnitState:
                // Admission-line seam (registered member scope): LOGS ONLY — registration keeps its
                // exact position, and admission-before-publication (PlayerEnterService.cs:100-120)
                // plus the client's self-build remain untouched. Decode first (registry does the
                // same), create, then emit the admitted/refused line for Characters.
                var admission = UnitStateSnapshot.Read(frame.Body.Span);
                var isPlayer = admission.Identity.Kind == Protocol.Units.ZoneUnitKind.Character;
                try
                {
                    var created = Registry.Create(frame.Body.Span);
                    if (isPlayer)
                    {
                        if (created == UnitCreationResult.Created)
                        {
                            var admitted = _scene?.Frame?.PlayerActors.FindByUnitObjectId(admission.ObjectId);
                            Console.WriteLine($"Player admitted unit={admission.ObjectId} gen={admitted?.Handle.Generation.ToString() ?? "n/a"} physicalId={admitted?.PhysicalId.ToString() ?? "n/a"}");
                            // Registration with the scene's physics backend belongs to ADMISSION, not to
                            // the first movement frame: a client that logs in and stands still sends no
                            // movement frame, and the backend would then never learn the player exists
                            // (observed live: the owned authority stepped with `actors=0` while the
                            // player was already in-world).
                            _scene?.Frame?.RegisterAdmittedPlayer(admission.ObjectId);
                        }
                        else
                            Console.WriteLine($"Player admission refused: duplicate object id {admission.ObjectId} (UnitCreationResult.Duplicate)");
                    }
                }
                catch (NotSupportedException refusal)
                {
                    // A NAMED admission refusal (an unresolved dependency gate such as the
                    // IGame+0xE8 character-spawn callback) belongs to THIS player, not to the zone.
                    // The first real client login proved the difference: rethrowing it retired the
                    // whole host seconds after ZWZoneLoaded, taking the zone offline for everyone.
                    // The unit never existed (the factory rolled its own admission back), so the
                    // refusal is counted, named, and the session keeps serving other players.
                    AdmissionRefusalCount++;
                    FirstAdmissionRefusal ??= refusal.Message;
                    Console.WriteLine($"Player admission refused by name (count {AdmissionRefusalCount}) unit={admission.ObjectId}: {refusal.Message}");
                }
                catch (ZonePlayerAdmissionRefusedException refusal)
                {
                    // Same containment, different cause: an admission that fails for a reason owned
                    // by THIS player (an unresolved capability, or an identity space the native
                    // allocator and this world cannot currently agree on) is counted, named, and the
                    // session keeps serving. Before this, one character trying to log back in after a
                    // logout retired the whole host and took the zone offline for everyone.
                    AdmissionRefusalCount++;
                    FirstAdmissionRefusal ??= refusal.Message;
                    Console.WriteLine($"Player admission refused by name (count {AdmissionRefusalCount}) unit={admission.ObjectId}: {refusal.Message}");
                }
                catch (Exception error)
                {
                    if (isPlayer) Console.WriteLine($"Player admission failed: {error.Message}");
                    throw;
                }
                break;
            case WzOpcodes.UnitRemoved:
                // Logout retire seam (registered member scope, plan B4): run the frame
                // coordinator's RemoveActor → factory.Retire order while the unit is still the
                // exact live owner; the registry removal then resolves the already-retired
                // generation as its documented no-op and never detaches a replacement.
                var removedObjectId = new Protocol.ZoneReader(frame.Body.Span).ReadBc();
                _scene?.Frame?.RemovePlayer(removedObjectId);
                Registry.Remove(frame.Body.Span);
                break;
            case WzOpcodes.UnitPoints:
                Registry.UpdatePoints(frame.Body.Span);
                break;
            case WzOpcodes.UnitDeath:
                Registry.Kill(frame.Body.Span);
                break;
            case WzOpcodes.UnitResurrection:
                Registry.Resurrect(frame.Body.Span);
                break;
            case WzOpcodes.CombatEngaged:
                Registry.EngageCombat(frame.Body.Span);
                break;
            case WzOpcodes.CombatCleared:
                Registry.ClearCombat(frame.Body.Span);
                break;
            case WzOpcodes.ForceAttackSet:
                Registry.SetForceAttack(frame.Body.Span);
                break;
            case WzOpcodes.ActivateNpcSpawnersInArea:
                var area = ActivateNpcSpawnersInArea.Read(frame.Body.Span);
                Spawners?.ActivateInArea(area.Center, area.Radius, area.Activate);
                break;
            case WzOpcodes.UnitFlyingStateChanged:
                Registry.ChangeFlyingState(frame.Body.Span);
                break;
            case WzOpcodes.TargetChanged:
                Registry.ChangeTarget(frame.Body.Span);
                break;
            case WzOpcodes.UnitDuelState:
                Registry.ChangeDuel(frame.Body.Span);
                break;
            case WzOpcodes.UnitEquipmentChanged:
                Registry.ChangeEquipment(frame.Body.Span);
                break;
            case WzOpcodes.UnitEquipmentsRndAttrUnitModifierAvtivateChanged:
                Registry.ChangeEquipmentActivation(frame.Body.Span);
                break;
            case WzOpcodes.BuffCreated:
                Registry.CreateBuff(frame.Body.Span);
                break;
            case WzOpcodes.BuffUpdated:
                Registry.UpdateBuff(frame.Body.Span);
                break;
            case WzOpcodes.BuffRemoved:
                Registry.RemoveBuff(frame.Body.Span);
                break;
            case WzOpcodes.BuffLearned:
                Registry.LearnBuff(frame.Body.Span);
                break;
            case WzOpcodes.SkillsReset:
                Registry.ResetSkills(frame.Body.Span);
                break;
            case WzOpcodes.AddSwapPassiveBuffs:
                Registry.AddSwapPassives(frame.Body.Span);
                break;
            case WzOpcodes.LevelChanged:
                Registry.ChangeLevel(frame.Body.Span);
                break;
            case WzOpcodes.TimeOfDay:
            case WzOpcodes.DetailedTimeOfDay:
            case WzOpcodes.WorldGameTime:
                Startup.Receive(frame);
                break;
            case WzOpcodes.SkillStarted:
            {
                var message = SkillStarted.Read(frame.Body.Span);
                Registry.ApplySkillStarted(message); Skills.Start(message); break;
            }
            case WzOpcodes.SkillFired:
            {
                var message = SkillFired.Read(frame.Body.Span);
                Registry.ApplySkillFired(message); Skills.Fire(message); break;
            }
            case WzOpcodes.SkillEnded:
            {
                var message = SkillEnded.Read(frame.Body.Span);
                Registry.ApplySkillEnded(message); Skills.End(message); break;
            }
            case WzOpcodes.SkillStopped:
            {
                var message = SkillStopped.Read(frame.Body.Span);
                Registry.ApplySkillStopped(message); Skills.Stop(message); break;
            }
            case WzOpcodes.CastingStopped:
            {
                var message = CastingStopped.Read(frame.Body.Span);
                Registry.ApplyCastingStopped(message);
                Skills.StopCasting(message);
                break;
            }
            case WzOpcodes.SkillCooldownReset: Registry.ResetCooldowns(SkillCooldownReset.Read(frame.Body.Span)); break;
            case WzOpcodes.SkillCooldownReduce: Registry.ReduceCooldowns(SkillCooldownReduce.Read(frame.Body.Span)); break;
            case WzOpcodes.ChangeChargeSkillCooldown: Registry.ChangeChargeCooldown(ChargeSkillCooldownChange.Read(frame.Body.Span)); break;
            case WzOpcodes.UnitDamaged: Skills.Damage(UnitDamaged.Read(frame.Body.Span)); break;
            case WzOpcodes.UnitHealed: Skills.Heal(UnitHealed.Read(frame.Body.Span)); break;
            case WzOpcodes.PlotEvent:
            {
                var message = PlotEvent.Read(frame.Body.Span);
                Registry.ApplyPlotEvent(message); Skills.Plot(message); break;
            }
            case WzOpcodes.PlotEnded:
            {
                var message = PlotEnded.Read(frame.Body.Span);
                Registry.ApplyPlotEnded(message); Skills.PlotEnd(message); break;
            }
            case WzOpcodes.PlotCastingStopped:
            {
                var message = PlotStopped.Read(frame.Body.Span);
                Registry.ApplyPlotCastingStopped(message); Skills.PlotCastingStop(message); break;
            }
            case WzOpcodes.PlotChannelingStopped:
            {
                var message = PlotStopped.Read(frame.Body.Span);
                Registry.ApplyPlotChannelingStopped(message); Skills.PlotChannelingStop(message); break;
            }
            default:
                // Named, counted refusal — see UnimplementedOpcodes. The first occurrence of each
                // opcode is printed so the gap is visible in the live host log, and the session
                // continues instead of retiring the zone over one unknown packet.
                _unimplementedOpcodes.TryGetValue(frame.Opcode, out var seen);
                _unimplementedOpcodes[frame.Opcode] = seen + 1;
                if (seen == 0)
                    Console.WriteLine($"Zone simulation opcode 0x{frame.Opcode:X4} is not implemented by this wave; " +
                        $"refused by name and counted (total distinct: {_unimplementedOpcodes.Count}).");
                break;
        }
    }

    /// <summary>World's doodads (WZ 0x0073-0x0076); state only, declared after DispatchCore to keep line citations stable.</summary>
    public ZoneDoodadRegistry Doodads { get; } = new();

    /// <summary>World's skill, plot and heal/damage feedback (WZ 0x002B-0x0031, 0x003A-0x003D); state only.</summary>
    public ZoneSkillState Skills => _skills ??= new(id => Registry.Units.ContainsKey(id));
    private ZoneSkillState? _skills;

    private ZoneNpcSimulation RequireNpcs() => Registry.Npcs ??
        throw new NotSupportedException("NPC simulation requires configured content and actor/spatial services.");
}

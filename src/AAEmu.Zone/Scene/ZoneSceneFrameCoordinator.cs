using System.Numerics;

using AAEmu.Zone.Actors;
using AAEmu.Zone.Ai;
using AAEmu.Zone.Hosting;
using AAEmu.Zone.Physics;
using AAEmu.Zone.Units;
using AAEmu.Zone.WorldLink;

namespace AAEmu.Zone.Scene;

/// <summary>
/// One scene generation's global frame composition — Agent 5's registered host file
/// (IMPLEMENTATION_OWNERSHIP.md section 6). Exactly one instance exists per scene generation;
/// the scene factory sets <see cref="ZoneSceneCoordinator.Frame"/> after level completion and the
/// adopted session calls <see cref="RunFrame"/> from its tick branch instead of the bare
/// <see cref="ZoneSimulationTick.Update(uint)"/> path.
///
/// One global frame, in the exact contract order
/// (Actors/IZoneMovementFramePhases.cs:52-64, plan section 3):
/// <code>
/// packet dispatch (incl. ReceiveMovement) → flush
/// tick:  PrePhase(frame) → StepPhysics(elapsed) → PostPhase(frame) → aiPump.Pump(serverMs)
///        → simulation.Update(frame) → flush (ZoneSession.cs:257)
/// </code>
/// Ordering rationale, all cited: packet-before-tick puts this frame's movement input before
/// PrePhase/Step (IZoneMovementFramePhases.cs:64-69); PostPhase precedes
/// <c>simulation.Update</c> so <c>UpdateUnitLocationReports</c> reads published post-phase state
/// (IZoneMovementFramePhases.cs:70-74); the AI pump runs after PostPhase and before the tick so
/// due AI work lands in the same frame and <c>UpdateNpcManager</c> observes it (pump discipline
/// IZoneAiFramePump.cs:8-12); the session flushes after dispatch so this frame's ZWUnitMovements
/// batch goes on the wire in the same iteration.
///
/// Clock domains, sampled EXACTLY ONCE per frame here (never mixed, IZoneMovementFramePhases.cs:
/// 9-34): <c>ServerMilliseconds</c> = the scene clock
/// (<c>ZoneStartupState.WorldGameClock.GetTime()</c>, the World-aligned CTimer domain — never
/// server uptime, ZoneWorldGameClock.cs:60-72); <c>GameSeconds</c> = the same sample divided by
/// 1000 (same documented domain); <c>Elapsed</c> = the configured frame period, validated
/// 0 &lt; period ≤ 10 s at ZoneSceneActivation.cs:81-82; <c>Frame</c> = the session's
/// IZoneFrameSource counter (unsigned wrap). <c>PrePhase</c> pushes the record into the shared
/// <see cref="ZoneMovementFrameClock"/> before staging actors (ZoneMovementFramePhases.cs:104-107),
/// so no leaf samples its own time source. Native physics time is a third domain that advances
/// only inside ZoneNativeTerrainPhysicsWorld.Step — reached here solely through StepPhysics.
///
/// Exactly one StepPhysics per frame: the phases object refuses a second step inside an open
/// frame BY NAME (ZoneMovementFramePhases.cs:24-26) and the native world keeps its own busy
/// guard (ZoneNativeTerrainPhysicsWorld.cs:238-239).
///
/// Retirement: this coordinator is registered in the scene's ZoneSceneResourceScope LAST by the
/// host, so LIFO disposal releases it (and, through it, the AI provider) before the movement
/// stack, activation and engine — after ZoneSession has already drained both producers
/// (ZoneSession.cs:262-277 → EndReceiveAsync → Cleanup, ZoneSceneCoordinator.cs:269-276).
/// </summary>
public sealed class ZoneSceneFrameCoordinator : IDisposable
{
    private readonly IZoneMovementFramePhases _phases;
    private readonly IZonePhysicsBackend _backend;
    private readonly ZonePlayerActorFactory _playerActors;
    private readonly IZoneAiFramePump? _ai;
    private readonly ZoneMovementServices _movement;
    private readonly ZoneSimulationTick _simulation;
    private readonly ZoneStartupState _startup;
    private readonly TimeSpan _framePeriod;
    private readonly Action<string>? _trace;
    private readonly Action<TimeSpan>? _scriptTick;
    private readonly Action<TimeSpan>? _beginFrame;
    private bool _disposed;

    /// <param name="phases">The scene's one Agent-3 concrete triple coordinator (PrePhase/StepPhysics/PostPhase).</param>
    /// <param name="backend">The scene's one physics adapter; StepPhysics maps 1:1 onto its single Step.</param>
    /// <param name="playerActors">The scene's one player lifecycle — retire order seam for <see cref="RemovePlayer"/>.</param>
    /// <param name="ai">The scene's selected AI provider. Pumped once here and disposed during frame retirement when it owns resources.</param>
    /// <param name="movement">The concrete IZoneActorMovementServices shared with the controllers this coordinator binds.</param>
    /// <param name="simulation">The scene's tick; RunFrame completes the frame by updating it after the pump.</param>
    /// <param name="startup">Connection clocks sampled once per frame (never a wall clock of our own).</param>
    /// <param name="framePeriod">The validated explicit replacement scheduling period.</param>
    public ZoneSceneFrameCoordinator(IZoneMovementFramePhases phases, IZonePhysicsBackend backend,
        ZonePlayerActorFactory playerActors, IZoneAiFramePump? ai, ZoneMovementServices movement,
        ZoneSimulationTick simulation, ZoneStartupState startup, TimeSpan framePeriod, Action<string>? trace,
        Action<TimeSpan>? scriptTick = null, Action<TimeSpan>? beginFrame = null)
    {
        ArgumentNullException.ThrowIfNull(phases);
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(playerActors);
        ArgumentNullException.ThrowIfNull(movement);
        ArgumentNullException.ThrowIfNull(simulation);
        ArgumentNullException.ThrowIfNull(startup);
        if (framePeriod <= TimeSpan.Zero || framePeriod > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(framePeriod),
                "The frame coordinator requires the scene's validated explicit period (0 < period ≤ 10 s, ZoneSceneActivation.cs:81-82).");
        _phases = phases;
        _backend = backend;
        _playerActors = playerActors;
        _ai = ai;
        _movement = movement;
        _simulation = simulation;
        _startup = startup;
        _framePeriod = framePeriod;
        _trace = trace;
        _scriptTick = scriptTick;
        _beginFrame = beginFrame;
    }

    /// <summary>The one player actor factory of this scene generation (admission-line evidence source).</summary>
    public ZonePlayerActorFactory PlayerActors => _playerActors;
    /// <summary>The scene's selected AI pump, sharing the frame clock.</summary>
    public IZoneAiFramePump? AiPump => _ai;

    /// <summary>
    /// Runs ONE complete global frame on the scene owner thread (inside Dispatch, which is
    /// single-threaded and non-reentrant — ZoneSceneCoordinator.cs:242-249): one clock sample,
    /// PrePhase → StepPhysics → PostPhase → Pump → ZoneSimulationTick. Throws nothing benign;
    /// any failure propagates through Dispatch and retires the exact scene + connection.
    /// </summary>
    public void RunFrame(uint frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // ONE sample for every leaf of this frame (pump discipline, IZoneAiFramePump.cs:8-12).
        var serverMs = _startup.WorldGameClock.GetTime();
        var gameSeconds = serverMs / 1000f; // same documented millisecond domain, seconds view
        var sample = new ZoneMovementFrame(frame, _framePeriod, serverMs, gameSeconds);
        _beginFrame?.Invoke(_framePeriod);

        _phases.PrePhase(sample);           // eligibility, sampling, input production (clock pushed here)
        _phases.StepPhysics(_framePeriod);  // EXACTLY one Step per frame; <= 0 refused by name downstream
        _phases.PostPhase(sample);          // correction, CommitPose, spatial, entity mirror, ZW encode
        if (_trace is not null)
            foreach (var actor in _registeredActors)
                if (_reportedActorStatus.Add(actor))
                {
                    _backend.CopyStatus(actor, out var status);
                    _trace($"Player physics status unit={actor.UnitObjectId} gen={actor.Generation} local=({status.Position.X}, {status.Position.Y}, {status.Position.Z}) grounded={status.IsGrounded}");
                }
        _scriptTick?.Invoke(_framePeriod);  // managed entity timers/proxies; exact-owner callback faults retire their NPC
        _ai?.Pump(serverMs);                // one AI authority; post-phase state observed by the tick
        _simulation.Update(frame);          // ZoneSimulationTick phases (own clocks/gates inside)
    }

    /// <summary>
    /// The logout seam (plan B4): the MANDATED retire order backend.RemoveActor → factory.Retire
    /// (IZoneActorLifecycle.cs:80-82) lives ONLY here so no other call site can invert it.
    /// Called from the session's UnitRemoved dispatch while the unit is still the exact live
    /// owner: RemoveActor first (idempotent for never-adopted bodies), then Retire, whose stage 2
    /// performs the registry's exact-owner RemovePrepared — so the subsequent
    /// ZoneUnitRegistry.Remove for this object ID resolves as the already-removed no-op it is,
    /// and a replacement owner of the same numeric ID is never detached.
    /// Unknown/absent/retired generations are a no-op (players only; every other unit kind is
    /// not tracked by the player factory).
    /// </summary>
    public void RemovePlayer(uint unitObjectId)
    {
        if (_disposed) return;
        if (_playerActors.FindByUnitObjectId(unitObjectId) is not { IsLive: true } actor) return;
        _backend.RemoveActor(actor.Handle); // mandated BEFORE factory.Retire (IZoneActorLifecycle.cs:80-82)
        _registeredActors.Remove(actor.Handle);
        _reportedActorStatus.Remove(actor.Handle);
        _trace?.Invoke($"Player actor retiring unit={actor.Handle.UnitObjectId} gen={actor.Handle.Generation} (backend.RemoveActor -> factory.Retire)");
        _playerActors.Retire(actor.Handle);
    }

    /// <summary>
    /// The movement-model admission seam for this scene: inbound WzOpcodes.UnitMovement reaches
    /// ZoneUnitRegistry.ReceiveMovement, whose model gate refuses by name
    /// (ZoneUnitRegistry.cs:254) when the unit owns no movement model. Nothing in production
    /// constructed ZoneActorMovementController before this host, so the session calls this first
    /// for Character owners; NPCs take the named refusal (their motion is not client-relayed).
    /// Idempotent: an already-bound model is never replaced
    /// (ZoneUnitRegistry.BindMovementModel :216-225).
    /// </summary>
    public void EnsureMovementModel(ZoneUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (_disposed) throw new ObjectDisposedException(nameof(ZoneSceneFrameCoordinator));
        if (unit.MovementModel is not null) return;
        if (unit.Snapshot.Identity.Kind != Protocol.Units.ZoneUnitKind.Character)
            return; // non-Character inbound movement keeps the registry's named refusal (only Characters are relayed)
        var controller = new ZoneActorMovementController(_simulation.Registry, unit, _movement);
        _simulation.Registry.BindMovementModel(controller);
        _trace?.Invoke($"Movement model bound unit={unit.Snapshot.ObjectId} gen={_playerActors.FindByUnitObjectId(unit.Snapshot.ObjectId)?.Handle.Generation ?? 0}");
    }

    /// <summary>
    /// Registers an ADMITTED player with the scene's physics backend, once per generation. This is
    /// the seam the backend contract expects and the product was missing: the player's body is
    /// created by the factory straight through the physics world, so nothing ever told the BACKEND
    /// the player exists, and the frame stepped the owned authority with zero actors. Shape and
    /// mass come from the authored actor model (`physics_living_mass`, stance sizes) - never a
    /// default invented here.
    /// </summary>
    public void RegisterAdmittedPlayer(uint unitObjectId)
    {
        if (_disposed) return;
        var actor = _playerActors.FindByUnitObjectId(unitObjectId);
        if (actor is null) return; // not a player actor: not this backend's business.
        var handle = actor.Handle;
        if (!_registeredActors.Add(handle)) return; // already registered for this generation
        try
        {
            ZonePhysicsActorDescription description;
            if (_backend is ZoneManagedPhysicsBackend)
            {
                // Reuse the recovered 392DF300/392E2F20 source-order/default-stance and entity-scale
                // mapping. This builds copied values only; no native surface or engine is needed.
                var initial = ZoneActorPhysicalizationRequest.BuildInitial(actor.Model, actor.Scale, false, _ => null);
                description = ZonePhysicsActorDescription.FromLivingRequest(initial);
            }
            else
            {
                // The comparison/native backend retains its existing adoption contract.
                var definition = actor.Model.Definition;
                var stance = actor.Model.Stances.FirstOrDefault(row => row.StanceId == 2);
                var scale = Math.Max(actor.Scale, 0.0001f);
                var halfExtents = new Vector3(
                    Math.Max((stance?.SizeX ?? 0.5f) * scale, 0.0001f),
                    Math.Max((stance?.SizeY ?? 0.5f) * scale, 0.0001f),
                    Math.Max((stance?.SizeZ ?? 0.5f) * scale, 0.0001f));
                var mass = definition.PhysicsLivingMass > 0 ? definition.PhysicsLivingMass : definition.PhysicsMass;
                description = new(new(halfExtents, true), mass);
            }
            if (!float.IsFinite(description.Mass) || description.Mass <= 0)
                throw new InvalidOperationException(
                    $"Character unit {unitObjectId} has no finite positive authored living mass after entity scaling " +
                    $"(mass={description.Mass}); the backend refuses to invent one.");
            _backend.AddActor(handle, description);
            // The owned authority starts at the origin until the scene poses it; pose it immediately
            // from the actor's admitted transform so the first published frame is the real position.
            _backend.Teleport(handle, actor.Position, actor.Orientation);
            actor.BindPhysicsAuthority(_backend, description);
            if (_simulation.Registry.Units.TryGetValue(unitObjectId, out var unit)) EnsureMovementModel(unit);
            _trace?.Invoke($"Backend registered unit={handle.UnitObjectId} gen={handle.Generation} mass={description.Mass} " +
                $"halfExtents=({description.Shape.HalfExtents.X}, {description.Shape.HalfExtents.Y}, {description.Shape.HalfExtents.Z}) " +
                $"shape={description.Shape.Kind} centerOffsetZ={description.CenterOffsetZ} pivotHeight={description.GroundPivotHeight} gravity={description.Gravity}");
        }
        catch
        {
            _registeredActors.Remove(handle);
            throw;
        }
    }

    private readonly HashSet<ZoneActorHandle> _registeredActors = [];
    private readonly HashSet<ZoneActorHandle> _reportedActorStatus = [];

    /// <summary>
    /// Retires backend registrations and player bodies/entities before the scene's terrain,
    /// entity services and engine. AI retirement is still attempted if a player cleanup fails.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        List<Exception>? errors = null;
        try
        {
            _playerActors.RetireAll(_backend);
            _trace?.Invoke($"Scene player actors retired count={_playerActors.LiveCount}");
        }
        catch (Exception error) { (errors ??= []).Add(error); }
        _registeredActors.Clear();
        _reportedActorStatus.Clear();
        try { (_ai as IDisposable)?.Dispose(); }
        catch (Exception error) { (errors ??= []).Add(error); }
        if (errors is not null) throw new AggregateException("Scene frame retirement failed.", errors);
    }
}

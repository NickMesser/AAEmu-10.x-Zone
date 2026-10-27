using System.Numerics;
using AAEmu.Zone.Content;
using AAEmu.Zone.Protocol.Messages;
using AAEmu.Zone.Simulation.Contracts;

namespace AAEmu.Zone.Simulation;

/// <summary>Structured dependency status for the player actor factory (ZoneNativeAiDependency pattern, Simulation/ZoneNativeAiProvider.cs:20).</summary>
public sealed record ZonePlayerActorDependency(string Name, bool IsResolved, string Evidence);

/// <summary>F3 exhaustion accounting: one structured diagnostic per reserved-physical-ID collision.</summary>
public sealed record ZonePlayerPhysicalIdCollision(uint UnitObjectId, uint Generation, uint EntityId,
    ushort PhysicalId, int Attempt, int ReservedIdentityCount);

/// <summary>
/// Player actor lifecycle for one scene generation, resolving TODO(player-factory) and
/// TODO(player-rollback) (IMPLEMENTATION_OWNERSHIP.md section 3).
///
/// Split of responsibility, binding on consumers:
/// <list type="bullet">
/// <item><c>IZoneActorLifecycle.Create/Retire/IsLive</c> are the managed identity surface
/// (Simulation/Contracts/IZoneActorLifecycle.cs:53-63): duplicate-live create is rejected,
/// Generation = last + 1 per identity, retire is terminal and idempotent, unknown handles are
/// never live. Create allocates no native resource — it compiles and runs in managed-only scenes.</item>
/// <item>The registry hook <c>Admit(ZoneUnitRegistry, ZoneUnit, UnitStateSnapshot)</c>
/// (called from ZoneUnitRegistry.Create's Character branch) runs the full native admission:
/// model resolution → entity → physicalization request → fresh-physical-ID living body
/// (bounded respawn) → initial pose publish → flow player binding. Any stage failure unwinds
/// only the completed stages in F1 order and the registry's exact-owner catch removes the unit.</item>
/// <item>Re-entry is a FRESH unused native identity (plan section 4): every claimed ushort
/// physical ID stays reserved until world teardown
/// (Simulation/ZoneNativeTerrainPhysicsWorld.Living.cs:59-63), so each new generation spawns a
/// new native entity, pre-checks IsPhysicalIdReserved, and retries a fresh spawn up to
/// <see cref="MaxPhysicalIdAttempts"/> times before failing with an explicit exhaustion error
/// that names the reserved-ID count. The reservation guard is never weakened and no ID is ever
/// released early.</item>
/// <item>Every removal step verifies reference identity of the exact owner before detaching
/// (registry IsCurrent :196, flow maps :169-171, entity lookup-vs-captured-pointer :187-196);
/// an old generation can never remove a replacement owner of the same numeric ID.</item>
/// </list>
/// Native components (entity services, physics world, engine) are one all-or-nothing scene
/// dependency; supplying none yields a managed-only factory. Agent 5 wires exactly one factory
/// per scene generation and passes it to the ZoneUnitRegistry constructor.
/// </summary>
public sealed class ZonePlayerActorFactory : IZoneActorLifecycle
{
    internal const int MaxPhysicalIdAttempts = 8;
    internal const string ClassPublicationDependency = "P1 canonical player-class publication";
    internal const string ActorPublicationDependency = "GameObject/ActorSystem/AnimatedCharacter composition";
    internal const string PlayerOperationsDependency = "real P1 sensor/operations ABI";
    internal const string SurfaceResolverDependency = "engine physical-surface resolver (3DEngine+6A0, Agent 1 collision supply)";
    internal const string LivingTeleportDependency = "native living-body teleport seam";

    private const string ClassPublicationEvidence =
        "canonical token = actor +1F0 -> 39302950 -> literal 39FC0ED0 (Simulation/ZoneNativeEngine.FlowActors.cs:182-191); " +
        "RECOVERED 2026-09-24 (AAEmuResearch/ZoneAuthority/pose-write/decompiled-p1-class + refs-p1): 39302950 is " +
        "'lea rax,[rip+..]; ret' returning the string \"CPlayer\" at 0x39FC0ED0 - both the address and the string are " +
        "confirmed. That string is CPlayer's RTTI type name and the +1F0 slot is the class-NAME operation, not an " +
        "opaque token. CPlayer vtable = 0x39FD4E58 (its +1F0 slot 0x39FD5048 is the only reference to 39302950); " +
        "constructor FUN_39301ca0 sets CPlayer::vftable on the object and six sub-objects and initialises the sound " +
        "event names \"foley_player\"/\"bodyfall\"; factory FUN_3929e580 is operator_new(0xB78) then construct, so a " +
        "CPlayer is 0xB78 bytes; the factory has NO code callers, only DATA references, because it is published " +
        "through an RTTI-keyed class registry (RTTI_Complete_Object_Locator 0x39FC1888 -> factory 3929E580, destroy " +
        "3929E520). The class owner is DOWNSTREAM of actor publication: a real CPlayer must exist before its +1F0 " +
        "name can be read. " +
        "no producer registers an IZoneNativeFlowPlayerClassOwner through ZoneNativeFlowActorRegistry.RegisterPlayerClass (Simulation/ZoneNativeFlowActorRegistry.cs:78-95); " +
        "the referenced handoff unit-first-player-resource-handoff.md and evidence/unit-first-player-resource-contract.json are not supplied with this source snapshot. " +
        "TODO(player-lifecycle): resolve CPlayer in the RTTI-keyed class registry, create it, capture its canonical name token, register the real owner, then flip IsResolved.";
    private const string ActorPublicationEvidence =
        "plain Default-class entities never publish to ActorSystem (Simulation/ZoneNativeEntityServices.cs:7-9); " +
        "the composition item 'entity, GameObject, ActorSystem, AnimatedCharacter and physics ownership' is still unchecked (FIRST_ENTRY_NOTES.md:1390-1392); " +
        "CaptureFlowActorIdentity therefore returns null for the admitted entity (Simulation/ZoneNativeEngine.FlowActors.cs:113). " +
        "TODO(player-lifecycle): publish the admitted entity through GameObject/ActorSystem, then flip IsResolved.";
    private const string PlayerOperationsEvidence =
        "listener add/remove ABI 39302320/393008C0 (Simulation/ZoneNativeFlowNodes.Sensors.cs:18-19) and the staging operations at actor +340/+217/+218/+258 and 392FDC10 " +
        "(Simulation/ZoneNativeFlowNodes.Player.cs:23-36) have no bound call site in the working tree. " +
        "RECOVERED 2026-09-24 (decompiled-p1-class/FUN_39301ec0): the CPlayer destructor removes listeners through a " +
        "vtable call at actor +800 (0x320) with ids 1..12 in a loop, then tears down a listener vector at actor +0x9F0 " +
        "(3 elements of 0x18 bytes, dtor iterator FUN_3986f060), an ISoundSystemEventListener sub-object at actor +0x9B8, " +
        "and a VoiceListener removal at actor[1] vtable +0x158. So the listener set is ids 1-12 held in an owned " +
        "0x9F0 vector - that is the concrete shape the add/remove ABI must satisfy, and the sound event names " +
        "\"foley_player\"/\"bodyfall\" are initialised at actor +0x3EC/+0x42C by the constructor. " +
        "TODO(player-lifecycle): bind the real listener add/remove and staging call sites against that shape, then flip IsResolved.";
    private const string LivingTeleportEvidence =
        "IZonePhysicsBackend.Teleport (Simulation/Contracts/IZonePhysicsBackend.cs:77) is contract-only in the working tree; " +
        "ZoneNativeTerrainPhysicsWorld binds no living position-parameter write — only the simulation/flag/dimensions/dynamics buffers at Simulation/ZoneNativeTerrainPhysicsWorld.Living.cs:114-128. " +
        "Publishing only the entity transform would silently diverge body and transform, so teleport refuses by name. " +
        "TODO(player-lifecycle): wire the native reposition call site (Agent 3/5 backend adapter seam), then flip IsResolved.";

    private static string SurfaceResolverEvidence(Func<string, ushort?>? resolver) => resolver is not null
        ? "supplied by scene wiring at factory construction; ZoneActorPhysicalizationRequest.BuildInitial admits exactly this delegate (Simulation/ZoneActorPhysicalizationRequest.cs:110-114)."
        : "required engine surface lookup boundary 3DEngine+6A0/manager+80/surface+08 (Simulation/ZoneActorPhysicalizationRequest.cs:105-109), sourced from Agent 1's collision/surface supply (IMPLEMENTATION_OWNERSHIP.md section 7) through scene wiring; absent, physicalization refuses by name (D2 gate, never a stub).";

    private readonly Dictionary<ZoneActorHandle, ZonePlayerActor> _live = [];
    private readonly Dictionary<uint, ZonePlayerActor> _byObjectId = [];
    private readonly Dictionary<uint, uint> _generations = [];
    private readonly List<ZonePlayerPhysicalIdCollision> _physicalIdCollisions = [];
    private readonly ZoneModelContent _models;
    private ZoneNativeEntityServices? _entities;
    private ZoneNativeTerrainPhysicsWorld? _physics;
    private ZoneNativeEngine? _engine;
    private bool _nativeTripleAttached;
    /// <summary>The scene's physics world, exposed for retirement-stage identity release only.</summary>
    internal ZoneNativeTerrainPhysicsWorld? Physics => _physics;
    // Not readonly: the D2 resolver is attached with the native triple, because the scene that owns
    // the physics world is the same scene that loaded the surface catalog the resolver reads.
    private Func<string, ushort?>? _physicalSurfaceResolver;
    private int _retired;
    private int _physicalSpawnAttempts;
    private int _physicalIdExhaustions;

    /// <summary>Generations that are live right now (retired and rolled-back admissions excluded).</summary>
    public int LiveCount => _live.Count;
    /// <summary>B1 retired counter: completed seals, including rolled-back admissions.</summary>
    public int RetiredCount => _retired;
    /// <summary>F3: native entity spawns attempted for physical-ID admission across all generations.</summary>
    public int PhysicalSpawnAttempts => _physicalSpawnAttempts;
    /// <summary>F3: admissions that exhausted <see cref="MaxPhysicalIdAttempts"/> fresh spawns.</summary>
    public int PhysicalIdExhaustionCount => _physicalIdExhaustions;
    /// <summary>F3: one structured diagnostic per reserved-physical-ID collision, in observation order.</summary>
    public IReadOnlyList<ZonePlayerPhysicalIdCollision> PhysicalIdCollisions => _physicalIdCollisions;

    /// <param name="models">Model table for named resolution before any allocation (B3).</param>
    /// <param name="entities">Native entity services; null = managed-only scene.</param>
    /// <param name="physics">Native physics world owning the living bodies.</param>
    /// <param name="engine">The engine lease that roots the entity services and flow registry.</param>
    /// <param name="physicalSurfaceResolver">
    /// Engine surface lookup for BuildInitial; null in a native scene is the D2 dependency gate —
    /// refused by name at admission, never replaced by a fabricated lookup.
    /// </param>
    public ZonePlayerActorFactory(ZoneModelContent models, ZoneNativeEntityServices? entities = null,
        ZoneNativeTerrainPhysicsWorld? physics = null, ZoneNativeEngine? engine = null,
        Func<string, ushort?>? physicalSurfaceResolver = null)
    {
        ArgumentNullException.ThrowIfNull(models);
        var nativeCount = (entities is null ? 0 : 1) + (physics is null ? 0 : 1) + (engine is null ? 0 : 1);
        if (nativeCount is not (0 or 3))
            throw new ArgumentException("Native entity services, the physics world and the owning engine are one all-or-nothing scene dependency " +
                "(entity services are rooted in the engine lease); supply all three or none.", nameof(entities));
        if (nativeCount == 0 && physicalSurfaceResolver is not null)
            throw new ArgumentException("A physical-surface resolver requires the native physics world it belongs to.", nameof(physicalSurfaceResolver));
        _models = models;
        _entities = entities;
        _physics = physics;
        _engine = engine;
        _physicalSurfaceResolver = physicalSurfaceResolver;
    }

    /// <summary>
    /// One-shot late attachment of the native triple (entity services, physics world, owning
    /// engine). The scene composition constructs this factory BEFORE the activation can Create
    /// (the coordinator needs the registry services first), so a managed-only instance is
    /// completed exactly once, on the owner thread, after the scene load — never concurrently
    /// with an admission and never with a second replacement. The same constructor invariant
    /// applies: all three arrive together, or the factory stays managed-identity only.
    /// </summary>
    internal void AttachNativeTriple(ZoneNativeEntityServices entities, ZoneNativeTerrainPhysicsWorld physics,
        ZoneNativeEngine engine, Func<string, ushort?>? physicalSurfaceResolver = null)
    {
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(physics);
        ArgumentNullException.ThrowIfNull(engine);
        if (_nativeTripleAttached)
            throw new InvalidOperationException("The native triple is already attached to this scene's player factory; " +
                "the engine admits one entity borrower and one physics world per scene generation.");
        if (physicalSurfaceResolver is not null && _physicalSurfaceResolver is not null)
            throw new InvalidOperationException("The physical-surface resolver is already attached to this scene's player factory.");
        _nativeTripleAttached = true;
        _entities = entities;
        _physics = physics;
        _engine = engine;
        // The resolver belongs to the same scene generation as the physics world it was read from.
        _physicalSurfaceResolver = physicalSurfaceResolver ?? _physicalSurfaceResolver;
    }

    /// <summary>Whether native admission is armed for this scene (the triple is attached).</summary>
    internal bool NativeTripleAttached => _nativeTripleAttached;

    /// <summary>
    /// Managed identity admission per Simulation/Contracts/IZoneActorLifecycle.cs:67-73: player
    /// kind only, named model rejection and pose validation BEFORE any allocation, duplicate-live
    /// rejection (there is no silent replacement — retire first), then Generation = last + 1 so
    /// handle equality never survives retirement. No native resource is allocated here; the
    /// registry hook <see cref="Admit"/> runs the native stages.
    /// </summary>
    public ZoneActorHandle Create(ZoneActorCreationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Kind != ZoneActorKind.Player)
            throw new ArgumentException($"The player actor factory admits {ZoneActorKind.Player} generations only; " +
                $"unit {request.UnitObjectId} requested {request.Kind} (NPC admission stays with ZoneNpcActorRegistry).", nameof(request));
        var model = RequirePlayerModel(request.ModelId);
        ValidateRequestPose(request);
        if (_byObjectId.TryGetValue(request.UnitObjectId, out var live))
            throw new InvalidOperationException($"Unit {request.UnitObjectId} already has live player generation {live.Handle}; " +
                "re-entry never silently replaces a live actor — retire first (IZoneActorLifecycle.cs:55-56).");
        var generation = _generations.GetValueOrDefault(request.UnitObjectId) + 1;
        var handle = new ZoneActorHandle(request.UnitObjectId, generation);
        var actor = new ZonePlayerActor(this, handle, model, request.Position, request.Orientation, request.Scale);
        _live.Add(handle, actor);
        _byObjectId.Add(request.UnitObjectId, actor);
        _generations[request.UnitObjectId] = generation;
        return handle;
    }

    /// <summary>
    /// Permanently invalidates one generation through the full F1 stage order (the caller's
    /// backend-first precondition is satisfied internally: the physical body is disposed before
    /// the seal). Idempotent; unknown or already-retired handles are a no-op; stage failures
    /// aggregate after every stage has been attempted.
    /// </summary>
    public void Retire(ZoneActorHandle actor) => Find(actor)?.Retire();

    /// <summary>Whether the exact generation is still live. Retired and unknown handles return false.</summary>
    public bool IsLive(ZoneActorHandle actor) => _live.ContainsKey(actor);

    public ZonePlayerActor? Find(ZoneActorHandle actor) => _live.GetValueOrDefault(actor);

    /// <summary>Re-entry refresh lookup: the current live generation for a logical world object ID, or null.</summary>
    public ZonePlayerActor? FindByUnitObjectId(uint unitObjectId) => _byObjectId.GetValueOrDefault(unitObjectId);

    /// <summary>Structured dependency status; the sole authority on whether a native stage can run (Agent 4 pattern).</summary>
    public IReadOnlyList<ZonePlayerActorDependency> ReportDependencies() => new ZonePlayerActorDependency[]
    {
        new(ClassPublicationDependency, false, ClassPublicationEvidence),
        new(ActorPublicationDependency, false, ActorPublicationEvidence),
        new(PlayerOperationsDependency, false, PlayerOperationsEvidence),
        new(SurfaceResolverDependency, _physicalSurfaceResolver is not null, SurfaceResolverEvidence(_physicalSurfaceResolver)),
        new(LivingTeleportDependency, false, LivingTeleportEvidence),
    };

    // ------------------------------------------------------------------
    // Registry admission hook (phase C/F): full native path with F1-F6 rollback.
    // ------------------------------------------------------------------

    /// <summary>
    /// Character-branch admission called by ZoneUnitRegistry.Create inside its existing try/catch.
    /// Managed identity first (model/pose gates, duplicate-live rejection), then the native stages.
    /// A stage failure unwinds only the completed stages in F1 order and rethrows the original
    /// error (aggregating rollback errors when the unwind itself reports failures); the registry's
    /// exact-owner catch then removes the unit if it is still the exact registered owner.
    /// </summary>
    internal ZonePlayerActor Admit(ZoneUnitRegistry registry, ZoneUnit unit, UnitStateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!ReferenceEquals(unit.Snapshot, snapshot))
            throw new ArgumentException("Player admission requires the exact snapshot that produced the registry unit.", nameof(snapshot));
        if (snapshot.Identity.Kind != Protocol.Units.ZoneUnitKind.Character)
            throw new ArgumentException($"Player admission accepts Character snapshots only; unit {snapshot.ObjectId} is {snapshot.Identity.Kind}.", nameof(snapshot));
        var pose = ZoneUnitPose.FromSnapshot(snapshot);
        const double fixedToWorld = 5.684341886080802e-14; // 2^-54; conversion precedent ZoneActorMovementCorrectionMath.World :21-22
        var position = new Vector3((float)(pose.Position.FixedX * fixedToWorld),
            (float)(pose.Position.FixedY * fixedToWorld), pose.Position.Z);
        var handle = Create(new ZoneActorCreationRequest(snapshot.ObjectId, ZoneActorKind.Player, snapshot.ModelId,
            position, pose.Rotation, snapshot.Scale));
        var actor = _live[handle];
        actor.AttachAdmission(registry, unit);
        try
        {
            NativeAdmission(actor, snapshot);
            return actor;
        }
        catch (Exception error)
        {
            // F2-F5: unwind only the stages that completed; every stage inside Retire is
            // idempotent/exact-owner, the seal always runs, and the original failure stays primary.
            List<Exception>? cleanup = null;
            try { actor.Retire(); }
            catch (Exception retireError) { (cleanup ??= []).Add(retireError); }
            if (cleanup is null) throw; // original failure with its original stack
            var failures = new List<Exception> { error };
            failures.AddRange(cleanup);
            throw new AggregateException(
                $"Player admission for unit {snapshot.ObjectId} generation {handle.Generation} rolled back after failure.", failures);
        }
    }

    /// <summary>
    /// Exact-owner rollback for a failed Character admission (registry catch, F6): retires only
    /// the live generation whose attached unit IS this exact unit. A replacement owner of the same
    /// numeric ID is never detached (registry IsCurrent policy, Simulation/ZoneUnitRegistry.cs:196).
    /// </summary>
    internal void RollbackAdmitted(ZoneUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (_byObjectId.TryGetValue(unit.Snapshot.ObjectId, out var actor) &&
            ReferenceEquals(actor.Unit, unit) && actor.IsLive)
            actor.Retire();
    }

    // ------------------------------------------------------------------
    // Native create path (phase D).
    // ------------------------------------------------------------------

    private void NativeAdmission(ZonePlayerActor actor, UnitStateSnapshot snapshot)
    {
        if (_entities is null) return; // managed-only scene: no native services exist by constructor invariant
        CreateLivingBody(actor, snapshot);
        var entity = actor.Entity ??
            throw new InvalidOperationException("Player admission returned without a native entity.");
        var body = actor.Body ??
            throw new InvalidOperationException("Player admission returned without a native living body.");
        // D5: initial pose publish — the body's native status drives the entity transform
        // (Simulation/ZoneNativeEntityServices.Pose.cs:31-65). Spatial registration is not run
        // here; it remains the registry's exact-owner RegisterSpatial stage after Admit returns.
        var status = body.GetStatus();
        entity.PublishPose(status.Position, status.Orientation);
        // E4: flow binding only after the body exists; duplicate keys are rejected by the registry itself.
        //
        // PARTIAL ADMISSION (owned physics only). The flow binding publishes the player into the
        // game's ActorSystem — the P1 class publication, the AnimatedCharacter composition and the
        // sensor/operations ABI. With the OWNED backend the transform is the host's, so refusing
        // this stage would remove the player from the zone entirely and the owned authority would
        // never see it (observed live: body admitted, unit rolled back, backend stepped with
        // actors=0). The admission therefore COMPLETES as a named partial one: the body, the entity
        // and the pose are real, the missing publication is counted and reported, and nothing
        // pretends the native actor exists. Under the NATIVE backend the refusal still stands,
        // because there the engine - not this host - is the movement authority and a body without
        // its actor would be driven by a frame pump that does not own it.
        try
        {
            BindFlowBinding(actor, entity);
        }
        catch (NotSupportedException refusal) when (_allowUnboundFlowActor)
        {
            FlowBindingRefusalCount++;
            FirstFlowBindingRefusal ??= refusal.Message;
            Console.WriteLine($"Player admitted with an UNBOUND flow actor (count {FlowBindingRefusalCount}) unit={snapshot.ObjectId}: {refusal.Message}");
        }
    }

    /// <summary>Partial admissions whose native flow actor was not published, counted per player.</summary>
    public int FlowBindingRefusalCount { get; private set; }

    /// <summary>The first unbound-flow-actor refusal, reported verbatim at readiness.</summary>
    public string? FirstFlowBindingRefusal { get; private set; }

    /// <summary>
    /// Whether a player may complete admission WITHOUT the native flow actor. Set only by the
    /// owned-physics scene, where the host is the movement authority; never set for the native
    /// backend, whose engine-owned body needs its actor.
    /// </summary>
    internal bool AllowUnboundFlowActor
    {
        get => _allowUnboundFlowActor;
        set => _allowUnboundFlowActor = value;
    }

    private bool _allowUnboundFlowActor;

    /// <summary>
    /// D1-D4: spawn the native entity, pre-check the physical-ID reservation with the read-only
    /// mirror, build the physicalization request (D2 resolver gate), and create the living body.
    /// A reserved ushort identity disposes that entity and respawns a fresh native entity, bounded
    /// by <see cref="MaxPhysicalIdAttempts"/>; exhaustion fails with an explicit error naming the
    /// reserved-ID count. Non-collision body failures propagate (the world captures its own fault,
    /// Simulation/ZoneNativeTerrainPhysicsWorld.Living.cs:133) and the admission rollback disposes
    /// the entity — a faulted world cannot host a successful respawn, so only the reserved-ID
    /// branch retries.
    /// </summary>
    private void CreateLivingBody(ZonePlayerActor actor, UnitStateSnapshot snapshot)
    {
        var entities = _entities ?? throw new InvalidOperationException("Native player admission requires entity services (factory constructor invariant).");
        var physics = _physics ?? throw new InvalidOperationException("Native player admission requires the native physics world (factory constructor invariant).");
        for (var attempt = 1; ; attempt++)
        {
            if (actor.Entity is null)
            {
                // D1/F4: fresh spawn (first attempt or collision respawn). Input validation runs
                // before any allocation and a normal failure self-rollbacks
                // (Simulation/ZoneNativeEntityServices.cs:49-96, :87-95) — poisoned engines rethrow untouched.
                //
                // The capability gate comes FIRST because the unbound slot is FATAL, not merely
                // unimplemented: the first real client login (Agent 6, 2026-09-23) reached
                // CreateInitialCharacter -> IGame+0xE8 and the zone host process died with
                // "Unsupported native IGame callback +0xE8. This isolated engine must terminate."
                // Refusing here keeps the failure named and recoverable: the admission rolls back,
                // World returns the client to character select, and the zone keeps serving.
                if (_engine is not null && !_engine.CharacterSpawnCallbackPublished)
                    throw new NotSupportedException(
                        "Player admission is refused by name: this engine does not publish the IGame+0xE8 " +
                        "character-spawn callback, and entering it terminates the process (ZoneNativeEngine." +
                        "GameServices binds every unbound IGame slot to a fatal placeholder). TODO(player-lifecycle): " +
                        "implement the real callback, then admission takes this path. No entity, body or transform " +
                        "is created and nothing is faked.");
                actor.AttachEntity(entities.CreateInitialCharacter(actor.Model, snapshot.Name,
                    actor.Position, actor.Orientation, actor.Scale));
                _physicalSpawnAttempts++;
            }
            var entity = actor.Entity!;
            // D3: exact read-only mirror of the reservation guard (Living.cs:75-78); the guard
            // itself and the "reserved until world teardown" policy are untouched.
            if (physics.IsPhysicalIdReserved(entity.Id))
            {
                Collision(actor, entity.Id, attempt);
                continue;
            }
            // D2: the resolver gate fires with this attempt's entity already created (F3: + entity).
            var resolver = RequireSurfaceResolver();
            var request = ZoneActorPhysicalizationRequest.BuildInitial(actor.Model, actor.Scale,
                IsLocalActorIdentity(entity.Id), resolver);
            try
            {
                actor.AttachBody(physics.CreateLivingEntity(request, actor.Position, actor.Orientation, entity.Id, entity));
            }
            catch (Exception error) when (error.Message == ZoneNativeTerrainPhysicsWorld.ReservedIdentityMessage)
            {
                Collision(actor, entity.Id, attempt); // unreachable single-threaded after the exact pre-check; retained as the named F4 branch
                continue;
            }
            // Admission evidence: the BOUND body's class byte decides whether the recovered living
            // move action (body vtable +0x50 = 3502A300) applies inline or defers to the native
            // frame pump — the producer only takes the inline branch when body+0x47 > 6
            // (AAEmuResearch/ZoneAuthority/pose-write). The unbound probe body reads 0x03; this line
            // is the live-host observation of the real player body.
            try
            {
                var pointer = actor.Body?.Pointer ?? 0;
                var bodyClass = pointer == 0 ? (byte)0 : System.Runtime.InteropServices.Marshal.ReadByte(pointer + 0x47);
                Console.WriteLine($"Player body admitted: entityId={entity.Id} physicalId={actor.Body?.PhysicalEntityId} " +
                    $"body=0x{pointer:X} classByte=0x{bodyClass:X2} " +
                    $"actionInline={(bodyClass > 6 ? "ARMED" : "DEFERRED (native frame pump)")}");
            }
            catch (Exception error) { Console.WriteLine($"Player body admission evidence unavailable: {error.Message}"); }
            return; // D4 complete: the body is bound to this fresh entity's exact full ID.
        }

        void Collision(ZonePlayerActor target, uint entityId, int attemptNumber)
        {
            var reserved = physics.ReservedPhysicalIdCount;
            _physicalIdCollisions.Add(new(target.Handle.UnitObjectId, target.Handle.Generation, entityId,
                unchecked((ushort)entityId), attemptNumber, reserved));
            // Bounded, because the decision this evidence feeds (release-on-retire vs allocator
            // skipping) must be made from WHICH id the engine actually hands back, not from a guess:
            // a constant id means the allocator is deterministic and re-spawning can never help.
            if (attemptNumber <= 3)
                Console.WriteLine($"Physical-ID collision unit={target.Handle.UnitObjectId} gen={target.Handle.Generation} " +
                    $"attempt={attemptNumber} nativeEntityId={entityId} ushort={unchecked((ushort)entityId)} reserved={reserved}");
            target.ClearEntity(); // F4: dispose the colliding entity, then respawn a fresh native ID
            if (attemptNumber < MaxPhysicalIdAttempts) return;
            _physicalIdExhaustions++;
            // Per-player, not per-zone: this failure belongs to ONE character, and letting it reach
            // the session's catch-all retired the whole host and dropped the zone for everyone
            // (measured: zone 129 went offline on a single re-entry). The zone's identity spaces are
            // independent - the native allocator reissues an id this world reserves forever - so the
            // refusal is named and contained rather than thrown as a server fault.
            throw new ZonePlayerAdmissionRefusedException(
                $"Player physical-ID admission exhausted {attemptNumber} fresh native entity spawns for unit {target.Handle.UnitObjectId} " +
                $"generation {target.Handle.Generation}: every spawned entity truncated to an already-reserved ushort identity; " +
                $"{reserved} ushort identities remain reserved until the owning world is released (Simulation/ZoneNativeTerrainPhysicsWorld.Living.cs:59-63). " +
                "The native entity allocator and this world's reserved-identity set are independent, so a re-entry of the " +
                "same character deterministically lands on its own retired id. TODO(re-entry-identity): establish whether " +
                "releasing a retired actor's reservation is safe with the engine's deferred destruction, or have the entity " +
                "allocator skip reserved identities. Per-attempt diagnostics: ZonePlayerActorFactory.PhysicalIdCollisions.");
        }
    }

    /// <summary>
    /// D2 dependency gate: BuildInitial's required engine surface lookup boundary. Absent, player
    /// physicalization refuses by name with the dependency evidence (Agent 4 pattern) — never a
    /// fabricated lookup and never a silent default.
    /// </summary>
    private Func<string, ushort?> RequireSurfaceResolver() =>
        _physicalSurfaceResolver ?? throw Blocked("Player physicalization", SurfaceResolverDependency, SurfaceResolverEvidence(null));

    /// <summary>
    /// 392E2F20's local-actor identity comparison belongs to the caller
    /// (Simulation/ZoneActorPhysicalizationRequest.cs:101-104). Without the P1 class owner the
    /// framework observation cannot exist (Simulation/ZoneNativeEngine.FlowActors.cs:147-151), so
    /// a plain, unpublished entity is provably not the local actor — the exact comparison result
    /// is false, not a guess. Once publication lands this compares the admitted entity's captured
    /// actor pointer against the engine's client-actor observation.
    /// </summary>
    private bool IsLocalActorIdentity(uint entityId)
    {
        var engine = _engine;
        if (engine is null) return false;
        var registry = engine.GetFlowActorRegistry();
        if (!registry.HasPlayerClassOwner) return false;
        var identity = engine.CaptureFlowActorIdentity(entityId);
        var observation = engine.GetFlowClientActorObservation();
        return identity is not null && identity.Actor == observation.Actor;
    }

    // ------------------------------------------------------------------
    // Flow player owner + class-owner dependency gate (phase E).
    // ------------------------------------------------------------------

    /// <summary>
    /// E4/E2: publishes the admitted actor into the real flow registry. The dependency table is
    /// required first — while any entry is unresolved, binding fails with a named
    /// <see cref="NotSupportedException"/> listing each missing entry and its evidence. No class
    /// token, identity or listener owner is ever fabricated (Simulation/ZoneNativeFlowActorRegistry.cs:6-10).
    /// </summary>
    private void BindFlowBinding(ZonePlayerActor actor, ZoneNativeEntityServices.Entity entity)
    {
        var engine = _engine ??
            throw new InvalidOperationException("Native player admission requires the owning engine (factory constructor invariant).");
        var registry = engine.GetFlowActorRegistry();
        RequireFlowDependencies();
        var playerClass = registry.PlayerClassOwner ??
            throw new InvalidOperationException($"The dependency table reports {ClassPublicationDependency} resolved but no " +
                "IZoneNativeFlowPlayerClassOwner is registered (ZoneNativeFlowActorRegistry.RegisterPlayerClass); " +
                "complete the TODO call site before flipping IsResolved.");
        var owner = new FlowPlayerActorOwner(engine, entity.Id, playerClass);
        actor.Binding = registry.BindPlayer(owner);
    }

    private void RequireFlowDependencies()
    {
        var missing = new List<string>();
        foreach (var dependency in ReportDependencies())
            if ((dependency.Name is ClassPublicationDependency or ActorPublicationDependency or PlayerOperationsDependency) &&
                !dependency.IsResolved)
                missing.Add($"{dependency.Name} [{dependency.Evidence}]");
        if (missing.Count != 0)
            throw new NotSupportedException("Player flow binding (GameObject/ActorSystem publication) is blocked at unresolved native dependencies: " +
                string.Join(" | ", missing));
    }

    private static NotSupportedException Blocked(string operation, string dependency, string evidence) =>
        new($"{operation} is blocked at unresolved native dependency: {dependency} [{evidence}]");

    /// <summary>
    /// The concrete P1-owned player lifetime (IZoneNativeFlowPlayerActorOwner,
    /// Simulation/ZoneNativeFlowActorRegistry.cs:34-49). Construction is reachable only after
    /// <see cref="RequireFlowDependencies"/> passes. The identity is captured after native
    /// publication, Lifetime is unique and immutable for that allocation, and Sensor/Operations
    /// refuse every operation by name until their real ABI call sites are wired — there is
    /// deliberately no stand-in owner and no fabricated behavior behind these interfaces.
    /// AnimatedCharacter ownership stays with the slot-zero character/skeleton captured by
    /// ZoneNativeEntityServices.CaptureCharacter (:145-157); skeleton physicalization is out of
    /// scope this wave and plain-body ownership remains enforced by RequirePlainPhysicsOwner (:170-176).
    /// </summary>
    private sealed class FlowPlayerActorOwner : IZoneNativeFlowPlayerActorOwner
    {
        private readonly ZoneNativeEngine _engine;
        private readonly ZoneNativeEngine.FlowActorIdentity _identity;
        private readonly IZoneNativeFlowPlayerClassOwner _playerClass;
        private readonly object _lifetime = new();
        private readonly FlowPlayerSensor _sensor = new();
        private readonly FlowPlayerOperations _operations = new();

        internal FlowPlayerActorOwner(ZoneNativeEngine engine, uint entityId, IZoneNativeFlowPlayerClassOwner playerClass)
        {
            _engine = engine;
            _playerClass = playerClass;
            _identity = engine.CaptureFlowActorIdentity(entityId) ??
                throw new InvalidOperationException($"Entity {entityId} has no published ActorSystem registration; " +
                    "P1 admission requires an engine-issued actor identity.");
        }

        public ZoneNativeEngine.FlowActorIdentity Identity => _identity;
        public object Lifetime => _lifetime;
        public IZoneNativeFlowPlayerClassOwner PlayerClass => _playerClass;
        public IZoneNativeFlowActorSensorPlayer Sensor => _sensor;
        public IZoneNativeFlowPlayerOperations Operations => _operations;

        public void ValidateRetainedActor() => _engine.ValidateFlowActorIdentity(_identity);

        public void VerifyFlowListenersRetired() => _sensor.VerifyListenersRetired();
    }

    /// <summary>
    /// Listener owner for the real player (native 39302320 adds once by listener identity, 393008C0
    /// removes that exact listener if present — Simulation/ZoneNativeFlowNodes.Sensors.cs:17-23).
    /// The ABI has no bound call site, so registration refuses by name; removal preserves the
    /// native "if present" semantics as a managed no-op for absent listeners. No callback path is
    /// synthesized: an empty retained set is what VerifyFlowListenersRetired proves.
    /// </summary>
    private sealed class FlowPlayerSensor : IZoneNativeFlowActorSensorPlayer
    {
        private readonly HashSet<IZoneNativeFlowActorSensorListener> _listeners = new(ReferenceEqualityComparer.Instance);

        public void AddListener(IZoneNativeFlowActorSensorListener listener)
        {
            ArgumentNullException.ThrowIfNull(listener);
            throw Blocked("Player sensor listener registration", PlayerOperationsDependency, PlayerOperationsEvidence);
        }

        public void RemoveListener(IZoneNativeFlowActorSensorListener listener)
        {
            ArgumentNullException.ThrowIfNull(listener);
            if (!_listeners.Contains(listener)) return; // native 393008C0: an absent listener is a no-op
            // Unreachable while registration is gated: a retained listener implies the recovered ABI.
            throw Blocked("Player sensor listener removal", PlayerOperationsDependency, PlayerOperationsEvidence);
        }

        internal void VerifyListenersRetired()
        {
            if (_listeners.Count != 0)
                throw new InvalidOperationException("Flow sensor listeners remain retained by the actual player producer.");
        }
    }

    /// <summary>
    /// The five real player operations (Simulation/ZoneNativeFlowNodes.Player.cs:21-37). Each
    /// member refuses by name with the dependency evidence until the native call sites
    /// (actor +340/+217/+218/+258, 392FDC10) are wired — caching inputs or returning invented
    /// values would diverge from the actual CPlayer state the nodes consume.
    /// </summary>
    private sealed class FlowPlayerOperations : IZoneNativeFlowPlayerOperations
    {
        public Quaternion ReadStagingRotation() =>
            throw Blocked("Player staging rotation read (actor +340)", PlayerOperationsDependency, PlayerOperationsEvidence);

        public byte DrawHiddenState =>
            throw Blocked("Player draw-hidden read (actor +217)", PlayerOperationsDependency, PlayerOperationsEvidence);

        public void SetDrawHiddenState(bool hidden) =>
            throw Blocked("Player draw-hidden write (actor +217/+218)", PlayerOperationsDependency, PlayerOperationsEvidence);

        public void LinkToEntity(uint targetEntityId, bool keepTransformOnDetach) =>
            throw Blocked("Player link operation (actor +258)", PlayerOperationsDependency, PlayerOperationsEvidence);

        public void ApplyStaging(bool enabled, ZoneNativeFlowPlayerStagingParameters parameters) =>
            throw Blocked("Player staging application (392FDC10)", PlayerOperationsDependency, PlayerOperationsEvidence);
    }

    // ------------------------------------------------------------------
    // Managed identity bookkeeping (internal only so ZonePlayerActor's Retire stage 5 can seal;
    // external callers always go through the contract Retire/ZonePlayerActor.Retire surface).
    // ------------------------------------------------------------------

    internal void Seal(ZoneActorHandle handle)
    {
        if (!_live.Remove(handle, out var actor)) return; // idempotent: a sealed/unknown handle is a no-op
        if (_byObjectId.TryGetValue(handle.UnitObjectId, out var current) && ReferenceEquals(current, actor))
            _byObjectId.Remove(handle.UnitObjectId);
        _retired++;
    }

    /// <summary>Same validation predicate as ZoneNativeEntityServices.CreateInitialCharacter (:56-59).</summary>
    private static void ValidateRequestPose(in ZoneActorCreationRequest request)
    {
        var position = request.Position;
        var orientation = request.Orientation;
        if (!Finite(position) || !Finite(new(orientation.X, orientation.Y, orientation.Z)) ||
            !float.IsFinite(orientation.W) || !float.IsFinite(orientation.LengthSquared()) ||
            orientation.LengthSquared() <= 0 || !float.IsFinite(request.Scale) || request.Scale <= 0)
            throw new ArgumentException("Entity pose must be finite, with a nonzero rotation and positive scale.", nameof(request));
    }

    private static bool Finite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    /// <summary>B3 named model resolution, precedent ZoneNpcSpawnCollision.RequireActor (:104-105); runs before any allocation.</summary>
    private ZoneActorModel RequirePlayerModel(uint modelId)
    {
        if (_models.Models.GetValueOrDefault(modelId) is not { } definition)
            throw new NotSupportedException($"Player model {modelId} has no model definition in ZoneModelContent.Models; " +
                "player admission is refused before any allocation.");
        if (definition.Kind != ZoneModelKind.Actor)
            throw new NotSupportedException($"Player model {modelId} resolves to {definition.Kind}; " +
                "player admission requires an ActorModel (ZoneModelContent.ActorForModel :115-116).");
        return _models.ActorForModel(modelId) ??
            throw new NotSupportedException($"Model {modelId} requires actor model {definition.SubtypeId} and its stance content.");
    }
}

/// <summary>
/// One live player generation: managed identity plus, in a native scene, the exact entity, the
/// physical body bound to that entity's fresh full ID, and the flow binding. All native members
/// are null in managed-only scenes. Every stage of <see cref="Retire"/> verifies reference
/// identity of the exact owner before detaching, so an old generation can never remove a
/// replacement owner of the same numeric ID.
/// </summary>
public sealed class ZonePlayerActor
{
    private readonly ZonePlayerActorFactory _factory;

    internal ZonePlayerActor(ZonePlayerActorFactory factory, ZoneActorHandle handle, ZoneActorModel model,
        Vector3 position, Quaternion orientation, float scale)
    {
        _factory = factory;
        Handle = handle;
        Model = model;
        Position = position;
        Orientation = orientation;
        Scale = scale;
    }

    /// <summary>(UnitObjectId, Generation) — the physics/AI key; equality never survives retirement.</summary>
    public ZoneActorHandle Handle { get; }
    public ZoneActorModel Model { get; }
    /// <summary>Admission pose in the WZUnitState world frame; fixed for this generation.</summary>
    public Vector3 Position { get; }
    public Quaternion Orientation { get; }
    public float Scale { get; }
    /// <summary>
    /// The registry-owned unit (movement-model owner). Null only for contract-created
    /// admissions that never passed through the registry hook (IZoneActorLifecycle.Create is the
    /// managed identity surface); every Admit generation has its exact unit attached.
    /// </summary>
    public ZoneUnit? Unit { get; private set; }
    /// <summary>Native full ID, fresh per generation; 0 while managed-only or before the native entity exists.</summary>
    public uint EntityId { get; private set; }
    /// <summary>(ushort)EntityId as truncated by the reservation guard; reserved until world teardown.</summary>
    public ushort PhysicalId => unchecked((ushort)EntityId);
    public ZoneNativeEntityServices.Entity? Entity { get; private set; }
    public ZoneNativeTerrainPhysicsWorld.LivingEntity? Body { get; private set; }
    /// <summary>Whether the exact generation is live right now.</summary>
    public bool IsLive => _factory.IsLive(Handle);
    internal ZoneUnitRegistry? Registry { get; private set; }
    internal ZoneNativeFlowActorRegistry.PlayerBinding? Binding { get; set; }

    internal void AttachAdmission(ZoneUnitRegistry registry, ZoneUnit unit)
    {
        Registry = registry;
        Unit = unit;
    }

    internal void AttachEntity(ZoneNativeEntityServices.Entity entity)
    {
        Entity = entity;
        EntityId = entity.Id;
    }

    internal void AttachBody(ZoneNativeTerrainPhysicsWorld.LivingEntity body) => Body = body;

    /// <summary>F4 collision unwind: dispose the exact entity, then clear the association (a failed dispose keeps the reference for Retire stage 4).</summary>
    internal void ClearEntity()
    {
        var entity = Entity;
        entity?.Dispose();
        Entity = null;
        EntityId = 0;
    }

    /// <summary>
    /// Copies the live body's physical status (IZonePhysicsBackend.CopyStatus value semantics:
    /// snapshots only, no backend state retained). Returns false for retired generations and for
    /// managed-only admissions, which have no native body; a disposed world still surfaces its
    /// own ObjectDisposedException rather than a fabricated result.
    /// </summary>
    public bool TryCopyStatus(out ZonePhysicsActorStatus status)
    {
        status = default;
        if (!IsLive) return false;
        var body = Body;
        if (body is null) return false;
        var native = body.GetStatus();
        // status2 exposes the native flying bit only (Simulation/ZoneNativeTerrainPhysicsWorld.Living.cs:166-179);
        // grounded is its exact complement — no separate native grounded flag is captured, and none is invented.
        status = new ZonePhysicsActorStatus(native.Position, native.Orientation, native.Velocity,
            !native.IsFlying, native.IsFlying);
        return true;
    }

    /// <summary>
    /// Body status → entity.PublishPose mirror (the explicit physics-caller publication of
    /// Simulation/ZoneNativeEntityServices.Pose.cs:25-30). Retired generations are rejected;
    /// managed-only generations have nothing to mirror and are a no-op.
    /// </summary>
    public void SyncEntityPose()
    {
        if (!IsLive)
            throw new InvalidOperationException($"Generation {Handle} is retired; its pose can no longer be published.");
        var entity = Entity;
        var body = Body;
        if (entity is null || body is null) return;
        var status = body.GetStatus();
        entity.PublishPose(status.Position, status.Orientation);
    }

    /// <summary>
    /// Scene-authoritative repositioning, refused by name while its native seam is unresolved:
    /// the only Teleport declaration in the working tree is the contract itself, and publishing
    /// only the entity transform would silently diverge body and transform. Evidence lives in
    /// the factory dependency table (LivingTeleportDependency).
    /// </summary>
    public void Teleport(Vector3 position, Quaternion orientation)
    {
        if (!IsLive)
            throw new InvalidOperationException($"Generation {Handle} is retired; scene-authoritative repositioning is rejected.");
        var dependency = ReportDependency(ZonePlayerActorFactory.LivingTeleportDependency);
        throw new NotSupportedException($"Scene-authoritative player teleport is blocked at unresolved native dependency: " +
            $"{dependency.Name} [{dependency.Evidence}]");
    }

    /// <summary>
    /// F1-order retirement, idempotent: (1) dispose the physical body — body before entity is
    /// mandatory (Simulation/ZoneNativeEntityServices.cs:102/:181) —, (2) registry RemovePrepared
    /// for the exact registered owner, (3) flow unbind of the exact binding maps, (4) native
    /// entity dispose, (5) seal the generation. Every stage always runs; a completed stage
    /// repeats as a no-op; failures aggregate after the seal (F7, precedent
    /// ZoneNpcActorRegistry.Remove :229-255). The seal makes the generation terminal even when an
    /// earlier native stage reported errors — no operation makes a retired handle live again.
    /// </summary>
    public void Retire()
    {
        if (!_factory.IsLive(Handle)) return;
        List<Exception>? errors = null;
        var body = Body;
        var releasedId = body?.PhysicalEntityId;
        if (body is not null)
            Stage("dispose physical body", () => { body.Dispose(); Body = null; });
        // Re-entry support: once this generation's body is DESTROYED and the native entity is gone,
        // its ushort identity is handed back so the same character can log in again in this world.
        // This is measured, not assumed - see ZoneNativeTerrainPhysicsWorld.ReleaseRetiredPhysicalId
        // for the allocator evidence (the engine advances the high half of its ids, so the low 16
        // bits never move) and for the guards that keep a still-used identity reserved.
        if (releasedId is { } retiredId)
            Stage("release retired physical id", () =>
            {
                if (_factory.Physics?.ReleaseRetiredPhysicalId(retiredId) == true)
                    Console.WriteLine($"Released retired physical id ushort={retiredId} for unit {Handle.UnitObjectId} " +
                        $"gen={Handle.Generation}; the identity is free for this character's re-entry.");
            });
        var registry = Registry;
        var unit = Unit;
        if (registry is not null && unit is not null)
            Stage("registry RemovePrepared (exact owner)", () => { if (registry.IsCurrent(unit)) registry.RemovePrepared(unit); });
        var binding = Binding;
        if (binding is not null)
            Stage("flow player unbind (exact binding)", () => { binding.Registry.UnbindPlayer(binding); Binding = null; });
        var entity = Entity;
        if (entity is not null)
            Stage("dispose native entity", () => { entity.Dispose(); Entity = null; EntityId = 0; });
        Stage("seal actor generation", () => _factory.Seal(Handle));
        if (errors is not null)
            throw new AggregateException($"Player actor retirement failed for generation {Handle}; " +
                "every stage was attempted and completed stages repeat as no-ops.", errors);

        void Stage(string name, Action action)
        {
            try { action(); }
            catch (Exception error)
            {
                (errors ??= []).Add(new InvalidOperationException(
                    $"Player retirement stage '{name}' failed for generation {Handle}.", error));
            }
        }
    }

    private ZonePlayerActorDependency ReportDependency(string name) =>
        _factory.ReportDependencies().First(dependency => dependency.Name == name);
}

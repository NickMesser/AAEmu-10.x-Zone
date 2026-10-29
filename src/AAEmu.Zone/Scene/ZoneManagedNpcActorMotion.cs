using System.Numerics;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Content;
using AAEmu.Zone.Hosting;
using AAEmu.Zone.Physics;
using AAEmu.Zone.Terrain;

namespace AAEmu.Zone.Scene;

public enum ZoneManagedNpcActorMotionMode { None, Swimming, Ground, Air }

/// <summary>Copied CActor statistics, distinct from the living body and its requested velocity.</summary>
public sealed record ZoneManagedNpcActorMotionState(ZoneManagedNpcActorMotionMode Mode,
    Vector3 ActorVelocity, float GroundedSeconds, float AirborneSeconds, float BlockedSeconds,
    float FallSpeed, float MaximumFallSpeed, bool LandingTransition, bool FirstLanding,
    float LandingCooldown, uint? Frame, uint? Timestamp);

/// <summary>
/// CActor392E0C80/392E0B50/392E0A70's upright, dry, static-support profile. Borrows real scene
/// owners, never Steps or writes a body/Unit pose. The initial skeleton and complete water
/// inventory certify the dry branch; unknown animated bounds are not replaced by collider bounds.
/// </summary>
public sealed partial class ZoneManagedNpcActorMotion : IDisposable
{
    private readonly ZoneManagedNpcActorScene _actors;
    private readonly ZoneManagedNpcPhysics _physics;
    private readonly ZoneManagedNpcActorConstruction _construction;
    private readonly ZoneManagedWaterQueries? _water;
    private readonly ZoneManagedCharacterSlots? _characters;
    private readonly IZoneAssetSource? _assets;
    private readonly ZoneModelContent? _models;
    private readonly ZoneTerrainHeightField? _terrain;
    private readonly ZoneEngineCVars _engineCVars;
    private readonly Dictionary<string, ZoneCharacterInitialSwimThreshold> _thresholds = new(StringComparer.Ordinal);
    private readonly Dictionary<ZoneActorHandle, ZoneManagedNpcActorMotionLease> _live = [];
    public bool IsDisposed { get; private set; }
    public int Count => _live.Count;
    public static string CapabilitySummary =>
        "Source CActor constructor/request state, dry static-ground classification, timers, landing transition, " +
        "actual actor velocity and accepted stance; animated water/swimming, jumping, fast/hard-fall effects, " +
        "moving supports, ragdoll/ladder/glide and full Puppet proxy-update alertness transfer remain unported.";

    public ZoneManagedNpcActorMotion(ZoneManagedNpcActorScene actors, ZoneManagedNpcPhysics physics,
        ZoneManagedNpcActorConstruction construction, ZoneManagedWaterQueries? water = null,
        ZoneManagedCharacterSlots? characters = null, IZoneAssetSource? assets = null,
        ZoneModelContent? models = null, ZoneTerrainHeightField? terrain = null,
        ZoneEngineCVars? engineCVars = null)
    {
        _actors = actors ?? throw new ArgumentNullException(nameof(actors));
        _physics = physics ?? throw new ArgumentNullException(nameof(physics));
        _construction = construction ?? throw new ArgumentNullException(nameof(construction));
        _water = water; _characters = characters; _assets = assets; _models = models;
        _terrain = terrain; _engineCVars = engineCVars ?? ZoneEngineCVars.Empty;
        if ((characters is null) != (assets is null))
            throw new ArgumentException("Character-slot and asset owners must be supplied together.");
    }

    public ZoneManagedNpcActorMotionLease Bind(ZoneManagedNpcActor actor)
    {
        Check(); ArgumentNullException.ThrowIfNull(actor);
        if (!ReferenceEquals(_actors.Find(actor.Handle), actor))
            throw new InvalidOperationException("CActor motion requires the exact adopted scene actor.");
        var construction = _construction.Find(actor.Handle) ??
            throw new NotSupportedException("CActor motion requires this generation's actual construction owner.");
        var body = _physics.Find(actor.Handle) ??
            throw new NotSupportedException("CActor motion requires this generation's actual living body.");
        if (!ReferenceEquals(body.Npc, actor.Npc) || !ReferenceEquals(construction.Npc, actor.Npc) ||
            construction.PhysicalizedFrame is null)
            throw new InvalidOperationException("CActor motion must follow its exact NPC physicalization.");
        if (_live.TryGetValue(actor.Handle, out var previous))
        {
            if (previous.IsCurrent) throw new InvalidOperationException("This CActor generation already owns motion statistics.");
            previous.Dispose();
        }
        var owner = new ZoneManagedNpcActorMotionLease(this, actor, body, construction);
        RequireDependencies(owner);
        _live.Add(actor.Handle, owner);
        try { BindModelEffects(owner); return owner; }
        catch { owner.Dispose(); throw; }
    }

    /// <summary>39346960 sets model.flags bit1 after the original character constructor succeeds.</summary>
    public void CompleteNpcInitialization(ZoneManagedNpcActor actor)
    {
        var owner = Require(actor);
        if (owner.ModelInitializationCompleted)
            throw new InvalidOperationException("This NPC model initialization has already completed.");
        owner.ModelFlags |= 1;
        owner.ModelInitializationCompleted = true;
        //Deferred admission must not put a late flag1 write after real post-enter buffs.
        //Initial restored effects precede39346960; later World mutations follow it.
        DrainModelEffects(owner);
        RefuseModelEffectFailure(owner);
    }

    /// <summary>
    /// Copy actual status after the scene's one physical Step. Call exactly once per frame;
    /// model relocation later in that frame does not rerun these CActor statistics.
    /// </summary>
    public void Update(ZoneManagedNpcActor actor, ZoneManagedNpcBodySnapshot body, ZoneMovementFrame frame)
    {
        var owner = Require(actor);
        ArgumentNullException.ThrowIfNull(body);
        var elapsed = (float)frame.Elapsed.TotalSeconds;
        if (!(elapsed > 0) || !float.IsFinite(elapsed)) throw new ArgumentOutOfRangeException(nameof(frame));
        var timestamp = frame.UnpausedMilliseconds ??
            throw new NotSupportedException("CActor movement sampling requires the scene's sampled unpaused CSystem timestamp.");
        if (owner.State.Frame == frame.Frame)
            throw new InvalidOperationException("CActor statistics already consumed this physical frame.");
        var actual = owner.Body.CopyState();
        Require(actor);
        if (body != actual)
            throw new InvalidOperationException("CActor statistics require the actual unchanged postphysical body snapshot.");
        RequireDependencies(owner);
        CertifyDry(owner, actual.Status.Position);
        Require(actor);
        var speedState = StageMovementSpeedState(owner, actual, elapsed);

        var prior = owner.State;
        var aiPhysics = owner.AiPhysics;
        ZoneManagedNpcActorMotionState next;
        if (!actual.AppliedDescription.CollisionActive)
        {
            //392E7140 falls through392E0630 when ordinary living dynamics are inactive:
            //actual actor velocity is cleared; the ground/air timers and transition are not.
            next = prior with { ActorVelocity = Vector3.Zero, LandingCooldown = 0,
                Frame = frame.Frame, Timestamp = timestamp };
        }
        else
        {
            var physical = actual.Status;
            if (physical.LivingHasRetainedGroundEntity != false)
                throw new NotSupportedException("CActor movement requires a copied absent retained support; moving/unknown support is not admitted.");
            if (physical.LivingHadBlockingContact is not { } blocked)
                throw new NotSupportedException("CActor movement requires the living motor's copied blocking-contact producer.");
            var floor = physical.CachedLivingGroundHeight ??
                throw new NotSupportedException("CActor movement requires the actual living ground cache.");
            if (!float.IsFinite(floor) || !Finite(physical.Velocity))
                throw new InvalidDataException("CActor copied physical statistics must be finite.");
            //392E0C80 copies status2+24 (velocity including retained ground) to AI16,
            //and status2+72 (body496 gravity) to AI28. The current profile owns no
            //retained ground, so its copied status velocity is already the first vector.
            aiPhysics = new(physical.Velocity, new(0, 0, actual.AppliedDescription.Gravity));
            var blockedSeconds = blocked ? .3f : MathF.Max(0, prior.BlockedSeconds - elapsed);
            //392E0C80 first copies status.flying to actor369. Its redundant !369 test
            //therefore supplies no extra grounded condition. The near-floor override uses
            //ENTITY origin, strict .1m, old air timer==0 and the real -1e10 miss sentinel.
            var grounded = floor > -1e10f && (!physical.IsFlying || blockedSeconds > 0);
            if (floor > -1e10f && physical.Position.Z - floor < .1f && prior.AirborneSeconds == 0)
                grounded = true;
            var mode = grounded ? ZoneManagedNpcActorMotionMode.Ground : ZoneManagedNpcActorMotionMode.Air;
            var transitioned = mode != prior.Mode;
            var landing = transitioned && mode == ZoneManagedNpcActorMotionMode.Ground &&
                prior.Mode == ZoneManagedNpcActorMotionMode.Air;
            //No queued/wished velocity is used here.392E0C80 zeros actor228 when the
            //copied physical gravity vector is exactly zero, even if the body is moving.
            var velocity = actual.AppliedDescription.Gravity == 0 ? Vector3.Zero : physical.Velocity;
            var fall = prior.FallSpeed;
            var maximumFall = prior.MaximumFallSpeed;
            var firstLanding = prior.FirstLanding;
            if (landing)
            {
                if (!firstLanding && prior.FallSpeed >= 5)
                    throw new NotSupportedException("Repeated hard CActor landing requires its original fall-effect/damage consumers.");
                firstLanding = false;
            }
            var groundSeconds = transitioned ? 0 : prior.GroundedSeconds;
            var airSeconds = transitioned ? 0 : prior.AirborneSeconds;
            var cooldown = landing ? .1f : prior.LandingCooldown;
            if (mode == ZoneManagedNpcActorMotionMode.Ground) groundSeconds += elapsed;
            else
            {
                RefuseUnownedFastFallEntry(prior.FallSpeed, physical.Position);
                fall = actual.AppliedDescription.Gravity * velocity.Z <= 0 ? 0 : -velocity.Z;
                maximumFall = MathF.Max(maximumFall, fall);
                airSeconds += elapsed;
            }
            next = new(mode, velocity, groundSeconds, airSeconds, blockedSeconds, fall, maximumFall,
                landing, firstLanding, MathF.Max(0, cooldown - elapsed), frame.Frame, timestamp);
        }
        if (!Finite(next.ActorVelocity) || !float.IsFinite(next.GroundedSeconds) ||
            !float.IsFinite(next.AirborneSeconds) || !float.IsFinite(next.MaximumFallSpeed))
            throw new InvalidDataException("CActor motion timers or copied velocity overflowed finite precision.");
        Require(actor);
        if (owner.Body.CopyState() != actual)
            throw new InvalidOperationException("The NPC physical state changed during CActor statistics sampling.");
        Require(actor);
        owner.SurfaceSlope = speedState;
        owner.State = next;
        owner.AiPhysics = aiPhysics;
        if (next.LandingTransition && actual.AppliedDescription.CollisionActive) owner.LandingCount++;
        //Dedicated39277D00 never calls client-only3945F1E0 to install controller+24.
        //3945B0D0's actual missing-driver branch has no landing mutation in this profile.
    }

    private void RefuseUnownedFastFallEntry(float previousFallSpeed, Vector3 entityPosition)
    {
        //392E08F0 tests stored actor376 BEFORE 392D7A10 derives this frame's fall
        //speed. The admitted constructor profile keeps actor312/533/2312 and
        //jump420 zero; entering an unowned special-state producer is refused.
        //392B3540/392B356F register start-height 2 and start-velocity 10, respectively.
        var velocity = _engineCVars.GetFloat("pl_fall_start_velocity", 10);
        if (!float.IsFinite(velocity))
            throw new NotSupportedException("CActor fast-fall admission requires a finite pl_fall_start_velocity.");
        if (!(previousFallSpeed > velocity)) return;
        var height = _engineCVars.GetFloat("pl_fall_start_height", 2);
        if (!float.IsFinite(height))
            throw new NotSupportedException("CActor fast-fall admission requires a finite pl_fall_start_height.");
        var terrain = _terrain ?? throw new NotSupportedException(
            "CActor fast-fall admission requires its actual renderer terrain-height owner.");
        //I3DEngine+1064, with voxel queries disabled. A static roof/cached living
        //floor is not this source height query. Keep the float addition grouping.
        var threshold = height + terrain.GetHeight(entityPosition.X, entityPosition.Y);
        if (!float.IsFinite(threshold))
            throw new InvalidDataException("CActor fast-fall terrain threshold is not finite.");
        if (entityPosition.Z > threshold)
            throw new NotSupportedException("CActor fast-fall state/effects require their original consumers.");
    }

    public ZoneManagedNpcReplicationSample CopyReplicationState(ZoneManagedNpcActor actor)
    {
        var owner = Require(actor);
        var state = owner.State;
        var timestamp = state.Timestamp ?? throw new NotSupportedException("CActor statistics have not consumed an actual physical frame.");
        //CheckPos relocation legitimately changes the model pose between this frame's
        //physical stats and replication. Preserve actor228/timers while reading fresh pose.
        var body = owner.Body.CopyState();
        Require(actor); RequireDependencies(owner);
        if (CopyPuppetAlertness(actor) is not { } alertness)
            throw new NotSupportedException("Non-Puppet replica alertness requires its copied native model-data fallback producer.");
        var flags = (ushort)((owner.SourceWalking ? 1 : 0) | (state.GroundedSeconds > 0 ? 4 : 0) |
            (state.LandingTransition ? 0x10 : 0) | (state.BlockedSeconds > .001f ? 0x1000 : 0));
        var delta = owner.ControllerRequest is { } request && (request.Flags & 0x20) != 0 ? request.Delta : Vector3.Zero;
        return new(body, state.ActorVelocity, delta, flags, checked((sbyte)body.CurrentStanceSlot),
            checked((sbyte)alertness), ControllerId: owner.ControllerId,
            KeepSyncing: false, GuardedAllowStrafing: GuardedAllowStrafing(actor),
            ModelCanReplicate: (owner.ModelFlags & 1) != 0, Timestamp: timestamp);
    }

    /// <summary>392A48D0 resets only model.controllerId152; this does not cancel a controller or clear CActor statistics.</summary>
    public void ResetControllerAfterReplication(ZoneManagedNpcActor actor) => Require(actor).ControllerId = 0;

    /// <summary>
    /// CPuppet ctor31171F8C owns m_Alertness4160=0. Its later311680BC copy occurs only
    /// after the full proxy update in31172DEC. The concrete request owner performs the
    /// admitted zero-alertness copy at that boundary, independently of this getter.
    /// </summary>
    public int? CopyPuppetAlertness(ZoneManagedNpcActor actor)
    { var owner = Require(actor); return actor.IsPuppet ? owner.PuppetAlertness : null; }

    private bool? GuardedAllowStrafing(ZoneManagedNpcActor actor)
    {
        var models = _models ?? throw new NotSupportedException("NPC movement sampling requires its cached template model's authored branch inventory.");
        var cachedModel = actor.Npc.Model;
        if (cachedModel.Kind != ZoneModelKind.Actor) return null;
        if (!models.ActorModels.Models.TryGetValue(cachedModel.SubtypeId, out var cached))
            throw new NotSupportedException("The NPC's cached template ActorModel subtype is unresolved.");
        //393405B0: the guard is the NPC TEMPLATE model. Its IAActor+16 result is
        //SAIActorState(full+240);+964 is full+1204 AllowStrafing, not CActor+964.
        return cached.Definition.SlopeAlignment ? actor.Controls.AllowStrafing : null;
    }

    public ZoneManagedNpcActorMotionLease? Find(ZoneActorHandle actor) =>
        !IsDisposed && _live.TryGetValue(actor, out var owner) && owner.IsCurrent ? owner : null;

    private void CertifyDry(ZoneManagedNpcActorMotionLease owner, Vector3 position)
    {
        if (_water is null || _characters is null || _assets is null)
            throw new NotSupportedException("CActor dry movement requires its complete water and admitted initial-character inventory owners.");
        var character = _characters.Find(owner.Actor.Handle) ??
            throw new NotSupportedException("CActor swim threshold requires this exact generation's loaded character slot0.");
        if (character.ActorModelId != owner.Body.Admission.Request.ActorModel.Id || !character.HasSlot(0))
            throw new InvalidOperationException("CActor swim threshold belongs to a different character/model generation.");
        var definition = character.Definition;
        if (!_thresholds.TryGetValue(definition.SkeletonAssetName, out var threshold))
        {
            threshold = ZoneCharacterInitialSwimThreshold.Load(_assets, definition);
            RequireDependencies(owner);
            _thresholds.Add(definition.SkeletonAssetName, threshold);
        }
        var height = threshold.Height * owner.Construction.EntityScale;
        var query = position;
        query.Z += height;
        var maximum = _water.MaximumPotentialWaterLevelAt(query);
        //Retain the original float operation order at high coordinates; subtracting
        //height from the comparison algebraically is not equivalent under rounding.
        if (!float.IsFinite(height) || !Finite(query) || !(query.Z - maximum > height * 2))
            throw new NotSupportedException("CActor initial-pose dry profile cannot prove its swim query above every eligible water surface; swimming/current animated bounds need their own owner.");
    }

    internal bool Owns(ZoneManagedNpcActorMotionLease owner) => !IsDisposed &&
        _live.TryGetValue(owner.Actor.Handle, out var current) && ReferenceEquals(current, owner) &&
        ReferenceEquals(_actors.Find(owner.Actor.Handle), owner.Actor) &&
        ReferenceEquals(_construction.Find(owner.Actor.Handle), owner.Construction) &&
        owner.Construction.IsCurrent && ReferenceEquals(_physics.Find(owner.Actor.Handle), owner.Body) &&
        !IsDisposed && !owner.IsDisposed &&
        _live.TryGetValue(owner.Actor.Handle, out current) && ReferenceEquals(current, owner) &&
        ReferenceEquals(_construction.Find(owner.Actor.Handle), owner.Construction) &&
        ReferenceEquals(_actors.Find(owner.Actor.Handle), owner.Actor);
    private void RequireDependencies(ZoneManagedNpcActorMotionLease owner)
    {
        if (IsDisposed || !ReferenceEquals(_actors.Find(owner.Actor.Handle), owner.Actor) ||
            !ReferenceEquals(_construction.Find(owner.Actor.Handle), owner.Construction) ||
            !ReferenceEquals(_physics.Find(owner.Actor.Handle), owner.Body) ||
            IsDisposed || owner.IsDisposed ||
            !ReferenceEquals(_construction.Find(owner.Actor.Handle), owner.Construction) ||
            !ReferenceEquals(_actors.Find(owner.Actor.Handle), owner.Actor))
            throw new ObjectDisposedException(nameof(ZoneManagedNpcActorMotionLease), "The exact CActor/model/body generation retired.");
        var unit = owner.Actor.Npc.Unit;
        if (unit.Snapshot.ModelId != owner.Construction.ModelId || unit.Scale.BaseScale != owner.Construction.EntityScale ||
            unit.Scale.Multiplier != 1 || unit.Snapshot.Attachment.Point != -1 || unit.Snapshot.Bonding.Point != -1)
            throw new NotSupportedException("CActor motion requires its unchanged upright unparented model and initial scale.");
    }
    private ZoneManagedNpcActorMotionLease Require(ZoneManagedNpcActor actor)
    {
        Check(); ArgumentNullException.ThrowIfNull(actor);
        var owner = Find(actor.Handle) is { } current && ReferenceEquals(current.Actor, actor) ? current :
            throw new ObjectDisposedException(nameof(ZoneManagedNpcActorMotionLease), "The exact CActor motion generation retired.");
        RefuseModelEffectFailure(owner);
        return owner;
    }
    private void Check() => ObjectDisposedException.ThrowIf(IsDisposed, this);
    internal void Retire(ZoneManagedNpcActorMotionLease owner)
    { if (_live.TryGetValue(owner.Actor.Handle, out var current) && ReferenceEquals(current, owner)) _live.Remove(owner.Actor.Handle); }
    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    public void Dispose()
    { if (IsDisposed) return; IsDisposed = true; foreach (var owner in _live.Values.ToArray()) owner.Dispose(); _thresholds.Clear(); _modelEffectFaults.Clear(); }
}

/// <summary>One source-backed CActor/model generation. Does not own the borrowed actor, construction or physical body.</summary>
public sealed partial class ZoneManagedNpcActorMotionLease : IDisposable
{
    private readonly ZoneManagedNpcActorMotion _owner;
    internal ZoneManagedNpcBodyLease Body { get; }
    internal ZoneManagedNpcActorConstructionLease Construction { get; }
    internal ZoneActorModelDefinition ActorModel => Body.Admission.Request.ActorModel.Definition;
    internal uint ModelFlags;
    internal uint ControllerId; //39289EB0: native invalid dword3A602F7C=0, independent of pointer144=null.
    public ZoneManagedNpcActor Actor { get; }
    public ZoneManagedNpcActorMotionState State { get; internal set; } =
        //392E7D80's FINAL writes override its early zero: mode3 and airTimer=1.
        new(ZoneManagedNpcActorMotionMode.Air, Vector3.Zero, 0, 1, 0, 0, 0, false, true, 0, null, null);
    public ulong LandingCount { get; internal set; }
    public bool IsDisposed { get; private set; }
    public bool IsCurrent => !IsDisposed && _owner.Owns(this);
    internal ZoneManagedNpcActorMotionLease(ZoneManagedNpcActorMotion owner, ZoneManagedNpcActor actor,
        ZoneManagedNpcBodyLease body, ZoneManagedNpcActorConstructionLease construction)
    { _owner = owner; Actor = actor; Body = body; Construction = construction; }
    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        if (BuffEffectHandler is { } handler) Actor.Npc.Unit.BuffEffect -= handler;
        BuffEffectHandler = null;
        PendingModelEffects.Clear();
        _owner.Retire(this);
    }
}

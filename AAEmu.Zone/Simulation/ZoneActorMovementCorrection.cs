using System.Numerics;
using AAEmu.Zone.Protocol.Messages;
using AAEmu.Zone.Simulation.Contracts;
using static AAEmu.Zone.Simulation.ZoneActorMovementCorrectionMath;

namespace AAEmu.Zone.Simulation;

public sealed partial class ZoneActorMovementApplicationState
{
    public byte NativeStoppedByte { get; internal set; }
    public Vector3 CorrectionAccumulator { get; internal set; }
    public ZoneActorMovementDouble3 PreviousCorrection { get; internal set; }
    public ZoneActorMovementDouble3 CurrentCorrection { get; internal set; }
    public ZoneActorMovementDouble3 CorrectionVelocity { get; internal set; }
    public Vector3 HangingVelocity { get; internal set; }
    public Vector3 PreviousObservedPosition { get; internal set; }
    public Vector3 PreviousCalculatedVelocity { get; internal set; }
    public Vector3 PreviousSelectedPosition { get; internal set; }

    /// <summary>3924A220 clears these accumulators for every selected-history reset, retaining the other application fields.</summary>
    internal void ResetCorrectionHistory()
    {
        CorrectionAccumulator = default; PreviousCorrection = default;
        CurrentCorrection = default; CorrectionVelocity = default;
    }

    internal void RetireCorrection()
    {
        ResetCorrectionHistory(); NativeStoppedByte = 0; HangingVelocity = default;
        PreviousObservedPosition = default; PreviousCalculatedVelocity = default; PreviousSelectedPosition = default;
    }
}

public readonly record struct ZoneActorMovementCorrectionBounds(Vector3 Minimum, Vector3 Maximum);
public readonly record struct ZoneActorMovementGroundIds(uint UnitId, uint GimmickId, uint DoodadId);
/// <summary>The defined fields consumed from native physical status type 2. Unproved flags retain their native offsets.</summary>
public readonly record struct ZoneActorMovementLivingStatus(int Native04, float VelocityZ, float Native58,
    IZoneActorMovementGroundCollider? Collider, int Native7C, Vector3 RelativePosition);
public readonly record struct ZoneActorMovementPartParameters(Vector3 Position, Quaternion Rotation, float Scale)
{
    public static ZoneActorMovementPartParameters Initial => new(default, Quaternion.Identity, 1);
}
public readonly record struct ZoneActorMovementHangingPose(Vector2 Coordinates, Quaternion Rotation);

/// <summary>Defined native type-4 action fields. Hanging actions leave the original-velocity vector undefined except its sentinel X.</summary>
public readonly record struct ZoneActorMovementCorrectionAction(Vector3 Position, Vector3 Velocity,
    Vector3? OriginalVelocity, bool SameGround, bool GroundTransition, bool Native46, uint GroundPart, uint GroundPartId)
{
    public int Type => 4;
    public uint Native48 => 0;
    public uint UndefinedOriginalVelocitySentinel => 0xFFBFFFFF;
}

public interface IZoneActorMovementGroundCollider
{
    ZoneActorMovementGroundIds GetGroundIds(int native7C);
}

/// <summary>Required physical status, parameter, and action boundaries; query failures keep their actual native results.</summary>
public interface IZoneActorMovementCorrectionPhysics
{
    Vector3 GetPositionStatus();
    ZoneActorMovementLivingStatus GetLivingStatus();
    bool TryGetPartParameters(int partId, out ZoneActorMovementPartParameters parameters);
    float GetDimensionsHeight();
    void Apply(ZoneActorMovementCorrectionAction action);
}

public interface IZoneActorMovementCorrectionEntity
{
    IZoneActorMovementCorrectionPhysics? GetPhysics();
    ZoneActorMovementCorrectionBounds GetBounds();
    Quaternion GetRotation();
    Vector3 GetPosition();
    /// <summary>Native entity virtual A0 controls the additional part-owner and part-index queries.</summary>
    bool HasPartOwner { get; }
    int GetPartIdOffset();
    IZoneActorMovementCorrectionEntity GetPartOwner();
    void SetPosition(Vector3 position, uint flags);
}

public interface IZoneActorMovementCorrectionActor
{
    bool NativeRagdoll191 { get; }
    ZoneActorMovementHangingPose GetHangingPose();
    void SetHangingPose(ZoneActorMovementHangingPose pose);
}

public interface IZoneActorMovementCorrectionObserver
{
    void Apply(ZoneActorMovementState selected);
}

/// <summary>Real model, entity, clock and configuration leaves for 392440E0/39245840. Debug drawing is outside this gameplay component.</summary>
public interface IZoneActorMovementCorrectionServices
{
    IZoneActorMovementCorrectionActor? GetPhysicalActor(ZoneUnit owner);
    IZoneActorMovementCorrectionEntity? GetModelEntity(ZoneUnit owner);
    IZoneActorMovementCorrectionObserver? GetCorrectionObserver(ZoneUnit owner);
    IZoneActorMovementCorrectionEntity? ResolveGround(ZoneActorMovementGroundIds ids);
    ZoneActorMovementWorldOrigin GetWorldOrigin();
    float FrameSeconds { get; }
    float SmoothingSeconds { get; }
    byte NativeEnvironment121 { get; }
}

public sealed partial class ZoneActorMovementController
{
    /// <summary>
    /// 392440E0: clears pending movement at entry and applies physical correction from the independently
    /// selected sample cache. The caller provides its working copy of Latest and actual constraint state.
    /// This does not schedule a phase or replace Latest, the sample caches, or the owner's pose.
    /// </summary>
    public bool TryApplyPhysicalCorrection(ZoneActorMovementState working, bool hasConstraint,
        IZoneActorMovementCorrectionServices services)
    {
        ArgumentNullException.ThrowIfNull(working); ArgumentNullException.ThrowIfNull(services);
        if (!Current) return false;
        if (_receiving || _sampling) throw new InvalidOperationException("Actor movement correction cannot reenter its owning controller.");
        _sampling = true;
        try { return ApplyPhysicalCorrectionCore(working, hasConstraint, services); }
        catch (CorrectionOwnershipLostException) { return false; }
        finally { _sampling = false; }
    }

    /// <summary>
    /// 39263B10/39245840: the postphase permits model flag 2, transfers correction buffers, runs the
    /// correction producer when the actor is not ragdolled, and publishes a nonzero or NaN world correction.
    /// Updated and constraint state must come from the actual enclosing physical caller.
    /// </summary>
    public bool TryRunPhysicalPostPhase(ZoneActorMovementState working, bool updated, bool hasConstraint,
        ZoneActorMovementEligibilitySnapshot snapshot, IZoneActorMovementCorrectionServices services)
    {
        ArgumentNullException.ThrowIfNull(working); ArgumentNullException.ThrowIfNull(services);
        if (!Current || (snapshot.ModelFlags & 1) != 0 ||
            !ZoneActorMovementEligibility.AllowsUpdate(LastGameSeconds, DirectMovementRecentlyApplied, snapshot with { ModelFlags = 0 }) || !updated) return false;
        if (_receiving || _sampling) throw new InvalidOperationException("Actor movement postphase cannot reenter its owning controller.");
        _sampling = true;
        try
        {
            var actor = CorrectionRead(() => services.GetPhysicalActor(Owner));
            if (actor is null) return false;
            Application.PreviousCorrection = Application.CurrentCorrection;
            Application.CurrentCorrection = default;
            if (!CorrectionRead(() => actor.NativeRagdoll191)) ApplyPhysicalCorrectionCore(working, hasConstraint, services);
            if (!Zero(Application.CurrentCorrection))
            {
                var entity = CorrectionRead(() => services.GetModelEntity(Owner));
                if (entity is not null)
                {
                    var origin = World(CorrectionOrigin(services));
                    var world = Application.CurrentCorrection;
                    CorrectionCall(() => entity.SetPosition(new((float)(world.X - origin.X),
                        (float)(world.Y - origin.Y), (float)(world.Z - origin.Z)), 0x102000));
                }
            }
            return true;
        }
        catch (CorrectionOwnershipLostException) { return false; }
        finally { _sampling = false; }
    }

    private bool ApplyPhysicalCorrectionCore(ZoneActorMovementState working, bool hasConstraint, IZoneActorMovementCorrectionServices services)
    {
        HasPendingMovement = false;
        if (hasConstraint) { Application.HangingVelocity = default; return false; }
        if (CorrectionRead(() => _services.InterpolationMode) == 3 && !LinkedHistory.HasLinkedSample) return false;
        var mode = CorrectionRead(() => _services.InterpolationMode);
        var selected = mode == 2 ? VectorHistory.SampledState ?? InitialInputState :
            CorrectionRead(() => _services.InterpolationMode) == 3 ? LinkedHistory.SampledState ?? InitialInputState : working;
        if (selected.HangingObjectId != 0) return ApplyHangingCorrection(working, selected, services);
        Application.HangingVelocity = default;
        var observer = CorrectionRead(() => services.GetCorrectionObserver(Owner));
        if (observer is not null) CorrectionCall(() => observer.Apply(selected));
        Application.NativeStoppedByte = selected.NativeStoppedByte;
        var ids = new ZoneActorMovementGroundIds(selected.GroundUnitId, selected.GroundGimmickId, selected.GroundDoodadId);
        var ground = CorrectionRead(() => services.ResolveGround(ids));
        var entity = CorrectionRead(() => services.GetModelEntity(Owner));
        if (entity is null) return false;
        var physical = CorrectionRead(entity.GetPhysics);
        if (physical is null) return false;
        var current = CorrectionRead(physical.GetPositionStatus);
        var living = CorrectionRead(physical.GetLivingStatus);
        var origin = CorrectionOrigin(services);
        var desired = Local(selected.Pose.Position, origin);
        var velocity = selected.Velocity;
        // COMISS/JBE sends unordered values through the filtered-velocity branch.
        var preserveVertical = living.Native58 > -1e10f && living.Native04 == 0;
        var radiusSquared = BoundsRadiusSquared(CorrectionRead(entity.GetBounds));
        var observed = current;
        var force = false; var transition = false; var sameGround = false;
        if (ground is null) force = transition = living.Collider is not null;
        else
        {
            desired = Local(selected.Pose.Position, default);
            if (living.Collider is not null)
                sameGround = CorrectionRead(() => living.Collider.GetGroundIds(living.Native7C)) == ids;
            if (sameGround) observed = living.RelativePosition;
            else
            {
                if (!TryGroundCorrectionPosition(entity, ground, unchecked((int)selected.GroundPartId), desired, out desired))
                    desired = Local(selected.GroundPose.Position, origin);
                force = transition = true;
            }
        }
        var frame = Frame(CorrectionRead(() => services.FrameSeconds));
        var position = current;
        if (CorrectionRead(() => _services.InterpolationMode) == 0) { position = desired; force = false; }
        else if (CorrectionRead(() => _services.InterpolationMode) == 1)
        {
            if (force) { Application.CorrectionAccumulator = default; position = desired; force = false; }
            else
            {
                var smoothing = CorrectionRead(() => services.SmoothingSeconds);
                if (.01f > smoothing) smoothing = .01f;
                else
                {
                    smoothing = CorrectionRead(() => services.SmoothingSeconds);
                    smoothing = 10f > smoothing ? CorrectionRead(() => services.SmoothingSeconds) : 10f;
                }
                var accumulator = Application.CorrectionAccumulator;
                position = Smooth(observed, ref accumulator, frame, desired, smoothing);
                Application.CorrectionAccumulator = accumulator; force = true;
            }
        }
        else if (CorrectionRead(() => _services.InterpolationMode) == 2)
        {
            position = Local((VectorHistory.SampledState ?? InitialInputState).Pose.Position, origin); force = true;
        }
        else if (CorrectionRead(() => _services.InterpolationMode) == 3)
        {
            velocity = (LinkedHistory.SampledState ?? InitialInputState).Velocity;
            position = desired;
            if (force) Application.CorrectionAccumulator = default;
            force = !transition || force;
        }
        else force = false;
        var previous = Application.PreviousSelectedPosition;
        var delta = Zero(previous) ? default : new Vector3(position.X - previous.X, position.Y - previous.Y, position.Z - previous.Z);
        var inverse = 1f / frame;
        var calculated = new Vector3(delta.X * inverse, inverse * delta.Y, inverse * delta.Z);
        if (CorrectionRead(() => _services.InterpolationMode) == 2) velocity = calculated;
        var filteredZ = velocity.Z;
        if (Application.JumpMode == 1 || !preserveVertical || (selected.ActorFlags & 4) == 0)
        {
            var fraction = .01f > frame ? .01f : .033f > frame ? frame : .033f;
            filteredZ = (velocity.Z - living.VelocityZ) * fraction + living.VelocityZ;
        }
        var parts = ground is not null && selected.GroundUnitId != 0;
        CorrectionCall(() => physical.Apply(new(position, new(velocity.X, velocity.Y, filteredZ), velocity,
            sameGround, transition, force, parts ? selected.GroundPart : 0, parts ? selected.GroundPartId : 0)));
        if (!sameGround)
        {
            var world = World(position, origin);
            var threshold = 100f > radiusSquared ? 100f : radiusSquared;
            var before = Application.PreviousCorrection;
            var dx = world.X - before.X; var dy = world.Y - before.Y; var dz = world.Z - before.Z;
            // Native JA selects the snap branch only for an ordered greater distance.
            if (!Zero(before) && !(dx * dx + dy * dy + dz * dz > threshold))
            {
                Application.CurrentCorrection = before;
                var advanceZ = velocity.Z * frame;
                var target = World(new Vector3(velocity.X * frame + position.X, velocity.Y * frame + position.Y, position.Z + advanceZ), origin);
                if (Application.JumpMode == 1) target = target with { Z = target.Z + advanceZ };
                var smoothing = CorrectionRead(() => services.SmoothingSeconds);
                var correctionVelocity = Application.CorrectionVelocity;
                Application.CurrentCorrection = Smooth(before, ref correctionVelocity, frame, target, smoothing);
                Application.CorrectionVelocity = correctionVelocity;
            }
            else { Application.PreviousCorrection = world; Application.CurrentCorrection = world; }
        }
        Application.PreviousCalculatedVelocity = calculated;
        Application.PreviousObservedPosition = observed;
        Application.PreviousSelectedPosition = position;
        return true;
    }

    private bool TryGroundCorrectionPosition(IZoneActorMovementCorrectionEntity entity, IZoneActorMovementCorrectionEntity ground,
        int partId, Vector3 local, out Vector3 position)
    {
        if (CorrectionRead(() => ground.HasPartOwner))
        {
            partId = unchecked(partId + CorrectionRead(ground.GetPartIdOffset));
            ground = CorrectionRead(ground.GetPartOwner);
        }
        var part = ZoneActorMovementPartParameters.Initial;
        var physics = CorrectionRead(ground.GetPhysics);
        if (physics is not null)
        {
            var valid = CorrectionRead(() => physics.TryGetPartParameters(partId, out part));
            if (!valid) { position = default; return false; }
        }
        var height = 0f;
        physics = CorrectionRead(entity.GetPhysics);
        if (physics is not null) height = CorrectionRead(physics.GetDimensionsHeight);
        var partPosition = Rotate(CorrectionRead(ground.GetRotation), part.Position);
        var groundPosition = CorrectionRead(ground.GetPosition);
        var rotation = GroundRotation(part.Rotation, CorrectionRead(ground.GetRotation));
        var transformed = Rotate(rotation, local);
        var x = transformed.X * part.Scale; var y = transformed.Y * part.Scale; var z = transformed.Z * part.Scale;
        var heightVector = Rotate(CorrectionRead(entity.GetRotation), new(0, 0, height));
        position = new(x + (groundPosition.X + partPosition.X) + heightVector.X,
            y + (groundPosition.Y + partPosition.Y) + heightVector.Y, z + (groundPosition.Z + partPosition.Z) + heightVector.Z);
        return true;
    }

    private bool ApplyHangingCorrection(ZoneActorMovementState working, ZoneActorMovementState selected,
        IZoneActorMovementCorrectionServices services)
    {
        var actor = CorrectionRead(() => services.GetPhysicalActor(Owner));
        if (actor is not null)
        {
            var elapsed = CorrectionRead(() => services.FrameSeconds);
            var pose = CorrectionRead(actor.GetHangingPose);
            var q = pose.Rotation;
            var heading = MathF.Atan2(-((q.Y * q.X - q.W * q.Z) * 2f), (q.W * q.W + q.Y * q.Y) * 2f - 1f);
            var velocity = Application.HangingVelocity;
            var next = Smooth(new(pose.Coordinates, heading), ref velocity, elapsed, selected.Climb, .1f);
            Application.HangingVelocity = velocity;
            var half = next.Z * .5f;
            CorrectionCall(() => actor.SetHangingPose(new(new(next.X, next.Y), new(0, 0, MathF.Sin(half), MathF.Cos(half)))));
        }
        if (CorrectionRead(() => services.NativeEnvironment121) != 0)
        {
            var entity = CorrectionRead(() => services.GetModelEntity(Owner));
            if (entity is not null)
            {
                var physical = CorrectionRead(entity.GetPhysics);
                if (physical is not null)
                {
                    var position = Local(working.Pose.Position, CorrectionOrigin(services));
                    CorrectionCall(() => physical.Apply(new(position, working.Velocity, null, false, false, false, 0, 0)));
                }
            }
        }
        return true;
    }

    private UnitWorldPosition CorrectionOrigin(IZoneActorMovementCorrectionServices services)
    {
        var x = CorrectionRead(services.GetWorldOrigin).X;
        var y = CorrectionRead(services.GetWorldOrigin).Y;
        return Origin(x, y);
    }
    private T CorrectionRead<T>(Func<T> callback)
    {
        var value = callback();
        if (!Current) throw new CorrectionOwnershipLostException();
        return value;
    }
    private void CorrectionCall(Action callback)
    {
        callback();
        if (!Current) throw new CorrectionOwnershipLostException();
    }
    private sealed class CorrectionOwnershipLostException : Exception;
}

// ---------------------------------------------------------------------------
// Turn 2 — concrete authoritative-pose correction services (registered
// IMPLEMENTATION_OWNERSHIP.md section 4, change log 2026-09-23; PLAN D3).
// The leaves reconcile from ZonePhysicsActorStatus COPIES: every physical read
// goes through the player actor's TryCopyStatus (the exact mapping of
// ZonePlayerActorFactory.cs:606-618), and every write lands on IZonePhysicsMotion
// in the engine-local world frame — no local/wire conversion happens here
// (correction origins convert local↔world with the fixed region step,
// ZoneActorMovementCorrectionMath.cs:14-22; the outgoing encoder owns wire policy).
// ---------------------------------------------------------------------------

/// <summary>
/// Eligibility snapshot builder for plain bodies (PLAN D3). The contract states the model and
/// environment values "must come from those owners"
/// (<c>ZoneActorMovementEligibility.cs:5-7</c>) and no such owner exists in this wave:
/// TODO(model-flags) — every field takes the permissive documented default (the all-open
/// snapshot the eligibility tests use, <c>AAEmu.Zone.Tests/ActorMovementLinkedSamplingTests.cs:186</c>),
/// which keeps <c>AllowsUpdate</c> (<c>ZoneActorMovementEligibility.cs:15-19</c>) decided by the
/// gates that DO have owners (lastGameSeconds, direct-recent, model state). Flip these from the
/// model/environment owners when they land; never silently invent blocking or permitting values.
/// </summary>
public static class ZoneMovementEligibilitySnapshotBuilder
{
    public static ZoneActorMovementEligibilitySnapshot PlainBody { get; } =
        new(ModelFlags: 0, EnvironmentFlag120: 0, EnvironmentFlag121: 0, EnvironmentFlag150: 0,
            ModelState210: 0, HasModelObject1A0: false);
}

/// <summary>
/// Concrete <c>IZoneActorMovementCorrectionServices</c>: physics status and actions from the
/// adapter-backed <c>ZonePhysicsActorStatus</c> copies, world origin from the scene's region
/// origin, frame/smoothing seconds from the injected frame/config (PLAN D3).
/// Coordinate rule (PLAN section 4 citations): reads are engine-frame floats (CopyStatus);
/// <c>GetWorldOrigin</c> returns the scene origin in 1024 m region units —
/// <c>ZoneActorMovementWorldOrigin(x, y)</c> feeds <c>CorrectionOrigin</c>'s
/// <c>x &lt;&lt; 54</c> step (<c>ZoneActorMovementCorrectionMath.cs:14</c>) exactly, and
/// region bounds are 1024 m each (<c>ZoneUnitSpatialIndex.cs:9</c>). Clock rule: FrameSeconds
/// is THIS frame's <c>Elapsed</c> from the single-sample clock — never a wall clock
/// (<c>IZoneMovementFramePhases</c> doc surface).
/// One instance per scene generation, wired by Agent 5 (change-log wiring note).
/// </summary>
public sealed class ZoneMovementCorrectionServices : IZoneActorMovementCorrectionServices
{
    private readonly ZoneUnitRegistry _registry;
    private readonly ZonePlayerActorFactory _actors;
    private readonly IZonePhysicsMotion _motion;
    private readonly ZoneMovementFrameClock _clock;
    private readonly ZoneMovementDiagnostics _diagnostics;
    private readonly ZoneActorMovementWorldOrigin _origin;

    public ZoneMovementCorrectionServices(ZoneUnitRegistry registry, ZonePlayerActorFactory actors,
        IZonePhysicsMotion motion, ZoneMovementFrameClock clock, ZoneMovementDiagnostics diagnostics,
        ZoneActorMovementWorldOrigin origin, float smoothingSeconds = .01f)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(actors);
        ArgumentNullException.ThrowIfNull(motion);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(diagnostics);
        _registry = registry; _actors = actors; _motion = motion; _clock = clock;
        _diagnostics = diagnostics; _origin = origin;
        SmoothingSeconds = smoothingSeconds;
    }

    /// <summary>Ragdoll flag: plain bodies are never ragdolled — TODO(model-flags), same policy as the eligibility snapshot. Exact-owner recheck first: a unit replaced or removed mid-frame resolves to null, never to the replacement (IsCurrent, ZoneUnitRegistry.cs:213).</summary>
    public IZoneActorMovementCorrectionActor? GetPhysicalActor(ZoneUnit owner)
    {
        if (!_registry.IsCurrent(owner)) return null;
        var actor = _actors.FindByUnitObjectId(owner.Snapshot.ObjectId);
        return actor is { IsLive: true } ? new ZoneMovementCorrectionActorLeaf(owner) : null;
    }

    public IZoneActorMovementCorrectionEntity? GetModelEntity(ZoneUnit owner)
    {
        var actor = _registry.IsCurrent(owner)
            ? _actors.FindByUnitObjectId(owner.Snapshot.ObjectId)
            : null;
        return new ZoneMovementCorrectionEntity(owner, actor, _motion, _diagnostics);
    }

    /// <summary>No correction observer exists for plain bodies (producer skips on null, <c>ZoneActorMovementCorrection.cs:172-173</c>).</summary>
    public IZoneActorMovementCorrectionObserver? GetCorrectionObserver(ZoneUnit owner) => null;

    /// <summary>
    /// Ground resolution: this wave binds no gimmick/doodad/unit ground attachments, so every
    /// ids triple resolves to null AFTER a structured missing-ground report when a nonzero id
    /// was requested (no fabrication; the producer's ground-null branch then keeps the
    /// plain-body behavior, :191/:200-203).
    /// </summary>
    public IZoneActorMovementCorrectionEntity? ResolveGround(ZoneActorMovementGroundIds ids)
    {
        if (ids.UnitId != 0) _diagnostics.ReportMissingGround(ZoneActorMovementGroundKind.Unit, ids.UnitId);
        if (ids.GimmickId != 0) _diagnostics.ReportMissingGround(ZoneActorMovementGroundKind.Gimmick, ids.GimmickId);
        if (ids.DoodadId != 0) _diagnostics.ReportMissingGround(ZoneActorMovementGroundKind.Doodad, ids.DoodadId);
        return null;
    }

    public ZoneActorMovementWorldOrigin GetWorldOrigin() => _origin;

    /// <summary>The injected frame's sampled period in seconds (clock domain: Elapsed stays TimeSpan until this conversion, IZoneMovementFramePhases doc).</summary>
    public float FrameSeconds => (float)_clock.Current.Elapsed.TotalSeconds;

    /// <summary>TODO(movement-smoothing-config): no smoothing config source exists in this tree; the default is the native mode-1 clamp floor (:214 <c>.01f &gt; smoothing → .01f</c>), i.e. the tightest documented smoothing.</summary>
    public float SmoothingSeconds { get; }

    /// <summary>TODO(model-flags): native environment byte +121 has no owner; plain-body default 0 (contract note, ZoneActorMovementEligibility.cs:5-7).</summary>
    public byte NativeEnvironment121 => 0;
}

/// <summary>Correction actor leaf: no ragdoll, no hanging support for plain bodies.</summary>
public sealed class ZoneMovementCorrectionActorLeaf(ZoneUnit unit) : IZoneActorMovementCorrectionActor
{
    /// <summary>TODO(model-flags): ragdoll byte +191 has no owner; plain bodies are never ragdolled.</summary>
    public bool NativeRagdoll191 => false;

    public ZoneActorMovementHangingPose GetHangingPose() => default;

    /// <summary>Climbing/hanging poses are out of this wave (done-criteria is walk/turn/stop/jump); reaching this write is named, never a fabricated attachment.</summary>
    public void SetHangingPose(ZoneActorMovementHangingPose pose) =>
        throw new NotSupportedException(
            $"Hanging/climb pose application for unit {unit.Snapshot.ObjectId} is not implemented: this wave binds no hanging " +
            "attachment (FIRST_ENTRY_NOTES.md:1423-1425 brings up walking first; ragdoll/hanging phases are separate work).");
}

/// <summary>
/// Physical status/action leaf over the adapter-backed body status COPIES — the exact
/// "copied status/actions" requirement of PLAN section 4: every read is
/// <c>ZonePlayerActor.TryCopyStatus</c> (<c>Simulation/ZonePlayerActorFactory.cs:606-618</c>)
/// and every write is <see cref="IZonePhysicsMotion"/> in the engine-local world frame.
/// </summary>
public sealed class ZoneMovementCorrectionPhysics(
    ZonePlayerActor? actor, ZoneUnit unit, IZonePhysicsMotion motion, ZoneMovementDiagnostics diagnostics)
    : IZoneActorMovementCorrectionPhysics
{
    /// <summary>Throws the actual native-shaped failure when no live body exists (no fabricated pose; the phase catches per actor and skips, PLAN section 5).</summary>
    private ZonePhysicsActorStatus CopyStatus()
    {
        if (actor is null || !actor.IsLive)
            throw new ObjectDisposedException(nameof(ZonePlayerActor),
                $"Unit {unit.Snapshot.ObjectId} has no live physical body; correction status cannot be fabricated.");
        try
        {
            if (actor.TryCopyStatus(out var status)) return status;
        }
        catch (ObjectDisposedException) { throw; } // Pointer==0 retirement is the actual native result (Living.cs:199-200/:221-263)
        throw new ObjectDisposedException(nameof(ZonePlayerActor),
            $"Unit {unit.Snapshot.ObjectId} body reported no status; correction cannot be fabricated.");
    }

    public Vector3 GetPositionStatus() => CopyStatus().Position;

    /// <summary>
    /// Living status 2 mapped from the copy: VelocityZ and RelativePosition are real copied
    /// values (engine frame); Native04/Native58/Native7C and the ground collider have NO
    /// proven plain-body source — TODO(living-status-abi): Native04 = 0 (no state word),
    /// Native58 = 0 (keeps the documented preserveVertical branch, :187, which then filters
    /// against the REAL copied VelocityZ), Native7C = 0 and Collider = null so the
    /// sameGround/collider branches never claim an attachment that does not exist.
    /// </summary>
    public ZoneActorMovementLivingStatus GetLivingStatus()
    {
        var status = CopyStatus();
        return new ZoneActorMovementLivingStatus(
            Native04: 0, VelocityZ: status.Velocity.Z, Native58: 0f,
            Collider: null, Native7C: 0, RelativePosition: status.Position);
    }

    /// <summary>Plain bodies own no sub-parts (the ground-part path stays inactive; producer tolerates the false, :289-291).</summary>
    public bool TryGetPartParameters(int partId, out ZoneActorMovementPartParameters parameters)
    {
        parameters = ZoneActorMovementPartParameters.Initial;
        return false;
    }

    /// <summary><c>actor_height</c> content column (<c>Content/ZoneActorModelContent.cs:12/:106</c>); only the ground-part path consumes it, which this wave keeps inactive — TODO(dimensions-height) if a part-owning ground lands.</summary>
    public float GetDimensionsHeight() => actor?.Model.Definition.ActorHeight ?? 0f;

    /// <summary>
    /// Type-4 correction action → authoritative pose + velocity writes (PLAN section 4 flow).
    /// <paramref name="action"/>.Position is the producer's engine-frame target; orientation
    /// comes from the committed unit pose (same frame by construction). Both writes are
    /// refusal-counted while <c>TODO(pose-write)</c> is open — the batch survives, the seam
    /// stays visible in <see cref="ZoneMovementDiagnostics"/>.
    /// </summary>
    public void Apply(ZoneActorMovementCorrectionAction action)
    {
        if (actor is null || !actor.IsLive)
            throw new ObjectDisposedException(nameof(ZonePlayerActor),
                $"Unit {unit.Snapshot.ObjectId} has no live physical body; correction action cannot be applied.");
        ZoneMovementMotionCall.Pose(motion, actor.Handle, action.Position, unit.Pose.Rotation, diagnostics);
        ZoneMovementMotionCall.Velocity(motion, actor.Handle, action.Velocity, diagnostics);
    }
}

/// <summary>Model-entity leaf over the committed unit pose + adapter-backed physics (contract <c>IZoneActorMovementCorrectionEntity</c>).</summary>
public sealed class ZoneMovementCorrectionEntity(
    ZoneUnit unit, ZonePlayerActor? actor, IZonePhysicsMotion motion, ZoneMovementDiagnostics diagnostics)
    : IZoneActorMovementCorrectionEntity
{
    private ZoneMovementCorrectionPhysics? _physics;

    public IZoneActorMovementCorrectionPhysics? GetPhysics()
    {
        // The producer null-checks this result (ZoneActorMovementCorrection.cs:179-180) even
        // though the legacy interface signature is non-nullable: a body-less generation
        // surfaces as null HERE, and status reads on a live-but-disposed body throw their
        // actual native-shaped failure inside the leaf.
        if (actor is null || !actor.IsLive) return null!;
        return _physics ??= new(actor, unit, motion, diagnostics);
    }

    /// <summary>Physicalized stance half-extents (<c>ZoneActorPhysicalizationRequest.cs:124-128/:153-161</c>); falls back to the native constructor default box (.5, :118) when no stance row exists — used only for the correction distance threshold (:188/:254).</summary>
    public ZoneActorMovementCorrectionBounds GetBounds()
    {
        var size = new Vector3(.5f);
        var model = actor?.Model;
        if (model is not null)
        {
            var found = false;
            foreach (var row in model.Stances)
            {
                if (row.StanceId != 2) continue;
                size = new Vector3(row.SizeX, row.SizeY, row.SizeZ);
                found = true;
            }
            if (!found) size = new Vector3(.5f);
        }
        return new ZoneActorMovementCorrectionBounds(-size, size);
    }

    public Quaternion GetRotation() => unit.Pose.Rotation;

    public Vector3 GetPosition() => ZoneMovementPoseMath.ToMeters(unit.Pose.Position);

    /// <summary>TODO(part-owners): plain bodies have no part-owner chain; the gate reads this flag first (:280-283).</summary>
    public bool HasPartOwner => false;

    public int GetPartIdOffset() => 0;

    /// <summary>Unreachable while <see cref="HasPartOwner"/> is false (producer checks the flag, :280); named refusal instead of a stub.</summary>
    public IZoneActorMovementCorrectionEntity GetPartOwner() =>
        throw new NotSupportedException("Plain bodies have no part-owner chain (HasPartOwner is false).");

    /// <summary>
    /// Correction-side entity publication: the producer publishes a world correction with the
    /// engine's local-position flags (<c>ZoneActorMovementCorrection.cs:148-154</c>,
    /// flags 0x102000) → mirrored onto the entity through the explicit pose-publication seam
    /// (<c>ZoneNativeEntityServices.Pose.cs:31</c>, the same call
    /// <c>ZonePlayerActor.SyncEntityPose</c> uses, <c>ZonePlayerActorFactory.cs:625-634</c>).
    /// Body-less generations record a structured skip — never a transform-only ghost.
    /// </summary>
    public void SetPosition(Vector3 position, uint flags)
    {
        if (actor is null || !actor.IsLive)
        {
            diagnostics.ReportSkippedActor(unit.Snapshot.ObjectId,
                "correction SetPosition skipped: no live body to mirror (no transform-only publication).");
            return;
        }
        try
        {
            var entity = actor.Entity;
            if (entity is null) return;
            entity.PublishPose(position, unit.Pose.Rotation);
        }
        catch (ObjectDisposedException)
        {
            diagnostics.ReportSkippedActor(unit.Snapshot.ObjectId,
                "correction SetPosition skipped: native entity retired (Pointer==0 purge semantics).");
        }
    }
}

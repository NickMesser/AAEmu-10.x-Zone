using System.Numerics;

using AAEmu.Zone.Actors;

namespace AAEmu.Zone.Physics;

/// <summary>
/// The upright convex primitive used by a query or a character body.
/// </summary>
public enum ZonePhysicsShapeKind { Box, Capsule, Cylinder }

/// <summary>Generic character kinematics, or the authored living dimensions/controller profile.</summary>
public enum ZonePhysicsActorProfile { Kinematic, Living }

/// <summary>
/// Backend-neutral shape dimensions. For a box, <see cref="HalfExtents"/> contains its three
/// half sizes. For a capsule or cylinder, X is the radius, Z is the axial segment half-length,
/// and Y is unused, matching CLivingEntity::SetParams (35028BF0). Capsule hemispheres extend
/// beyond both ends of that segment. Query positions are primitive centers; the actor's authored
/// pivot and collider offset belong to <see cref="ZonePhysicsActorDescription"/>.
/// </summary>
public readonly record struct ZonePhysicsShape
{
    public ZonePhysicsShape(Vector3 HalfExtents, bool UseCapsule)
        : this(HalfExtents, UseCapsule ? ZonePhysicsShapeKind.Capsule : ZonePhysicsShapeKind.Box) { }

    public ZonePhysicsShape(Vector3 HalfExtents, ZonePhysicsShapeKind Kind)
    { this.HalfExtents = HalfExtents; this.Kind = Kind; }

    public Vector3 HalfExtents { get; init; }
    public ZonePhysicsShapeKind Kind { get; init; }
    public bool UseCapsule => Kind == ZonePhysicsShapeKind.Capsule;
    public float Radius => HalfExtents.X;
    public float SegmentHalfHeight => HalfExtents.Z;
    public Vector3 BoundingHalfExtents => Kind == ZonePhysicsShapeKind.Box ? HalfExtents :
        new(Radius, Radius, SegmentHalfHeight + (UseCapsule ? Radius : 0));

    public static ZonePhysicsShape Box(Vector3 halfExtents) => new(halfExtents, ZonePhysicsShapeKind.Box);
    public static ZonePhysicsShape Capsule(float radius, float segmentHalfHeight) =>
        new(new(radius, radius, segmentHalfHeight), ZonePhysicsShapeKind.Capsule);
    public static ZonePhysicsShape Cylinder(float radius, float halfHeight) =>
        new(new(radius, radius, halfHeight), ZonePhysicsShapeKind.Cylinder);
}

/// <summary>
/// What the scene must supply to register one actor body: its shape and mass. Character-geometry
/// loading, skeleton physicalization and content physicalization requests stay with their
/// existing owners (see <c>ZoneActorPhysicalizationRequest</c>,
/// Actors/ZoneActorPhysicalizationRequest.cs).
/// </summary>
public sealed record ZonePhysicsActorDescription(ZonePhysicsShape Shape, float Mass)
{
    /// <summary>Copied target material. Original living type4 narrows surface_idx to a signed10-bit part field.</summary>
    public ushort PhysicalMaterialId { get; init; }
    public ZonePhysicsActorProfile Profile { get; init; } = ZonePhysicsActorProfile.Kinematic;
    /// <summary>
    /// Collider center relative to the actor origin. Null preserves the legacy feet-origin
    /// convention for explicitly constructed descriptions. Authored living bodies set this to
    /// HeightCollider - HeightPivot, as the original 35028BF0 stores in the part's local position.
    /// </summary>
    public float? ColliderCenterOffsetZ { get; init; }

    /// <summary>
    /// Actor origin above its ground support. The upright native living step (3502DED0) settles
    /// position.Z at support height + HeightPivot; this is independent of the primitive's bottom.
    /// </summary>
    public float GroundPivotHeight { get; init; }
    /// <summary>Signed world Z acceleration. Native living dynamics negate the authored scalar gravity.</summary>
    public float Gravity { get; init; } = ZoneOwnedPlayerPhysics.Gravity;
    public float CenterOffsetZ => ColliderCenterOffsetZ ?? Shape.BoundingHalfExtents.Z;
    /// <summary>Copied authored living thresholds/dimensions; generic kinematic bodies have none.</summary>
    public ZonePhysicsLivingMovementSettings? LivingMovement { get; init; }
    /// <summary>Original living dynamics bActive. This is independent of authored IgnoreCollision.</summary>
    public bool CollisionActive { get; init; } = true;
    /// <summary>Copied type4 mode bit4. Its gameplay producer is separate from the movement solver.</summary>
    public bool LivingFlag4 { get; init; }

    /// <summary>
    /// Maps the recovered living dimensions only. IgnoreCollision remains content metadata until
    /// its native behavior is recovered; it is not present in the dimension block.
    /// </summary>
    public static ZonePhysicsActorDescription FromLivingStance(ZoneActorPhysicalizationStance stance, float mass,
        float gravity = ZoneOwnedPlayerPhysics.Gravity)
    {
        ArgumentNullException.ThrowIfNull(stance);
        return new(new(stance.Size, stance.UseCapsule ? ZonePhysicsShapeKind.Capsule : ZonePhysicsShapeKind.Cylinder), mass)
        {
            Profile = ZonePhysicsActorProfile.Living,
            ColliderCenterOffsetZ = stance.HeightCollider - stance.HeightPivot,
            GroundPivotHeight = stance.HeightPivot,
            Gravity = gravity,
            LivingMovement = ZonePhysicsLivingMovementSettings.FromStance(stance),
        };
    }

    /// <summary>
    /// Maps an initial authored living request, including its defined dynamics instead of
    /// substituting constructor slope/air coefficients. The copied settings retain no request.
    /// </summary>
    public static ZonePhysicsActorDescription FromLivingRequest(ZoneActorPhysicalizationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var mass = request.Dynamics.ReadSingle(0x28) ??
            throw new NotSupportedException("Managed living admission requires defined living mass.");
        var gravity = request.Dynamics.ReadSingle(0x1C) ??
            throw new NotSupportedException("Managed living admission requires defined scalar gravity.");
        var settings = ZonePhysicsLivingMovementSettings.FromRequest(request);
        return new(new(new(settings.Radius, request.Stance.Size.Y, settings.SegmentHalfHeight),
            settings.UseCapsule ? ZonePhysicsShapeKind.Capsule : ZonePhysicsShapeKind.Cylinder), mass)
        {
            PhysicalMaterialId = request.Dynamics.ReadUInt32(0x2C) is { } material &&
                material != ZoneActorPhysicalizationRequest.UnusedIntegerBits
                ? unchecked((ushort)((int)(material << 22) >> 22)) : (ushort)0,
            Profile = ZonePhysicsActorProfile.Living,
            ColliderCenterOffsetZ = settings.HeightCollider - settings.HeightPivot,
            GroundPivotHeight = settings.HeightPivot,
            Gravity = -gravity,
            LivingMovement = settings,
            CollisionActive = request.Dynamics.ReadUInt32(0x5C) is not { } active ||
                active == ZoneActorPhysicalizationRequest.UnusedIntegerBits || active != 0,
            LivingFlag4 = request.Dynamics.ReadUInt32(0x24) is { } mode &&
                mode != ZoneActorPhysicalizationRequest.UnusedIntegerBits && (mode & 1) != 0,
        };
    }
}

/// <summary>
/// A copied blocking-query result. Follows the copied-value pattern of <c>ZoneNativeTerrainRayHit</c>
/// (Native/Physics/ZoneNativeTerrainPhysicsWorld.cs) and <c>ZoneNativeSceneRayHit</c>
/// (Native/Physics/ZoneNativeTerrainPhysicsWorld.SceneQueries.cs): no borrowed pointer or backend
/// storage escapes, and the value stays usable after the backend retires.
/// </summary>
public readonly record struct ZonePhysicsHit(
    float Distance, Vector3 Position, Vector3 Normal, ushort PhysicalMaterialId);

/// <summary>A copied ground-query result; the managed shape of <c>ZoneSceneGroundSample</c>'s physical hit (Scene/ZoneSceneActivation.cs).</summary>
public readonly record struct ZonePhysicsGroundHit(
    float Distance, Vector3 Position, Vector3 Normal, ushort PhysicalMaterialId);

/// <summary>
/// Copied physical status of one actor body. Follows <c>ZoneNativeLivingStatus</c>
/// (Native/Physics/ZoneNativeTerrainPhysicsWorld.Living.cs): position, orientation and velocity are
/// snapshots; no native state is retained by the reader.
/// </summary>
public readonly record struct ZonePhysicsActorStatus(
    Vector3 Position, Quaternion Orientation, Vector3 Velocity, bool IsGrounded, bool IsFlying)
{
    /// <summary>Original status2+0x58/body572 cache; null means this backend does not copy it.</summary>
    public float? CachedLivingGroundHeight { get; init; }
}

/// <summary>
/// The one replaceable physics backend of a scene generation: stepping, sweep/collide queries,
/// ground queries, teleport, actor registration and status copy.
///
/// Invariants:
/// <list type="bullet">
/// <item>One scene owner: exactly one backend instance serves one scene generation. It is chosen
/// once at scene creation through <see cref="ZoneSceneBackendSelection"/> and cannot be swapped
/// while the scene lives; a second backend for the same scene is rejected.</item>
/// <item>One movement authority: this backend integrates physical state and reports it through
/// <see cref="CopyStatus"/>; it never authors gameplay movement. The only pose write it accepts
/// is <see cref="Teleport"/>, which is scene-authoritative repositioning issued by the scene's
/// single movement authority — AI providers and script services request movement through that
/// authority, never through this backend directly.</item>
/// <item>Actor registration mirrors the lifecycle: <see cref="AddActor"/> accepts only live
/// handles, and the scene owner calls <see cref="RemoveActor"/> before
/// <c>IZoneActorLifecycle.Retire</c>; any later operation on the removed handle is rejected.</item>
/// </list>
/// Queries are synchronous, take copied value records and return copied results — the property
/// <c>RaycastStaticScene</c> already guarantees (Native/Physics/ZoneNativeTerrainPhysicsWorld.SceneQueries.cs).
/// The current native implementation is <c>ZoneNativeTerrainPhysicsWorld</c>
/// (Native/Physics/ZoneNativeTerrainPhysicsWorld.cs), reached only through its adapter type.
/// </summary>
public interface IZonePhysicsBackend
{
    /// <summary>Registers one live actor's body. Rejects unknown or retired handles and duplicate registration.</summary>
    void AddActor(ZoneActorHandle actor, ZonePhysicsActorDescription description);

    /// <summary>Removes one actor's body. Idempotent for cleanup paths; the handle never becomes usable again on this backend.</summary>
    void RemoveActor(ZoneActorHandle actor);

    /// <summary>Advances the backend by one explicit scene period. Only the scene owner's single tick authority calls Step.</summary>
    void Step(TimeSpan elapsed);

    /// <summary>Scene-authoritative repositioning of a registered actor, clearing its residual velocity.</summary>
    void Teleport(ZoneActorHandle actor, Vector3 position, Quaternion orientation);

    /// <summary>Copies the current physical status of a registered actor; unknown or removed handles are rejected.</summary>
    void CopyStatus(ZoneActorHandle actor, out ZonePhysicsActorStatus status);

    /// <summary>
    /// Sweeps a shape along a bounded segment against static geometry plus registered bodies,
    /// excluding <paramref name="ignoreActor"/> (typically the querying actor itself). Returns
    /// the nearest blocking hit; zero-length segments are a miss, non-finite input is rejected.
    /// </summary>
    bool TrySweep(ZonePhysicsShape shape, Vector3 origin, Vector3 displacement,
        ZoneActorHandle? ignoreActor, out ZonePhysicsHit hit);

    /// <summary>
    /// Overlap test of a shape at one position against static geometry plus registered bodies,
    /// excluding <paramref name="ignoreActor"/>.
    /// </summary>
    bool TryCollide(ZonePhysicsShape shape, Vector3 position, ZoneActorHandle? ignoreActor);

    /// <summary>
    /// Downward ground query from <paramref name="origin"/> across at most
    /// <paramref name="maxDistance"/>; the primitive generalization of <c>RaycastStaticScene</c>
    /// (Native/Physics/ZoneNativeTerrainPhysicsWorld.SceneQueries.cs) used by ground sampling in
    /// <c>ZoneSceneActivation</c> (Scene/ZoneSceneActivation.cs).
    /// </summary>
    bool TryGround(Vector3 origin, float maxDistance, out ZonePhysicsGroundHit ground);
}

/// <summary>
/// Optional living-dimension capability. The scene requests a stance through its existing
/// movement owner. Acceptance means the request passed preflight and was queued; the scene's
/// next physics step applies it before simulation. The original queues before preflight, so
/// even a refused preflight can leave a command to drain; the actor keeps its desired request
/// pending. Applied dimensions remain physical truth and must not be inferred from the return
/// value. This does not create a second pose authority.
/// </summary>
public interface IZonePhysicsLivingStances
{
    bool TryChangeLivingStance(ZoneActorHandle actor, ZoneActorPhysicalizationStance stance, out string refusal);
}

/// <summary>Copied live dynamics updates issued by the actor's existing frame/input owner.</summary>
public interface IZonePhysicsLivingDynamics
{
    void SetLivingInertia(ZoneActorHandle actor, float inertia);
    void SetLivingCollisionActive(ZoneActorHandle actor, bool active);
}

public enum ZonePhysicsLivingVelocityMode { Normal = 0, Immediate = 3 }

/// <summary>Queued living wishes, separate from the owned actual velocity and collision response.</summary>
public interface IZonePhysicsLivingMovementRequests
{
    void ApplyLivingVelocityRequest(ZoneActorHandle actor, Vector3 requestedVelocity,
        ZonePhysicsLivingVelocityMode mode = ZonePhysicsLivingVelocityMode.Normal);
}

/// <summary>
/// Optional original network-correction boundary (PE virtual0xC0), distinct from Action4.
/// The copied tuple updates velocity fields now and queues its position for the next step.
/// It does not submit a mode3 wish or select a new orientation.
/// </summary>
public interface IZonePhysicsLivingCorrections
{
    bool TryApplyLivingCorrection(ZoneActorHandle actor, ZoneActorMovementCorrectionAction correction, out string refusal);
}

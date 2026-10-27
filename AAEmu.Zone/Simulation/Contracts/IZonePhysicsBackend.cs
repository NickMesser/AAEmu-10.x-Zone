using System.Numerics;

namespace AAEmu.Zone.Simulation.Contracts;

/// <summary>
/// Backend-neutral convex query/actor shape. The half-extent plus capsule-flag shape reuses the
/// admitted stance semantics of <c>ZoneActorPhysicalizationStance</c>
/// (Simulation/ZoneActorPhysicalizationRequest.cs); a native adapter maps it to its own physical
/// primitives inside the adapter. No native or ABI type crosses this boundary.
/// </summary>
public readonly record struct ZonePhysicsShape(Vector3 HalfExtents, bool UseCapsule);

/// <summary>
/// What the scene must supply to register one actor body: its shape and mass. Character-geometry
/// loading, skeleton physicalization and content physicalization requests stay with their
/// existing owners (see <c>ZoneActorPhysicalizationRequest</c>,
/// Simulation/ZoneActorPhysicalizationRequest.cs).
/// </summary>
public sealed record ZonePhysicsActorDescription(ZonePhysicsShape Shape, float Mass);

/// <summary>
/// A copied blocking-query result. Follows the copied-value pattern of <c>ZoneNativeTerrainRayHit</c>
/// (Simulation/ZoneNativeTerrainPhysicsWorld.cs) and <c>ZoneNativeSceneRayHit</c>
/// (Simulation/ZoneNativeTerrainPhysicsWorld.SceneQueries.cs): no borrowed pointer or backend
/// storage escapes, and the value stays usable after the backend retires.
/// </summary>
public readonly record struct ZonePhysicsHit(
    float Distance, Vector3 Position, Vector3 Normal, ushort PhysicalMaterialId);

/// <summary>A copied ground-query result; the managed shape of <c>ZoneSceneGroundSample</c>'s physical hit (Simulation/ZoneSceneActivation.cs).</summary>
public readonly record struct ZonePhysicsGroundHit(
    float Distance, Vector3 Position, Vector3 Normal, ushort PhysicalMaterialId);

/// <summary>
/// Copied physical status of one actor body. Follows <c>ZoneNativeLivingStatus</c>
/// (Simulation/ZoneNativeTerrainPhysicsWorld.Living.cs): position, orientation and velocity are
/// snapshots; no native state is retained by the reader.
/// </summary>
public readonly record struct ZonePhysicsActorStatus(
    Vector3 Position, Quaternion Orientation, Vector3 Velocity, bool IsGrounded, bool IsFlying);

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
/// <c>RaycastStaticScene</c> already guarantees (Simulation/ZoneNativeTerrainPhysicsWorld.SceneQueries.cs).
/// The current native implementation is <c>ZoneNativeTerrainPhysicsWorld</c>
/// (Simulation/ZoneNativeTerrainPhysicsWorld.cs), reached only through its adapter type.
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
    /// (Simulation/ZoneNativeTerrainPhysicsWorld.SceneQueries.cs) used by ground sampling in
    /// <c>ZoneSceneActivation</c> (Simulation/ZoneSceneActivation.cs).
    /// </summary>
    bool TryGround(Vector3 origin, float maxDistance, out ZonePhysicsGroundHit ground);
}

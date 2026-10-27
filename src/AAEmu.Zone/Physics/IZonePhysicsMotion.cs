using System.Numerics;

using AAEmu.Zone.Actors;

namespace AAEmu.Zone.Physics;

/// <summary>
/// Movement-authority motion application, sitting BESIDE <see cref="IZonePhysicsBackend"/>.
///
/// Why this interface exists (evidence, not preference): the replaceable backend exposes only
/// <c>Teleport</c> as a pose write (<c>Physics/IZonePhysicsBackend.cs:53-57</c>) and
/// no motion write at all, while its own invariant says gameplay movement is authored by the
/// scene's single movement authority and "AI providers and script services request movement
/// through that authority, never through this backend directly" (:52-55). The movement chain —
/// direct movement application (<c>Actors/ZoneActorMovementController.cs:18</c>
/// <c>ApplyDirectMovement</c>), the physical input producer's retained controller
/// (<c>Actors/ZoneActorMovementInput.cs:172</c> <c>input.Apply</c>) and physical correction
/// (<c>Actors/ZoneActorMovementCorrection.cs:250</c> <c>physical.Apply</c>) — needs a write
/// surface for exactly three operations, so it lands here instead of widening the backend
/// contract mid-scene. Registered in AAEmu.Zone/IMPLEMENTATION_OWNERSHIP.md section 4
/// (change log 2026-09-23) under the contract-folder extension allowance (Agent 4 precedent
/// <c>Ai/IZoneAiFramePump.cs:14-16</c>); flagged to Agent 5.
///
/// Invariants:
/// <list type="bullet">
/// <item><b>One movement authority per actor.</b> Every member is called only by the movement
/// chain of <c>Actors/ZoneActorMovementController.cs:76</c> (IMPLEMENTATION_OWNERSHIP.md
/// section 1, "One movement authority per actor"). The backend integrates and reports
/// (<c>IZonePhysicsBackend.CopyStatus</c>); this interface applies what the authority decided.</item>
/// <item><b>Handle-keyed, never UnitObjectId alone.</b> Keys are <c>ZoneActorHandle</c>
/// (UnitObjectId, Generation); equality never survives retirement
/// (<c>Actors/IZoneActorLifecycle.cs:30-38</c>). Unknown or removed handles are
/// rejected with <see cref="InvalidOperationException"/> — the exact contract of
/// <c>IZonePhysicsBackend.CopyStatus</c> (:79-80) mirrored here so a movement write can never
/// reach a retired generation.</item>
/// <item><b>Copied values only.</b> Every parameter is a value type; no native pointer or
/// backend storage crosses this boundary (<c>IZonePhysicsBackend.cs:23-34</c>).</item>
/// <item><b>Named refusals, never silent drops.</b> The concrete adapter over
/// <c>ZoneNativeTerrainPhysicsWorld</c> validates handle liveness and finiteness first, then
/// refuses the native write BY NAME with <c>TODO(pose-write)</c> until the bounded ABI probe
/// (implementation turn 3, PLAN B3) proves the living parameter/action type words — the same
/// evidence discipline that keeps <c>IZonePhysicsBackend.Teleport</c> and Agent 2's
/// <c>LivingTeleportDependency</c> (<c>Actors/ZonePlayerActorFactory.cs:58/:68-72</c>)
/// unresolved today. Queued parameter/action requests are replayed by the nonzero Step
/// (FIRST_ENTRY_NOTES.md:1774-1779), which is why these writes belong to the frame phases and
/// not to an ad-hoc writer.</item>
/// </list>
///
/// Coordinate domain: every position/velocity here is the ENGINE-LOCAL WORLD frame of the body
/// status copied by <c>IZonePhysicsBackend.CopyStatus</c> (copied values only,
/// <c>ZoneNativeTerrainPhysicsWorld.cs:12</c>); no local/wire conversion happens on this
/// interface — conversion belongs to the outgoing encoder and World's relay
/// (<c>AAEmu.WorldServer/AAEmu.World/Core/Relay/MovementRelay.cs:341-343</c>).
/// Clock domain: motion writes carry no clock; they land between <c>PrePhase</c> and
/// <c>StepPhysics</c> of one <c>ZoneMovementFrame</c> (<c>Actors/IZoneMovementFramePhases.cs</c>).
/// </summary>
public interface IZonePhysicsMotion
{
    /// <summary>
    /// Applies the authority's desired world velocity (m/s, engine-local world frame) to a
    /// registered body — walk/stop intent from the admitted movement input. Non-finite input is
    /// rejected by name; unknown or removed handles raise <see cref="InvalidOperationException"/>.
    /// </summary>
    void ApplyVelocity(ZoneActorHandle actor, Vector3 velocity);

    /// <summary>
    /// Applies an instantaneous velocity delta (m/s, engine-local world frame) — the jump
    /// impulse derived from content (model <c>game_jump_height</c> × <c>physics_living_gravity</c>,
    /// <c>Content/ZoneActorModelContent.cs:39/:68</c>) by the caller. Non-finite input is
    /// rejected by name; unknown or removed handles raise <see cref="InvalidOperationException"/>.
    /// </summary>
    void ApplyImpulse(ZoneActorHandle actor, Vector3 impulse);

    /// <summary>
    /// Applies an authoritative pose override (position + orientation, engine-local world
    /// frame): turn intent without displacement and physical correction both arrive here.
    /// Validation mirrors <c>IZonePhysicsBackend.Teleport</c> (:77) — finite position, finite
    /// nonzero rotation — before the native write itself is refused by name while
    /// <c>TODO(pose-write)</c> is open.
    /// </summary>
    void ApplyPose(ZoneActorHandle actor, Vector3 position, Quaternion orientation);
}

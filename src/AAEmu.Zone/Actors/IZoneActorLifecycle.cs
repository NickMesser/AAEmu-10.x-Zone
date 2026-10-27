using System.Numerics;

using AAEmu.Zone.Ai;
using AAEmu.Zone.Native.Entities;
using AAEmu.Zone.Physics;

namespace AAEmu.Zone.Actors;

/// <summary>The only actor kinds the shared lifecycle admits.</summary>
public enum ZoneActorKind
{
    Player,
    Npc,
}

/// <summary>
/// Everything the shared lifecycle needs to admit one actor: logical identity, kind, authored
/// model and a validated pose. This mirrors the admitted inputs of
/// <c>ZoneNativeEntityServices.CreateInitialCharacter</c> (Native/Entities/ZoneNativeEntityServices.cs);
/// a native adapter converts this record to its own request format strictly inside the adapter.
/// </summary>
public sealed record ZoneActorCreationRequest(
    uint UnitObjectId,
    ZoneActorKind Kind,
    uint ModelId,
    Vector3 Position,
    Quaternion Orientation,
    float Scale);

/// <summary>
/// One live actor generation. <see cref="UnitObjectId"/> is the logical identity that survives
/// scene re-entry (player reconnect, NPC respawn); <see cref="Generation"/> starts at 1 and
/// increases by exactly one on every re-entry creation for that identity, so handle equality
/// never survives retirement. The uniqueness rule follows <c>IZoneNativeFlowPlayerActorOwner</c>
/// (Native/FlowNodes/ZoneNativeFlowActorRegistry.cs): a retired generation is rejected by every
/// consumer even when an underlying allocator later reuses the same identity numbers.
/// </summary>
public readonly record struct ZoneActorHandle(uint UnitObjectId, uint Generation);

/// <summary>
/// Shared actor lifecycle for one scene generation: create, retire, and the re-entry identity
/// rules that connect them.
///
/// Scene invariants, binding on every implementation and consumer of these contracts:
/// <list type="bullet">
/// <item>One scene owner: exactly one lifecycle owner serves a scene generation (the role
/// <c>ZoneSceneActivation</c> plays today, Scene/ZoneSceneActivation.cs). A second owner
/// for the same scene generation is rejected; every create/retire belongs to that owner.</item>
/// <item>One movement authority: per live actor exactly one component authors movement. The
/// physics backend (<see cref="IZonePhysicsBackend"/>) integrates physical state and reports it;
/// it accepts only scene-authoritative teleports, never gameplay movement requests.</item>
/// <item>One AI backend per NPC: each live NPC actor is bound to exactly one
/// <see cref="IZoneAiProvider"/> instance for its whole generation; re-entry rebinds only after
/// the previous generation retired.</item>
/// </list>
///
/// Re-entry identity rules:
/// <list type="bullet">
/// <item><see cref="Create"/> for an identity whose actor is still live is rejected — there is
/// no silent replacement; retire first.</item>
/// <item><see cref="Create"/> after retirement returns a handle with the next generation: the
/// logical identity is preserved, handle equality is not. Backends must treat the old and new
/// handles as different actors.</item>
/// <item><see cref="Retire"/> is terminal. It is idempotent for cleanup paths (tolerating a
/// repeated call after an earlier partial failure, the policy of <c>IZoneNpcWorld.Remove</c>,
/// Npcs/IZoneNpcWorld.cs), but no operation makes a retired handle live again.</item>
/// </list>
/// </summary>
public interface IZoneActorLifecycle
{
    /// <summary>
    /// Admits one actor and issues its first (or next re-entry) generation. The request pose
    /// must be finite with a nonzero rotation and positive scale, matching the validation of
    /// <c>ZoneNativeEntityServices.CreateInitialCharacter</c>
    /// (Native/Entities/ZoneNativeEntityServices.cs).
    /// </summary>
    ZoneActorHandle Create(ZoneActorCreationRequest request);

    /// <summary>
    /// Permanently invalidates one actor generation. Callers retire the backends first
    /// (<c>IZonePhysicsBackend.RemoveActor</c>, then <c>IZoneAiProvider.RemoveNpc</c>) and this
    /// contract last; after it completes, every backend operation on the handle must be rejected.
    /// Repeated retirement of the same handle is a no-op.
    /// </summary>
    void Retire(ZoneActorHandle actor);

    /// <summary>Whether the exact generation is still live. Retired and unknown handles return false.</summary>
    bool IsLive(ZoneActorHandle actor);
}

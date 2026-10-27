using System.Numerics;

using AAEmu.Zone.Physics;

namespace AAEmu.Zone.Actors;

/// <summary>
/// Converts the movement chain's REGION-LOCAL pose domain into the backend's WORLD domain.
///
/// Why this type exists (measured, not assumed): the native movement correction works in a
/// region-local fixed-point frame — <c>ZoneActorMovementCorrectionMath.Local</c> subtracts a
/// 1024 m region origin — while the owned physics authority and the terrain raycast work in engine
/// WORLD coordinates. Mixing them is visible in one line of live evidence: with the player's
/// teleported world position (12, 20) and zone 129's region origin (9, 14) -> (9216, 14336) m, the
/// owned authority received (-9204, -14316) = (12 - 9216, 20 - 14336) and stepped off into empty
/// space.
///
/// Scope, deliberately narrow:
/// <list type="bullet">
/// <item>Only POSES are converted. Velocities and impulses are differences, so the origin cancels;
/// converting them would be wrong.</item>
/// <item>The conversion is applied to the motion surface the movement chain writes through, never
/// to the admission/teleport path, which is already in world coordinates. Wrapping at the boundary
/// keeps both domains honest instead of hiding the mismatch inside one of them.</item>
/// <item>It is a pure decorator: same handle rules, same refusals, no state of its own.</item>
/// </list>
/// </summary>
public sealed class ZoneLocalToWorldMotionAdapter : IZonePhysicsMotion
{
    private const float RegionMetres = 1024f;

    private readonly IZonePhysicsMotion _inner;
    private readonly Vector3 _originMetres;
    private readonly Func<ZoneActorHandle, float?> _authorityHeight;

    /// <param name="inner">The motion surface that actually applies writes (the selected backend).</param>
    /// <param name="regionOriginX">The scene's region origin X in 1024 m region units.</param>
    /// <param name="regionOriginY">The scene's region origin Y in 1024 m region units.</param>
    /// <param name="authorityHeight">
    /// Optional: the authority's current height for an actor, or null when it cannot say. The
    /// movement correction's vertical component is stale - it republishes the admission height
    /// forever (measured live: Z=98.46622 for a player actually standing at Z=321.1), which pulled
    /// the body off the ground mid-walk. With the owned backend as authority, horizontal motion is
    /// the correction's business and vertical is physics', so the incoming Z is dropped, not trusted.
    /// </param>
    public ZoneLocalToWorldMotionAdapter(IZonePhysicsMotion inner, int regionOriginX, int regionOriginY,
        Func<ZoneActorHandle, float?>? authorityHeight = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _originMetres = new Vector3(regionOriginX * RegionMetres, regionOriginY * RegionMetres, 0f);
        _authorityHeight = authorityHeight ?? (static _ => null);
    }

    /// <inheritdoc/>
    public void ApplyVelocity(ZoneActorHandle actor, Vector3 velocity) => _inner.ApplyVelocity(actor, velocity);

    /// <inheritdoc/>
    public void ApplyImpulse(ZoneActorHandle actor, Vector3 impulse) => _inner.ApplyImpulse(actor, impulse);

    /// <inheritdoc/>
    public void ApplyPose(ZoneActorHandle actor, Vector3 position, Quaternion orientation)
    {
        var world = position + _originMetres;
        // A correction may never drag the actor BELOW the height it is already standing at: the
        // stale correction Z (measured: 98.46622 for a player actually at 321.1) would otherwise pull
        // the body off its ground. It may, however, LIFT the actor - that rise is the jump the zone's
        // own input path produces (ActorFlags&8 -> JumpMode -> target.Z + advanceZ), and an earlier
        // version of this rule overwrote Z unconditionally, which silently erased every jump.
        if (_authorityHeight(actor) is { } height && world.Z <= height) world.Z = height;
        _inner.ApplyPose(actor, world, orientation);
    }
}

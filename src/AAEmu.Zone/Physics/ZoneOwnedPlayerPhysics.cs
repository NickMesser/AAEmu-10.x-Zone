using System.Numerics;

using AAEmu.Zone.Actors;

namespace AAEmu.Zone.Physics;

/// <summary>
/// OWNED player physics (Agent 8): the movement authority that does not depend on the native
/// CryPhysics body at all.
///
/// Why this type exists, and why it is honest rather than a shortcut: the native pose-write path
/// cannot move the player today. The recovered living action (<c>body vtable +0x50 =
/// 3502A300</c>) only applies INLINE when <c>body+0x47 &gt; 6</c>, and the BOUND player body
/// admitted by the live host reads <c>body+0x47 = 0x03</c> — the same value the unbound probe body
/// reported — so the action is deferred to a native frame pump this host does not own. Every
/// motion gate therefore refuses by name. That is the correct behavior for a host that is not the
/// physics authority.
///
/// This class makes the host the authority instead: it owns the player's transform, integrates
/// gravity, and resolves the ground with the scene's REAL terrain query
/// (<see cref="IZoneTerrainPhysicsWorld.RaycastTerrain"/>), which is already working in the live
/// scene. Nothing here is invented: the ground is the engine's terrain, and the integration is
/// ordinary kinematics. What this class deliberately does NOT do is claim the native body moved —
/// <see cref="TryReadPosition"/> reports the OWNED position, and the caller is told which authority
/// produced it, so no caller can mistake this for native agreement.
///
/// Invariants:
/// <list type="bullet">
/// <item>Handle-keyed, never UnitObjectId alone (one authority per actor, matching
/// <see cref="IZonePhysicsMotion"/>): a retired handle is rejected, never silently reused.</item>
/// <item>Copied values only; no native pointer crosses this boundary.</item>
/// <item>Named refusals: non-finite input, an unknown handle, and a step with no ground are all
/// reported, never smoothed over.</item>
/// <item>One gravity, one ground: the constants are named and public so a test asserts the same
/// values the runtime uses.</item>
/// </list>
/// </summary>
public sealed class ZoneOwnedPlayerPhysics : IZonePhysicsMotion
{
    /// <summary>
    /// The one thing owned physics needs from the scene: where the ground is. Kept deliberately
    /// minimal so a test can supply terrain without standing up an engine, and so the owned
    /// authority can never reach a native pointer.
    /// </summary>
    public interface IGroundQuery
    {
        /// <summary>
        /// Raycasts a column; false means "no terrain found" and never an invented floor.
        /// The material id is the AUTHORED surface the engine reported for the hit (Agent 7's neutral
        /// collision data), so the authority can hand a real surface to physics and the AI route
        /// instead of a constant.
        /// </summary>
        bool TryGround(Vector3 from, Vector3 to, out Vector3 position, out ushort physicalMaterialId);

        /// <summary>
        /// Resolves character support with the previous and proposed feet available. Terrain-only
        /// providers retain their wide column probe. Scene providers can restrict static support
        /// to reachable surfaces, so a roof overhead cannot become the character's floor.
        /// </summary>
        float ContactTolerance => 0;

        bool TryCharacterGround(Vector3 previousPosition, Vector3 nextPosition, bool wasGrounded,
            out Vector3 position, out ushort physicalMaterialId) =>
            TryGround(new(nextPosition.X, nextPosition.Y, previousPosition.Z + 2000f),
                new(nextPosition.X, nextPosition.Y, previousPosition.Z - 4000f),
                out position, out physicalMaterialId);
    }

    /// <summary>World gravity in m/s² (downward), matching the content gravity used for jump impulses.</summary>
    public const float Gravity = -9.81f;

    private readonly IGroundQuery? _terrain;
    private readonly Dictionary<ZoneActorHandle, Body> _bodies = [];
    private readonly List<ZoneActorHandle> _order = [];

    /// <param name="terrain">
    /// The scene's real ground query, used only to resolve the ground height. Null keeps the
    /// authority constructible, but then <see cref="Step"/> refuses by name: an owned authority with
    /// no terrain would have to invent a floor, and an invented floor is exactly the kind of
    /// fakery the motion gates exist to prevent.
    /// </param>
    public ZoneOwnedPlayerPhysics(IGroundQuery? terrain = null) => _terrain = terrain;

    /// <summary>One owned body: the authoritative transform, plus the motion state the frames own.</summary>
    private sealed class Body
    {
        internal Vector3 Position;
        internal Quaternion Orientation = Quaternion.Identity;
        internal Vector3 Velocity;
        internal bool Grounded;
        internal float GroundZ;
        internal bool HasGround;
        /// <summary>The AUTHORED surface id the engine reported for the current ground hit (Agent 7).</summary>
        internal ushort GroundMaterialId;
    }

    /// <summary>Registers an owned body at a starting pose. Re-registering a live handle is refused.</summary>
    public void AddActor(ZoneActorHandle actor, Vector3 position, Quaternion orientation)
    {
        RequireFinite(position, nameof(position));
        RequireFinite(orientation, nameof(orientation));
        if (orientation.LengthSquared() <= 0) throw new ArgumentException("Owned orientation must be a nonzero rotation.", nameof(orientation));
        if (_bodies.ContainsKey(actor))
            throw new InvalidOperationException($"Owned physics already owns actor {actor}; one authority per actor.");
        _bodies.Add(actor, new Body { Position = position, Orientation = orientation });
        _order.Add(actor);
    }

    /// <summary>Removes an owned body. Retiring an unknown handle is refused, never ignored.</summary>
    public void RemoveActor(ZoneActorHandle actor)
    {
        if (!_bodies.Remove(actor))
            throw new InvalidOperationException($"Owned physics has no actor {actor} to remove.");
        _order.Remove(actor);
    }

    /// <summary>Whether this authority currently owns the handle.</summary>
    public bool Owns(ZoneActorHandle actor) => _bodies.ContainsKey(actor);

    /// <inheritdoc/>
    public void ApplyVelocity(ZoneActorHandle actor, Vector3 velocity)
    {
        RequireFinite(velocity, nameof(velocity));
        OwnedBody(actor).Velocity = velocity;
    }

    /// <inheritdoc/>
    public void ApplyImpulse(ZoneActorHandle actor, Vector3 impulse)
    {
        RequireFinite(impulse, nameof(impulse));
        OwnedBody(actor).Velocity += impulse;
    }

    /// <inheritdoc/>
    public void ApplyPose(ZoneActorHandle actor, Vector3 position, Quaternion orientation)
    {
        RequireFinite(position, nameof(position));
        RequireFinite(orientation, nameof(orientation));
        if (orientation.LengthSquared() <= 0) throw new ArgumentException("Owned orientation must be a nonzero rotation.", nameof(orientation));
        var body = OwnedBody(actor);
        body.Position = position;
        body.Orientation = orientation;
        // An authoritative pose is a correction: the velocity it implies is zero, so a correction
        // never carries stale motion into the next step.
        body.Velocity = Vector3.Zero;
    }

    /// <summary>
    /// Advances every owned body by exactly one scene period: gravity, integration, then the ground
    /// resolution. Returns the number of bodies stepped. Refuses a non-positive elapsed time by
    /// name (a zero-time drain is a load, not a step) and refuses to invent a floor.
    /// </summary>
    public int Step(TimeSpan elapsed)
    {
        if (elapsed <= TimeSpan.Zero)
            throw new ArgumentException("Owned physics refuses a zero or negative step; that is a load, not a simulation step.", nameof(elapsed));
        if (_terrain is null)
            throw new NotSupportedException("Owned physics refused by name: no terrain physics world is attached, so a ground height would have to be invented.");
        var seconds = (float)elapsed.TotalSeconds;
        var stepped = 0;
        foreach (var actor in _order)
        {
            var body = _bodies[actor];
            // Z is UP in the engine-local world frame (terrain columns are cast along Z; player
            // positions read X/Y across the map and Z as height), so gravity belongs on Z. An
            // earlier revision put it on Y and the body drifted along the map instead of falling -
            // caught by the first owned-physics test run, which is exactly what it is for.
            body.Velocity += new Vector3(0f, 0f, Gravity * seconds);
            var next = body.Position + body.Velocity * seconds;
            if (_terrain.TryCharacterGround(body.Position, next, body.Grounded, out var ground, out var materialId))
            {
                body.GroundZ = ground.Z;
                body.GroundMaterialId = materialId;
                body.HasGround = true;
                if (next.Z <= ground.Z + _terrain.ContactTolerance)
                {
                    next.Z = ground.Z;
                    // Gravity runs on Z, so the LANDING velocity to cancel is Z as well. An earlier
                    // revision cleared Y here, which left the vertical velocity uncancelled and made
                    // a resting body oscillate around the ground height instead of settling on it.
                    if (body.Velocity.Z < 0f) body.Velocity.Z = 0f;
                    body.Grounded = true;
                }
                else
                {
                    body.Grounded = false;
                }
            }
            else
            {
                // No terrain under the column: keep integrating and say so, rather than snapping
                // the actor to an invented floor.
                body.HasGround = false;
                body.Grounded = false;
            }
            body.Position = next;
            stepped++;
        }
        return stepped;
    }

    /// <summary>Copies the OWNED position of an actor. This is this authority's truth, not the native body's.</summary>
    public bool TryReadPosition(ZoneActorHandle actor, out Vector3 position)
    {
        if (_bodies.TryGetValue(actor, out var body))
        {
            position = body.Position;
            return true;
        }
        position = default;
        return false;
    }

    /// <summary>Whether the actor is currently standing on resolved terrain.</summary>
    public bool TryReadGrounded(ZoneActorHandle actor, out bool grounded)
    {
        if (_bodies.TryGetValue(actor, out var body))
        {
            grounded = body.Grounded;
            return true;
        }
        grounded = false;
        return false;
    }

    /// <summary>Copies the OWNED velocity of an actor (the velocity this authority last applied).</summary>
    public bool TryReadVelocity(ZoneActorHandle actor, out Vector3 velocity)
    {
        if (_bodies.TryGetValue(actor, out var body))
        {
            velocity = body.Velocity;
            return true;
        }
        velocity = default;
        return false;
    }

    /// <summary>Copies the OWNED orientation of an actor.</summary>
    public bool TryReadOrientation(ZoneActorHandle actor, out Quaternion orientation)
    {
        if (_bodies.TryGetValue(actor, out var body))
        {
            orientation = body.Orientation;
            return true;
        }
        orientation = Quaternion.Identity;
        return false;
    }

    /// <summary>The ground height last resolved for this actor, or false when no terrain was hit.</summary>
    public bool TryReadGroundHeight(ZoneActorHandle actor, out float groundZ)
    {
        if (_bodies.TryGetValue(actor, out var body) && body.HasGround)
        {
            groundZ = body.GroundZ;
            return true;
        }
        groundZ = 0f;
        return false;
    }

    /// <summary>
    /// The AUTHORED surface id under this actor right now, for Agent 7's consumers. False means the
    /// actor has no ground hit at all (airborne or over absent terrain) - never a made-up surface.
    /// </summary>
    public bool TryReadGroundMaterial(ZoneActorHandle actor, out ushort physicalMaterialId)
    {
        if (_bodies.TryGetValue(actor, out var body) && body.HasGround)
        {
            physicalMaterialId = body.GroundMaterialId;
            return true;
        }
        physicalMaterialId = 0;
        return false;
    }

    private Body OwnedBody(ZoneActorHandle actor) => _bodies.TryGetValue(actor, out var body)        ? body
        : throw new InvalidOperationException($"Owned physics has no actor {actor}; a movement write must never reach a retired generation.");

    private static void RequireFinite(Vector3 value, string name)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z))
            throw new ArgumentException($"Owned physics refused non-finite {name}.", name);
    }

    private static void RequireFinite(Quaternion value, string name)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z) || !float.IsFinite(value.W))
            throw new ArgumentException($"Owned physics refused non-finite {name}.", name);
    }
}

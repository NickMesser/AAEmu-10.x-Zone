using System.Numerics;

using AAEmu.Zone.Actors;

namespace AAEmu.Zone.Physics;

/// <summary>
/// Owns copied actor poses and velocities independently of any native physical body. Integrates
/// each actor's explicit signed gravity and resolves its authored ground reference through the
/// scene provider. Missing surface samples mean free fall; a missing provider is a named refusal.
/// Handle identities and caller-controlled retirement preserve one authority per generation.
/// Legacy providers retain zero-pivot/default-gravity behavior. Reading this authority's pose
/// does not imply that a comparison native body moved, and no native pointer crosses this boundary.
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

        /// <summary>
        /// Height of the actor's entity origin above its upright ground reference point.
        /// Native living dimensions keep this pivot independent of the collider's center/size.
        /// Existing ground providers use an origin on the ground and retain a zero offset.
        /// </summary>
        float GetGroundPivotHeight(ZoneActorHandle actor) => 0;

        bool TryCharacterGround(Vector3 previousPosition, Vector3 nextPosition, bool wasGrounded,
            out Vector3 position, out ushort physicalMaterialId) =>
            TryGround(new(nextPosition.X, nextPosition.Y, previousPosition.Z + 2000f),
                new(nextPosition.X, nextPosition.Y, previousPosition.Z - 4000f),
                out position, out physicalMaterialId);

        /// <summary>
        /// Actor-aware support query. Scene providers can use copied authored living dimensions;
        /// existing providers retain their previous trajectory-based support behavior.
        /// Positions are ground references, with the actor's pivot already subtracted.
        /// </summary>
        bool TryCharacterGround(ZoneActorHandle actor, Vector3 previousPosition, Vector3 nextPosition,
            bool wasGrounded, out Vector3 position, out ushort physicalMaterialId) =>
            TryCharacterGround(previousPosition, nextPosition, wasGrounded, out position, out physicalMaterialId);
    }

    /// <summary>Legacy default signed Z acceleration in m/s²; an actor can supply authored gravity.</summary>
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
        internal float Gravity = ZoneOwnedPlayerPhysics.Gravity;
        internal bool Grounded;
        internal float GroundZ;
        internal bool HasGround;
        /// <summary>The AUTHORED surface id the engine reported for the current ground hit (Agent 7).</summary>
        internal ushort GroundMaterialId;
    }

    /// <summary>Registers an owned body at a starting pose. Re-registering a live handle is refused.</summary>
    public void AddActor(ZoneActorHandle actor, Vector3 position, Quaternion orientation, float gravity = Gravity)
    {
        RequireFinite(position, nameof(position));
        RequireFinite(orientation, nameof(orientation));
        if (!float.IsFinite(gravity)) throw new ArgumentOutOfRangeException(nameof(gravity), "Owned gravity must be finite.");
        if (!float.IsFinite(orientation.LengthSquared()) || orientation.LengthSquared() <= 0)
            throw new ArgumentException("Owned orientation must be a finite nonzero rotation.", nameof(orientation));
        if (_bodies.ContainsKey(actor))
            throw new InvalidOperationException($"Owned physics already owns actor {actor}; one authority per actor.");
        _bodies.Add(actor, new Body { Position = position, Orientation = orientation, Gravity = gravity });
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
        if (!float.IsFinite(orientation.LengthSquared()) || orientation.LengthSquared() <= 0)
            throw new ArgumentException("Owned orientation must be a finite nonzero rotation.", nameof(orientation));
        var body = OwnedBody(actor);
        body.Position = position;
        body.Orientation = orientation;
        // An authoritative pose is a correction: the velocity it implies is zero, so a correction
        // never carries stale motion into the next step.
        body.Velocity = Vector3.Zero;
    }

    /// <summary>
    /// Explicit authoritative repositioning. The new location has no validated support until
    /// the scene queries it; a previous ground/material must not survive a teleport.
    /// </summary>
    public void Teleport(ZoneActorHandle actor, Vector3 position, Quaternion orientation)
    {
        ApplyPose(actor, position, orientation);
        ClearGroundContact(OwnedBody(actor));
    }

    /// <summary>
    /// Commits one scene-validated contact correction. Orientation and authored gravity remain
    /// unchanged; the caller supplies the resolved velocity instead of fabricating pose velocity.
    /// All values are checked before either position, velocity or cached support is changed.
    /// </summary>
    internal void CorrectContactPose(ZoneActorHandle actor, Vector3 position, Vector3 velocity)
    {
        RequireFinite(position, nameof(position));
        RequireFinite(velocity, nameof(velocity));
        var body = OwnedBody(actor);
        body.Position = position;
        body.Velocity = velocity;
        ClearGroundContact(body);
    }

    private static void ClearGroundContact(Body body)
    {
        body.Grounded = false;
        body.HasGround = false;
        body.GroundZ = 0;
        body.GroundMaterialId = 0;
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
            var pivot = _terrain.GetGroundPivotHeight(actor);
            if (!float.IsFinite(pivot))
                throw new InvalidOperationException($"Owned physics received a non-finite ground pivot for {actor}.");
            var pivotOffset = new Vector3(0, 0, pivot);
            // Z is UP in the engine-local world frame (terrain columns are cast along Z; player
            // positions read X/Y across the map and Z as height), so gravity belongs on Z. An
            // earlier revision put it on Y and the body drifted along the map instead of falling -
            // caught by the first owned-physics test run, which is exactly what it is for.
            body.Velocity += new Vector3(0f, 0f, body.Gravity * seconds);
            var next = body.Position + body.Velocity * seconds;
            if (_terrain.TryCharacterGround(actor, body.Position - pivotOffset, next - pivotOffset, body.Grounded,
                out var ground, out var materialId))
            {
                body.GroundZ = ground.Z;
                body.GroundMaterialId = materialId;
                body.HasGround = true;
                if (next.Z - pivot <= ground.Z + _terrain.ContactTolerance)
                {
                    next.Z = ground.Z + pivot;
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

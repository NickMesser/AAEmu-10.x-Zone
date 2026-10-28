using System.Numerics;

using AAEmu.Zone.Actors;
using AAEmu.Zone.Physics;

namespace AAEmu.Zone.Native.Physics;

/// <summary>
/// The missing <see cref="IZonePhysicsBackend"/> implementation over one scene generation's
/// <see cref="ZoneNativeTerrainPhysicsWorld"/> (the KNOWN GAP of
/// <c>Scene/ZoneSceneBackendSelection.cs:5-7</c>: <c>NativeTerrain</c> is
/// "reached only through that adapter type").
///
/// Invariants kept at this surface (all cited):
/// <list type="bullet">
/// <item>One scene owner: exactly one adapter instance serves one scene generation
/// (<c>Physics/IZonePhysicsBackend.cs:49-52</c>,
/// <c>Scene/ZoneSceneBackendSelection.cs:41-44</c>); constructed per scene and
/// never swapped (wiring note recorded in IMPLEMENTATION_OWNERSHIP.md, change log 2026-09-23).</item>
/// <item>One movement authority: this backend integrates and reports through
/// <see cref="CopyStatus"/>; it authors no gameplay movement. Its pose/motion writes
/// (<see cref="Teleport"/>, <see cref="IZonePhysicsMotion.ApplyVelocity"/>,
/// <see cref="IZonePhysicsMotion.ApplyImpulse"/>, <see cref="IZonePhysicsMotion.ApplyPose"/>)
/// all refuse by name because the native reposition/velocity seam is unproven (see those
/// methods; turn-3 ABI probe flips them together).</item>
/// <item>Lifecycle order: callers <see cref="RemoveActor"/> before <c>IZoneActorLifecycle.Retire</c>
/// (<c>Actors/IZoneActorLifecycle.cs:80-82</c>); as belt-and-braces, any operation
/// that observes a body whose native pointer already retired
/// (<c>ZoneNativeTerrainPhysicsWorld.LivingEntity.Pointer == 0</c>, set on destroy,
/// <c>Native/Physics/ZoneNativeTerrainPhysicsWorld.Living.cs:224-266</c>) purges the registration and
/// rejects the handle — a handle the wiring forgot to remove still dies with the actor.</item>
/// <item>Copied values only: every query and status returns owned records; no native pointer or
/// backend storage escapes (<c>Physics/IZonePhysicsBackend.cs:23-34/:60-63</c>).</item>
/// </list>
///
/// Handle-keyed body registry: keys are <see cref="ZoneActorHandle"/> (UnitObjectId, Generation),
/// never the UnitObjectId alone — handle equality never survives retirement
/// (<c>Actors/IZoneActorLifecycle.cs:30-38</c>).
/// Registered in AAEmu.Zone/IMPLEMENTATION_OWNERSHIP.md section 4 (change log 2026-09-23).
/// </summary>
public sealed class ZoneNativePhysicsBackendAdapter : IZonePhysicsBackend, IZonePhysicsMotion
{
    private readonly ZoneNativeTerrainPhysicsWorld _world;
    private readonly IZoneActorLifecycle? _lifecycle;
    private readonly Dictionary<ZoneActorHandle, Registration> _bodies = [];

    /// <param name="world">The one native physics world of this scene generation.</param>
    /// <param name="lifecycle">
    /// Optional scene lifecycle (in practice Agent 2's <see cref="ZonePlayerActorFactory"/>,
    /// which implements it): required for <see cref="AddActor"/> liveness checks and the only
    /// body source today. Null keeps the adapter constructible for managed-only harnesses, but
    /// <see cref="AddActor"/> then refuses by name.
    /// </param>
    public ZoneNativePhysicsBackendAdapter(ZoneNativeTerrainPhysicsWorld world, IZoneActorLifecycle? lifecycle = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        _world = world;
        _lifecycle = lifecycle;
    }

    private sealed record Registration(ZoneNativeTerrainPhysicsWorld.LivingEntity Body, ZonePhysicsShape Shape);

    /// <summary>
    /// Registers one live actor's body by ADOPTING the factory-created native body — nothing
    /// creates a second body here (<c>ZonePlayerActor.Body</c> is attached during admission,
    /// <c>Actors/ZonePlayerActorFactory.cs:577/:589</c>).
    /// Rejects: missing lifecycle (named), non-live/unknown handles (contract
    /// <c>Physics/IZonePhysicsBackend.cs:69-70</c>), duplicate registration,
    /// invalid shapes/mass, and handles with no factory body —
    /// <c>TODO(ctor-bodies)</c>: NPC creation waves need their own body-construction owner.
    /// </summary>
    public void AddActor(ZoneActorHandle actor, ZonePhysicsActorDescription description)
    {
        ArgumentNullException.ThrowIfNull(description);
        if (_lifecycle is null)
            throw new NotSupportedException(
                "ZoneNativePhysicsBackendAdapter requires the scene's IZoneActorLifecycle to validate actor " +
                "liveness before body adoption; constructed without one, AddActor refuses by name.");
        if (!_lifecycle.IsLive(actor))
            throw new InvalidOperationException(
                $"Actor handle {actor} is unknown or retired; AddActor accepts only live generations " +
                $"(IZonePhysicsBackend.cs:69-70; IZoneActorLifecycle.IsLive, IZoneActorLifecycle.cs:87-89).");
        if (_bodies.ContainsKey(actor))
            throw new InvalidOperationException(
                $"Actor handle {actor} is already registered on this backend; duplicate registration is " +
                $"rejected (IZonePhysicsBackend.cs:69).");

        var shape = description.Shape;
        if (!Finite(shape.HalfExtents) || shape.HalfExtents.X <= 0 || shape.HalfExtents.Y <= 0 || shape.HalfExtents.Z <= 0)
            throw new ArgumentException("An actor shape requires finite, strictly positive half-extents.", nameof(description));
        if (!float.IsFinite(description.Mass) || description.Mass <= 0)
            throw new ArgumentException("An actor description requires a finite, positive mass.", nameof(description));

        var body = (_lifecycle as ZonePlayerActorFactory)?.Find(actor)?.Body;
        if (body is null)
            throw new NotSupportedException(
                $"TODO(ctor-bodies): handle {actor} has no factory-created native body to adopt. This backend " +
                $"adopts bodies produced by ZonePlayerActorFactory admission only " +
                $"(IMPLEMENTATION_OWNERSHIP.md section 3); NPC creation waves need their own body owner.");
        Adopt(actor, body, shape);
    }

    /// <summary>
    /// Adopts an already-created native body under its handle (wiring order: adoption happens
    /// AFTER factory admission produced the body — IMPLEMENTATION_OWNERSHIP.md change-log wiring
    /// note, 2026-09-23). A wrapper whose native pointer already retired
    /// (<c>Native/Physics/ZoneNativeTerrainPhysicsWorld.Living.cs:202/:221-263</c>) is refused.
    /// </summary>
    internal void Adopt(ZoneActorHandle actor, ZoneNativeTerrainPhysicsWorld.LivingEntity body, ZonePhysicsShape shape)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (body.Pointer == 0)
            throw new NotSupportedException(
                $"TODO(ctor-bodies): handle {actor} carries a retired native body wrapper " +
                $"(LivingEntity.Pointer == 0); there is nothing to adopt.");
        if (_bodies.ContainsKey(actor))
            throw new InvalidOperationException(
                $"Actor handle {actor} is already registered on this backend; duplicate registration is rejected.");
        _bodies[actor] = new Registration(body, shape);
    }

    /// <summary>
    /// Removes one actor's body. Idempotent purge: the handle never becomes usable again here —
    /// every later operation rejects it (contract <c>IZonePhysicsBackend.cs:72-73/:79-80</c>).
    /// Wiring still mandates calling this before <c>IZoneActorLifecycle.Retire</c>
    /// (<c>IZoneActorLifecycle.cs:80-82</c>); the <c>Pointer == 0</c> purge inside
    /// <see cref="Resolve"/> is belt-and-braces for a missed call, not permission to miss it.
    /// </summary>
    public void RemoveActor(ZoneActorHandle actor) => _bodies.Remove(actor);

    /// <summary>
    /// Advances the backend by exactly one scene period — the scene owner's single tick
    /// authority calls this (contract <c>IZonePhysicsBackend.cs:75-76</c>); it maps 1:1 onto
    /// <see cref="ZoneNativeTerrainPhysicsWorld.Step"/>, which refuses non-positive periods by
    /// name ("a zero-time drain is not a simulation step") and holds the Step admission profile.
    /// </summary>
    public void Step(TimeSpan elapsed) => _world.Step(elapsed);

    /// <summary>
    /// Scene-authoritative repositioning. The handle and pose are validated first (unknown or
    /// removed handles raise <see cref="InvalidOperationException"/>, non-finite poses raise
    /// <see cref="ArgumentOutOfRangeException"/>), then the write itself is REFUSED BY NAME:
    /// <c>ZoneNativeTerrainPhysicsWorld</c> binds no living position-parameter write —
    /// <c>TODO(pose-write)</c>. Publishing only an entity transform would silently diverge body
    /// and transform, so nothing is written. Evidence:
    /// <c>ZonePlayerActorFactory.LivingTeleportDependency</c> (<c>Actors/ZonePlayerActorFactory.cs:58</c>,
    /// unresolved with evidence at :68-72 — "wire the native reposition call site (Agent 3/5
    /// backend adapter seam), then flip IsResolved") and <c>ZonePlayerActor.Teleport</c>
    /// (<c>Actors/ZonePlayerActorFactory.cs:648-655</c>) refuse identically today.
    /// This adapter flips neither; the seam lands only on bounded ABI-probe proof (PLAN B3,
    /// implementation turn 3: typed parameter write through body vslot 0x38, no fabricated
    /// type words — first-entry evidence: parameter writes are queued by 35023200 and replayed
    /// by TimeStep, FIRST_ENTRY_NOTES.md:1774-1779).
    /// </summary>
    public void Teleport(ZoneActorHandle actor, Vector3 position, Quaternion orientation)
    {
        var registration = Resolve(actor); // unknown/removed handle must raise InvalidOperationException, not the seam refusal
        if (!Finite(position) || !Finite(new Vector3(orientation.X, orientation.Y, orientation.Z)) ||
            !float.IsFinite(orientation.W) || orientation.LengthSquared() <= 0)
            throw new ArgumentOutOfRangeException(nameof(position),
                "Teleport requires a finite position and a finite, nonzero rotation.");
        ApplyAbsoluteMove(registration, position, "Teleport");
    }

    /// <summary>
    /// Refuses repositioning until the production position-parameter binding owns body/transform
    /// updates. Original3502A300 action type1 controls velocity; no action or transform is written.
    /// </summary>
    private void ApplyAbsoluteMove(Registration registration, Vector3 position, string operation)
    {
        if (!_world.TryApplyLivingMove(registration.Body, position, absolute: true, out _, out var refusal))
            throw new NotSupportedException(
                $"{operation} refused by name: {refusal}. No native velocity action or entity transform was written.");
    }

    /// <summary>
    /// Copies the registered body's physical status. Unknown or removed handles raise
    /// <see cref="InvalidOperationException"/> (contract <c>IZonePhysicsBackend.cs:81-82</c>).
    /// Mapping is the exact precedent of <c>ZonePlayerActor.TryCopyStatus</c>
    /// (<c>Actors/ZonePlayerActorFactory.cs:612-624</c>): position/orientation/velocity from
    /// <c>ZoneNativeLivingStatus</c> (<c>Native/Physics/ZoneNativeTerrainPhysicsWorld.Living.cs:49-50</c>),
    /// grounded = exact complement of the native flying bit — nothing invented.
    /// </summary>
    public void CopyStatus(ZoneActorHandle actor, out ZonePhysicsActorStatus status)
    {
        var registration = Resolve(actor);
        var native = registration.Body.GetStatus();
        // status2 exposes the native flying bit only (Native/Physics/ZoneNativeTerrainPhysicsWorld.Living.cs:199-222);
        // grounded is its exact complement — no separate native grounded flag is captured, and none is invented.
        status = new ZonePhysicsActorStatus(native.Position, native.Orientation, native.Velocity,
            !native.IsFlying, native.IsFlying);
    }

    // ------------------------------------------------------------------
    // IZonePhysicsMotion — the movement-authority write surface beside the
    // backend contract (Contracts/IZonePhysicsMotion.cs, registered section 4
    // change log 2026-09-23). Handle liveness and value validation run FIRST
    // (removed handle = InvalidOperationException, non-finite = rejected by
    // name), then the native write itself REFUSES BY NAME while
    // TODO(pose-write) is open — the identical evidence posture as Teleport
    // above: ZoneNativeTerrainPhysicsWorld binds no living velocity/pose
    // parameter write, so an entity-only publication would silently diverge
    // body and transform (ZonePlayerActorFactory.cs:58/:68-72). The bounded
    // ABI probe of implementation turn 3 (PLAN B3: typed parameter write
    // through body vslot 0x38, no fabricated type words — queued writes are
    // replayed by the nonzero Step, FIRST_ENTRY_NOTES.md:1774-1779) flips
    // these three members and Teleport together, on proof only.
    // ------------------------------------------------------------------

    private Registration EnterMotionWrite(ZoneActorHandle actor)
    {
        // Resolve first: an unknown/removed handle must raise InvalidOperationException
        // (IZonePhysicsBackend.cs:81-82), never the seam refusal.
        return Resolve(actor);
    }

    /// <inheritdoc cref="IZonePhysicsMotion.ApplyVelocity"/>
    public void ApplyVelocity(ZoneActorHandle actor, Vector3 velocity)
    {
        EnterMotionWrite(actor);
        if (!Finite(velocity))
            throw new ArgumentOutOfRangeException(nameof(velocity),
                "ApplyVelocity requires a finite world velocity (engine-local frame).");
        throw new NotSupportedException(
            "Movement-authoritative velocity write is refused by name: the recovered living ABI is POSITIONAL, not " +
            "velocity-valued (cryphysics_dedicate 3502A300 body vtable +0x50, pe_action {type,x,y,z,mode}, type 1 " +
            "= move, mode 2 = relative delta — AAEmuResearch/ZoneAuthority/pose-write). A velocity is therefore " +
            "expressed as a per-frame relative move owned by the movement frame phase, and an entity-only " +
            "publication would still diverge body and transform (ZonePlayerActorFactory.cs:58/:68-72).");
    }

    /// <inheritdoc cref="IZonePhysicsMotion.ApplyImpulse"/>
    public void ApplyImpulse(ZoneActorHandle actor, Vector3 impulse)
    {
        EnterMotionWrite(actor);
        if (!Finite(impulse))
            throw new ArgumentOutOfRangeException(nameof(impulse),
                "ApplyImpulse requires a finite world impulse (engine-local frame).");
        throw new NotSupportedException(
            "Movement-authoritative impulse write is refused by name: no impulse action exists in the recovered " +
            "living ABI — the only action type recovered is type 1 move (cryphysics_dedicate 3502A300, body vtable " +
            "+0x50; the full registered type tables carry parameters, not impulses; " +
            "AAEmuResearch/ZoneAuthority/pose-write). Nothing is published and nothing is faked.");
    }

    /// <inheritdoc cref="IZonePhysicsMotion.ApplyPose"/>
    public void ApplyPose(ZoneActorHandle actor, Vector3 position, Quaternion orientation)
    {
        var registration = EnterMotionWrite(actor);
        if (!Finite(position) || !Finite(new Vector3(orientation.X, orientation.Y, orientation.Z)) ||
            !float.IsFinite(orientation.W) || orientation.LengthSquared() <= 0)
            throw new ArgumentOutOfRangeException(nameof(position),
                "ApplyPose requires a finite position and a finite, nonzero rotation.");
        ApplyAbsoluteMove(registration, position, "ApplyPose");
    }

    /// <summary>
    /// Sweeps a shape along a bounded segment against static geometry plus registered bodies,
    /// excluding <paramref name="ignoreActor"/> (contract <c>IZonePhysicsBackend.cs:84-90</c>).
    /// Non-finite input is rejected; a zero-length segment is a miss.
    /// <list type="bullet">
    /// <item>Static half: one bounded-segment <c>RaycastStaticScene</c> family
    /// (<c>Native/Physics/ZoneNativeTerrainPhysicsWorld.SceneQueries.cs:26-57</c>; grid prerequisite
    /// enforced inside it, <c>Native/Physics/ZoneNativeTerrainPhysicsWorld.EntityGrid.cs:71-78</c>)
    /// — a center arm plus four cross arms offset by the shape's circumscribed radius, each
    /// arm's contact candidate = <c>max(0, hitDistance - radius)</c>: conservative for head-on
    /// contact (sphere touches the surface exactly a radius before the center ray does) and
    /// bounding the swept tube's arms laterally. Bounded by construction (five rays per sweep,
    /// no per-step loop). TODO(scene-sweep): an exact native shape sweep would remove the
    /// cross-arm coverage bound; no such surface exists in this tree, and none is fabricated.</item>
    /// <item>Body half: exact segment-vs-sphere tests (sphere radius = sum of both shapes'
    /// circumscribed radii, orientation-independent and therefore conservative) against each
    /// other registered body's COPIED status — <c>GetStatus</c> returns owned values
    /// (<c>Native/Physics/ZoneNativeTerrainPhysicsWorld.Living.cs:199-222</c>), no pointer escapes.</item>
    /// </list>
    /// </summary>
    public bool TrySweep(ZonePhysicsShape shape, Vector3 origin, Vector3 displacement,
        ZoneActorHandle? ignoreActor, out ZonePhysicsHit hit)
    {
        hit = default;
        ValidateShape(shape);
        if (!Finite(origin) || !Finite(displacement) || !float.IsFinite(displacement.LengthSquared()) ||
            !Finite(origin + displacement))
            throw new ArgumentOutOfRangeException(nameof(displacement),
                "Sweep queries require finite bounded segments.");
        var lengthSquared = displacement.LengthSquared();
        if (lengthSquared == 0) return false; // zero-length segments are a miss (IZonePhysicsBackend.cs:87)
        var length = MathF.Sqrt(lengthSquared);
        var radius = shape.HalfExtents.Length();

        // --- static geometry: center arm + four cross arms, nearest conservative contact wins ---
        var best = float.MaxValue;
        PerpendicularBasis(displacement, out var u, out var w);
        ReadOnlySpan<Vector3> offsets = [Vector3.Zero, u * radius, -u * radius, w * radius, -w * radius];
        foreach (var offset in offsets)
        {
            if (!_world.RaycastStaticScene(origin + offset, displacement, out var nativeHit)) continue;
            var contact = nativeHit.Distance - radius;
            if (contact < 0) contact = 0;
            if (contact >= best) continue;
            best = contact;
            hit = new ZonePhysicsHit(contact, nativeHit.Position, nativeHit.Normal, nativeHit.PhysicalMaterialId);
        }

        // --- registered bodies: exact segment vs circumscribed sphere of the summed radii ---
        foreach (var (handle, registration) in SnapshotBodies())
        {
            if (handle == ignoreActor) continue; // excluding the querying actor itself (IZonePhysicsBackend.cs:89)
            if (registration.Body.Pointer == 0) { _bodies.Remove(handle); continue; } // retired wrapper purge
            var status = registration.Body.GetStatus();
            var bodyRadius = radius + registration.Shape.HalfExtents.Length();
            if (!TrySegmentSphere(origin, displacement, status.Position, bodyRadius, out var t, out var point))
                continue;
            var distance = t * length;
            if (distance >= best) continue;
            best = distance;
            var normal = point - status.Position;
            normal = normal.LengthSquared() > 0 ? Vector3.Normalize(normal) : -displacement / length;
            hit = new ZonePhysicsHit(distance, point, normal, 0); // body contact: no native physical material id (0)
        }

        return best != float.MaxValue;
    }

    /// <summary>
    /// Overlap test of a shape at one position against static geometry plus registered bodies,
    /// excluding <paramref name="ignoreActor"/> (contract <c>IZonePhysicsBackend.cs:92-96</c>).
    /// Non-finite input is rejected.
    /// <list type="bullet">
    /// <item>Static half: six axis probes by the matching half-extent through
    /// <c>RaycastStaticScene</c> — any hit within the extent reports overlap. This is a bounded
    /// managed approximation (a corner overlap with every axis probe clear is possible).
    /// TODO(scene-overlap): an exact native overlap primitive does not exist in this tree and
    /// none is fabricated.</item>
    /// <item>Body half: sphere test with the sum of both circumscribed radii against each other
    /// body's copied status (conservative, orientation-independent).</item>
    /// </list>
    /// </summary>
    public bool TryCollide(ZonePhysicsShape shape, Vector3 position, ZoneActorHandle? ignoreActor)
    {
        ValidateShape(shape);
        if (!Finite(position))
            throw new ArgumentOutOfRangeException(nameof(position), "Overlap queries require a finite position.");

        var extents = shape.HalfExtents;
        ReadOnlySpan<Vector3> directions =
        [
            new(extents.X, 0, 0), new(-extents.X, 0, 0),
            new(0, extents.Y, 0), new(0, -extents.Y, 0),
            new(0, 0, extents.Z), new(0, 0, -extents.Z)
        ];
        foreach (var probe in directions)
            if (_world.RaycastStaticScene(position, probe, out _)) return true;

        var radius = extents.Length();
        foreach (var (handle, registration) in SnapshotBodies())
        {
            if (handle == ignoreActor) continue;
            if (registration.Body.Pointer == 0) { _bodies.Remove(handle); continue; }
            var status = registration.Body.GetStatus();
            var delta = position - status.Position;
            var sum = radius + registration.Shape.HalfExtents.Length();
            if (delta.LengthSquared() <= sum * sum) return true;
        }
        return false;
    }

    /// <summary>
    /// Downward ground query from <paramref name="origin"/> across at most
    /// <paramref name="maxDistance"/> (contract <c>IZonePhysicsBackend.cs:98-104</c>). Implemented
    /// as the terrain ray — the exact primitive the existing ground sampler uses
    /// (<c>_physics.RaycastTerrain((x, y, height+10), (0,0,-30), ...)</c>,
    /// <c>Scene/ZoneSceneActivation.cs:274</c>; PLAN section 2 C5). Terrain is Z-up
    /// (<c>Native/Physics/ZoneNativeTerrainPhysicsWorld.EntityGrid.cs:14</c>), so "downward" is -Z and
    /// the result is the terrain hit reshaped into the copied ground record; static-scene rays
    /// deliberately exclude terrain (<c>Native/Physics/ZoneNativeTerrainPhysicsWorld.SceneQueries.cs:17</c>).
    /// Non-finite input is rejected; a non-positive distance is a miss (nothing to hit).
    /// </summary>
    public bool TryGround(Vector3 origin, float maxDistance, out ZonePhysicsGroundHit ground)
    {
        ground = default;
        if (!Finite(origin) || !float.IsFinite(maxDistance))
            throw new ArgumentOutOfRangeException(nameof(maxDistance),
                "Ground queries require a finite origin and distance.");
        if (maxDistance <= 0) return false;
        if (!_world.RaycastTerrain(origin, new Vector3(0, 0, -maxDistance), out var hit)) return false;
        ground = new ZonePhysicsGroundHit(hit.Distance, hit.Position, hit.Normal, hit.PhysicalMaterialId);
        return true;
    }

    // ------------------------------------------------------------------
    // helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// Resolves a registered, physically live body. Unknown/removed handles raise
    /// <see cref="InvalidOperationException"/> (IZonePhysicsBackend.cs:81-82); a registered
    /// wrapper whose native pointer retired — <c>ZonePlayerActor.Retire</c> disposes the body
    /// (<c>Actors/ZonePlayerActorFactory.cs:666-672</c>) even if the wiring missed the
    /// mandated <c>RemoveActor</c> call (<c>IZoneActorLifecycle.cs:80-82</c>) — is purged here
    /// and rejected, so a retired handle never yields output (PLAN section 5).
    /// </summary>
    private Registration Resolve(ZoneActorHandle actor)
    {
        if (!_bodies.TryGetValue(actor, out var registration))
            throw new InvalidOperationException(
                $"Actor handle {actor} is not registered on this backend (never added, or removed); " +
                $"the handle never becomes usable again (IZonePhysicsBackend.cs:81-82).");
        if (registration.Body.Pointer == 0)
        {
            _bodies.Remove(actor);
            throw new InvalidOperationException(
                $"Actor handle {actor} was removed: its native body has already retired " +
                $"(LivingEntity.Pointer == 0, Native/Physics/ZoneNativeTerrainPhysicsWorld.Living.cs:224-266).");
        }
        return registration;
    }

    /// <summary>Point-in-time copy of the registry so query loops tolerate their own lazy purges.</summary>
    private List<KeyValuePair<ZoneActorHandle, Registration>> SnapshotBodies() => [.. _bodies];

    private static void ValidateShape(ZonePhysicsShape shape)
    {
        if (!Finite(shape.HalfExtents) || shape.HalfExtents.X <= 0 || shape.HalfExtents.Y <= 0 || shape.HalfExtents.Z <= 0)
            throw new ArgumentOutOfRangeException(nameof(shape), "Query shapes require finite, strictly positive half-extents.");
    }

    /// <summary>Any two unit vectors spanning the plane perpendicular to <paramref name="direction"/> (nonzero, finite).</summary>
    private static void PerpendicularBasis(Vector3 direction, out Vector3 u, out Vector3 w)
    {
        var absX = MathF.Abs(direction.X);
        var absY = MathF.Abs(direction.Y);
        var absZ = MathF.Abs(direction.Z);
        var axis = absX <= absY && absX <= absZ ? Vector3.UnitX
            : absY <= absZ ? Vector3.UnitY
            : Vector3.UnitZ;
        u = Vector3.Normalize(Vector3.Cross(axis, direction));
        w = Vector3.Normalize(Vector3.Cross(direction, u));
    }

    /// <summary>Exact segment (parameter s ∈ [0,1]) vs sphere; s = 0 counts as contact (already overlapping).</summary>
    private static bool TrySegmentSphere(Vector3 origin, Vector3 displacement, Vector3 center, float radius,
        out float s, out Vector3 point)
    {
        s = 0;
        point = origin;
        var offset = origin - center;
        var c = Vector3.Dot(offset, offset) - radius * radius;
        if (c <= 0) return true; // origin already inside the sphere
        var a = Vector3.Dot(displacement, displacement);
        if (a == 0) return false;
        var b = 2f * Vector3.Dot(offset, displacement);
        var discriminant = b * b - 4f * a * c;
        if (discriminant < 0) return false;
        s = (-b - MathF.Sqrt(discriminant)) / (2f * a);
        if (s < 0 || s > 1) return false;
        point = origin + displacement * s;
        return true;
    }

    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}

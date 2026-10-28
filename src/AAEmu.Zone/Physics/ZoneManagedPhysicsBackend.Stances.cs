using System.Numerics;
using AAEmu.Zone.Actors;

namespace AAEmu.Zone.Physics;

/// <summary>The physical queue outcome is independent of the actor's accepted request/current slot.</summary>
public enum ZoneManagedLivingStancePhase { Queued, Applied, RefusedAtDrain }

/// <summary>Copied latest request/drain result for one exact live actor generation.</summary>
public readonly record struct ZoneManagedLivingStanceUpdate(ulong Sequence, bool PreflightAccepted,
    ZoneManagedLivingStancePhase Phase, Vector3 Position, float UpwardShift, int Queries,
    bool UsedNormalFallback, bool HadMultipleContacts, string Refusal);

public sealed partial class ZoneManagedPhysicsBackend
{
    private readonly List<PendingLivingStance> _pendingLivingStances = [];
    private readonly Dictionary<ZoneActorHandle, ZoneManagedLivingStanceUpdate> _lastLivingStance = [];
    private ulong _nextLivingStanceSequence;
    private sealed record PendingLivingStance(ZoneActorHandle Actor, ZoneActorPhysicalizationStance Stance,
        ulong Sequence, bool PreflightAccepted);

    public bool TryChangeLivingStance(ZoneActorHandle actor, ZoneActorPhysicalizationStance stance, out string refusal)
    {
        Require(actor);
        ArgumentNullException.ThrowIfNull(stance);
        var current = _actors[actor];
        if (current.Profile != ZonePhysicsActorProfile.Living || current.LivingMovement is null)
        { refusal = "Living stance requests require a registered authored living body."; return false; }
        // Validate the complete copied request before queuing anything. Content failures are
        // explicit; input callers retain their existing admission/diagnostic boundary.
        var replacement = DescriptionForStance(current, stance);
        ZoneManagedTerrainGeometry.ValidateShape(replacement.Shape);
        if (_nextLivingStanceSequence == ulong.MaxValue)
            throw new InvalidOperationException("Living stance request sequences are exhausted.");
        var sequence = ++_nextLivingStanceSequence;
        OwnedPhysics.TryReadPosition(actor, out var position);
        _pendingLivingStances.Add(new(actor, stance, sequence, false));
        QueueLivingCommand(actor, LivingCommandKind.Stance, () => ApplyQueuedLivingStance(actor, sequence));

        // Original SetParams(-1) queues before its preflight. That preflight only tests changed
        // hc/r/raw h, and uses the OLD pivot and primitive kind. Return1 is actor acceptance,
        // not proof that the queued application later accepted the actual new hp/kind.
        var preflightStance = stance with
        {
            HeightPivot = current.GroundPivotHeight,
            UseCapsule = current.Shape.UseCapsule,
        };
        var needsProbe = current.CollisionActive && DimensionsChanged(current, stance, includePivotAndKind: false);
        var result = needsProbe ? SolveStance(actor, current, preflightStance,
            replacement.LivingMovement!.MaximumUnprojectionDistance, position) : StanceSolve.Clear(position);
        var accepted = result.Accepted;
        //A scene-query callback may have retired this exact generation during preflight.
        Require(actor);
        var queuedIndex = _pendingLivingStances.FindIndex(request => request.Actor == actor && request.Sequence == sequence);
        if (queuedIndex < 0) throw new InvalidOperationException("Living stance request retired during preflight.");
        _pendingLivingStances[queuedIndex] = new(actor, stance, sequence, accepted);
        refusal = result.Refusal;
        _lastLivingStance[actor] = new(sequence, accepted, ZoneManagedLivingStancePhase.Queued,
            position, result.Shift, result.Queries, result.UsedFallback, result.MultipleContacts, refusal);
        return accepted;
    }

    public bool TryReadLivingStanceUpdate(ZoneActorHandle actor, out ZoneManagedLivingStanceUpdate update)
    { Require(actor); return _lastLivingStance.TryGetValue(actor, out update); }

    /// <summary>Copies the immutable applied body description, never the pending request.</summary>
    public void CopyDescription(ZoneActorHandle actor, out ZonePhysicsActorDescription description)
    { Require(actor); description = _actors[actor]; }

    private void ApplyQueuedLivingStance(ZoneActorHandle actor, ulong sequence)
    {
        var request = _pendingLivingStances.Find(candidate => candidate.Actor == actor && candidate.Sequence == sequence);
        if (request is null) return;
        if (_actors.TryGetValue(request.Actor, out var current))
        {
            var replacement = DescriptionForStance(current, request.Stance);
            OwnedPhysics.TryReadPosition(request.Actor, out var position);
            var changed = DimensionsChanged(current, request.Stance, includePivotAndKind: true);
            var result = current.CollisionActive && changed
                ? SolveStance(request.Actor, current, request.Stance,
                    replacement.LivingMovement!.MaximumUnprojectionDistance, position)
                : StanceSolve.Clear(position);
            if (!_actors.ContainsKey(request.Actor)) return;
            if (result.Accepted && changed)
            {
                // Type1 preserves actual/requested velocity and cached grounded/support state.
                // It is a dimension update, not a type0 teleport or an ordinary pose correction.
                OwnedPhysics.CommitLivingDimensionsPose(request.Actor, result.Position);
                _actors[request.Actor] = replacement;
            }
            _lastLivingStance[request.Actor] = new(request.Sequence, request.PreflightAccepted,
                result.Accepted ? ZoneManagedLivingStancePhase.Applied : ZoneManagedLivingStancePhase.RefusedAtDrain,
                result.Accepted ? result.Position : position, result.Shift, result.Queries,
                result.UsedFallback, result.MultipleContacts, result.Refusal);
        }
        _pendingLivingStances.Remove(request);
    }

    private void RetireLivingStanceRequests(ZoneActorHandle actor)
    {
        _pendingLivingStances.RemoveAll(request => request.Actor == actor);
        _lastLivingStance.Remove(actor);
    }

    /// <summary>Isolated original immediate type1 profile; production actors use queued requests.</summary>
    internal bool TryApplyLivingStanceImmediatelyForDiagnostic(ZoneActorHandle actor,
        ZoneActorPhysicalizationStance stance, out string refusal)
    {
        Require(actor);
        var current = _actors[actor];
        if (current.Profile != ZonePhysicsActorProfile.Living || current.LivingMovement is null)
        { refusal = "A registered living body is required."; return false; }
        var replacement = DescriptionForStance(current, stance);
        ZoneManagedTerrainGeometry.ValidateShape(replacement.Shape);
        OwnedPhysics.TryReadPosition(actor, out var position);
        var changed = DimensionsChanged(current, stance, includePivotAndKind: true);
        var result = current.CollisionActive && changed
            ? SolveStance(actor, current, stance, replacement.LivingMovement!.MaximumUnprojectionDistance, position)
            : StanceSolve.Clear(position);
        refusal = result.Refusal;
        if (!result.Accepted) return false;
        if (changed)
        {
            // Native compares the requested raw h with its clamped support h. If they
            // compare equal it retains the prior primitive (including a smaller raw h).
            OwnedPhysics.CommitLivingDimensionsPose(actor, result.Position);
            _actors[actor] = replacement;
        }
        return true;
    }

    private static ZonePhysicsActorDescription DescriptionForStance(ZonePhysicsActorDescription current,
        ZoneActorPhysicalizationStance stance)
    {
        var movement = current.LivingMovement!.WithStance(stance);
        return current with
        {
            Shape = new(stance.Size, stance.UseCapsule ? ZonePhysicsShapeKind.Capsule : ZonePhysicsShapeKind.Cylinder),
            ColliderCenterOffsetZ = stance.HeightCollider - stance.HeightPivot,
            GroundPivotHeight = stance.HeightPivot,
            LivingMovement = movement,
        };
    }

    private static bool DimensionsChanged(ZonePhysicsActorDescription current, ZoneActorPhysicalizationStance next,
        bool includePivotAndKind) => current.LivingMovement!.HeightCollider != next.HeightCollider ||
        current.Shape.Radius != next.Size.X || current.LivingMovement.SupportHalfHeight != next.Size.Z ||
        includePivotAndKind && (current.GroundPivotHeight != next.HeightPivot || current.Shape.UseCapsule != next.UseCapsule);

    private readonly record struct StanceSolve(bool Accepted, Vector3 Position, float Shift, int Queries,
        bool UsedFallback, bool MultipleContacts, string Refusal)
    {
        public static StanceSolve Clear(Vector3 position) => new(true, position, 0, 0, false, false, string.Empty);
    }

    private StanceSolve SolveStance(ZoneActorHandle actor, ZonePhysicsActorDescription current,
        ZoneActorPhysicalizationStance stance, float maximumShift, Vector3 origin)
    {
        var shape = new ZonePhysicsShape(stance.Size,
            stance.UseCapsule ? ZonePhysicsShapeKind.Capsule : ZonePhysicsShapeKind.Cylinder);
        var shift = 0f;
        var counter = 0;
        var queries = 0;
        var usedFallback = false;
        var multiple = false;
        var timeInterval = current.LivingMovement!.SupportHalfHeight * 2;
        while (true)
        {
            var staged = origin + new Vector3(0, 0, shift);
            var center = staged + new Vector3(0, 0, stance.HeightCollider - stance.HeightPivot);
            if (!Finite(staged) || !Finite(center))
                return new(false, origin, shift, queries, usedFallback, multiple, "Living stance unprojection exceeded finite coordinate precision.");
            var envelope = ZoneLivingMovementMath.CreateStanceCandidateBounds(staged,
                stance.Size.X, stance.Size.Z, stance.HeightCollider, stance.HeightPivot);
            queries++;
            var depth = InitialStanceDepth(actor, shape, center, staged, stance.HeightCollider,
                stance.HeightPivot, envelope, stance.Size.Z,
                timeInterval, out var fallback, out var moreThanOne);
            usedFallback |= fallback;
            multiple |= moreThanOne;
            if (!float.IsFinite(depth) || depth < 0)
                return new(false, origin, shift, queries, usedFallback, multiple, "Living stance query returned an invalid unprojection depth.");
            shift += (float)((double)depth * 1.01);
            // Preserve native ordering, including accepting equality without another query.
            if (shift >= maximumShift)
                return shift <= maximumShift
                    ? new(true, origin + new Vector3(0, 0, shift), shift, queries, usedFallback, multiple, string.Empty)
                    : new(false, origin, shift, queries, usedFallback, multiple, "Living stance exceeds its authored upward unprojection range.");
            if (++counter >= 8)
                return new(false, origin, shift, queries, usedFallback, multiple, "Living stance exhausted the original eight-query unprojection limit.");
            if (depth <= 0)
                return new(true, origin + new Vector3(0, 0, shift), shift, queries, usedFallback, multiple, string.Empty);
        }
    }

    private float InitialStanceDepth(ZoneActorHandle actor, ZonePhysicsShape shape, Vector3 center,
        Vector3 entityOrigin, float heightCollider, float pivotHeight, ZonePhysicsQueryBounds envelope,
        float rawHalfHeight, float timeInterval,
        out bool fallback, out bool multiple)
    {
        fallback = false;
        multiple = false;
        foreach (var (other, description) in _actors)
        {
            if (other == actor || description.Profile != ZonePhysicsActorProfile.Living || !ColliderBodyAdmitsQuery(other)) continue;
            var otherCenter = Center(other, description);
            var otherBounds = new ZoneManagedBounds(otherCenter - description.Shape.BoundingHalfExtents,
                otherCenter + description.Shape.BoundingHalfExtents);
            if (!otherBounds.Overlaps(envelope.Managed)) continue;
            OwnedPhysics.TryReadPosition(other, out var otherOrigin);
            var delta = center - otherCenter;
            // The native living shortcut groups hc+origin BEFORE subtracting hp. Keep this
            // separate from the actual collider's origin+(hc-hp) placement at large coordinates.
            var queryZ = (heightCollider + entityOrigin.Z) - pivotHeight;
            var otherZ = (description.LivingMovement!.HeightCollider + otherOrigin.Z) - description.GroundPivotHeight;
            var radius = shape.Radius + description.Shape.Radius;
            // Original other-PE_LIVING shortcut is deliberately not the capsule's outer Z size.
            if (MathF.Abs(queryZ - otherZ) < description.LivingMovement.SupportHalfHeight + rawHalfHeight &&
                delta.X * delta.X + delta.Y * delta.Y < radius * radius) return 1e10f;
        }
        var contacts = Geometry.GetUpwardUnprojectionsForStancePreflight(shape, center).ToList();
        if (StaticGeometry is not null)
            contacts.AddRange(StaticGeometry.GetUpwardUnprojectionsForStancePreflight(shape, center, envelope));
        foreach (var (other, description) in _actors)
        {
            if (other == actor || description.Profile == ZonePhysicsActorProfile.Living || !ColliderBodyAdmitsQuery(other)) continue;
            if (ZoneManagedShapeQueries.TryUpwardUnprojectionForStancePreflight(shape, center, description.Shape, Center(other, description),
                out var contact, envelope)) contacts.Add(contact);
        }
        multiple = contacts.Count > 1;
        if (contacts.Count == 0) return 0;
        // The native helper returns its first entity/part contact. Managed enumeration is
        // deterministic geometry order; full native cross-entity/heightfield order is unowned.
        var selected = contacts[0];
        fallback = selected.Depth > timeInterval;
        return fallback ? selected.NormalFallbackDepth : selected.Depth;
    }
}

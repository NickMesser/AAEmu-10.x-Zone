using System.Numerics;

namespace AAEmu.Zone.Ai;

/// <summary>A real grid entry. CachedPosition is the grid's copied position, not a new entity-position lookup.</summary>
public readonly record struct ZoneEmptyCrowdCandidate(ZoneAiObjectState Object, Vector3 CachedPosition);

public readonly record struct ZoneEmptyCrowdAvoidanceFrame(Vector3 EntityPosition, Vector3 ViewDirection,
    Vector3 MovementDirection, float DesiredSpeed, float AvoidanceRadius, int Alertness, uint UpdatePriority,
    bool UsesThreeDimensionalNavigation, bool FullUpdate, long FrameMicroseconds, float FrameSeconds)
{
    /// <summary>Copied raw actor1593/1594 zero tests; only required when the alertness-dependent entry gate needs them.</summary>
    public bool? Actor1593 { get; init; }
    public bool? Actor1594 { get; init; }
}

public readonly record struct ZoneEmptyCrowdAvoidanceState(long LastCensusMicroseconds, bool WasMoving,
    float SteeringSide, float ObstructionTimer, Vector3 SolverOrigin, Vector3 SolverForward,
    Vector3 SolverSide, float SolverRadius);

public readonly record struct ZoneEmptyCrowdAvoidanceResult(bool Modified, Vector3 MovementDirection,
    float DesiredSpeed, bool CensusRefreshed, bool SolverRebuilt, ZoneEmptyCrowdAvoidanceState State);

/// <summary>
/// The empty-census branch of CPuppet3116E80C and its actual3104595C/3104496C solver state.
/// This owns crowd-avoidance state only. An empty actor census says nothing about static collision or route clearance.
/// </summary>
public sealed class ZoneEmptyCrowdAvoidance : IDisposable
{
    private readonly ZoneAiObjectState _self;
    private readonly ushort _objectType;
    private ZoneEmptyCrowdAvoidanceState _state = new(0, false, 0, 0, Vector3.Zero,
        Vector3.UnitX, Vector3.UnitY, 1);

    public ZoneEmptyCrowdAvoidance(ZoneAiObjectState exactSelf, ushort objectType = 5)
    { _self = exactSelf ?? throw new ArgumentNullException(nameof(exactSelf)); _objectType = objectType; }

    //Allows independent controls to supply a copied, previously unobstructed native state.
    internal ZoneEmptyCrowdAvoidance(ZoneAiObjectState exactSelf, ushort objectType, ZoneEmptyCrowdAvoidanceState state)
        : this(exactSelf, objectType)
    {
        if (!float.IsFinite(state.SteeringSide) || !float.IsFinite(state.ObstructionTimer) ||
            !Finite(state.SolverOrigin) || !Finite(state.SolverForward) || !Finite(state.SolverSide) ||
            !float.IsFinite(state.SolverRadius)) throw new ArgumentOutOfRangeException(nameof(state));
        _state = state;
    }

    public bool IsDisposed { get; private set; }
    public ZoneEmptyCrowdAvoidanceState State => _state;

    /// <summary>
    /// Consumes the union of the two actual 30m grid-query inputs when the source refresh is due.
    /// Exact self is ignored. A nonself entry strictly inside the full3D radius requires the unported populated solver.
    /// Refusals leave the persistent state unchanged; retirement requires disposing this exact actor's owner.
    /// </summary>
    public ZoneEmptyCrowdAvoidanceResult Update(ZoneEmptyCrowdAvoidanceFrame frame,
        IReadOnlyList<ZoneEmptyCrowdCandidate> census)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        ArgumentNullException.ThrowIfNull(census);
        Validate(frame);
        var next = _state;
        if (frame.AvoidanceRadius > 15 || SkipForAlertness(frame)) return Result(false, false);

        var direction = frame.MovementDirection;
        var length = MathF.Sqrt((direction.X * direction.X + direction.Y * direction.Y) + direction.Z * direction.Z);
        var moving = length > .01f && frame.DesiredSpeed > .01f;
        if (!moving)
        {
            next = next with { WasMoving = false };
            _state = next;
            return Result(false, false);
        }

        //3116E94A..E968: SUB64, CVTSI2SS64, MULSS. The decompiler's DWORD annotation is incorrect.
        var elapsed = (float)unchecked(frame.FrameMicroseconds - next.LastCensusMicroseconds) * .000001f;
        var refresh = !next.WasMoving || elapsed > .5f;
        if (refresh)
        {
            foreach (var candidate in census)
            {
                if (candidate.Object is null) throw new InvalidDataException("Crowd census contains an absent AI object.");
                if (ReferenceEquals(candidate.Object, _self)) continue;
                if (!Finite(candidate.CachedPosition)) throw new InvalidDataException("Crowd census contains a nonfinite cached position.");
                var difference = candidate.CachedPosition - frame.EntityPosition;
                var squared = (difference.X * difference.X + difference.Y * difference.Y) + difference.Z * difference.Z;
                if (squared < 30f * 30f)
                    throw new NotSupportedException("A nonself AI grid entry lies strictly within the native 30m crowd census; populated avoidance is not owned.");
            }
            next = next with { LastCensusMicroseconds = frame.FrameMicroseconds };
        }

        var groundPuppet = _objectType == 5 && !frame.UsesThreeDimensionalNavigation;
        var rebuild = groundPuppet &&
            (frame.UpdatePriority <= 1 || frame.FullUpdate || !next.WasMoving);
        if (rebuild)
        {
            //The real entity bounds are read by3116E80C only for its candidate loop; empty output is independent of them.
            var forward = frame.ViewDirection;
            forward.Z = 0;
            var inverse = 1f / MathF.Sqrt(((forward.X * forward.X + forward.Y * forward.Y) + forward.Z * forward.Z)
                + 1.1754943508222875e-38f);
            forward = new(forward.X * inverse, forward.Y * inverse, forward.Z * inverse);
            var side = new Vector3(forward.Y, -forward.X, 0);
            if (!Finite(forward) || !Finite(side))
                throw new InvalidOperationException("Empty crowd avoidance produced nonfinite solver state.");
            next = next with { SolverOrigin = frame.EntityPosition, SolverForward = forward, SolverSide = side,
                SolverRadius = frame.AvoidanceRadius * 2f };
        }
        if (groundPuppet)
        {
            //LABEL92 is outside the rebuild gate: partial updates also apply the existing empty solver.
            //3104496C with zero constraints copies the original direction and sets the side selector to zero.
            var timer = next.ObstructionTimer - frame.FrameSeconds;
            if (timer >= 0) { if (timer > 3) timer = 3; } else timer = 0;
            if (!float.IsFinite(timer)) throw new InvalidOperationException("Empty crowd avoidance produced a nonfinite timer.");
            next = next with { SteeringSide = 0, ObstructionTimer = timer };
        }
        next = next with { WasMoving = true };
        _state = next;
        return Result(refresh, rebuild);

        ZoneEmptyCrowdAvoidanceResult Result(bool refreshed, bool solved) =>
            new(false, frame.MovementDirection, frame.DesiredSpeed, refreshed, solved, _state);
    }

    private static bool SkipForAlertness(ZoneEmptyCrowdAvoidanceFrame frame)
    {
        if (frame.Alertness < 2 || frame.Actor1593 == false || frame.Actor1594 == true) return false;
        if (frame.Actor1593 == true && frame.Actor1594 == false) return true;
        throw new NotSupportedException("Alertness-dependent crowd entry requires the actual actor1593/1594 flags.");
    }

    private static void Validate(ZoneEmptyCrowdAvoidanceFrame frame)
    {
        if (!Finite(frame.EntityPosition) || !Finite(frame.ViewDirection) || !Finite(frame.MovementDirection) ||
            !float.IsFinite(frame.DesiredSpeed) || !float.IsFinite(frame.AvoidanceRadius) || frame.AvoidanceRadius < 0 ||
            !float.IsFinite(frame.FrameSeconds) || frame.FrameSeconds < 0)
            throw new NotSupportedException("Empty crowd avoidance requires finite source geometry, radius and nonnegative frame duration.");
    }
    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    public void Dispose() => IsDisposed = true;
}

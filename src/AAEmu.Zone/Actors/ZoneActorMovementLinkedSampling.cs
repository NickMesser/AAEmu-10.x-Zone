using System.Numerics;

namespace AAEmu.Zone.Actors;

public sealed partial class ZoneActorMovementHistory
{
    /// <summary>Native linked history +1F0. Selected reset invalidates this flag but retains its sample storage.</summary>
    public bool HasLinkedSample { get; private set; }

    // 39255B70 retains both state buffers; receive refreshes the anchor when its clock is zero.
    internal void ClearLinked()
    {
        _entries.Clear(); AnchorTime = 0; OffsetSeconds = 0; LagSeconds = 0;
        HasLinkedSample = false;
    }

    internal bool TrySampleLinkedAt(uint time, bool hasPhysicalAttachment, out ZoneActorMovementState? state)
    {
        state = null;
        if (AnchorTime == 0) return false;
        AnchorTime = time;
        while (_entries.Count > 1 && _entries[0].Time < time)
        {
            AnchorState = _entries[0].State;
            _entries.RemoveAt(0);
        }
        var first = _entries[0];
        if (first.Time < time) AnchorState = first.State;
        var anchor = AnchorState!; // A nonzero anchor clock is established only together with an admitted state.
        var amount = first.Time > anchor.Time && time > anchor.Time
            ? (float)unchecked(time - anchor.Time) / unchecked(first.Time - anchor.Time)
            : 1f;
        state = ZoneActorMovementInterpolation.BlendLinked(anchor, first.State, amount, hasPhysicalAttachment);
        SampledState = state; HasLinkedSample = true;
        return true;
    }
}

public sealed partial class ZoneActorMovementController
{
    /// <summary>
    /// 39256340: sample mode 3 using frame seconds and the required server clock. The physical
    /// attachment boolean is the canonical context byte +29 produced by 3924A300 from physical +11C.
    /// The caller owns native phase eligibility, actual physical state acquisition and application.
    /// Received Latest, HasPendingMovement and the owner's pose are unchanged.
    /// </summary>
    public bool TrySampleLinkedHistory(float elapsedSeconds, uint bufferMilliseconds, uint toleranceMilliseconds,
        bool hasPhysicalAttachment, out ZoneActorMovementState? state)
    {
        state = null;
        if (!Current) return false;
        if (_receiving || _sampling) throw new InvalidOperationException("Actor movement sampling cannot reenter its owning controller.");
        _sampling = true;
        try
        {
            var mode = _services.InterpolationMode;
            if (!Current || mode != 3 || LinkedHistory.AnchorTime == 0) return false;
            LinkedHistory.CorrectSampleTime(elapsedSeconds, bufferMilliseconds / 1000.0, toleranceMilliseconds / 1000.0);
            var serverTime = LinkedHistory.OffsetSeconds == 0 ? 0 : _services.ServerMilliseconds;
            if (!Current) return false;
            return LinkedHistory.TrySampleLinkedAt(LinkedHistory.SampleTime(serverTime), hasPhysicalAttachment, out state);
        }
        finally { _sampling = false; }
    }
}

public static partial class ZoneActorMovementInterpolation
{
    /// <summary>
    /// 3923FF20: mode 3 copies anchor metadata and interpolates toward the first queued state.
    /// ResetHistory is not a blend guard. Physical attachment is the caller's normalized +29 byte.
    /// </summary>
    public static ZoneActorMovementState BlendLinked(ZoneActorMovementState anchor, ZoneActorMovementState next,
        float amount, bool hasPhysicalAttachment)
    {
        ArgumentNullException.ThrowIfNull(anchor); ArgumentNullException.ThrowIfNull(next);
        if (unchecked(anchor.Time + 200u) < next.Time) return anchor with { };
        var attached = anchor.GroundUnitId != 0 || anchor.GroundGimmickId != 0 || anchor.GroundDoodadId != 0;
        var position = anchor.Pose.Position;
        var velocity = anchor.Velocity;
        if (hasPhysicalAttachment == attached && anchor.GroundUnitId == next.GroundUnitId &&
            anchor.GroundGimmickId == next.GroundGimmickId && anchor.GroundDoodadId == next.GroundDoodadId &&
            anchor.GroundPart == next.GroundPart)
        {
            position = BlendPosition(anchor.Pose.Position, next.Pose.Position, amount);
            // 390E0740 uses component-wise absolute comparisons, including NaN failure.
            if (!Within(next.Delta, 0) || !Within(next.Velocity, .1f))
                velocity = BlendVector(anchor.Velocity, next.Velocity, amount);
        }
        return anchor with
        {
            Pose = new(position, BlendRotation(anchor.Pose.Rotation, next.Pose.Rotation, amount)),
            Velocity = velocity,
            NativeVector3C = BlendVector(anchor.NativeVector3C, next.NativeVector3C, amount),
            Delta = BlendVector(anchor.Delta, next.Delta, amount)
        };
    }

    private static bool Within(Vector3 value, float tolerance) =>
        MathF.Abs(value.X) <= tolerance && MathF.Abs(value.Y) <= tolerance && MathF.Abs(value.Z) <= tolerance;
}

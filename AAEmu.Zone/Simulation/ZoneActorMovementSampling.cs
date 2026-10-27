using System.Numerics;

namespace AAEmu.Zone.Simulation;

public sealed partial class ZoneActorMovementHistory
{
    /// <summary>
    /// Native vector +30 / linked +110 sample storage. Failed sampling and discontinuous admission retain it.
    /// Selected linked reset also retains it; HasLinkedSample separately describes mode 3 validity.
    /// </summary>
    public ZoneActorMovementState? SampledState { get; private set; }

    // 39253DF0. Preserve the native comparisons and arithmetic: the outer bands use a
    // minimum adjustment of elapsed, and the inner band moves by exactly one millisecond.
    internal void CorrectSampleTime(double elapsed, double desired, double tolerance)
    {
        if (OffsetSeconds == 0) return;
        var lower = tolerance <= desired ? desired - tolerance : 0;
        var upper = desired + tolerance;
        var lag = LagSeconds;
        if (upper <= lag)
        {
            var excess = (lag - upper) / 5.0;
            var adjustment = elapsed <= excess ? excess : elapsed;
            LagSeconds = lag - adjustment;
            OffsetSeconds += adjustment;
        }
        else if (lag <= lower)
        {
            var deficit = (lower - lag) / 5.0;
            var adjustment = elapsed <= deficit ? deficit : elapsed;
            LagSeconds = lag + adjustment;
            OffsetSeconds -= adjustment;
        }
        else if (desired < lag)
        {
            LagSeconds = lag - .001;
            OffsetSeconds += .001;
        }
        else if (lag < desired)
        {
            LagSeconds = lag + .001;
            OffsetSeconds -= .001;
        }
    }

    internal uint SampleTime(uint serverMilliseconds) => OffsetSeconds == 0 ? 0 :
        unchecked((uint)ZoneActorMovementInterpolation.NativeInt64(
            (serverMilliseconds / 1000.0) * 1000.0 + OffsetSeconds * 1000.0));

    internal bool TrySampleAt(uint time, out ZoneActorMovementState? state)
    {
        state = null;
        if (_entries.Count == 0 || (_entries.Count == 1 ? time <= _entries[0].Time : time < _entries[0].Time))
            return false;

        var remove = 0;
        for (var index = 1; index < _entries.Count && _entries[index].Time < time; index++) remove++;
        if (remove != 0) _entries.RemoveRange(0, remove);
        var first = _entries[0];
        var second = _entries.Count > 1 ? _entries[1] : first;
        var amount = first.Time < second.Time
            ? (float)unchecked(time - first.Time) / unchecked(second.Time - first.Time)
            : 0f;
        state = ZoneActorMovementInterpolation.Blend(first.State, second.State, amount);
        SampledState = state;
        return true;
    }
}

/// <summary>39255C90: native mode 2 state interpolation, before physical application.</summary>
public static partial class ZoneActorMovementInterpolation
{
    public static ZoneActorMovementState Blend(ZoneActorMovementState first, ZoneActorMovementState second, float amount)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        if (second.ResetHistory || first.GroundUnitId != second.GroundUnitId ||
            first.GroundGimmickId != second.GroundGimmickId || first.GroundDoodadId != second.GroundDoodadId)
            return second with { };

        return second with
        {
            Pose = new(BlendPosition(first.Pose.Position, second.Pose.Position, amount),
                BlendRotation(first.Pose.Rotation, second.Pose.Rotation, amount)),
            Velocity = BlendVector(first.Velocity, second.Velocity, amount),
            NativeVector3C = BlendVector(first.NativeVector3C, second.NativeVector3C, amount),
            Delta = BlendVector(first.Delta, second.Delta, amount)
        };
    }

    private static Protocol.Messages.UnitWorldPosition BlendPosition(Protocol.Messages.UnitWorldPosition from,
        Protocol.Messages.UnitWorldPosition to, float amount)
    {
        const float inverseScale = 5.684341886080802e-14f;
        const float scale = 17592186044416f;
        // 39B10E00/39B10D60 round the fixed-point difference to float before scaling,
        // then truncate the float offset back to a signed integer before adding it.
        var x = (float)unchecked(to.FixedX - from.FixedX) * inverseScale;
        var y = (float)unchecked(to.FixedY - from.FixedY) * inverseScale;
        var z = to.Z - from.Z;
        x *= amount; y *= amount; z *= amount;
        return new Protocol.Messages.UnitWorldPosition(
            unchecked(from.FixedX + NativeInt64(x * scale)),
            unchecked(from.FixedY + NativeInt64(y * scale)), from.Z + z);
    }

    private static Vector3 BlendVector(Vector3 first, Vector3 second, float amount) =>
        new((second.X - first.X) * amount + first.X,
            (second.Y - first.Y) * amount + first.Y,
            (second.Z - first.Z) * amount + first.Z);

    private static Quaternion BlendRotation(Quaternion first, Quaternion second, float amount)
    {
        // 3916CE10/390B3AE0 use these scalar sums; SIMD Quaternion.Lerp changes their rounding.
        if (first.X * second.X + first.Y * second.Y + first.Z * second.Z + second.W * first.W < 0)
            second = new(-second.X, -second.Y, -second.Z, -second.W);
        var x = first.X + (second.X - first.X) * amount;
        var y = first.Y + (second.Y - first.Y) * amount;
        var z = first.Z + (second.Z - first.Z) * amount;
        var w = (second.W - first.W) * amount + first.W;
        var inverse = 1f / MathF.Sqrt(x * x + w * w + y * y + z * z);
        return new(x * inverse, y * inverse, z * inverse, w * inverse);
    }

    // CVTTSS2SI/CVTTSD2SI return the integer indefinite value for NaN and overflow.
    internal static long NativeInt64(double value) =>
        value >= 9223372036854775808.0 || value < -9223372036854775808.0 || double.IsNaN(value)
            ? long.MinValue : unchecked((long)value);
}

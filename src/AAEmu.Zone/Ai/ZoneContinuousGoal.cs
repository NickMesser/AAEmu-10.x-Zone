using System.Numerics;

using AAEmu.Zone.Actors;

namespace AAEmu.Zone.Ai;

/// <summary>The pipe user's KeepMoving flag and last movement direction, initialized by its owner.</summary>
public sealed class ZoneActorContinuationState
{
    public bool KeepMoving { get; set; }
    public Vector3 LastMovementDirection { get; set; }

    /// <summary>Apply the movement continuation phase at the end of native 311614B4.</summary>
    public void UpdateMovementDirection(ZoneActorControlState controls)
    {
        ArgumentNullException.ThrowIfNull(controls);
        var direction = controls.MovementDirection;
        // The native JB branches treat unordered components as movement as well.
        var hasDirection = !(MathF.Abs(direction.X) <= 0.01f && MathF.Abs(direction.Y) <= 0.01f && MathF.Abs(direction.Z) <= 0.01f);
        var last = LastMovementDirection;
        var lastIsSmall = MathF.Abs(last.X) <= 0.01f && MathF.Abs(last.Y) <= 0.01f && MathF.Abs(last.Z) <= 0.01f;
        if (!KeepMoving || hasDirection || lastIsSmall)
        {
            if (hasDirection) LastMovementDirection = direction;
        }
        else controls.MovementDirection = last;
    }
}

/// <summary>310D5F38: enable or disable the pipe user's movement continuation.</summary>
public sealed class ZoneContinuousGoal(bool enabled, ZoneActorContinuationState state) : IZoneGoalOperation
{
    private readonly ZoneActorContinuationState _state = state ?? throw new ArgumentNullException(nameof(state));
    private bool _disposed;
    public int Execute()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _state.KeepMoving = enabled;
        return 4;
    }
    public void Reset() => ObjectDisposedException.ThrowIf(_disposed, this);
    public void Dispose() => _disposed = true;
}

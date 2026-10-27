using System.Numerics;

using AAEmu.Zone.Actors;

namespace AAEmu.Zone.Ai;

/// <summary>Actor look controls and the native loose-attention target/version, sharing the actor's attention flag.</summary>
public sealed class ZoneActorLookState(ZoneActorControlState controls)
{
    public ZoneActorControlState Controls { get; } = controls ?? throw new ArgumentNullException(nameof(controls));
    // COPLookAt sets actor +114 while using its body direction and clears it on Reset.
    public bool UseBodyDirection { get; set; }
    public object? LooseAttentionTarget { get; private set; }
    public int LooseAttentionId { get; set; }

    /// <summary>311589E4: replace a loose target, or clear it only when its version still matches.</summary>
    public int SetLooseAttentionTarget(object? target, int expectedId = -1)
    {
        if (target is not null || expectedId == LooseAttentionId || expectedId == -1)
        {
            Controls.LooseAttention = target is not null;
            if (target is not null) LooseAttentionId = unchecked(LooseAttentionId + 1);
            LooseAttentionTarget = target;
        }
        return LooseAttentionId;
    }

    /// <summary>3115BE74: select an accepted scene-owned positional target without incrementing its version.</summary>
    public void UsePointTarget(object target)
    {
        ArgumentNullException.ThrowIfNull(target);
        Controls.LooseAttention = true;
        LooseAttentionTarget = target;
    }
}

public interface IZoneLookAtGoalServices
{
    ZoneActorLookState LookState { get; }
    Vector3 GetPosition();
    Vector3 GetMoveDirection();
    Vector3 GetBodyDirection();
    Vector3 GetViewDirection();
    object? GetReferencePoint();
    Vector3 GetTargetBodyDirection(object target);
    Vector3 GetTargetPosition(object target);
    object? GetAttentionTarget();
    object? Argument { get; }
    // 3115BE74, with both additional arguments zero. Preserve an existing forced point; otherwise update
    // the scene-owned positional target and call LookState.UsePointTarget. The goal increments the version.
    void SetLookAtPoint(Vector3 position);
}

/// <summary>310DAD28: native named, angular and reference-direction look-at goal execution.</summary>
public sealed class ZoneLookAtGoal : IZoneGoalOperation
{
    private readonly IZoneLookAtGoalServices _services;
    private readonly float _startAngle, _endAngle;
    private readonly bool _useLastOperation, _continuous, _useBodyDirection, _referenceDirection;
    private bool _initialized, _disposed;

    // 310DAC98. The name selects only the exact refpointdir special case; it is not a named target lookup.
    public ZoneLookAtGoal(ZoneGoalParameters parameters, IZoneLookAtGoalServices services)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _referenceDirection = ZoneAiSignalName.CString(parameters.Text) == "refpointdir";
        if (_referenceDirection) _useBodyDirection = true;
        else
        {
            _startAngle = parameters.First; _endAngle = parameters.Second;
            _useLastOperation = parameters.Boolean;
            _continuous = (parameters.Integer & 1) != 0;
            _useBodyDirection = (parameters.Integer & 2) != 0;
        }
    }

    public int Execute()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_startAngle < -450) { ClearLook(true); return 2; }
        var state = _services.LookState;
        if (_useBodyDirection) state.UseBodyDirection = true;
        if (!_initialized)
        {
            _initialized = true;
            var position = _services.GetPosition();
            var move = _services.GetMoveDirection();
            if (_referenceDirection)
            {
                var reference = _services.GetReferencePoint();
                var direction = reference is null ? Vector3.UnitY : _services.GetTargetBodyDirection(reference);
                if (reference is not null && AllWithin(direction, 0)) { Reset(); return 3; }
                _services.SetLookAtPoint(new(position.X + direction.X, position.Y + direction.Y, position.Z + direction.Z));
                state.LooseAttentionId = unchecked(state.LooseAttentionId + 1);
                return 1;
            }
            if (_startAngle != 0 || _endAngle != 0)
            {
                var magnitude = MathF.Abs(_endAngle - _startAngle);
                // Native ADDSS keeps the left NaN after the absolute-value operation. Prevent commuted
                // managed addition from restoring a negative NaN payload from the start angle.
                var angle = (float.IsNaN(magnitude) ? magnitude : magnitude + _startAngle) * 0.01745329238474369f;
                var rotated = RotateHorizontal(move, angle);
                _services.SetLookAtPoint(new(rotated.X * 20 + position.X, rotated.Y * 20 + position.Y, rotated.Z * 20 + position.Z));
                state.LooseAttentionId = unchecked(state.LooseAttentionId + 1);
                return 1;
            }

            object? target;
            if (_services.GetAttentionTarget() is null || _useLastOperation)
            {
                target = _services.Argument;
                if (target is null) { Reset(); return 3; }
            }
            else target = _services.GetAttentionTarget();
            state.SetLooseAttentionTarget(target);
            if (!_continuous) return 1;
        }
        else
        {
            if (_continuous) return 2;
            var position = _services.GetPosition();
            var facing = _useBodyDirection ? _services.GetBodyDirection() : _services.GetViewDirection();
            var targetPosition = state.Controls.LooseAttention && state.LooseAttentionTarget is { } looseTarget
                ? _services.GetTargetPosition(looseTarget) : Vector3.Zero;
            if (AllWithin(targetPosition, 1) && _services.GetAttentionTarget() is not null)
                targetPosition = _services.GetTargetPosition(_services.GetAttentionTarget()!);
            var x = targetPosition.X - position.X;
            var y = targetPosition.Y - position.Y;
            var z = targetPosition.Z - position.Z;
            if (_useBodyDirection)
            {
                var squared = (facing.X * facing.X + facing.Y * facing.Y) + 0f;
                if (!(squared > 1.1920928955078125e-7f)) facing = Vector3.Zero;
                else
                {
                    var scale = 1f / MathF.Sqrt(squared);
                    facing = new(facing.X * scale, facing.Y * scale, 0f * scale);
                }
                z = 0;
            }
            var inverseLength = 1f / MathF.Sqrt(((y * y + x * x) + z * z) + 1.1754943508222875e-38f);
            var dot = (facing.Y * (y * inverseLength) + facing.X * (x * inverseLength)) + (z * inverseLength) * facing.Z;
            if (!(dot > 0.98f)) return 1;
        }
        Reset();
        return 2;
    }

    private static bool AllWithin(Vector3 value, float tolerance) =>
        MathF.Abs(value.X) <= tolerance && MathF.Abs(value.Y) <= tolerance && MathF.Abs(value.Z) <= tolerance;

    // 310D3844 + 31081540, with the native input pitch and roll zero and movement Z explicitly discarded.
    private static Vector3 RotateHorizontal(Vector3 move, float angle)
    {
        var sin = MathF.Sin(angle); var cos = MathF.Cos(angle);
        var xx = cos * 1f;
        var xy = cos * 0f * 0f - sin * 1f;
        var xz = sin * 0f + cos * 0f * 1f;
        var yx = sin * 1f;
        var yy = sin * 0f * 0f + cos * 1f;
        var yz = sin * 0f * 1f - cos * 0f;
        return new((move.Y * xy + move.X * xx) + 0f * xz,
            (move.X * yx + move.Y * yy) + 0f * yz, (move.X * -0f + move.Y * 0f) + 0f);
    }

    private void ClearLook(bool force)
    {
        if (force || !_continuous) _services.LookState.SetLooseAttentionTarget(null);
        if (_useBodyDirection) _services.LookState.UseBodyDirection = false;
    }

    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ClearLook(false); _initialized = false;
        if (_useBodyDirection) _services.LookState.UseBodyDirection = false;
    }

    // Native destruction does not Reset or release the actor-owned positional target.
    public void Dispose() => _disposed = true;
}

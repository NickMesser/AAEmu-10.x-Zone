using AAEmu.Zone.Actors;
using AAEmu.Zone.Combat;

namespace AAEmu.Zone.Ai;

/// <summary>Native immediate actor-control goals. Reset preserves the controls already applied to the actor.</summary>
public sealed class ZoneActorControlGoal : IZoneGoalOperation
{
    private readonly IZoneActorControlGoalServices _services;
    private readonly ZoneGoalOperation _operation;
    private readonly string _targetName;
    private readonly ZoneStrafeDistances _strafe;
    private readonly int _stance;
    private readonly bool _delayedStance, _ignoreAll;
    private readonly float _urgency, _minimumUrgency, _scaleDownPathLength;
    private bool _disposed;

    public ZoneActorControlGoal(ZoneGoalOperation operation, ZoneGoalParameters parameters, IZoneActorControlGoalServices services)
    {
        if (operation is not (ZoneGoalOperation.AcquireTarget or ZoneGoalOperation.Strafe or ZoneGoalOperation.BodyPosition or
            ZoneGoalOperation.Run or ZoneGoalOperation.IgnoreAll)) throw new ArgumentOutOfRangeException(nameof(operation));
        ArgumentNullException.ThrowIfNull(parameters);
        _services = services ?? throw new ArgumentNullException(nameof(services)); _operation = operation;
        _targetName = ZoneAiSignalName.CString(parameters.Text);
        _strafe = new(parameters.First, parameters.Second, parameters.Boolean);
        _stance = ZoneStatMath.ToInt32(parameters.First);
        _delayedStance = parameters.Integer != 0;
        _ignoreAll = parameters.First > 0;
        _urgency = ConvertUrgency(parameters.First); _minimumUrgency = ConvertUrgency(parameters.Second);
        _scaleDownPathLength = parameters.Vector.X;
    }

    public int Execute()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var state = _services.Controls;
        switch (_operation)
        {
            case ZoneGoalOperation.AcquireTarget: // 310DA938
                var target = _targetName.Length == 0 ? null : _services.ResolveTarget(_targetName);
                target ??= _services.Argument;
                state.LooseAttention = false;
                _services.SetAttentionTarget(target);
                break;
            case ZoneGoalOperation.Strafe: // 310D6C88
                if (!_services.IsPuppet) break;
                var characterSystem = _services.GetCharacterSystem();
                var proxy = _services.GetActorProxy();
                var distances = _strafe;
                if (characterSystem is not null && proxy is not null &&
                    _services.GetCharacterStrafeSettings(characterSystem, _services.GetCharacterKey(proxy)) is { } settings)
                    distances = new(settings.Start, settings.End, (settings.Flags & 1) != 0);
                state.SetStrafeDistances(distances);
                break;
            case ZoneGoalOperation.BodyPosition: // 310D6D88
                if (!_delayedStance) state.BodyState = _stance;
                if (_services.IsPuppet) state.SetDelayedStance(_delayedStance ? _stance : -1);
                break;
            case ZoneGoalOperation.Run: // 310D6E18
                state.MovementUrgency = state.GoalMovementUrgency = _urgency;
                if (_services.IsPuppet)
                {
                    // The native JBE also takes the clearing branch for unordered (NaN) inputs.
                    if (!(_scaleDownPathLength > 0.00001f)) state.SetAdaptiveUrgency(0, 0, 0);
                    else state.SetAdaptiveUrgency(_minimumUrgency, _urgency, _scaleDownPathLength);
                }
                break;
            case ZoneGoalOperation.IgnoreAll: // 310D5E18
                state.SignalsEnabled = !_ignoreAll;
                break;
        }
        return 4;
    }

    // 310D5C98 maps script speed requests to native movement urgency; unordered comparisons reach the last case.
    private static float ConvertUrgency(float speed)
    {
        if (speed < -4.5f) return -1.4f;
        if (speed < -3.5f) return -1;
        if (speed < -2.5f) return -0.4f;
        if (speed < -1.5f) return -0.21f;
        if (speed < -0.5f) return 0.21f;
        if (speed <= 0.5f) return 0.4f;
        if (speed < 1.5f) return 1;
        return 1.4f;
    }

    public void Reset() => ObjectDisposedException.ThrowIf(_disposed, this);
    public void Dispose() => _disposed = true;
}

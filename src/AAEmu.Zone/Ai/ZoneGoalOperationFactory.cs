using AAEmu.Zone.Actors;
using AAEmu.Zone.Combat;

namespace AAEmu.Zone.Ai;

/// <summary>Implemented native operation constructors. Unsupported actor operations fail explicitly.</summary>
public sealed class ZoneGoalOperationFactory
{
    private readonly ZoneGoalExecutor _executor;
    private readonly Func<long> _frameTimeMicroseconds;
    private readonly Func<uint> _random;
    private readonly IZoneDevalueGoalServices _devaluation;
    private readonly IZoneSignalGoalServices? _signals;
    private readonly IZoneActorControlGoalServices? _controls;
    private readonly IZoneLocateGoalServices? _location;
    private readonly ZoneActorContinuationState? _continuation;
    private readonly IZoneLookAtGoalServices? _looking;
    private readonly IZoneApproachGoalServices? _approach;

    public ZoneGoalOperationFactory(ZoneGoalExecutor executor, Func<long> frameTimeMicroseconds,
        Func<uint> random, IZoneDevalueGoalServices devaluation, IZoneSignalGoalServices? signals = null,
        IZoneActorControlGoalServices? controls = null, IZoneLocateGoalServices? location = null,
        ZoneActorContinuationState? continuation = null, IZoneLookAtGoalServices? looking = null,
        IZoneApproachGoalServices? approach = null)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _frameTimeMicroseconds = frameTimeMicroseconds ?? throw new ArgumentNullException(nameof(frameTimeMicroseconds));
        _random = random ?? throw new ArgumentNullException(nameof(random));
        _devaluation = devaluation ?? throw new ArgumentNullException(nameof(devaluation));
        _signals = signals;
        _controls = controls;
        _location = location;
        _continuation = continuation;
        _looking = looking;
        _approach = approach;
    }

    public IZoneGoalOperation Create(ZoneGoalDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var parameters = definition.Parameters;
        return definition.Operation switch
        {
            ZoneGoalOperation.Approach => new ZoneApproachGoal(ZoneApproachGoalSettings.FromParameters(parameters, _random),
                _approach ?? throw new InvalidOperationException("Approach goals require actor-owned pathfinding and trace services."),
                _frameTimeMicroseconds),
            ZoneGoalOperation.Continuous => new ZoneContinuousGoal(parameters.Boolean,
                _continuation ?? throw new InvalidOperationException("Continuous goals require actor movement-continuation state.")),
            ZoneGoalOperation.LookAt => new ZoneLookAtGoal(parameters,
                _looking ?? throw new InvalidOperationException("Look-at goals require actor look and target services.")),
            ZoneGoalOperation.Locate => new ZoneLocateGoal(parameters.Text, parameters.Integer, parameters.First,
                _location ?? throw new InvalidOperationException("Locate goals require target-resolution services.")),
            ZoneGoalOperation.AcquireTarget or ZoneGoalOperation.Strafe or ZoneGoalOperation.BodyPosition or
                ZoneGoalOperation.Run or ZoneGoalOperation.IgnoreAll => new ZoneActorControlGoal(definition.Operation, parameters,
                    _controls ?? throw new InvalidOperationException("Actor-control goals require scene services.")),
            ZoneGoalOperation.Timeout => new ZoneTimeoutGoal(parameters.First, parameters.Second, _frameTimeMicroseconds, _random),
            ZoneGoalOperation.Wait => new ZoneWaitGoal(parameters.Integer, definition.WaitGroupCount, _executor),
            ZoneGoalOperation.Signal => new ZoneSignalGoal(ZoneStatMath.ToInt32(parameters.First), parameters.Text,
                parameters.Integer, ZoneStatMath.ToInt32(parameters.Second), _signals ??
                throw new InvalidOperationException("Signal goals require actor routing services.")),
            ZoneGoalOperation.WaitSignal => new ZoneWaitSignalGoal(parameters.Text,
                (ZoneSignalMatchKind)ZoneStatMath.ToInt32(parameters.First), parameters.Text2, parameters.Integer,
                parameters.Second, _executor.SignalWaits, _frameTimeMicroseconds),
            ZoneGoalOperation.Devalue => new ZoneDevalueGoal(ZoneStatMath.ToInt32(parameters.First) != 0, parameters.Boolean, _devaluation),
            ZoneGoalOperation.Branch or ZoneGoalOperation.Random or ZoneGoalOperation.Clear => new ZoneDevalueGoal(false, false, _devaluation),
            _ => throw new NotSupportedException($"Executable goal operation '{definition.Operation}' is not implemented.")
        };
    }
}

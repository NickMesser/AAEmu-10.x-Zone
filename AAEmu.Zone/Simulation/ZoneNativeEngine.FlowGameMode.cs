namespace AAEmu.Zone.Simulation;

public sealed partial class ZoneNativeEngine
{
    private ZoneNativeFlowGameModeServices? _flowGameModeServices;

    /// <summary>
    /// The unique game-mode provider for this engine. Construction owns only unpublished
    /// backing; game publication and actual console initialization are separate phases.
    /// </summary>
    internal ZoneNativeFlowGameModeServices GetFlowGameModeServices()
    {
        ValidateFlowFactoryConstruction();
        return _flowGameModeServices ??= new ZoneNativeFlowGameModeServices(this);
    }

    internal void ValidateFlowGameModeOwner(ZoneNativeFlowGameModeServices services)
    {
        ValidateFlowFactoryConstruction();
        if (!ReferenceEquals(_flowGameModeServices, services))
            throw new InvalidOperationException("Use this engine's owned game-mode service so native CVar backing has an explicit lifetime.");
    }

    private void ReleaseFlowGameModeServices()
    {
        if (_flowGameModeServices is null) return;
        if (_claimed)
        {
            RequireGameFramework(_gameFrameworkShutdownReturned && _system == 0 &&
                _gameFrameworkModule == 0 && _systemModule == 0 && _commonModule == 0,
                "Retain owned game-mode backing through actual console and native module shutdown.");
            _flowGameModeServices.ReleaseAfterNativeModules();
        }
        else _flowGameModeServices.ReleaseUnpublished();
        _flowGameModeServices = null;
    }
}

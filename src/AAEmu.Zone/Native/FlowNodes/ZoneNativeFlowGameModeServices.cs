using AAEmu.Zone.Native.Engine;

namespace AAEmu.Zone.Native.FlowNodes;

/// <summary>
/// Concrete game-mode services backed by this engine's actual console, GameFramework,
/// EntitySystem and xlcommon logger. Root composes the explicit CVar ownership phases.
/// </summary>
internal sealed class ZoneNativeFlowGameModeServices : IZoneNativeFlowGameModeServices
{
    private readonly ZoneNativeEngine _engine;
    private readonly ZoneNativeFlowGameModeCVars _variables;
    private readonly ZoneNativeFlowDiagnostics _diagnostics;

    internal ZoneNativeFlowGameModeServices(ZoneNativeEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
        _variables = new ZoneNativeFlowGameModeCVars(engine);
        _diagnostics = new ZoneNativeFlowDiagnostics(engine);
    }

    public void ValidateForRegistration()
    {
        _engine.ValidateFlowGameModeOwner(this);
        _variables.ValidateForRegistration();
    }

    internal bool IsInitialized => _variables.IsInitialized;
    internal void InitializeUnderCallback() => _variables.InitializeUnderCallback();
    internal void ReleaseUnpublished() => _variables.ReleaseUnpublished();
    internal void RetireAfterFrameworkShutdown() => _variables.RetireAfterFrameworkShutdown();
    internal void ReleaseAfterNativeModules() => _variables.ReleaseAfterNativeModules();

    public int ReadDifficultyLevel() => _variables.ReadDifficultyLevel();
    public bool IsFlowGraphDebugLoggingEnabled => _variables.ReadDebugEnabled();

    public bool HasClientActor()
    {
        _variables.RequireInitialized();
        // Native IGame+410 returns its actual framework. The held owner admits that same
        // published game generation; framework+148 returns the real optional client actor.
        return _engine.GetFlowCallbackClientActor() != 0;
    }

    public bool EntityExists(uint entityId)
    {
        _variables.RequireInitialized();
        return _engine.FindFlowEntity(entityId) != 0;
    }

    // The unlevelled native X2Log33018520 used by3931C740 forwards level1 to the same sink.
    public void LogFlowGraph(string message) => _diagnostics.Log(1, message);
}

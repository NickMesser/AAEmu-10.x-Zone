namespace AAEmu.Zone.Simulation;

public sealed partial class ZoneNativeEngine
{
    private ZoneNativeFlowCrysisScreenFaderProvider? _flowCrysisScreenFader;
    private ZoneNativeFlowUiScreenFaderProvider? _flowUiScreenFader;

    /// <summary>
    /// The unique Crysis master/group fader provider for this engine. Construction owns only
    /// unpublished managed state; the real renderer and every texture reference are captured by
    /// GetFader under the published callback gate.
    /// </summary>
    internal ZoneNativeFlowCrysisScreenFaderProvider GetFlowCrysisScreenFaderProvider()
    {
        VerifyAccess();
        if (_disposed) throw new ObjectDisposedException(nameof(ZoneNativeEngine));
        if (_flowCrysisScreenFader is not null) return _flowCrysisScreenFader;
        ValidateFlowFactoryConstruction();
        return _flowCrysisScreenFader ??= new ZoneNativeFlowCrysisScreenFaderProvider(this);
    }

    /// <summary>The unique shared X2 UI fade owner for this engine; see the master provider above.</summary>
    internal ZoneNativeFlowUiScreenFaderProvider GetFlowUiScreenFaderProvider()
    {
        VerifyAccess();
        if (_disposed) throw new ObjectDisposedException(nameof(ZoneNativeEngine));
        if (_flowUiScreenFader is not null) return _flowUiScreenFader;
        ValidateFlowFactoryConstruction();
        return _flowUiScreenFader ??= new ZoneNativeFlowUiScreenFaderProvider(this);
    }

    internal void ValidateFlowCrysisScreenFaderOwner(ZoneNativeFlowCrysisScreenFaderProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ValidateFlowFactoryConstruction();
        if (!ReferenceEquals(_flowCrysisScreenFader, provider))
            throw new InvalidOperationException("Use this engine's owned Crysis screen fader so its renderer and texture ownership have an explicit lifetime.");
    }

    internal void ValidateFlowUiScreenFaderOwner(ZoneNativeFlowUiScreenFaderProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ValidateFlowFactoryConstruction();
        if (!ReferenceEquals(_flowUiScreenFader, provider))
            throw new InvalidOperationException("Use this engine's owned shared UI fader so its render state and callbacks have an explicit lifetime.");
    }

    /// <summary>
    /// Releases both fader providers' renderer/texture/callback ownership. The claimed generation
    /// calls this from inside the framework shutdown phase, before the native framework destructor
    /// releases CSystem: CHUDFader texture references and the constructor-captured borrowed
    /// renderer must be released while the renderer and every native module are still mapped.
    /// Retiring them in the later factory-cache phase would call a destroyed renderer, and the
    /// shared UI owner's borrowed completion callback belongs to a still-mapped native module.
    /// The unclaimed branch is construction rollback; no fader can own native state before the
    /// engine publishes its callback gate.
    /// </summary>
    private void ReleaseFlowFaderProviders()
    {
        if (_flowCrysisScreenFader is not null)
        {
            if (_claimed) _flowCrysisScreenFader.Retire();
            else _flowCrysisScreenFader.ReleaseUnpublished();
            _flowCrysisScreenFader = null;
        }
        if (_flowUiScreenFader is not null)
        {
            if (_claimed) _flowUiScreenFader.Retire();
            else _flowUiScreenFader.ReleaseUnpublished();
            _flowUiScreenFader = null;
        }
    }
}

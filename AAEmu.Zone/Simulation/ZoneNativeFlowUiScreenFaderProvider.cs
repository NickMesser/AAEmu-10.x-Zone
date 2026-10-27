using System.Numerics;

namespace AAEmu.Zone.Simulation;

/// <summary>
/// One shared X2 UI fade owner per native game, replacing 3A580D80 and 398C6850..398C6AF0.
/// InitializeRenderer and the frame/callback phases must be composed by that game's lifecycle.
/// Neither a dedicated process nor a missing renderer supplies an artificial completion.
/// </summary>
internal sealed class ZoneNativeFlowUiScreenFaderProvider : IZoneNativeFlowUiScreenFader
{
    private readonly ZoneNativeEngine _engine;
    private readonly ZoneNativeFlowScreenFaderNative _native;
    private readonly ZoneNativeFlowDiagnostics _diagnostics;
    private nint _renderer;
    private int _state;
    private int _expectedCallbackState;
    private Vector4 _color = new(0, 0, 0, 1);
    private float _duration;
    private float _elapsed;
    private int _textureId = -1;
    private int _callbackKind;
    private nint _callback;
    private bool _updating;
    private bool _dispatching;
    private bool _retired;

    internal ZoneNativeFlowUiScreenFaderProvider(ZoneNativeEngine engine)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _native = new(engine);
        _diagnostics = new(engine);
    }

    public void ValidateForRegistration()
    {
        if (_retired) throw new ObjectDisposedException(nameof(ZoneNativeFlowUiScreenFaderProvider));
        _engine.ValidateFlowUiScreenFaderOwner(this);
    }

    public int State { get { Verify(); return _state; } }

    /// <summary>398C6AD0 captures gEnv's actual renderer and clears only callback kind/pointer.</summary>
    internal void InitializeRenderer()
    {
        Verify();
        _renderer = _native.CaptureRenderer();
        _callbackKind = 0;
        _callback = 0;
    }

    public void FadeOut(float seconds, Vector4 color, int textureId)
    {
        Verify();
        _color = new Vector4(color.X, color.Y, color.Z, 1);
        _duration = seconds;
        _elapsed = 0;
        _state = 1;
        _textureId = textureId;
        _diagnostics.Log(2, FormattableString.Invariant(
            $"--- Fade Out Start color({_color.X:F6}, {_color.Y:F6}, {_color.Z:F6}, {_color.W:F6}) duration({_duration:F6}) fadeOutTexId({_textureId})"));
        Verify();
    }

    public void FadeIn(float seconds, Vector4 color)
    {
        Verify();
        _color = color;
        BeginFadeIn(seconds);
    }

    private void BeginFadeIn(float seconds)
    {
        _duration = seconds;
        _elapsed = 0;
        _textureId = -1;
        _state = 3;
        _diagnostics.Log(2, FormattableString.Invariant(
            $"--- Fade In Start color({_color.X:F6}, {_color.Y:F6}, {_color.Z:F6}, {_color.W:F6}) duration({_duration:F6})"));
        Verify();
    }

    /// <summary>398C6A10: callback is a borrowed real engine function pointer, retained until dispatch.</summary>
    internal void FadeInWithCallback(float seconds, nint callback)
    {
        Verify();
        BeginFadeIn(seconds); // This overload preserves the current RGBA.
        _callbackKind = 1;
        _callback = callback;
        _expectedCallbackState = _state;
    }

    /// <summary>398C6A50. The engine must retain the callback's module through dispatch/retirement.</summary>
    internal void FadeOutWithCallback(float seconds, Vector4 color, int textureId, nint callback)
    {
        FadeOut(seconds, color, textureId);
        _callbackKind = 1;
        _callback = callback;
        _expectedCallbackState = _state;
    }

    /// <summary>398C6A90: only states one/two begin a one-second fade in and replace callback ownership.</summary>
    internal void CancelFadeOut()
    {
        Verify();
        if (_state != 1 && _state != 2) return;
        BeginFadeIn(1);
        _callbackKind = 0;
        _callback = 0;
        _expectedCallbackState = _state;
    }

    /// <summary>
    ///398C6AF0 advances, sets the real render state, and draws even on the frame completing fade in.
    ///392A95D0 invokes completion dispatch later, after its separate optional UI update.
    /// </summary>
    internal void Update(float frameSeconds)
    {
        Verify();
        if (_updating) throw new InvalidOperationException("The shared UI fader frame update reentered itself.");
        _updating = true;
        try
        {
            var state = _state;
            var color = _color; // Native RGB/alpha are captured before any log or renderer callback.
            if (state == 0) return;
            float alpha;
            if (state == 1)
            {
                _elapsed = ZoneNativeFlowScreenFaderMath.Add(frameSeconds, _elapsed);
                if (_elapsed >= _duration)
                {
                    _elapsed = _duration;
                    _state = 2;
                    _diagnostics.Log(2, "--- Faded Out");
                }
                alpha = _duration == 0 ? 1 : ZoneNativeFlowScreenFaderMath.Divide(_elapsed, _duration);
            }
            else if (state == 2) alpha = color.W;
            else if (state == 3)
            {
                _elapsed = ZoneNativeFlowScreenFaderMath.Add(frameSeconds, _elapsed);
                if (_elapsed >= _duration)
                {
                    _elapsed = _duration;
                    _state = 0;
                    _diagnostics.Log(2, "--- Faded In");
                }
                alpha = _duration == 0 ? 0 : ZoneNativeFlowScreenFaderMath.Subtract(1,
                    ZoneNativeFlowScreenFaderMath.Divide(_elapsed, _duration));
            }
            else throw new InvalidOperationException("The owned UI fader has an impossible state.");

            _native.SetUiRenderState(_renderer);
            // The original rereads renderer/texture after SetState; RGB and alpha remain captured.
            _native.Draw(_renderer, _textureId, new Vector4(color.X, color.Y, color.Z, alpha));
        }
        finally { _updating = false; }
    }

    /// <summary>398C69A0 clears pending ownership before invoking the actual completion callback.</summary>
    internal void DispatchCompletionCallbacks()
    {
        Verify();
        if (_dispatching) throw new InvalidOperationException("The UI fader callback dispatch reentered itself.");
        if (_expectedCallbackState == 0 || _expectedCallbackState == _state) return;
        _dispatching = true;
        try
        {
            var callback = _callback;
            var kind = _callbackKind;
            _expectedCallbackState = 0;
            _callback = 0;
            if (kind != 1) return;
            _diagnostics.Log(2, $"--- Fade callback begin callback({callback:X16})");
            _native.InvokeCompletion(callback); // The callback may start the next fade on this owner.
            _diagnostics.Log(2, "--- Fade callback end");
        }
        finally { _dispatching = false; }
    }

    internal void Retire()
    {
        if (_retired) return;
        Verify();
        if (_updating || _dispatching)
            throw new InvalidOperationException("Retire the UI fader after its current native callback returns.");
        _retired = true;
        _renderer = 0; // Borrowed, never Release'd by this owner.
        _callback = 0;
    }

    /// <summary>
    /// Construction rollback before process claim. Neither the borrowed renderer nor a completion
    /// callback is reachable before this engine publishes its callback gate.
    /// </summary>
    internal void ReleaseUnpublished()
    {
        if (_renderer != 0 || _callback != 0 || _updating || _dispatching)
            throw new InvalidOperationException("An unpublished UI fader cannot retain renderer or callback state.");
        _retired = true;
    }

    private void Verify()
    {
        _engine.VerifyFlowCallbackAccess();
        if (_retired) throw new ObjectDisposedException(nameof(ZoneNativeFlowUiScreenFaderProvider));
    }
}

using System.Numerics;

using AAEmu.Zone.Native.Engine;

namespace AAEmu.Zone.Native.FlowNodes;

/// <summary>
/// Engine-owned CMasterFader/CHUDFader replacement (39317A00..39318275). Frame advancement
/// belongs to the enclosing native game phase, never to a FlowNode's GetFader/CurrentTicket call.
/// There is no dedicated-mode branch; drawing requires the actual captured renderer.
/// </summary>
internal sealed class ZoneNativeFlowCrysisScreenFaderProvider : IZoneNativeFlowScreenFaderServices
{
    private readonly ZoneNativeEngine _engine;
    private readonly ZoneNativeFlowScreenFaderNative _native;
    private readonly ZoneNativeFlowDiagnostics _diagnostics;
    private Group?[]? _groups; // Null is the original absent master singleton, not eight absent groups.
    private bool _operationActive;
    private bool _retired;

    internal ZoneNativeFlowCrysisScreenFaderProvider(ZoneNativeEngine engine)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _native = new(engine);
        _diagnostics = new(engine);
    }

    public void ValidateForRegistration()
    {
        if (_retired) throw new ObjectDisposedException(nameof(ZoneNativeFlowCrysisScreenFaderProvider));
        _engine.ValidateFlowCrysisScreenFaderOwner(this);
    }

    public IZoneNativeFlowCrysisScreenFader? GetFader(int group)
    {
        Enter();
        try
        {
            _groups ??= new Group?[8]; //39317EC0 creates the master before rejecting an invalid group.
            if (unchecked((uint)group) >= 8) return null;
            return _groups[group] ??= new Group(this, _native.CaptureRenderer());
        }
        finally { Exit(); }
    }

    public void ReportPortTypeMismatch(string nodeName, int portIndex, string portName, int nativeType)
    {
        Verify();
        _diagnostics.Log(3, $"{nodeName}: input port {portIndex} ({portName}) has incompatible native type {nativeType}.");
    }

    /// <summary>Invoke 39317A00 for the already-created master, if one exists.</summary>
    internal void Update(float frameSeconds)
    {
        Enter();
        try
        {
            if (_groups is null) return;
            var restrictDrawing = _native.QueryFramework1D0();
            for (var index = 0; index < 8; ++index)
            {
                var group = _groups[index];
                if (group is null) continue;
                group.Update(frameSeconds);
                if (!restrictDrawing || index < 4) group.Draw();
            }
        }
        finally { Exit(); }
    }

    /// <summary>39317A90 retains allocated groups, colors, timing and owned textures.</summary>
    internal void Reset()
    {
        Enter();
        try { StopAll(); }
        finally { Exit(); }
    }

    /// <summary>39317E70 writes no fields; reading resets only each ticket/drawing flag.</summary>
    internal void Serialize(nint serializer)
    {
        Enter();
        try { if (_groups is not null && _native.IsReading(serializer)) StopAll(); }
        finally { Exit(); }
    }

    /// <summary>
    /// Release each real CHUDFader texture reference before the engine releases its renderer.
    /// This is the managed owner's explicit teardown, not a fabricated native global-master event.
    /// A failed release leaves all still-owned handles retained for the engine's fault handling.
    /// </summary>
    internal void Retire()
    {
        if (_retired) return;
        Enter();
        try
        {
            _ = _engine.RequireFlowCallbackEnvironment();
            if (_groups is not null)
                foreach (var group in _groups) group?.ReleaseTexture();
            _retired = true;
            _groups = null;
        }
        finally { Exit(); }
    }

    /// <summary>
    /// Construction rollback before process claim. No group, texture or renderer can exist yet:
    /// GetFader and every native endpoint require this engine's published callback gate.
    /// </summary>
    internal void ReleaseUnpublished()
    {
        if (_groups is not null)
            throw new InvalidOperationException("An unpublished screen fader cannot retain master groups or textures.");
        _retired = true;
    }

    private void StopAll()
    {
        if (_groups is not null)
            foreach (var group in _groups) group?.Reset();
        // Master+8 is initialized/reset to zero and has no other producer in these operations.
    }

    private void Verify()
    {
        _engine.VerifyFlowCallbackAccess();
        if (_retired) throw new ObjectDisposedException(nameof(ZoneNativeFlowCrysisScreenFaderProvider));
    }

    private void Enter()
    {
        Verify();
        // Prevent callbacks from replacing/releasing a texture while its native operation is active.
        if (_operationActive) throw new InvalidOperationException("A screen-fader operation reentered its owning master.");
        _operationActive = true;
    }

    private void Exit() => _operationActive = false;

    private sealed class Group(ZoneNativeFlowCrysisScreenFaderProvider owner, nint renderer)
        : IZoneNativeFlowCrysisScreenFader
    {
        private Vector4 _source = new(0, 0, 0, 1);
        private Vector4 _target = new(0, 0, 0, 1);
        private Vector4 _drawColor = new(0, 0, 0, 1);
        private nint _texture;
        private float _duration;
        private float _elapsed;
        private int _direction;
        private int _ticket;
        private bool _drawing;

        public int CurrentTicket { get { owner.Verify(); return _ticket; } }

        public void StopDrawing()
        {
            owner.Enter();
            try { _drawing = false; }
            finally { owner.Exit(); }
        }

        public Vector4 GetCurrentColor()
        {
            owner.Verify();
            return CurrentColor();
        }

        public int FadeIn(Vector4 color, float seconds, bool useCurrentColor)
        {
            owner.Enter();
            try { return Begin(color, seconds, useCurrentColor, -1); }
            finally { owner.Exit(); }
        }

        public int FadeOut(Vector4 color, float seconds, string textureName, bool useCurrentColor)
        {
            ArgumentNullException.ThrowIfNull(textureName);
            owner.Enter();
            try
            {
                LoadTexture(textureName); //393181B0 performs this before reading the old ticket/colors.
                return Begin(color, seconds, useCurrentColor, 1);
            }
            finally { owner.Exit(); }
        }

        private int Begin(Vector4 color, float seconds, bool useCurrentColor, int direction)
        {
            var oldTicket = _ticket;
            _source = oldTicket != 0 && useCurrentColor ? CurrentColor()
                : new Vector4(color.X, color.Y, color.Z, direction < 0 ? 1 : 0);
            _target = new Vector4(color.X, color.Y, color.Z, direction < 0 ? 0 : 1);
            //3931816D/3931823C retain only strictly positive durations; NaN also becomes +0.
            _duration = seconds > 0 ? seconds : 0;
            _elapsed = 0;
            _ticket = unchecked(oldTicket + 1);
            _direction = direction;
            _drawing = true;
            return _ticket;
        }

        private Vector4 CurrentColor() => ZoneNativeFlowScreenFaderMath.Blend(_source, _target,
            ZoneNativeFlowScreenFaderMath.CrysisRatio(_elapsed, _duration));

        internal void Update(float frameSeconds)
        {
            if (!_drawing) return;
            var elapsed = _elapsed;
            var duration = _duration;
            if (elapsed >= duration)
            {
                if (_direction < 0) _drawing = false;
                _ticket = 0;
                _source = _target;
            }
            var ratio = ZoneNativeFlowScreenFaderMath.CrysisRatio(elapsed, duration);
            _elapsed = ZoneNativeFlowScreenFaderMath.Add(elapsed, frameSeconds);
            _drawColor = ZoneNativeFlowScreenFaderMath.Blend(_source, _target, ratio);
        }

        internal void Draw()
        {
            if (!_drawing) return;
            var id = _texture == 0 ? -1 : owner._native.GetTextureId(_texture);
            owner._native.Draw(renderer, id, _drawColor); // Constructor-captured renderer, not a new gEnv read.
        }

        private void LoadTexture(string textureName)
        {
            var name = ZoneNativeFlowScreenFaderNative.TextureName(textureName);
            if (_texture != 0 && owner._native.TextureNameEquals(_texture, name)) return;
            ReleaseTexture();
            if (name[0] != 0) owner._native.LoadTexture(ref _texture, name);
        }

        internal void ReleaseTexture() => owner._native.ReleaseTexture(ref _texture);
        internal void Reset() { _ticket = 0; _drawing = false; }
    }
}

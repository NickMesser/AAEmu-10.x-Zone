using System.Numerics;

namespace AAEmu.Zone.Simulation;

/// <summary>
/// Actual CMasterFader ownership and native diagnostics used by CrysisFX:ScreenFader.
/// No dedicated-mode shortcut exists in 39317EC0; a replacement must preserve its lazy
/// master creation followed by unsigned group validation (0..7) and retained per-group faders.
/// </summary>
internal interface IZoneNativeFlowScreenFaderServices
{
    void ValidateForRegistration();
    IZoneNativeFlowCrysisScreenFader? GetFader(int group);
    void ReportPortTypeMismatch(string nodeName, int portIndex, string portName, int nativeType);
}

/// <summary>
/// A real group fader from the current master. Begin operations are the provider's actual
/// 393180F0/393181B0 operations, including retained color, texture ownership and ticket increment.
/// A missing renderer or texture service is not a successfully completed fade.
/// </summary>
internal interface IZoneNativeFlowCrysisScreenFader
{
    void StopDrawing(); // +58 = false; does not reset the provider's ticket or colors.
    int FadeIn(Vector4 color, float seconds, bool useCurrentColor);
    int FadeOut(Vector4 color, float seconds, string textureName, bool useCurrentColor);
    Vector4 GetCurrentColor(); // 39317CB0; all four components, only RGB is a graph output.
    int CurrentTicket { get; } // +54, positive and equal to the node's ticket means unfinished.
}

/// <summary>
/// Required current X2 UI fade owner at native 3A580D80. It owns the actual fade state and its
/// renderer/update phase. The node neither advances that state nor fabricates completion.
/// Native 398C6850/398C6970 set the color, duration, elapsed zero, texture ID and state 1/3.
/// </summary>
internal interface IZoneNativeFlowUiScreenFader
{
    void ValidateForRegistration();
    int State { get; }
    void FadeOut(float seconds, Vector4 color, int textureId);
    void FadeIn(float seconds, Vector4 color);
}

internal static class ZoneNativeFlowScreenFaderNodes
{
    internal static IZoneNativeFlowNodeBehavior CreateCrysis(IZoneNativeFlowScreenFaderServices services)
    { ArgumentNullException.ThrowIfNull(services); return new Crysis(services); }

    internal static IZoneNativeFlowNodeBehavior CreateX2Ui(IZoneNativeFlowUiScreenFader fader)
    { ArgumentNullException.ThrowIfNull(fader); return new X2Ui(fader); }

    private sealed class Crysis(IZoneNativeFlowScreenFaderServices services) : IZoneNativeFlowNodeBehavior
    {
        private static readonly ZoneNativeFlowNodeConfiguration NodeConfiguration = new(
            Array.AsReadOnly(new ZoneNativeFlowInputPort[]
            {
                new("FadeGroup", ZoneNativeFlowValue.Int32(0), "Fade Group [0-3]", null, "enum_int:0=0,1=1,2=2,3=3"),
                new("FadeIn", ZoneNativeFlowValue.Void(), "Fade back from the specified color back to normal screen"),
                new("FadeOut", ZoneNativeFlowValue.Void(), "Fade the screen to the specified color"),
                new("UseCurColor", ZoneNativeFlowValue.Boolean(true), "If checked, use the current color as Source color. Otherwise use [FadeColor] as Source color and Target color."),
                new("FadeInTime", ZoneNativeFlowValue.Float(2), "Duration of fade in"),
                new("FadeOutTime", ZoneNativeFlowValue.Float(2), "Duration of fade out"),
                new("color_FadeColor", ZoneNativeFlowValue.Vector3(default), "Target Color to fade to"),
                new("tex_TextureName", ZoneNativeFlowValue.String(string.Empty), "Texture Name")
            }),
            Array.AsReadOnly(new ZoneNativeFlowOutputPort[]
            {
                new("FadedIn", 0, "FadedIn"), new("FadedOut", 0, "FadedOut"),
                new("CurColor", 5, "Current Faded Color")
            }), "Controls Screen Fading.", 0xfffff08f, 0x80);

        // Native 0x28-byte node starts with every one of these fields zero.
        private int _ticket; // +10
        private int _direction; // +14
        private int _groupOffset; // +18
        private int _postSerializeTrigger; // +1C
        private byte _playing; // +20
        private byte _needsStop; // +21

        public ZoneNativeFlowNodeConfiguration Configuration => NodeConfiguration;
        public bool SerializeXml(ZoneNativeFlowActivation activation, nint xmlNodeReference, bool loading) => true;
        public void Destroy() { } // Common base destruction has no group-fader ownership transfer.

        public void ProcessEvent(int eventId, ZoneNativeFlowActivation activation)
        {
            if (eventId == 3)
            {
                // Preserve short-circuit order of graph virtual 50 queries.
                _groupOffset = activation.QueryGraphType(0) != 0 || activation.QueryGraphType(1) != 0 ? 4 : 0;
                if (_needsStop != 0)
                {
                    Stop(activation); _playing = 0; _needsStop = 0; _ticket = 0;
                }
                activation.SetRegularlyUpdated(false);
            }
            else if (eventId == 1)
            {
                // Both active inputs execute, in this order, against the live ports.
                if (activation.IsPortActive(1)) Begin(activation, -1);
                if (activation.IsPortActive(2)) Begin(activation, 1);
            }
            else if (eventId == 0) Update(activation);
        }

        private IZoneNativeFlowCrysisScreenFader? GetFader(ZoneNativeFlowActivation activation) =>
            services.GetFader(unchecked(_groupOffset + activation.GetInt32(0)));

        private void Stop(ZoneNativeFlowActivation activation)
        {
            var fader = GetFader(activation);
            if (fader is not null) fader.StopDrawing();
            _needsStop = 0; _ticket = 0;
        }

        private void Begin(ZoneNativeFlowActivation activation, int direction)
        {
            Stop(activation);
            activation.SetRegularlyUpdated(true);
            _direction = direction;
            StartProvider(activation);
            // Native activates these even when the group lookup returned null.
            _playing = 1; _needsStop = 1; _postSerializeTrigger = 0;
        }

        private void StartProvider(ZoneNativeFlowActivation activation)
        {
            var fader = GetFader(activation);
            if (fader is null) return;
            var seconds = activation.GetFloat(_direction < 0 ? 4 : 5);
            var color = activation.GetVector3(6);
            if (!activation.TryGetBoolean(3, out var useCurrent))
            {
                services.ReportPortTypeMismatch("CrysisFX:ScreenFader", 3, "UseCurColor", activation.GetNativePortType(3));
                useCurrent = false;
            }
            if (_direction < 0) _ticket = fader.FadeIn(new(color, 0), seconds, useCurrent);
            else
            {
                if (!activation.TryGetString(7, out var texture))
                {
                    services.ReportPortTypeMismatch("CrysisFX:ScreenFader", 7, "tex_TextureName", activation.GetNativePortType(7));
                    texture = string.Empty;
                }
                _ticket = fader.FadeOut(new(color, 1), seconds, texture, useCurrent);
            }
            _needsStop = 1;
        }

        private void Update(ZoneNativeFlowActivation activation)
        {
            if (_postSerializeTrigger != 0)
            {
                activation.ActivateBooleanOutput(_postSerializeTrigger >= 0 ? (byte)1 : (byte)0, true);
                _postSerializeTrigger = 0; activation.SetRegularlyUpdated(false); return;
            }
            var fader = GetFader(activation); // Lookup occurs before the playing flag check.
            if (fader is null || _playing == 0) activation.SetRegularlyUpdated(false);
            else
            {
                var color = fader.GetCurrentColor();
                activation.ActivateVector3Output(2, new(color.X, color.Y, color.Z));
                if (fader.CurrentTicket > 0 && fader.CurrentTicket == _ticket) return;
                if (_direction >= 0)
                {
                    activation.ActivateBooleanOutput(1, true); activation.SetRegularlyUpdated(false); _needsStop = 1;
                }
                else
                {
                    activation.ActivateBooleanOutput(0, true); activation.SetRegularlyUpdated(false); _needsStop = 0;
                }
            }
            _ticket = 0; _playing = 0;
        }

        public unsafe void Serialize(ZoneNativeFlowActivation activation, nint serializer)
        {
            if (serializer == 0) throw new ArgumentException("A real native serializer is required.", nameof(serializer));
            var playing = _playing;
            // Each serializer callback reads/writes the corresponding live native node field in order.
            fixed (byte* name = "m_bPlaying\0"u8)
                ((delegate* unmanaged[Stdcall]<nint, byte*, byte*, int, void>)(*(nint*)(*(nint*)serializer + 0xF8)))(serializer, name, &playing, 0);
            _playing = playing;
            var direction = _direction;
            fixed (byte* name = "m_direction\0"u8)
                ((delegate* unmanaged[Stdcall]<nint, byte*, int*, int, void>)(*(nint*)(*(nint*)serializer + 0xA0)))(serializer, name, &direction, 0);
            _direction = direction;
            var trigger = _postSerializeTrigger;
            fixed (byte* name = "m_postSerializeTrigger\0"u8)
                ((delegate* unmanaged[Stdcall]<nint, byte*, int*, int, void>)(*(nint*)(*(nint*)serializer + 0xA0)))(serializer, name, &trigger, 0);
            _postSerializeTrigger = trigger;
            if (((delegate* unmanaged[Stdcall]<nint, byte>)(*(nint*)(*(nint*)serializer + 0x40)))(serializer) == 0) return;
            if (_playing != 0)
            {
                activation.SetRegularlyUpdated(true); _postSerializeTrigger = _direction;
            }
            _playing = 0; _needsStop = 0; _ticket = 0; _direction = 0;
        }
    }

    private sealed class X2Ui(IZoneNativeFlowUiScreenFader fader) : IZoneNativeFlowNodeBehavior
    {
        private static readonly ZoneNativeFlowNodeConfiguration NodeConfiguration = new(
            Array.AsReadOnly(new ZoneNativeFlowInputPort[]
            {
                new("FadeOut", ZoneNativeFlowValue.Void(), "Fade the screen to the specified color"),
                new("FadeIn", ZoneNativeFlowValue.Void(), "Fade back from the spcified color back to normal screen"),
                new("FadeOutTime", ZoneNativeFlowValue.Float(1), "Duration of fade out"),
                new("FadeInTime", ZoneNativeFlowValue.Float(1), "Duration of fade in"),
                new("clr_Color", ZoneNativeFlowValue.Vector3(default), "Target color to fade to", "Color")
            }),
            Array.AsReadOnly(new ZoneNativeFlowOutputPort[]
            {
                new("FadedOut", 0, "Complete event for Fade Out"),
                new("FadeIn", 0, "Complete event for Fade In")
            }), "Controls screen Fade In/Out", 0xfffff01f, 0x10);
        private int _state; // Native +10; event 3 does not reset this field.
        public ZoneNativeFlowNodeConfiguration Configuration => NodeConfiguration;
        public bool SerializeXml(ZoneNativeFlowActivation activation, nint xmlNodeReference, bool loading) => true;
        public void Serialize(ZoneNativeFlowActivation activation, nint serializer) { }
        public void Destroy() { }
        public void ProcessEvent(int eventId, ZoneNativeFlowActivation activation)
        {
            if (eventId == 3) activation.SetRegularlyUpdated(false);
            else if (eventId == 1)
            {
                if (activation.IsPortActive(0))
                {
                    activation.SetRegularlyUpdated(true);
                    var seconds = activation.GetFloat(2); var color = activation.GetVector3(4);
                    fader.FadeOut(seconds, new(color, 1), -1); _state = 1;
                }
                if (activation.IsPortActive(1))
                {
                    activation.SetRegularlyUpdated(true);
                    var seconds = activation.GetFloat(3); var color = activation.GetVector3(4);
                    fader.FadeIn(seconds, new(color, 1)); _state = 3;
                }
            }
            else if (eventId == 0)
            {
                if (_state == 1 && fader.State == 2)
                {
                    activation.SetRegularlyUpdated(false); activation.ActivateBooleanOutput(0, true); _state = 0; return;
                }
                if (_state == 3 && fader.State == 0)
                {
                    activation.SetRegularlyUpdated(false); activation.ActivateBooleanOutput(1, true); _state = 0;
                }
            }
        }
    }
}

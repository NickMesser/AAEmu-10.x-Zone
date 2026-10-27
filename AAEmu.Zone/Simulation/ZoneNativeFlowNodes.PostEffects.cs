using System.Runtime.InteropServices;

namespace AAEmu.Zone.Simulation;

/// <summary>
/// Native singleton PostFX/FOV behaviors. The shared factory owns references, callback admission,
/// fault containment and configuration storage. These leaves do not enter the engine gate again.
/// </summary>
internal static partial class ZoneNativeFlowNodes
{
    internal static IZoneNativeFlowNodeBehavior CreatePostEffect(ZoneNativeEngine engine, string name) =>
        new PostEffectNode(engine, name);

    internal static IZoneNativeFlowNodeBehavior CreateOverrideFov(ZoneNativeEngine engine) =>
        new OverrideFovNode(engine);

    private sealed class PostEffectNode : IZoneNativeFlowNodeBehavior
    {
        private readonly ZoneNativeEngine _engine;
        private readonly ZoneNativeFlowInputPort[] _ports;
        public ZoneNativeFlowNodeConfiguration Configuration { get; }

        internal PostEffectNode(ZoneNativeEngine engine, string name)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            var (ports, description) = name switch
            {
                "CrysisFX:PostFXGlobal" => (new[]
                {
                    PostFxFloatPort("Global_Brightness", 1), PostFxFloatPort("Global_Contrast", 1),
                    PostFxFloatPort("Global_Saturation", 1), PostFxFloatPort("Global_Sharpening", 1),
                    PostFxFloatPort("Global_ColorC", 0), PostFxFloatPort("Global_ColorM", 0),
                    PostFxFloatPort("Global_ColorY", 0), PostFxFloatPort("Global_ColorK", 0),
                    PostFxFloatPort("Global_ColorHue", 0),
                }, "Sets the global PostFX params"),
                "CrysisFX:PostFXScreenFrost" => (new[]
                {
                    PostFxBoolPort("ScreenFrost_Active"), PostFxFloatPort("ScreenFrost_Amount", 0),
                    PostFxFloatPort("ScreenFrost_CenterAmount", 1),
                }, "ScreenFrost"),
                "CrysisFX:PostFXKuwaharaFilter" => (new[]
                {
                    PostFxBoolPort("KuwaharaFilter_Active"), PostFxFloatPort("KuwaharaFilter_Dimension", 5),
                    PostFxFloatPort("KuwaharaFilter_Alpha", 1),
                }, "KuwaharaFilter"),
                "CrysisFX:PostFXWaterDroplets" => (new[]
                {
                    PostFxBoolPort("WaterDroplets_Active"), PostFxFloatPort("WaterDroplets_Amount", 0),
                }, "WaterDroplets"),
                "CrysisFX:PostFXBloodSplats" => (new[]
                {
                    PostFxBoolPort("BloodSplats_Active"),
                    new ZoneNativeFlowInputPort("BloodSplats_Type", ZoneNativeFlowValue.Int32(0)),
                    PostFxFloatPort("BloodSplats_Amount", 1), PostFxBoolPort("BloodSplats_Spawn"),
                    PostFxFloatPort("BloodSplats_Scale", 1),
                }, "Bloodsplats"),
                "CrysisFX:PostFXGlow" => (new[]
                {
                    PostFxBoolPort("Glow_Active"), PostFxFloatPort("Glow_Scale", 0.5f),
                }, "Glow"),
                "CrysisFX:PostFXGlittering" => (new[] { PostFxBoolPort("Glittering_Active") }, "Glittering"),
                _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown native PostFX factory."),
            };
            _ports = ports;
            Configuration = new(_ports, Array.Empty<ZoneNativeFlowOutputPort>(), description,
                0xfffff08f, 0x80);
        }

        public void ProcessEvent(int eventId, ZoneNativeFlowActivation activation)
        {
            if (eventId != 1) return;
            //3931B830..3931C310 have no dedicated-mode or per-port active-bit gate.
            // They read each current value and skip only a failed native float conversion.
            var environment = _engine.RequireFlowCallbackEnvironment();
            var engine3D = PostFxRequiredPointer(environment + 0x58, "PostFX requires the actual 3DEngine.");
            for (var index = 0; index < _ports.Length; ++index)
                if (activation.TryGetFloat(index, out var value))
                    SetPostEffect(engine3D, _ports[index].Name, value);
        }

        public bool SerializeXml(ZoneNativeFlowActivation activation, nint xmlNodeReference, bool loading) => true;
        public void Serialize(ZoneNativeFlowActivation activation, nint serializer) { }
        public void Destroy() { }
    }

    private sealed unsafe class OverrideFovNode : IZoneNativeFlowNodeBehavior
    {
        private readonly ZoneNativeEngine _engine;
        private float _storedFov;
        public ZoneNativeFlowNodeConfiguration Configuration { get; } = new(
            new[]
            {
                new ZoneNativeFlowInputPort("SetFOV", ZoneNativeFlowValue.Float(0),
                    "Trigger to override Camera's FieldOfView."),
                new ZoneNativeFlowInputPort("GetFOV", ZoneNativeFlowValue.Void(),
                    "Trigger to get current Camera's FieldOfView."),
                new ZoneNativeFlowInputPort("ResetFOV", ZoneNativeFlowValue.Void(),
                    "Trigger to reset FieldOfView to default value."),
            },
            new[]
            {
                new ZoneNativeFlowOutputPort("CurFOV", 3, "Current FieldOfView"),
                new ZoneNativeFlowOutputPort("ResetDone", 0, "Triggered after Reset"),
            }, "Override Camera's FieldOfView. Cutscene ONLY!", 0xfffff02f, 0x20);

        internal OverrideFovNode(ZoneNativeEngine engine) =>
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));

        public void ProcessEvent(int eventId, ZoneNativeFlowActivation activation)
        {
            // Native39317070 looks up cl_fov even for otherwise unhandled events.
            var variable = FindFov(_engine.RequireFlowCallbackEnvironment(), false);
            if (variable == 0) return;
            if (eventId == 1)
            {
                // Preserve Get, then Set, then Reset when several inputs activate together.
                if (activation.IsPortActive(1)) activation.ActivateFloatOutput(0, ReadFov(variable));
                if (activation.IsPortActive(0))
                {
                    if (_storedFov <= 0) _storedFov = ReadFov(variable);
                    WriteFov(variable, activation.GetFloat(0)); // Native requires exact float kind3.
                }
                if (activation.IsPortActive(2) && _storedFov > 0)
                {
                    WriteFov(variable, _storedFov);
                    _storedFov = 0;
                }
            }
            else if (eventId == 3 && _storedFov > 0)
            {
                WriteFov(variable, _storedFov);
                _storedFov = 0;
            }
            //ResetDone is authored in the native config, but this body never activates it.
        }

        public bool SerializeXml(ZoneNativeFlowActivation activation, nint xmlNodeReference, bool loading) => true;

        public void Serialize(ZoneNativeFlowActivation activation, nint serializer)
        {
            var variable = FindFov(_engine.RequireFlowCallbackEnvironment(), false);
            if (serializer == 0) throw new InvalidOperationException("FOV serialization requires its actual native serializer.");
            if (PostFxVirtual<IsReadingCall>(serializer, 0x40)(serializer) == 0)
            {
                var current = _storedFov > 0 && variable != 0 ? ReadFov(variable) : 0;
                fixed (float* stored = &_storedFov) SerializeFloat(serializer, "m_storedFOV\0"u8, stored);
                SerializeFloat(serializer, "curFOV\0"u8, &current);
                return;
            }
            float saved = 0, currentFov = 0;
            SerializeFloat(serializer, "m_storedFOV\0"u8, &saved);
            SerializeFloat(serializer, "curFOV\0"u8, &currentFov);
            if (_storedFov > 0)
            {
                if (variable != 0) WriteFov(variable, _storedFov);
                _storedFov = 0;
            }
            _storedFov = saved;
            if (saved > 0 && currentFov > 0 && variable != 0) WriteFov(variable, currentFov);
        }

        public void Destroy()
        {
            if (!(_storedFov > 0)) return;
            // The native destructor explicitly allows absent gEnv/console during shutdown.
            // Only the retained engine owner may report its environment as already retired.
            var variable = FindFov(_engine.RequireFlowCallbackEnvironment(destruction: true), true);
            if (variable != 0) WriteFov(variable, _storedFov);
        }
    }

    private static ZoneNativeFlowInputPort PostFxFloatPort(string name, float value) =>
        new(name, ZoneNativeFlowValue.Float(value));
    private static ZoneNativeFlowInputPort PostFxBoolPort(string name) =>
        new(name, ZoneNativeFlowValue.Boolean(false));

    private static unsafe nint FindFov(nint environment, bool destruction)
    {
        if (destruction && environment == 0) return 0;
        if (environment == 0) throw new InvalidOperationException("The FlowNode environment is unavailable.");
        var console = Marshal.ReadIntPtr(environment + 0x38);
        if (destruction && console == 0) return 0;
        if (console == 0) throw new InvalidOperationException("FOV requires this engine's actual console.");
        //CName is a zero hash/reserved prefix plus the borrowed C string at+8.
        fixed (byte* name = "cl_fov\0"u8)
        {
            var key = new FovCName { Text = (nint)name };
            return PostFxVirtual<FindFovCall>(console, 0xc0)(console, (nint)(&key));
        }
    }

    private static float ReadFov(nint variable) => PostFxVirtual<ReadFovCall>(variable, 0x18)(variable);
    private static void WriteFov(nint variable, float value) => PostFxVirtual<WriteFovCall>(variable, 0x30)(variable, value);

    private static unsafe void SerializeFloat(nint serializer, ReadOnlySpan<byte> key, float* value)
    {
        fixed (byte* name = key)
            PostFxVirtual<SerializeFloatCall>(serializer, 0xf0)(serializer, (nint)name, (nint)value, 0);
    }

    private static unsafe void SetPostEffect(nint engine3D, string name, float value)
    {
        // UTF8 literals have process-stable storage, matching the native static name lifetime.
        ReadOnlySpan<byte> key = name switch
        {
            "Global_Brightness" => "Global_Brightness\0"u8,
            "Global_Contrast" => "Global_Contrast\0"u8,
            "Global_Saturation" => "Global_Saturation\0"u8,
            "Global_Sharpening" => "Global_Sharpening\0"u8,
            "Global_ColorC" => "Global_ColorC\0"u8,
            "Global_ColorM" => "Global_ColorM\0"u8,
            "Global_ColorY" => "Global_ColorY\0"u8,
            "Global_ColorK" => "Global_ColorK\0"u8,
            "Global_ColorHue" => "Global_ColorHue\0"u8,
            "ScreenFrost_Active" => "ScreenFrost_Active\0"u8,
            "ScreenFrost_Amount" => "ScreenFrost_Amount\0"u8,
            "ScreenFrost_CenterAmount" => "ScreenFrost_CenterAmount\0"u8,
            "KuwaharaFilter_Active" => "KuwaharaFilter_Active\0"u8,
            "KuwaharaFilter_Dimension" => "KuwaharaFilter_Dimension\0"u8,
            "KuwaharaFilter_Alpha" => "KuwaharaFilter_Alpha\0"u8,
            "WaterDroplets_Active" => "WaterDroplets_Active\0"u8,
            "WaterDroplets_Amount" => "WaterDroplets_Amount\0"u8,
            "BloodSplats_Active" => "BloodSplats_Active\0"u8,
            "BloodSplats_Type" => "BloodSplats_Type\0"u8,
            "BloodSplats_Amount" => "BloodSplats_Amount\0"u8,
            "BloodSplats_Spawn" => "BloodSplats_Spawn\0"u8,
            "BloodSplats_Scale" => "BloodSplats_Scale\0"u8,
            "Glow_Active" => "Glow_Active\0"u8,
            "Glow_Scale" => "Glow_Scale\0"u8,
            "Glittering_Active" => "Glittering_Active\0"u8,
            _ => throw new InvalidOperationException("The PostFX parameter is outside the native factory inventory."),
        };
        fixed (byte* parameter = key)
            PostFxVirtual<SetPostEffectCall>(engine3D, 0x6d8)(engine3D, (nint)parameter, value, 0);
    }

    private static nint PostFxRequiredPointer(nint address, string diagnostic)
    {
        var result = Marshal.ReadIntPtr(address);
        return result != 0 ? result : throw new InvalidOperationException(diagnostic);
    }

    private static T PostFxVirtual<T>(nint instance, int slot) where T : Delegate
    {
        if (instance == 0) throw new InvalidOperationException("A FlowNode native service is absent.");
        var table = PostFxRequiredPointer(instance, "A FlowNode native service has no vtable.");
        return Marshal.GetDelegateForFunctionPointer<T>(PostFxRequiredPointer(table + slot,
            "A FlowNode native service operation is absent."));
    }

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct FovCName
    {
        [FieldOffset(0)] internal uint Hash;
        [FieldOffset(4)] internal uint Padding;
        [FieldOffset(8)] internal nint Text;
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint FindFovCall(nint console, nint name);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate float ReadFovCall(nint variable);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void WriteFovCall(nint variable, float value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate byte IsReadingCall(nint serializer);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void SerializeFloatCall(nint serializer, nint name, nint value, int policy);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void SetPostEffectCall(nint engine3D, nint name, float value, byte force);
}

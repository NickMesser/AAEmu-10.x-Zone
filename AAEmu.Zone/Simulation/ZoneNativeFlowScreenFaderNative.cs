using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Text;

namespace AAEmu.Zone.Simulation;

/// <summary>Actual renderer/texture endpoints shared by the two native screen-fader owners.</summary>
internal sealed unsafe class ZoneNativeFlowScreenFaderNative(ZoneNativeEngine engine)
{
    internal nint CaptureRenderer() => Marshal.ReadIntPtr(engine.RequireFlowCallbackEnvironment() + 0x18);

    internal bool QueryFramework1D0()
    {
        var environment = engine.RequireFlowCallbackEnvironment();
        var game = Marshal.ReadIntPtr(environment + 8);
        var framework = ((delegate* unmanaged<nint, nint>)Virtual(game, 0x410))(game);
        engine.ThrowIfFlowCallbackFaulted();
        //39317A30 consumes AL, not the undefined upper bytes of EAX.
        var result = ((delegate* unmanaged<nint, byte>)Virtual(framework, 0x1d0))(framework);
        engine.ThrowIfFlowCallbackFaulted();
        return result != 0;
    }

    internal bool IsReading(nint serializer)
    {
        engine.VerifyFlowCallbackAccess();
        var result = ((delegate* unmanaged<nint, byte>)Virtual(serializer, 0x40))(serializer);
        engine.ThrowIfFlowCallbackFaulted();
        return result != 0;
    }

    internal bool TextureNameEquals(nint texture, byte[] name)
    {
        engine.VerifyFlowCallbackAccess();
        var actual = ((delegate* unmanaged<nint, byte*>)Virtual(texture, 0x20))(texture);
        engine.ThrowIfFlowCallbackFaulted();
        if (actual == null) throw new InvalidOperationException("The native texture returned a null name.");
        //39317F90 compares case-sensitive bytes through the first NUL, including embedded NULs.
        for (var i = 0; ; ++i)
        {
            if (name[i] != actual[i]) return false;
            if (name[i] == 0) return true;
        }
    }

    internal void ReleaseTexture(ref nint texture)
    {
        engine.VerifyFlowCallbackAccess();
        if (texture == 0) return;
        _ = engine.RequireFlowCallbackEnvironment();
        ((delegate* unmanaged<nint, void>)Virtual(texture, 8))(texture);
        // Clear only after the real release returns, and before surfacing a callback fault.
        texture = 0;
        engine.ThrowIfFlowCallbackFaulted();
    }

    internal void LoadTexture(ref nint texture, byte[] name)
    {
        var renderer = CaptureRenderer();
        if (renderer == 0) return; //39317F90 explicitly permits a missing current renderer here.
        fixed (byte* value = name)
            texture = ((delegate* unmanaged<nint, byte*, int, nint>)Virtual(renderer, 0x4a0))
                (renderer, value, 0x10000);
        // The returned owning reference is retained even if the native call raised a managed fault.
        engine.ThrowIfFlowCallbackFaulted();
        if (texture == 0) return;
        ((delegate* unmanaged<nint, byte, void>)Virtual(texture, 0xd0))(texture, 1);
        engine.ThrowIfFlowCallbackFaulted();
    }

    internal int GetTextureId(nint texture)
    {
        engine.VerifyFlowCallbackAccess();
        var id = ((delegate* unmanaged<nint, int>)Virtual(texture, 0x50))(texture);
        engine.ThrowIfFlowCallbackFaulted();
        return id;
    }

    internal void SetUiRenderState(nint renderer)
    {
        _ = engine.RequireFlowCallbackEnvironment();
        ((delegate* unmanaged<nint, uint, uint, void>)Virtual(renderer, 0x6a0))
            (renderer, 0x40065, uint.MaxValue);
        engine.ThrowIfFlowCallbackFaulted();
    }

    internal void Draw(nint renderer, int textureId, Vector4 color)
    {
        _ = engine.RequireFlowCallbackEnvironment();
        //39317B90/398C6AF0: actual IRenderer+1F8, fixed 800x600 coordinates, byte final flag.
        ((delegate* unmanaged<nint, float, float, float, float, int,
            float, float, float, float, float, float, float, float, float, float, byte, void>)
            Virtual(renderer, 0x1f8))
            (renderer, 0, 0, 800, 600, textureId, 0, 1, 1, 0, 0,
                color.X, color.Y, color.Z, color.W, 1, 1);
        engine.ThrowIfFlowCallbackFaulted();
    }

    internal void InvokeCompletion(nint callback)
    {
        engine.VerifyFlowCallbackAccess();
        if (callback != 0) ((delegate* unmanaged<void>)callback)();
        engine.ThrowIfFlowCallbackFaulted();
    }

    internal static byte[] TextureName(string name) => Encoding.UTF8.GetBytes(name + '\0');

    private static nint Virtual(nint instance, int offset)
    {
        if (instance == 0)
            throw new InvalidOperationException("Screen fading requires its actual native renderer or service instance.");
        var table = Marshal.ReadIntPtr(instance);
        if (table == 0) throw new InvalidOperationException("The screen-fader native service has no vtable.");
        var function = Marshal.ReadIntPtr(table + offset);
        if (function == 0) throw new InvalidOperationException("The screen-fader native service has no required operation.");
        return function;
    }
}

internal static class ZoneNativeFlowScreenFaderMath
{
    // Keep the original scalar SSE operand order, including signed zero and NaN payload choice.
    internal static float Add(float left, float right) =>
        Sse.AddScalar(Vector128.CreateScalar(left), Vector128.CreateScalar(right)).ToScalar();
    internal static float Subtract(float left, float right) =>
        Sse.SubtractScalar(Vector128.CreateScalar(left), Vector128.CreateScalar(right)).ToScalar();
    internal static float Divide(float left, float right) =>
        Sse.DivideScalar(Vector128.CreateScalar(left), Vector128.CreateScalar(right)).ToScalar();
    private static float Blend(float source, float target, float ratio) => Add(
        Sse.MultiplyScalar(Vector128.CreateScalar(Subtract(target, source)), Vector128.CreateScalar(ratio))
            .ToScalar(), source);

    internal static Vector4 Blend(Vector4 source, Vector4 target, float ratio) => new(
        Blend(source.X, target.X, ratio), Blend(source.Y, target.Y, ratio),
        Blend(source.Z, target.Z, ratio), Blend(source.W, target.W, ratio));

    internal static float CrysisRatio(float elapsed, float duration)
    {
        if (duration == 0) return 1;
        var ratio = Divide(elapsed, duration);
        if (0f > ratio) return 0;
        return 1f > ratio ? ratio : 1; // The native COMISS/JA maps unordered ratios to one.
    }
}

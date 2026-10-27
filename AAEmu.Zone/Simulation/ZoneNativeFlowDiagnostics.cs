using System.Runtime.InteropServices;
using System.Text;

namespace AAEmu.Zone.Simulation;

/// <summary>The real xlcommon diagnostic sink, invoked under the engine's current callback.</summary>
internal sealed unsafe class ZoneNativeFlowDiagnostics(ZoneNativeEngine engine)
{
    private const string ExportName = "?X2Log@@YAXW4X2LogLevel@@PEBDZZ";

    internal void Log(int level, string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        engine.VerifyFlowCallbackAccess();
        var module = engine.RequireFlowCallbackModule("xlcommon.dll");
        var write = NativeLibrary.GetExport(module, ExportName);
        var text = Encoding.UTF8.GetBytes(message + '\0');
        fixed (byte* format = "%s\0"u8)
        fixed (byte* value = text)
        {
            //330184B0 forwards the level, format and va_list to the actual registered logger.
            // Windows x64 uses the same integer argument ABI for this fixed string invocation
            // of the variadic export. Message contents never become a format string.
            ((delegate* unmanaged[Cdecl]<int, byte*, byte*, void>)write)(level, format, value);
        }
        engine.ThrowIfFlowCallbackFaulted();
    }
}

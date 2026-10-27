using AAEmu.Zone.Simulation;

namespace AAEmu.Zone.Scripting;

public static class ZoneAiLogBindings
{
    public static void Install(ZoneLuaReference ai, Action<string> log, Action<string> warning)
    {
        ArgumentNullException.ThrowIfNull(ai); ArgumentNullException.ThrowIfNull(log); ArgumentNullException.ThrowIfNull(warning);
        ai.Runtime.WithStack(() =>
        {
            Bind("LogEvent", log); Bind("Warning", warning);
            return 0;
        });

        // 30521480 / 305211E0: exactly one actual string, passed literally with the <Lua> prefix.
        void Bind(string name, Action<string> output)
        {
            using var function = ai.Runtime.CreateFunction(arguments =>
            {
                if (arguments.Count != 1) warning($"[CScriptBind_AI::{name}] {arguments.Count} arguments passed, 1 expected)");
                else if (arguments[0] is string message) output("<Lua> " + ZoneAiSignalName.CString(message));
                return [];
            });
            ai.Set(name, function);
        }
    }
}

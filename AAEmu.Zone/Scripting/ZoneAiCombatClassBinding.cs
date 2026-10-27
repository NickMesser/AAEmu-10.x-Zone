using AAEmu.Zone.Simulation;

namespace AAEmu.Zone.Scripting;

public static class ZoneAiCombatClassBinding
{
    /// <summary>Install AI.AddCombatClass on the owning runtime's AI table.</summary>
    public static void Install(ZoneLuaReference ai, ZoneAiCombatClasses classes)
    {
        ArgumentNullException.ThrowIfNull(ai); ArgumentNullException.ThrowIfNull(classes);
        var lua = ai.Runtime;
        lua.WithStack(() =>
        {
            using var function = lua.CreateFunction(arguments =>
            {
                // 305417D0: zero arguments reset; a nonnumeric first argument makes no change.
                if (arguments.Count == 0) { classes.Clear(); return []; }
                if (Number(arguments[0]) is not { } classId) return [];
                var factors = new List<float>();
                if (arguments.Count > 1 && arguments[1] is ZoneLuaReference { IsTable: true } table)
                {
                    // Native GetAt starts at 1 and stops at the first missing or nonnumeric element.
                    for (var index = 1; Number(lua.Get(table, index, 3)) is { } factor; index++) factors.Add(factor);
                }
                var signal = arguments.Count > 2 ? arguments[2] as string : null;
                classes.SetClass(ZoneStatMath.ToInt32(classId), factors.ToArray(), signal);
                return [];
            });
            ai.Set("AddCombatClass", function);
            return 0;
        });
    }

    private static float? Number(object? value) => value switch
    {
        long integer => (float)(double)integer,
        double number => (float)number,
        _ => null
    };
}

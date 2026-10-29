namespace AAEmu.Zone.Scripting;

/// <summary>Original typed ScriptAny reads, rather than Lua truthiness or a VM-wide coercion.</summary>
public static class ZoneLuaScriptAny
{
    /// <summary>
    ///CScriptTable35512DC0 ->3550D4E0 accepts booleans and numeric values for requested tag2.
    ///Its UCOMISD/JE numeric branch treats zero and unordered (NaN) as false; other types fail.
    ///The caller retains its original field/default when this returns false.
    /// </summary>
    public static bool TryBoolean(object? value, out bool result)
    {
        switch (value)
        {
            case bool boolean: result = boolean; return true;
            case long integer: result = integer != 0; return true;
            case double number: result = number != 0 && !double.IsNaN(number); return true;
            default: result = false; return false;
        }
    }
}

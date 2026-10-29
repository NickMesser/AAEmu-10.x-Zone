using AAEmu.Zone.Combat;
using AAEmu.Zone.Scene;

namespace AAEmu.Zone.Scripting;

/// <summary>
/// Original AI.GetAutoDisable30534F70 / AI.AutoDisable30535170 over this generation's
///actual CAIProxy countdown. There is no independent auto-disable boolean.
/// </summary>
public static class ZoneNpcAutoDisable
{
    public static bool Get(ZoneManagedNpcActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor); actor.Scene.Require(actor);
        return !actor.Entry.Activation.AlwaysUpdate;
    }

    /// <summary>The numeric argument crosses float then CVTTSS2SI; any nonzero integer requests0.</summary>
    public static void Set(ZoneManagedNpcActor actor, float status)
    {
        ArgumentNullException.ThrowIfNull(actor); actor.Scene.Require(actor);
        actor.Entry.SetUpdateAlways(ZoneStatMath.ToInt32(status) != 0 ? (short)0 : short.MinValue);
    }

    /// <summary>
    ///39265E50 runs after Server.OnInit, before the registration read392ADC00 and
    ///3927F610's proxy408 setter. Missing tables refuse this actual NPC construction profile.
    /// </summary>
    public static void ApplyInitialParameters(ZoneManagedNpcScriptBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding); binding.RequireCurrent();
        if (binding.AutoDisableParametersApplied) return;
        using var properties = binding.Script.Get("PropertiesInstance") as ZoneLuaReference;
        binding.RequireCurrent();
        if (properties is not { IsTable: true })
            throw new NotSupportedException("NPC auto-disable registration requires original entity.PropertiesInstance.");
        //NpcDesc656&4 is the loaded activate_ai_always column; the original producer negates it.
        properties.Set("bAutoDisable", !binding.Actor.Npc.Template.ActivateAiAlways);
        binding.RequireCurrent();
        //The requested ScriptAny boolean also accepts actual numeric values; strings fail.
        var allow = ZoneLuaScriptAny.TryBoolean(properties.Get("bAutoDisable"), out var enabled) && enabled;
        binding.RequireCurrent();
        binding.Actor.Entry.SetUpdateAlways(allow ? (short)0 : short.MinValue);
        binding.RequireCurrent();
        binding.AutoDisableParametersApplied = true;
    }
}

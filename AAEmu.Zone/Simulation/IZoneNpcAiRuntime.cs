namespace AAEmu.Zone.Simulation;

/// <summary>Actor and script services required by the native AI signal queue and proxy.</summary>
public interface IZoneNpcAiRuntime : IZoneNpcActorRuntime
{
    // 393401B0: copy the NPC entity's position into its script AI.talkPos if both tables exist.
    // Missing entity/script state is silent and does not prevent subsequent OnTalk admission.
    void CaptureTalkPosition(ZoneNpc npc);
    // 305108E0: pending character constructor, then pending behavior constructor.
    void RunPendingConstructors(ZoneNpc npc);
    // 30511450: actor event 4, ACTION_DONE, current/default behavior and any behavior transition.
    // The Zone event dispatcher runs afterward, using the extra data's entity ID at that time.
    void DispatchScriptSignal(ZoneNpc npc, ZoneAiSignal signal);
    void ReleaseScriptState(ZoneNpc npc);
}

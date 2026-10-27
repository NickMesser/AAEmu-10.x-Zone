namespace AAEmu.Zone.Scripting;

/// <summary>Required actor/scene services for one native CAIHandler owner.</summary>
public interface IZoneNpcBehaviorServices
{
    bool HasActor { get; }
    void LoadScript(string filename);
    void ActorEvent(int kind, string name);
    void RecordEvent(int kind, string name);
    // 30510930: the pipe-user's vtable +1C0 call with null and float zero, after actor event 9.
    void ResetPipeUserAfterBehaviorExit();
    void ChangeSmartObjectState(string change);
    void NotifyAlert();
    void NotifyActionDone();
    void ScriptError(string callback, ZoneLuaException error);
}

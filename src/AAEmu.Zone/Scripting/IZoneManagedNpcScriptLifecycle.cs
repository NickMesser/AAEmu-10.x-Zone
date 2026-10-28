namespace AAEmu.Zone.Scripting;

/// <summary>
/// Scene-owned script components bind after exact actor adoption, before authored OnInit.
/// The returned generation lease retires before its entity binding, actor and proxy.
/// </summary>
public interface IZoneManagedNpcScriptLifecycle
{
    IDisposable BindEntity(ZoneManagedNpcScriptBinding binding);
    IReadOnlyList<ZoneManagedNpcScriptFault> Tick(TimeSpan elapsed);
}

/// <summary>An actual owner of authored NPC child data installed after OnInit and before character construction.</summary>
public interface IZoneManagedNpcAuthoredChildren
{
    bool HasAuthoredChildren { get; }
    void InstallAuthoredChildren(ZoneManagedNpcScriptBinding binding);
}

/// <summary>Actual event29 and post-physics AI proxy publication, on the scene's one sampled frame.</summary>
public interface IZoneManagedNpcFrameLifecycle
{
    IReadOnlyList<ZoneManagedNpcScriptFault> PrePhysics(AAEmu.Zone.Actors.ZoneMovementFrame frame);
    IReadOnlyList<ZoneManagedNpcScriptFault> PublishPose(AAEmu.Zone.Actors.ZoneActorHandle actor);
}

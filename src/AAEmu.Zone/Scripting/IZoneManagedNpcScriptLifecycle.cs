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

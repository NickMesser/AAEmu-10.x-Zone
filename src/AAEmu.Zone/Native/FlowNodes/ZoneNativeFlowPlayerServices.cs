using System.Globalization;

using AAEmu.Zone.Native.Engine;

namespace AAEmu.Zone.Native.FlowNodes;

/// <summary>
/// Connects player nodes to the actual engine lookups and retained P1 actor registry.
/// It does not create a substitute client player or implement player operations by caching them.
/// </summary>
internal sealed class ZoneNativeFlowPlayerServices : IZoneNativeFlowPlayerServices
{
    private readonly ZoneNativeEngine _engine;
    private readonly ZoneNativeFlowActorRegistry _actors;
    private readonly ZoneNativeFlowDiagnostics _diagnostics;

    internal ZoneNativeFlowPlayerServices(ZoneNativeEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        engine.ValidateFlowFactoryConstruction();
        _engine = engine;
        _actors = engine.GetFlowActorRegistry();
        _diagnostics = new(engine);
    }

    public void ValidateForRegistration()
    {
        _engine.ValidateFlowPlayerServicesOwner(this);
        _actors.ValidateForRegistration();
    }

    public nint FindEntity(uint entityId) => _engine.FindFlowEntity(entityId);
    public uint ReadEntityId(nint entity) => _engine.ReadFlowEntityId(entity);
    public IZoneNativeFlowPlayerOperations? GetClientPlayer() => _actors.GetClientPlayer();

    public void ReportBooleanPortMismatch(nint node, int port, string portName, int actualType) =>
        _diagnostics.Log(3, string.Create(CultureInfo.InvariantCulture,
            $"CFlowBaseNode::GetPortBool: Node={(nuint)node:X16} Port={port} '{portName}' Tag={actualType} -> Not a bool tag!"));

    public void ReportInvalidStance(int stance) =>
        _diagnostics.Log(3, string.Create(CultureInfo.InvariantCulture, $"[flow] PlayerStaging: stance={stance} invalid"));
}

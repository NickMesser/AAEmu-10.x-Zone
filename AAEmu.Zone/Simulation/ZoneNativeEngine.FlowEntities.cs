using System.Runtime.InteropServices;

namespace AAEmu.Zone.Simulation;

public sealed unsafe partial class ZoneNativeEngine
{
    /// <summary>Real EntitySystem access inside an already-held game/flow callback.</summary>
    internal nint RequireFlowEntitySystem()
    {
        var environment = RequireFlowCallbackEnvironment();
        var module = RequireFlowCallbackModule("cryentitysystem_dedicate.dll");
        var entities = Marshal.ReadIntPtr(environment + 0x88);
        if (entities == 0 || Marshal.ReadIntPtr(module + 0xf4220) != environment ||
            Marshal.ReadIntPtr(module + 0xf4228) != entities ||
            Marshal.ReadIntPtr(entities) != module + 0xcfd48)
            throw new InvalidOperationException("The FlowNode EntitySystem does not belong to this engine generation.");
        return entities;
    }

    internal nint FindFlowEntity(uint entityId)
    {
        var entities = RequireFlowEntitySystem();
        var module = RequireFlowCallbackModule("cryentitysystem_dedicate.dll");
        var lookup = Marshal.ReadIntPtr(Marshal.ReadIntPtr(entities) + 0x70);
        if (lookup != module + 0x1c270)
            throw new InvalidOperationException("The FlowNode entity lookup has an unsupported native binding.");
        return InvokeGameFramework(() => ((delegate* unmanaged[Stdcall]<nint, uint, nint>)lookup)(entities, entityId));
    }

    internal uint ReadFlowEntityId(nint entity)
    {
        _ = RequireFlowEntitySystem();
        var module = RequireFlowCallbackModule("cryentitysystem_dedicate.dll");
        if (entity == 0 || Marshal.ReadIntPtr(entity) != module + 0xcd998)
            throw new InvalidOperationException("Read an ID only from this engine's actual native CEntity.");
        // The actual IEntity +0 getter used by the original PlayerLink/Tornado callers.
        var readId = Marshal.ReadIntPtr(Marshal.ReadIntPtr(entity));
        var entityId = InvokeGameFramework(() => ((delegate* unmanaged[Stdcall]<nint, uint>)readId)(entity));
        if (FindFlowEntity(entityId) != entity)
            throw new InvalidOperationException("The FlowNode entity is no longer the current full-ID registry entry.");
        return entityId;
    }
}

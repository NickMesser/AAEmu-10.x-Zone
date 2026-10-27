using System.Runtime.InteropServices;

using AAEmu.Zone.Native.FlowNodes;

namespace AAEmu.Zone.Native.Engine;

public sealed unsafe partial class ZoneNativeEngine
{
    private readonly object _flowTornadoIdentityIssuer = new();
    private ZoneNativeFlowTornadoServices? _flowTornadoServices;

    /// <summary>Managed-only construction before process/module admission is supported.</summary>
    internal ZoneNativeFlowTornadoServices GetFlowTornadoServices()
    {
        ValidateFlowFactoryConstruction();
        return _flowTornadoServices ??= new ZoneNativeFlowTornadoServices(this);
    }

    /// <summary>
    /// A future actual extension producer captures this identity only after its real extension
    /// has been published in this framework. No raw-pointer constructor or fabricated extension
    /// is accepted. The capability establishes lookup identity, not ownership by itself.
    /// </summary>
    internal FlowTornadoIdentity? CaptureFlowTornadoIdentity(uint entityId)
    {
        VerifyFlowCallbackAccess();
        var entity = FindFlowEntity(entityId);
        if (entity == 0) return null;
        var observed = ObserveFlowTornado(entity);
        if (observed.Extension == 0) return null;
        return new FlowTornadoIdentity(this, _flowTornadoIdentityIssuer, observed.EntityId,
            entity, observed.Extension, observed.ExtensionTable);
    }

    internal void ValidateFlowTornadoIdentity(FlowTornadoIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        VerifyFlowCallbackAccess();
        if (!identity.WasIssuedBy(this, _flowTornadoIdentityIssuer))
            throw new InvalidOperationException("The Tornado identity belongs to another engine generation.");
        // Query by full ID before dereferencing the captured pointer. A reused ID or extension
        // also requires the retained producer's independent, immutable lifetime capability.
        var entity = FindFlowEntity(identity.EntityId);
        if (entity == 0 || entity != identity.Entity)
            throw new InvalidOperationException("The Tornado entity identity is no longer current.");
        var current = ObserveFlowTornado(entity);
        if (current.EntityId != identity.EntityId || current.Extension != identity.Extension ||
            current.ExtensionTable != identity.ExtensionTable)
            throw new InvalidOperationException("The actual Tornado extension identity changed.");
    }

    internal FlowTornadoObservation ObserveFlowTornado(nint entity)
    {
        _ = RequireFlowCallbackEnvironment();
        RequireGameServicePublication();
        ValidateGameFrameworkOwner(_system);
        // Preserve3920C4E0's framework-before-IEntity-ID order through the actual published
        // IGame+410 callback; the existing game owner validates its receiver and publication.
        var framework = InvokeGameFramework(() =>
            ZoneNativePhysicsModule.Virtual<PointerCall>(_gameServices!.Pointer, 0x410)(_gameServices.Pointer));
        RequireGameFramework(framework == _gameFramework,
            "The Tornado callback did not obtain this engine's current framework.");
        var entityId = ReadFlowEntityId(entity);
        var query = BindGameFramework<TornadoQueryCall>(0x198, 0x1f7c60);
        nint extension;
        fixed (byte* name = "Tornado\0"u8)
        {
            //306F7C60 is the actual framework QueryGameObjectExtension implementation.
            try { extension = query(framework, entityId, name); }
            catch (Exception error) { Poison(error); throw; }
        }
        ThrowIfFlowCallbackFaulted();
        if (extension == 0) return new(entityId, 0, 0);
        // Only native-returned pointers are inspected. This does not assert a CTornado vtable
        // or authorize calls through it: a present extension still requires its real owner.
        var table = Marshal.ReadIntPtr(extension);
        if (table == 0) throw new InvalidOperationException("The native Tornado extension has no interface identity.");
        return new(entityId, extension, table);
    }

    /// <summary>
    /// Called by root before framework shutdown. It makes no native calls or pointer reads,
    /// including after a failed teardown; actual producers must have retired their bindings.
    /// </summary>
    internal void VerifyFlowTornadoBindingsForShutdown()
    {
        VerifyAccess();
        _flowTornadoServices?.VerifyNoLiveBindingsForShutdown();
    }

    internal readonly record struct FlowTornadoObservation(uint EntityId, nint Extension, nint ExtensionTable);

    internal sealed class FlowTornadoIdentity
    {
        private readonly object _issuer;
        internal ZoneNativeEngine Engine { get; }
        internal uint EntityId { get; }
        internal nint Entity { get; }
        internal nint Extension { get; }
        internal nint ExtensionTable { get; }

        internal FlowTornadoIdentity(ZoneNativeEngine engine, object issuer, uint entityId,
            nint entity, nint extension, nint extensionTable)
        {
            if (!ReferenceEquals(issuer, engine._flowTornadoIdentityIssuer))
                throw new InvalidOperationException("Only the actual engine lookup can issue a Tornado identity.");
            Engine = engine;
            _issuer = issuer;
            EntityId = entityId;
            Entity = entity;
            Extension = extension;
            ExtensionTable = extensionTable;
        }

        internal bool WasIssuedBy(ZoneNativeEngine engine, object issuer) =>
            ReferenceEquals(Engine, engine) && ReferenceEquals(_issuer, issuer);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private unsafe delegate nint TornadoQueryCall(nint framework, uint entityId, byte* extensionName);
}

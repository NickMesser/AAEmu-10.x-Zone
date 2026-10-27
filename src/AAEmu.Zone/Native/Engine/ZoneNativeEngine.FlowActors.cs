using System.Runtime.InteropServices;

using AAEmu.Zone.Native.FlowNodes;

namespace AAEmu.Zone.Native.Engine;

public sealed partial class ZoneNativeEngine
{
    private readonly object _flowActorIdentityIssuer = new();
    private ZoneNativeFlowActorRegistry? _flowActorRegistry;
    private ZoneNativeFlowPlayerServices? _flowPlayerServices;
    private nint _flowActorSystem;
    private nint _flowActorSystemTable;

    /// <summary>Managed construction only; an empty registry does not substitute for a native lookup.</summary>
    internal ZoneNativeFlowActorRegistry GetFlowActorRegistry()
    {
        VerifyAccess();
        if (_disposed) throw new ObjectDisposedException(nameof(ZoneNativeEngine));
        if (_flowActorRegistry is not null) return _flowActorRegistry;
        ValidateFlowFactoryConstruction();
        return _flowActorRegistry ??= new ZoneNativeFlowActorRegistry(this);
    }

    internal void VerifyFlowActorRegistryOwner(ZoneNativeFlowActorRegistry registry)
    {
        VerifyAccess();
        if (_disposed || !ReferenceEquals(_flowActorRegistry, registry))
            throw new InvalidOperationException("The actor registry is not this engine's retained owner.");
    }

    /// <summary>
    /// The unique player-node bridge for this engine. It borrows the retained actor registry and
    /// owns no native storage of its own. Construction is managed-only and runs before process claim.
    /// </summary>
    internal ZoneNativeFlowPlayerServices GetFlowPlayerServices()
    {
        VerifyAccess();
        if (_disposed) throw new ObjectDisposedException(nameof(ZoneNativeEngine));
        if (_flowPlayerServices is not null) return _flowPlayerServices;
        ValidateFlowFactoryConstruction();
        return _flowPlayerServices ??= new ZoneNativeFlowPlayerServices(this);
    }

    internal void ValidateFlowPlayerServicesOwner(ZoneNativeFlowPlayerServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        ValidateFlowFactoryConstruction();
        if (!ReferenceEquals(_flowPlayerServices, services))
            throw new InvalidOperationException("Use this engine's owned player services so native player lookups have an explicit lifetime.");
    }

    /// <summary>
    /// Ends the player-services bridge in the same phase as the other engine-owned providers. The
    /// bridge allocates no native storage and only borrows this engine's actor registry, so the
    /// claimed generation requires the completed native module shutdown before the reference ends.
    /// The unclaimed branch is construction rollback, where nothing was published.
    /// </summary>
    private void ReleaseFlowPlayerServices()
    {
        if (_flowPlayerServices is null) return;
        if (_claimed)
            RequireGameFramework(_gameFrameworkShutdownReturned && _system == 0 &&
                _gameFrameworkModule == 0 && _systemModule == 0 && _commonModule == 0,
                "Retain the player services through native modules and the actor registry lifetime.");
        _flowPlayerServices = null;
    }

    /// <summary>
    /// An observation issued only after real ActorSystem and EntitySystem lookups agree on a
    /// current full ID. It proves native identity, not P1 ownership or implemented player behavior.
    /// </summary>
    internal sealed class FlowActorIdentity
    {
        private readonly object _issuer;
        internal ZoneNativeEngine Engine { get; }
        internal uint EntityId { get; }
        internal nint EntitySystem { get; }
        internal nint Entity { get; }
        internal nint Framework { get; }
        internal nint ActorSystem { get; }
        internal nint Actor { get; }
        internal nint ActorTable { get; }
        internal nint ClassToken { get; }

        internal FlowActorIdentity(ZoneNativeEngine engine, object issuer, uint entityId,
            nint entitySystem, nint entity, nint framework, nint actorSystem,
            nint actor, nint actorTable, nint classToken)
        {
            if (!ReferenceEquals(issuer, engine._flowActorIdentityIssuer))
                throw new InvalidOperationException("Only the owning engine can issue a native actor identity.");
            _issuer = issuer;
            Engine = engine;
            EntityId = entityId;
            EntitySystem = entitySystem;
            Entity = entity;
            Framework = framework;
            ActorSystem = actorSystem;
            Actor = actor;
            ActorTable = actorTable;
            ClassToken = classToken;
        }

        internal bool IsIssuedBy(ZoneNativeEngine engine) => ReferenceEquals(Engine, engine) &&
            ReferenceEquals(_issuer, engine._flowActorIdentityIssuer);
    }

    internal FlowActorIdentity? CaptureFlowActorIdentity(uint entityId)
    {
        VerifyFlowCallbackAccess();
        if (entityId == 0) return null; //39316630 has no provider call for ID0.
        var actorSystem = RequireFlowActorSystem();
        var actor = InvokeGameFramework(() => ZoneNativePhysicsModule.Virtual<FlowActorLookupCall>(
            actorSystem, 0x10)(actorSystem, entityId));
        if (actor == 0) return null; // The actual native result, including an empty ActorSystem.
        RequireFlowPlayerClassOwner();
        var table = Marshal.ReadIntPtr(actor);
        var token = ReadFlowActorClassToken(actor);
        var entitySystem = RequireFlowEntitySystem();
        var entity = FindFlowEntity(entityId);
        if (entity == 0 || ReadFlowEntityId(entity) != entityId)
            throw new InvalidOperationException("A native actor has no matching current full EntitySystem registration.");
        return new(this, _flowActorIdentityIssuer, entityId, entitySystem, entity,
            _gameFramework, actorSystem, actor, table, token);
    }

    internal void ValidateFlowActorIdentity(FlowActorIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        VerifyFlowCallbackAccess();
        if (!identity.IsIssuedBy(this))
            throw new InvalidOperationException("The actor identity belongs to another engine generation.");
        var current = CaptureFlowActorIdentity(identity.EntityId);
        if (current is null || current.EntitySystem != identity.EntitySystem || current.Entity != identity.Entity ||
            current.Framework != identity.Framework || current.ActorSystem != identity.ActorSystem ||
            current.Actor != identity.Actor || current.ActorTable != identity.ActorTable || current.ClassToken != identity.ClassToken)
            throw new InvalidOperationException("The retained native actor registration was removed or replaced.");
    }

    internal (nint Actor, nint ClassToken) GetFlowClientActorObservation()
    {
        VerifyFlowCallbackAccess();
        var actor = GetFlowCallbackClientActor(); // Actual framework+148, including its native absence gates.
        if (actor == 0) return default;
        RequireFlowPlayerClassOwner();
        return (actor, ReadFlowActorClassToken(actor));
    }

    private void RequireFlowPlayerClassOwner()
    {
        if (_flowActorRegistry?.HasPlayerClassOwner != true)
            throw new NotSupportedException("An actual native actor requires the retained P1 canonical player-class owner.");
    }

    internal void VerifyFlowActorRegistryShutdownAccess()
    {
        VerifyAccess(); // Managed state only; shutdown admission does not require a callback gate.
        if (!_usesGameFramework || _disposed)
            throw new InvalidOperationException("The actor registry has no live framework engine owner.");
    }

    private nint RequireFlowActorSystem()
    {
        _ = RequireFlowCallbackEnvironment();
        ValidateGameFrameworkOwner(_system);
        // Original39316630: game+410 -> current framework, then framework+A8. The verified
        // actual getter306F7DF0 returns framework+4E8; absence is not a missing-actor result.
        var actors = InvokeGameFramework(() => BindGameFramework<PointerCall>(0xa8, 0x1f7df0)(_gameFramework));
        if (actors == 0 || actors != Marshal.ReadIntPtr(_gameFramework + 0x4e8))
            throw new InvalidOperationException("The framework has no actual current ActorSystem.");
        var table = Marshal.ReadIntPtr(actors);
        if (table == 0 || Marshal.ReadIntPtr(table + 0x10) == 0)
            throw new InvalidOperationException("The actual ActorSystem lookup interface is unavailable.");
        if (_flowActorSystem == 0)
        {
            _flowActorSystem = actors;
            _flowActorSystemTable = table;
        }
        else if (_flowActorSystem != actors || _flowActorSystemTable != table)
            throw new InvalidOperationException("The framework's ActorSystem generation changed.");
        return actors;
    }

    private nint ReadFlowActorClassToken(nint actor)
    {
        VerifyFlowCallbackAccess();
        var table = Marshal.ReadIntPtr(actor);
        if (table == 0 || Marshal.ReadIntPtr(table + 0x1f0) == 0)
            throw new InvalidOperationException("The actual actor has no native class-token operation.");
        // Original CPlayer table39FD4E58+1F0 ->39302950 returns literal39FC0ED0.
        // The replacement class owner must provide its canonical owned token; never strcmp.
        return InvokeGameFramework(() => ZoneNativePhysicsModule.Virtual<PointerCall>(actor, 0x1f0)(actor));
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint FlowActorLookupCall(nint actorSystem, uint entityId);
}

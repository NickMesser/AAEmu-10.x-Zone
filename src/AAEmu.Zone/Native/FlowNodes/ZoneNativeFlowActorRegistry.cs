using System.Numerics;

using AAEmu.Zone.Native.Engine;

namespace AAEmu.Zone.Native.FlowNodes;

/// <summary>
/// The real P1 player-class publication owner. Its canonical native token is returned by all
/// owned players' +1F0 operation, equivalent to original CPlayer's single literal39FC0ED0.
/// Merely allocating a string or labelling an arbitrary actor does not implement this contract.
/// This bridge supplies no class creator, token allocation or substitute P1 implementation.
/// </summary>
internal interface IZoneNativeFlowPlayerClassOwner
{
    ZoneNativeEngine Engine { get; }
    object Lifetime { get; }
    nint PlayerClassToken { get; }
    void ValidateForRegistration();
    void ValidateRetainedClass();

    /// <summary>
    /// Managed-only proof that actual actors/listeners are gone and the class publication can
    /// undergo framework shutdown. The class/token/callback owner remains retained until its
    /// actual native creator references are released; this method must not free it early.
    /// </summary>
    void VerifyReadyForFrameworkShutdown();
}

/// <summary>
/// A concrete P1-owned actor/GameObject lifetime, published into the actual ActorSystem.
/// The identity is captured after native publication, and Lifetime is unique and immutable
/// for that allocation. ValidateRetainedActor must reject retirement, replacement and reuse,
/// even when the allocator reuses the same native addresses/full ID. Sensor and Operations
/// must operate on this actor's real owned state. There is deliberately no stand-in owner.
/// </summary>
internal interface IZoneNativeFlowPlayerActorOwner
{
    ZoneNativeEngine.FlowActorIdentity Identity { get; }
    object Lifetime { get; }
    IZoneNativeFlowPlayerClassOwner PlayerClass { get; }
    IZoneNativeFlowActorSensorPlayer Sensor { get; }
    IZoneNativeFlowPlayerOperations Operations { get; }
    void ValidateRetainedActor();

    /// <summary>
    /// Confirm that no flow-listener callback is in flight and no detached flow listener is
    /// retained by the actual player producer. Called after exact RemoveListener operations,
    /// before the registry releases the actor binding and before native actor destruction.
    /// </summary>
    void VerifyFlowListenersRetired();
}

/// <summary>
/// One engine's actual player binding registry. Native lookups run even with no managed
/// bindings; an actual unsupported player is never hidden behind a manufactured null result.
/// Registration is a bridge for future P1 admission, not player or GameObject construction.
/// </summary>
internal sealed class ZoneNativeFlowActorRegistry : IZoneNativeFlowActorSensorServices
{
    private readonly ZoneNativeEngine _engine;
    private readonly Dictionary<uint, PlayerBinding> _byId = [];
    private readonly Dictionary<nint, PlayerBinding> _byActor = [];
    private readonly HashSet<object> _retiredLifetimes = new(ReferenceEqualityComparer.Instance);
    private IZoneNativeFlowPlayerClassOwner? _playerClass;
    private object? _classLifetime;
    private nint _classToken;

    internal bool HasPlayerClassOwner => _playerClass is not null;

    /// <summary>
    /// The retained P1 class owner for exact-owner binding, or null while the canonical
    /// publication is unresolved. Read-only: the player factory consumes this after its
    /// dependency gate passes; it can never substitute for RegisterPlayerClass's validation.
    /// </summary>
    internal IZoneNativeFlowPlayerClassOwner? PlayerClassOwner => _playerClass;

    internal ZoneNativeFlowActorRegistry(ZoneNativeEngine engine) => _engine = engine;

    public void ValidateForRegistration()
    {
        _engine.VerifyFlowActorRegistryOwner(this);
        _engine.ValidateFlowFactoryConstruction();
        // Pre-P1 startup is valid. This validation never substitutes for later native lookup.
        _playerClass?.ValidateForRegistration();
    }

    internal void RegisterPlayerClass(IZoneNativeFlowPlayerClassOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _engine.VerifyFlowActorRegistryOwner(this);
        _engine.VerifyFlowCallbackAccess();
        owner.ValidateForRegistration();
        owner.ValidateRetainedClass();
        var lifetime = owner.Lifetime ?? throw new InvalidOperationException("A player class requires its retained lifetime.");
        var token = owner.PlayerClassToken;
        if (!ReferenceEquals(owner.Engine, _engine) || token == 0)
            throw new InvalidOperationException("The canonical player class must belong to this engine's actual P1 publication.");
        if (_playerClass is not null && (!ReferenceEquals(owner, _playerClass) ||
            !ReferenceEquals(lifetime, _classLifetime) || token != _classToken))
            throw new InvalidOperationException("The native player-class identity cannot be replaced within an engine generation.");
        _playerClass = owner;
        _classLifetime = lifetime;
        _classToken = token;
    }

    internal PlayerBinding BindPlayer(IZoneNativeFlowPlayerActorOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _engine.VerifyFlowActorRegistryOwner(this);
        _engine.VerifyFlowCallbackAccess();
        var identity = owner.Identity ?? throw new InvalidOperationException("P1 admission requires an engine-issued actor identity.");
        var lifetime = owner.Lifetime ?? throw new InvalidOperationException("P1 admission requires the actual actor lifetime.");
        ValidatePlayerClass();
        owner.ValidateRetainedActor();
        _engine.ValidateFlowActorIdentity(identity);
        if (!ReferenceEquals(owner.PlayerClass, _playerClass) || identity.ClassToken != _classToken ||
            !ReferenceEquals(owner.Identity, identity) || !ReferenceEquals(owner.Lifetime, lifetime))
            throw new InvalidOperationException("The P1 lifetime does not own this exact native player-class registration.");
        if (_retiredLifetimes.Contains(lifetime))
            throw new InvalidOperationException("A retired P1 actor lifetime cannot be registered again.");
        if (_byId.ContainsKey(identity.EntityId) || _byActor.ContainsKey(identity.Actor))
            throw new InvalidOperationException("Retire the existing exact player binding before publishing another owner.");
        var binding = new PlayerBinding(this, owner, identity, lifetime,
            owner.Sensor ?? throw new InvalidOperationException("The P1 actor has no real listener owner."),
            owner.Operations ?? throw new InvalidOperationException("The P1 actor has no real player-operation owner."));
        _byId.Add(identity.EntityId, binding);
        try { _byActor.Add(identity.Actor, binding); }
        catch { _byId.Remove(identity.EntityId); throw; }
        return binding;
    }

    public IZoneNativeFlowActorSensorPlayer? FindPlayer(uint entityId)
    {
        _engine.VerifyFlowActorRegistryOwner(this);
        var identity = _engine.CaptureFlowActorIdentity(entityId);
        if (identity is null) return null;
        if (!IsPlayerClass(identity.ClassToken)) return null;
        if (!_byActor.TryGetValue(identity.Actor, out var binding) || binding.Identity.EntityId != identity.EntityId)
            throw new NotSupportedException("The actual native player has no admitted P1 listener/operation owner.");
        ValidateBinding(binding);
        return binding;
    }

    internal IZoneNativeFlowPlayerOperations? GetClientPlayer()
    {
        _engine.VerifyFlowActorRegistryOwner(this);
        var current = _engine.GetFlowClientActorObservation();
        if (current.Actor == 0) return null;
        if (!IsPlayerClass(current.ClassToken))
            throw new NotSupportedException("The actual native client actor is not a supported P1 player.");
        if (!_byActor.TryGetValue(current.Actor, out var binding))
            throw new NotSupportedException("The actual native client player has no admitted P1 operation owner.");
        ValidateBinding(binding);
        return binding;
    }

    internal void UnbindPlayer(PlayerBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        _engine.VerifyFlowCallbackAccess();
        if (!ReferenceEquals(binding.Registry, this))
            throw new InvalidOperationException("The player binding belongs to another registry.");
        if (binding.Retired) return; // An old capability can never remove a replacement binding.
        ValidateBinding(binding);
        if (binding.ActiveCalls != 0)
            throw new InvalidOperationException("Complete the current player operation before native actor retirement.");
        binding.Retiring = true;
        try
        {
            foreach (var listener in binding.Listeners.ToArray())
            {
                binding.Sensor.RemoveListener(listener);
                binding.Listeners.Remove(listener);
            }
            binding.Owner.VerifyFlowListenersRetired();
            binding.Owner.ValidateRetainedActor();
            _engine.ValidateFlowActorIdentity(binding.Identity);
            if (!_byId.TryGetValue(binding.Identity.EntityId, out var byId) || !ReferenceEquals(byId, binding) ||
                !_byActor.TryGetValue(binding.Identity.Actor, out var byActor) || !ReferenceEquals(byActor, binding))
                throw new InvalidOperationException("The exact player binding changed during listener retirement.");
            _retiredLifetimes.Add(binding.Lifetime);
            _byId.Remove(binding.Identity.EntityId);
            _byActor.Remove(binding.Identity.Actor);
            binding.Retired = true;
        }
        catch (Exception error)
        {
            // Partial native listener removal is not replayable by assumption. Keep the owner,
            // callback references and binding rooted, and stop subsequent engine operations.
            _engine.RecordFlowCallbackFault(error);
            throw;
        }
    }

    internal void VerifyNoLiveBindingsForShutdown()
    {
        _engine.VerifyFlowActorRegistryOwner(this);
        _engine.VerifyFlowActorRegistryShutdownAccess();
        if (_byId.Count != 0 || _byActor.Count != 0)
            throw new InvalidOperationException("Retire all admitted P1 actors and flow listeners before framework shutdown.");
        if (_playerClass is not null)
        {
            if (!ReferenceEquals(_playerClass.Engine, _engine) || !ReferenceEquals(_playerClass.Lifetime, _classLifetime) ||
                _playerClass.PlayerClassToken != _classToken)
                throw new InvalidOperationException("The retained P1 class lifetime changed before framework shutdown.");
            _playerClass.VerifyReadyForFrameworkShutdown();
        }
    }

    private bool IsPlayerClass(nint token)
    {
        ValidatePlayerClass();
        return token == _classToken; // Original pointer identity predicate, never string matching.
    }

    private void ValidatePlayerClass()
    {
        _engine.VerifyFlowCallbackAccess();
        if (_playerClass is null)
            throw new NotSupportedException("An actual native actor requires the retained P1 canonical player-class owner.");
        _playerClass.ValidateRetainedClass();
        if (!ReferenceEquals(_playerClass.Engine, _engine) || !ReferenceEquals(_playerClass.Lifetime, _classLifetime) ||
            _playerClass.PlayerClassToken != _classToken)
            throw new InvalidOperationException("The P1 canonical class token or publication lifetime changed.");
    }

    private void ValidateBinding(PlayerBinding binding)
    {
        _engine.VerifyFlowCallbackAccess();
        if (!ReferenceEquals(binding.Registry, this) || binding.Retiring || binding.Retired ||
            !_byId.TryGetValue(binding.Identity.EntityId, out var byId) || !ReferenceEquals(byId, binding) ||
            !_byActor.TryGetValue(binding.Identity.Actor, out var byActor) || !ReferenceEquals(byActor, binding))
            throw new InvalidOperationException("The player binding is no longer active in this registry.");
        ValidatePlayerClass();
        binding.Owner.ValidateRetainedActor();
        if (!ReferenceEquals(binding.Owner.Identity, binding.Identity) || !ReferenceEquals(binding.Owner.Lifetime, binding.Lifetime) ||
            !ReferenceEquals(binding.Owner.PlayerClass, _playerClass) || !ReferenceEquals(binding.Owner.Sensor, binding.Sensor) ||
            !ReferenceEquals(binding.Owner.Operations, binding.Operations))
            throw new InvalidOperationException("The P1 actor lifetime or its concrete operation owner was replaced.");
        _engine.ValidateFlowActorIdentity(binding.Identity);
    }

    /// <summary>Exact retirement capability and guarded access to the real retained P1 operations.</summary>
    internal sealed class PlayerBinding : IZoneNativeFlowActorSensorPlayer, IZoneNativeFlowPlayerOperations
    {
        internal readonly ZoneNativeFlowActorRegistry Registry;
        internal readonly IZoneNativeFlowPlayerActorOwner Owner;
        internal readonly ZoneNativeEngine.FlowActorIdentity Identity;
        internal readonly object Lifetime;
        internal readonly IZoneNativeFlowActorSensorPlayer Sensor;
        internal readonly IZoneNativeFlowPlayerOperations Operations;
        internal readonly HashSet<IZoneNativeFlowActorSensorListener> Listeners = new(ReferenceEqualityComparer.Instance);
        internal bool Retiring, Retired;
        internal int ActiveCalls;

        internal PlayerBinding(ZoneNativeFlowActorRegistry registry, IZoneNativeFlowPlayerActorOwner owner,
            ZoneNativeEngine.FlowActorIdentity identity, object lifetime, IZoneNativeFlowActorSensorPlayer sensor,
            IZoneNativeFlowPlayerOperations operations)
        { Registry = registry; Owner = owner; Identity = identity; Lifetime = lifetime; Sensor = sensor; Operations = operations; }

        private T Invoke<T>(Func<T> operation)
        {
            Registry.ValidateBinding(this);
            ActiveCalls++;
            try { return operation(); }
            catch (Exception error) { Registry._engine.RecordFlowCallbackFault(error); throw; }
            finally { ActiveCalls--; }
        }

        public void AddListener(IZoneNativeFlowActorSensorListener listener)
        {
            ArgumentNullException.ThrowIfNull(listener);
            Invoke(() =>
            {
                Listeners.Add(listener); // Retain before fallible native registration.
                Sensor.AddListener(listener); // Preserve native identity dedup, including repeated calls.
                return 0;
            });
        }

        public void RemoveListener(IZoneNativeFlowActorSensorListener listener)
        {
            ArgumentNullException.ThrowIfNull(listener);
            Invoke(() => { Sensor.RemoveListener(listener); Listeners.Remove(listener); return 0; });
        }

        public Quaternion ReadStagingRotation() => Invoke(Operations.ReadStagingRotation);
        public byte DrawHiddenState => Invoke(() => Operations.DrawHiddenState);
        public void SetDrawHiddenState(bool hidden) => Invoke(() => { Operations.SetDrawHiddenState(hidden); return 0; });
        public void LinkToEntity(uint targetEntityId, bool keepTransformOnDetach) =>
            Invoke(() => { Operations.LinkToEntity(targetEntityId, keepTransformOnDetach); return 0; });
        public void ApplyStaging(bool enabled, ZoneNativeFlowPlayerStagingParameters parameters) =>
            Invoke(() => { Operations.ApplyStaging(enabled, parameters); return 0; });
    }
}

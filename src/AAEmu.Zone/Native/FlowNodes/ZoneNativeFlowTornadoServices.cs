using AAEmu.Zone.Native.Engine;

namespace AAEmu.Zone.Native.FlowNodes;

/// <summary>
/// Capability supplied only by the actual engine-owned Tornado extension producer. Identity
/// must be captured from its real framework publication; Lifetime is an immutable identity
/// for this one production/retirement interval. A native pointer and an arbitrary listener
/// adapter do not establish this ownership. There is no such extension producer in this file.
/// </summary>
internal interface IZoneNativeFlowTornadoExtensionOwner : IZoneNativeFlowTornado
{
    ZoneNativeEngine.FlowTornadoIdentity Identity { get; }
    object Lifetime { get; }

    /// <summary>Verify this exact extension is still owned and can accept a target.</summary>
    void VerifyLive();

    /// <summary>
    /// Managed-only reachability of the supplied bridge receiver, including in-flight native
    /// completion and the post-callback clearing interval. Must remain valid after retirement.
    /// Track3920DBE0 replacement and3920FE10's entity-event/current-receiver/unconditional-clear
    /// sequence. False must prove that no native extension can invoke this receiver again.
    /// </summary>
    bool RetainsCompletion(IZoneNativeFlowTornadoCompletion completion);

    /// <summary>
    /// Managed-only proof that actual extension retirement returned, including its callbacks
    /// and target lifetime. Do not perform destruction or cancellation here. An uncertain
    /// retirement must throw and preserve all ownership instead of claiming successful release.
    /// </summary>
    void VerifyRetired();
}

/// <summary>
/// Actual framework/entity lookup and retained completion ownership for TornadoWander. An
/// empty registry does not bypass lookup: native null returns null, while a native present
/// extension without its exact owned capability is unsupported. No CTornado is constructed,
/// no generic flow-node pointer is stored in native memory and no wandering is simulated.
/// </summary>
internal sealed class ZoneNativeFlowTornadoServices : IZoneNativeFlowTornadoServices
{
    private readonly ZoneNativeEngine _engine;
    private readonly Dictionary<nint, OwnerBinding> _bindings = [];
    private readonly List<IZoneNativeFlowTornadoExtensionOwner> _retainedOwners = [];
    private readonly Dictionary<IZoneNativeFlowTornadoCompletion, Completion> _completions =
        new(ReferenceEqualityComparer.Instance);
    private bool _busy;
    private int _deliveryDepth;
    private Exception? _fault;

    internal ZoneNativeFlowTornadoServices(ZoneNativeEngine engine) => _engine = engine;

    public void ValidateForRegistration()
    {
        _engine.ValidateFlowFactoryConstruction();
        ThrowIfFaulted();
    }

    public IZoneNativeFlowTornado? FindTornado(nint entity)
    {
        using var operation = Enter();
        var observed = _engine.ObserveFlowTornado(entity);
        if (observed.Extension == 0) return null;
        if (!_bindings.TryGetValue(observed.Extension, out var binding))
            throw new NotSupportedException("The actual Tornado extension has no owned adapter in this engine generation.");
        if (binding.Identity.Entity != entity || binding.Identity.EntityId != observed.EntityId ||
            binding.Identity.ExtensionTable != observed.ExtensionTable)
            throw new InvalidOperationException("The queried Tornado extension does not match its captured owner.");
        ValidateOwner(binding);
        return binding;
    }

    public nint FindEntity(uint entityId)
    {
        using var operation = Enter();
        return _engine.FindFlowEntity(entityId);
    }

    /// <summary>
    /// Called by a real extension producer after framework publication, under the current
    /// engine callback. The returned binding is not an independent native extension owner.
    /// The producer must retire its actual extension before RetireOwner can release this root.
    /// </summary>
    internal OwnerBinding BindOwner(IZoneNativeFlowTornadoExtensionOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        using var operation = Enter();
        // Owner getters are external code. Retain first, then validate under the busy gate;
        // uncertainty leaves the engine rooted rather than losing a partially bound owner.
        _retainedOwners.Add(owner);
        try
        {
            var identity = owner.Identity ?? throw new InvalidOperationException("The Tornado owner has no issued identity.");
            var lifetime = owner.Lifetime ?? throw new InvalidOperationException("The Tornado owner has no immutable lifetime.");
            _engine.ValidateFlowTornadoIdentity(identity);
            owner.VerifyLive();
            if (!ReferenceEquals(identity, owner.Identity) || !ReferenceEquals(lifetime, owner.Lifetime))
                throw new InvalidOperationException("The Tornado owner changed its identity during admission.");
            if (_bindings.ContainsKey(identity.Extension))
                throw new InvalidOperationException("The actual Tornado extension already has an owned binding.");
            VerifyNoFault(); // A provider may have swallowed a rejected reentry.
            var binding = new OwnerBinding(this, owner, identity, lifetime);
            _bindings.Add(identity.Extension, binding);
            return binding;
        }
        catch (Exception error) { Fail(error); throw; }
    }

    /// <summary>Managed owner proof plus actual absence/replacement precede release of roots.</summary>
    internal void RetireOwner(OwnerBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        using var operation = Enter();
        RequireBinding(binding);
        try
        {
            VerifyStableOwner(binding);
            binding.Owner.VerifyRetired();
            var currentEntity = _engine.FindFlowEntity(binding.Identity.EntityId);
            if (currentEntity != 0 &&
                _engine.ObserveFlowTornado(currentEntity).Extension == binding.Identity.Extension)
                throw new InvalidOperationException("The retired Tornado extension remains published in the actual framework.");
            foreach (var completion in binding.Completions)
                if (completion.Delivering != 0 || binding.Owner.RetainsCompletion(completion))
                    throw new InvalidOperationException("The retired Tornado owner still retains a completion receiver.");
            // All fallible ownership proofs complete before removing either native identity
            // or owner roots. Node-local receiver roots survive until node Destroy admits them.
            VerifyNoFault();
            var ownerIndex = _retainedOwners.FindIndex(candidate => ReferenceEquals(candidate, binding.Owner));
            if (ownerIndex < 0)
                throw new InvalidOperationException("The Tornado producer lost its retained owner identity.");
            _bindings.Remove(binding.Identity.Extension);
            binding.Retired = true;
            binding.Completions.Clear();
            _retainedOwners.RemoveAt(ownerIndex); // Never call an owner's overridable Equals.
        }
        catch (Exception error) { Fail(error); throw; }
    }

    public void VerifyCompletionRetired(IZoneNativeFlowTornadoCompletion completion)
    {
        ArgumentNullException.ThrowIfNull(completion);
        using var operation = Enter(); // Current callback only; no environment is dereferenced.
        if (!_completions.TryGetValue(completion, out var retained)) return;
        if (retained.Delivering != 0)
            throw new InvalidOperationException("A Tornado completion cannot retire its own flow node.");
        try
        {
            foreach (var binding in _bindings.Values)
            {
                if (!binding.Completions.Contains(retained)) continue;
                VerifyStableOwner(binding);
                if (binding.Owner.RetainsCompletion(retained))
                    throw new InvalidOperationException("An actual Tornado extension can still invoke this node.");
            }
            VerifyNoFault();
            foreach (var binding in _bindings.Values) binding.Completions.Remove(retained);
            _completions.Remove(completion);
            retained.Retired = true;
        }
        catch (Exception error) { Fail(error); throw; }
    }

    /// <summary>Managed-only shutdown admission; zero native pointer reads or provider calls.</summary>
    internal void VerifyNoLiveBindingsForShutdown()
    {
        ThrowIfFaulted();
        if (_busy || _deliveryDepth != 0 || _bindings.Count != 0 || _retainedOwners.Count != 0)
            throw new InvalidOperationException("Retire actual Tornado extension owners before native framework shutdown.");
    }

    private void SetTarget(OwnerBinding binding, nint targetEntity, IZoneNativeFlowTornadoCompletion receiver)
    {
        ArgumentNullException.ThrowIfNull(receiver);
        using var operation = Enter();
        RequireBinding(binding);
        ValidateOwner(binding);
        // Admit only the real current IEntity returned by this engine. The actual extension
        // owner supplies its target lifetime; no keepalive or extra native AddRef is invented.
        var targetId = _engine.ReadFlowEntityId(targetEntity);
        if (_engine.FindFlowEntity(targetId) != targetEntity)
            throw new InvalidOperationException("The Tornado target is not the current entity in this engine.");
        if (!_completions.TryGetValue(receiver, out var completion))
        {
            completion = new Completion(this, receiver);
            _completions.Add(receiver, completion);
        }
        binding.Completions.Add(completion); // Retain before any external/native publication.
        try
        {
            binding.Owner.SetTarget(targetEntity, completion);
            VerifyStableOwner(binding);
            //3920DBE0 stores a receiver and never invokes it. A provider that did not retain
            // this receiver has not completed the exact assignment contract.
            if (!binding.Owner.RetainsCompletion(completion))
                throw new InvalidOperationException("The Tornado owner did not retain its newly assigned completion.");
            VerifyNoFault();
        }
        catch (Exception error) { Fail(error); throw; }
    }

    private void ValidateOwner(OwnerBinding binding)
    {
        RequireBinding(binding);
        try
        {
            VerifyStableOwner(binding);
            _engine.ValidateFlowTornadoIdentity(binding.Identity);
            binding.Owner.VerifyLive();
            VerifyStableOwner(binding);
            VerifyNoFault();
        }
        catch (Exception error) { Fail(error); throw; }
    }

    private static void VerifyStableOwner(OwnerBinding binding)
    {
        if (!ReferenceEquals(binding.Owner.Identity, binding.Identity) ||
            !ReferenceEquals(binding.Owner.Lifetime, binding.Lifetime))
            throw new InvalidOperationException("The Tornado owner's issued identity or lifetime changed.");
    }

    private void RequireBinding(OwnerBinding binding)
    {
        if (!ReferenceEquals(binding.Services, this) || binding.Retired ||
            !_bindings.TryGetValue(binding.Identity.Extension, out var current) || !ReferenceEquals(current, binding))
            throw new ObjectDisposedException(nameof(OwnerBinding));
    }

    private Operation Enter()
    {
        _engine.VerifyFlowCallbackAccess();
        ThrowIfFaulted();
        if (_busy)
        {
            var error = new InvalidOperationException("A Tornado owner operation reentered its binding registry.");
            Fail(error);
            throw error;
        }
        _busy = true;
        return new Operation(this);
    }

    private void ThrowIfFaulted()
    {
        if (_fault is not null) throw new InvalidOperationException("The Tornado ownership bridge is faulted.", _fault);
    }

    private void VerifyNoFault()
    {
        ThrowIfFaulted();
        _engine.ThrowIfFlowCallbackFaulted();
    }

    private void Fail(Exception error)
    {
        _fault ??= error;
        _engine.RecordFlowCallbackFault(error);
    }

    private void Deliver(Completion completion)
    {
        _engine.VerifyFlowCallbackAccess();
        ThrowIfFaulted();
        if (_busy || completion.Retired)
        {
            var error = new InvalidOperationException("Tornado completion escaped its actual retained delivery interval.");
            Fail(error);
            throw error;
        }
        // A retained wrapper alone is not permission to emit a graph output. Revalidate the
        // exact current extension owner and its managed pending interval before delivery.
        using (Enter())
        {
            try
            {
                var admitted = false;
                foreach (var binding in _bindings.Values)
                {
                    if (!binding.Completions.Contains(completion)) continue;
                    ValidateOwner(binding);
                    if (binding.Owner.RetainsCompletion(completion)) admitted = true;
                }
                if (!admitted)
                    throw new InvalidOperationException("No live Tornado owner retains this completion delivery.");
                VerifyNoFault();
                completion.Delivering++;
                _deliveryDepth++;
            }
            catch (Exception error) { Fail(error); throw; }
        }
        // Native graph output may synchronously activate a new target. The registry is no
        // longer busy here; the delivery counter still prevents reclaiming this receiver.
        try { completion.Receiver.OnTargetReached(); VerifyNoFault(); }
        catch (Exception error) { Fail(error); throw; }
        finally { completion.Delivering--; _deliveryDepth--; }
        // Do not drop the receiver here:3920FE10 clears native fields only AFTER this call
        // returns. Real owner tracking must retain it through that post-callback interval.
    }

    internal sealed class OwnerBinding : IZoneNativeFlowTornado
    {
        internal readonly ZoneNativeFlowTornadoServices Services;
        internal readonly IZoneNativeFlowTornadoExtensionOwner Owner;
        internal readonly ZoneNativeEngine.FlowTornadoIdentity Identity;
        internal readonly object Lifetime;
        internal readonly HashSet<Completion> Completions = [];
        internal bool Retired;

        internal OwnerBinding(ZoneNativeFlowTornadoServices services, IZoneNativeFlowTornadoExtensionOwner owner,
            ZoneNativeEngine.FlowTornadoIdentity identity, object lifetime)
        { Services = services; Owner = owner; Identity = identity; Lifetime = lifetime; }

        public void SetTarget(nint targetEntity, IZoneNativeFlowTornadoCompletion completion) =>
            Services.SetTarget(this, targetEntity, completion);
    }

    internal sealed class Completion(ZoneNativeFlowTornadoServices services, IZoneNativeFlowTornadoCompletion receiver)
        : IZoneNativeFlowTornadoCompletion
    {
        internal readonly IZoneNativeFlowTornadoCompletion Receiver = receiver;
        internal int Delivering;
        internal bool Retired;
        public void OnTargetReached() => services.Deliver(this);
    }

    private readonly struct Operation(ZoneNativeFlowTornadoServices services) : IDisposable
    {
        public void Dispose() => services._busy = false;
    }
}

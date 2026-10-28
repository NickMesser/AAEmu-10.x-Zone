using System.Numerics;
using System.Diagnostics.CodeAnalysis;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Ai;
using AAEmu.Zone.Content;
using AAEmu.Zone.Npcs;
using AAEmu.Zone.Scene;
using AAEmu.Zone.Scripting;
using AAEmu.Zone.Units;

namespace AAEmu.Zone.Hosting;

/// <summary>
/// An admitted AI construction profile. Radius is the actual AI object's radius, independently
/// of graph pass radius and living collider radius. The scene allocates the entity ID separately.
/// </summary>
public sealed record ZoneManagedNpcActorProfile(ZoneAiObjectState Object, short ObjectType,
    ulong ProxyAllocationKey, bool IsPuppet, ZoneActorControlState Controls, IDisposable? Lifetime = null);

/// <summary>
/// One managed scene's original NPC script composition. Bootstrap and model classes execute
/// without engine modules. Per-NPC initialization additionally requires real actor construction
/// and gameplay API owners; refusals unwind the exact generation instead of scheduling it as ready.
/// </summary>
public sealed class ZoneManagedNpcRuntime : IDisposable
{
    private readonly ZoneSceneNpcCatalog _catalog;
    private readonly ZoneModelContent _models;
    private readonly ZoneNpcActorModelScripts _modelScripts;
    private readonly Func<ZoneActorHandle, ZoneNpc, uint, ZoneManagedNpcActorProfile>? _createProfile;
    private readonly IDisposable? _ownedScriptServices;
    private readonly Dictionary<ZoneActorHandle, OwnedActor> _live = [];
    private readonly HashSet<string> _preparedClasses = new(StringComparer.Ordinal);
    private uint _nextEntity;
    private bool _bootstrapAttempted;
    public ZoneManagedLuaRuntime Lua { get; }
    public ZoneScriptLoader Loader { get; }
    public ZoneManagedNpcActorScene Actors { get; }
    public ZoneManagedNpcScriptHost Scripts { get; }
    public IZoneManagedNpcScriptServices ScriptServices { get; }
    public bool IsDisposed { get; private set; }
    public int Count => _live.Count;
    public int ModelClassesPrepared => _preparedClasses.Count;
    public int RefusedActors { get; private set; }
    public string ScriptingStatus => $"Managed Lua source VM: bootstrapReady={Scripts.BootstrapReport.Ready}, " +
        $"loadedFiles={Loader.LoadedScripts.Count}, preparedModelClasses={ModelClassesPrepared}, " +
        $"scriptReadyActors={Count}, refusedActors={RefusedActors}; gameplay parity=false" +
        (Scripts.BootstrapReport.Refusal is { } refusal ? $"; {refusal}" : ".");

    public ZoneManagedNpcRuntime(ZoneUnitRegistry units, ZoneSceneNpcCatalog catalog, ZoneModelContent models,
        ZoneNpcContent npcs, IZoneScriptSource source, Func<ZoneUnit, Vector3> position,
        Func<long> frameMicroseconds, Func<uint> random, Func<DateTimeOffset> utcNow, TimeSpan period,
        Action<string>? trace = null, IZoneManagedNpcScriptServices? services = null,
        Func<ZoneActorHandle, ZoneNpc, uint, ZoneManagedNpcActorProfile>? createProfile = null,
        Func<IZoneLuaRuntime, IZoneManagedNpcScriptServices>? createScriptServices = null)
    {
        if (services is not null && createScriptServices is not null)
            throw new ArgumentException("Supply borrowed script services or an owned service factory, not both.");
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _models = models ?? throw new ArgumentNullException(nameof(models));
        _createProfile = createProfile;
        Lua = new();
        try
        {
            Loader = new(Lua, source, message => trace?.Invoke(message), () => 0);
            Actors = new(units, catalog, Lua, new(), position, frameMicroseconds, random,
                name => { if (!Loader.ExecuteFile(name)) throw new ZoneLuaException(Loader.LastFailure ?? $"Script '{name}' failed."); }, period, trace);
            try
            {
                ScriptServices = createScriptServices is { } factory
                    ? factory(Lua) ?? throw new InvalidOperationException("The script service factory returned no owner.")
                    : services ?? new ZoneManagedNpcScriptServices();
                _ownedScriptServices = createScriptServices is null ? null : ScriptServices as IDisposable;
                Scripts = new(Lua, Loader, npcs, Actors, ScriptServices, frameMicroseconds, utcNow, trace);
                _modelScripts = new(Lua);
            }
            catch { _ownedScriptServices?.Dispose(); Actors.Dispose(); throw; }
        }
        catch { Lua.Dispose(); throw; }
    }

    public ZoneNpcScriptBootstrapReport Bootstrap()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (_bootstrapAttempted) return Scripts.BootstrapReport;
        _bootstrapAttempted = true;
        return Scripts.Bootstrap();
    }

    /// <summary>
    /// Factory for ZoneManagedAiProvider. It transfers only a fully initialized script owner.
    /// Managed entity IDs are fresh scene allocations, never derived from World/NPC object IDs.
    /// They are reserved until this scene retires, including failed initialization attempts.
    /// </summary>
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification =
        "Successful initialization transfers the actor to _live. The finally block aggregates disposal of every untransferred stage; lambda cleanup is not recognized by the analyzer.")]
    public IZoneManagedAiActor CreateActor(ZoneActorHandle handle)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        var npc = _catalog.Find(handle) ?? throw new InvalidOperationException("NPC script admission requires its exact catalog generation.");
        ZoneManagedNpcActor? actor = null;
        ZoneNpcAuthoredEntity? prepared = null;
        ZoneManagedNpcScriptBinding? binding = null;
        ZoneManagedNpcActorProfile? profile = null;
        ZoneManagedNpcScriptEffectsLease? effectsLease = null;
        Exception? failure = null;
        try
        {
            if (!_bootstrapAttempted || !Scripts.BootstrapReport.Ready)
                throw new NotSupportedException(Scripts.BootstrapReport.Refusal ?? "Original NPC scripts have not bootstrapped.");
            var model = _models.ActorForModel(npc.Model.Id) ??
                throw new NotSupportedException($"NPC model {npc.Model.Id} is not a resolved actor model.");
            if (_createProfile is null)
                throw new NotSupportedException("NPC initialization profile is not admitted: complete actor parameters, " +
                    "physical/character components and required game services need owners.");
            using var sourceClass = _modelScripts.RetainNpcClass(npc.Model, model);
            _preparedClasses.Add(ZoneNpcActorModelScripts.NpcClassName(npc.Model));
            if (_nextEntity == uint.MaxValue) throw new InvalidOperationException("Managed NPC entity IDs are exhausted.");
            var entityId = ++_nextEntity;
            profile = _createProfile(handle, npc, entityId) ?? throw new InvalidOperationException("NPC construction returned no profile.");
            if (profile.Object.EntityId != entityId)
                throw new InvalidOperationException("NPC construction did not retain its allocated managed entity ID.");
            actor = Actors.Prepare(handle, profile.Object, profile.Controls, profile.IsPuppet);
            prepared = Scripts.Scripts.PrepareFromClass(npc.TemplateId, new(entityId), sourceClass, actor);
            // The replacement entity allocator owns this name, separately from all numeric IDs.
            binding = Scripts.BindEntity(prepared, actor, npc.Unit.Snapshot.Name);
            Actors.Adopt(actor, profile.ObjectType, profile.ProxyAllocationKey, prepared.EntityScript);
            if (profile.Lifetime is ZoneManagedNpcProxyOwner proxy) proxy.Attach(actor);
            var behavior = prepared.DetachBehavior();
            try { Actors.RegisterBehavior(actor, behavior); }
            catch { behavior.Dispose(); throw; }
            if (ScriptServices is ZoneManagedNpcScriptEffects effects)
                effectsLease = effects.BindEntity(binding, new ZoneManagedNpcActorHealth());
            var readiness = Scripts.Initialize(binding);
            if (!readiness.SignalReady) throw new NotSupportedException(readiness.Refusal ?? "Authored NPC initialization did not admit script signals.");
            var owner = new OwnedActor(this, actor, prepared, binding, profile, effectsLease);
            _live.Add(handle, owner);
            actor = null; prepared = null; binding = null; profile = null; effectsLease = null;
            return owner;
        }
        catch (Exception error)
        {
            failure = error;
            if (error is NotSupportedException or ZoneLuaException or InvalidDataException)
            {
                RefusedActors++;
                failure = new NotSupportedException($"Managed authored NPC {npc.TemplateId} ({handle}) refused: {error.Message}", error);
                throw failure;
            }
            throw;
        }
        finally
        {
            List<Exception>? cleanup = null;
            Release(() => effectsLease?.Dispose()); Release(() => binding?.Dispose());
            Release(() => actor?.Dispose()); Release(() => prepared?.Dispose());
            Release(() => profile?.Lifetime?.Dispose());
            if (cleanup is not null) throw new AggregateException(failure is null ? cleanup : new[] { failure }.Concat(cleanup));
            void Release(Action action) { try { action(); } catch (Exception error) { (cleanup ??= []).Add(error); } }
        }
    }

    /// <summary>The scene calls this once after physical publication and before its AI pump.</summary>
    public IReadOnlyList<ZoneManagedNpcScriptFault> TickScripts(TimeSpan elapsed, Action<ZoneActorHandle> retireProviderActor)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        ArgumentNullException.ThrowIfNull(retireProviderActor);
        var faults = ScriptServices is ZoneManagedNpcScriptEffects effects ? effects.Tick(elapsed) : [];
        foreach (var fault in faults)
        {
            if (!_live.ContainsKey(fault.Actor)) continue;
            retireProviderActor(fault.Actor);
            if (_live.ContainsKey(fault.Actor))
                throw new InvalidOperationException("The script fault consumer did not retire its exact provider actor.");
            _catalog.SetAdmission(fault.Actor, ZoneSceneNpcAdmissionPhase.Refused, $"{fault.Callback}: {fault.Message}");
            RefusedActors++;
        }
        return faults;
    }

    private void Remove(OwnedActor owner)
    {
        if (!_live.TryGetValue(owner.Actor.Handle, out var current) || !ReferenceEquals(current, owner)) return;
        _live.Remove(owner.Actor.Handle);
        List<Exception>? errors = null;
        Release(() => owner.Effects?.Dispose()); Release(owner.Binding.Dispose);
        Release(owner.Actor.Dispose); Release(owner.Prepared.Dispose);
        Release(() => owner.Profile.Lifetime?.Dispose());
        if (errors is not null) throw new AggregateException("Managed NPC script retirement failed.", errors);
        void Release(Action action) { try { action(); } catch (Exception error) { (errors ??= []).Add(error); } }
    }

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        List<Exception>? errors = null;
        foreach (var actor in _live.Values.ToArray()) Release(actor.Dispose);
        Release(Scripts.Dispose); Release(Actors.Dispose); Release(_modelScripts.Dispose);
        Release(() => _ownedScriptServices?.Dispose()); Release(Lua.Dispose);
        if (errors is not null) throw new AggregateException("Managed NPC runtime retirement failed.", errors);
        void Release(Action action) { try { action(); } catch (Exception error) { (errors ??= []).Add(error); } }
    }

    private sealed class OwnedActor(ZoneManagedNpcRuntime runtime, ZoneManagedNpcActor actor,
        ZoneNpcAuthoredEntity prepared, ZoneManagedNpcScriptBinding binding, ZoneManagedNpcActorProfile profile,
        ZoneManagedNpcScriptEffectsLease? effects) : IZoneManagedAiActor
    {
        internal ZoneManagedNpcActor Actor { get; } = actor;
        internal ZoneNpcAuthoredEntity Prepared { get; } = prepared;
        internal ZoneManagedNpcScriptBinding Binding { get; } = binding;
        internal ZoneManagedNpcActorProfile Profile { get; } = profile;
        internal ZoneManagedNpcScriptEffectsLease? Effects { get; } = effects;
        public void SetActive(bool active) => Actor.SetActive(active);
        public void SetTarget(ZoneActorHandle? target) => Actor.SetTarget(target);
        public void SendSignal(ZoneAiSignal signal) => Actor.SendSignal(signal);
        public void Update(uint timeMilliseconds, float elapsedSeconds) => Actor.Update(timeMilliseconds, elapsedSeconds);
        public void Dispose() => runtime.Remove(this);
    }
}

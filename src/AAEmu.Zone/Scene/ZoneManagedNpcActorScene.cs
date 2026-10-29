using System.Numerics;
using System.Diagnostics.CodeAnalysis;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Ai;
using AAEmu.Zone.Hosting;
using AAEmu.Zone.Npcs;
using AAEmu.Zone.Scripting;
using AAEmu.Zone.Units;

namespace AAEmu.Zone.Scene;

public sealed record ZoneManagedNpcActorEvent(ZoneActorHandle Actor, string Source, int Kind, string Name);

/// <summary>
/// Owns the real actor index, goal phases and script runtime for one managed scene. The admission
/// owner supplies fresh AI identity, type, radius, controls and proxy allocation identity; this
/// service never substitutes a World unit ID for an engine entity or writes an actor pose.
/// </summary>
public sealed class ZoneManagedNpcActorScene : IZoneNpcActorScene, IDisposable
{
    private readonly ZoneUnitRegistry _units;
    private readonly ZoneSceneNpcCatalog _catalog;
    private readonly Func<ZoneUnit, Vector3> _position;
    private readonly Func<long> _frameMicroseconds;
    private readonly Func<uint> _random;
    private readonly Action<string> _loadScript;
    private readonly Action<string>? _trace;
    private readonly Dictionary<ZoneActorHandle, ZoneManagedNpcActor> _actors = [];
    private ulong _nextActorAllocationOrderKey;
    private long _lastAiFrameMicroseconds;
    private readonly Queue<ZoneManagedNpcActorEvent> _events = [];
    private readonly ZoneGoalPipeEventDispatcher _pipeEvents = new();
    public ZoneNpcActorRegistry Actors { get; }
    public ZoneAiObjectRegistry Objects { get; } = new();
    public ZoneGoalPipeCatalog GoalPipes { get; }
    public ZoneManagedNpcPointObjects Points { get; }
    public bool IsDisposed { get; private set; }
    public float FrameTimeSeconds { get; }
    public bool GlobalAiUpdateAlways { get; set; }
    public ZoneManagedNpcGameObjectActivationProfile GameObjectActivationProfile { get; set; }
    public Action<ZoneManagedNpcActor, string>? SmartObjectStateConsumer { get; set; }
    public Func<ZoneManagedNpcActor, ZoneGoalDefinition, IZoneGoalOperation>? OperationFactory { get; set; }
    public ulong EventCount { get; private set; }
    public IReadOnlyList<ZoneManagedNpcActorEvent> RecentEvents => _events.ToArray();
    public int Count => _actors.Count;

    public ZoneManagedNpcActorScene(ZoneUnitRegistry units, ZoneSceneNpcCatalog catalog, IZoneLuaRuntime lua,
        ZoneGoalPipeCatalog goalPipes, Func<ZoneUnit, Vector3> position, Func<long> frameMicroseconds,
        Func<uint> random, Action<string> loadScript, TimeSpan framePeriod, Action<string>? trace = null)
    {
        _units = units ?? throw new ArgumentNullException(nameof(units));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        GoalPipes = goalPipes ?? throw new ArgumentNullException(nameof(goalPipes));
        _position = position ?? throw new ArgumentNullException(nameof(position));
        _frameMicroseconds = frameMicroseconds ?? throw new ArgumentNullException(nameof(frameMicroseconds));
        _random = random ?? throw new ArgumentNullException(nameof(random));
        _loadScript = loadScript ?? throw new ArgumentNullException(nameof(loadScript));
        _trace = trace;
        if (framePeriod <= TimeSpan.Zero || !float.IsFinite((float)framePeriod.TotalSeconds))
            throw new ArgumentOutOfRangeException(nameof(framePeriod));
        FrameTimeSeconds = (float)framePeriod.TotalSeconds;
        Points = new(this);
        Actors = new(lua, Objects, this);
    }

    /// <summary>Verified generic CPipeUser reset defaults, not authored physics or a complete Puppet constructor.</summary>
    public static ZoneActorControlState CreatePipeUserControls() => new()
    {
        SignalsEnabled = true, MovementUrgency = .4f, GoalMovementUrgency = .4f,
        DesiredSpeed = 1, DistanceToPathEnd = -1, //31007EAC→31009BBC→31009ABC's actual SOBJECTSTATE constructor.
    };

    /// <summary>
    /// Prepare stable behavior services before the authored entity table exists. No script constructor,
    /// index registration or actor update runs until Adopt succeeds. The caller owns the pending shell.
    /// </summary>
    public ZoneManagedNpcActor Prepare(ZoneActorHandle actor, ZoneAiObjectState aiObject,
        ZoneActorControlState controls, bool isPuppet)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        ArgumentNullException.ThrowIfNull(aiObject); ArgumentNullException.ThrowIfNull(controls);
        var npc = _catalog.Find(actor) ?? throw new InvalidOperationException($"NPC actor {actor} has no exact live catalog owner.");
        if (!_units.Units.TryGetValue(npc.ObjectId, out var unit) || !ReferenceEquals(unit, npc.Unit))
            throw new InvalidOperationException("NPC actor admission requires its exact registered Unit.");
        if (_actors.ContainsKey(actor)) throw new InvalidOperationException("This NPC generation already has a managed actor.");
        var allocationOrderKey = checked(++_nextActorAllocationOrderKey);
        return new ZoneManagedNpcActor(this, actor, npc, aiObject, controls, isPuppet, allocationOrderKey);
    }

    /// <summary>
    /// Transfer a pending shell into the scene, retaining the borrowed entity script. Register its
    /// behavior next, before invoking authored constructors. Failure retires the pending shell.
    /// </summary>
    public void Adopt(ZoneManagedNpcActor managed, short objectType, ulong proxyAllocationKey,
        ZoneLuaReference? entityScript)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        ArgumentNullException.ThrowIfNull(managed);
        if (!ReferenceEquals(managed.Scene, this)) throw new ArgumentException("The actor belongs to a different scene.", nameof(managed));
        if (!managed.IsPending) throw new InvalidOperationException("Only a fresh pending actor can be adopted.");
        var actor = managed.Handle; var npc = managed.Npc;
        if (!ReferenceEquals(_catalog.Find(actor), npc) ||
            !_units.Units.TryGetValue(npc.ObjectId, out var unit) || !ReferenceEquals(unit, npc.Unit))
            throw new InvalidOperationException("NPC actor admission requires its exact live catalog and Unit owners.");
        if (_actors.ContainsKey(actor)) throw new InvalidOperationException("This NPC generation already has a managed actor.");
        var phase = new ZoneActorGoalPhase(GoalPipes, managed.Services, _pipeEvents);
        ZoneNpcActorEntry? entry = null;
        try
        {
            entry = Actors.Adopt(npc, npc.Unit.Snapshot.Name, objectType, managed.AiObject, phase, proxyAllocationKey, entityScript);
            if (IsDisposed || !ReferenceEquals(_catalog.Find(actor), npc) ||
                !_units.Units.TryGetValue(npc.ObjectId, out unit) || !ReferenceEquals(unit, npc.Unit))
                throw new InvalidOperationException("The NPC generation retired during actor admission.");
            managed.Attach(entry);
            _actors.Add(actor, managed);
            managed.AttachPoints(Points.Bind(managed, npc.Unit.Snapshot.Name));
            // CPipeUser constructor 31163974 selects the catalog's real initial pipe.
            phase.Pipes.Select(0, "_first_");
        }
        catch (Exception failure)
        {
            _actors.Remove(actor);
            managed.Points?.Dispose();
            managed.MarkRetired();
            try { if (entry is not null) Actors.Remove(entry); else phase.Dispose(); }
            catch (Exception cleanup) { throw new AggregateException(failure, cleanup); }
            throw;
        }
    }

    public ZoneManagedNpcActor Adopt(ZoneActorHandle actor, ZoneAiObjectState aiObject, short objectType,
        ulong proxyAllocationKey, ZoneActorControlState controls, bool isPuppet, ZoneLuaReference? entityScript)
    {
        var managed = Prepare(actor, aiObject, controls, isPuppet);
        try { Adopt(managed, objectType, proxyAllocationKey, entityScript); return managed; }
        catch { managed.Dispose(); throw; }
    }

    public void RegisterBehavior(ZoneManagedNpcActor actor, ZoneNpcBehavior behavior)
    {
        Require(actor);
        Actors.AiRuntime.Register(actor.Npc, behavior);
        actor.AttachBehavior(behavior);
    }

    public ZoneManagedNpcActor? Find(ZoneActorHandle actor) =>
        !IsDisposed && _actors.TryGetValue(actor, out var current) && IsCurrent(current) ? current : null;

    public ZoneManagedNpcActor? Find(ZoneNpc npc) => _catalog.TryGet(npc) is { } handle &&
        Find(handle) is { } actor && ReferenceEquals(actor.Npc, npc) ? actor : null;

    internal bool IsCurrent(ZoneManagedNpcActor actor) => !IsDisposed &&
        _actors.TryGetValue(actor.Handle, out var current) && ReferenceEquals(current, actor) &&
        ReferenceEquals(_catalog.Find(actor.Handle), actor.Npc) &&
        _units.Units.TryGetValue(actor.Npc.ObjectId, out var unit) && ReferenceEquals(unit, actor.Npc.Unit) &&
        ReferenceEquals(Actors.Find(actor.Npc), actor.Entry);

    internal void Require(ZoneManagedNpcActor actor)
    {
        if (!IsCurrent(actor)) throw new ObjectDisposedException(nameof(ZoneManagedNpcActor), "The exact NPC actor generation has retired.");
    }

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The dictionary owns the actor shell; this method retires its registry entry and target state directly.")]
    public bool Remove(ZoneActorHandle actor)
    {
        if (!_actors.Remove(actor, out var managed)) return false;
        managed.ActorGrid?.Dispose();
        managed.MarkRetired();
        managed.Points?.Dispose();
        foreach (var other in _actors.Values.ToArray()) other.Services.RemoveTarget(managed.AiObject);
        try { Actors.Remove(managed.Entry); }
        finally { managed.Services.ReleaseTargets(); }
        return true;
    }

    internal long FrameMicroseconds() => _frameMicroseconds();
    internal void RetirePoint(ZoneAiObjectState point)
    {
        foreach (var actor in _actors.Values.ToArray()) actor.Services.RemoveTarget(point);
    }
    internal uint NextRandom() => _random();
    internal void LoadScript(string name) => _loadScript(name);
    internal Vector3 Position(ZoneManagedNpcActor actor) { Require(actor); return _position(actor.Npc.Unit); }
    public void RefreshPositions()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        foreach (var actor in _actors.Values.ToArray())
            if (IsCurrent(actor)) RefreshPosition(actor);
    }
    /// <summary>Borrowed proxy owner. Its frame publication owns eye position; entity queries retain their origin.</summary>
    public ZoneManagedNpcAiPose? ProxyPoses { get; set; }
    public ZoneManagedNpcMovementAbility? MovementAbility { get; set; }
    public ZoneManagedNpcNavigation? Navigation { get; set; }
    public ZoneManagedNpcPuppetOutput? PuppetOutput { get; set; }
    public ZoneManagedNpcMovementRequests? MovementRequests { get; set; }
    public ZoneManagedActorGrids? ActorGrids { get; set; }
    /// <summary>3109B198's accepted frame prefix under the managed enabled-server profile.</summary>
    public bool BeginAiFrame(bool initialized, ZoneEngineCVars cvars)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this); ArgumentNullException.ThrowIfNull(cvars);
        var now = _frameMicroseconds(); ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (_lastAiFrameMicroseconds == now) return false;
        _lastAiFrameMicroseconds = now; //Native consumes the timestamp before initialization/CVar guards.
        if (!initialized || cvars.GetInt("ai_SystemUpdate", 1) == 0) return false;
        ActorGrids?.AdvanceCacheSlice(); //Source prepass before scheduled Puppet updates.
        return true;
    }
    public ZoneManagedNpcMovementEligibility? MovementEligibility { get; private set; }

    internal void AttachMovementEligibility(ZoneManagedNpcActorMotion motion)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        ArgumentNullException.ThrowIfNull(motion);
        if (MovementEligibility is not null || _actors.Count != 0)
            throw new InvalidOperationException("NPC movement eligibility attaches once before actor admission.");
        MovementEligibility = new(this, motion);
    }

    internal void RefreshPosition(ZoneManagedNpcActor actor)
    {
        Require(actor);
        actor.AiObject.Position = ProxyPoses?.Find(actor.Handle)?.Last?.Position ?? _position(actor.Npc.Unit);
    }
    internal ZoneManagedNpcActor? ObjectOwner(object target) =>
        _actors.Values.FirstOrDefault(actor => ReferenceEquals(actor.AiObject, target) && IsCurrent(actor));
    internal IReadOnlyList<ZoneManagedNpcActor> CopyActors() => _actors.Values.Where(IsCurrent).ToArray();
    internal ZoneManagedNpcActor? EntryOwner(ZoneNpcActorEntry entry) =>
        _actors.Values.FirstOrDefault(actor => ReferenceEquals(actor.Entry, entry) && IsCurrent(actor));

    internal void Record(ZoneManagedNpcActor actor, string source, int kind, string? name)
    {
        if (EventCount != ulong.MaxValue) EventCount++;
        if (_events.Count == 256) _events.Dequeue();
        _events.Enqueue(new(actor.Handle, source, kind, name ?? string.Empty));
    }

    public IZoneActorEntityView? FindExternalEntity(uint entityId) => null;
    public IZoneActorEntityView? FindExternalEntity(ZoneUnit unit) => null;
    public ZoneGoalPipeController? GetExternalPipeUser(object aiObject) => null;
    public Vector3? GetEntityPosition(ZoneNpcActorEntry actor) => EntryOwner(actor) is { } owner ? Position(owner) : null;
    public bool HasSignalHandler(ZoneNpcActorEntry actor) => EntryOwner(actor)?.Behavior is { IsDisposed: false };
    public void ActorSignalEvent(ZoneNpcActorEntry actor, string name)
    { if (EntryOwner(actor) is { } owner) Record(owner, "actor", 2, name); }
    public void RecordSignal(ZoneNpcActorEntry actor, string name)
    { if (EntryOwner(actor) is { } owner) Record(owner, "recorder", 2, name); }
    public int GetActorAlertness(ZoneNpcActorEntry actor) => EntryOwner(actor)?.Behavior?.Alertness ?? 0;
    public void SetEntityUpdatePolicy(ZoneNpcActorEntry actor, int policy)
    { if (EntryOwner(actor) is { } owner) owner.ApplyGameObjectAiActivationPolicy(policy); }
    public void ActorActivationEvent(ZoneNpcActorEntry actor, int eventId)
    {
        if (EntryOwner(actor) is not { } owner) return;
        if (eventId is not (6 or 7)) throw new NotSupportedException($"Unknown NPC activation event {eventId}.");
        owner.SetActive(eventId == 7);
        Record(owner, "activation", eventId, null);
    }
    public bool StartAuxiliaryBehavior(ZoneNpcActorEntry actor, string name, int kind) =>
        throw Missing("auxiliary behavior execution");
    public bool IsAuxiliaryBehaviorFinished(ZoneNpcActorEntry actor) =>
        throw Missing("auxiliary behavior execution");
    public object? ResolvePipeTarget(ZoneGoalPipeController pipeUser, string name) => throw Missing($"special/named pipe target '{name}'");
    public object? FindAiObject(string name) => throw Missing($"named AI object '{name}'");
    public void Warning(string message) => _trace?.Invoke(message);
    internal static NotSupportedException Missing(string dependency) =>
        new($"Managed NPC actor requires an owned scene service for {dependency}.");

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        List<Exception>? errors = null;
        foreach (var actor in _actors.Keys.ToArray())
            try { Remove(actor); } catch (Exception error) { (errors ??= []).Add(error); }
        try { Points.Dispose(); } catch (Exception error) { (errors ??= []).Add(error); }
        try { Actors.Dispose(); } catch (Exception error) { (errors ??= []).Add(error); }
        if (errors is not null) throw new AggregateException("Managed NPC actors failed to retire.", errors);
    }
}

/// <summary>One exact NPC generation's actor phase and behavior services; the enclosing scene owns physics.</summary>
public sealed partial class ZoneManagedNpcActor : IZoneManagedAiActor, IZoneNpcBehaviorServices
{
    private readonly ZoneManagedNpcActorScene _scene;
    private bool _retired;
    internal ZoneManagedNpcActorServices Services { get; }
    internal ZoneManagedNpcActorScene Scene => _scene;
    internal bool IsPending => !_retired && Entry is null;
    public ZoneActorHandle Handle { get; }
    /// <summary>Managed actor allocation order, independently of entity IDs, proxy keys and native heap addresses.</summary>
    public ulong AiAllocationOrderKey { get; }
    internal ZoneManagedActorGridLease? ActorGrid { get; private set; }
    public ZoneNpc Npc { get; }
    public ZoneAiObjectState AiObject { get; }
    public ZoneActorLookState LookState { get; }
    public ZoneManagedNpcPointLease? Points { get; private set; }
    public ZoneNpcActorEntry Entry { get; private set; } = null!;
    public ZoneActorControlState Controls => Services.Controls;
    public ZoneActorNavigationState Navigation => Services.Navigation;
    public ZoneActorContinuationState Continuation => Services.Continuation;
    public ZoneAiObjectState? Argument => Services.Argument as ZoneAiObjectState;
    public ZoneAiObjectState? AttentionTarget => Services.GetAttentionTarget() as ZoneAiObjectState;
    public IReadOnlyList<ZoneManagedNpcDevaluation> Devaluations => Services.Devaluations;
    public ZoneNpcBehavior? Behavior { get; private set; }
    public bool IsPuppet { get; }
    public int EntityUpdatePolicy { get; internal set; } = 2; //CGameObject305E1D80 flags100=0x23.
    public bool GameplayReady => false;
    public bool HasActor => !_retired && _scene.IsCurrent(this);

    internal ZoneManagedNpcActor(ZoneManagedNpcActorScene scene, ZoneActorHandle handle, ZoneNpc npc,
        ZoneAiObjectState aiObject, ZoneActorControlState controls, bool isPuppet, ulong allocationOrderKey)
    {
        _scene = scene; Handle = handle; Npc = npc; AiObject = aiObject; IsPuppet = isPuppet;
        AiAllocationOrderKey = allocationOrderKey;
        LookState = new(controls);
        Services = new(scene, this, controls);
    }
    internal void Attach(ZoneNpcActorEntry entry) => Entry = entry;
    internal void AttachPoints(ZoneManagedNpcPointLease points) => Points = points;
    internal void AttachBehavior(ZoneNpcBehavior behavior) => Behavior = behavior;
    internal void MarkRetired() { _retired = true; Behavior = null; }
    public void SetActive(bool active)
    {
        _scene.Require(this); AiObject.Enabled = active;
        ActorGrid?.SetEnabled(active);
    }
    internal ZoneManagedActorGridLease? BindActorGrid()
    {
        _scene.Require(this);
        if (_scene.ActorGrids is not { } grids) return null;
        if (ActorGrid is not null) throw new InvalidOperationException("This exact actor already has a grid registration.");
        if (!IsPuppet || Entry.Registration?.ObjectType != 5)
            throw ZoneManagedNpcActorScene.Missing("this actor's actual grid classification and insertion profile");
        //32012370 enables the new Puppet before Entity.v1048 publishes transform.
        //3105A7E8 therefore inserts its constructor-zero actor108.
        return ActorGrid = grids.BindPuppet(Entry.Registration, AiObject, AiAllocationOrderKey, Vector3.Zero, AiObject.Enabled);
    }
    public void SetTarget(ZoneActorHandle? target)
    {
        _scene.Require(this);
        if (target is null) { Services.SetAttentionTarget(null); return; }
        var other = _scene.Find(target.Value) ?? throw ZoneManagedNpcActorScene.Missing("an AI object for the selected target generation");
        Services.SetAttentionTarget(other.AiObject);
    }
    public void SendSignal(ZoneAiSignal signal)
    {
        _scene.Require(this);
        Npc.AiSignals.Submit(signal.Kind, signal.Name, signal.SenderEntityId, signal.Data, signal.Crc);
    }
    public void Update(uint timeMilliseconds, float elapsedSeconds)
    {
        _scene.Require(this);
        if (!float.IsFinite(elapsedSeconds) || elapsedSeconds < 0) throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
        if (!AiObject.Enabled) return;
        _scene.RefreshPosition(this);
        _scene.MovementAbility?.UpdateBodyTurn(this);
        _scene.PuppetOutput?.BeforeGoals(this);
        Entry.Phase.Update();
        if (!_scene.IsCurrent(this)) return;
        _scene.PuppetOutput?.BuildBeforeProxy(this);
        if (!_scene.IsCurrent(this) || _scene.PuppetOutput is { ProxyUpdatesEnabled: false } || !UpdateProxyActivationPolicy()) return;
        //31172DEC runs the goals/output before CAIProxy.v64 (30518020).
        //Its first owned handler phase is v240 ->30518D30. Only this scheduled
        //generation drains; a signal operation itself only submits to the queue.
        if (_scene.IsCurrent(this) && _scene.Actors.HasSignalHandler(Npc)) Npc.AiSignals.Update();
        if (_scene.IsCurrent(this) && _scene.PuppetOutput is { } output)
            (_scene.MovementRequests ?? throw ZoneManagedNpcActorScene.Missing("the actual CAIProxy movement request consumer"))
                .SubmitProxyOutput(this, output.CopyStateForProxy(this));
    }
    public void LoadScript(string filename) { _scene.Require(this); _scene.LoadScript(filename); }
    public void ActorEvent(int kind, string name) { _scene.Require(this); _scene.Record(this, "actor", kind, name); }
    public void RecordEvent(int kind, string name) { _scene.Require(this); _scene.Record(this, "recorder", kind, name); }
    public void ResetPipeUserAfterBehaviorExit() { _scene.Require(this); Services.SetAttentionTarget(null); }
    public void ChangeSmartObjectState(string change)
    {
        _scene.Require(this);
        var consume = _scene.SmartObjectStateConsumer ?? throw ZoneManagedNpcActorScene.Missing($"smart-object state '{change}'");
        consume(this, change);
        _scene.Require(this);
    }
    public void NotifyAlert() => throw ZoneManagedNpcActorScene.Missing("gameplay alert notification");
    public void NotifyActionDone() => throw ZoneManagedNpcActorScene.Missing("gameplay action-done notification");
    public void ScriptError(string callback, ZoneLuaException error) => _scene.Warning($"NPC {Handle} {callback}: {error.Message}");
    public void Dispose()
    {
        if (_retired) return;
        if (Entry is null) { MarkRetired(); Services.ReleaseTargets(); }
        else _scene.Remove(Handle);
    }
}

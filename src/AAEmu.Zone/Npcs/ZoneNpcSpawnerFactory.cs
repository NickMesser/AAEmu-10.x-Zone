using System.Numerics;
using AAEmu.Zone.Content;
using AAEmu.Zone.Protocol.Messages;
using AAEmu.Zone.Protocol.Units;
using AAEmu.Zone.WorldState;

namespace AAEmu.Zone.Npcs;

public enum ZoneNpcSpawnerAdmissionDiagnostic
{
    MissingContent, MissingMembers, MissingTriangles, Filtered,
    // Explicit replacement admission boundaries, not native row filtering.
    ZeroInstanceId, DuplicateInstanceId, MissingPointPlacement, MissingSortPoint, UnsupportedNameEncoding,
    ReinitializedIdentityChange
}

/// <summary>Resolved target types in the native tower/phase and game-schedule traversal order.</summary>
public sealed class ZoneNpcSpawnerPostLoadRelations
{
    public static ZoneNpcSpawnerPostLoadRelations Empty { get; } = new([], []);
    public IReadOnlyList<uint> TowerSpawnerTypes { get; }
    public IReadOnlyList<uint> GameScheduleSpawnerTypes { get; }
    public ZoneNpcSpawnerPostLoadRelations(IEnumerable<uint> towerSpawnerTypes, IEnumerable<uint> gameScheduleSpawnerTypes)
    {
        ArgumentNullException.ThrowIfNull(towerSpawnerTypes); ArgumentNullException.ThrowIfNull(gameScheduleSpawnerTypes);
        TowerSpawnerTypes = Array.AsReadOnly(towerSpawnerTypes.ToArray());
        GameScheduleSpawnerTypes = Array.AsReadOnly(gameScheduleSpawnerTypes.ToArray());
    }
}

/// <summary>Actual engine/content leaves; physical attempts use the concrete point, area and group implementations.</summary>
public interface IZoneNpcSpawnerFactoryServices : IZoneNpcPointSpawnServices, IZoneNpcAreaSpawnServices,
    IZoneNpcSpawnProximityServices, IZoneNpcSpawnAreaCollisionServices, IZoneNpcGroupSpawnServices,
    IZoneNpcSpawnerEventServices
{
    float FrameSeconds { get; }
    float Hour { get; }
    uint TimeMilliseconds { get; }
    // Preserve the actual native fields without inferring names for engine/world modes.
    bool EnvironmentFlag122 { get; }
    bool EnvironmentFlag150 { get; }
    int WorldValue8C { get; }
    bool EditorNpcsDisabled { get; } // npc_editor_no_npc, read only when environment+150 is set.
    bool IsSpawnerTypeFiltered(uint spawnerType); //39C537F0: membership in the loaded filter set.
    bool? FindActivationOverride(uint spawnerType);
    Vector3 GetCameraPosition(); //3933D680 samples the camera for each comparison.
    void RemoveSpawnedNpcs(ZoneNpcSpawnerRegistration owner, bool preserveAlertNpcs, Func<bool> current);
    void DrawSpawner(ZoneNpcSpawnerRegistration owner, ZoneNpcSpawnerPlacementRow source);
    void ReportAdmission(ZoneNpcSpawnerPlacementRow source, ZoneNpcSpawnerAdmissionDiagnostic diagnostic);
}

/// <summary>One authored physical owner and its concrete runtime components.</summary>
public sealed class ZoneNpcSpawnerRuntime : IZoneNpcSpawnerPhase
{
    private readonly Func<bool> _current;
    public ZoneNpcSpawnerPlacementRow Source { get; internal set; }
    public ZoneNpcSpawnerRegistration Registration { get; }
    public ZoneNpcSpawnerState Scheduler => Registration.Scheduler;
    public ZoneNpcSpawnPlacement Placement { get; internal set; }
    public ZoneNpcSpawnCollision Collision { get; internal set; }
    public ZoneNpcGroupSpawner? Group { get; internal set; }
    public ZoneNpcPointSpawner? Point { get; internal set; }
    public ZoneNpcAreaSpawner? Area { get; internal set; }
    public ZoneNpcSpawnerEvents Events { get; }
    internal ZoneNpcSpawnerRuntime(ZoneNpcSpawnerPlacementRow source, ZoneNpcSpawnerRegistration registration,
        ZoneNpcSpawnPlacement placement, ZoneNpcSpawnCollision collision, ZoneNpcGroupSpawner? group,
        ZoneNpcPointSpawner? point, ZoneNpcAreaSpawner? area, ZoneNpcSpawnerEvents events, Func<bool> current)
    {
        Source = source; Registration = registration; Placement = placement; Collision = collision;
        Group = group; Point = point; Area = area; Events = events; _current = current;
    }
    public void Update() { if (_current()) Scheduler.Update(); }
    public void DrawDebug() { if (_current()) Scheduler.DrawDebug(); }
}

/// <summary>
///3934B0E0/3934AD80 file-load sequencing over already parsed authored parameters. This factory owns
/// only its exact registrations and manager phases; the scene, registry and manager remain caller-owned.
/// Native duplicate physical IDs are explicitly rejected until acknowledgment attribution supports them.
/// Whole-file reload destroys spawner state without removing live NPCs (3934A8D0/39350310), unlike
/// same-owner reinitialization3934A780. Retired actor bindings cannot update replacement counters.
/// Reload/disposal callback retirement is a managed ownership policy. Persistent restore is a separate
/// ZoneLoaded operation and is never triggered here.
/// </summary>
public sealed class ZoneNpcSpawnerFactory : IDisposable
{
    private sealed class Generation
    {
        internal readonly List<ZoneNpcSpawnerRuntime> Spawners = [];
        internal readonly List<ZoneNpcSpawnerRegistration> TypeOwners = [];
    }
    private sealed class RetiredLoadException : Exception { }
    private readonly ZoneNpcSpawnerRegistry _registry;
    private readonly ZoneNpcManager _manager;
    private readonly IZoneNpcSpawnerFactoryServices _services;
    private Generation _generation = new();
    public bool IsDisposed { get; private set; }
    public IReadOnlyList<ZoneNpcSpawnerRuntime> Spawners => _generation.Spawners.ToArray();
    public ZoneGameScheduleController GameSchedules { get; }
    public ZoneTowerDefController TowerDefs { get; }
    /// <summary>Exact scene-owner liveness, independent of the native editor-disabled load gate.</summary>
    public bool HasLiveOwners => !IsDisposed && !_manager.IsDisposed && !_registry.IsDisposed &&
        !_registry.IsShuttingDown && !_registry.Npcs.IsStopped && ReferenceEquals(_registry.Npcs.Spawners, _registry);

    public ZoneNpcSpawnerFactory(ZoneNpcSpawnerRegistry registry, ZoneNpcManager manager, IZoneNpcSpawnerFactoryServices services)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        ObjectDisposedException.ThrowIf(registry.IsDisposed, registry); ObjectDisposedException.ThrowIf(manager.IsDisposed, manager);
        if (!manager.BelongsTo(registry.Npcs, registry.Groups))
            throw new ArgumentException("The manager must update this registry's NPC scene and group owners.", nameof(manager));
        if (!ReferenceEquals(registry.Npcs.Spawners, registry) || registry.IsShuttingDown || registry.Npcs.IsStopped)
            throw new ArgumentException("The factory requires the scene's attached accepting spawner registry.", nameof(registry));
        GameSchedules = new(this, registry);
        TowerDefs = new(CountTypeOwners);
    }

    private bool Current(Generation generation) => HasLiveOwners && ReferenceEquals(_generation, generation);

    internal bool IsCurrentGeneration(object generation) => generation is Generation value && Current(value);
    internal IReadOnlyList<ZoneNpcSpawnerRegistration> GetTypeOwners(object generation, uint type) =>
        generation is Generation value && Current(value)
            ? value.TypeOwners.Where(owner => owner.SpawnerType == type && _registry.IsCurrent(owner)).ToArray() : [];

    private int CountTypeOwners(uint type) =>
        Current(_generation) ? _generation.TypeOwners.Count(owner => owner.SpawnerType == type && _registry.IsCurrent(owner)) : 0;

    /// <summary>Returns false when the editor gate or callback retirement stops this load.</summary>
    public bool Reload(ZoneNpcSpawnerPlacementContent content, ZoneNpcSpawnerPostLoadRelations relations)
    {
        ArgumentNullException.ThrowIfNull(relations);
        return ReloadCore(content, _ => relations, null);
    }

    public bool Reload(ZoneNpcSpawnerPlacementContent content, ZoneNpcSpawnerControllerContent controllers)
    {
        ArgumentNullException.ThrowIfNull(controllers);
        return ReloadCore(content, controllers.ProjectPostLoadRelations, controllers);
    }

    private bool ReloadCore(ZoneNpcSpawnerPlacementContent content, Func<IEnumerable<uint>, ZoneNpcSpawnerPostLoadRelations> project,
        ZoneNpcSpawnerControllerContent? controllers)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        ArgumentNullException.ThrowIfNull(content);
        var previous = _generation;
        var generation = _generation = new();
        GameSchedules.BeginReload(controllers);
        TowerDefs.BeginReload(controllers);
        Retire(previous);
        if (!Current(generation)) return false;
        try
        {
            var editor = _services.EnvironmentFlag150;
            if (!Current(generation)) return false;
            if (editor)
            {
                var disabled = _services.EditorNpcsDisabled;
                if (!Current(generation) || disabled) return false;
            }
            foreach (var row in content.Rows)
            {
                if (!Current(generation)) return false;
                Admit(generation, row);
            }
            if (!Current(generation)) return false;
            ApplyPostLoad(generation, project(generation.Spawners.Where(runtime => _registry.IsAccepting(runtime.Registration)).Select(runtime => runtime.Source.SpawnerType)));
            if (!Current(generation)) return false;
            GameSchedules.CompletePostLoad(generation);
            TowerDefs.CompletePostLoad();
            var flag122 = _services.EnvironmentFlag122;
            if (!Current(generation)) return false;
            if (!flag122) Sort(generation);
            return Current(generation);
        }
        catch (RetiredLoadException) { return false; }
        catch
        {
            // Unwind only this load, including when a callback has already installed its replacement.
            Retire(generation);
            throw;
        }
        finally
        {
            // A manager-only retirement does not itself close registry event admission. Retire
            // this load's exact owners as well, without touching a nested replacement generation.
            if (!Current(generation)) Retire(generation);
        }
    }

    private void Admit(Generation generation, ZoneNpcSpawnerPlacementRow row)
    {
        void Reject(ZoneNpcSpawnerAdmissionDiagnostic reason) => _services.ReportAdmission(row, reason);
        if (!_registry.Content.Spawners.TryGetValue(row.SpawnerType, out var content))
        { Reject(ZoneNpcSpawnerAdmissionDiagnostic.MissingContent); return; }
        if (content.Members.Count == 0) { Reject(ZoneNpcSpawnerAdmissionDiagnostic.MissingMembers); return; }
        var area = row.AreaType == ZoneNpcSpawnAreaType.Area;
        if (area && row.Placement.Triangles.Count == 0) { Reject(ZoneNpcSpawnerAdmissionDiagnostic.MissingTriangles); return; }
        var adapter = new SchedulerServices(this, generation, row);
        var scheduler = new ZoneNpcSpawnerState(row.SpawnerType, _registry.Npcs, adapter) { Active = content.Definition.ActivationState };
        ZoneNpcSpawnerRegistration? registration = null;
        var transferred = false;
        try
        {
            if (scheduler.Active)
            {
                var flag122 = _services.EnvironmentFlag122;
                if (!Current(generation)) return;
                scheduler.QueueAll(flag122 ? 100u : 1000u, () => Current(generation));
                if (!Current(generation)) return;
            }
            //3933ED40 runs after the initial queue and before the content filter.
            var worldValue = _services.WorldValue8C;
            if (!Current(generation)) return;
            var persistent = worldValue == 2 && content.Definition.SaveInstance && content.Definition.MaximumPopulation == 1 &&
                !content.Members.Any(member => member.Kind == ZoneNpcSpawnerMemberKind.NpcGroup);
            var filtered = _services.IsSpawnerTypeFiltered(row.SpawnerType);
            if (!Current(generation)) return;
            if (filtered) { Reject(ZoneNpcSpawnerAdmissionDiagnostic.Filtered); return; }
            var active = _services.FindActivationOverride(row.SpawnerType);
            if (!Current(generation)) return;
            if (active == false) scheduler.Active = false; // Deliberately retain the initial queue.

            if (row.SpawnerId == 0) { Reject(ZoneNpcSpawnerAdmissionDiagnostic.ZeroInstanceId); return; }
            if (_registry.Find(row.SpawnerId) is not null) { Reject(ZoneNpcSpawnerAdmissionDiagnostic.DuplicateInstanceId); return; }
            if (!area && row.Placement.Points.Count == 0) { Reject(ZoneNpcSpawnerAdmissionDiagnostic.MissingPointPlacement); return; }
            if (!row.HasUtf8AreaNames || row.InitialBehavior.Paths.Any(path => !path.HasUtf8Name) || row.InitialBehavior.Anchors.Any(anchor => !anchor.HasUtf8Name))
            { Reject(ZoneNpcSpawnerAdmissionDiagnostic.UnsupportedNameEncoding); return; }
            var behavior = new ZoneNpcInitialSpawnContext(
                row.InitialBehavior.Paths.Select(path => new ZoneNpcNamedSpawnPath(path.Name, path.PointNumber)),
                row.InitialBehavior.Anchors.Select(anchor => new ZoneNpcSpawnAnchor(anchor.Name, anchor.AnchorType, anchor.Position)));
            registration = _registry.Register(row.SpawnerId, scheduler, row.Placement.Points.Count, row.Placement.Triangles.Count,
                area, persistent, initialBehavior: behavior);
            adapter.Owner = registration;
            bool Owned() => Current(generation) && _registry.IsAccepting(registration);
            var data = new ZoneNpcSpawnPlacementData(row.Placement.Points.Select(point => new ZoneNpcSpawnPoint(point.Position, point.ZRotation)),
                row.Placement.Triangles.Select(triangle => new ZoneNpcSpawnTriangle(triangle.V1, triangle.V2, triangle.V3, triangle.AreaRate)));
            var placement = new ZoneNpcSpawnPlacement(registration, data, _services);
            var collision = new ZoneNpcSpawnCollision(_registry.Registry, registration, _registry.NpcContent, _services);
            var group = content.Members.Any(member => member.Group is not null) ? new ZoneNpcGroupSpawner(_registry, registration, _services) : null;
            var point = area ? null : new ZoneNpcPointSpawner(_registry, placement, collision, _services, group);
            var areaSpawner = area ? new ZoneNpcAreaSpawner(_registry, placement, new(_registry.Registry, registration, _services), collision, _services, group) : null;
            var events = new ZoneNpcSpawnerEvents(_registry, registration, adapter.Attempt, _services);
            _registry.AttachEvents(registration, events);
            var runtime = new ZoneNpcSpawnerRuntime(row, registration, placement, collision, group, point, areaSpawner, events, Owned);
            adapter.Runtime = runtime;
            _manager.RegisterSpawner(runtime);
            generation.Spawners.Add(runtime);
            generation.TypeOwners.Add(registration);
            transferred = true;
        }
        finally
        {
            if (!transferred)
            {
                if (registration is not null) _registry.Remove(registration);
                scheduler.Dispose();
            }
        }
    }

    /// <summary>
    ///3934A780 removes live members, clears pending spawns and member/triangle storage, then invokes
    /// Init on the same physical owner. Scheduler clocks, deferred commands, event/controller flags,
    /// total population, preferred template and persistent/validation counters survive. Population
    /// changes only through actual removals before storage retirement. Identity changes are rejected
    /// explicitly: no native caller proves how manager indexes should be repaired for that operation.
    /// </summary>
    public bool Reinitialize(ZoneNpcSpawnerRuntime runtime, ZoneNpcSpawnerPlacementRow row)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        ArgumentNullException.ThrowIfNull(runtime); ArgumentNullException.ThrowIfNull(row);
        var generation = _generation;
        var owner = runtime.Registration;
        bool Current() => CurrentGeneration() && _registry.IsCurrent(owner) && !owner.Scheduler.IsDisposed;
        bool CurrentGeneration() => this.Current(generation) && generation.Spawners.Contains(runtime);
        if (!Current()) return false;
        if (owner.Reinitializing) throw new InvalidOperationException("The same spawner cannot reinitialize recursively.");
        if (row.SpawnerId != owner.Id || row.SpawnerType != owner.SpawnerType)
        { _services.ReportAdmission(row, ZoneNpcSpawnerAdmissionDiagnostic.ReinitializedIdentityChange); return false; }
        var area = row.AreaType == ZoneNpcSpawnAreaType.Area;
        if (!area && row.Placement.Points.Count == 0)
        { _services.ReportAdmission(row, ZoneNpcSpawnerAdmissionDiagnostic.MissingPointPlacement); return false; }
        if (!row.HasUtf8AreaNames || row.InitialBehavior.Paths.Any(path => !path.HasUtf8Name) || row.InitialBehavior.Anchors.Any(anchor => !anchor.HasUtf8Name))
        { _services.ReportAdmission(row, ZoneNpcSpawnerAdmissionDiagnostic.UnsupportedNameEncoding); return false; }
        var previousContentGeneration = owner.ContentGeneration;
        owner.Reinitializing = true;
        try
        {
            _services.RemoveSpawnedNpcs(owner, false, Current);
            if (!Current()) return false;
            var content = _registry.Content.Spawners.GetValueOrDefault(row.SpawnerType);
            var behavior = new ZoneNpcInitialSpawnContext(
                row.InitialBehavior.Paths.Select(path => new ZoneNpcNamedSpawnPath(path.Name, path.PointNumber)),
                row.InitialBehavior.Anchors.Select(anchor => new ZoneNpcSpawnAnchor(anchor.Name, anchor.AnchorType, anchor.Position)));
            if (!_registry.ReplaceContent(owner, content, row.SpawnerType, row.Placement.Points.Count,
                row.Placement.Triangles.Count, area, behavior) || !Current()) return false;
            runtime.Source = row;
            if (!owner.ContentInitialized)
            {
                _services.ReportAdmission(row, content is null ? ZoneNpcSpawnerAdmissionDiagnostic.MissingContent :
                    content.Members.Count == 0 ? ZoneNpcSpawnerAdmissionDiagnostic.MissingMembers : ZoneNpcSpawnerAdmissionDiagnostic.MissingTriangles);
                return false;
            }
            // Constructors contain no engine callbacks. Publish a complete physical generation before
            // native Init samples its environment flag and clock; the old components stay retired.
            owner.Reinitializing = false;
            var data = new ZoneNpcSpawnPlacementData(row.Placement.Points.Select(point => new ZoneNpcSpawnPoint(point.Position, point.ZRotation)),
                row.Placement.Triangles.Select(triangle => new ZoneNpcSpawnTriangle(triangle.V1, triangle.V2, triangle.V3, triangle.AreaRate)));
            runtime.Placement = new(owner, data, _services);
            runtime.Collision = new(_registry.Registry, owner, _registry.NpcContent, _services);
            runtime.Group = content!.Members.Any(member => member.Group is not null) ? new(_registry, owner, _services) : null;
            runtime.Point = area ? null : new(_registry, runtime.Placement, runtime.Collision, _services, runtime.Group);
            runtime.Area = area ? new(_registry, runtime.Placement, new(_registry.Registry, owner, _services), runtime.Collision, _services, runtime.Group) : null;
            owner.Reinitializing = true;
            var contentGeneration = owner.ContentGeneration;
            bool StillCurrent() => Current() && ReferenceEquals(contentGeneration, owner.ContentGeneration);
            owner.Scheduler.Active = content.Definition.ActivationState;
            if (owner.Scheduler.Active)
            {
                var flag122 = _services.EnvironmentFlag122;
                if (!StillCurrent()) return false;
                owner.Scheduler.QueueAll(flag122 ? 100u : 1000u, StillCurrent);
                if (!StillCurrent()) return false;
            }
            var world = _services.WorldValue8C;
            if (!StillCurrent()) return false;
            //3933ED40 only sets this byte; a later ineligible Init never clears it.
            if (world == 2 && content.Definition.SaveInstance && content.Definition.MaximumPopulation == 1 &&
                !content.Members.Any(member => member.Kind == ZoneNpcSpawnerMemberKind.NpcGroup)) owner.Persistent = true;
            return true;
        }
        catch
        {
            // A callback exception after old buffers were released cannot expose an incomplete
            // set of physical components. Keep the exact owner available for a later reinit.
            if (Current() && !ReferenceEquals(previousContentGeneration, owner.ContentGeneration)) owner.ContentInitialized = false;
            throw;
        }
        finally { owner.Reinitializing = false; }
    }

    private void ApplyPostLoad(Generation generation, ZoneNpcSpawnerPostLoadRelations relations)
    {
        foreach (var type in relations.TowerSpawnerTypes)
            foreach (var runtime in generation.Spawners.ToArray())
            {
                if (!Current(generation)) return;
                if (runtime.Source.SpawnerType != type || !_registry.IsAccepting(runtime.Registration)) continue;
                runtime.Scheduler.Active = false;
                runtime.Events.MarkTowerDefenseControlled();
            }
        foreach (var type in relations.GameScheduleSpawnerTypes)
            foreach (var runtime in generation.Spawners.ToArray())
            {
                if (!Current(generation)) return;
                if (runtime.Source.SpawnerType != type || !_registry.IsAccepting(runtime.Registration)) continue;
                runtime.Events.Handle(new(runtime.Registration.Id, 6, new(2), new(ZoneUnitKind.Character), 0, false, false, 0));
                if (!Current(generation)) return;
                runtime.Events.MarkGameScheduleControlled();
            }
    }

    private void Sort(Generation generation)
    {
        if (generation.Spawners.Count < 2) return;
        //3933D680 dereferences the first point without a bounds guard. Reject this unsafe input
        // explicitly when sorting needs it; an area-only owner remains valid when no comparison runs.
        foreach (var runtime in generation.Spawners.ToArray())
        {
            if (runtime.Source.Placement.Points.Count != 0) continue;
            _manager.RemoveSpawner(runtime);
            generation.Spawners.Remove(runtime);
            _registry.Remove(runtime.Registration);
            if (!Current(generation)) return;
            _services.ReportAdmission(runtime.Source, ZoneNpcSpawnerAdmissionDiagnostic.MissingSortPoint);
            if (!Current(generation)) return;
        }
        var sorted = generation.Spawners.ToList();
        ZoneNpcSpawnerOrdering.Sort(sorted, (left, right) =>
        {
            if (!Current(generation)) throw new RetiredLoadException();
            var camera = _services.GetCameraPosition();
            if (!Current(generation)) throw new RetiredLoadException();
            var a = left.Source.Placement.Points[0].Position;
            var b = right.Source.Placement.Points[0].Position;
            var ax = a.X - camera.X; var ay = a.Y - camera.Y;
            var bx = b.X - camera.X; var by = b.Y - camera.Y;
            return ax * ax + ay * ay < by * by + bx * bx;
        });
        if (!Current(generation)) return;
        foreach (var runtime in generation.Spawners) _manager.RemoveSpawner(runtime);
        generation.Spawners.Clear();
        generation.Spawners.AddRange(sorted.Where(runtime => _registry.IsAccepting(runtime.Registration)));
        foreach (var runtime in generation.Spawners) _manager.RegisterSpawner(runtime);
    }

    private void Retire(Generation generation)
    {
        GameSchedules.RetireGeneration(generation);
        TowerDefs.Retire();
        var owners = generation.Spawners.ToArray();
        generation.Spawners.Clear();
        generation.TypeOwners.Clear();
        List<Exception>? failures = null;
        foreach (var runtime in owners)
        {
            _manager.RemoveSpawner(runtime);
            try { _registry.Remove(runtime.Registration); }
            catch (Exception failure) { (failures ??= []).Add(failure); }
        }
        if (failures is not null) throw new AggregateException("Spawner retirement failed after all exact owners were visited.", failures);
    }

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        GameSchedules.Dispose();
        Retire(_generation);
    }

    private sealed class SchedulerServices(ZoneNpcSpawnerFactory factory, Generation generation, ZoneNpcSpawnerPlacementRow source) : IZoneNpcSpawnerServices
    {
        internal ZoneNpcSpawnerRegistration? Owner;
        internal ZoneNpcSpawnerRuntime? Runtime;
        internal ZoneNpcSpawnAttempt Attempt(bool force, ZoneNpcSpawnOverrides? overrides) => Runtime?.Point is { } point
            ? point.Attempt(force, overrides) : Runtime?.Area?.Attempt(force, overrides) ?? new(false, 1000);
        private bool Current() => factory.Current(generation) && Owner is not null && factory._registry.IsAccepting(Owner);
        public float FrameSeconds => factory._services.FrameSeconds;
        public float Hour => factory._services.Hour;
        public uint TimeMilliseconds => factory._services.TimeMilliseconds;
        public ZoneNpcSpawnerSchedule? FindSchedule(uint type) => factory._registry.Content.Spawners.GetValueOrDefault(type)?.Schedule;
        public ZoneNpcSpawnAttempt AttemptSpawn(ZoneNpcSpawnerState owner, bool belowMinimum, uint delayAfterSpawn) =>
            Current() && ReferenceEquals(Owner!.Scheduler, owner) ? Attempt(belowMinimum, null) : new(false, 1000);
        public void RemoveSpawnedNpcs(ZoneNpcSpawnerState owner, bool preserveAlertNpcs)
        {
            if (Current() && ReferenceEquals(Owner!.Scheduler, owner)) factory._services.RemoveSpawnedNpcs(Owner, preserveAlertNpcs, Current);
        }
        public void RunDeferredAiCommand(ZoneNpc npc, string text, uint parameter, Func<bool> current) =>
            factory._registry.RunDeferredAiCommand(npc, text, parameter, () => Current() && current() && Current());
        public void DrawDebug(ZoneNpcSpawnerState owner)
        {
            if (Current() && ReferenceEquals(Owner!.Scheduler, owner)) factory._services.DrawSpawner(Owner, Runtime?.Source ?? source);
        }
    }
}

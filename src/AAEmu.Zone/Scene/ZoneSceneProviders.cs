using System.Numerics;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Ai;
using AAEmu.Zone.Combat;
using AAEmu.Zone.Content;
using AAEmu.Zone.Hosting;
using AAEmu.Zone.Native.Ai;
using AAEmu.Zone.Native.Entities;
using AAEmu.Zone.Npcs;
using AAEmu.Zone.Physics;
using AAEmu.Zone.Protocol;
using AAEmu.Zone.Protocol.Messages;
using AAEmu.Zone.Protocol.Units;
using AAEmu.Zone.Scripting;
using AAEmu.Zone.Units;
using AAEmu.Zone.WorldLink;

namespace AAEmu.Zone.Scene;

// ---------------------------------------------------------------------------
// Agent 5's registered production provider file (IMPLEMENTATION_OWNERSHIP.md
// section 6): every mandatory registry/NPC-world/update/movement/spawner/tick
// provider the scene composition needs, over REUSED subsystems. Leaf policy
// (IZoneNpcWorld.cs:10-12, IZoneSceneLoadingServices doc, plan section 2): real
// backing or a NAMED failure/once-per-session TODO report — no successful
// no-op, no permissive default that hides a missing subsystem, and never a
// throw that can kill the ordinary tick from a leaf whose native counterpart
// is conditional or whose gate is closed by construction (each such leaf cites
// the exact gate below).
// ---------------------------------------------------------------------------

/// <summary>
/// Once-per-session named partial-leaf sink feeding the host's readiness table
/// (PLAN C4): keys deduplicate; lines print after level completion. This is the
/// report channel for leaves with no native owner yet — reported, never silent.
/// </summary>
public sealed class ZoneSceneReadinessNotes
{
    private readonly object _gate = new();
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);
    private readonly List<string> _lines = [];

    public void AddOnce(string key, string line)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(line);
        lock (_gate)
            if (_keys.Add(key))
                _lines.Add(line);
    }

    public IReadOnlyList<string> Lines
    {
        get { lock (_gate) return _lines.ToArray(); }
    }
}

/// <summary>
/// The scene's accumulated native float-seconds clock for the signal runtime
/// (IZoneNpcSignalRuntime.cs:9-10: native current-time float seconds, NEVER a conversion from
/// the wrapping millisecond timer). Advanced exactly once per frame by
/// <see cref="ZoneSceneTickServices.UpdateHeartbeat"/> — the host engine phase that owns
/// advancement (ZoneTimeOfDayState.cs:8) — and read by <see cref="ZoneSceneSignalRuntime"/>.
/// </summary>
public sealed class ZoneSceneScriptClock
{
    public float TimeSeconds { get; private set; }

    public void Advance(float frameSeconds)
    {
        if (float.IsFinite(frameSeconds) && frameSeconds > 0f) TimeSeconds += frameSeconds;
    }
}

/// <summary>
/// REAL 0x39332510 send gate: ZWHeartbeat when the float delta on the simulation clock since the last send is &gt;= 1.0
/// (last starts at 0 like the native static dword_3A607B54). Pure timing; the caller queues the frame.
/// </summary>
public sealed class ZoneHeartbeatEmitter(bool oceanSimulateOnZone)
{
    private float _lastSeconds;

    public bool TryCreate(float nowSeconds, out ZoneFrame frame)
    {
        if (nowSeconds - _lastSeconds < 1.0f) { frame = default; return false; }
        _lastSeconds = nowSeconds;
        frame = new ZoneHeartbeat(oceanSimulateOnZone).ToFrame();
        return true;
    }
}

/// <summary>
/// One scene generation's NPC actor-handle catalog — the IZoneNativeAiActorView backing the
/// native AI provider's admission gate (ZoneNativeAiProvider.cs:11-22): generation per live
/// owner, +1 on re-entry, GetKind throws for unknown/retired handles with the exact rejection
/// discipline of IZoneActorLifecycle.Create/Retire (:54-63). Single-threaded scene use only
/// (all calls happen inside scene dispatch).
/// </summary>
public sealed class ZoneSceneNpcCatalog : IZoneNativeAiActorView
{
    private sealed class Entry
    {
        public uint Generation = 1;
        public bool Retired;
    }

    private readonly Dictionary<uint, Entry> _entries = new();

    /// <summary>One generation per Initialize of a live owner; a retired identity re-enters as generation + 1.</summary>
    public ZoneActorHandle Register(ZoneNpc npc)
    {
        ArgumentNullException.ThrowIfNull(npc);
        if (!_entries.TryGetValue(npc.ObjectId, out var entry))
            _entries[npc.ObjectId] = entry = new Entry();
        else if (entry.Retired)
        {
            entry.Generation = unchecked(entry.Generation + 1);
            entry.Retired = false;
        }
        return new ZoneActorHandle(npc.ObjectId, entry.Generation);
    }

    public void Retire(uint objectId)
    {
        if (_entries.TryGetValue(objectId, out var entry)) entry.Retired = true;
    }

    public ZoneActorHandle? TryGet(uint objectId) =>
        _entries.TryGetValue(objectId, out var entry) && !entry.Retired
            ? new ZoneActorHandle(objectId, entry.Generation) : null;

    public ZoneActorKind GetKind(ZoneActorHandle actor)
    {
        if (_entries.TryGetValue(actor.UnitObjectId, out var entry) && !entry.Retired && entry.Generation == actor.Generation)
            return ZoneActorKind.Npc;
        throw new InvalidOperationException(
            $"Scene NPC catalog refuses handle {actor}: unknown or retired generations have no admitted kind " +
            "(IZoneNativeAiActorView.cs:14-17; rejection discipline IZoneActorLifecycle.cs:58-67).");
    }
}

// ---------------------------------------------------------------------------
// IZoneNpcWorld — the production NPC world (62 leaves, IZoneNpcWorld.cs:14-65).
// ---------------------------------------------------------------------------

/// <summary>
/// Production <see cref="IZoneNpcWorld"/> composing the REAL scene subsystems: the unit
/// registry (spatial/units), the NPC simulation's aggro tables and spawn state, the physics
/// backend ground ray, the scene model-position source, and the AI handle catalog + the one
/// dependency-gated native AI provider (ledger A4). Decisions bound here:
/// <list type="bullet">
/// <item>NO managed actor adoption: the AiRuntime mirrors ZoneNpcActorRegistry's not-adopted
/// results exactly (:114-127, :138-149, :186-187, :195-202) because managed adoption would
/// violate the native-driven exclusivity invariant (ZoneNativeAiProvider.cs:35-40).</item>
/// <item><see cref="HasAiActor"/> = false is the FACTUAL state (no entity, no AI actor — the
/// 8-row native dependency table is unresolved), and it is the exact gate
/// ZoneNpcSimulation.Update :356 applies before any per-NPC step, so the actor-subsystem leaves
/// below are unreachable in the tick path by construction and refuse BY NAME if ever called.
/// Their refusals and per-NPC AI bind refusals surface through readiness, never silently.</item>
/// <item>Return/housing/territory/abuser leaves follow the interface contract (:7-9): named
/// NotSupportedException with the native address + TODO — no permissive defaults.</item>
/// </list>
/// </summary>
public sealed class ZoneSceneNpcWorld : IZoneNpcWorld
{
    private const float CancelTerrainToleranceMeters = 4f;
    private readonly ZoneUnitRegistry _registry;
    private readonly ZoneStartupState _startup;
    private readonly ZoneNpcContent _npcContent;
    private readonly Func<ZoneUnit, Vector3> _modelPosition;
    private readonly Func<IZonePhysicsBackend?> _backend;
    private readonly ZoneSceneNpcCatalog _catalog;
    private readonly ZoneNativeAiProvider? _ai;
    private readonly ZoneSceneScriptClock _scriptClock;
    private readonly ZoneSceneReadinessNotes _notes;
    private readonly Action<string>? _trace;
    private readonly Dictionary<uint, ZoneSceneActivationServices> _activationServices = new();
    private readonly List<(uint ObjectId, string Message)> _aiBindRefusals = [];
    private readonly ZoneSceneSignalRuntime _signalRuntime;
    private readonly ZoneSceneAiRuntime _aiRuntime;
    private bool _aiRefusalSummaryTraced;

    public ZoneSceneNpcWorld(ZoneUnitRegistry registry, ZoneStartupState startup, ZoneNpcContent npcContent,
        Func<ZoneUnit, Vector3> modelPosition, Func<IZonePhysicsBackend?> backend, ZoneSceneNpcCatalog catalog,
        ZoneNativeAiProvider? ai, ZoneSceneScriptClock scriptClock, TimeSpan framePeriod,
        ZoneSceneReadinessNotes notes, Action<string>? trace)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(startup);
        ArgumentNullException.ThrowIfNull(npcContent);
        ArgumentNullException.ThrowIfNull(modelPosition);
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(scriptClock);
        ArgumentNullException.ThrowIfNull(notes);
        if (framePeriod <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(framePeriod));
        _registry = registry;
        _startup = startup;
        _npcContent = npcContent;
        _modelPosition = modelPosition;
        _backend = backend;
        _catalog = catalog;
        _ai = ai;
        _scriptClock = scriptClock;
        _notes = notes;
        _trace = trace;
        _signalRuntime = new(this, scriptClock);
        _aiRuntime = new(this, framePeriod);
    }

    /// <summary>Per-NPC native AI bind refusals (objectId, exact blocked message) for the readiness table.</summary>
    public IReadOnlyList<(uint ObjectId, string Message)> AiBindRefusals => _aiBindRefusals;
    public int AiBindRefusalCount => _aiBindRefusals.Count;
    public ZoneSceneNpcCatalog Catalog => _catalog;

    private ZoneNpcSimulation? Scene => _registry.Npcs;
    private IZonePhysicsBackend RequireBackend() =>
        _backend() ?? throw new NotSupportedException(
            "The NPC world requires the scene physics backend (constructed by the host after level completion).");

    public uint TimeMilliseconds => _startup.WorldGameClock.GetTime();

    /// <summary>
    /// Native 39345000 pre-publication validation (called at ZoneNpcSimulation.Create :77,
    /// AFTER that path already resolved spawner, template and model content). What remains is
    /// the pose/scale admission the native entity path validates before any allocation — the
    /// exact predicate family of ZoneNativeEntityServices.CreateInitialCharacter :56-59 /
    /// ZonePlayerActorFactory.ValidateRequestPose — so malformed World data fails HERE by name
    /// instead of producing an unphysical owner.
    /// </summary>
    public void ValidateCreation(NpcStateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var unit = snapshot.Unit;
        var pose = ZoneUnitPose.FromSnapshot(unit);
        var rotation = pose.Rotation;
        var scale = unit.Scale;
        if (!Finite(new Vector3(rotation.X, rotation.Y, rotation.Z)) || !float.IsFinite(rotation.W) ||
            rotation.LengthSquared() <= 0 || !float.IsFinite(scale) || scale <= 0)
            throw new ArgumentException(
                $"NPC creation rejected for template {unit.Identity.TemplateId}: pose/scale admission failed " +
                $"(rotation length {rotation.LengthSquared()}, scale {scale}) — the same predicate family as " +
                "ZoneNativeEntityServices.CreateInitialCharacter :56-59 before any allocation.", nameof(snapshot));
    }

    /// <summary>
    /// 39345000/39348440 scene half: register the owner generation in the NPC actor catalog,
    /// then attempt the native AI bind through the one per-scene dependency-gated provider.
    /// Every NotSupportedException is RECORDED per NPC {objectId, message} and counted for the
    /// readiness table (ledger A4) — a refused bind never half-publishes (admission order of
    /// ZoneNativeAiProvider.CreateNpc :137-151). Creator hostility/inherited-target behavior has
    /// no owner until the native actor exists (IZoneNpcWorld.cs:18) — TODO(creator-hostility),
    /// traced once. Spatial admission is NOT this leaf's: the simulation publishes it at :135.
    /// </summary>
    public void Initialize(ZoneNpc npc, ZoneUnit? creator)
    {
        ArgumentNullException.ThrowIfNull(npc);
        var handle = _catalog.Register(npc);
        try
        {
            if (_ai is null)
                throw new NotSupportedException(
                    "No native AI provider is wired for this scene (managed-AI scenes stay behind TODO(managed-ai), ZoneSceneBackendSelection.cs:25-27).");
            _ai.CreateNpc(handle);
        }
        catch (NotSupportedException error)
        {
            _aiBindRefusals.Add((npc.ObjectId, error.Message));
            if (_aiBindRefusals.Count <= 5)
                _trace?.Invoke($"NPC AI bind refused unit={npc.ObjectId} npcId={npc.Id} gen={handle.Generation}: {error.Message}");
        }
        if (creator is not null)
            _notes.AddOnce("creator-hostility",
                "NPC creator hostility/inherited target: no adopted actor to attribute the inherited state to TODO(creator-hostility)");
        if (_aiBindRefusals.Count == 1 && !_aiRefusalSummaryTraced)
        {
            _aiRefusalSummaryTraced = true;
            _trace?.Invoke("NPC AI bind refusals are counted per owner and printed at readiness (dependency-gated native provider).");
        }
    }

    /// <summary>Retire the exact catalog generation (idempotent, tolerates partial initialization per :17-18); the provider's RemoveNpc is idempotent for unbound handles (:154-161).</summary>
    public void Remove(ZoneNpc npc)
    {
        ArgumentNullException.ThrowIfNull(npc);
        if (_catalog.TryGet(npc.ObjectId) is { } handle)
        {
            try { _ai?.RemoveNpc(handle); }
            catch (ObjectDisposedException) { /* scene teardown already retired the provider; catalog still retires below */ }
        }
        _catalog.Retire(npc.ObjectId);
        _activationServices.Remove(npc.ObjectId);
    }

    /// <summary>Owner death clears its threat table for real (ZoneAggroTable.Clear :148-160); reverse memberships unwind through IZoneAggroWorld.EntryRemoved (ZoneNpcSimulation :547-552). The native AI death EVENT has no owner until TODO(native-ai) — documented, gated with the rest of the actor path.</summary>
    public void Died(ZoneNpc npc)
    {
        ArgumentNullException.ThrowIfNull(npc);
        npc.Aggro.Clear();
    }

    /// <summary>3925FA70's second half updates the unit's AI actor IF ONE EXISTS. None exists in this scene (native AI gated, TODO(native-ai)); the faction write itself is the registry's (ZoneUnitRegistry.SetFaction :390-394) and already happened before this leaf — so the conditional native half correctly has nothing to do. Traced once for visibility.</summary>
    public void UpdateFaction(ZoneUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        _notes.AddOnce("npc-faction-ai",
            "NPC faction propagation: unit half real (registry SetFaction); AI-actor half skipped — no AI actor adopted TODO(native-ai)");
    }

    public Vector3 GetPosition(ZoneUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return _modelPosition(unit);
    }

    /// <summary>The owner's spawn anchor (native encounter-origin source at IZoneAggroWorld :348/:393 of ZoneAggroTable): with no script/actor to localize a fight origin, the authored spawn position is the real anchor in this scene.</summary>
    public Vector3 GetEncounterOrigin(ZoneNpc npc)
    {
        ArgumentNullException.ThrowIfNull(npc);
        return GetSpawnPosition(npc);
    }

    /// <summary>The actor script's spawnPos normally can differ from the initial wire position — the script/actor half is gated (TODO(native-ai)), so the initial wire pose IS the spawn position here (model-position source reads the immutable spawn snapshot).</summary>
    public Vector3 GetSpawnPosition(ZoneNpc npc)
    {
        ArgumentNullException.ThrowIfNull(npc);
        return _modelPosition(npc.Unit);
    }

    /// <summary>
    /// Named refusal: relocating an owner's spawn anchor requires the script/actor spawnPos
    /// write, which has no owner while the native actor is gated (TODO(npc-relocate)). In the
    /// tick path this is unreachable — the only table-driven caller (RelocateOwner from
    /// ProcessReturn) sits behind ZoneNpcSimulation.Update :356's HasAiActor gate, and with no
    /// motion source every owner stays at its spawn anchor (distance ≈ 0), so return-completion
    /// never fires. A packet-driven RefreshAggro on a dragged owner would surface this exact
    /// named gate instead of a fabricated anchor write.
    /// </summary>
    public void Relocate(ZoneNpc npc, Vector3 position)
    {
        throw new NotSupportedException(
            $"TODO(npc-relocate): native spawn-anchor relocation has no owner for unit {npc.ObjectId} at ({position.X:0.##},{position.Y:0.##},{position.Z:0.##}) — " +
            "the actor/script spawnPos write is gated with TODO(native-ai) (IZoneNpcWorld.cs:29).");
    }

    /// <summary>393211E0: AI navigation service ABSENT or nonzero state enables ordinary return checks — the AI navigation service IS absent in this scene, so the enabled state is the factual native reading, not a fabricated default (IZoneAggroWorld.cs:20-21).</summary>
    public bool ReturnChecksEnabled => true;

    /// <summary>Named refusal per IZoneNpcWorld.cs:10-12 + :28: the housing manager has no owner (readiness ownerless leaf, PLAN A3). Zone 129 ships no housing area for this key, but the check itself is never replaced with a fabricated false.</summary>
    public bool IsInHousingArea(Vector3 position) =>
        throw new NotSupportedException(
            $"TODO(housing-area): native housing-area containment for ({position.X:0.#},{position.Y:0.#}) has no housing-manager owner in this tree (IZoneNpcWorld.cs:31).");

    /// <summary>Named refusal per IZoneNpcWorld.cs:32: territory/nav-area containment content exists (ZoneNavigationAreaContent) but no wired scene owner resolves it yet — TODO(territory-check), first name in the readiness partial table.</summary>
    public bool IsOutsideTerritory(ZoneNpc npc, Vector3 position) =>
        throw new NotSupportedException(
            $"TODO(territory-check): native territory containment for npcId {npc.Id} at ({position.X:0.#},{position.Y:0.#}) has no wired navigation owner (IZoneNpcWorld.cs:32).");

    /// <summary>Named refusal per IZoneNpcWorld.cs:33: navigation-type/forbidden-area/path-query abuse detection requires the unwired AI navigation services (3931D890) — TODO(navigation-services).</summary>
    public bool IsNavigationAbuser(ZoneUnit target) =>
        throw new NotSupportedException(
            $"TODO(navigation-services): native navigation-abuse checks for unit {target.Snapshot.ObjectId} require the AI navigation service (3931D890, IZoneNpcWorld.cs:33).");

    /// <summary>
    /// REAL: the scene's downward terrain ray through the physics backend (backing decision,
    /// casefile: terrain height = IZonePhysicsBackend.TryGround). The ray starts well above the
    /// engine-local window; a miss (outside loaded cells) returns the caller's own Z and is
    /// counted once — the managed heightfield itself is activation-private, so a ray miss is
    /// reported, not disguised.
    /// </summary>
    public float GetTerrainHeight(Vector3 position)
    {
        var backend = RequireBackend();
        if (backend.TryGround(new Vector3(position.X, position.Y, position.Z + 4096f), 8192f, out var ground))
            return ground.Position.Y;
        _notes.AddOnce("terrain-ray-miss",
            $"terrain height: at least one ray miss returned the caller's own Z (outside loaded cells) TODO(terrain-height-fallback)");
        return position.Z;
    }

    /// <summary>Factual state: every NPC in this scene was admitted awake by a spawner; sleep/dormancy is native AI-actor state which no owner has adopted (feed of IZoneAggroWorld.FindTarget's sleeping modifier, ZoneNpcSimulation :523-524). TODO(native-ai) when adoption lands.</summary>
    public bool IsSleeping(ZoneUnit unit)
    {
        _notes.AddOnce("npc-sleep-state",
            "NPC sleep state: all spawner-admitted owners run awake; dormancy is native AI-actor state TODO(native-ai)");
        return false;
    }

    public bool CanAcquire(ZoneNpc npc, ZoneUnit target)
    {
        ArgumentNullException.ThrowIfNull(npc);
        ArgumentNullException.ThrowIfNull(target);
        return AcquireCore(npc, target);
    }

    /// <summary>393204B0's group half: at least one NPC in the owner's aggro-share group must pass 39261710 — implemented as the real any-member pass over the resolved share group (ungrouped owners reduce to their own check).</summary>
    public bool CanRetain(ZoneNpc npc, ZoneUnit target)
    {
        ArgumentNullException.ThrowIfNull(npc);
        ArgumentNullException.ThrowIfNull(target);
        foreach (var id in GetSharedAggroNpcIds(npc))
        {
            var member = id == npc.Id ? npc : Scene?.Npcs.GetValueOrDefault(id);
            if (member is not null && AcquireCore(member, target)) return true;
        }
        return false;
    }

    /// <summary>393204B0's terrain-dependent cancellation half: cancel an entry whose target sits below the scene's terrain surface beyond the named tolerance. Entry timestamps stay the table's own state (:33) — the owner-side rule is terrain only, with the tolerance exposed as a named constant instead of a hidden default.</summary>
    public bool ShouldCancel(ZoneNpc npc, ZoneAggroEntry entry, ZoneUnit target, uint now)
    {
        ArgumentNullException.ThrowIfNull(npc);
        ArgumentNullException.ThrowIfNull(target);
        _ = entry; _ = now; // entry timestamps are consumed by the table itself, not this terrain rule
        var position = _modelPosition(target);
        return position.Z < GetTerrainHeight(position) - CancelTerrainToleranceMeters;
    }

    /// <summary>Named refusal per IZoneNpcWorld.cs:39: abuser-category decisions belong to the native actor service (one-second throttle, IZoneAggroWorld.cs:40-41) — no actor exists; RefreshAbusers sits behind the HasAiActor gate (:356) in the tick path. TODO(abuser-marking).</summary>
    public void CheckAbusers(ZoneNpc npc, ZoneUnit target) =>
        throw new NotSupportedException(
            $"TODO(abuser-marking): native abuser-category evaluation for npcId {npc.Id}/unit {target.Snapshot.ObjectId} requires the actor service (IZoneNpcWorld.cs:39).");

    /// <summary>
    /// Aggro rule 2 (Share) composition over LIVE data — no throw where the data is reachable
    /// (casefile decision): ungrouped or Share-unresolved owners return [npc.Id] (the documented
    /// ungrouped result, :37-38); a resolved LINK rule names its unwired content pass; the
    /// shared live group is the same (type, registration id) spawn group of ZoneNpcGroups
    /// (:132/:170 identity), traversed in unsigned NPC-ID order like the native update walk
    /// (ZoneNpcSimulation :354).
    /// </summary>
    public IReadOnlyList<uint> GetSharedAggroNpcIds(ZoneNpc npc)
    {
        ArgumentNullException.ThrowIfNull(npc);
        if (!npc.SharesAggro) return new[] { npc.Id };
        var spawn = npc.Spawn.Group; // NpcSpawnGroup is a struct; Type 0 = ungrouped/default
        if (spawn.Type == 0) return new[] { npc.Id };
        if (!_npcContent.Groups.TryGetValue(spawn.Type, out var groupContent))
        {
            _notes.AddOnce($"aggro-group-unresolved:{spawn.Type}",
                $"aggro share group type {spawn.Type} has no NpcGroupDefinition row — ungrouped result returned TODO(aggro-group-content)");
            return new[] { npc.Id };
        }
        if (groupContent.Definition.AggroRule == NpcGroupAggroRule.Link)
            throw new NotSupportedException(
                $"TODO(aggro-link-rule): native aggro rule Link (rule 1) for group {groupContent.Definition.Id} requires the ZoneNpcContent.AggroLinks traversal, which has no recovered shape yet (IZoneNpcWorld.cs:45-46).");
        if (groupContent.Definition.AggroRule != NpcGroupAggroRule.Share) return new[] { npc.Id };
        var scene = Scene;
        if (scene is null) return new[] { npc.Id };
        var members = new List<uint> { npc.Id };
        foreach (var member in scene.Npcs.Values)
        {
            if (member.Id == npc.Id) continue;
            var other = member.Spawn.Group;
            if (other.Type == spawn.Type && other.Id == spawn.Id) members.Add(member.Id);
        }
        members.Sort();
        return members;
    }

    /// <summary>
    /// 39321540 composition: each aggro-share group member's CURRENT target, re-checked through
    /// this scene's own retain rule (range/eligibility) — the slave-attachment exclusion is the
    /// caller's (ZoneNpcSimulation.Route :473-476 resolves the owning character first).
    /// TODO(additional-aggro-range): native's explicit distance gate is approximated by the
    /// retain rule until the native range source is recovered.
    /// </summary>
    public IReadOnlyList<uint> GetAdditionalAggroTargetIds(ZoneNpc npc, ZoneUnit character)
    {
        ArgumentNullException.ThrowIfNull(npc);
        ArgumentNullException.ThrowIfNull(character);
        var scene = Scene;
        if (scene is null) return Array.Empty<uint>();
        var result = new HashSet<uint>();
        foreach (var id in GetSharedAggroNpcIds(npc))
        {
            var member = scene.Npcs.GetValueOrDefault(id);
            if (member is null) continue;
            var targetId = member.Aggro.TargetId;
            if (targetId == 0 || targetId == character.Snapshot.ObjectId || targetId == npc.Aggro.TargetId) continue;
            if (_registry.Units.TryGetValue(targetId, out var target) && CanRetain(member, target))
                result.Add(targetId);
        }
        return result.ToArray();
    }

    /// <summary>Named refusal per IZoneNpcWorld.cs:45-47: 39321C80's content link rules + AI state have no recovered composition (combat-skill path — unreachable without a skill executor, which stays behind TODO(npc-signal-skills)).</summary>
    public IReadOnlyList<uint> GetLinkedAggroNpcIds(ZoneUnit skillTarget, ZoneUnit? source, ZoneUnit unitInCharge) =>
        throw new NotSupportedException(
            $"TODO(npc-linked-aggro): native linked-aggro resolution (39321C80) for target {skillTarget.Snapshot.ObjectId} requires content link rules + AI state that have no owner (IZoneNpcWorld.cs:46-47).");

    /// <summary>Region-observer/AI notification half of a selection-flag change: no region observer registry and no AI actor exist to notify (native-conditional work) — recorded once for the readiness table instead of a fabricated side effect. The reverse-membership/group half is the simulation's (ZoneNpcSimulation :553-565).</summary>
    public void AggroChanged(ZoneNpc npc, uint targetId)
    {
        _notes.AddOnce("aggro-notify",
            "aggro selection notifications: reverse membership real; region-observer/AI notification skipped (no observers, TODO(native-ai))");
    }

    /// <summary>Same native-conditional notification as <see cref="AggroChanged"/> for target swaps; group synchronization is the simulation's own pass (ZoneNpcSimulation :566-572).</summary>
    public void AggroTargetChanged(ZoneNpc npc, uint previousTargetId)
    {
        _ = previousTargetId;
        _notes.AddOnce("aggro-notify",
            "aggro selection notifications: reverse membership real; region-observer/AI notification skipped (no observers, TODO(native-ai))");
    }

    /// <summary>
    /// 393482D0's gate, FACTUAL: neither the entity nor the AI actor exists (native dependency
    /// table unresolved — ZoneNativeAiProvider.ReportDependencies, 8 rows), and it is the exact
    /// condition ZoneNpcSimulation.Update :356 / UpdateAiSignals :395 apply before every
    /// per-NPC step. The per-NPC bind refusals recorded at Initialize are this leaf's evidence.
    /// </summary>
    public bool HasAiActor(ZoneNpc npc)
    {
        _ = npc;
        return false;
    }

    /// <summary>Named refusal (39345770): unreachable while <see cref="HasAiActor"/> is false — ZoneNpcSimulation.Update :356 skips the whole step sequence, including AcquireTargets, before this leaf can run. TODO(native-ai) wires it with the actor.</summary>
    public void AcquireAggroTargets(ZoneNpc npc) =>
        throw new NotSupportedException(
            $"TODO(native-ai): native aggro acquisition (39345770) for npcId {npc.Id} requires an AI actor that no owner has adopted (gate ZoneNpcSimulation.cs:358).");

    /// <summary>REAL per-owner activation inputs (sight cvars 120/250 are the native defaults recorded at ZoneNpcActivationState.cs:15-16), cached per NPC generation and invalidated by <see cref="Remove"/>.</summary>
    public IZoneNpcActivationServices GetActivationServices(ZoneNpc npc)
    {
        ArgumentNullException.ThrowIfNull(npc);
        if (!_activationServices.TryGetValue(npc.ObjectId, out var services))
            _activationServices[npc.ObjectId] = services = new ZoneSceneActivationServices(this, npc, _registry);
        return services;
    }

    public IZoneNpcSignalRuntime SignalRuntime => _signalRuntime;
    public IZoneNpcAiRuntime AiRuntime => _aiRuntime;

    /// <summary>99264D0/9CE6AA0's physical-proxy half: the owner's body does not exist (TODO(ctor-bodies) — the backend adopts factory bodies only), so the physics query cannot run and unit centers are FORBIDDEN as a fallback (:54-56). Named refusal instead of a fabricated center distance; unreachable while the owner loop is gated.</summary>
    public float GetInteractionDistance(ZoneUnit owner, ZoneUnit target, float stopDistance) =>
        throw new NotSupportedException(
            $"TODO(ctor-bodies): native interaction-distance query (99264D0) for owner {owner.Snapshot.ObjectId} has no physical proxy to query — unit centers are a forbidden fallback (IZoneNpcWorld.cs:57-59).");

    /// <summary>Named refusal (3933C270): unreachable behind the HasAiActor gate — follow-target motion needs the AI actor's target state. TODO(native-ai).</summary>
    public void UpdateFollowTarget(ZoneNpc npc) =>
        throw new NotSupportedException(
            $"TODO(native-ai): follow-target update (3933C270) for npcId {npc.Id} requires an adopted AI actor (gate ZoneNpcSimulation.cs:358).");

    /// <summary>Named refusal (39347060): unreachable behind the HasAiActor gate — crowd-buff state belongs to the native actor. TODO(native-ai).</summary>
    public void UpdateCrowdBuff(ZoneNpc npc) =>
        throw new NotSupportedException(
            $"TODO(native-ai): crowd-buff update (39347060) for npcId {npc.Id} requires an adopted AI actor (gate ZoneNpcSimulation.cs:358).");

    /// <summary>Named refusal (3933D0E0): unreachable behind the HasAiActor gate — position checks read the actor proxy that does not exist. TODO(native-ai).</summary>
    public void CheckPosition(ZoneNpc npc) =>
        throw new NotSupportedException(
            $"TODO(native-ai): position check (3933D0E0) for npcId {npc.Id} requires an adopted AI actor (gate ZoneNpcSimulation.cs:358).");

    /// <summary>
    /// The movement-replication gate, decided FALSE and reported (readiness partial leaf): the
    /// replication SOURCE — an authoritative NPC motion state — does not exist while the native
    /// actor/physics bodies are gated (TODO(ctor-bodies)/TODO(native-ai)), and the NPC pose in
    /// this scene is its immutable spawn pose, which the wire already carries. Flipping this
    /// true without that source would replicate fabricated motion; ZoneNpcSimulation.Update :385
    /// reads this flag before visiting ReplicateMovement.
    /// </summary>
    public bool MovementReplicationEnabled
    {
        get
        {
            _notes.AddOnce("npc-movement-replication",
                "NPC movement replication: gate OFF — no authoritative NPC motion source exists (TODO(native-ai)/TODO(ctor-bodies)); spawn poses come from the wire");
            return false;
        }
    }

    /// <summary>Named refusal, unreachable while <see cref="MovementReplicationEnabled"/> is false (gate read at ZoneNpcSimulation.Update :385 before this leaf). TODO(native-ai)/TODO(ctor-bodies) flip both together.</summary>
    public void ReplicateMovement(ZoneNpc npc) =>
        throw new NotSupportedException(
            $"TODO(native-ai): NPC movement replication (393405B0) for npcId {npc.Id} has no motion source while MovementReplicationEnabled is false (gate ZoneNpcSimulation.cs:387).");

    /// <summary>39261710 composition over reachable state: hostile faction, player-controlled kinds only, own aggro-share group excluded — the stealth/dead flags are the caller's earlier gates (ZoneNpcSimulation :536).</summary>
    private bool AcquireCore(ZoneNpc npc, ZoneUnit target)
    {
        if (ReferenceEquals(target, npc.Unit)) return false;
        var kind = target.Snapshot.Identity.Kind;
        if (kind is not (ZoneUnitKind.Character or ZoneUnitKind.Slave or ZoneUnitKind.Mate)) return false;
        if (target.FactionId == npc.Unit.FactionId) return false;
        if (kind == ZoneUnitKind.Npc) return false; // unreachable today; guarded for completeness of 39261710's NPC exclusions
        if (target.Snapshot.Identity.Kind is ZoneUnitKind.Slave or ZoneUnitKind.Mate &&
            target.Snapshot.Identity.OwnerId != 0 &&
            _registry.Units.Values.Any(owner => owner.Snapshot.Identity.Kind == ZoneUnitKind.Character &&
                owner.Snapshot.Identity.PersistentId == target.Snapshot.Identity.OwnerId &&
                owner.FactionId == npc.Unit.FactionId))
            return false; // attached to a same-faction character
        return true;
    }

    private static bool Finite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    // -----------------------------------------------------------------------
    // Per-owner activation inputs (IZoneNpcActivationServices, ZoneNpcActivationState.cs:12-21).
    // -----------------------------------------------------------------------
    private sealed class ZoneSceneActivationServices : IZoneNpcActivationServices
    {
        private readonly ZoneSceneNpcWorld _world;
        private readonly ZoneNpc _npc;
        private readonly ZoneUnitRegistry _registry;

        internal ZoneSceneActivationServices(ZoneSceneNpcWorld world, ZoneNpc npc, ZoneUnitRegistry registry)
        {
            _world = world; _npc = npc; _registry = registry;
        }

        public bool HasUnit =>
            _registry.Units.TryGetValue(_npc.ObjectId, out var current) && ReferenceEquals(current, _npc.Unit);
        public float PlayerSightRange => 120f;   // player_unit_sight_range native default, ZoneNpcActivationState.cs:15
        public float PlayerSightRangeBig => 250f; // player_unit_sight_range_big native default, :13
        public Vector3 GetPosition() => _world.GetPosition(_npc.Unit);
        /// <summary>Null = no native entity adopted for this generation (documented missing-entity path, :15 / :78-80).</summary>
        public IZoneNpcActivationEntity? GetEntity() => null;
        /// <summary>This dedicated process runs server type 2 (ZoneSceneLoadingServices.ServerType) — the native dedicated flag reads true.</summary>
        public bool IsDedicated => true;
        /// <summary>Unreachable while GetEntity() is null — the flag is only consulted after an AI actor exists (:107-111); the env slot has no accessor, TODO(env-slot).</summary>
        public bool GlobalAiUpdateAlways => false;
    }

    // -----------------------------------------------------------------------
    // Signal runtime (IZoneNpcSignalRuntime) and AI runtime (IZoneNpcAiRuntime : IZoneNpcActorRuntime).
    // -----------------------------------------------------------------------
    private sealed class ZoneSceneSignalRuntime : IZoneNpcSignalRuntime
    {
        private readonly ZoneSceneNpcWorld _world;
        private readonly ZoneSceneScriptClock _clock;

        internal ZoneSceneSignalRuntime(ZoneSceneNpcWorld world, ZoneSceneScriptClock clock)
        {
            _world = world; _clock = clock;
        }

        /// <summary>Native float-seconds domain, advanced once per frame by the tick heartbeat — never a ms conversion (IZoneNpcSignalRuntime.cs:9-10).</summary>
        public float TimeSeconds => _clock.TimeSeconds;
        /// <summary>No configured debug NPC in this scene (native cvar owner absent, TODO(npc-signal-debug)).</summary>
        public uint DebugNpcObjectId => 0;
        public string GetDebugUnitName(ZoneUnit unit) => unit.Snapshot.Name;
        /// <summary>Named refusal: no skill/requirement executor exists (native 39361350). Unreachable — the drain is gated by HasSignalHandler == false (ZoneNpcAiSignals.cs:95/:133).</summary>
        public bool CheckRequirement(ZoneUnitRequirement requirement, ZoneUnit subject, ZoneUnit npc) =>
            throw new NotSupportedException(
                $"TODO(npc-signal-skills): native requirement evaluation (39361350) has no skill executor in this tree (IZoneNpcSignalRuntime.cs:13-14).");
        /// <summary>Named refusal, same gate as CheckRequirement (native 39361350 family, ZoneNpcSignals.cs:89-91).</summary>
        public bool CanUseSkill(ZoneNpc npc, uint skillId) =>
            throw new NotSupportedException(
                $"TODO(npc-signal-skills): native skill eligibility for skill {skillId} has no skill executor (ZoneNpcSignals.cs:47-52).");
        /// <summary>Named refusal, same gate (native 39361590 family).</summary>
        public bool UseSkill(ZoneNpc npc, uint skillId) =>
            throw new NotSupportedException(
                $"TODO(npc-signal-skills): native skill execution for skill {skillId} has no skill executor (ZoneNpcSignals.cs:89-91).");
        /// <summary>The scene RNG: Random.Shared, the SAME documented source the spawner services use (one source per scene, stable call order inside one dispatch).</summary>
        public uint NextRandomUInt32() => unchecked((uint)Random.Shared.NextInt64());
    }

    private sealed class ZoneSceneAiRuntime : IZoneNpcAiRuntime
    {
        private readonly ZoneSceneNpcWorld _world;
        private readonly float _frameSeconds;

        internal ZoneSceneAiRuntime(ZoneSceneNpcWorld world, TimeSpan framePeriod)
        {
            _world = world;
            _frameSeconds = (float)framePeriod.TotalSeconds;
        }

        /// <summary>Native 393401B0 is SILENT when entity/script state is missing (IZoneNpcAiRuntime doc :6-7) — no actor/script exists, so the documented silent path is the native behavior itself.</summary>
        public void CaptureTalkPosition(ZoneNpc npc) { }
        /// <summary>Named refusal (CryAction 30511450/305108E0): unreachable — HasSignalHandler == false gates the pending-constructor drain (ZoneNpcAiSignals.cs:95/:133). TODO(native-ai).</summary>
        public void RunPendingConstructors(ZoneNpc npc) =>
            throw new NotSupportedException(
                $"TODO(native-ai): pending behavior constructors (305108E0/30511450) require CryAction behavior dispatch + an adopted actor (IZoneNpcAiRuntime.cs:11-15).");
        /// <summary>Named refusal, same gate — ACTION_DONE dispatch needs the actor behavior stack. TODO(native-ai).</summary>
        public void DispatchScriptSignal(ZoneNpc npc, ZoneAiSignal signal) =>
            throw new NotSupportedException(
                $"TODO(native-ai): script signal ACTION_DONE dispatch (30511450) has no adopted actor behavior stack (IZoneNpcAiRuntime.cs:13-15).");
        /// <summary>Native's documented missing-state silence (IZoneNpcAiRuntime doc :14): releasing script state that was never retained is exactly the native no-side-effect path.</summary>
        public void ReleaseScriptState(ZoneNpc npc) { }

        // IZoneNpcActorRuntime — EXACT not-adopted results mirrored from ZoneNpcActorRegistry
        // (:114-127, :138-149, :186-187, :195-202): false/null/0 for a generation no actor registry adopted.
        public bool IsEnabled(ZoneNpc npc) => false;              // AI object +68 absent
        public bool CanReceiveSignals(ZoneNpc npc) => false;      // +29 absent — ZoneNpcActorRegistry :121 mirror
        public bool HasSignalHandler(ZoneNpc npc) => false;       // gates every pending drain
        public bool HasPipeUser(ZoneNpc npc) => false;
        public uint? GetEntityId(ZoneUnit unit) => null;          // no native entity adopted
        public uint GetUnitObjectId(uint entityId) => 0;          // no entity→unit mapping exists
        public ZoneLuaReference? GetEntityScript(uint entityId) => null;
        public Vector3? GetEntityPosition(uint entityId) => null;
        public void SignalReceived(ZoneNpc npc, string name) { }  // native silent for a missing actor
        /// <summary>The REAL scene clock: this frame's period in seconds (same domain as FrameSeconds of the tick services).</summary>
        public float FrameTimeSeconds => _frameSeconds;
        /// <summary>false = nothing started: no auxiliary behavior can run without an adopted actor — not a fabricated success (the finished query below answers factually for the empty set).</summary>
        public bool StartAuxiliaryBehavior(ZoneNpc npc, string name, int kind) => false;
        public bool IsAuxiliaryBehaviorFinished(ZoneNpc npc) => true;
    }
}

// ---------------------------------------------------------------------------
// Per-unit update services (IZoneUnitUpdateServices, ZoneUnitUpdateScheduler.cs:7-24).
// ---------------------------------------------------------------------------

/// <summary>
/// Production <see cref="IZoneUnitUpdateServices"/> for the scene's rolling unit scheduler.
/// Backing decisions (casefile): one clock sample = the World-aligned scene clock; allocation
/// keys = World object IDs (documented explicit ordering, scheduler :12-13); spatial leaves call
/// the registry for real; buff leaf runs the REAL ZoneUnitRegistry.AdvanceBuffTiming pass and
/// names the missing landing-physics half once; ship leaves are guarded by unit kind — the five
/// ordinary kinds of this Zone's stream return (their native base pass is empty), any other kind
/// (ship/shipyard/transfer) hits the named TODO(ship-units) refusal and is reported in the
/// readiness table as structurally unreachable for Zone 129.
/// </summary>
public sealed class ZoneSceneUnitUpdateServices : IZoneUnitUpdateServices
{
    private readonly ZoneUnitRegistry _registry;
    private readonly ZoneStartupState _startup;
    private readonly ZoneSceneReadinessNotes _notes;
    private bool _modelGateTraced;

    public ZoneSceneUnitUpdateServices(ZoneUnitRegistry registry, ZoneStartupState startup, ZoneSceneReadinessNotes notes)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(startup);
        ArgumentNullException.ThrowIfNull(notes);
        _registry = registry;
        _startup = startup;
        _notes = notes;
    }

    /// <summary>The single scene clock (ZoneWorldGameClock.GetTime — World-aligned CTimer domain, :60-72), sampled per scheduler call exactly like the native clock read.</summary>
    public uint TimeMilliseconds => _startup.WorldGameClock.GetTime();

    /// <summary>Stable, unique, nonzero live key = the World-issued object ID (Characters and NPCs never share one inside a registry — scheduler :67 rejects 0/duplicates, and World admission guarantees uniqueness before ZWUnitState).</summary>
    public ulong GetAllocationKey(ZoneUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return unit.Snapshot.ObjectId;
    }

    /// <summary>3925A3D0's per-frame scale advance has no owner: content admission writes targets (ZoneUnitRegistry.Prepare :174-177) but nothing steps them — once-reported at readiness TODO(scale-transition), never a fabricated advance.</summary>
    public void UpdateScaleTransition(ZoneUnit unit)
    {
        _ = unit;
        _notes.AddOnce("scale-transition",
            "scale transitions: targets written at content admission; per-frame advance (3925A3D0) has no owner TODO(scale-transition)");
    }

    /// <summary>Gate OFF while actor/model publication is unresolved (P1 class publication + GameObject/ActorSystem — ZonePlayerActorFactory dependency table rows 1-2): native skips the model pass when the flag says so; the gate decision is reported once, not silently swallowed.</summary>
    public bool UpdateUnitModels
    {
        get
        {
            if (!_modelGateTraced)
            {
                _modelGateTraced = true;
                _notes.AddOnce("actor-publication",
                    "unit model updates: gate OFF — no actor publication owner (P1/ActorSystem, TODO(actor-publication)); models stay at their admitted state");
            }
            return false;
        }
    }

    /// <summary>Named refusal, unreachable while <see cref="UpdateUnitModels"/> is false (scheduler :138-144 skips the call). TODO(actor-publication) flips gate and body together.</summary>
    public void UpdateUnitModel(ZoneUnit unit) =>
        throw new NotSupportedException(
            $"TODO(actor-publication): unit model publication for unit {unit.Snapshot.ObjectId} requires the unowned GameObject/ActorSystem composition (ZonePlayerActorFactory.ActorPublicationDependency).");

    /// <summary>39BF7A90 skill timers have no skill-runtime owner in this tree — once-reported at readiness TODO(skill-runtime); the scheduler still receives its real elapsed delta, so no timing information is invented here.</summary>
    public void UpdateSkills(ZoneUnit unit, uint elapsedMilliseconds)
    {
        _ = unit; _ = elapsedMilliseconds;
        _notes.AddOnce("skill-runtime",
            "skill updates: per-unit skill timers (39BF7A90) have no skill-runtime owner TODO(skill-runtime)");
    }

    /// <summary>
    /// REAL half + named half: advances the Zone buff timing pass through
    /// ZoneUnitRegistry.AdvanceBuffTiming (committed stat invalidations, :481-495), while the
    /// landing-removal evaluation (AdvanceBuffs :503 — requires IZoneBuffLandingPhysics + a wire
    /// sender, neither available) is once-reported TODO(buff-landing-physics) instead of being
    /// fabricated or skipped silently.
    /// </summary>
    public void UpdateBuffs(ZoneUnit unit, uint elapsedMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(unit);
        _ = _registry.AdvanceBuffTiming(unit.Snapshot.ObjectId, elapsedMilliseconds);
        _notes.AddOnce("buff-landing-physics",
            "buff landing removals: timing pass real (AdvanceBuffTiming); landing evaluation + wire send deferred — IZoneBuffLandingPhysics/sender unavailable TODO(buff-landing-physics)");
    }

    /// <summary>
    /// The exact scheduler-semantics pattern (casefile + ZoneUnitUpdateScheduler.cs:152-161):
    /// excluded = the scene has no spatial index yet (bounds phase has not run) OR this unit has
    /// no membership row — deferring the rolling Register/Update until admission publishes it
    /// (Characters publish at registry Create :142, NPCs at spawn :135). Absence is exclusion,
    /// the native object-ID vector's restrictive reading — never a permissive false.
    /// </summary>
    public bool IsSpatiallyExcluded(ZoneUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return _registry.Spatial is null || _registry.Spatial.GetMembership(unit) is null;
    }

    /// <summary>392589F0 → 39D2DD60 for real: registry.UpdateSpatialMembership re-caches the live model position for the exact registered owner (ZoneUnitRegistry.cs:283-288).</summary>
    public void UpdateSpatialMembership(ZoneUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        _registry.UpdateSpatialMembership(unit);
    }

    public void UpdateShipMass(ZoneUnit unit) => ShipLeaf(unit, "mass (ServerUnit virtual +C8 → 39261A50)");
    public void UpdateShipSubmergeDepth(ZoneUnit unit) => ShipLeaf(unit, "submerge depth (ServerUnit virtual +D8 → 39258EB0)");

    /// <summary>Structurally unreachable for Zone 129 (ships are absent from this Zone's unit stream — readiness prints this guard line): the five ordinary kinds return as their native base pass does; ANY other kind names TODO(ship-units) instead of silently no-opping a real ship state.</summary>
    private static void ShipLeaf(ZoneUnit unit, string leaf)
    {
        var kind = unit.Snapshot.Identity.Kind;
        if (kind is ZoneUnitKind.Character or ZoneUnitKind.Npc or ZoneUnitKind.Slave or ZoneUnitKind.Mate or ZoneUnitKind.Housing)
            return; // ordinary kinds: the native ship pass has nothing to do for them
        throw new NotSupportedException(
            $"TODO(ship-units): ship update leaf {leaf} reached for unit {unit.Snapshot.ObjectId} of kind {kind}; " +
            "ship units are outside this Zone's scope and are reported in the readiness table instead of no-opped.");
    }
}

// ---------------------------------------------------------------------------
// Registry-facing movement services (IZoneUnitMovementServices,
// IZoneUnitMovementModel.cs:14-19) — the receive-path clock/switch/recorder.
// ---------------------------------------------------------------------------

/// <summary>
/// The thin production <see cref="IZoneUnitMovementServices"/> the unit registry reads on its
/// inbound movement path (ZoneUnitRegistry.ReceiveMovementCore :236-255). One instance per
/// scene generation, constructed by the host's registry-services factory.
/// </summary>
public sealed class ZoneSceneMovementServices : IZoneUnitMovementServices
{
    private readonly ZoneStartupState _startup;
    private readonly ZoneSceneReadinessNotes _notes;

    public ZoneSceneMovementServices(ZoneStartupState startup, ZoneSceneReadinessNotes notes)
    {
        ArgumentNullException.ThrowIfNull(startup);
        ArgumentNullException.ThrowIfNull(notes);
        _startup = startup;
        _notes = notes;
        notes.AddOnce("server-controlled-movement",
            "movement switch: server-controlled mode OFF — this host relays the ordinary client movement stream through World (local-on-wire, MovementRelay.cs:341-343); turning it on would drop every kind except 5 (ZoneUnitRegistry.cs:250) TODO(server-controlled-movement)");
        notes.AddOnce("native-movement-recording",
            "movement recording: the optional native recorder of 39362E80 has no owner — entries are consumed by the frame phases, never recorded TODO(native-movement-recording)");
    }

    /// <summary>
    /// False = accept the relayed client movement kinds (see the ctor's cited decision note).
    /// Cited, single, documented scene configuration — not a silent default: true would make
    /// the receive path drop every inbound kind except 5 by name-equivalent policy with no
    /// evidence that this relay only carries kind 5.
    /// </summary>
    public bool ServerControlledMovement => false;

    /// <summary>The same single scene clock as every other leaf (World-aligned domain, sampled per call — the registry reads it once per inbound packet).</summary>
    public uint ServerMilliseconds => _startup.WorldGameClock.GetTime();

    /// <summary>Once-reported at readiness (ctor note): no native movement recorder exists in this tree; the frame phases consume each movement for input/correction instead.</summary>
    public void RecordMovement(ZoneUnit owner, UnitMovementData movement)
    {
        _ = owner; _ = movement;
    }
}

// ---------------------------------------------------------------------------
// Scene tick services (IZoneSimulationTickServices, ZoneSimulationTick.cs:11-33).
// ---------------------------------------------------------------------------

/// <summary>
/// Production <see cref="IZoneSimulationTickServices"/> — the scene/manager half of the X2 Zone
/// tick (39333110). REAL leaves: detailed/time-of-day reports through the real
/// ZoneTimeOfDayState generators, the NPC manager update, aggro selection, spatial location
/// reports, and the heartbeat (time-of-day Advance + signal-seconds accumulator — the host
/// engine phase that owns advancement, ZoneTimeOfDayState.cs:8). Ownerless native leaves
/// (housing/gimmicks/doodads/plots/mole/aiDebug/doodadUnitCollisions/content-reload/
/// skill-contexts/deferred-removals/movement-buffers) are registered ONCE in the constructor as
/// named readiness partial-leaf lines — the plan's report-don't-kill rule (:97-104): their
/// bodies stay empty BY DECISION with the note printed, so the tick can never die on an
/// unowned phase and readiness cannot claim the phase ran.
/// </summary>
public sealed class ZoneSceneTickServices : IZoneSimulationTickServices
{
    private readonly ZoneUnitRegistry _registry;
    private readonly ZoneStartupState _startup;
    private readonly ZoneNpcManager _manager;
    private readonly ZoneSceneScriptClock _scriptClock;
    private readonly float _frameSeconds;
    private readonly ZoneSceneReadinessNotes _notes;
    private readonly ZoneHeartbeatEmitter _heartbeat;

    public ZoneSceneTickServices(ZoneUnitRegistry registry, ZoneStartupState startup, ZoneNpcManager manager,
        ZoneSceneScriptClock scriptClock, TimeSpan framePeriod, ZoneSceneReadinessNotes notes, bool oceanSimulateOnZone = false)
    {
        _heartbeat = new ZoneHeartbeatEmitter(oceanSimulateOnZone);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(startup);
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(scriptClock);
        ArgumentNullException.ThrowIfNull(notes);
        if (framePeriod <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(framePeriod));
        _registry = registry;
        _startup = startup;
        _manager = manager;
        _scriptClock = scriptClock;
        _frameSeconds = (float)framePeriod.TotalSeconds;
        _notes = notes;
        notes.AddOnce("server-type-slot",
            "server type: dedicated slot value 2 (no ZoneClient+8C accessor recovered) TODO(server-type-slot)");
        notes.AddOnce("content-reload", "content reload (39331D30): no reload owner in this single-content scene TODO(content-reload)");
        notes.AddOnce("skill-contexts", "skill contexts (39456970): expire pass unowned — no skill runtime yet TODO(skill-contexts)");
        notes.AddOnce("deferred-removals",
            "deferred unit removals (3935FE10): 0 pending BY DESIGN — World removals are immediate (ZoneUnitRegistry.Remove :612-631)");
        notes.AddOnce("housing-manager", "housing manager (second owner of 39360C10): no housing in this scene TODO(housing-manager)");
        notes.AddOnce("gimmicks", "gimmick updates (39339B70): no gimmick runtime owner TODO(gimmicks)");
        notes.AddOnce("doodads", "doodad updates (39452AA0): no doodad runtime owner TODO(doodads)");
        notes.AddOnce("plots", "plot updates (39455230): no plot runtime owner TODO(plots)");
        notes.AddOnce("ai-debug", "AI debug sessions (3944EB80): no debug-session owner TODO(ai-debug)");
        notes.AddOnce("mole", "mole reports (39332C50, miner/trader rays): no mole owner TODO(mole)");
        notes.AddOnce("doodad-unit-collisions", "doodad/unit collisions (39454970): no doodad collision owner TODO(doodad-unit-collisions)");
        notes.AddOnce("movement-buffers",
            "movement buffers (3936A010): controller history is drained inside the frame phases (TryRunPrePhase/TryRunPostPhase, ZoneActorMovementController.cs:283/:342); no separate external pass owner TODO(movement-buffers-external)");
    }

    /// <summary>Managers are constructed before this service in the scene factory — that construction order IS the 39333490 initialization gate; ticks themselves only start at session receive after phase Completed.</summary>
    public bool IsInitialized => true;
    public int ServerType => 2; // dedicated slot, see the ctor note

    // Ownerless phases: bodies intentionally empty; the ctor registered their named readiness lines.
    public void UpdateContentReload() { }
    public void ProcessDeferredUnitRemovals() { }
    public void ExpireSkillContexts() { }
    public void UpdateHousingManager() { }
    public void UpdateGimmicks() { }
    public void UpdateDoodads() { }
    public void UpdatePlots() { }
    public void UpdateAiDebugSessions() { }
    public void UpdateMoleReports() { }
    public void UpdateDoodadUnitCollisions() { }

    /// <summary>REAL 0x393322F0: the ZoneTimeOfDayState compares advanced settings by bits and returns the detailed report only on change; true means this tick's ordinary time phase is suppressed (:108-110).</summary>
    public bool UpdateDetailedTimeOfDay()
    {
        if (_startup.TimeOfDayState.CreateDetailedReport() is { } report)
        {
            _registry.QueueNotification(report);
            return true;
        }
        return false;
    }

    /// <summary>REAL 0x39332400: two-second accumulator + hour-change gate inside ZoneTimeOfDayState.CreateTimeReport; call only when the detailed report did not fire (the tick order guarantees it).</summary>
    public void UpdateTimeOfDay()
    {
        if (_startup.TimeOfDayState.CreateTimeReport(_frameSeconds) is { } report)
            _registry.QueueNotification(report);
    }

    /// <summary>REAL 3934A170 manager update: movement gate, _npcs.Update(frame, canReplicate), posture pass, spawner phases, group phase — all inside ZoneNpcManager.Update (:82-134).</summary>
    public void UpdateNpcManager(uint frame) => _manager.Update(frame);

    /// <summary>REAL 39344DA20: per-owner aggro selection over the live tables (ZoneAggroTable.UpdateSelection :265), dead owners skipped exactly like RefreshAggro does (:338).</summary>
    public void UpdateAiAggroReports()
    {
        var scene = _registry.Npcs;
        if (scene is null) return;
        foreach (var npc in scene.Npcs.Values.ToArray())
            if (!npc.Unit.IsDead)
                npc.Aggro.UpdateSelection();
    }

    /// <summary>
    /// REAL 3935E7E0: re-cache spatial membership for the scene's registered units. Runs AFTER
    /// PostPhase inside RunFrame precisely so it reads what postphase published
    /// (IZoneMovementFramePhases.cs:70-74). Membership presence is checked by the registry
    /// itself (:281), so unregistered/pre-bounds units are naturally skipped.
    /// </summary>
    public void UpdateUnitLocationReports()
    {
        foreach (var unit in _registry.Units.Values.ToArray())
            _registry.UpdateSpatialMembership(unit);
    }

    /// <summary>
    /// REAL 39332510 heartbeat: advances time-of-day (the engine-phase owner per ZoneTimeOfDayState.cs:8) and the native
    /// float-seconds signal clock once per frame, then sends ZWHeartbeat (op 60) when the float delta since the last send is
    /// &gt;= 1.0 (last starts at 0 like the native static). The bool is cvar e_water_ocean_simulate_on_zone != 0 (the native
    /// sets it per zone group; writer near line 576089 of the x2game-dev_dedicate.c export); World answers with WZWorldGameTime.
    /// </summary>
    public void UpdateHeartbeat()
    {
        _startup.TimeOfDayState.Advance(_frameSeconds);
        _scriptClock.Advance(_frameSeconds);
        if (_heartbeat.TryCreate(_scriptClock.TimeSeconds, out var frame)) _registry.QueueNotification(frame);
    }

    /// <summary>Ownerless body — ctor note. See the class doc: report-don't-kill (ZoneSimulationTick never dies on an unowned phase).</summary>
    public void UpdateMovementBuffers() { }

    /// <summary>The tick releases its services when the scene retires them (ZoneSimulationTick.DisposeServices); manager/registry lifetimes belong to the scene resource scope, not here.</summary>
    public void Dispose() { }
}

// ---------------------------------------------------------------------------
// Spawner/manager services (5 interfaces, blueprint NpcSpawnerFactoryTests.cs:239-349).
// ---------------------------------------------------------------------------

/// <summary>
/// Production spawner + NPC-manager services implementing EXACTLY the blueprint's interface set
/// — <see cref="IZoneNpcSpawnerFactoryServices"/> (which inherits the point/area/proximity/
/// collision/group/event sub-services, ZoneNpcSpawnerFactory.cs:32-50),
/// <see cref="IZoneNpcManagerServices"/> (which inherits <see cref="IZoneNpcPostureServices"/>,
/// ZoneNpcManager.cs:3-10), <see cref="IZoneNpcSpawnerLifecycleServices"/>,
/// <see cref="IZoneNpcGroupServices"/> and <see cref="IZoneNpcInitialSpawnServices"/> — over the
/// REAL scene subsystems: spawner content (activation overrides), the scene clock (posture hour,
/// frame seconds), the physics backend (terrain/occupancy sweeps), the model-position source,
/// the registry notification queue, and a managed group-id pool. Debug drawing stays gated off
/// (env+120 has no accessor — ctor note); capability leaves whose native owner is absent
/// (water level, navigation shapes, spawn scripts, forbidden lists, type filters) are named
/// once in the constructor readiness notes — reported, never silent successes.
/// One instance per scene generation; disposal belongs to the scene resource scope order the
/// host registers (factory → manager → spawner registry, blueprint Scene.Dispose :348).
/// </summary>
public sealed class ZoneSceneSpawnerServices : IZoneNpcSpawnerFactoryServices, IZoneNpcManagerServices,
    IZoneNpcSpawnerLifecycleServices, IZoneNpcGroupServices, IZoneNpcInitialSpawnServices, IDisposable
{
    private readonly ZoneUnitRegistry _registry;
    private readonly ZoneStartupState _startup;
    private readonly ZoneNpcContent _npcContent;
    private readonly float _frameSeconds;
    private readonly Func<ZoneUnit, Vector3> _modelPosition;
    private readonly Func<IZonePhysicsBackend?> _backend;
    private readonly Func<bool> _isShuttingDown;
    private readonly ZoneSceneReadinessNotes _notes;
    private readonly Action<string>? _trace;
    private readonly Stack<uint> _freeGroupIds = [];
    private uint _nextGroupId;
    private bool _disposed;

    public int AdmissionRejectionCount { get; private set; }
    public int InvalidPlacementCount { get; private set; }
    /// <summary>
    /// Placements rejected because spawn separation could not be evaluated (no authored NPC spawn
    /// script source, ZoneSceneProviders.GetSpawnScript). Counted + traced + reported at readiness;
    /// the placement is REJECTED, never admitted unseparated.
    /// </summary>
    public int SeparationRefusalCount { get; private set; }
    /// <summary>The first named separation refusal, reported verbatim at readiness.</summary>
    public string? FirstSeparationRefusal { get; private set; }

    public ZoneSceneSpawnerServices(ZoneUnitRegistry registry, ZoneStartupState startup, ZoneNpcContent npcContent,
        TimeSpan framePeriod, Func<ZoneUnit, Vector3> modelPosition, Func<IZonePhysicsBackend?> backend,
        Func<bool> isShuttingDown, ZoneSceneReadinessNotes notes, Action<string>? trace)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(startup);
        ArgumentNullException.ThrowIfNull(npcContent);
        ArgumentNullException.ThrowIfNull(modelPosition);
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(isShuttingDown);
        ArgumentNullException.ThrowIfNull(notes);
        if (framePeriod <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(framePeriod));
        _registry = registry;
        _startup = startup;
        _npcContent = npcContent;
        _frameSeconds = (float)framePeriod.TotalSeconds;
        _modelPosition = modelPosition;
        _backend = backend;
        _isShuttingDown = isShuttingDown;
        _notes = notes;
        _trace = trace;
        notes.AddOnce("env-slot-122",
            "environment+122 (spawning master enable): no env accessor recovered — dedicated default true TODO(env-slot-122)");
        notes.AddOnce("env-slot-150",
            "environment+150: no env accessor recovered — dedicated default false (bounds published) TODO(env-slot)");
        notes.AddOnce("world-slot", "World+8C: no world slot accessor recovered — dedicated value 2 TODO(world-slot)");
        notes.AddOnce("editor-slot",
            "npc_editor_no_npc: production dedicated build is not editor mode — false, gate unread (read only when env+150 set) TODO(editor-slot)");
        notes.AddOnce("spawner-type-filter",
            "spawner type filter (39C537F0): no loaded filter set has an owner in this scene — nothing filtered TODO(spawner-type-filter)");
        notes.AddOnce("water-level",
            "spawn water level: cell water volumes have no level query owner — candidates resolve to air TODO(water-level)");
        notes.AddOnce("spawn-script",
            "NPC spawn scripts: no authored spawn-script source resolved for this scene TODO(npc-spawn-script)");
        notes.AddOnce("forbidden-templates",
            "forbidden NPC template list: no owner for the native blacklist — no template forbidden by policy TODO(forbidden-template-list)");
        notes.AddOnce("navigation-services",
            "spawn navigation checks: GetNavigationType/IsPointInNavigationShape unowned — unreachable while SpawnValidationMode is 0 TODO(navigation-services)");
        notes.AddOnce("movement-rate-limit",
            "NPC movement rate limit: env/config gate unowned — limit disabled, gate object still runs (ZoneNpcManager :98-108) TODO(movement-rate-limit)");
        notes.AddOnce("drawing",
            "debug drawing (env+120 / config 3A607CE8): no owners — DebugDrawingAvailable=false gates every draw call TODO(drawing-config)");
        notes.AddOnce("cvar-3A607D38",
            "spawn validation cvar 3A607D38: no cvar owner — mode 0 (recorded default; area-ground pass skipped) TODO(cvar-3A607D38)");
        notes.AddOnce("adjust-radius",
            "spawn AdjustPosition uses the single-point terrain ray; the native radius footprint has no owner TODO(adjust-radius)");
        notes.AddOnce("group-id-pool",
            "group ids: managed scene-local monotonic pool (native pool slot unowned) TODO(native-group-id-pool)");
        notes.AddOnce("puppet-alertness",
            "puppet alertness (native AI +448 chain): null for every owner — no Puppet without an adopted actor TODO(native-ai)");
    }

    private IZonePhysicsBackend RequireBackend() =>
        _backend() ?? throw new NotSupportedException(
            "Spawner collision requires the scene physics backend (constructed by the host after level completion).");

    // ------------------------------------------------------------------
    // IZoneNpcSpawnerFactoryServices
    // ------------------------------------------------------------------
    public float FrameSeconds => _frameSeconds;
    public float Hour => _startup.TimeOfDayState.Hour;
    public uint TimeMilliseconds => _startup.WorldGameClock.GetTime();
    /// <summary>Dedicated default true (spawning enabled); the env+122 slot has no accessor — named at readiness (ctor note).</summary>
    public bool EnvironmentFlag122 => true;
    public bool EnvironmentFlag150 => false;
    public int WorldValue8C => 2;
    public bool EditorNpcsDisabled => false;
    /// <summary>39C537F0's filter set has no owner — nothing is filtered, named at readiness (ctor note).</summary>
    public bool IsSpawnerTypeFiltered(uint spawnerType) => false;
    /// <summary>REAL activation override from spawner content (casefile pin): the authored per-type ActivationState of ZoneNpcSpawnerContent; an unresolvable type returns null = no override (native falls to its own gate).</summary>
    public bool? FindActivationOverride(uint spawnerType) =>
        _npcContent.Spawners.Spawners.TryGetValue(spawnerType, out var type) ? type.Definition.ActivationState : null;

    /// <summary>3933D680's camera sample: first admitted Character's live model position, else the first NPC's, else zero before any unit exists (documented: no camera exists until an admitted unit does — draw comparison only, and drawing is gated off).</summary>
    public Vector3 GetCameraPosition()
    {
        foreach (var unit in _registry.Units.Values)
            if (unit.Snapshot.Identity.Kind == ZoneUnitKind.Character)
                return _modelPosition(unit);
        foreach (var unit in _registry.Units.Values)
            if (unit.Snapshot.Identity.Kind == ZoneUnitKind.Npc)
                return _modelPosition(unit);
        return Vector3.Zero;
    }

    /// <summary>
    /// REAL despawn of one registration's owners: queues immediate NpcRemoval for every NPC of
    /// this spawner (the wire path World itself uses for lifetime removals —
    /// ZoneNpcSimulation :107-108), guarded per owner by current(). preserveAlertNpcs keeps
    /// owners with LIVE aggro (alert) — the parameter's exact meaning — and removes the rest.
    /// </summary>
    public void RemoveSpawnedNpcs(ZoneNpcSpawnerRegistration owner, bool preserveAlertNpcs, Func<bool> current)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(current);
        var scene = _registry.Npcs;
        if (scene is null) return;
        foreach (var npc in scene.Npcs.Values.ToArray())
        {
            if (!current()) return;
            if (npc.SpawnerId != owner.Id) continue;
            if (preserveAlertNpcs && npc.Aggro.Entries.Count > 0) continue;
            _registry.QueueNotification(new NpcRemoval(npc.Id, 0).ToFrame());
        }
    }

    /// <summary>Unreachable while DebugDrawingAvailable is false (ZoneNpcManager.UpdateCore :123-126 gates before DrawDebug); the readiness drawing note covers the disabled capability — empty is the native gated outcome, not a fabricated draw.</summary>
    public void DrawSpawner(ZoneNpcSpawnerRegistration owner, ZoneNpcSpawnerPlacementRow source) { }

    /// <summary>REAL admission diagnostic line — every authored placement rejection World-side reaches the log with its exact reason (bounded by placement count).</summary>
    public void ReportAdmission(ZoneNpcSpawnerPlacementRow source, ZoneNpcSpawnerAdmissionDiagnostic diagnostic)
    {
        AdmissionRejectionCount = unchecked(AdmissionRejectionCount + 1);
        _trace?.Invoke($"Authored spawner {source.SpawnerId} (type {source.SpawnerType}) admission rejected: {diagnostic}");
    }

    // ------------------------------------------------------------------
    // IZoneNpcSpawnCollisionServices (+ area sub-service)
    // ------------------------------------------------------------------
    /// <summary>model virtual3A8 has no managed owner — null is the DOCUMENTED absence path (ZoneNpcSpawnCollision :45 skips the entity test for exactly this :172-174), not a fabricated pass.</summary>
    public IZoneNpcSpawnPhysicalEntity? GetPhysicalEntity(ZoneUnit unit) => null;
    /// <summary>Named refusal, unreachable while <see cref="GetPhysicalEntity"/> returns null (the loop :172-175 never calls this without an entity). TODO(ctor-bodies) when bodies exist.</summary>
    public int IntersectEntity(IZoneNpcSpawnPhysicalEntity entity, ZoneNpcSpawnEntityIntersection query) =>
        throw new NotSupportedException(
            "TODO(ctor-bodies): entity intersection (393478A2) requires model-owned physical entities; none exist in this scene (GetPhysicalEntity documents the absence).");

    /// REAL static-world intersection over the scene physics backend: zero sweep = overlap probe
    /// (TryCollide), nonzero sweep = bounded segment sweep (TrySweep) returning the CONTACT
    /// DISTANCE (>0 = hit, 0 = miss) — exactly the semantics ZoneNpcSpawnCollision consumes
    /// (:181-183 raise-on-hit, :198-203 distance-to-ground). The authored capsule maps directly
    /// onto the backend's capsule shape (both call sites use the native Z axis,
    /// ZoneNpcSpawnCollision.cs:162/:198); query.EntityTypes flag filtering (13/8/5) has no
    /// owner and is named TODO(world-intersection-entity-types).
    /// </summary>
    public float IntersectWorld(ZoneNpcSpawnWorldIntersection query)
    {
        var backend = RequireBackend();
        var capsule = query.Capsule;
        var shape = new ZonePhysicsShape(new Vector3(capsule.Radius, capsule.Radius, capsule.HalfHeight), true);
        if (query.SweepDirection == Vector3.Zero)
            return backend.TryCollide(shape, capsule.Center, null) ? 1f : 0f;
        if (backend.TrySweep(shape, capsule.Center, query.SweepDirection, null, out var hit))
            return hit.Distance;
        return 0f;
    }

    /// <summary>0 = the recorded default of cvar3A607D38 (no cvar owner — ctor note). Nonzero would enable the triangle-spawner area-ground pass, whose navigation leaves are unwired; mode 0 keeps them unreachable by construction while still running occupancy + world sweeps for every admission.</summary>
    public int SpawnValidationMode => 0;
    public void ReportInvalidNpcTemplate(uint templateId) =>
        _trace?.Invoke($"Spawn collision: invalid NPC template {templateId} during occupancy (native retains capsule defaults, ZoneNpcSpawnCollision.cs:208-216)");
    public void ReportOccupiedSpawn(ZoneNpcSpawnerRegistration owner, NpcSpawnRequest request) =>
        _trace?.Invoke($"Spawner {owner.Id}: spawn occupied {request.SpawnerId}/{request.TemplateId} beyond the 30-failure report gate (ZoneNpcSpawnCollision.cs:142-147)");
    /// <summary>Named refusal — unreachable while SpawnValidationMode == 0 (CheckAreaGround is gated at ZoneNpcSpawnCollision.cs:152). TODO(navigation-services) wires both leaves together.</summary>
    public int GetNavigationType(Vector3 position, uint capabilityMask) =>
        throw new NotSupportedException(
            $"TODO(navigation-services): AISystem virtual368 navigation type for ({position.X:0.#},{position.Y:0.#}) has no owner (unreachable at SpawnValidationMode 0).");
    /// <summary>Named refusal — same mode gate as <see cref="GetNavigationType"/> (AISystem virtual258, ZoneNpcSpawnCollision.cs:192-198).</summary>
    public bool IsPointInNavigationShape(Vector3 position, bool useFirstShapeSet) =>
        throw new NotSupportedException(
            $"TODO(navigation-services): navigation shape containment for ({position.X:0.#},{position.Y:0.#}) has no owner (unreachable at SpawnValidationMode 0).");

    // ------------------------------------------------------------------
    // Point/area spawn services (terrain, water, adjustment, script, policy)
    // ------------------------------------------------------------------
    /// <summary>REAL terrain height for placement through the scene's downward ray; a miss outside loaded cells returns 0 and is named once (authored placements sit inside the loaded window).</summary>
    public float GetTerrainHeight(float x, float y)
    {
        var backend = RequireBackend();
        if (backend.TryGround(new Vector3(x, y, 4096f), 8192f, out var ground)) return ground.Position.Y;
        _notes.AddOnce("spawner-terrain-ray-miss",
            "spawner terrain ray: at least one placement candidate fell outside the loaded window (height 0) TODO(terrain-height-fallback)");
        return 0f;
    }

    /// <summary>Once-reported at readiness (ctor note): cell water volumes have no level-query owner in this scene — candidates resolve as air, never a fabricated depth.</summary>
    public float GetWaterLevel(Vector3 candidate, float radius) => 0f;

    /// <summary>
    /// REAL re-anchoring through the scene's terrain ray: when raisedAnchor is set or the ground
    /// sits outside a centimeter of the candidate, the accepted position is clamped to the
    /// measured ground and reported true (false = already sound / no ground found).
    /// radius is unused — the native radius footprint has no owner (ctor note TODO(adjust-radius)).
    /// </summary>
    public bool AdjustPosition(Vector3 position, float radius, bool raisedAnchor, out Vector3 adjusted)
    {
        _ = radius;
        adjusted = default;
        var backend = RequireBackend();
        if (!backend.TryGround(new Vector3(position.X, position.Y, position.Z + 100f), 200f, out var ground))
            return false;
        var groundZ = ground.Position.Y;
        if (!raisedAnchor && MathF.Abs(groundZ - position.Z) <= 0.01f) return false;
        adjusted = new Vector3(position.X, position.Y, groundZ);
        return true;
    }

    /// <summary>REAL invalid-placement report (native 3933Fxxx reports rather than crashes): counted + traced; the blueprint's throw was synthetic-fixture policy, not production semantics.</summary>
    public void ReportInvalidPlacement(ZoneNpcSpawnerRegistration owner)
    {
        InvalidPlacementCount = unchecked(InvalidPlacementCount + 1);
        _trace?.Invoke($"Spawner {owner.Id}: invalid placement reported (count {InvalidPlacementCount})");
    }

    /// <summary>Once-reported at readiness (ctor note): no authored spawn-script source resolved — null = no script, never a fabricated reference.</summary>
    public ZoneLuaReference? GetSpawnScript(ZoneNpc npc) => null;

    /// <summary>Named separation refusal for one placement; the caller rejects that placement.</summary>
    public void ReportSeparationRefusal(ZoneNpcSpawnerRegistration owner, string reason)
    {
        SeparationRefusalCount = unchecked(SeparationRefusalCount + 1);
        FirstSeparationRefusal ??= reason;
        // Counted for the whole run, traced for the first few only: the refusal is a standing
        // dependency gap, and a trace per attempt produced ~1,800 lines per minute in the first
        // live host run. The count and the first message still reach the readiness report.
        if (SeparationRefusalCount <= 5)
            _trace?.Invoke($"Spawner {owner.Id}: spawn separation unevaluable, placement rejected by name (count {SeparationRefusalCount}) — {reason}");
    }
    /// <summary>False: the native editor-disable gate lives in the spawner registry's own admission (ZoneSceneCoordinator.cs:173-175 comment) and a production dedicated build never sets npc_editor_no_npc (ctor note).</summary>
    public bool SpawningDisabled => false;
    /// <summary>Once-reported at readiness (ctor note): the native forbidden-template blacklist has no owner — no template is forbidden by policy, and the policy's absence is on the table.</summary>
    public bool IsForbiddenTemplate(uint id) => false;
    public Vector3 GetModelPosition(ZoneUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return _modelPosition(unit);
    }

    // ------------------------------------------------------------------
    // IZoneNpcManagerServices / IZoneNpcPostureServices
    // ------------------------------------------------------------------
    /// <summary>Env/config gate unowned (ctor note) — the gate OBJECT still runs every manager update (ZoneNpcManager :98-108); disabling the limit is the reported configuration, not a skipped gate.</summary>
    public bool MovementRateLimitEnabled => false;
    public uint MovementIntervalMilliseconds => 0; // unused while the limit is disabled (manager reads it only inside the limited branch :102)
    /// <summary>env+120 has no accessor (ctor note) — drawing capability off, which gates every draw call at ZoneNpcManager :123-126.</summary>
    public bool DebugDrawingAvailable => false;
    public bool DrawSpawners => false;

    /// <summary>This NPC owner's stable allocation key — the NpcObjectId, distinct from unit IDs and the update phase exactly as the interface documents (ZoneNpcPostureState.cs:15-16); matches the blueprint (:330).</summary>
    public ulong GetOwnerAllocationKey(ZoneNpc npc)
    {
        ArgumentNullException.ThrowIfNull(npc);
        return npc.Id;
    }

    /// <summary>Null = the current actor model cannot be published — actor publication is unowned (ctor/readiness note actor-publication via the update services). The posture pass still runs for real over FindPostures + Hour and emits its signal-queue work where an actor exists.</summary>
    public IZoneNpcPostureModel? GetCurrentModel(ZoneNpc npc) => null;

    /// <summary>REAL content: the NPC's configured posture set rows (null = the set is absent, exactly the interface's contract — ZoneNpcPostureState.cs:18-19).</summary>
    public IReadOnlyList<ZoneNpcPosture>? FindPostures(uint postureSetId) =>
        _npcContent.Postures.Sets.TryGetValue(postureSetId, out var set) ? set.Rows : null;

    // ------------------------------------------------------------------
    // IZoneNpcSpawnerLifecycleServices
    // ------------------------------------------------------------------
    /// <summary>The scene RNG: Random.Shared — the SAME documented source as the signal runtime's NextRandomUInt32 (one source per scene, stable order inside one dispatch).</summary>
    public float NextRandomFloat() => Random.Shared.NextSingle();

    /// <summary>39344AA0 is CONDITIONAL: "if an entity/AI object exists, set its actual AI group to the empty group" (ZoneNpcSpawnerRegistry doc :12-13). No entity/AI object exists for any owner here, so the native half correctly has nothing to set — the documented native-conditional path, not a permissive default.</summary>
    public void ClearActorGroup(ZoneNpc npc) { }

    // ------------------------------------------------------------------
    // IZoneNpcGroupServices
    // ------------------------------------------------------------------
    /// <summary>Native AI +448 chain — null for every owner: "missing entity, AI object or Puppet is represented by null" (interface doc :19-20), which is this scene's factual state; ctor note feeds readiness.</summary>
    public int? GetPuppetAlertness(ZoneNpc npc) => null;
    /// <summary>The live anchor read comes from the native AI actor (+448 → +208 → +8) — no actor, so null = missing, the interface's documented representation (:21-23).</summary>
    public byte? GetFormationAnchorMember(uint groupId) => null;

    /// <summary>REAL: the owner's authored spawn orientation from its immutable wire pose (ZoneUnitPose.FromSnapshot).</summary>
    public Quaternion GetRotation(ZoneNpc npc)
    {
        ArgumentNullException.ThrowIfNull(npc);
        return ZoneUnitPose.FromSnapshot(npc.Unit.Snapshot).Rotation;
    }

    public Vector3 GetPosition(ZoneNpc npc)
    {
        ArgumentNullException.ThrowIfNull(npc);
        return _modelPosition(npc.Unit);
    }

    /// <summary>REAL formation construction through the owned engine-free calculator (ZoneNpcGroupFormation), supplied this scene's services and the live not-shutting-down guard (blueprint :338-339).</summary>
    public IZoneNpcGroupFormation CreateFormation(uint groupType, Vector3 position, float yaw, byte anchorMember) =>
        ZoneNpcGroupFormation.Create(_npcContent, groupType, position, yaw, anchorMember, this,
            () => !_isShuttingDown())!;

    /// <summary>REAL outgoing path: the scene registry's notification queue — the same ordered flush every other producer uses (ZoneUnitRegistry.QueueNotification :36).</summary>
    public void Send(ZoneFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        _registry.QueueNotification(frame);
    }

    /// <summary>REAL: returns an id to the managed pool for reuse (pair of <see cref="AllocateGroupId"/>).</summary>
    public void FreeGroupId(uint groupId) => ReleaseGroupId(groupId);

    /// <summary>Unreachable-in-practice (unbounded managed pool, ctor note): the trace keeps the native failure report visible if a future bounded pool ever trips it.</summary>
    public void ReportGroupAllocationFailure() =>
        _trace?.Invoke("Group allocation failure reported (managed pool is unbounded; native cap slot TODO(native-group-id-pool))");

    /// <summary>Gated off with drawing (ctor note) — unreachable while DebugDrawingAvailable is false; empty is the native gated outcome.</summary>
    public void DrawSphere(Vector3 position, float radius, uint color, bool shaded) { }
    /// <summary>Gated off with drawing (ctor note) — unreachable while DebugDrawingAvailable is false.</summary>
    public void DrawText(Vector3 position, float scale, string text) { }

    // ------------------------------------------------------------------
    // IZoneNpcInitialSpawnServices
    // ------------------------------------------------------------------
    /// <summary>The owner's live spawn group identity (type, registration id, member index) — ZoneNpcSpawnGroup of NpcSpawnMessages.cs:39 — null when ungrouped.</summary>
    public NpcSpawnGroup? GetCurrentAiGroup(ZoneNpc npc)
    {
        ArgumentNullException.ThrowIfNull(npc);
        return npc.Spawn.Group;
    }

    /// <summary>The scene RNG (same documented Random.Shared source as <see cref="NextRandomFloat"/>).</summary>
    public uint NextRandom() => unchecked((uint)Random.Shared.NextInt64());

    /// <summary>Named refusal: AI virtual1C8 path-type execution (31058598) has no path engine in this tree — reachable only when an authored named path matches (ZoneNpcInitialSpawnBehavior :48-52), and surfaces as the exact named gate if it ever does. TODO(path-finder).</summary>
    public uint FindPathType(string name) =>
        throw new NotSupportedException(
            $"TODO(path-finder): AI path type resolution for '{name}' requires the unowned native path engine (31058598, ZoneNpcInitialSpawnBehavior.cs:44).");

    /// <summary>Null = the current entity has no script table — no entity exists (the interface's documented missing path, :43-44).</summary>
    public ZoneLuaReference? RetainEntityScript(ZoneNpc npc) => null;

    /// <summary>REAL script-error channel: traced with the exact owner + reason (blueprint routed this to the harness's script-error collector :347).</summary>
    public void ScriptError(ZoneNpc npc, string reason) =>
        _trace?.Invoke($"NPC script error npcId={npc.Id} unit={npc.ObjectId}: {reason}");

    // ------------------------------------------------------------------
    // Remaining blueprint members shared across interfaces
    // ------------------------------------------------------------------
    /// <summary>39361350-family RNG for the spawner event surface — the same documented scene RNG source.</summary>
    public uint NextRandomUInt32() => unchecked((uint)Random.Shared.NextInt64());

    /// <summary>REAL spawner event diagnostic line (interface ZoneNpcSpawnerEventServices, blueprint :325).</summary>
    public void Report(ZoneNpcSpawnerEventDiagnostic diagnostic, ZoneNpcSpawnerRegistration owner, byte id) =>
        _trace?.Invoke($"Spawner event {diagnostic}: spawner {owner.Id} part {id}");

    /// <summary>Managed monotonic pool (ctor note TODO(native-group-id-pool)): released ids are reused before fresh ones are minted, scene-local and bounded only by memory.</summary>
    public int AllocateGroupId()
    {
        if (_freeGroupIds.Count > 0) return unchecked((int)_freeGroupIds.Pop());
        _nextGroupId = unchecked(_nextGroupId + 1);
        return unchecked((int)_nextGroupId);
    }

    /// <summary>Interface sibling of <see cref="FreeGroupId"/> — same pool release (blueprint :321).</summary>
    public void ReleaseGroupId(uint id)
    {
        if (id > 0) _freeGroupIds.Push(id);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _freeGroupIds.Clear();
    }
}

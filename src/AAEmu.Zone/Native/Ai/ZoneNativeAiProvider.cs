using System.Runtime.InteropServices;

using AAEmu.Zone.Actors;
using AAEmu.Zone.Ai;
using AAEmu.Zone.Content;
using AAEmu.Zone.Native.Engine;
using AAEmu.Zone.Npcs;
using AAEmu.Zone.Physics;

namespace AAEmu.Zone.Native.Ai;

/// <summary>
/// Scene admission view consulted by <see cref="ZoneNativeAiProvider"/> before it binds a handle.
/// One implementation exists per scene generation, backed by the scene's actor catalog
/// (<c>ZoneSceneActivation</c>'s role, Scene/ZoneSceneActivation.cs): <see cref="GetKind"/>
/// returns the admitted kind of a live generation and throws for unknown or retired handles —
/// the rejection discipline of <c>IZoneActorLifecycle.Create</c>/<c>Retire</c>
/// (Actors/IZoneActorLifecycle.cs:58-67).
/// </summary>
public interface IZoneNativeAiActorView
{
    ZoneActorKind GetKind(ZoneActorHandle actor);
}

/// <summary>One native dependency of the AI backend, with the concrete evidence or open TODO that governs it.</summary>
public sealed record ZoneNativeAiDependency(string Name, bool IsResolved, string Evidence);

/// <summary>
/// The temporary native AI provider behind <c>IZoneAiProvider</c>
/// (Ai/IZoneAiProvider.cs:34): the embedded engine's AI, constructed and
/// retired only inside this adapter type. Native ABI stays in this file; the contracts folder
/// receives only value records and interfaces.
///
/// Invariants enforced here:
/// <list type="bullet">
/// <item>One AI backend per NPC: the static binding gate rejects a second bind for a live
/// generation across every provider instance of this type, and only <see cref="RemoveNpc"/>
/// ends a binding; re-entry reaches the next generation through a fresh <see cref="CreateNpc"/>.
/// The managed provider stays gated behind TODO(managed-ai)
/// (Scene/ZoneSceneBackendSelection.cs:25-27) and must consult
/// <see cref="IsBound"/> so goal/Lua execution never picks up a native-driven NPC.</item>
/// <item>One scene owner: the scene constructs exactly one instance through
/// <c>ZoneSceneBackendSelection.Ai == Native</c> and pumps its clock through one authority
/// (<c>IZoneAiFramePump</c>); this type samples no time source.</item>
/// <item>Missing native passes are reported, never substituted: every native operation first
/// requires its dependency entry through <see cref="ReportDependencies"/> and throws
/// <see cref="NotSupportedException"/> listing each unresolved entry with its evidence while the
/// entry is open.</item>
/// </list>
///
/// Native dependencies, each with its evidence and open question:
/// <list type="bullet">
/// <item>cryaisystem_dedicate.dll module — SHA-256 identity
/// <c>A740D58C…A9326CBB…AADFE921</c> (Native/Engine/ZoneNativeEngine.Identities.cs:14); the
/// native conditional AI loader accepts an absent module (Native/Engine/ZoneNativeEngine.cs:171-173).</item>
/// <item>CAISystem instance — AI init already runs from framework PostInit
/// (FIRST_ENTRY_NOTES.md:722-724, direct-call chain PostInit 306F77A2 → AI 31049268 →
/// 3100A8C4 reading the game service from gEnv+8; CSystem+0x58 == gEnv+8 at :766), and the
/// CAISystem virtuals are known (virtual+38 global update policy,
/// Npcs/IZoneNpcActorScene.cs:36; virtual368/310515F8 and virtual258/3104AA48,
/// Npcs/ZoneNpcSpawnCollision.cs:37-39). TODO(native-ai): locate the CAISystem instance
/// publication slot beside the gEnv+8 game-service slot; no evidence yet records the getter.</item>
/// <item>Native NPC AI actor construction for a content model — the recovered chain is
/// 39345000 (owner construction, Npcs/ZoneNpcSimulation.cs:107-110) and 39348440
/// (actor initialization, :123-135), with stance selection at AI 31050598 accepting model
/// 10 → actor 1 → stance row 74 (FIRST_ENTRY_NOTES.md:568-571, :729-733).
/// TODO(native-ai): verify module attribution for the 39…… range — recorded addresses mix the
/// preferred CryAI base 0x31000000 (FIRST_ENTRY_NOTES.md:721 gives RVA 0xA963) with
/// zone-process load addresses such as 390B30D0 — and recover the callable construction ABI.</item>
/// <item>Native signal dispatch — CAISystem filter-0 send 390B30D0
/// (Npcs/ZoneNpcAiSignals.cs:37) with actor-local admission 3100756C
/// (Npcs/ZoneNpcAiSignals.cs:44-61). TODO(native-ai): same module attribution as the
/// construction entry; both resolve only after the CAISystem instance is obtainable.</item>
/// <item>CryAction behavior dispatch — 30511450 (pending character/behavior constructors,
/// Ai/IZoneNpcAiRuntime.cs:12-15) lives in cryaction_dedicate.dll, loaded only by the
/// owned game-framework path; the engine rejects an unowned cryaction in the CSystem-only mode
/// (Native/Engine/ZoneNativeEngine.cs:411-420).</item>
/// </list>
///
/// Physics-query dependencies the native behavior surface consumes once the entries above
/// resolve; the managed replacement must preserve these reads through IZonePhysicsBackend:
/// <list type="bullet">
/// <item>Interaction distance through the target's physical proxy shapes, never unit centers
/// (Npcs/IZoneNpcWorld.cs:57-59).</item>
/// <item>Terrain height and territory bounds for return/leash checks
/// (Npcs/IZoneNpcWorld.cs:32-34, :59).</item>
/// <item>Spawn admission collision and building ID through AISystem virtual368/virtual258
/// (Npcs/ZoneNpcSpawnCollision.cs:37-39), over the scene's ground query
/// (Physics/IZonePhysicsBackend.cs:97-104).</item>
/// </list>
///
/// Future managed replacement boundary: this single class is the seam. The replacement is the
/// managed provider behind <c>ZoneAiBackendKind.Managed</c> (TODO(managed-ai),
/// Scene/ZoneSceneBackendSelection.cs:25-27); its construct/retire maps to
/// <c>ZoneNpcActorRegistry.Adopt</c>/<c>Remove</c> (Npcs/ZoneNpcActorRegistry.cs:53,
/// :229), its signal path to <c>ZoneNpcAiSignals.Submit/Update</c>
/// (Npcs/ZoneNpcAiSignals.cs:45, :85), and its scheduling to the same pump discipline —
/// with the exclusivity gate above consulted first so neither side runs for the other's NPCs.
/// </summary>
public sealed class ZoneNativeAiProvider : IZoneAiProvider, IZoneAiFramePump, IDisposable
{
    internal const string ConstructDependency = "native NPC AI actor construction";
    internal const string SignalDependency = "native signal dispatch";
    internal const string ActivationDependency = "native activation event 6/7";
    internal const string TargetDependency = "native target binding";
    internal const string UpdateDependency = "native scheduled actor update";

    private static readonly object GlobalGate = new();
    private static readonly Dictionary<ZoneActorHandle, ZoneNativeAiProvider> GlobalBindings = new();

    private readonly IZoneNativeAiActorView _actors;
    private readonly NativeAiAccess _native;
    private readonly Dictionary<ZoneActorHandle, Binding> _bindings = new();
    private bool _disposed;

    public ZoneNativeAiProvider(IZoneNativeAiActorView actors)
    {
        ArgumentNullException.ThrowIfNull(actors);
        _actors = actors;
        _native = new NativeAiAccess();
    }

    /// <summary>
    /// Attaches the scene's neutral collision data to the AI (Agent 7's compatibility route).
    ///
    /// The AI reasons about footing: footstep and impact radii, crouch multipliers, whether a surface
    /// collides at all. All of that is AUTHORED per surface, and the same table the owned player
    /// physics already reads. Sharing the instance is the point - if the AI resolved surfaces
    /// separately, the two consumers would eventually disagree about what the player is standing on.
    ///
    /// Optional by design: a scene without surfaces leaves the AI with no footing answer, which
    /// returns false rather than a made-up surface.
    /// </summary>
    public void AttachCollisionSurfaces(IZoneCollisionSurfaces? surfaces,
        Func<ZoneActorHandle, ushort?>? tryReadGroundMaterial = null)
    {
        _surfaces = surfaces;
        _tryReadGroundMaterial = tryReadGroundMaterial ?? (static _ => null);
    }

    private IZoneCollisionSurfaces? _surfaces;
    private Func<ZoneActorHandle, ushort?> _tryReadGroundMaterial = static _ => null;

    /// <summary>
    /// The authored surface an actor is standing on, resolved through the scene's ONE neutral table.
    /// False means the actor has no ground hit right now (airborne, or terrain that is not loaded) -
    /// never a substituted default pretending to be the real footing.
    /// </summary>
    public bool TryResolveFootingSurface(ZoneActorHandle actor, out ZonePhysicalSurfaceDefinition? surface)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_surfaces is null || _tryReadGroundMaterial(actor) is not { } material)
        {
            surface = null;
            return false;
        }
        surface = _surfaces.Resolve(material);
        return surface is not null;
    }

    /// <summary>The footstep/impact radii the game authored for a surface; false when it authored none.</summary>
    public bool TryResolveFootingAudio(ZoneActorHandle actor, out ZoneSurfaceAiParameters? parameters)
    {
        if (!TryResolveFootingSurface(actor, out var surface))
        {
            parameters = null;
            return false;
        }
        parameters = surface!.Ai;
        return parameters is not null;
    }

    /// <summary>Whether any provider instance of this type currently binds the exact generation.</summary>
    public static bool IsBound(ZoneActorHandle actor)
    {
        lock (GlobalGate) return GlobalBindings.ContainsKey(actor);
    }

    /// <summary>Structured native dependency status; the sole authority on whether a native pass can run.</summary>
    public IReadOnlyList<ZoneNativeAiDependency> ReportDependencies() => _native.Dependencies;

    public void CreateNpc(ZoneActorHandle actor)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var kind = _actors.GetKind(actor); // throws for unknown or retired handles
        if (kind != ZoneActorKind.Npc)
            throw new ArgumentException($"The AI provider binds NPC generations only; handle {actor} is {kind}.", nameof(actor));
        lock (GlobalGate)
        {
            if (_bindings.ContainsKey(actor) || GlobalBindings.ContainsKey(actor))
                throw new InvalidOperationException($"Handle {actor} already has one AI backend bound; one AI backend per NPC.");
        }
        // Admission order: native construction publishes first, the binding only on success,
        // so a failed construct leaves no partial registration (the rollback discipline of
        // ZoneNpcActorRegistry.Adopt, Npcs/ZoneNpcActorRegistry.cs:94-113).
        _native.ConstructNpcActor(actor);
        var binding = new Binding(actor);
        lock (GlobalGate)
        {
            if (_bindings.ContainsKey(actor) || GlobalBindings.ContainsKey(actor))
            {
                _native.DestroyNpcActor(actor);
                throw new InvalidOperationException($"Handle {actor} acquired another AI backend during construction.");
            }
            _bindings.Add(actor, binding);
            GlobalBindings.Add(actor, this);
        }
    }

    public void RemoveNpc(ZoneActorHandle actor)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Binding binding;
        lock (GlobalGate)
        {
            if (!_bindings.TryGetValue(actor, out var found)) return; // idempotent for cleanup paths
            binding = found;
        }
        // A native destroy failure keeps the binding rooted and retryable; uncertain native
        // state never half-retires a generation (the engine's retained-generation policy,
        // Native/Engine/ZoneNativeEngine.cs:157-159). Signal queues live on the native side of a
        // successful construct; the adapter roots none, so retirement leaves no queue behind.
        _native.DestroyNpcActor(actor);
        binding.Period = null;
        lock (GlobalGate)
        {
            if (_bindings.TryGetValue(actor, out var current) && ReferenceEquals(current, binding))
                _bindings.Remove(actor);
            if (GlobalBindings.TryGetValue(actor, out var owner) && ReferenceEquals(owner, this))
                GlobalBindings.Remove(actor);
        }
    }

    public void SendSignal(ZoneActorHandle actor, ZoneAiSignal signal)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(signal);
        var binding = RequireBinding(actor);
        _native.DispatchSignal(actor, binding, signal); // rejected before dispatch when unbound; never buffered
    }

    public void SetActive(ZoneActorHandle actor, bool active)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var binding = RequireBinding(actor);
        if (binding.Active == active) return; // repeated identical states do nothing
        _native.SendActivationEvent(actor, active ? 7 : 6);
        binding.Active = active;
        // Inactive pauses update scheduling but keeps the binding: the cadence re-arms from the
        // first pump after reactivation instead of firing a backlog accumulated while paused.
        if (!active) binding.ScheduleArmed = false;
    }

    public void SetTarget(ZoneActorHandle actor, ZoneActorHandle? target)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var binding = RequireBinding(actor);
        if (target is { } resolved) _actors.GetKind(resolved); // any live actor, including players; retired handles reject
        _native.ApplyTarget(actor, target);
        binding.Target = target; // the write precedes subsequent signal execution
    }

    public void SetUpdateSchedule(ZoneActorHandle actor, TimeSpan? period)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var binding = RequireBinding(actor);
        if (period is { } value)
        {
            if (value <= TimeSpan.Zero || value.TotalMilliseconds > uint.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(period), "The update period must be a positive span fitting non-wrapping milliseconds.");
            binding.Period = value;
            binding.PeriodMilliseconds = (uint)value.TotalMilliseconds;
            binding.ScheduleArmed = false;
        }
        else
        {
            binding.Period = null;
            binding.PeriodMilliseconds = 0;
            binding.ScheduleArmed = false;
        }
    }

    public void Pump(uint timeMilliseconds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (var pair in _bindings.ToArray())
        {
            if (_disposed) return;
            var binding = pair.Value;
            if (!binding.Active || binding.Period is null) continue;
            if (!binding.ScheduleArmed)
            {
                binding.NextDue = unchecked(timeMilliseconds + binding.PeriodMilliseconds);
                binding.ScheduleArmed = true;
                continue;
            }
            // Non-wrapping discipline of ZoneUnitUpdateScheduler (Units/ZoneUnitUpdateScheduler.cs:132-135).
            if (unchecked(timeMilliseconds - binding.NextDue) >= 0x80000000u) continue;
            // Re-validate identity after every native callback: removal during the pump skips the cadence.
            if (!_bindings.TryGetValue(pair.Key, out var current) || !ReferenceEquals(current, binding)) continue;
            _native.UpdateNpcActor(pair.Key);
            if (_disposed || !_bindings.TryGetValue(pair.Key, out current) || !ReferenceEquals(current, binding)) continue;
            binding.NextDue = unchecked(timeMilliseconds + binding.PeriodMilliseconds); // one callback per pump, no backlog burst
        }
    }

    private Binding RequireBinding(ZoneActorHandle actor)
    {
        lock (GlobalGate)
        {
            if (_bindings.TryGetValue(actor, out var binding)) return binding;
        }
        throw new InvalidOperationException($"Handle {actor} has no AI binding on this provider; signals, activation, target and scheduling calls for unbound or retired generations are rejected.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (GlobalGate)
        {
            foreach (var pair in _bindings)
                if (GlobalBindings.TryGetValue(pair.Key, out var owner) && ReferenceEquals(owner, this))
                    GlobalBindings.Remove(pair.Key);
            _bindings.Clear();
        }
    }

    private sealed class Binding(ZoneActorHandle actor)
    {
        public ZoneActorHandle Actor { get; } = actor;
        public bool Active = true;
        public ZoneActorHandle? Target;
        public TimeSpan? Period;
        public uint PeriodMilliseconds;
        public bool ScheduleArmed;
        public uint NextDue;
    }

    /// <summary>
    /// The one place native ABI may exist: module probes, dependency admission and the (still
    /// unwritten) calls. Every operation requires its dependency first, so no native pointer is
    /// ever invoked without recorded evidence, and no unresolved pass can report success.
    /// </summary>
    private sealed class NativeAiAccess
    {
        private readonly nint _aiModule;
        private readonly nint _actionModule;

        internal readonly List<ZoneNativeAiDependency> Dependencies = [];

        internal NativeAiAccess()
        {
            _aiModule = GetModuleHandleW("cryaisystem_dedicate.dll");
            _actionModule = GetModuleHandleW("cryaction_dedicate.dll");
            Dependencies.Add(new("cryaisystem_dedicate.dll module", _aiModule != 0,
                "SHA-256 identity at Native/Engine/ZoneNativeEngine.Identities.cs:14; conditional native loader at Native/Engine/ZoneNativeEngine.cs:171-173."));
            Dependencies.Add(new("CAISystem instance", false,
                "TODO(native-ai): recover the CAISystem instance slot; AI init 31049268 already runs from framework PostInit (FIRST_ENTRY_NOTES.md:722-724, gEnv+8 observed at :766)."));
            Dependencies.Add(new(ConstructDependency, false,
                "TODO(native-ai): recover the callable construction ABI for the 39345000/39348440 chain (Npcs/ZoneNpcSimulation.cs:107, :123) and verify the 39…… module attribution against the preferred CryAI base 0x31000000 (FIRST_ENTRY_NOTES.md:721)."));
            Dependencies.Add(new(SignalDependency, false,
                "TODO(native-ai): CAISystem filter-0 send 390B30D0 and actor admission 3100756C (Npcs/ZoneNpcAiSignals.cs:37, :42) need the instance slot and module attribution."));
            Dependencies.Add(new(ActivationDependency, false,
                "TODO(native-ai): IAIObject::Event 6/7 dispatch needs the CAISystem instance; virtual layout known from Npcs/IZoneNpcActorScene.cs:36 and Npcs/ZoneNpcSpawnCollision.cs:37-39."));
            Dependencies.Add(new(TargetDependency, false,
                "TODO(native-ai): native attention-target write needs the CAISystem instance and the actor handle mapping recovered with " + ConstructDependency + "."));
            Dependencies.Add(new(UpdateDependency, false,
                "TODO(native-ai): per-generation actor update callback needs the CAISystem proxy entry; CAIProxy alertness read is CryAction 30519920 (Npcs/IZoneNpcActorScene.cs:31), owned-framework module only."));
            Dependencies.Add(new("CryAction behavior dispatch (30511450)", _actionModule != 0 && false,
                $"cryaction_dedicate.dll module: {(_actionModule != 0 ? "loaded by the owned game framework" : "absent (CSystem-only mode; rejected when unowned, Native/Engine/ZoneNativeEngine.cs:411-420)")}; TODO(native-ai): behavior dispatch ABI 30511450 (Ai/IZoneNpcAiRuntime.cs:13) after the actor chain exists."));
        }

        private NotSupportedException Blocked(string operation, params string[] required)
        {
            var missing = Dependencies.Where(dependency => required.Contains(dependency.Name) && !dependency.IsResolved)
                .Select(dependency => $"{dependency.Name} [{dependency.Evidence}]");
            return new NotSupportedException($"{operation} is blocked at unresolved native dependencies: {string.Join(" | ", missing)}");
        }

        internal void ConstructNpcActor(ZoneActorHandle actor)
        {
            Require("Native NPC actor construction", ConstructDependency, "CAISystem instance", "cryaisystem_dedicate.dll module");
            throw new InvalidOperationException($"The dependency table reports {ConstructDependency} resolved but no wired native call exists; complete the TODO(native-ai) call site before flipping IsResolved.");
        }

        internal void DestroyNpcActor(ZoneActorHandle actor)
        {
            Require("Native NPC actor retirement", ConstructDependency, "CAISystem instance", "cryaisystem_dedicate.dll module");
            throw new InvalidOperationException($"The dependency table reports {ConstructDependency} resolved but no wired native call exists; complete the TODO(native-ai) call site before flipping IsResolved.");
        }

        internal void DispatchSignal(ZoneActorHandle actor, Binding binding, ZoneAiSignal signal)
        {
            Require("Native signal dispatch", SignalDependency, "CAISystem instance", "cryaisystem_dedicate.dll module");
            throw new InvalidOperationException($"The dependency table reports {SignalDependency} resolved but no wired native call exists; complete the TODO(native-ai) call site before flipping IsResolved.");
        }

        internal void SendActivationEvent(ZoneActorHandle actor, int eventId)
        {
            Require($"Native activation event {eventId}", ActivationDependency, "CAISystem instance", "cryaisystem_dedicate.dll module");
            throw new InvalidOperationException($"The dependency table reports {ActivationDependency} resolved but no wired native call exists; complete the TODO(native-ai) call site before flipping IsResolved.");
        }

        internal void ApplyTarget(ZoneActorHandle actor, ZoneActorHandle? target)
        {
            Require("Native target binding", TargetDependency, "CAISystem instance", "cryaisystem_dedicate.dll module");
            throw new InvalidOperationException($"The dependency table reports {TargetDependency} resolved but no wired native call exists; complete the TODO(native-ai) call site before flipping IsResolved.");
        }

        internal void UpdateNpcActor(ZoneActorHandle actor)
        {
            Require("Native scheduled actor update", UpdateDependency, "CAISystem instance", "cryaisystem_dedicate.dll module");
            throw new InvalidOperationException($"The dependency table reports {UpdateDependency} resolved but no wired native call exists; complete the TODO(native-ai) call site before flipping IsResolved.");
        }

        private void Require(string operation, params string[] required)
        {
            if (required.Any(name => Dependencies.All(dependency => dependency.Name != name || !dependency.IsResolved)))
                throw Blocked(operation, required);
        }

        [DllImport("kernel32", CharSet = CharSet.Unicode)]
        private static extern nint GetModuleHandleW(string? name);
    }
}

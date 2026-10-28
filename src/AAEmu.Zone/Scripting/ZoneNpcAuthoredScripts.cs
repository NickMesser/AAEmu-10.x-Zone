using AAEmu.Zone.Ai;
using AAEmu.Zone.Content;

namespace AAEmu.Zone.Scripting;

public sealed record ZoneNpcScriptBootstrapReport(bool Ready, IReadOnlyList<string> RequiredScripts, string? Refusal);

/// <summary>Copied progress through authored script initialization, separate from gameplay service admission.</summary>
public sealed record ZoneNpcAuthoredScriptReadiness(bool EntityCreated, bool EntityInitialized,
    bool CharacterConstructed, bool ParametersApplied, bool BehaviorConstructed, bool SignalReady,
    string? Refusal)
{
    /// <summary>Script initialization alone cannot admit perception, goal execution, navigation or combat.</summary>
    public bool GameplayReady => false;
}

/// <summary>
/// Executes the original NPC bootstrap and character scripts. The caller supplies actual engine-free
/// API bindings and an authored source-actor table; missing methods remain script errors. This owner
/// does not install successful placeholders or replace authored callbacks with managed decisions.
/// </summary>
public sealed class ZoneNpcAuthoredScripts : IDisposable
{
    private static readonly string[] BootstrapScripts =
        ["scripts/common.lua", "scripts/ai/aiconfig.lua", "Scripts/Entities/Actor/npc.lua"];
    private readonly IZoneLuaRuntime _lua;
    private readonly ZoneScriptLoader _loader;
    private readonly ZoneNpcContent _content;
    private readonly HashSet<ZoneNpcAuthoredEntity> _entities = [];
    private uint _nextClass;
    private bool _bootstrapAttempted;
    public bool IsDisposed { get; private set; }
    public ZoneNpcScriptBootstrapReport BootstrapReport { get; private set; } =
        new(false, Array.AsReadOnly(BootstrapScripts), "Authored NPC bootstrap has not run.");

    /// <summary>All inputs remain borrowed. Dispose prepared entities before disposing their Lua runtime.</summary>
    public ZoneNpcAuthoredScripts(IZoneLuaRuntime lua, ZoneScriptLoader loader, ZoneNpcContent content)
    {
        _lua = lua ?? throw new ArgumentNullException(nameof(lua));
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));
        _content = content ?? throw new ArgumentNullException(nameof(content));
        ObjectDisposedException.ThrowIf(lua.IsDisposed, lua);
    }

    /// <summary>
    /// Run after installing constants, Script bindings, and the recovered AI/System/entity services.
    /// Nested LoadAll failures cannot produce readiness merely because the outer chunk returned.
    /// Globals from a refused run may exist, as with the original script loader; Ready stays false.
    /// </summary>
    public ZoneNpcScriptBootstrapReport Bootstrap()
    {
        CheckAlive();
        if (BootstrapReport.Ready) return BootstrapReport;
        var retry = _bootstrapAttempted;
        _bootstrapAttempted = true;
        var phase = "bootstrap";
        try
        {
            foreach (var script in BootstrapScripts)
            {
                phase = script;
                var failures = _loader.FailureCount;
                // A failed nested load does not invalidate the original loader's outer-file cache.
                // Retry the complete required root so repairing a dependency actually reruns it.
                var executed = _loader.ExecuteFile(script, forceReload: retry);
                if (_loader.FailureCount != failures)
                    throw new ZoneLuaException(_loader.LastFailure ?? $"A nested dependency of '{script}' failed.");
                if (!executed) throw new ZoneLuaException($"Required script '{script}' failed to execute; consult the script error channel.");
                CheckAlive();
            }
            phase = "required NPC tables and callbacks";
            // Structural validation only: all behavior is still read from the authored files.
            // AIConfig calls both LoadAll functions, including AVAILABLE and INTERNAL maps.
            using var validation = _lua.Execute("""
                local function need(t,k,kind,label)
                    if type(t)~='table' or type(t[k])~=kind then
                        error('Missing authored NPC dependency: '..label,0)
                    end
                    return t[k]
                end
                if type(AICharacter)~='table' or type(AIBehaviour)~='table' then
                    error('Missing authored AI registry',0)
                end
                for _,root in ipairs({AICharacter,AIBehaviour}) do
                    if type(root)~='table' then error('Missing authored AI registry',0) end
                    for _,key in ipairs({'AVAILABLE','INTERNAL'}) do
                        local map=need(root,key,'table','AI registry.'..key)
                        for name,filename in pairs(map) do
                            need(root,name,'table',tostring(filename)..' -> '..tostring(name))
                        end
                    end
                    need(root,'DEFAULT','table','AI registry.DEFAULT')
                end
                local template=need(npc,'ActorTemplate','table','npc.ActorTemplate')
                need(npc,'Create','function','npc.Create')
                need(_G,'new','function','common.new')
                need(BasicActor,'ResetCommon','function','BasicActor.ResetCommon')
                need(BasicAI,'OnReset','function','BasicAI.OnReset')
                local server=need(template,'Server','table','npc.ActorTemplate.Server')
                need(server,'OnInit','function','npc.ActorTemplate.Server.OnInit')
                local commands=need(X2AI,'commandSet','table','X2AI.commandSet')
                need(commands,'CanRunCommandSet','function','X2AI.commandSet.CanRunCommandSet')
                need(commands,'RunCommandSet','function','X2AI.commandSet.RunCommandSet')
                need(commands,'EndCommandSet','function','X2AI.commandSet.EndCommandSet')
                """, "=zone-npc-bootstrap-validation");
            CheckAlive();
            BootstrapReport = new(true, Array.AsReadOnly(BootstrapScripts), null);
        }
        catch (Exception error) when (IsScriptFailure(error))
        {
            BootstrapReport = new(false, Array.AsReadOnly(BootstrapScripts), $"Authored NPC {phase}: {error.Message}");
        }
        return BootstrapReport;
    }

    /// <summary>
    /// An owned reference to the original generic template, for an explicitly scoped script-only
    /// profile. This does not supply a model's authored actor script or make an NPC gameplay-ready.
    /// </summary>
    public ZoneLuaReference RetainGenericActorTemplate()
    {
        RequireBootstrap();
        using var npc = GlobalTable("npc");
        return Table(npc, "ActorTemplate", "npc.ActorTemplate");
    }

    /// <summary>
    /// Phase one: original npc:Create plus common.new creates a fresh entity table. No owner
    /// constructor runs. Bind the entity/actor/unit APIs, adopt it into the scene registry, then
    /// register <see cref="ZoneNpcAuthoredEntity.DetachBehavior"/> before calling Initialize.
    /// sourceActor is borrowed and must be the resolved authored actor/model table, or the
    /// explicitly scoped generic template above; its source is never inferred from a mesh path.
    /// </summary>
    public ZoneNpcAuthoredEntity Prepare(uint npcTemplateId, ZoneLuaHandle entityHandle,
        ZoneLuaReference sourceActor, IZoneNpcBehaviorServices services) =>
        PrepareCore(npcTemplateId, entityHandle, sourceActor, services, false);

    /// <summary>
    /// Clone a resolved, cached native NPC class through original common.new. Its model-class
    /// resolver already ran npc:Create("npc_"..model.Name,sourceActor); do not invoke it again.
    /// Ownership and the adopt/register/initialize sequence match Prepare.
    /// </summary>
    public ZoneNpcAuthoredEntity PrepareFromClass(uint npcTemplateId, ZoneLuaHandle entityHandle,
        ZoneLuaReference sourceNpcClass, IZoneNpcBehaviorServices services) =>
        PrepareCore(npcTemplateId, entityHandle, sourceNpcClass, services, true);

    private ZoneNpcAuthoredEntity PrepareCore(uint npcTemplateId, ZoneLuaHandle entityHandle,
        ZoneLuaReference sourceActor, IZoneNpcBehaviorServices services, bool fromNpcClass)
    {
        RequireBootstrap();
        ArgumentNullException.ThrowIfNull(sourceActor); ArgumentNullException.ThrowIfNull(services);
        ValidateTable(sourceActor, nameof(sourceActor));
        if (entityHandle.Value == 0 || entityHandle.Value > uint.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(entityHandle), "NPC entity handles must be nonzero native uint IDs.");
        if (!_content.Templates.TryGetValue(npcTemplateId, out var definition))
            throw new InvalidDataException($"Authored NPC template {npcTemplateId} is not present in the content catalog.");
        // 3972E570 uses 'default' if the AI file descriptor cannot resolve.
        var characterName = ZoneAiSignalName.CString(_content.AiFiles.GetValueOrDefault(definition.AiFileId)?.Name ?? "default");
        var parameterText = _content.ParametersForNpc(npcTemplateId)?.Text;
        using var characters = GlobalTable("AICharacter");
        using var behaviors = GlobalTable("AIBehaviour");
        using var character = ResolveTable(characters, characterName, "character");
        using var transitions = Table(character, "AnyBehavior", $"AICharacter.{characterName}.AnyBehavior");
        var first = InitialBehavior(transitions, characterName);
        using var defaultBehavior = ResolveTable(behaviors, first, "initial behavior");
        using var defaultCharacter = Table(characters, "DEFAULT", "AICharacter.DEFAULT");
        using var fallbackBehavior = Table(behaviors, "DEFAULT", "AIBehaviour.DEFAULT");
        using var copy = GlobalFunction("new");
        var temporaryName = $"__aaemu_npc_class_{entityHandle.Value}_{++_nextClass}";
        var previous = fromNpcClass ? null : _lua.GetGlobal(temporaryName);
        ZoneLuaReference? entity = null;
        ZoneNpcBehavior? behavior = null;
        var phase = "npc.Create";
        try
        {
            ZoneLuaReference generated;
            if (fromNpcClass) generated = sourceActor.Retain();
            else
            {
                using var npc = GlobalTable("npc");
                using var create = Function(npc, "Create", "npc.Create");
                _lua.Invoke(create, npc, temporaryName, sourceActor);
                CheckAlive();
                phase = "generated actor class";
                generated = GlobalTable(temporaryName);
            }
            using var generatedOwner = generated;
            phase = "common.new";
            using var copied = _lua.Call(copy, generated);
            if (copied.Count != 1 || copied[0] is not ZoneLuaReference { IsTable: true } table)
                throw new ZoneLuaException("Authored common.new did not return one NPC entity table.");
            entity = table.Retain();
            phase = "entity identity";
            entity.Set("id", entityHandle);
            entity.Set("StartupBehaviour", first);
            var errors = new ZoneNpcAuthoredBehaviorServices(services);
            behavior = new(_lua, entity, behaviors, character, defaultCharacter, fallbackBehavior, first, first, errors);
            var prepared = new ZoneNpcAuthoredEntity(this, entity, behavior, errors, npcTemplateId, characterName, first, parameterText);
            _entities.Add(prepared);
            entity = null; behavior = null;
            return prepared;
        }
        catch (Exception error) when (IsScriptFailure(error))
        { throw new ZoneLuaException($"Authored NPC {npcTemplateId} character '{characterName}' {phase}: {error.Message}"); }
        finally
        {
            behavior?.Dispose(); entity?.Dispose();
            if (!fromNpcClass && !_lua.IsDisposed) _lua.SetGlobal(temporaryName, previous);
            (previous as ZoneLuaReference)?.Dispose();
        }
    }

    /// <summary>
    /// Phase two, after scene adoption and behavior registration. Execute the original server
    /// OnInit/OnReset chain, immediate character constructor, authored parameter closure, and
    /// initial behavior constructor in that order. A failed attempt cannot be retried on this
    /// partially initialized owner. Retire it and prepare a fresh generation instead.
    /// initializationError may observe this owner's error channel: original SetParamX2 logs
    /// System.Error and returns normally on invalid parameter source. Report that error here
    /// to refuse readiness without replacing the authored validation/error branch.
    /// </summary>
    public ZoneNpcAuthoredScriptReadiness Initialize(ZoneNpcAuthoredEntity entity, Func<bool> current,
        Func<string?>? initializationError = null, Action? afterEntityInitialization = null)
    {
        RequireBootstrap();
        ArgumentNullException.ThrowIfNull(entity); ArgumentNullException.ThrowIfNull(current);
        if (!ReferenceEquals(entity.Owner, this)) throw new ArgumentException("The entity belongs to another script owner.", nameof(entity));
        ObjectDisposedException.ThrowIf(entity.IsDisposed, entity);
        if (entity.InitializationAttempted) throw new InvalidOperationException("This NPC script initialization already ran.");
        entity.InitializationAttempted = true;
        var phase = "entity Server.OnInit";
        try
        {
            CheckCurrent();
            using (var server = Table(entity.EntityScript, "Server", "entity.Server"))
            using (var initialize = Function(server, "OnInit", "entity.Server.OnInit"))
                _lua.Invoke(initialize, entity.EntityScript);
            CheckCurrent(); CheckCallbackErrors();
            using (var ai = Table(entity.EntityScript, "AI", "entity.AI after Server.OnInit"))
            using (var commands = Table(ai, "commandSet", "entity.AI.commandSet after Server.OnInit"))
            using (var canRun = Function(commands, "CanRunCommandSet", "entity.AI.commandSet.CanRunCommandSet")) { }
            entity.Progress = entity.Progress with { EntityInitialized = true };

            phase = "authored NPC child installation";
            afterEntityInitialization?.Invoke();
            CheckCurrent(); CheckCallbackErrors();

            phase = $"AICharacter.{entity.CharacterName}.Constructor";
            entity.Behavior.QueueCharacterConstructor();
            entity.Behavior.RunPendingConstructors();
            CheckCurrent(); CheckCallbackErrors();
            entity.Progress = entity.Progress with { CharacterConstructed = true };

            phase = "entity.AI.SetParamX2";
            if (!string.IsNullOrEmpty(entity.ParameterText))
            {
                using var ai = Table(entity.EntityScript, "AI", "entity.AI");
                using var setter = Function(ai, "SetParamX2", "entity.AI.SetParamX2");
                _lua.Invoke(setter, ai, entity.ParameterText);
            }
            CheckCurrent(); CheckCallbackErrors();
            entity.Progress = entity.Progress with { ParametersApplied = true };

            phase = $"AIBehaviour.{entity.InitialBehaviorName}.Constructor";
            entity.Behavior.SetBehavior(entity.InitialBehaviorName);
            CheckCurrent(); CheckCallbackErrors();
            using (var selected = entity.Behavior.RetainCurrent())
                if (selected is null) throw new ZoneLuaException("The initial behavior table did not resolve.");
            entity.Progress = entity.Progress with { BehaviorConstructed = true, SignalReady = true };
        }
        catch (Exception error) when (IsScriptFailure(error))
        {
            entity.Progress = entity.Progress with
            { SignalReady = false, Refusal = $"Authored NPC {entity.NpcTemplateId} {phase}: {error.Message}" };
        }
        return entity.Readiness;

        void CheckCurrent()
        {
            CheckAlive();
            if (entity.IsDisposed || entity.Behavior.IsDisposed || !current() || entity.IsDisposed || entity.Behavior.IsDisposed)
                throw new ZoneLuaException("The NPC script generation retired during initialization.");
        }
        void CheckCallbackErrors()
        {
            if (entity.Errors.FirstError is { } error) throw new ZoneLuaException(error);
            if (initializationError?.Invoke() is { } reported) throw new ZoneLuaException(reported);
        }
    }

    private string InitialBehavior(ZoneLuaReference transitions, string character)
    {
        if (Text(transitions, "GO_TO_SPAWN") is { } spawning) return spawning;
        if (Text(transitions, "GO_TO_IDLE") is { } idle) return idle;
        // The native final fallback takes the first table entry. Keep the VM's actual traversal
        // rather than imposing an alphabetical managed order on this uncommon authored case.
        using var first = _lua.Execute("return function(t) for _,v in pairs(t) do return v end end", "=zone-npc-first-transition");
        using var result = _lua.Call((ZoneLuaReference)first[0]!, transitions);
        if (result.Count > 0 && result[0] is string name) return ZoneAiSignalName.CString(name);
        throw new ZoneLuaException($"AICharacter.{character} has no initial behavior transition.");
    }

    private ZoneLuaReference ResolveTable(ZoneLuaReference registry, string name, string kind)
    {
        if (_lua.Get(registry, name, 5) is ZoneLuaReference found) return found;
        using var available = _lua.Get(registry, "AVAILABLE", 5) as ZoneLuaReference;
        using var lookup = available is null ? _lua.Get(registry, "INTERNAL", 5) as ZoneLuaReference : available.Retain();
        var filename = lookup is null ? null : Text(lookup, name);
        if (filename is null || !_loader.ExecuteFile(filename))
            throw new ZoneLuaException($"Cannot load authored {kind} '{name}'{(filename is null ? " (no registry filename)" : $" from '{filename}'")}.");
        return _lua.Get(registry, name, 5) as ZoneLuaReference ??
            throw new ZoneLuaException($"Authored {kind} '{name}' did not define its table in '{filename}'.");
    }

    private string? Text(ZoneLuaReference table, string key) =>
        _lua.Get(table, key, 4) is string text ? ZoneAiSignalName.CString(text) : null;
    private ZoneLuaReference Table(ZoneLuaReference table, string key, string label) =>
        _lua.Get(table, key, 5) as ZoneLuaReference ?? throw new ZoneLuaException($"Missing authored NPC table: {label}.");
    private ZoneLuaReference Function(ZoneLuaReference table, string key, string label) =>
        _lua.Get(table, key, 6) as ZoneLuaReference ?? throw new ZoneLuaException($"Missing authored NPC callback: {label}.");
    private ZoneLuaReference GlobalTable(string name) => Global(name, true);
    private ZoneLuaReference GlobalFunction(string name) => Global(name, false);
    private ZoneLuaReference Global(string name, bool table)
    {
        var value = _lua.GetGlobal(name);
        if (value is ZoneLuaReference reference && reference.IsTable == table) return reference;
        (value as ZoneLuaReference)?.Dispose();
        throw new ZoneLuaException($"Missing authored NPC {(table ? "table" : "callback")}: {name}.");
    }
    private void ValidateTable(ZoneLuaReference table, string name)
    {
        ObjectDisposedException.ThrowIf(table.IsDisposed, table);
        if (!table.IsTable || !ReferenceEquals(table.Runtime, _lua)) throw new ArgumentException("Supply a live table from this script runtime.", name);
    }
    private void RequireBootstrap()
    {
        CheckAlive();
        if (!BootstrapReport.Ready) throw new InvalidOperationException(BootstrapReport.Refusal);
    }
    private void CheckAlive()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this); ObjectDisposedException.ThrowIf(_lua.IsDisposed, _lua);
    }
    private static bool IsScriptFailure(Exception error) => error is ZoneLuaException or InvalidDataException or NotSupportedException or IOException or ObjectDisposedException;
    internal void Forget(ZoneNpcAuthoredEntity entity) => _entities.Remove(entity);

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        foreach (var entity in _entities.ToArray()) entity.Dispose();
        _entities.Clear();
    }
}

/// <summary>One fresh script entity and its construction progress. References are borrowed unless explicitly retained.</summary>
public sealed class ZoneNpcAuthoredEntity : IDisposable
{
    internal ZoneNpcAuthoredScripts Owner { get; }
    internal ZoneNpcAuthoredBehaviorServices Errors { get; }
    internal string? ParameterText { get; }
    internal bool InitializationAttempted { get; set; }
    internal ZoneNpcAuthoredScriptReadiness Progress { get; set; } = new(true, false, false, false, false, false, null);
    private bool _ownsBehavior = true;
    public uint NpcTemplateId { get; }
    public string CharacterName { get; }
    public string InitialBehaviorName { get; }
    public ZoneLuaReference EntityScript { get; }
    public ZoneNpcBehavior Behavior { get; }
    public bool IsDisposed { get; private set; }
    public long ScriptErrorCount => Errors.ErrorCount;
    public string? FirstScriptError => Errors.FirstError;
    public ZoneNpcAuthoredScriptReadiness Readiness => IsDisposed || EntityScript.IsDisposed || Behavior.IsDisposed
        ? Progress with { SignalReady = false, Refusal = Progress.Refusal ?? "The NPC script generation is retired." } : Progress;

    internal ZoneNpcAuthoredEntity(ZoneNpcAuthoredScripts owner, ZoneLuaReference entity, ZoneNpcBehavior behavior,
        ZoneNpcAuthoredBehaviorServices errors, uint npcTemplateId, string character, string initialBehavior, string? parameterText)
    {
        Owner = owner; EntityScript = entity; Behavior = behavior; Errors = errors;
        NpcTemplateId = npcTemplateId; CharacterName = character; InitialBehaviorName = initialBehavior; ParameterText = parameterText;
    }

    /// <summary>Transfers behavior ownership exactly once, before registering it and running constructors.</summary>
    public ZoneNpcBehavior DetachBehavior()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (!_ownsBehavior) throw new InvalidOperationException("The behavior ownership already transferred.");
        _ownsBehavior = false;
        return Behavior;
    }

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        try { if (_ownsBehavior) Behavior.Dispose(); }
        finally { EntityScript.Dispose(); Owner.Forget(this); }
    }
}

/// <summary>Retains constructor failures which CAIHandler otherwise reports through a nonthrowing error channel.</summary>
internal sealed class ZoneNpcAuthoredBehaviorServices(IZoneNpcBehaviorServices inner) : IZoneNpcBehaviorServices
{
    public string? FirstError { get; private set; }
    public long ErrorCount { get; private set; }
    public bool HasActor => inner.HasActor;
    public void LoadScript(string filename) => inner.LoadScript(filename);
    public void ActorEvent(int kind, string name) => inner.ActorEvent(kind, name);
    public void RecordEvent(int kind, string name) => inner.RecordEvent(kind, name);
    public void ResetPipeUserAfterBehaviorExit() => inner.ResetPipeUserAfterBehaviorExit();
    public void ChangeSmartObjectState(string change) => inner.ChangeSmartObjectState(change);
    public void NotifyAlert() => inner.NotifyAlert();
    public void NotifyActionDone() => inner.NotifyActionDone();
    public void ScriptError(string callback, ZoneLuaException error)
    {
        ErrorCount++;
        FirstError ??= $"{callback}: {error.Message}";
        inner.ScriptError(callback, error);
    }
}

using System.Globalization;
using System.Text;
using AAEmu.Zone.Ai;
using AAEmu.Zone.Content;

namespace AAEmu.Zone.Scripting;

/// <summary>
/// The database-generated ActorModel script path in CEntityScript 320293A0, followed by
/// CEntityClass's original npc:Create call (32094AE0/3202C5C0). This owns class references,
/// not the borrowed VM or per-NPC entity tables. No actor asset path is inferred from a mesh.
/// </summary>
public sealed class ZoneNpcActorModelScripts : IDisposable
{
    private sealed class ModelClass(uint modelId, uint actorModelId, ZoneLuaReference source)
    {
        public uint ModelId { get; } = modelId;
        public uint ActorModelId { get; } = actorModelId;
        public ZoneLuaReference Source { get; } = source;
        public ZoneLuaReference? Npc { get; set; }
    }

    // Native source-vector positions, not StanceId lookup or stance-ID sorting.
    private static readonly (string Name, int Position)[] MovementGroups =
        [("Relaxed", 1), ("Combat", 0), ("Stealth", 5), ("Crouch", 6), ("Prone", 7), ("Swim", 2), ("Fly", 8)];
    private readonly IZoneLuaRuntime _lua;
    private readonly Dictionary<string, ModelClass> _classes = new(StringComparer.Ordinal);
    public bool IsDisposed { get; private set; }
    public int Count => _classes.Count;

    public ZoneNpcActorModelScripts(IZoneLuaRuntime lua)
    {
        _lua = lua ?? throw new ArgumentNullException(nameof(lua));
        ObjectDisposedException.ThrowIf(lua.IsDisposed, lua);
    }

    /// <summary>
    /// Retain the real model class generated from its matched database actor row. The native
    /// file-script fallback and incomplete stance-vector cases remain explicit refusals.
    /// </summary>
    public ZoneLuaReference RetainSourceActor(ZoneModelDefinition model, ZoneActorModel actor) =>
        Source(model, actor).Source.Retain();

    /// <summary>
    /// Retain the cached npc_ class after executing original npc:Create exactly once for this
    /// model. Clone this class with the authored common.new for each new NPC entity generation.
    /// </summary>
    public ZoneLuaReference RetainNpcClass(ZoneModelDefinition model, ZoneActorModel actor)
    {
        var source = Source(model, actor);
        if (source.Npc is not null) return source.Npc.Retain();
        var className = NpcClassName(model);
        using var npc = _lua.GetGlobal("npc") as ZoneLuaReference;
        if (npc is not { IsTable: true }) throw new ZoneLuaException("Original npc class must be bootstrapped before resolving a model NPC class.");
        using var create = _lua.Get(npc, "Create", 6) as ZoneLuaReference;
        if (create is null) throw new ZoneLuaException("Original npc.Create is unavailable.");
        _lua.Invoke(create, npc, className, source.Source);
        CheckAlive();
        ZoneLuaReference? created = _lua.GetGlobal(className) as ZoneLuaReference;
        try
        {
            if (created is not { IsTable: true }) throw new ZoneLuaException($"Original npc.Create did not publish class '{className}'.");
            CheckAlive();
            source.Npc = created;
            created = null;
            return source.Npc.Retain();
        }
        finally { created?.Dispose(); }
    }

    public static string NpcClassName(ZoneModelDefinition model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var name = "npc_" + CString(model.Name);
        if (Encoding.UTF8.GetByteCount(name) >= 128)
            throw new NotSupportedException($"Model {model.Id} NPC class name exceeds the native 127-byte class-clone buffer.");
        return name;
    }

    private ModelClass Source(ZoneModelDefinition model, ZoneActorModel actor)
    {
        CheckAlive();
        ArgumentNullException.ThrowIfNull(model); ArgumentNullException.ThrowIfNull(actor);
        if (model.Kind != ZoneModelKind.Actor || model.SubtypeId != actor.Id)
            throw new ArgumentException("The model must resolve the supplied ActorModel database row.", nameof(actor));
        var name = CString(model.Name);
        if (_classes.TryGetValue(name, out var existing))
        {
            if (existing.ModelId != model.Id || existing.ActorModelId != actor.Id)
                throw new NotSupportedException($"Model class '{name}' is already owned by model {existing.ModelId}/actor {existing.ActorModelId}; duplicate class registration is not resolved by this owner.");
            return existing;
        }
        var source = CreateSourceActorScript(model, actor);
        using var executed = _lua.Execute(source, $"=Actor script table: {name}");
        CheckAlive();
        ZoneLuaReference? table = _lua.GetGlobal(name) as ZoneLuaReference;
        try
        {
            if (table is not { IsTable: true }) throw new ZoneLuaException($"Generated ActorModel class '{name}' was not published as a table.");
            CheckAlive();
            var entry = new ModelClass(model.Id, actor.Id, table);
            _classes.Add(name, entry);
            table = null;
            return entry;
        }
        finally { table?.Dispose(); }
    }

    internal static string CreateSourceActorScript(ZoneModelDefinition model, ZoneActorModel actor)
    {
        var name = CString(model.Name);
        if (!IsIdentifier(name))
            throw new NotSupportedException($"Model {model.Id} name '{name}' is not supported by the native generated Lua identifier path.");
        // The native check is >=8, but its final group dereferences element8. Refuse the
        // malformed eight-row case instead of copying an out-of-bounds native read.
        if (actor.Stances.Count < 9)
            throw new NotSupportedException($"Model {model.Id}/actor {actor.Id} has {actor.Stances.Count} stance rows; the native generated actor script requires source positions 0..8. The file-script fallback is not installed.");
        var definition = actor.Definition;
        var text = new StringBuilder();
        text.Append(name).Append(" = { AnimationGraph = \"").Append(CString(definition.AnimationGraph))
            .Append("\", UpperBodyGraph = \"").Append(CString(definition.UpperbodyGraph))
            .Append("\", Properties = { fileModel = \"").Append(CString(definition.ModelFile))
            .Append("\", clientFileModel = \"").Append(CString(definition.ModelFile))
            .Append("\" }, AIMovementAbility = { AIMovementSpeeds = {");
        foreach (var (group, position) in MovementGroups)
        {
            var stance = actor.Stances[position];
            text.Append(group).Append(" = {");
            AppendSpeed("Slow", stance.AiMoveSpeedSlow, true);
            AppendSpeed("Walk", stance.AiMoveSpeedWalk, true);
            AppendSpeed("Run", stance.AiMoveSpeedRun, false);
            AppendSpeed("Sprint", stance.AiMoveSpeedSprint, false);
            text.Append("},");
        }
        text.Append("}, }, }");
        var result = text.ToString();
        if (Encoding.UTF8.GetByteCount(result) >= 4096)
            throw new NotSupportedException($"Model {model.Id} generated actor script exceeds the native 4095-byte formatter buffer.");
        return result;

        void AppendSpeed(string speedName, float value, bool expandMaximum)
        {
            // Scalar float multiplication occurs before C varargs promotes to double; %f
            // then serializes six decimal places before the Lua parser reads the number.
            text.Append(speedName).Append(" = {").Append(Format(value)).Append(',')
                .Append(Format(value * .8f)).Append(',').Append(Format(expandMaximum ? value * 1.2f : value)).Append("},");
        }
    }

    private static string Format(float value)
    {
        if (!float.IsFinite(value)) throw new NotSupportedException("Nonfinite authored movement speeds are not supported by generated ActorModel scripts.");
        return ((double)value).ToString("F6", CultureInfo.InvariantCulture);
    }
    private static bool IsIdentifier(string name) => name.Length > 0 &&
        (name[0] is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_') &&
        name.All(character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_');
    private static string CString(string value) => ZoneAiSignalName.CString(value);
    private void CheckAlive()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        ObjectDisposedException.ThrowIf(_lua.IsDisposed, _lua);
    }
    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        var classes = _classes.Values.ToArray(); _classes.Clear();
        foreach (var entry in classes) { entry.Npc?.Dispose(); entry.Source.Dispose(); }
    }
}

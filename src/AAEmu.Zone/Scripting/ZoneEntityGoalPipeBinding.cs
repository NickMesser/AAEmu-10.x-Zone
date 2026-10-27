using AAEmu.Zone.Ai;
using AAEmu.Zone.Combat;

namespace AAEmu.Zone.Scripting;

/// <summary>Native entity SelectPipe, InsertSubpipe, CancelSubpipe and IsUsingPipe Lua methods.</summary>
public sealed class ZoneEntityGoalPipeBinding
{
    private readonly IZoneEntityGoalPipeServices _services;

    /// <summary>Installs shared methods; each call resolves its receiver's current entity handle.</summary>
    public ZoneEntityGoalPipeBinding(ZoneLuaReference methods, IZoneEntityGoalPipeServices services)
    {
        ArgumentNullException.ThrowIfNull(methods);
        _services = services ?? throw new ArgumentNullException(nameof(services));
        methods.Runtime.WithStack(() =>
        {
            Bind("SelectPipe", args => SelectOrInsert(args, false));
            Bind("InsertSubpipe", args => SelectOrInsert(args, true));
            Bind("CancelSubpipe", Cancel); Bind("IsUsingPipe", IsUsing);
            return 0;
        });
        void Bind(string name, Func<IReadOnlyList<object?>, object?[]> callback)
        { using var function = methods.Runtime.CreateFunction(callback); methods.Set(name, function); }
    }

    // 3202E630: self is a table containing a handle-valued id, or the handle itself. Numbers are not handles.
    private IZoneGoalPipeScriptEntity? Entity(IReadOnlyList<object?> args)
    {
        var receiver = Value(args, 0);
        if (receiver is ZoneLuaReference { IsTable: true } table) receiver = table.Runtime.Get(table, "id", 2);
        return receiver is ZoneLuaHandle handle ? _services.FindEntity(unchecked((uint)handle.Value)) : null;
    }

    private ZoneGoalPipeController? PipeUser(object? aiObject) => aiObject is null ? null : _services.GetPipeUser(aiObject);

    // 32032590 / 32032950. The caller's fourth parameter is an event ID, never a force flag.
    private object?[] SelectOrInsert(IReadOnlyList<object?> args, bool insert)
    {
        var entity = Entity(args);
        if (entity is null) return [];
        var actor = entity.GetAiObject();
        var method = insert ? "InsertSubpipe" : "SelectPipe";
        if (args.Count < 3)
        { _services.Warning($"{method}: too few parameters"); return []; }
        if (Number(Value(args, 1)) is not { } flags || Value(args, 2) is not string name)
        { _services.Warning($"{method}: wrong parameter type"); return []; }
        var target = Value(args, 3);
        // Native passes null to strcmp or reads uninitialized stack data on malformed arguments.
        // Reject those calls before scene or controller mutation instead of reproducing memory faults.
        if (target is not (null or string or long or double or ZoneLuaHandle))
        { _services.Warning($"{method}: target must be a name, entity handle, number or nil"); return []; }
        name = ZoneAiSignalName.CString(name);
        var argument = ResolveTarget(actor, target);
        var eventId = Number(Value(args, 4)) is { } number ? ZoneStatMath.ToInt32(number) : 0;
        var pipe = PipeUser(actor);
        if (insert) return [pipe?.Insert((ZoneGoalPipeFlags)ZoneStatMath.ToInt32(flags), name, argument, eventId) is not null];
        if (pipe?.Select((ZoneGoalPipeFlags)ZoneStatMath.ToInt32(flags), name, argument, eventId) != true)
            _services.Warning($"[AIWARNING] Entity {entity.Name} failed to select goal pipe {name}");
        return [];
    }

    private object? ResolveTarget(object? actor, object? value)
    {
        if (value is string name)
        {
            name = ZoneAiSignalName.CString(name);
            if (PipeUser(actor) is { } pipe && _services.ResolvePipeTarget(pipe, name) is { } target) return target;
            return _services.FindAiObject(name);
        }
        var entityId = value is ZoneLuaHandle handle ? unchecked((uint)handle.Value) :
            Number(value) is { } number ? unchecked((uint)ZoneStatMath.ToInt32(number)) : 0;
        return entityId == 0 ? null : _services.FindEntity(entityId)?.GetAiObject();
    }

    // 32032D20 calls pipe-user +18 (Remove), not +20 (Cancel). Extra arguments cause removal with ID zero.
    private object?[] Cancel(IReadOnlyList<object?> args)
    {
        var entity = Entity(args);
        if (entity is null) return [];
        var actor = entity.GetAiObject(); var count = args.Count - 1; var id = 0;
        if (count > 1) _services.Warning("CancelSubpipe: only 1 parameter expected");
        if (count == 1)
        {
            if (Number(args[1]) is { } number) id = ZoneStatMath.ToInt32(number);
            else _services.Warning("CancelSubpipe: wrong parameter type (number expected)");
        }
        return [PipeUser(actor)?.Remove(id) ?? false];
    }

    // 32032E70 returns no value for an entity without a pipe user; a valid pipe user returns a boolean.
    private object?[] IsUsing(IReadOnlyList<object?> args)
    {
        var entity = Entity(args);
        if (entity is null) return [];
        if (args.Count < 2) { _services.Warning("IsUsingPipe: too few parameters"); return []; }
        if (args[1] is not string name) { _services.Warning("IsUsingPipe: wrong parameter type"); return []; }
        return PipeUser(entity.GetAiObject()) is { } pipe ? [pipe.IsUsingPipe(name)] : [];
    }

    private static object? Value(IReadOnlyList<object?> args, int index) => index < args.Count ? args[index] : null;
    private static float? Number(object? value) => value switch
    { long integer => (float)(double)integer, double number => (float)number, _ => null };
}

using System.Numerics;
using AAEmu.Zone.Simulation;

namespace AAEmu.Zone.Scripting;

/// <summary>Native AI goal-pipe definition bindings. Does not execute the resulting goals.</summary>
public sealed class ZoneGoalPipeBinding
{
    private static readonly string[] OperationNames =
    [
        "acqtarget", "lookaround", "approach", "follow", "followpath", "backoff", "strafe", "bodypos",
        "run", "timeout", "pathfind", "locate", "trace", "signal", "ignoreall", "devalue", "forget", "hide",
        "form", "stick", "clear", "branch", "random", "lookat", "continuous", "move", "charge", "waitsignal",
        "animation", "animtarget", "usecover", "wait", "adjustaim", "proximity", "movetowards", "dodge",
        "useskill", "flyuseskill", "bind", "bindedUseskill"
    ];
    private readonly ZoneLuaRuntime _lua;
    private readonly ZoneGoalPipeCatalog _catalog;
    private readonly Action<string> _diagnostic;
    private readonly Func<string, object?> _resolveTarget;
    public ZoneGoalPipeDefinition? Current { get; private set; }
    public bool InGroup { get; private set; }

    public ZoneGoalPipeBinding(ZoneLuaReference ai, ZoneGoalPipeCatalog catalog, Action<string> diagnostic,
        Func<string, object?> resolveTarget)
    {
        ArgumentNullException.ThrowIfNull(ai);
        _lua = ai.Runtime;
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _diagnostic = diagnostic ?? throw new ArgumentNullException(nameof(diagnostic));
        _resolveTarget = resolveTarget ?? throw new ArgumentNullException(nameof(resolveTarget));
        _lua.WithStack(() =>
        {
            Bind("CreateGoalPipe", args => Create(args, false)); Bind("BeginGoalPipe", args => Create(args, true));
            Bind("EndGoalPipe", End); Bind("BeginGroup", args => Group(true)); Bind("EndGroup", args => Group(false));
            Bind("PushGoal", Push); Bind("PushLabel", Label);
            return 0;
        });
        void Bind(string name, Func<IReadOnlyList<object?>, object?[]> callback)
        { using var function = _lua.CreateFunction(callback); ai.Set(name, function); }
    }

    // 30522780 / 30522810: Begin clears an existing pipe before argument validation.
    private object?[] Create(IReadOnlyList<object?> args, bool begin)
    {
        if (begin && Current is not null)
        {
            _diagnostic($"Calling AI.BeginGoalPipe() with active goalpipe '{Current.Name}', must call AI.EndGoalPipe() to end goalpipe.");
            Current = null;
        }
        if (String(args, 0) is not { } name) return [];
        var pipe = _catalog.Create(name);
        InGroup = false;
        if (begin)
        {
            Current = pipe;
            if (pipe is null) _diagnostic("AI.BeginGoalPipe: Goalpipe was not created.");
        }
        return [];
    }

    private object?[] End(IReadOnlyList<object?> args)
    {
        if (Current is null) _diagnostic("Calling AI.EndGoalPipe() without AI.BeginGoalPipe() (current pipe is null).");
        Current = null;
        return [];
    }

    private object?[] Group(bool begin)
    {
        if (begin == InGroup) _diagnostic(begin ? "Calling AI.BeginGroup() twice (no nesed grouping supported)." :
            "Calling AI.EndGroup() without AI.BeginGroup().");
        InGroup = begin;
        return [];
    }

    private object?[] Label(IReadOnlyList<object?> args)
    {
        var pipe = Current ?? (String(args, 0) is { } name ? _catalog.Find(name) : null);
        if (pipe is not null && String(args, Current is null ? 1 : 0) is { } label) pipe.AddLabel(label, _diagnostic);
        return [];
    }

    // 3053A790: preserve typed reads, operation-specific defaults and the native lookaround double append.
    private object?[] Push(IReadOnlyList<object?> args)
    {
        var pipe = Current ?? (String(args, 0) is { } pipeName ? _catalog.Find(pipeName) : null);
        if (pipe is null) return [];
        var index = Current is null ? 1 : 0;
        var name = String(args, index) ?? "";
        var blocking = Boolean(Value(args, index + 1));
        var grouping = InGroup ? ZoneGoalGrouping.Grouped : ZoneGoalGrouping.None;
        if (name.StartsWith('+')) { name = name[1..]; grouping = ZoneGoalGrouping.WithPrevious; }
        var code = Array.IndexOf(OperationNames, name);
        var operation = code < 0 ? ZoneGoalOperation.Pipe : (ZoneGoalOperation)code;
        var start = index + 2;
        var count = args.Count - start;
        object? A(int n) => Value(args, start + n - 1);
        float N(int n, float fallback = 0) => Number(A(n)) ?? fallback;
        float Absent(int n, float fallback) => count < n ? fallback : N(n);
        int I(int n) => ZoneStatMath.ToInt32(N(n));
        string? S(int n) => String(args, start + n - 1);
        bool B(int n, bool fallback = false) => Boolean(A(n), fallback);
        Vector3 V(int n) => A(n) is ZoneLuaReference { IsTable: true } table ? _lua.GetVector(table) ?? Vector3.Zero : Vector3.Zero;
        var p = new ZoneGoalParameters();
        switch (operation)
        {
            case ZoneGoalOperation.AcquireTarget:
            case ZoneGoalOperation.Form:
                p = p with { Text = S(1) ?? name }; break;
            case ZoneGoalOperation.LookAround:
                p = p with { First = N(1), Second = Absent(2, -1), Vector = new(Absent(3, -1), Absent(4, -1), 0), Integer = I(5) };
                pipe.Append(operation, blocking, grouping, p, _diagnostic);
                p = p with { Boolean = B(2) }; break;
            case ZoneGoalOperation.Approach:
                var approachText = S(4) ?? "";
                p = p with { First = N(1), Integer = I(2), Second = N(3, 1),
                    Text = System.Text.Encoding.UTF8.GetByteCount(approachText) > 1 ? approachText : "", Vector = new(N(5), 0, 0) }; break;
            case ZoneGoalOperation.FollowPath:
                p = p with { Integer = (B(1) ? 1 : 0) | (B(2) ? 2 : 0) | (B(3) ? 4 : 0) | (B(6) ? 8 : 0) | (B(7) ? 16 : 0),
                    Integer2 = I(4), Second = N(5, 0.1f) }; break;
            case ZoneGoalOperation.BackOff:
                p = p with { First = N(1), Second = N(2), Integer = I(3), Vector = new(N(4), N(5), 0) }; break;
            case ZoneGoalOperation.Strafe:
                p = p with { First = N(1), Second = N(2), Boolean = I(3) != 0 }; break;
            case ZoneGoalOperation.BodyPosition:
                p = p with { First = N(1), Integer = I(2) }; break;
            case ZoneGoalOperation.Run:
                p = p with { First = N(1), Second = N(2), Vector = new(N(3), 0, 0) }; break;
            case ZoneGoalOperation.Timeout:
                p = p with { First = N(1), Second = N(2) }; break;
            case ZoneGoalOperation.PathFind:
                if (count > 0)
                {
                    var target = S(1) ?? name;
                    p = p with { Text = target, Target = target.Length > 0 ? _resolveTarget(target) : null, Boolean = B(2) };
                }
                break;
            case ZoneGoalOperation.Locate:
                p = p with { Text = S(1) ?? "", Integer = I(1), First = N(2) }; break;
            case ZoneGoalOperation.Trace:
                p = p with { Integer = I(1), Integer2 = I(2), First = N(3), Second = Absent(4, N(3)) }; break;
            case ZoneGoalOperation.Signal:
                p = p with { First = N(1), Text = S(2) ?? "", Integer = I(3), Second = N(4) }; break;
            case ZoneGoalOperation.Devalue:
                p = p with { First = N(1), Boolean = B(2) }; break;
            case ZoneGoalOperation.Hide:
                p = p with { First = N(1), Integer = I(2), Boolean = count < 3 || B(3), Second = N(4), Integer2 = I(5) }; break;
            case ZoneGoalOperation.Stick:
                p = p with { First = N(1), Integer = I(2), Integer2 = I(3), Second = N(4, N(1)), Vector = new(N(5, -1), N(6), 0) }; break;
            case ZoneGoalOperation.Clear:
                p = p with { First = Absent(1, 1) }; break;
            case ZoneGoalOperation.Branch:
                p = p with { Text = S(1) ?? "", Integer2 = I(1), Integer = I(2) };
                if (count > 1)
                    p = p with { First = N(3), Second = count > 5 ? -1 : Absent(4, -1),
                        Vector = count > 5 ? new(N(4), N(5), N(6)) : Vector3.Zero };
                break;
            case ZoneGoalOperation.Random:
                p = p with { Integer = I(1), First = N(2) }; break;
            case ZoneGoalOperation.LookAt:
                p = S(1) is { } lookAt ? p with { Text = lookAt } :
                    p with { First = N(1), Second = N(2), Boolean = B(3), Integer = I(4) }; break;
            case ZoneGoalOperation.Continuous:
                p = p with { Boolean = B(1) }; break;
            case ZoneGoalOperation.Move:
                p = p with { First = N(1), Second = N(2, 1), Integer = I(3), Boolean = I(4) == 0 }; break;
            case ZoneGoalOperation.Charge:
                p = p with { First = N(1), Second = N(2), Integer = I(3) }; break;
            case ZoneGoalOperation.WaitSignal:
                p = p with { Text = S(1) ?? name, Second = count == 3 ? N(3) : 0 };
                if (S(2) is { } signal) p = p with { First = 1, Text2 = signal };
                else if (A(2) is ZoneLuaHandle handle) p = p with { First = 2, Integer = unchecked((int)handle.Value) };
                else if (Number(A(2)) is not null) p = p with { First = 2, Integer = I(2) };
                break;
            case ZoneGoalOperation.Animation:
                p = p with { Integer = I(1), Text = S(2) ?? name, First = N(3, -1) }; break;
            case ZoneGoalOperation.AnimationTarget:
                p = p with { Boolean = B(1), Text = S(2) ?? name, Vector = new(N(3), N(4), N(5)) }; break;
            case ZoneGoalOperation.UseCover:
                p = p with { Boolean = B(1), First = N(2), Second = N(3), Integer = I(4), Integer2 = I(5) }; break;
            case ZoneGoalOperation.Wait:
                if (InGroup) { _diagnostic($"No WAIT goalOp allowed withing a group. Pipe: {pipe.Name} "); return []; }
                p = p with { Integer = I(1) }; break;
            case ZoneGoalOperation.AdjustAim:
                p = p with { Integer = (I(1) != 0 ? 1 : 0) | (I(2) != 0 ? 2 : 0) | (I(3) != 0 ? 4 : 0) }; break;
            case ZoneGoalOperation.Proximity:
                p = p with { First = N(1), Text = S(2) ?? "", Integer = I(3) }; break;
            case ZoneGoalOperation.MoveTowards:
                p = p with { First = N(1), Integer = I(2), Vector = new(N(3), 0, 0) }; break;
            case ZoneGoalOperation.Dodge:
                p = p with { First = N(1), Integer2 = I(2) }; break;
            case ZoneGoalOperation.UseSkill:
            case ZoneGoalOperation.FlyUseSkill:
            case ZoneGoalOperation.Bind:
            case ZoneGoalOperation.BindedUseSkill:
                if (!blocking)
                {
                    var display = operation == ZoneGoalOperation.BindedUseSkill ? "BindedUseSkill" : name;
                    _diagnostic($"\"{display}\" goal works only on blocking mode. Pipe: {pipe.Name} "); return [];
                }
                p = operation switch
                {
                    // A present non-string second parameter reads an uninitialized pointer in native
                    // useskill. Keep the owned string empty instead of retaining unrelated stack data.
                    ZoneGoalOperation.UseSkill => p with { Integer = I(1), Text = S(2) ?? "" },
                    ZoneGoalOperation.FlyUseSkill => p with { Integer = I(1) },
                    ZoneGoalOperation.Bind => p with { Text = S(1) ?? name, Vector = V(2), First = N(3) },
                    _ => p with { Text = S(1) ?? "", Vector = V(2), First = N(3), Text2 = S(4) ?? "" }
                };
                break;
            default:
                p = p with { First = N(1) }; break;
        }
        pipe.Append(operation, blocking, grouping, p, _diagnostic, operation == ZoneGoalOperation.Pipe ? name : "");
        return [];
    }

    private static object? Value(IReadOnlyList<object?> args, int index) => index >= 0 && index < args.Count ? args[index] : null;
    private static string? String(IReadOnlyList<object?> args, int index) =>
        Value(args, index) is string value ? ZoneAiSignalName.CString(value) : null;
    private static float? Number(object? value) => value switch
    { long integer => (float)(double)integer, double number => (float)number, _ => null };
    private static bool Boolean(object? value, bool fallback = false) => value switch
    { bool boolean => boolean, long integer => integer != 0, double number => number != 0, _ => fallback };
}

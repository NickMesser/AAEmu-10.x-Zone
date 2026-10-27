using System.Numerics;

using AAEmu.Zone.Ai;
using AAEmu.Zone.Combat;

namespace AAEmu.Zone.Scripting;

/// <summary>The four native AI track-pattern builder bindings, with one current descriptor per binding owner.</summary>
public sealed class ZoneTrackPatternBinding
{
    private readonly ZoneLuaRuntime _lua;
    private readonly ZoneTrackPatternCatalog _catalog;
    private readonly Action<string> _diagnostic;
    public ZoneTrackPatternDefinition? Current { get; private set; }

    public ZoneTrackPatternBinding(ZoneLuaReference ai, ZoneTrackPatternCatalog catalog, Action<string> diagnostic)
    {
        ArgumentNullException.ThrowIfNull(ai);
        _lua = ai.Runtime;
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _diagnostic = diagnostic ?? throw new ArgumentNullException(nameof(diagnostic));
        _lua.WithStack(() =>
        {
            Bind("BeginTrackPattern", Begin); Bind("AddPatternNode", AddNode);
            Bind("AddPatternBranch", AddBranch); Bind("EndTrackPattern", End);
            return 0;
        });

        void Bind(string name, Func<IReadOnlyList<object?>, object?[]> handler)
        {
            using var function = _lua.CreateFunction(handler);
            ai.Set(name, function);
        }
    }

    // 305291C0 clears an unfinished current descriptor before checking the next call's arguments.
    private object?[] Begin(IReadOnlyList<object?> args)
    {
        if (Current is not null)
        {
            _diagnostic("Calling CreateTrackPattern without finishing the previous track pattern with EndTrackPattern.");
            Current = null;
        }
        if (args.Count < 3) { _diagnostic("CreateTrackPattern: Not enough parameters."); return []; }
        // Native arity diagnostic checks three arguments, but successful creation requires four.
        if (String(args, 0) is not { } name || Number(args, 1) is not { } flags ||
            Number(args, 2) is not { } first || Number(args, 3) is not { } second) return [];
        float[] scalars = [first, second, Number(args, 4) ?? 0.35f, Number(args, 5) ?? 0.4f,
            Number(args, 6) ?? 0, Number(args, 7) ?? 0, Number(args, 8) ?? 0];
        var vector = Vector3.Zero;
        if (args.Count > 9 && args[9] is ZoneLuaReference { IsTable: true } table) vector = _lua.GetVector(table) ?? Vector3.Zero;
        Current = _catalog.GetOrCreate(name);
        Current.SetParameters(ZoneStatMath.ToInt32(flags), scalars, vector);
        return [];
    }

    // 30529720: required typed fields, followed by an optional parent string and numeric signal.
    private object?[] AddNode(IReadOnlyList<object?> args)
    {
        if (Current is null) { _diagnostic("Calling AddPatternNode without starting a pattern using BeginTrackPattern."); return []; }
        if (args.Count < 5) { _diagnostic("AddPatternNode: Not enough parameters."); return []; }
        if (String(args, 0) is not { } name || Number(args, 1) is not { } x || Number(args, 2) is not { } y ||
            Number(args, 3) is not { } z || Number(args, 4) is not { } flags) return [];
        Current.AddNode(name, new(x, y, z), ZoneStatMath.ToInt32(flags), String(args, 5), ZoneStatMath.ToInt32(Number(args, 6) ?? 0));
        return [];
    }

    // 30544BF0: branch calls replace the node's choice rule and append only actual string arguments.
    private object?[] AddBranch(IReadOnlyList<object?> args)
    {
        if (Current is null) { _diagnostic("Calling AddPatternPoint without starting a pattern using BeginTrackPattern."); return []; }
        if (args.Count < 3) { _diagnostic("AddPatternSeqBranch: Not enough parameters."); return []; }
        if (String(args, 0) is not { } name || Number(args, 1) is not { } choice) return [];
        var targets = args.Skip(2).OfType<string>().Select(ZoneAiSignalName.CString).ToArray();
        var node = Current.FindNode(name);
        if (node is null)
            _diagnostic($"Track pattern '{Current.Name}': Could not find node '{name}' while trying to create sequence to {string.Concat(targets.Select(target => ", " + target))}");
        else node.AddBranches(ZoneStatMath.ToInt32(choice), targets);
        return [];
    }

    private object?[] End(IReadOnlyList<object?> args)
    {
        if (Current is null) { _diagnostic("Calling EndTrackPattern without matching BeginTrackPattern."); return []; }
        Current.FinalizeDefinition(_diagnostic);
        Current = null;
        return [];
    }

    private static string? String(IReadOnlyList<object?> args, int index) =>
        index < args.Count && args[index] is string value ? ZoneAiSignalName.CString(value) : null;
    private static float? Number(IReadOnlyList<object?> args, int index) => index < args.Count ? Number(args[index]) : null;
    private static float? Number(object? value) => value switch
    { long integer => (float)(double)integer, double number => (float)number, _ => null };
}

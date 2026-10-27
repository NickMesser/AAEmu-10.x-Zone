using System.Collections.ObjectModel;
using System.Numerics;

namespace AAEmu.Zone.Simulation;

// CryAI 311FF4B0: operation names and enum values are case-sensitive.
public enum ZoneGoalOperation
{
    AcquireTarget, LookAround, Approach, Follow, FollowPath, BackOff, Strafe, BodyPosition,
    Run, Timeout, PathFind, Locate, Trace, Signal, IgnoreAll, Devalue, Forget, Hide, Form,
    Stick, Clear, Branch, Random, LookAt, Continuous, Move, Charge, WaitSignal, Animation,
    AnimationTarget, UseCover, Wait, AdjustAim, Proximity, MoveTowards, Dodge, UseSkill,
    FlyUseSkill, Bind, BindedUseSkill, Pipe
}

public enum ZoneGoalGrouping { None, WithPrevious, Grouped }

/// <summary>
/// The owned native GoalParameters payload (310F7E58). Field meanings depend on the operation.
/// Target is the engine AI object returned by name lookup, not an entity ID or a native pointer.
/// </summary>
public sealed record ZoneGoalParameters
{
    public Vector3 Vector { get; init; }
    public Vector3 Vector2 { get; init; }
    public object? Target { get; init; }
    public float First { get; init; }
    public float Second { get; init; }
    public int Integer { get; init; }
    public int Integer2 { get; init; }
    public bool Boolean { get; init; }
    public string Text { get; init; } = "";
    public string Text2 { get; init; } = "";
}

/// <summary>A goal definition; executable goal instances and their actor state are separate.</summary>
public sealed record ZoneGoalDefinition(ZoneGoalOperation Operation, string PipeName, bool Blocking,
    ZoneGoalGrouping Grouping, ZoneGoalParameters Parameters, int WaitGroupCount);

public sealed class ZoneGoalPipeCatalog
{
    private readonly Dictionary<string, ZoneGoalPipeDefinition> _pipes = new(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, ZoneGoalPipeDefinition> Pipes { get; }
    public bool CreatingDynamicPipes { get; private set; }

    public ZoneGoalPipeCatalog()
    {
        Pipes = new ReadOnlyDictionary<string, ZoneGoalPipeDefinition>(_pipes);
        Create("_first_"); // 31156C24 creates this static pipe before scripts execute.
    }

    // 311565B4 replaces a duplicate definition; _last_ is a marker and is never inserted.
    public ZoneGoalPipeDefinition? Create(string name)
    {
        name = ZoneAiSignalName.CString(name);
        if (name == "_last_") { CreatingDynamicPipes = true; return null; }
        var pipe = new ZoneGoalPipeDefinition(name, CreatingDynamicPipes);
        _pipes[name] = pipe;
        return pipe;
    }

    public ZoneGoalPipeDefinition? Find(string name) => _pipes.GetValueOrDefault(ZoneAiSignalName.CString(name));

    /// <summary>31156314 / 310F7604: resolve the source labels once, then create fresh goal instances.</summary>
    public ZoneGoalPipeInstance? Instantiate(string name, IZoneGoalPipeRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        var definition = Find(name);
        if (definition is null) return null;
        definition.ResolveLabels(runtime.Diagnostic);
        return new(this, definition, runtime);
    }
}

public sealed class ZoneGoalPipeDefinition
{
    private readonly List<ZoneGoalDefinition> _goals = [];
    private readonly Dictionary<string, int> _labels = new(StringComparer.Ordinal);
    private int _groupCount;
    public string Name { get; }
    public bool IsDynamic { get; }
    public IReadOnlyList<ZoneGoalDefinition> Goals { get; }
    public IReadOnlyDictionary<string, int> Labels { get; }

    internal ZoneGoalPipeDefinition(string name, bool dynamic)
    {
        Name = name; IsDynamic = dynamic;
        Goals = _goals.AsReadOnly(); Labels = new ReadOnlyDictionary<string, int>(_labels);
    }

    // 310F6574 / 310F7434: WAIT consumes the preceding group's count and forces blocking/grouped.
    public void Append(ZoneGoalOperation operation, bool blocking, ZoneGoalGrouping grouping,
        ZoneGoalParameters parameters, Action<string> diagnostic, string pipeName = "")
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(diagnostic);
        if (!Enum.IsDefined(operation) || !Enum.IsDefined(grouping)) throw new ArgumentOutOfRangeException(nameof(operation));
        if (operation == ZoneGoalOperation.Trace && ((parameters.Integer & 0x4000) != 0 ? Math.Abs(parameters.First) : parameters.First) > 0)
            diagnostic($"{Name} - Specifying distance/duration in 'trace' goal-op is deprecated.");
        var waitCount = 0;
        if (operation == ZoneGoalOperation.Wait)
        {
            waitCount = _groupCount; _groupCount = 0;
            blocking = true; grouping = ZoneGoalGrouping.Grouped;
        }
        else
        {
            _groupCount = grouping == ZoneGoalGrouping.Grouped ? unchecked(_groupCount + 1) : 0;
            if (operation is ZoneGoalOperation.Branch or ZoneGoalOperation.Random) blocking = false;
        }
        _goals.Add(new(operation, ZoneAiSignalName.CString(pipeName), blocking, grouping, parameters with
        { Text = ZoneAiSignalName.CString(parameters.Text), Text2 = ZoneAiSignalName.CString(parameters.Text2) }, waitCount));
    }

    // 310F6414 stores the next goal index and replaces duplicate labels with a diagnostic.
    public void AddLabel(string label, Action<string> diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        label = ZoneAiSignalName.CString(label);
        if (_labels.ContainsKey(label)) diagnostic($"Label {label} already exists in goal pipe {Name}. Overriding previous label!");
        _labels[label] = _goals.Count;
    }

    // Cloning mutates the prototype. With no labels, unresolved branch strings remain untouched.
    internal void ResolveLabels(Action<string> diagnostic)
    {
        if (_labels.Count == 0) return;
        for (var index = 0; index < _goals.Count; index++)
        {
            var goal = _goals[index];
            if (goal.Operation is not (ZoneGoalOperation.Branch or ZoneGoalOperation.Random) || goal.Parameters.Text.Length == 0) continue;
            var label = goal.Parameters.Text; var offset = 0;
            if (!_labels.TryGetValue(label, out var target)) diagnostic($"Label {label} not found in goal pipe {Name}.");
            else if (target == index) diagnostic($"Empty loop at label {label} ignored in goal pipe {Name}.");
            else offset = target > index ? target - index - 1 : target - index;
            _goals[index] = goal with { Parameters = goal.Parameters with { Integer2 = offset, Text = "" } };
        }
        _labels.Clear();
    }
}

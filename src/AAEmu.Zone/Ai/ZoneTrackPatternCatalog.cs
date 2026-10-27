using System.Collections.ObjectModel;
using System.Numerics;

namespace AAEmu.Zone.Ai;

/// <summary>Native track-pattern descriptors. Their actor-side movement execution is separate.</summary>
public sealed class ZoneTrackPatternCatalog
{
    private readonly Dictionary<string, ZoneTrackPatternDefinition> _patterns = new(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, ZoneTrackPatternDefinition> Patterns { get; }

    public ZoneTrackPatternCatalog() => Patterns = new ReadOnlyDictionary<string, ZoneTrackPatternDefinition>(_patterns);

    // 3107F0A8 inserts a descriptor only if the name is new; an existing descriptor is reused intact.
    public ZoneTrackPatternDefinition GetOrCreate(string name)
    {
        name = ZoneAiSignalName.CString(name);
        if (!_patterns.TryGetValue(name, out var pattern)) _patterns.Add(name, pattern = new(name));
        return pattern;
    }
}

public sealed class ZoneTrackPatternDefinition
{
    private readonly Dictionary<string, ZoneTrackPatternNode> _nodes = new(StringComparer.Ordinal);
    public string Name { get; }
    public int Flags { get; private set; }
    /// <summary>BeginTrackPattern's seven scalar arguments (positions 3 through 9), in Lua order.</summary>
    public IReadOnlyList<float> ScalarParameters { get; private set; } = Array.AsReadOnly(new float[] { 0, 0, 0.35f, 0.4f, 0, 0, 0 });
    /// <summary>BeginTrackPattern's optional vector argument (position 10).</summary>
    public Vector3 VectorParameter { get; private set; }
    public bool IsFinalized { get; private set; }
    public IReadOnlyDictionary<string, ZoneTrackPatternNode> Nodes { get; }

    internal ZoneTrackPatternDefinition(string name)
    { Name = name; Nodes = new ReadOnlyDictionary<string, ZoneTrackPatternNode>(_nodes); }

    public void SetParameters(int flags, ReadOnlySpan<float> scalars, Vector3 vector)
    {
        if (scalars.Length != 7) throw new ArgumentException("A track pattern has seven scalar parameters.", nameof(scalars));
        Flags = flags; ScalarParameters = Array.AsReadOnly(scalars.ToArray()); VectorParameter = vector;
    }

    // 3119A110 replaces duplicate nodes, including their prior branch definitions and links.
    public ZoneTrackPatternNode AddNode(string name, Vector3 position, int flags, string? parentName = null, int signal = 0)
    {
        name = ZoneAiSignalName.CString(name);
        var node = new ZoneTrackPatternNode(name, position, flags, ZoneAiSignalName.CString(parentName ?? ""), signal);
        _nodes[name] = node;
        return node;
    }

    public ZoneTrackPatternNode? FindNode(string name) => _nodes.GetValueOrDefault(ZoneAiSignalName.CString(name));

    // 31199F40 assigns IDs by bytewise name order and appends resolved branches on every finalization.
    public void FinalizeDefinition(Action<string> diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        var index = 0;
        foreach (var name in _nodes.Keys.Order(ZoneUtf8StringComparer.Instance))
        {
            var node = _nodes[name]; node.Index = index++;
            if (node.ParentName.Length != 0)
            {
                node.Parent = FindNode(node.ParentName);
                if (node.Parent is null) diagnostic($"<CTrackPatternDescriptor> While building hierarchy for node '{name}', could not find parent node '{node.ParentName}'.");
            }
            foreach (var target in node.BranchNames)
            {
                var branch = FindNode(target);
                if (branch is null) diagnostic($"<CScriptBind_AI> While building hierarchy for node '{name}', could not find branch node '{target}'.");
                else node.AppendResolvedBranch(branch);
            }
        }
        IsFinalized = true;
    }
}

public sealed class ZoneTrackPatternNode
{
    private readonly List<string> _branchNames = [];
    private readonly List<ZoneTrackPatternNode> _branches = [];
    public string Name { get; }
    public Vector3 Position { get; }
    public int Flags { get; }
    public string ParentName { get; }
    public ZoneTrackPatternNode? Parent { get; internal set; }
    public int Signal { get; }
    public int Index { get; internal set; }
    public int BranchChoice { get; private set; }
    public IReadOnlyList<string> BranchNames { get; }
    public IReadOnlyList<ZoneTrackPatternNode> Branches { get; }

    internal ZoneTrackPatternNode(string name, Vector3 position, int flags, string parentName, int signal)
    {
        Name = name; Position = position; Flags = flags; ParentName = parentName; Signal = signal;
        BranchNames = _branchNames.AsReadOnly(); Branches = _branches.AsReadOnly();
    }

    public void AddBranches(int choice, IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        BranchChoice = choice;
        foreach (var name in names) _branchNames.Add(ZoneAiSignalName.CString(name));
    }

    internal void AppendResolvedBranch(ZoneTrackPatternNode node) => _branches.Add(node);
}

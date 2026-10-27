using System.Globalization;
using System.Text;

namespace AAEmu.Zone.Content;

public enum ZoneNavigationLegacyStartupStatus
{
    DisabledMissingNetwork,
    RequiresRemainingGraphReaders
}

public sealed record ZoneNavigationLegacyLoadStep(string Operation, string? Path = null);

/// <summary>
/// Dedicated mode's proved startup prefix. A present network asset cannot become ready until
/// the actual remaining graph readers and quadtree construction are implemented.
/// </summary>
public sealed class ZoneNavigationLegacyStartup
{
    public ZoneNavigationLegacyStartupStatus Status { get; }
    /// <summary>
    /// Native AISystem+0x670 projected through the content prefix: 0 disabled, 1 network present,
    /// 2 known vertex/network failure. Unexecuted runtime construction can still change this flag.
    /// </summary>
    public int NativeLoadFlag { get; }
    public string RootPath { get; }
    public string NetworkPath => RootPath + "/netmission0.bai";
    public ZoneNavigationLegacyAreaContent? Areas { get; }
    public ZoneNavigationLegacyVertexContent? Vertices { get; }
    /// <summary>Complete authored records; runtime graph ownership and publication remain separate.</summary>
    public ZoneNavigationLegacyGraphContent? Graph { get; }
    public IReadOnlyList<ZoneNavigationLegacyLoadStep> CallOrder { get; }
    public IReadOnlyList<string> Diagnostics { get; }
    public string? NextRequiredReader { get; }

    private ZoneNavigationLegacyStartup(ZoneNavigationLegacyStartupStatus status, int nativeLoadFlag,
        string rootPath, ZoneNavigationLegacyAreaContent? areas, ZoneNavigationLegacyVertexContent? vertices,
        ZoneNavigationLegacyGraphContent? graph,
        List<ZoneNavigationLegacyLoadStep> steps, List<string> diagnostics, string? nextRequiredReader)
    {
        Status = status;
        NativeLoadFlag = nativeLoadFlag;
        RootPath = rootPath;
        Areas = areas;
        Vertices = vertices;
        Graph = graph;
        CallOrder = steps.AsReadOnly();
        Diagnostics = diagnostics.AsReadOnly();
        NextRequiredReader = nextRequiredReader;
    }

    /// <summary>
    /// Starts the enabled dedicated navigation branch on fresh content. CryAction30507530 uses
    /// legacy AI+280 in System mode2; System36510A10 permits streaming only in modes0/3.
    /// The caller owns broader AI enablement and runtime manager retirement.
    /// </summary>
    public static ZoneNavigationLegacyStartup Load(IZoneAssetSource assets, string worldName, int zoneId)
    {
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentException.ThrowIfNullOrWhiteSpace(worldName);
        // Zone365650A0 formats World.Name and signed Zone.Id. LevelInfo305083F0 supplies
        // mission0. Normalize the native doubled slash before the archive-name boundary.
        var nativeRoot = $"game/worlds/{worldName}/zone/{zoneId.ToString(CultureInfo.InvariantCulture)}/";
        // 365650A0 has a 260-byte root buffer, including its trailing slash and terminator.
        if (Encoding.UTF8.GetByteCount(nativeRoot) >= 260)
            throw new ArgumentException("The native navigation root exceeds its 260-byte buffer including the terminator.", nameof(worldName));
        var rootCharacters = nativeRoot[..^1].ToCharArray();
        // CryPak's ordinary relative-path branch folds only ASCII, not Unicode case pairs.
        for (var i = 0; i < rootCharacters.Length; i++)
            if (rootCharacters[i] is >= 'A' and <= 'Z') rootCharacters[i] += (char)('a' - 'A');
        var root = ZoneAssetPath.Validate(new string(rootCharacters));
        var steps = new List<ZoneNavigationLegacyLoadStep>();
        var diagnostics = new List<string>();
        var network = root + "/netmission0.bai";
        steps.Add(new("ProbeNetwork", network));
        using (var probe = assets.OpenRead(network))
        {
            if (probe is null)
            {
                diagnostics.Add("No navigation data available - disabling AI");
                return new(ZoneNavigationLegacyStartupStatus.DisabledMissingNetwork, 0, root,
                    null, null, null, steps, diagnostics, null);
            }
        }

        ZoneNavigationLegacyAreaContent? areas = null;
        var areaPath = root + "/areasmission0.bai";
        steps.Add(new("ReadAreas", areaPath));
        using (var stream = assets.OpenRead(areaPath))
        {
            if (stream is null) diagnostics.Add($"Unable to open areas file {areaPath}");
            else
            {
                try { areas = ZoneNavigationLegacyAreaContent.Load(stream); }
                // 3107AB58 warns and returns on an unsupported version; its caller continues.
                // Partial bodies still throw the managed reader's explicit input-validation error.
                catch (NotSupportedException ex) { diagnostics.Add(ex.Message); }
            }
        }

        ZoneNavigationLegacyVertexContent? vertices = null;
        var vertexPath = root + "/vertsmission0.bai";
        steps.Add(new("ReadVertices", vertexPath));
        using (var stream = assets.OpenRead(vertexPath))
        {
            if (stream is null) diagnostics.Add($"Unable to open vertex file {vertexPath}");
            else
            {
                try { vertices = ZoneNavigationLegacyVertexContent.Load(stream); }
                catch (NotSupportedException ex) { diagnostics.Add(ex.Message); }
            }
        }

        ZoneNavigationLegacyGraphContent? graph = null;
        if (vertices is not null)
        {
            // 31104438 reopens the network after the probe, areas and successful vertices.
            // Missing/version/key1 failures return false; malformed input stays an explicit error.
            steps.Add(new("ReadNetwork", network));
            Stream? stream;
            try { stream = assets.OpenRead(network); }
            catch (IOException) { stream = null; }
            catch (UnauthorizedAccessException) { stream = null; }
            try
            {
                if (stream is null) diagnostics.Add($"Unable to open network file {network}");
                else
                {
                    var staged = ZoneNavigationLegacyGraphContent.LoadForStartup(stream);
                    graph = staged.Graph;
                    if (staged.Diagnostic is not null) diagnostics.Add(staged.Diagnostic);
                }
            }
            finally
            {
                // Native 31089288 ignores ordinary close failures. Preserve a completed read
                // or its parsing exception; unsupported source capabilities still fail explicitly.
                try { stream?.Dispose(); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        // 3107BE38 short-circuits triangles on vertex failure, then continues volume, flight,
        // road and surface loaders and builds quadtrees. This prefix stages network content;
        // it does not execute modifier queries, construct runtime nodes/links or publish a graph.
        var next = graph is null ? "Volume navigation (311A8A00)" : "Network runtime construction (31103988)";
        diagnostics.Add($"Navigation startup requires {next} and the remaining graph construction.");
        return new(ZoneNavigationLegacyStartupStatus.RequiresRemainingGraphReaders,
            graph is null ? 2 : 1, root, areas, vertices, graph, steps, diagnostics, next);
    }
}

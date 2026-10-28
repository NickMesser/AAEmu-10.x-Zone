using System.Numerics;
using System.Text.Json;
using AAEmu.Zone.Physics;

namespace AAEmu.Zone.Diagnostics;

/// <summary>Identical bounded static-scene rays for independent native/managed comparison runs.</summary>
internal static class ZoneSceneCollisionProbes
{
    internal sealed record Probe(string Name, float[] Origin, float[] Displacement);
    internal sealed record Result(string Name, bool Hit, float? Distance, float[]? Position,
        float[]? Normal, ushort? PhysicalMaterialId);
    internal delegate bool Query(Vector3 origin, Vector3 displacement, out ZonePhysicsHit hit);
    private static readonly JsonSerializerOptions Json = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = true };

    public static void Run(string? inputPath, string? outputPath, string backend, Query query)
    {
        if (inputPath is null) return;
        var probes = JsonSerializer.Deserialize<Probe[]>(File.ReadAllText(inputPath), Json) ??
            throw new InvalidDataException("Scene probes must contain a JSON array.");
        if (probes.Length > 100_000) throw new InvalidDataException("Too many scene probes.");
        var results = new List<Result>(probes.Length);
        foreach (var probe in probes)
        {
            if (probe.Origin is not { Length: 3 } || probe.Displacement is not { Length: 3 })
                throw new InvalidDataException($"Scene probe {probe.Name} requires three-component vectors.");
            var origin = new Vector3(probe.Origin[0], probe.Origin[1], probe.Origin[2]);
            var displacement = new Vector3(probe.Displacement[0], probe.Displacement[1], probe.Displacement[2]);
            var found = query(origin, displacement, out var hit);
            results.Add(found ? new(probe.Name, true, hit.Distance, Values(hit.Position), Values(hit.Normal), hit.PhysicalMaterialId)
                : new(probe.Name, false, null, null, null, null));
        }
        Console.WriteLine($"{backend} static scene probes: {results.Count(result => result.Hit)}/{results.Count} hits.");
        if (outputPath is not null)
        {
            File.WriteAllText(outputPath, JsonSerializer.Serialize(new { backend, results }, Json));
            Console.WriteLine($"Scene probe results: {Path.GetFullPath(outputPath)}");
        }
    }

    private static float[] Values(Vector3 value) => [value.X, value.Y, value.Z];
}

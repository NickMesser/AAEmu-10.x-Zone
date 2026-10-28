using System.Diagnostics;
using System.Text.Json;
using AAEmu.Zone.Content;
using AAEmu.Zone.Hosting;
using AAEmu.Zone.Scene;

namespace AAEmu.Zone.Diagnostics;

internal static class ZoneManagedSceneCheck
{
    public static int Run(ZoneRuntimeConfiguration? configuration, ZoneWorldMapLayout? layout,
        IZoneAssetSource? assets, string? coveragePath, string? probesPath, string? probeOutputPath,
        string? shapeProbesPath, string? shapeProbeOutputPath, CancellationToken shutdown)
    {
        if (configuration is null || layout is null || assets is null)
            throw new ArgumentException("--check-managed-scene requires --config and its configured assets.");
        shutdown.ThrowIfCancellationRequested();
        var elapsed = Stopwatch.StartNew();
        using var scene = new ZoneManagedSceneActivation(layout, assets, Console.WriteLine);
        scene.Create();
        var report = scene.Load();
        shutdown.ThrowIfCancellationRequested();
        var collision = scene.Collision!;
        Console.WriteLine($"Managed scene check: {report.TerrainLoadedCells}/{report.Cells.Count} terrain cells, " +
            $"{collision.Geometry.InstanceCount} static proxy instances, {collision.Report.UnsupportedBrushCount} unsupported brushes; " +
            $"elapsed {elapsed.Elapsed.TotalSeconds:0.00}s.");
        ZoneSceneCollisionProbes.Run(probesPath, probeOutputPath, "managed",
            (System.Numerics.Vector3 origin, System.Numerics.Vector3 displacement, out Physics.ZonePhysicsHit hit) =>
                collision.RayGeometry.Raycast(origin, displacement, out hit));
        ZoneSceneShapeProbes.RunManaged(shapeProbesPath, shapeProbeOutputPath, collision.Geometry,
            new Physics.ZoneManagedTerrainGeometry(scene.Heights, scene.Surfaces));
        if (coveragePath is not null)
        {
            var value = new { terrain = new { report.TerrainReady, report.TerrainLoadedCells, report.EmptyTerrainCells }, collision = collision.Report };
            File.WriteAllText(coveragePath, JsonSerializer.Serialize(value,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));
            Console.WriteLine($"Managed collision coverage: {Path.GetFullPath(coveragePath)}");
        }
        Console.WriteLine($"Managed scene result: terrainReady={report.TerrainReady}, " +
            $"brushCoverageComplete={collision.Report.BrushCoverageComplete}, fullFeatureParity=false.");
        return report.TerrainReady ? 0 : 1;
    }
}

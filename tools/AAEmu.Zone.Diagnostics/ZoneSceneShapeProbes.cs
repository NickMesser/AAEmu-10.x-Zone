using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using AAEmu.Zone.Native.Physics;
using AAEmu.Zone.Physics;

namespace AAEmu.Zone.Diagnostics;

/// <summary>Explicit raw-center shape queries for the original native primitive oracle and managed geometry.</summary>
internal static class ZoneSceneShapeProbes
{
    private sealed record Input(string Name, string Shape, float[] Center, float[] Sweep,
        float[]? HalfExtents = null, float? Radius = null, float? SegmentHalfHeight = null,
        float? HalfHeight = null, float[]? Axis = null, string? Target = null);
    private sealed record Probe(string Name, string Shape, Vector3 Center, Vector3 Sweep,
        Vector3 HalfExtents, float Radius, float AxialHalfHeight, Vector3 Axis, string Target)
    {
        public bool IsSweep => Sweep.LengthSquared() > 0;
    }
    private sealed record Result(string Name, string Shape, string Operation, string Outcome, bool? Hit,
        float? Distance = null, float[]? Position = null, float[]? Normal = null, int? PhysicalMaterialId = null,
        float? NativeReturn = null, double? NativeContactDistance = null, int? NativeEntityId = null,
        int? NativePartId = null, string? Diagnostic = null, float[]? NativeContactNormal = null,
        float[]? NativeUnprojectionDirection = null, string Target = "static");
    private static readonly JsonSerializerOptions Json = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, WriteIndented = true };

    public static void RunNative(string? inputPath, string? outputPath, ZoneNativeTerrainPhysicsWorld world)
    {
        if (inputPath is null) return;
        ArgumentNullException.ThrowIfNull(world);
        Run(inputPath, outputPath, "native", probe =>
        {
            var value = (probe.Shape, probe.Target) switch
            {
                ("box", "terrain") => world.QueryTerrainBox(probe.Center, probe.HalfExtents, probe.Sweep),
                ("capsule", "terrain") => world.QueryTerrainCapsule(probe.Center, probe.Radius, probe.AxialHalfHeight, probe.Sweep, probe.Axis),
                ("cylinder", "terrain") => world.QueryTerrainCylinder(probe.Center, probe.Radius, probe.AxialHalfHeight, probe.Sweep, probe.Axis),
                ("box", _) => world.QueryStaticBox(probe.Center, probe.HalfExtents, probe.Sweep),
                ("capsule", _) => world.QueryStaticCapsule(probe.Center, probe.Radius, probe.AxialHalfHeight, probe.Sweep, probe.Axis),
                _ => world.QueryStaticCylinder(probe.Center, probe.Radius, probe.AxialHalfHeight, probe.Sweep, probe.Axis)
            };
            var hit = value.Outcome switch
            {
                ZoneNativePrimitiveQueryOutcome.Hit => (bool?)true,
                ZoneNativePrimitiveQueryOutcome.Miss => false,
                _ => null
            };
            var contact = value.Contact;
            return new(probe.Name, probe.Shape, probe.IsSweep ? "sweep" : "overlap",
                hit is null ? "indeterminate" : hit.Value ? "hit" : "miss", hit,
                probe.IsSweep && contact.HasValue ? (float)contact.Value.Distance : null,
                contact.HasValue ? Values(contact.Value.Position) : null,
                probe.IsSweep && contact.HasValue ? Values(-contact.Value.Normal) : null,
                contact?.PhysicalMaterialId, value.NativeReturn, contact?.Distance,
                contact?.EntityId, contact?.PartId, value.Diagnostic,
                contact.HasValue ? Values(contact.Value.Normal) : null,
                contact?.UnprojectionDirection is { } direction ? Values(direction) : null, probe.Target);
        });
    }

    public static void RunManaged(string? inputPath, string? outputPath, ZoneManagedStaticGeometry geometry,
        ZoneManagedTerrainGeometry? terrain = null)
    {
        if (inputPath is null) return;
        ArgumentNullException.ThrowIfNull(geometry);
        Run(inputPath, outputPath, "managed", probe =>
        {
            if (probe.Shape != "box" &&
                (probe.Axis.X != 0 || probe.Axis.Y != 0 || MathF.Abs(probe.Axis.Z) != 1))
                return new(probe.Name, probe.Shape, probe.IsSweep ? "sweep" : "overlap", "unsupported", null,
                    Diagnostic: $"Managed character shape queries currently support upright {probe.Shape}s only.", Target: probe.Target);
            if (probe.Target == "terrain" && terrain is null)
                return new(probe.Name, probe.Shape, probe.IsSweep ? "sweep" : "overlap", "unsupported", null,
                    Diagnostic: "No managed terrain geometry was supplied for the explicit terrain target.", Target: probe.Target);
            var shape = probe.Shape switch
            {
                "box" => ZonePhysicsShape.Box(probe.HalfExtents),
                "capsule" => ZonePhysicsShape.Capsule(probe.Radius, probe.AxialHalfHeight),
                _ => ZonePhysicsShape.Cylinder(probe.Radius, probe.AxialHalfHeight)
            };
            if (!probe.IsSweep)
            {
                var overlap = probe.Target == "terrain" ? terrain!.TryCollide(shape, probe.Center) : geometry.TryCollide(shape, probe.Center);
                return new(probe.Name, probe.Shape, "overlap", overlap ? "hit" : "miss", overlap, Target: probe.Target);
            }
            ZonePhysicsHit hit;
            var found = probe.Target == "terrain" ? terrain!.TrySweep(shape, probe.Center, probe.Sweep, out hit)
                : geometry.TrySweep(shape, probe.Center, probe.Sweep, out hit);
            return found ? new(probe.Name, probe.Shape, "sweep", "hit", true, hit.Distance,
                Values(hit.Position), Values(hit.Normal), hit.PhysicalMaterialId, Target: probe.Target)
                : new(probe.Name, probe.Shape, "sweep", "miss", false, Target: probe.Target);
        });
    }

    private static void Run(string inputPath, string? outputPath, string backend, Func<Probe, Result> query)
    {
        var probes = Read(inputPath);
        var results = new List<Result>(probes.Length);
        try
        {
            foreach (var probe in probes)
            {
                // Persist the last entered case to the ordinary diagnostic log before invoking
                // original code. A process-level native failure must not hide which call began.
                Console.WriteLine($"{backend} shape probe {probe.Name}: {probe.Shape} {(probe.IsSweep ? "sweep" : "overlap")}.");
                try
                {
                    var result = query(probe);
                    results.Add(result);
                    Console.WriteLine($"  outcome={result.Outcome}, distance={result.Distance}, nativeReturn={result.NativeReturn}, contact={result.NativeContactDistance}.");
                }
                catch (Exception error)
                {
                    results.Add(new(probe.Name, probe.Shape, probe.IsSweep ? "sweep" : "overlap", "error", null,
                        Diagnostic: error.Message, Target: probe.Target));
                    throw;
                }
            }
        }
        catch
        {
            Write(false);
            throw;
        }
        Write(true);
        var unresolved = results.Count(result => result.Hit is null);
        Console.WriteLine($"{backend} scene shape probes: {results.Count(result => result.Hit == true)}/{results.Count} hits, {unresolved} unresolved.");
        if (unresolved != 0)
            throw new InvalidOperationException($"{unresolved} shape probes have unsupported or indeterminate outcomes; inspect the shape report.");

        void Write(bool complete)
        {
            if (outputPath is null) return;
            File.WriteAllText(outputPath, JsonSerializer.Serialize(new
            {
                backend, queryProfile = "explicit target per probe: static=mask1(default), terrain=mask0x100; native required/any geometry flags2/2",
                centerConvention = "raw primitive center; no actor or stance offset", complete,
                normalConvention = "normal is collider-outward for sweeps; original nativeContactNormal is retained and negated for that convention; overlap normal is not compared",
                requested = probes.Length, results
            }, Json));
            Console.WriteLine($"Scene shape probe results: {Path.GetFullPath(outputPath)}");
        }
    }

    private static Probe[] Read(string path)
    {
        var inputs = JsonSerializer.Deserialize<Input[]>(File.ReadAllText(path), Json) ??
            throw new InvalidDataException("Scene shape probes require a JSON array.");
        if (inputs.Length > 100_000) throw new InvalidDataException("Too many scene shape probes.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        return inputs.Select(input =>
        {
            if (input is null || string.IsNullOrWhiteSpace(input.Name) || !names.Add(input.Name))
                throw new InvalidDataException("Scene shape probes require unique nonempty names.");
            var center = Vector(input.Center, input.Name, "center");
            var sweep = Vector(input.Sweep, input.Name, "sweep");
            var squared = sweep.LengthSquared();
            if (!float.IsFinite(squared) || !Finite(center + sweep) || squared == 0 && sweep != Vector3.Zero)
                throw new InvalidDataException($"Shape probe {input.Name} has an unbounded sweep.");
            var shape = input.Shape?.ToLowerInvariant();
            var target = input.Target?.ToLowerInvariant() ?? "static";
            if (target is not ("static" or "terrain"))
                throw new InvalidDataException($"Shape probe {input.Name} must target static(default) or terrain geometry explicitly.");
            if (shape == "box")
            {
                if (input.Radius is not null || input.SegmentHalfHeight is not null || input.HalfHeight is not null || input.Axis is not null)
                    throw new InvalidDataException($"Shape probe {input.Name} must specify only halfExtents for a box.");
                var extents = Vector(input.HalfExtents, input.Name, "halfExtents");
                if (extents.X <= 0 || extents.Y <= 0 || extents.Z <= 0)
                    throw new InvalidDataException($"Shape probe {input.Name} requires positive box half extents.");
                return new Probe(input.Name, shape, center, sweep, extents, 0, 0, Vector3.UnitZ, target);
            }
            if (shape is not ("capsule" or "cylinder"))
                throw new InvalidDataException($"Shape probe {input.Name} must select box, capsule or cylinder.");
            if (input.HalfExtents is not null)
                throw new InvalidDataException($"Shape probe {input.Name} must specify explicit axial dimensions rather than halfExtents.");
            if (input.Radius is not { } radius || !float.IsFinite(radius) || radius <= 0)
                throw new InvalidDataException($"Shape probe {input.Name} requires a finite positive radius.");
            var axialHeight = shape == "capsule" ? input.SegmentHalfHeight : input.HalfHeight;
            if (shape == "capsule" && input.HalfHeight is not null || shape == "cylinder" && input.SegmentHalfHeight is not null ||
                axialHeight is not { } height || !float.IsFinite(height) || height < 0 || shape == "cylinder" && height == 0)
                throw new InvalidDataException($"Shape probe {input.Name} requires {(shape == "capsule" ? "nonnegative segmentHalfHeight" : "positive halfHeight")}.");
            var axis = input.Axis is null ? Vector3.UnitZ : Vector(input.Axis, input.Name, "axis");
            var squaredAxis = axis.LengthSquared();
            if (!float.IsFinite(squaredAxis) || squaredAxis <= 0)
                throw new InvalidDataException($"Shape probe {input.Name} requires a finite nonzero primitive axis.");
            return new Probe(input.Name, shape, center, sweep, default, radius, height, Vector3.Normalize(axis), target);
        }).ToArray();
    }

    private static Vector3 Vector(float[]? values, string name, string field)
    {
        if (values is not { Length: 3 })
            throw new InvalidDataException($"Shape probe {name} requires three-component {field}.");
        var value = new Vector3(values[0], values[1], values[2]);
        return Finite(value) ? value : throw new InvalidDataException($"Shape probe {name} requires finite {field}.");
    }
    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    private static float[] Values(Vector3 value) => [value.X, value.Y, value.Z];
}

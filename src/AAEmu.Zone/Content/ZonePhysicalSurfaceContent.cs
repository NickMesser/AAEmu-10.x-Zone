using System.Xml.Linq;

namespace AAEmu.Zone.Content;

[Flags]
public enum ZonePhysicalSurfaceFlags : uint
{
    None = 0,
    NoCollide = 4,
    VehicleOnlyCollisions = 8,
    CanShatter = 16
}

/// <summary>CMaterialSurfaceType's eleven fields at +28..50, in native order.</summary>
public sealed record ZoneSurfacePhysics(int BreakableId, int BreakEnergy, float HoleSize,
    float HitRadius, float HitPoints, float HitMaximumDamage, float HitLifetime,
    int Pierceability, float Friction, float Elasticity, int Breakability);

public sealed record ZoneSurfaceBreakable2D(string ParticleEffect, float BlastRadius, int RigidBody,
    float Lifetime, float CellSize, int MaximumPatchTriangles, float ShardDensity);

public sealed record ZoneSurfaceBreakageParticles(string Type, string Effect, int CountPerUnit,
    float CountScale, float Scale);

public sealed record ZoneSurfaceAiParameters(float ImpactRadius, float ImpactSoundRadius,
    float FootstepRadius, float ProneMultiplier, float CrouchMultiplier, float MovingMultiplier);

public sealed record ZonePhysicalSurfaceDefinition(int Id, string Name, string Type,
    ZonePhysicalSurfaceFlags Flags, bool HasPhysics, ZoneSurfacePhysics Physics,
    ZoneSurfaceBreakable2D? Breakable2D, ZoneSurfaceAiParameters? Ai,
    IReadOnlyList<ZoneSurfaceBreakageParticles> BreakageParticles);

/// <summary>The exact IPhysicalWorld+E8 call emitted by one authored Physics child.</summary>
public sealed record ZonePhysicalSurfaceRegistration(int SurfaceId, float Elasticity, float Friction, uint Flags);

/// <summary>
/// SurfaceTypes.xml parameter loading from 301CC130 and constructor301CD7F0. Final definitions
/// retain native duplicate-row mutations; Registrations retains every Physics call in source order.
/// No physics world, collision geometry, effects or AI services are created by parsing this content.
/// This models fresh manager construction. Native live reload can retain its prior mat_default
/// object; carrying state across such reloads is outside this immutable content snapshot.
/// </summary>
public sealed class ZonePhysicalSurfaceContent
{
    public ZonePhysicalSurfaceCatalog Catalog { get; }
    public IReadOnlyList<ZonePhysicalSurfaceDefinition> Surfaces { get; }
    public IReadOnlyList<ZonePhysicalSurfaceRegistration> Registrations { get; }

    private ZonePhysicalSurfaceContent(XDocument document)
    {
        Catalog = ZonePhysicalSurfaceCatalog.Read(document.ToString(SaveOptions.DisableFormatting));
        var definitions = Catalog.Surfaces.ToDictionary(surface => surface.Name,
            surface => new ZonePhysicalSurfaceDefinition(surface.Id, surface.Name, "", 0, false,
                new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0), null, null, Array.Empty<ZoneSurfaceBreakageParticles>()),
            StringComparer.Ordinal);
        var registrations = new List<ZonePhysicalSurfaceRegistration>();
        foreach (var row in document.Root!.Elements())
        {
            if (!ZoneTerrainSurfaceXml.NameEquals(row.Name.ToString(), "SurfaceType")) continue;
            var name = Text(row, "name");
            // The identity catalog already applies the native 100..255 registration capacity.
            if (!definitions.TryGetValue(name, out var surface)) continue;
            var type = Text(row, "type");
            if (type.Length == 0) type = name.StartsWith("mat_", StringComparison.Ordinal) ? name[4..] : name;
            surface = surface with { Type = type };
            if (ZoneTerrainSurfaceXml.Child(row, "Physics") is { } physics)
            {
                var previous = surface.Physics;
                var energy = Integer(physics, "break_energy", 0);
                var value = new ZoneSurfacePhysics(Integer(physics, "breakable_id", -1), energy,
                    Single(physics, "hole_size", previous.HoleSize), Single(physics, "hit_radius", 10000),
                    Single(physics, "hit_points", 0), Single(physics, "hit_maxdmg", 1000),
                    Single(physics, "hit_lifetime", 10), Integer(physics, "pierceability", 0),
                    Single(physics, "friction", 1), Single(physics, "elasticity", previous.Elasticity),
                    energy != 0 ? (Integer(physics, "breakable_2d", 0) != 0 ? 1 : 2) : previous.Breakability);
                var flags = surface.Flags;
                if (Integer(physics, "no_collide", 0) != 0) flags |= ZonePhysicalSurfaceFlags.NoCollide;
                if (Integer(physics, "vehicle_only_collisions", 0) != 0) flags |= ZonePhysicalSurfaceFlags.VehicleOnlyCollisions;
                if (Integer(physics, "can_shatter", 0) != 0) flags |= ZonePhysicalSurfaceFlags.CanShatter;
                surface = surface with { Physics = value, Flags = flags, HasPhysics = true };
                // These flags are separate from CMaterialSurfaceType's NoCollide/Vehicle/CanShatter flags.
                var physicalFlags = unchecked((uint)((value.BreakableId + 1) * 65536)) |
                    unchecked((uint)value.Pierceability) | (energy != 0 ? 0x400u : 0u);
                registrations.Add(new(surface.Id, value.Elasticity, value.Friction, physicalFlags));
            }
            if (ZoneTerrainSurfaceXml.Child(row, "breakable_2d") is { } breakable)
                surface = surface with { Breakable2D = new(Text(breakable, "particle_effect"),
                    Single(breakable, "blast_radius", .2f), Integer(breakable, "rigid_body", 0),
                    Single(breakable, "lifetime", 4), Single(breakable, "cell_size", .1f),
                    Integer(breakable, "max_patch_tris", 6), Single(breakable, "shard_density", 1200)) };
            var particles = surface.BreakageParticles.ToList();
            foreach (var child in row.Elements())
                if (ZoneTerrainSurfaceXml.NameEquals(child.Name.ToString(), "BreakageParticles"))
                    particles.Add(new(Text(child, "type"), Text(child, "effect"), Integer(child, "count_per_unit", 1),
                        Single(child, "count_scale", 1), Single(child, "scale", 1)));
            surface = surface with { BreakageParticles = particles.AsReadOnly() };
            if (ZoneTerrainSurfaceXml.Child(row, "AI") is { } ai)
                surface = surface with { Ai = new(Single(ai, "fImpactRadius", 2.5f),
                    Single(ai, "fImpactSoundRadius", 20), Single(ai, "fFootStepRadius", 20),
                    Single(ai, "proneMult", .2f), Single(ai, "crouchMult", .5f), Single(ai, "movingMult", 2.5f)) };
            definitions[name] = surface;
        }
        Surfaces = Array.AsReadOnly(Catalog.Surfaces.Select(surface => definitions[surface.Name]).ToArray());
        Registrations = registrations.AsReadOnly();
    }

    public static ZonePhysicalSurfaceContent Read(string xml) => new(ZoneTerrainSurfaceXml.Read(xml));
    public static ZonePhysicalSurfaceContent Load(Stream stream) => new(ZoneTerrainSurfaceXml.Load(stream));
    public static ZonePhysicalSurfaceContent LoadFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Load(stream);
    }

    private static string Text(XElement element, string name) => ZoneTerrainSurfaceXml.Attribute(element, name) ?? "";
    private static int Integer(XElement element, string name, int fallback) =>
        ZoneTerrainSurfaceXml.Attribute(element, name) is { } text ? ZoneWorldMapXmlValue.Int32(text) : fallback;
    private static float Single(XElement element, string name, float fallback) =>
        ZoneTerrainSurfaceXml.Attribute(element, name) is { } text ? ZoneWorldMapXmlValue.Single(text) : fallback;
}

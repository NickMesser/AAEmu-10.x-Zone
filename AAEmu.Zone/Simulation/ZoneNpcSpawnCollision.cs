using System.Numerics;
using AAEmu.Zone.Content;
using AAEmu.Zone.Protocol.Messages;

namespace AAEmu.Zone.Simulation;

public sealed record ZoneNpcSpawnBriefData(NpcTemplateDefinition Template, bool FlyMode, bool UnderwaterCreature, float Radius);
public readonly record struct ZoneNpcSpawnCapsule(Vector3 Center, Vector3 Axis, float Radius, float HalfHeight);

/// <summary>The actual model-owned physical entity returned by model virtual3A8.</summary>
public interface IZoneNpcSpawnPhysicalEntity { }

/// <summary>393478A2: primitive6, zero sweep, output hit buffer, then native arguments0/1/false.</summary>
public readonly record struct ZoneNpcSpawnEntityIntersection(ZoneNpcSpawnCapsule Capsule)
{
    public int PrimitiveType => 6;
    public Vector3 SweepDirection => Vector3.Zero;
    // The dedicated binary has a longer interface than current public CryPhysics headers.
    // Preserve the exact tail instead of assigning unproven meanings to its parameters.
    public nint NativeArgument7 => 0;
    public int NativeArgument8 => 1;
    public bool NativeArgument9 => false;
}

/// <summary>3934797C: no contact output, custom intersection parameters, foreign data or skipped entities.</summary>
public readonly record struct ZoneNpcSpawnWorldIntersection(ZoneNpcSpawnCapsule Capsule, int EntityTypes, Vector3 SweepDirection = default)
{
    public int PrimitiveType => 6;
    public int GeometryFlagsAll => 0;
    public int GeometryFlagsAny => 3;
    public string NameTag => "PrimitiveWorldIntersection(Game)";
}

public interface IZoneNpcSpawnAreaCollisionServices : IZoneNpcSpawnCollisionServices
{
    // AISystem virtual368/310515F8. The native building-ID output is unused by spawn admission.
    int GetNavigationType(Vector3 position, uint capabilityMask);
    // AISystem virtual258/3104AA48: true selects navigation manager+48; false selects+C8.
    bool IsPointInNavigationShape(Vector3 position, bool useFirstShapeSet);
}

public interface IZoneNpcSpawnCollisionServices
{
    // Resolve the actual model (39258830), then its physical entity (virtual3A8). Either may be absent.
    IZoneNpcSpawnPhysicalEntity? GetPhysicalEntity(ZoneUnit unit);
    int IntersectEntity(IZoneNpcSpawnPhysicalEntity entity, ZoneNpcSpawnEntityIntersection query);
    float IntersectWorld(ZoneNpcSpawnWorldIntersection query);
    // The live integer cvar3A607D38. It is read twice on successful ordinary point admission.
    int SpawnValidationMode { get; }
    void ReportInvalidNpcTemplate(uint templateId);
    void ReportOccupiedSpawn(ZoneNpcSpawnerRegistration owner, NpcSpawnRequest request);
}

/// <summary>
///3933B680/3933F260/39347670/3933F340 and39347A00. Geometry comes from loaded actor
/// stances; all physical intersections require the real engine services. One instance owns one
/// spawner registration's occupied-position counter (constructor3934A2D0 initializes it to zero).
/// </summary>
public sealed class ZoneNpcSpawnCollision
{
    private readonly ZoneUnitRegistry _units;
    private readonly ZoneNpcSpawnerRegistry _spawners;
    private readonly ZoneNpcContent _content;
    private readonly IZoneNpcSpawnCollisionServices _services;
    private bool _validating;
    private readonly object _generation;
    public ZoneNpcSpawnerRegistration Owner { get; }
    public int OccupancyFailures { get => Owner.OccupancyFailures; private set => Owner.OccupancyFailures = value; }

    public ZoneNpcSpawnCollision(ZoneUnitRegistry units, ZoneNpcSpawnerRegistration owner, ZoneNpcContent content,
        IZoneNpcSpawnCollisionServices services)
    {
        _units = units ?? throw new ArgumentNullException(nameof(units));
        Owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _generation = owner.ContentGeneration;
        _content = content ?? throw new ArgumentNullException(nameof(content));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _spawners = units.Npcs?.Spawners ?? throw new NotSupportedException("Spawn collision requires the scene's actual spawner registry.");
        if (!_spawners.IsAccepting(owner)) throw new ArgumentException("The collision owner must belong to this accepting scene.", nameof(owner));
        if (owner.UsesTriangles && services is not IZoneNpcSpawnAreaCollisionServices)
            throw new ArgumentException("Area admission requires the actual navigation-region services.", nameof(services));
    }

    /// <summary>39348AF0 chooses the first group leader;3933B680 reads the representative's model.</summary>
    public static ZoneNpcSpawnBriefData? GetBriefData(ZoneNpcSpawnerMember member, ZoneNpcContent content)
    {
        ArgumentNullException.ThrowIfNull(member); ArgumentNullException.ThrowIfNull(content);
        var npc = member.Npc;
        if (npc is null && member.Group is { LeaderIndex: >= 0 } group)
            npc = content.Templates.GetValueOrDefault(group.Members[group.LeaderIndex].NpcTemplateId);
        return npc is null ? null : GetBriefData(npc, content);
    }

    /// <summary>3933B680 reads the supplied resolved NPC descriptor and its model reference.</summary>
    public static ZoneNpcSpawnBriefData? GetBriefData(NpcTemplateDefinition npc, ZoneNpcContent content)
    {
        ArgumentNullException.ThrowIfNull(npc); ArgumentNullException.ThrowIfNull(content);
        if (content.Models?.Models.GetValueOrDefault(npc.ModelId) is not { } model) return null;
        if (model.Kind != ZoneModelKind.Actor) return new(npc, false, false, npc.Scale);
        var actor = RequireActor(content, model);
        return new(npc, actor.Definition.FlyMode, actor.Definition.UnderwaterCreature, actor.Definition.Radius * npc.Scale);
    }

    private static ZoneActorModel RequireActor(ZoneNpcContent content, ZoneModelDefinition model) =>
        content.Models?.ActorForModel(model.Id) ?? throw new NotSupportedException($"Model {model.Id} requires actor model {model.SubtypeId} and its stance content.");

    /// <summary>Return the accepted, possibly raised position; null means rejected or retired ownership.</summary>
    public Vector3? ValidatePoint(NpcSpawnRequest request, Func<bool> current)
    {
        if (!_spawners.IsAccepting(Owner) || !ReferenceEquals(_generation, Owner.ContentGeneration)) return null;
        if (Owner.UsesTriangles) throw new InvalidOperationException("Use area admission for this spawner.");
        return Validate(request, current);
    }

    public Vector3? ValidateArea(NpcSpawnRequest request, Func<bool> current)
    {
        if (!_spawners.IsAccepting(Owner) || !ReferenceEquals(_generation, Owner.ContentGeneration)) return null;
        if (!Owner.UsesTriangles) throw new InvalidOperationException("Use point admission for this spawner.");
        return Validate(request, current);
    }

    private Vector3? Validate(NpcSpawnRequest request, Func<bool> current)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(current);
        if (request.SpawnerId != Owner.Id || request.SpawnerType != Owner.SpawnerType)
            throw new ArgumentException("The request belongs to another spawner instance.", nameof(request));
        bool IsOwned() => _spawners.IsAccepting(Owner) && ReferenceEquals(_generation, Owner.ContentGeneration);
        bool IsCurrent() => IsOwned() && current() && IsOwned();
        if (!IsCurrent()) return null;
        if (_validating) throw new InvalidOperationException("The same spawner cannot validate collision recursively.");
        _validating = true;
        try
        {
            //3938E0E0 +4E0 is3938DE50's reason discriminator, not a separate collision option.
            if (Owner.Persistent || request.Reason.Kind != 0) return request.Position;
            var position = CheckOccupancy(request.TemplateId, request.Position, IsCurrent);
            if (!IsCurrent()) return null;
            if (position is null)
            {
                OccupancyFailures = unchecked(OccupancyFailures + 1);
                if (OccupancyFailures > 30)
                {
                    _services.ReportOccupiedSpawn(Owner, request);
                    if (IsCurrent()) OccupancyFailures = 0;
                }
                return null;
            }
            OccupancyFailures = 0;
            var mode = _services.SpawnValidationMode; // Native reads it before testing the area type.
            if (!IsCurrent()) return null;
            return mode != 0 && Owner.UsesTriangles ? CheckAreaGround(request.TemplateId, position.Value, IsCurrent) : position;
        }
        finally { _validating = false; }
    }

    private Vector3? CheckOccupancy(uint templateId, Vector3 position, Func<bool> current)
    {
        var dimensions = GetDimensions(templateId, current);
        if (dimensions is not { } size) return null;
        var (radius, halfHeight, height) = size;
        var capsule = new ZoneNpcSpawnCapsule(new(position.X, position.Y, height + position.Z), Vector3.UnitZ, radius, halfHeight);
        // Authored collision owners exist before 39332730 allocates the spatial grid. Native
        // persistent/reason bypass above does not query it; ordinary occupancy must resolve
        // this exact scene's live index when the query is actually required.
        var spatial = _units.Spatial ?? throw new NotSupportedException("Spawn collision requires the scene's actual unit spatial index.");
        var nearby = spatial.Query(ZoneSpatialCategory.Npc, position, QueryRadius(radius, halfHeight, height));
        if (nearby.Count > 50) return null; // Includes dead units: the native count gate precedes filtering.
        foreach (var unit in nearby)
        {
            if (!current()) return null;
            if (unit.IsDead || !IsCurrentUnit(unit)) continue;
            var physical = _services.GetPhysicalEntity(unit);
            if (!current()) return null;
            if (physical is null || !IsCurrentUnit(unit)) continue;
            var result = _services.IntersectEntity(physical, new(capsule));
            if (!current()) return null;
            if (result != 0) return null;
        }
        var mode = _services.SpawnValidationMode;
        if (!current()) return null;
        var worldResult = _services.IntersectWorld(new(capsule, mode == 0 ? 13 : 8));
        if (!current()) return null;
        return worldResult > 0 ? new(position.X, position.Y, position.Z + 1f) : position;
    }

    private Vector3? CheckAreaGround(uint templateId, Vector3 position, Func<bool> current)
    {
        var area = (IZoneNpcSpawnAreaCollisionServices)_services;
        var type = area.GetNavigationType(position, 0x1fe);
        if (!current()) return null;
        if (type == 2)
        {
            var inside = area.IsPointInNavigationShape(position, true);
            if (!current() || inside) return null;
        }
        var dimensions = GetDimensions(templateId, current);
        if (dimensions is not { } size) return null;
        var capsule = new ZoneNpcSpawnCapsule(new(position.X, position.Y, position.Z + 500f), Vector3.UnitZ, size.Radius, size.HalfHeight);
        var result = _services.IntersectWorld(new(capsule, 5, new(0, 0, -500f)));
        if (!current()) return null;
        if (!(result > 0)) return position;
        var adjustment = 500f - result;
        if (adjustment >= 1f) return null;
        return new(position.X, position.Y, position.Z + adjustment);
    }

    private (float Radius, float HalfHeight, float Height)? GetDimensions(uint templateId, Func<bool> current)
    {
        var radius = .4f; var halfHeight = .35f; var height = 1.2f;
        if (!_content.Templates.ContainsKey(templateId))
        {
            _services.ReportInvalidNpcTemplate(templateId);
            if (!current()) return null;
            height = 0; // Native missing-template path retains capsule defaults and returns zero height.
        }
        else
        {
            var model = _content.ModelForNpc(templateId) ?? throw new NotSupportedException($"NPC template {templateId} requires its model content.");
            if (model.Kind == ZoneModelKind.Actor)
                foreach (var stance in RequireActor(_content, model).Stances)
                    if (stance.StanceId == 2 && stance.UseCapsule)
                    {
                        radius = stance.SizeX; halfHeight = stance.SizeZ; height = stance.HeightCollider;
                        break;
                    }
        }
        return (radius, halfHeight, height);
    }

    private bool IsCurrentUnit(ZoneUnit unit) => ReferenceEquals(_units.Units.GetValueOrDefault(unit.Snapshot.ObjectId), unit);

    private static float QueryRadius(float radius, float halfHeight, float height)
    {
        var total = (halfHeight + height) + radius;
        var upper = ((total + 2f) - (float)Math.Abs((double)(total - 2f))) * .5f;
        return ((float)Math.Abs((double)(upper - .1f)) + (upper + .1f)) * .5f;
    }
}

using System.Collections.Frozen;
using Microsoft.Data.Sqlite;
using static AAEmu.Zone.Content.NativeContentReader;

namespace AAEmu.Zone.Content;

public enum ZoneModelKind : uint { None = 0, Actor = 1, Prefab = 2, Vehicle = 3, Ship = 4 }

/// <summary>A resolved identity in a native subtype table. Subtype physics and asset fields are separate catalogs.</summary>
public sealed record ZoneModelSubtype(ZoneModelKind Kind, uint Id);

/// <summary>All scalar columns consumed by LoadModelDescs (0x39ADC040), with native NULL defaults.</summary>
public sealed record ZoneModelDefinition
{
    public uint Id { get; init; }
    public bool AutoAdjustBindOffset { get; init; }
    public bool Big { get; init; }
    public float CameraDistance { get; init; }
    public float CameraDistanceForActionMode { get; init; }
    public float CameraDistanceForWideMode { get; init; }
    public bool DespawnDoodadOnCollision { get; init; }
    public float DyingTime { get; init; }
    public bool ForceNavigateAroundObject { get; init; }
    public uint HighImpactFxGroupId { get; init; }
    public uint LowImpactFxGroupId { get; init; }
    public uint MiddleImpactFxGroupId { get; init; }
    public uint MountPoseId { get; init; }
    public string Name { get; init; } = "";
    public float NameTagOffset { get; init; }
    public bool PlayMountAnimation { get; init; }
    public bool PlayerMountNameTagPos { get; init; }
    public bool Selectable { get; init; }
    public bool ShowNameTag { get; init; }
    public uint SoundMaterialId { get; init; }
    public uint SoundPackId { get; init; }
    public string Subtype { get; init; } = "";
    public uint SubtypeId { get; init; }
    public float TargetDecalSize { get; init; }
    public bool UseTargetDecal { get; init; }
    public bool UseTargetHighlight { get; init; }
    public bool UseTargetSilhouette { get; init; }

    // XlCompareString (0x3301E1B0) is case-sensitive and stops at the first NUL.
    public ZoneModelKind Kind => CString(Subtype) switch
    {
        "ActorModel" => ZoneModelKind.Actor,
        "PrefabModel" => ZoneModelKind.Prefab,
        "VehicleModel" => ZoneModelKind.Vehicle,
        "ShipModel" => ZoneModelKind.Ship,
        _ => ZoneModelKind.None
    };

    internal const string SelectSql = """
        SELECT id, auto_adjust_bind_offset, big, camera_distance, camera_distance_for_action_mode,
            camera_distance_for_wide_mode, despawn_doodad_on_collision, dying_time, force_navigate_around_object,
            high_impact_fx_group_id, low_impact_fx_group_id, middle_impact_fx_group_id, mount_pose_id, name,
            name_tag_offset, play_mount_animation, player_mount_name_tag_pos, selectable, show_name_tag,
            sound_material_id, sound_pack_id, sub_type, sub_id, target_decal_size, use_target_decal,
            use_target_highlight, use_target_silhouette FROM models ORDER BY id
        """;

    internal static ZoneModelDefinition Read(SqliteDataReader reader) => new()
    {
        Id = UInt32(reader, 0), AutoAdjustBindOffset = Boolean(reader, 1), Big = Boolean(reader, 2),
        CameraDistance = Single(reader, 3), CameraDistanceForActionMode = Single(reader, 4),
        CameraDistanceForWideMode = Single(reader, 5), DespawnDoodadOnCollision = Boolean(reader, 6),
        DyingTime = Single(reader, 7), ForceNavigateAroundObject = Boolean(reader, 8),
        HighImpactFxGroupId = UInt32(reader, 9), LowImpactFxGroupId = UInt32(reader, 10),
        MiddleImpactFxGroupId = UInt32(reader, 11), MountPoseId = UInt32(reader, 12),
        Name = CString(String(reader, 13)), NameTagOffset = Single(reader, 14),
        PlayMountAnimation = Boolean(reader, 15), PlayerMountNameTagPos = Boolean(reader, 16),
        Selectable = Boolean(reader, 17), ShowNameTag = Boolean(reader, 18), SoundMaterialId = UInt32(reader, 19),
        SoundPackId = UInt32(reader, 20), Subtype = String(reader, 21), SubtypeId = UInt32(reader, 22),
        TargetDecalSize = Single(reader, 23), UseTargetDecal = Boolean(reader, 24),
        UseTargetHighlight = Boolean(reader, 25), UseTargetSilhouette = Boolean(reader, 26)
    };

    private static string CString(string value)
    {
        var end = value.IndexOf('\0');
        return end < 0 ? value : value[..end];
    }
}

/// <summary>
/// Immutable model scalar definitions and subtype identity resolution. Missing subtype rows leave a null
/// reference while retaining the model and its raw source type/ID, as in 0x39ADC040. Model bindings
/// and slave-extension link vectors are supplied by ZoneModelAuxiliaryContent. Actor scalars and stance physics
/// are loaded through ActorModels; other subtype physics/asset contents are separate catalogs.
/// </summary>
public sealed class ZoneModelContent
{
    public IReadOnlyDictionary<uint, ZoneModelDefinition> Models { get; }
    public IReadOnlyDictionary<(ZoneModelKind Kind, uint Id), ZoneModelSubtype> Subtypes { get; }
    public ZoneActorModelContent ActorModels { get; }

    public ZoneModelContent(IEnumerable<ZoneModelDefinition> models, IEnumerable<ZoneModelSubtype>? subtypes = null,
        ZoneActorModelContent? actorModels = null)
    {
        ArgumentNullException.ThrowIfNull(models);
        Models = models.ToDictionary(model => model.Id).ToFrozenDictionary();
        ActorModels = actorModels ?? ZoneActorModelContent.Empty;
        var references = (subtypes ?? []).ToArray();
        if (references.Any(reference => reference.Kind is not (ZoneModelKind.Actor or ZoneModelKind.Prefab or ZoneModelKind.Vehicle or ZoneModelKind.Ship)))
            throw new ArgumentException("Subtype identities must belong to a native model subtype table.", nameof(subtypes));
        var indexedReferences = references.ToDictionary(reference => (reference.Kind, reference.Id));
        foreach (var actor in ActorModels.Models.Values)
            indexedReferences.TryAdd((ZoneModelKind.Actor, actor.Id), new(ZoneModelKind.Actor, actor.Id));
        Subtypes = indexedReferences.ToFrozenDictionary();
    }

    public ZoneModelSubtype? SubtypeForModel(uint modelId) => Models.TryGetValue(modelId, out var model)
        ? Subtypes.GetValueOrDefault((model.Kind, model.SubtypeId)) : null;

    public ZoneActorModel? ActorForModel(uint modelId) => Models.TryGetValue(modelId, out var model) && model.Kind == ZoneModelKind.Actor
        ? ActorModels.Models.GetValueOrDefault(model.SubtypeId) : null;

    internal static ZoneModelContent Load(SqliteConnection connection)
    {
        var actors = ZoneActorModelContent.Load(connection);
        var subtypes = new List<ZoneModelSubtype>();
        // The native loader resolves Actor/Prefab/Vehicle/Ship through separate preloaded indexes.
        Read(connection, "SELECT id FROM prefab_models ORDER BY id", reader => subtypes.Add(new(ZoneModelKind.Prefab, UInt32(reader, 0))));
        Read(connection, "SELECT id FROM vehicle_models ORDER BY id", reader => subtypes.Add(new(ZoneModelKind.Vehicle, UInt32(reader, 0))));
        Read(connection, "SELECT id FROM ship_models ORDER BY id", reader => subtypes.Add(new(ZoneModelKind.Ship, UInt32(reader, 0))));
        var models = new List<ZoneModelDefinition>();
        Read(connection, ZoneModelDefinition.SelectSql, reader => models.Add(ZoneModelDefinition.Read(reader)));
        return new(models, subtypes, actors);
    }
}

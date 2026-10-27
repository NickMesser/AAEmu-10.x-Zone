using System.Collections.Frozen;
using Microsoft.Data.Sqlite;
using static AAEmu.Zone.Content.NativeContentReader;

namespace AAEmu.Zone.Content;

/// <summary>39AED260 model_bindings row; its native payload is 0x28 bytes after the owner key.</summary>
public sealed record ZoneModelBindingDefinition
{
    public string OwnerType { get; init; } = "";
    public uint OwnerId { get; init; }
    public uint PassengerCheckingAttachPointId { get; init; }
    public uint AttachPointId { get; init; }
    public float BreathZ { get; init; }
    public bool CameraOffset { get; init; }
    public bool HeelHeightEnable { get; init; }
    public bool HorseRein { get; init; }
    public bool NotCollideWithCamera { get; init; }
    public float OffsetX { get; init; }
    public float OffsetY { get; init; }
    public float OffsetZ { get; init; }
    public float PassengerOffsetX { get; init; }
    public float PassengerOffsetY { get; init; }
    public float PassengerOffsetZ { get; init; }
}

/// <summary>39AD8620 consumes only id from slave_extend_models; the native record is four bytes.</summary>
public sealed record ZoneSlaveExtendModelDefinition(uint Id);

/// <summary>39AD8790 row with six borrowed C strings and a resolved slave-extension parent pointer.</summary>
public sealed record ZoneSlaveExtendModelLinkDefinition
{
    public uint Id { get; init; }
    public string Damaged25 { get; init; } = "";
    public string Damaged50 { get; init; } = "";
    public string Damaged75 { get; init; } = "";
    public string Dead { get; init; } = "";
    public string Dying { get; init; } = "";
    public uint ModelId { get; init; }
    public string Normal { get; init; } = "";
    public uint SlaveExtendModelId { get; init; }
}

/// <summary>
/// Real model+50 binding payloads and model+70 slave-extension links. Physical rows and repeated
/// owner IDs remain in native query order. A missing model or extension parent does not remove
/// the authored row; native descriptor construction resolves the available model/parent indexes.
/// </summary>
public sealed class ZoneModelAuxiliaryContent
{
    private readonly FrozenDictionary<uint, IReadOnlyList<ZoneModelBindingDefinition>> _bindingsByModel;
    private readonly FrozenDictionary<uint, IReadOnlyList<ZoneSlaveExtendModelLinkDefinition>> _linksByModel;
    public IReadOnlyList<ZoneModelBindingDefinition> BindingRows { get; }
    public IReadOnlyList<ZoneSlaveExtendModelDefinition> SlaveExtendModelRows { get; }
    public IReadOnlyDictionary<uint, ZoneSlaveExtendModelDefinition> SlaveExtendModels { get; }
    public IReadOnlyList<ZoneSlaveExtendModelLinkDefinition> SlaveExtendModelLinkRows { get; }

    public ZoneModelAuxiliaryContent(IEnumerable<ZoneModelBindingDefinition> bindings,
        IEnumerable<ZoneSlaveExtendModelDefinition> slaveExtendModels,
        IEnumerable<ZoneSlaveExtendModelLinkDefinition> slaveExtendModelLinks)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(slaveExtendModels);
        ArgumentNullException.ThrowIfNull(slaveExtendModelLinks);
        var bindingRows = bindings.ToArray();
        var parentRows = slaveExtendModels.ToArray();
        var linkRows = slaveExtendModelLinks.ToArray();
        foreach (var row in bindingRows)
        {
            ArgumentNullException.ThrowIfNull(row);
            ArgumentNullException.ThrowIfNull(row.OwnerType);
        }
        var parentsById = new Dictionary<uint, ZoneSlaveExtendModelDefinition>();
        foreach (var row in parentRows)
        {
            ArgumentNullException.ThrowIfNull(row);
            parentsById[row.Id] = row;
        }
        foreach (var row in linkRows) ArgumentNullException.ThrowIfNull(row);
        BindingRows = Array.AsReadOnly(bindingRows);
        SlaveExtendModelRows = Array.AsReadOnly(parentRows);
        SlaveExtendModels = parentsById.ToFrozenDictionary();
        SlaveExtendModelLinkRows = Array.AsReadOnly(linkRows);
        // The native multimap inserts equal unsigned owner IDs after existing equals. Its
        // equal-range traversal therefore preserves physical insertion order, including duplicates.
        _bindingsByModel = bindingRows.Where(row => IsModelOwner(row.OwnerType))
            .GroupBy(row => row.OwnerId).ToFrozenDictionary(group => group.Key,
                group => (IReadOnlyList<ZoneModelBindingDefinition>)Array.AsReadOnly(group.ToArray()));
        _linksByModel = linkRows.GroupBy(row => row.ModelId).ToFrozenDictionary(group => group.Key,
            group => (IReadOnlyList<ZoneSlaveExtendModelLinkDefinition>)Array.AsReadOnly(group.ToArray()));
    }

    public IReadOnlyList<ZoneModelBindingDefinition> BindingsForModel(uint modelId) =>
        _bindingsByModel.GetValueOrDefault(modelId) ?? Array.Empty<ZoneModelBindingDefinition>();
    public IReadOnlyList<ZoneSlaveExtendModelLinkDefinition> SlaveExtendModelLinksForModel(uint modelId) =>
        _linksByModel.GetValueOrDefault(modelId) ?? Array.Empty<ZoneSlaveExtendModelLinkDefinition>();
    public ZoneSlaveExtendModelDefinition? ParentForLink(ZoneSlaveExtendModelLinkDefinition link)
    {
        ArgumentNullException.ThrowIfNull(link);
        return SlaveExtendModels.GetValueOrDefault(link.SlaveExtendModelId);
    }

    internal static bool IsModelOwner(string ownerType)
    {
        var terminator = ownerType.IndexOf('\0');
        return (terminator < 0 ? ownerType : ownerType[..terminator]) == "Model";
    }

    internal static ZoneModelAuxiliaryContent Load(SqliteConnection connection)
    {
        var parents = new List<ZoneSlaveExtendModelDefinition>();
        Read(connection, "SELECT id FROM slave_extend_models", reader => parents.Add(new(UInt32(reader, 0))));
        var links = new List<ZoneSlaveExtendModelLinkDefinition>();
        Read(connection, "SELECT id, damaged25, damaged50, damaged75, dead, dying, model_id, normal, slave_extend_model_id FROM slave_extend_model_links",
            reader => links.Add(new()
            {
                Id = UInt32(reader, 0), Damaged25 = String(reader, 1), Damaged50 = String(reader, 2),
                Damaged75 = String(reader, 3), Dead = String(reader, 4), Dying = String(reader, 5),
                ModelId = UInt32(reader, 6), Normal = String(reader, 7), SlaveExtendModelId = UInt32(reader, 8)
            }));
        var bindings = new List<ZoneModelBindingDefinition>();
        Read(connection, "SELECT owner_type, owner_id, ap_for_checking_passenger_id, attach_point_id, breath_z, camera_offset, heel_height_enable, horse_rein, not_collide_with_camera, offset_x, offset_y, offset_z, passenger_offset_x, passenger_offset_y, passenger_offset_z FROM model_bindings",
            reader =>
            {
                //39AED260 immediately uses strlen on owner_type; a NULL has no valid native
                // key. Reject invalid source admission instead of fabricating an empty owner.
                if (reader.IsDBNull(0)) throw new InvalidDataException("A native model-binding owner type is null.");
                bindings.Add(new()
                {
                    OwnerType = String(reader, 0), OwnerId = UInt32(reader, 1),
                    PassengerCheckingAttachPointId = UInt32(reader, 2), AttachPointId = UInt32(reader, 3),
                    BreathZ = Single(reader, 4), CameraOffset = Boolean(reader, 5),
                    HeelHeightEnable = Boolean(reader, 6), HorseRein = Boolean(reader, 7),
                    NotCollideWithCamera = Boolean(reader, 8), OffsetX = Single(reader, 9),
                    OffsetY = Single(reader, 10), OffsetZ = Single(reader, 11),
                    PassengerOffsetX = Single(reader, 12), PassengerOffsetY = Single(reader, 13),
                    PassengerOffsetZ = Single(reader, 14)
                });
            });
        return new(bindings, parents, links);
    }
}

using System.Collections.Frozen;
using Microsoft.Data.Sqlite;
using static AAEmu.Zone.Content.NativeContentReader;

namespace AAEmu.Zone.Content;

/// <summary>All scalar system_factions columns consumed by native loader 39C063F0.</summary>
public sealed record SystemFactionDefinition(
    uint Id, bool AggroLink, string DescriptionWhenCreatingExpedition, bool GuardHelp, uint IconId,
    bool IntegrationFaction, bool DiplomacyTarget, uint MotherId, string Name, uint OwnerId,
    string OwnerName, int OwnerType, int PoliticalSystem, bool ShowCreateExpedition);

/// <summary>39C067E0 preserves raw ordered pairs and the full signed 32-bit state.</summary>
public sealed record SystemFactionRelationDefinition(uint Id, uint FirstId, uint SecondId, int State);

/// <summary>
/// Immutable game-content definitions. Runtime faction ancestry and diplomacy use World's
/// connection snapshots; these static rows do not seed or override ZoneFactionState.
/// </summary>
public sealed class ZoneFactionContent
{
    public IReadOnlyDictionary<uint, SystemFactionDefinition> Factions { get; }
    public IReadOnlyList<SystemFactionRelationDefinition> Relations { get; }

    public ZoneFactionContent(IEnumerable<SystemFactionDefinition> factions, IEnumerable<SystemFactionRelationDefinition> relations)
    {
        Factions = factions.ToDictionary(row => row.Id).ToFrozenDictionary();
        Relations = Array.AsReadOnly(relations.ToDictionary(row => row.Id).Values.ToArray());
    }

    internal static ZoneFactionContent Load(SqliteConnection connection)
    {
        var factions = new List<SystemFactionDefinition>();
        // Names/descriptions retain source text; native localization and icon resolution are separate loaders.
        Read(connection, """
            SELECT id, aggro_link, desc_when_use_create_expedition, guard_help, icon_id,
                integration_faction, is_diplomacy_tgt, mother_id, name, owner_id, owner_name,
                owner_type_id, political_system_id, show_create_expedition FROM system_factions ORDER BY id
            """, reader => factions.Add(new(UInt32(reader, 0), Boolean(reader, 1), String(reader, 2),
                Boolean(reader, 3), UInt32(reader, 4), Boolean(reader, 5), Boolean(reader, 6), UInt32(reader, 7),
                String(reader, 8), UInt32(reader, 9), String(reader, 10), Int32(reader, 11), Int32(reader, 12), Boolean(reader, 13))));
        var relations = new List<SystemFactionRelationDefinition>();
        Read(connection, "SELECT id, faction1_id, faction2_id, state_id FROM system_faction_relations ORDER BY id", reader =>
            relations.Add(new(UInt32(reader, 0), UInt32(reader, 1), UInt32(reader, 2), Int32(reader, 3))));
        return new(factions, relations);
    }
}

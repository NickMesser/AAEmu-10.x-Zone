using System.Collections.Frozen;
using Microsoft.Data.Sqlite;
using static AAEmu.Zone.Content.NativeContentReader;

namespace AAEmu.Zone.Content;

public enum NpcSignalIgnoreCategory : uint { None, Npc, Target }
public sealed record NpcAiEventDefinition(uint Id, NpcSignalIgnoreCategory IgnoreCategory, float IgnoreTime,
    string Name, uint NpcTemplateId, bool OrUnitRequirements, uint SkillId);
public sealed record NpcChatBubble(uint Id, uint EventId, bool ShowUi);
public sealed record ZoneUnitRequirement(uint KindId, int Value1, int Value2, int Value3, bool DisplayMessage);

public sealed class NpcSignalDefinition
{
    public NpcAiEventDefinition Event { get; }
    public IReadOnlyList<NpcChatBubble> Bubbles { get; }
    public IReadOnlyList<ZoneUnitRequirement> Requirements { get; }

    internal NpcSignalDefinition(NpcAiEventDefinition definition, IEnumerable<NpcChatBubble> bubbles,
        IEnumerable<ZoneUnitRequirement> requirements)
    { Event = definition; Bubbles = Array.AsReadOnly(bubbles.ToArray()); Requirements = Array.AsReadOnly(requirements.ToArray()); }
}

/// <summary>AI signal rules and dialogue IDs. World/client content owns localized bubble text.</summary>
public sealed class ZoneNpcSignalContent
{
    public static ZoneNpcSignalContent Empty { get; } = new([], [], []);
    public IReadOnlyDictionary<uint, NpcSignalDefinition> Events { get; }
    public IReadOnlyDictionary<uint, NpcChatBubble> Bubbles { get; }
    private readonly FrozenDictionary<uint, IReadOnlyList<NpcSignalDefinition>> _byNpc;

    public ZoneNpcSignalContent(IEnumerable<NpcAiEventDefinition> events, IEnumerable<NpcChatBubble> bubbles,
        IEnumerable<KeyValuePair<uint, ZoneUnitRequirement>> requirements)
    {
        var bubbleRows = bubbles.ToDictionary(row => row.Id).Values.ToArray();
        Bubbles = bubbleRows.ToFrozenDictionary(row => row.Id);
        var byEvent = bubbleRows.ToLookup(row => row.EventId);
        var byOwner = requirements.ToLookup(row => row.Key, row => row.Value);
        var rules = events.Select(row => new NpcSignalDefinition(row, byEvent[row.Id], byOwner[row.Id])).ToArray();
        Events = rules.ToFrozenDictionary(row => row.Event.Id);
        _byNpc = rules.GroupBy(row => row.Event.NpcTemplateId).ToFrozenDictionary(group => group.Key,
            group => (IReadOnlyList<NpcSignalDefinition>)Array.AsReadOnly(group.ToArray()));
    }

    public IReadOnlyList<NpcSignalDefinition> ForNpc(uint templateId) => _byNpc.GetValueOrDefault(templateId) ?? [];

    internal static ZoneNpcSignalContent Load(SqliteConnection connection)
    {
        var events = new List<NpcAiEventDefinition>();
        // 39AFD1E0 retains raw skill and NPC IDs, including unresolved references.
        Read(connection, """
            SELECT id, ignore_category_id, ignore_time, name, npc_id, or_unit_reqs, skill_id
            FROM ai_events ORDER BY id
            """, r => events.Add(new(UInt32(r, 0), (NpcSignalIgnoreCategory)UInt32(r, 1), Single(r, 2),
                String(r, 3), UInt32(r, 4), Boolean(r, 5), UInt32(r, 6))));
        var bubbles = new List<NpcChatBubble>();
        Read(connection, "SELECT id, ai_event_id, show_ui FROM npc_chat_bubbles ORDER BY id", r =>
            bubbles.Add(new(UInt32(r, 0), UInt32(r, 1), Boolean(r, 2))));
        var requirements = new List<KeyValuePair<uint, ZoneUnitRequirement>>();
        // 39C15580 applies exact SQL enable='t', then indexes the C-string owner type.
        Read(connection, """
            SELECT owner_type, owner_id, display_msg, kind_id, value1, value2, value3
            FROM unit_reqs WHERE enable = 't' ORDER BY id
            """, r =>
        {
            if (String(r, 0).Split('\0')[0] == "AiEvent")
                requirements.Add(new(UInt32(r, 1), new(UInt32(r, 3), Int32(r, 4), Int32(r, 5), Int32(r, 6), Boolean(r, 2))));
        });
        return new(events, bubbles, requirements);
    }
}

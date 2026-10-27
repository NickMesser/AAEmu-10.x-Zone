using System.Collections.Frozen;
using Microsoft.Data.Sqlite;
using static AAEmu.Zone.Content.NativeContentReader;

namespace AAEmu.Zone.Content;

/// <summary>Shared immutable LoadNpcPostureDescs row; animation IDs are retained without resolution.</summary>
public sealed class ZoneNpcPosture
{
    public uint Id { get; }
    public uint AnimationId { get; }
    public uint PostureSetId { get; }
    public int StartHour { get; }
    public string TalkAnimation { get; }

    public ZoneNpcPosture(uint animationId, int startHour) : this(0, animationId, 0, startHour, "") { }

    public ZoneNpcPosture(uint id, uint animationId, uint postureSetId, int startHour, string? talkAnimation)
    {
        Id = id;
        AnimationId = animationId;
        PostureSetId = postureSetId;
        StartHour = startHour;
        talkAnimation ??= "";
        var terminator = talkAnimation.IndexOf('\0');
        TalkAnimation = terminator < 0 ? talkAnimation : talkAnimation[..terminator];
    }
}

public sealed record ZoneNpcPostureSetDefinition(uint Id, uint QuestAnimationId);

public sealed class ZoneNpcPostureSet
{
    public ZoneNpcPostureSetDefinition Definition { get; }
    public uint Id => Definition.Id;
    public uint QuestAnimationId => Definition.QuestAnimationId;
    public IReadOnlyList<ZoneNpcPosture> Rows { get; }

    internal ZoneNpcPostureSet(ZoneNpcPostureSetDefinition definition, IEnumerable<ZoneNpcPosture> rows)
    {
        Definition = definition;
        Rows = Array.AsReadOnly(rows.ToArray());
    }
}

/// <summary>Native posture rows and set links, loaded by 39AFDA50 then 39AFDC70.</summary>
public sealed class ZoneNpcPostureContent
{
    public static ZoneNpcPostureContent Empty { get; } = new([], []);
    public IReadOnlyList<ZoneNpcPosture> Rows { get; }
    public IReadOnlyDictionary<uint, ZoneNpcPosture> Postures { get; }
    public IReadOnlyList<ZoneNpcPostureSet> SetRows { get; }
    public IReadOnlyDictionary<uint, ZoneNpcPostureSet> Sets { get; }

    public ZoneNpcPostureContent(IEnumerable<ZoneNpcPosture> postures,
        IEnumerable<ZoneNpcPostureSetDefinition> sets)
    {
        ArgumentNullException.ThrowIfNull(postures);
        ArgumentNullException.ThrowIfNull(sets);
        var rows = postures.ToArray();
        var definitions = sets.ToArray();
        var byId = new Dictionary<uint, ZoneNpcPosture>();
        foreach (var row in rows)
        {
            ArgumentNullException.ThrowIfNull(row);
            byId[row.Id] = row;
        }
        var lastSetIndex = new Dictionary<uint, int>();
        for (var index = 0; index < definitions.Length; index++)
        {
            ArgumentNullException.ThrowIfNull(definitions[index]);
            lastSetIndex[definitions[index].Id] = index;
        }
        var bySet = rows.ToLookup(row => row.PostureSetId);
        // Native ID indexes overwrite duplicates, while the source arrays retain every row.
        // Linking walks that posture array and appends only to the final indexed set descriptor.
        var setRows = definitions.Select((definition, index) => new ZoneNpcPostureSet(definition,
            lastSetIndex[definition.Id] == index ? bySet[definition.Id] : [])).ToArray();
        Rows = Array.AsReadOnly(rows);
        Postures = byId.ToFrozenDictionary();
        SetRows = Array.AsReadOnly(setRows);
        Sets = lastSetIndex.ToFrozenDictionary(pair => pair.Key, pair => setRows[pair.Value]);
    }

    /// <summary>An existing empty set returns an empty list; an unresolved set returns null.</summary>
    public IReadOnlyList<ZoneNpcPosture>? FindPostures(uint postureSetId) =>
        Sets.GetValueOrDefault(postureSetId)?.Rows;

    internal static ZoneNpcPostureContent Load(SqliteConnection connection)
    {
        var rows = new List<ZoneNpcPosture>();
        // These exact native queries have no ORDER BY. Equal start hours keep the first source row.
        Read(connection, "SELECT id, anim_action_id, npc_posture_set_id, start_tod_time, talk_anim FROM npc_postures",
            reader => rows.Add(new(UInt32(reader, 0), UInt32(reader, 1), UInt32(reader, 2),
                Int32(reader, 3), String(reader, 4))));
        var sets = new List<ZoneNpcPostureSetDefinition>();
        Read(connection, "SELECT id, quest_anim_action_id FROM npc_posture_sets",
            reader => sets.Add(new(UInt32(reader, 0), UInt32(reader, 1))));
        return new(rows, sets);
    }
}

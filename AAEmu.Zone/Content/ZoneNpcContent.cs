using System.Collections.Frozen;
using System.Numerics;
using Microsoft.Data.Sqlite;
using static AAEmu.Zone.Content.NativeContentReader;

namespace AAEmu.Zone.Content;

public enum NpcGroupAggroRule : uint { None, Link, Share }
public enum NpcAggroLinkRule : uint { None, FactionHelp, FriendlyHelp, NeutralHelp, EveryoneHelp }
public sealed record NpcAiFile(uint Id, string Name, string ParameterTemplate);
public sealed record NpcAiParameters(uint Id, string Text);
public sealed record NpcAggroLinkMember(uint Id, uint LinkId, uint NpcTemplateId);
public sealed record NpcGroupDefinition(uint Id, NpcGroupAggroRule AggroRule, bool EnableRespawn, string Name);
public sealed record NpcGroupMember(uint Id, uint GroupId, uint NpcTemplateId, Vector3 FormationOffset,
    float FormationTension, bool IsLeader, bool IsMoveLeader);

public sealed class NpcGroupContent
{
    public NpcGroupDefinition Definition { get; }
    public IReadOnlyList<NpcGroupMember> Members { get; }
    public int LeaderIndex { get; }
    public int MoveLeaderIndex { get; }

    internal NpcGroupContent(NpcGroupDefinition definition, IEnumerable<NpcGroupMember> members)
    {
        Definition = definition;
        var copy = members.ToList();
        Members = copy.AsReadOnly();
        // 39346960 uses the first movement leader, and permits no such member.
        LeaderIndex = copy.FindIndex(member => member.IsLeader);
        MoveLeaderIndex = copy.FindIndex(member => member.IsMoveLeader);
    }
}

/// <summary>
/// Immutable native NPC scalar, AI, group-formation and aggro-link content.
/// Actor/model creation, AI script execution and other NPC child tables are separate systems.
/// </summary>
public sealed class ZoneNpcContent
{
    private readonly FrozenDictionary<uint, IReadOnlyList<NpcAggroLinkMember>> _linksByNpc;
    private readonly FrozenDictionary<uint, IReadOnlyList<uint>> _linkedTemplates;
    public IReadOnlyDictionary<uint, NpcTemplateDefinition> Templates { get; }
    public IReadOnlyDictionary<uint, NpcGroupContent> Groups { get; }
    public IReadOnlyDictionary<uint, NpcGroupMember> GroupMembers { get; }
    public IReadOnlyDictionary<uint, IReadOnlyList<NpcAggroLinkMember>> AggroLinks { get; }
    public IReadOnlyDictionary<uint, NpcAggroLinkMember> AggroLinkMembers { get; }
    public IReadOnlyDictionary<uint, NpcAiFile> AiFiles { get; }
    public IReadOnlyDictionary<uint, NpcAiParameters> AiParameters { get; }
    public ZoneNpcSignalContent Signals { get; }
    public ZoneModelContent? Models { get; }
    public ZoneNpcPostureContent Postures { get; }
    public ZoneNpcSpawnerContent Spawners { get; private set; }

    public ZoneNpcContent(IEnumerable<NpcTemplateDefinition> templates, IEnumerable<NpcGroupDefinition> groups,
        IEnumerable<NpcGroupMember> groupMembers, IEnumerable<uint> aggroLinks,
        IEnumerable<NpcAggroLinkMember> aggroLinkMembers, IEnumerable<NpcAiFile>? aiFiles = null,
        IEnumerable<NpcAiParameters>? aiParameters = null, ZoneNpcSignalContent? signals = null,
        ZoneModelContent? models = null, ZoneNpcPostureContent? postures = null, ZoneNpcSpawnerContent? spawners = null)
    {
        Templates = templates.ToDictionary(row => row.Id).ToFrozenDictionary();
        AiFiles = (aiFiles ?? []).ToDictionary(row => row.Id).ToFrozenDictionary();
        AiParameters = (aiParameters ?? []).ToDictionary(row => row.Id).ToFrozenDictionary();
        Signals = signals ?? ZoneNpcSignalContent.Empty;
        Models = models;
        Postures = postures ?? ZoneNpcPostureContent.Empty;
        Spawners = spawners ?? ZoneNpcSpawnerContent.Empty;
        var memberRows = groupMembers.ToDictionary(row => row.Id).Values.ToArray();
        // LoadNpcGroupMemberDescs (39AFE6F0) skips members whose NPC template cannot resolve.
        GroupMembers = memberRows.Where(row => Templates.ContainsKey(row.NpcTemplateId)).ToFrozenDictionary(row => row.Id);
        var membersByGroup = memberRows.Where(row => Templates.ContainsKey(row.NpcTemplateId)).ToLookup(row => row.GroupId);
        // LoadNpcGroupDescs (39AFE9E0) only attaches members to groups which exist.
        Groups = groups.ToDictionary(row => row.Id, row => new NpcGroupContent(row, membersByGroup[row.Id])).ToFrozenDictionary();

        var linkRows = aggroLinkMembers.ToDictionary(row => row.Id).Values.ToArray();
        AggroLinkMembers = linkRows.ToFrozenDictionary(row => row.Id);
        var rowsByLink = linkRows.ToLookup(row => row.LinkId);
        // 39AFC750 attaches raw link rows without requiring their NPC template to exist.
        // Duplicate (link, NPC) pairs are significant source rows and are never deduplicated.
        AggroLinks = aggroLinks.ToDictionary(id => id,
            id => (IReadOnlyList<NpcAggroLinkMember>)Array.AsReadOnly(rowsByLink[id].ToArray())).ToFrozenDictionary();
        _linksByNpc = linkRows.Where(row => Templates.ContainsKey(row.NpcTemplateId)).GroupBy(row => row.NpcTemplateId)
            .ToFrozenDictionary(group => group.Key, group => (IReadOnlyList<NpcAggroLinkMember>)Array.AsReadOnly(group.ToArray()));
        // 39321C80 expands each of the victim's links into that link's NPC rows, in order.
        _linkedTemplates = _linksByNpc.ToFrozenDictionary(pair => pair.Key, pair => (IReadOnlyList<uint>)Array.AsReadOnly(
            pair.Value.SelectMany(row => AggroLinks.TryGetValue(row.LinkId, out var linked)
                ? linked.Select(member => member.NpcTemplateId) : []).ToArray()));
    }

    public IReadOnlyList<NpcAggroLinkMember> LinksForNpc(uint npcTemplateId) =>
        _linksByNpc.GetValueOrDefault(npcTemplateId) ?? [];
    public IReadOnlyList<uint> LinkedNpcTemplateIds(uint npcTemplateId) =>
        _linkedTemplates.GetValueOrDefault(npcTemplateId) ?? [];
    public NpcAiFile? AiForNpc(uint npcTemplateId) => Templates.TryGetValue(npcTemplateId, out var npc)
        ? AiFiles.GetValueOrDefault(npc.AiFileId) : null;
    public NpcAiParameters? ParametersForNpc(uint npcTemplateId) => Templates.TryGetValue(npcTemplateId, out var npc)
        ? AiParameters.GetValueOrDefault(npc.NpcAiParamId) : null;
    public ZoneModelDefinition? ModelForNpc(uint npcTemplateId) => Templates.TryGetValue(npcTemplateId, out var npc)
        ? Models?.Models.GetValueOrDefault(npc.ModelId) : null;
    public ZoneNpcPostureSet? PostureSetForNpc(uint npcTemplateId) => Templates.TryGetValue(npcTemplateId, out var npc)
        ? Postures.Sets.GetValueOrDefault(npc.NpcPostureSetId) : null;

    internal static ZoneNpcContent Load(SqliteConnection connection, ZoneModelContent models, bool loadSpawnerContent)
    {
        var templates = new List<NpcTemplateDefinition>();
        Read(connection, NpcTemplateDefinition.SelectSql, reader => templates.Add(NpcTemplateDefinition.Read(reader)));
        var groups = new List<NpcGroupDefinition>();
        Read(connection, "SELECT id, aggro_rule_id, enable_respawn, name FROM npc_groups ORDER BY id", reader =>
            groups.Add(new(UInt32(reader, 0), (NpcGroupAggroRule)UInt32(reader, 1), Boolean(reader, 2), String(reader, 3))));
        var groupMembers = new List<NpcGroupMember>();
        Read(connection, """
            SELECT id, formation_offset_x, formation_offset_y, formation_offset_z, formation_tension,
                is_leader, is_move_leader, npc_id, npc_group_id FROM npc_group_members ORDER BY id
            """, reader => groupMembers.Add(new(UInt32(reader, 0), UInt32(reader, 8), UInt32(reader, 7),
                new(Single(reader, 1), Single(reader, 2), Single(reader, 3)), Single(reader, 4), Boolean(reader, 5), Boolean(reader, 6))));
        var links = new List<uint>();
        Read(connection, "SELECT id FROM aggro_links ORDER BY id", reader => links.Add(UInt32(reader, 0)));
        var linkMembers = new List<NpcAggroLinkMember>();
        Read(connection, "SELECT id, aggro_link_id, npc_id FROM npc_aggro_links ORDER BY id", reader =>
            linkMembers.Add(new(UInt32(reader, 0), UInt32(reader, 1), UInt32(reader, 2))));
        var aiFiles = new List<NpcAiFile>();
        Read(connection, "SELECT id, name, param_template FROM ai_files ORDER BY id", reader =>
            aiFiles.Add(new(UInt32(reader, 0), String(reader, 1), String(reader, 2))));
        var parameters = new List<NpcAiParameters>();
        Read(connection, "SELECT id, ai_param FROM npc_ai_params ORDER BY id", reader =>
            parameters.Add(new(UInt32(reader, 0), String(reader, 1))));
        var content = new ZoneNpcContent(templates, groups, groupMembers, links, linkMembers, aiFiles, parameters,
            ZoneNpcSignalContent.Load(connection), models, ZoneNpcPostureContent.Load(connection));
        // Publish only after polymorphic member references can share these exact NPC/group objects.
        content.Spawners = ZoneNpcSpawnerContent.Load(connection, content.Templates, content.Groups, loadSpawnerContent);
        return content;
    }
}

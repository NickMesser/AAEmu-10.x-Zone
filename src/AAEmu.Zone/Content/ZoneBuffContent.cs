using System.Collections.Frozen;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace AAEmu.Zone.Content;

public sealed record BuffStatTemplate(uint Id, byte Kind, byte StackRule, int MaximumStack, int MaximumCharge,
    uint MaximumLifetime, bool PerUnitCreation, bool OneTime, bool OffPassive = false, uint PassiveExceptionTagId = 0,
    bool RemoveOnLand = false, bool Gliding = false, float Scale = 1f, float ScaleDuration = 1f,
    bool Stun = false, bool Sleep = false, bool Root = false, bool Psychokinesis = false, float PsychokinesisSpeed = 0f);
public sealed record PassiveBuffTemplate(uint Id, uint BuffId, bool Active, uint AbilityId = 0, int RequiredLevel = 1);
public sealed record ConditionalBuffModifier(uint Id, uint OwnerBuffId, uint RequiredBuffId, uint RequiredTagId);
public sealed record TaggedBuffDefinition(uint Id, uint BuffId, uint TagId);
public enum BuffCurveKind { Linear, Manual, Dynamic, Formula }
public sealed record BuffStatCurve(uint Id, uint BuffId, uint Attribute, byte ModifierType, BuffCurveKind Kind,
    int Start = 0, int End = 0, IReadOnlyList<int>? Values = null, uint SourceAttribute = 0, int Percentage = 0,
    ContentFormula? Formula = null);

/// <summary>Read-only buff metadata and relational stat inputs, independent of an open database.</summary>
public sealed class ZoneBuffContent
{
    public static ZoneBuffContent Empty { get; } = new([], [], [], [], [], []);
    public IReadOnlyDictionary<uint, BuffStatTemplate> Templates { get; }
    public IReadOnlyDictionary<uint, PassiveBuffTemplate> Passives { get; }
    public IReadOnlyList<TaggedBuffDefinition> TaggedBuffs { get; }
    private readonly FrozenDictionary<uint, IReadOnlyList<ConditionalBuffModifier>> _conditions;
    private readonly FrozenDictionary<uint, IReadOnlyList<BuffStatCurve>> _curves;
    private readonly FrozenDictionary<uint, IReadOnlySet<uint>> _tags;
    private readonly FrozenDictionary<uint, IReadOnlySet<uint>> _runtimeTags;
    private readonly FrozenDictionary<uint, IReadOnlySet<uint>> _runtimeBuffs;
    private readonly FrozenDictionary<uint, IReadOnlyList<uint>> _grantedPassives;

    public ZoneBuffContent(IEnumerable<BuffStatTemplate> templates, IEnumerable<PassiveBuffTemplate> passives,
        IEnumerable<ConditionalBuffModifier> conditions, IEnumerable<BuffStatCurve> curves,
        IEnumerable<TaggedBuffDefinition> tags, IEnumerable<(uint BuffId, uint PassiveId)> grantedPassives)
    {
        Templates = templates.ToDictionary(v => v.Id).ToFrozenDictionary();
        Passives = passives.ToDictionary(v => v.Id).ToFrozenDictionary();
        if (Templates.Values.Any(v => v.Kind is < 1 or > 3 || v.StackRule is < 1 or > 7))
            throw new InvalidDataException("Buff kind or stack rule is outside the native content domain.");
        _conditions = conditions.GroupBy(v => v.OwnerBuffId).ToDictionary(g => g.Key,
            g => (IReadOnlyList<ConditionalBuffModifier>)g.OrderBy(v => v.Id).ToList().AsReadOnly()).ToFrozenDictionary();
        _curves = curves.GroupBy(v => v.BuffId).ToDictionary(g => g.Key,
            g => (IReadOnlyList<BuffStatCurve>)g.OrderBy(v => v.Id).Select(v => v with
            { Values = v.Values is null ? null : Array.AsReadOnly(v.Values.ToArray()) }).ToList().AsReadOnly()).ToFrozenDictionary();
        TaggedBuffs = Array.AsReadOnly(tags.ToArray());
        // 39B42380 attaches template tags only to existing buff definitions. Runtime indexes
        // (39C56B80) include every non-excluded tagged_buffs row, including null/zero IDs.
        _tags = IndexTags(TaggedBuffs.Where(row => Templates.ContainsKey(row.BuffId)));
        _runtimeTags = IndexTags(TaggedBuffs);
        _runtimeBuffs = IndexTags(TaggedBuffs, reverse: true);
        _grantedPassives = grantedPassives.GroupBy(v => v.BuffId).ToDictionary(g => g.Key,
            g => (IReadOnlyList<uint>)g.Select(v => v.PassiveId).ToList().AsReadOnly()).ToFrozenDictionary();
    }

    public BuffStatTemplate Template(uint id) => Templates.TryGetValue(id, out var value) ? value :
        throw new InvalidDataException($"Unknown buff template {id} in the configured content.");
    public PassiveBuffTemplate Passive(uint id) => Passives.TryGetValue(id, out var value) ? value :
        throw new InvalidDataException($"Unknown passive buff {id} in the configured content.");
    public IReadOnlyList<ConditionalBuffModifier> Conditions(uint id) => _conditions.GetValueOrDefault(id) ?? [];
    public IReadOnlyList<BuffStatCurve> Curves(uint id) => _curves.GetValueOrDefault(id) ?? [];
    public bool HasTag(uint buffId, uint tagId) => _tags.TryGetValue(buffId, out var tags) && tags.Contains(tagId);
    public bool HasRuntimeTag(uint buffId, uint tagId) => _runtimeTags.TryGetValue(buffId, out var tags) && tags.Contains(tagId);
    public IReadOnlySet<uint> RuntimeTags(uint buffId) => _runtimeTags.GetValueOrDefault(buffId) ?? FrozenSet<uint>.Empty;
    public IReadOnlySet<uint> RuntimeBuffs(uint tagId) => _runtimeBuffs.GetValueOrDefault(tagId) ?? FrozenSet<uint>.Empty;
    public IReadOnlyList<uint> GrantedPassives(uint id) => _grantedPassives.GetValueOrDefault(id) ?? [];

    public ZoneBuffContent WithExcludedRuntimeTags(IReadOnlySet<uint> excludedRowIds) => new(this, excludedRowIds);

    private ZoneBuffContent(ZoneBuffContent source, IReadOnlySet<uint> excludedRowIds)
    {
        Templates = source.Templates;
        Passives = source.Passives;
        TaggedBuffs = source.TaggedBuffs;
        _conditions = source._conditions;
        _curves = source._curves;
        _tags = source._tags;
        _grantedPassives = source._grantedPassives;
        var active = TaggedBuffs.Where(row => !excludedRowIds.Contains(row.Id)).ToArray();
        _runtimeTags = IndexTags(active);
        _runtimeBuffs = IndexTags(active, reverse: true);
    }

    private static FrozenDictionary<uint, IReadOnlySet<uint>> IndexTags(IEnumerable<TaggedBuffDefinition> rows, bool reverse = false) =>
        rows.GroupBy(row => reverse ? row.TagId : row.BuffId).ToFrozenDictionary(group => group.Key,
            group => (IReadOnlySet<uint>)group.Select(row => reverse ? row.BuffId : row.TagId).ToFrozenSet());

    internal static ZoneBuffContent Load(SqliteConnection connection)
    {
        var templates = new List<BuffStatTemplate>();
        //39AD3980 reads these exact columns:204 stun->1134&2,195 sleep->1133&8,
        //184 root->1132&0x10,132 psychokinesis->1127&2,133 speed->float328.
        Read("SELECT id, kind_id, stack_rule_id, max_stack, max_charge, max_life_time, per_unit_creation, one_time, off_passive, COALESCE(off_passive_exection_tag_id, 0), remove_on_land, gliding, scale, scaleDuration, stun, sleep, root, psychokinesis, psychokinesis_speed FROM buffs", r =>
            templates.Add(new(U(r, 0), checked((byte)U(r, 1)), checked((byte)U(r, 2)), r.GetInt32(3), r.GetInt32(4), U(r, 5), B(r, 6), B(r, 7), B(r, 8), U(r, 9), B(r, 10), B(r, 11),
                NativeContentReader.Single(r, 12), NativeContentReader.Single(r, 13), B(r, 14), B(r, 15), B(r, 16), B(r, 17), NativeContentReader.Single(r, 18))));
        var passives = new List<PassiveBuffTemplate>();
        Read("SELECT id, buff_id, active, ability_id, level FROM passive_buffs", r => passives.Add(new(U(r, 0), U(r, 1), B(r, 2), U(r, 3), r.GetInt32(4))));
        var conditions = new List<ConditionalBuffModifier>();
        Read("SELECT id, owner_id, COALESCE(buff_id, 0), COALESCE(tag_id, 0) FROM buff_unit_modifiers WHERE owner_type='Buff' AND enable IN ('t', 'true', 1)", r =>
            conditions.Add(new(U(r, 0), U(r, 1), U(r, 2), U(r, 3))));
        var tags = new List<TaggedBuffDefinition>();
        Read("SELECT id, buff_id, tag_id FROM tagged_buffs", r => tags.Add(new(
            NativeContentReader.UInt32(r, 0), NativeContentReader.UInt32(r, 1), NativeContentReader.UInt32(r, 2))));
        var granted = new List<(uint, uint)>();
        Read("SELECT buff_id, passive_buff_id FROM buff_passive_buffs", r => granted.Add((U(r, 0), U(r, 1))));
        var curves = new List<BuffStatCurve>();
        Read("""
            SELECT d.id, d.buff_id, d.unit_attribute_id, d.unit_modifier_type_id, d.func_type,
              l.start_value, l.end_value, m.value_list, f.attribute_id, f.value, x.formula
            FROM dynamic_unit_modifiers d
            LEFT JOIN linear_funcs l ON d.func_type='LinearFunc' AND d.func_id=l.id
            LEFT JOIN manual_funcs m ON d.func_type='ManualFunc' AND d.func_id=m.id
            LEFT JOIN dynamic_funcs f ON d.func_type='DynamicFunc' AND d.func_id=f.id
            LEFT JOIN formula_funcs x ON d.func_type='FormulaFunc' AND d.func_id=x.id
            WHERE d.enable IN ('t', 'true', 1) ORDER BY d.id
            """, r =>
        {
            var kind = r.GetString(4) switch
            {
                "LinearFunc" => BuffCurveKind.Linear, "ManualFunc" => BuffCurveKind.Manual,
                "DynamicFunc" => BuffCurveKind.Dynamic, "FormulaFunc" => BuffCurveKind.Formula,
                var value => throw new InvalidDataException($"Unknown buff curve function {value}.")
            };
            curves.Add(new(U(r, 0), U(r, 1), U(r, 2), checked((byte)U(r, 3)), kind,
                kind == BuffCurveKind.Linear ? r.GetInt32(5) : 0, kind == BuffCurveKind.Linear ? r.GetInt32(6) : 0,
                kind == BuffCurveKind.Manual ? r.GetString(7).Split(';', StringSplitOptions.RemoveEmptyEntries)
                    .Select(value => int.Parse(value, CultureInfo.InvariantCulture)).ToArray() : null,
                kind == BuffCurveKind.Dynamic ? U(r, 8) : 0, kind == BuffCurveKind.Dynamic ? r.GetInt32(9) : 0,
                kind == BuffCurveKind.Formula ? new(r.GetString(10)) : null));
        });
        return new(templates, passives, conditions, curves, tags, granted);

        void Read(string sql, Action<SqliteDataReader> consume)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            while (reader.Read()) consume(reader);
        }
    }

    private static uint U(SqliteDataReader r, int i) => checked((uint)r.GetInt64(i));
    private static bool B(SqliteDataReader r, int i) => !r.IsDBNull(i) && r.GetValue(i).ToString() is { Length: > 0 } text && text[0] is 't' or 'T' or '1';
}

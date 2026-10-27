using System.Collections.Frozen;
using AAEmu.Zone.Protocol.Units;
using Microsoft.Data.Sqlite;

namespace AAEmu.Zone.Content;

public sealed record UnitFormulaDefinition(uint Id, ZoneUnitKind Owner, uint Kind, ContentFormula Expression);
public sealed record FormulaCoefficient(uint FormulaId, uint VariableKind, uint Key, float Value);
public sealed record NpcStatTemplate(uint Id, uint TemplateKey, uint KindKey, uint GradeKey, uint MateKindKey);
public sealed record HousingStatTemplate(uint Id, long Health, int BuildActions);
public sealed record StatModifier(uint Id, string OwnerType, uint OwnerId, uint Attribute, byte Type,
    long Value, int LinearLevelBonus, int DynamicValue);
public readonly record struct AttributeLimits(long Minimum, long Maximum);

/// <summary>Immutable stat catalog and connection-specific filtered views from the configured read-only database.</summary>
public sealed class ZoneStatContent
{
    private readonly FrozenDictionary<(ZoneUnitKind, uint), UnitFormulaDefinition> _formulas;
    private readonly FrozenSet<uint> _formulaIds;
    private readonly FrozenDictionary<(uint, uint, uint), float> _coefficients;
    private readonly FrozenDictionary<(string, uint), IReadOnlyList<StatModifier>> _modifiers;
    public IReadOnlyDictionary<uint, NpcStatTemplate> Npcs { get; }
    public IReadOnlyDictionary<uint, HousingStatTemplate> Houses { get; }
    public IReadOnlySet<uint> Slaves { get; }
    public IReadOnlySet<uint> Transfers { get; }
    public IReadOnlySet<uint> Shipyards { get; }
    public IReadOnlyDictionary<uint, AttributeLimits> Limits { get; }
    public ZoneBuffContent Buffs { get; }
    public ZoneSkillContent Skills { get; }
    public ZoneEquipmentContent? Equipment { get; }
    public ZoneReinforcementContent? Reinforcements { get; }
    public ZoneAppellationContent? Appellations { get; }
    internal ZoneAppellationContent? UnfilteredAppellations { get; }
    public ZoneGearScoreContent? GearScores { get; }
    public ZoneEquipmentCombatContent? EquipmentCombat { get; }
    public IReadOnlyCollection<UnitFormulaDefinition> Formulas => _formulas.Values;

    public ZoneStatContent(IEnumerable<UnitFormulaDefinition> formulas, IEnumerable<FormulaCoefficient> coefficients,
        IEnumerable<NpcStatTemplate> npcs, IEnumerable<HousingStatTemplate> houses,
        IEnumerable<uint> slaves, IEnumerable<uint> transfers, IEnumerable<uint> shipyards,
        IEnumerable<StatModifier> modifiers, IEnumerable<KeyValuePair<uint, AttributeLimits>> limits, ZoneBuffContent? buffs = null,
        ZoneSkillContent? skills = null, ZoneEquipmentContent? equipment = null, ZoneReinforcementContent? reinforcements = null,
        ZoneAppellationContent? appellations = null, ZoneGearScoreContent? gearScores = null,
        ZoneEquipmentCombatContent? equipmentCombat = null)
    {
        // ToDictionary deliberately rejects ambiguous keys rather than silently selecting one.
        _formulas = formulas.ToDictionary(value => (value.Owner, value.Kind)).ToFrozenDictionary();
        _formulaIds = _formulas.Values.Select(value => value.Id).ToFrozenSet();
        var coefficientRows = coefficients.ToList();
        if (coefficientRows.Any(value => !_formulaIds.Contains(value.FormulaId) || !float.IsFinite(value.Value)))
            throw new InvalidDataException("Formula coefficient has an unresolved formula or non-finite value.");
        _coefficients = coefficientRows.ToDictionary(value => (value.FormulaId, value.VariableKind, value.Key), value => value.Value).ToFrozenDictionary();
        Npcs = npcs.ToDictionary(value => value.Id).ToFrozenDictionary();
        Houses = houses.ToDictionary(value => value.Id).ToFrozenDictionary();
        Slaves = slaves.ToFrozenSet();
        Transfers = transfers.ToFrozenSet();
        Shipyards = shipyards.ToFrozenSet();
        _modifiers = modifiers.GroupBy(value => (value.OwnerType, value.OwnerId)).ToDictionary(group => group.Key,
            group => (IReadOnlyList<StatModifier>)group.OrderBy(value => value.Id).ToList().AsReadOnly()).ToFrozenDictionary();
        Limits = limits.ToDictionary().ToFrozenDictionary();
        if (Limits.Any(pair => pair.Value.Minimum > pair.Value.Maximum))
            throw new InvalidDataException("Unit attribute limit minimum exceeds its maximum.");
        Buffs = buffs ?? ZoneBuffContent.Empty;
        Skills = skills ?? ZoneSkillContent.Empty;
        Equipment = equipment;
        Reinforcements = reinforcements;
        Appellations = appellations;
        UnfilteredAppellations = appellations;
        GearScores = gearScores;
        EquipmentCombat = equipmentCombat;
    }

    public UnitFormulaDefinition Formula(ZoneUnitKind owner, uint kind) =>
        _formulas.TryGetValue((owner, kind), out var formula) ? formula :
            throw new InvalidDataException($"Missing unit formula for owner {owner}, kind {kind}.");

    internal ZoneStatContent WithFilteredContent(ZoneBuffContent buffs, ZoneAppellationContent? appellations) => new(this, buffs, appellations);

    private ZoneStatContent(ZoneStatContent source, ZoneBuffContent buffs, ZoneAppellationContent? appellations)
    {
        _formulas = source._formulas;
        _formulaIds = source._formulaIds;
        _coefficients = source._coefficients;
        _modifiers = source._modifiers;
        Npcs = source.Npcs;
        Houses = source.Houses;
        Slaves = source.Slaves;
        Transfers = source.Transfers;
        Shipyards = source.Shipyards;
        Limits = source.Limits;
        Buffs = buffs;
        Skills = source.Skills;
        Equipment = source.Equipment;
        Reinforcements = source.Reinforcements;
        Appellations = appellations;
        UnfilteredAppellations = source.UnfilteredAppellations;
        GearScores = source.GearScores;
        EquipmentCombat = source.EquipmentCombat;
    }

    public float Coefficient(uint formulaId, uint variableKind, uint key) =>
        _coefficients.TryGetValue((formulaId, variableKind, key), out var value) ? value :
            // 39B1CE30 zeroes the arrays, then marks enumerated keys starting at 1
            // as required (NaN). NPC/mate key 0 retains its initialized zero.
            key == 0 && variableKind is >= 2 and <= 5 && _formulaIds.Contains(formulaId) ? 0 :
            throw new InvalidDataException($"Missing formula coefficient ({formulaId}, {variableKind}, {key}).");

    public IReadOnlyList<StatModifier> Modifiers(string owner, uint id) =>
        _modifiers.TryGetValue((owner, id), out var modifiers) ? modifiers : Array.Empty<StatModifier>();

    public AttributeLimits LimitsFor(uint attribute) => Limits.TryGetValue(attribute, out var limits) ? limits :
        // FUN_39B2EA30 initializes native attributes before applying the database overrides.
        // 39B27730 selects all 255 defined slots for the same initialization.
        NativeAttributeNames.DefinedIds.Contains(attribute) ? new(-2_000_000_000, 2_000_000_000) :
        throw new NotSupportedException($"Default limits for attribute {attribute} have not been validated.");

    public void ValidateTemplate(UnitIdentity identity)
    {
        var exists = identity.Kind switch
        {
            ZoneUnitKind.Character => true,
            ZoneUnitKind.Npc or ZoneUnitKind.Mate => Npcs.ContainsKey(identity.TemplateId),
            ZoneUnitKind.Housing => Houses.ContainsKey(identity.TemplateId),
            ZoneUnitKind.Slave => Slaves.Contains(identity.TemplateId),
            ZoneUnitKind.Transfer => Transfers.Contains(identity.TemplateId),
            ZoneUnitKind.Shipyard => Shipyards.Contains(identity.TemplateId),
            _ => throw new NotSupportedException($"No content-backed simulation exists for identity {identity.Kind}.")
        };
        if (!exists)
            throw new InvalidDataException($"Unknown {identity.Kind} template {identity.TemplateId} in the configured game content.");
    }

    internal static ZoneStatContent Load(SqliteConnection connection)
    {
        var formulas = new List<UnitFormulaDefinition>();
        Read(connection, "SELECT id, owner_type_id, kind_id, formula FROM unit_formulas ORDER BY id", reader =>
            formulas.Add(new(UInt(reader, 0), (ZoneUnitKind)checked((byte)UInt(reader, 1)), UInt(reader, 2), new(reader.GetString(3)))));
        var coefficients = new List<FormulaCoefficient>();
        Read(connection, "SELECT unit_formula_id, variable_kind_id, key, value FROM unit_formula_variables ORDER BY id", reader =>
            coefficients.Add(new(UInt(reader, 0), UInt(reader, 1), UInt(reader, 2), reader.GetFloat(3))));
        var npcs = new List<NpcStatTemplate>();
        Read(connection, "SELECT id, npc_template_id, npc_kind_id, npc_grade_id, COALESCE(mate_kind_id, 0) FROM npcs", reader =>
            npcs.Add(new(UInt(reader, 0), UInt(reader, 1), UInt(reader, 2), UInt(reader, 3), UInt(reader, 4))));
        var houses = new List<HousingStatTemplate>();
        Read(connection, """
            SELECT h.id, COALESCE(h.hp, 0), COALESCE(SUM(s.num_actions), 0)
            FROM housings h LEFT JOIN housing_build_steps s ON s.housing_id = h.id
            GROUP BY h.id, h.hp
            """, reader => houses.Add(new(UInt(reader, 0), reader.GetInt64(1), reader.GetInt32(2))));
        var slaves = new List<uint>();
        Read(connection, "SELECT id FROM slaves", reader => slaves.Add(UInt(reader, 0)));
        var transfers = new List<uint>();
        Read(connection, "SELECT id FROM transfers", reader => transfers.Add(UInt(reader, 0)));
        var shipyards = new List<uint>();
        Read(connection, "SELECT id FROM shipyards", reader => shipyards.Add(UInt(reader, 0)));
        var modifiers = new List<StatModifier>();
        Read(connection, """
            SELECT id, owner_type, owner_id, unit_attribute_id, unit_modifier_type_id, value, linear_level_bonus, dynamic_value
            FROM unit_modifiers WHERE enable IN ('t', 'true', 1) AND owner_id IS NOT NULL AND owner_type IS NOT NULL
            ORDER BY id
            """, reader => modifiers.Add(new(UInt(reader, 0), reader.GetString(1), UInt(reader, 2), UInt(reader, 3),
                checked((byte)UInt(reader, 4)), reader.GetInt64(5), reader.GetInt32(6), reader.GetInt32(7))));
        var limits = new List<KeyValuePair<uint, AttributeLimits>>();
        Read(connection, "SELECT unit_attribute_id, minimum, maximum FROM unit_attribute_limits", reader =>
            limits.Add(new(UInt(reader, 0), new(reader.GetInt64(1), reader.GetInt64(2)))));
        return new(formulas, coefficients, npcs, houses, slaves, transfers, shipyards, modifiers, limits,
            ZoneBuffContent.Load(connection), ZoneSkillContent.Load(connection), ZoneEquipmentContent.Load(connection),
            ZoneReinforcementContent.Load(connection), ZoneAppellationContent.Load(connection), ZoneGearScoreContent.Load(connection),
            ZoneEquipmentCombatContent.Load(connection));
    }

    private static uint UInt(SqliteDataReader reader, int ordinal) => checked((uint)reader.GetInt64(ordinal));
    private static void Read(SqliteConnection connection, string sql, Action<SqliteDataReader> consume)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.Read()) consume(reader);
    }
}

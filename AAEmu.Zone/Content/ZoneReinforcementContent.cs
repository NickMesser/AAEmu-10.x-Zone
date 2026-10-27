using System.Collections.Frozen;
using Microsoft.Data.Sqlite;

namespace AAEmu.Zone.Content;

public sealed record ReinforcementDefinition(int Slot, int Level, float ItemLevelBonus);
public sealed record ReinforcementSetEffect(uint Id, int Attribute, int RequiredLevel);
public sealed record ReinforcementBundleEffect(uint Id, int OffenseLevel, int DefenseLevel, int SupportLevel);
public sealed record ReinforcementModifier(uint Id, uint Attribute, byte Type, int Value);

/// <summary>Content used by the native reinforcement stat visitor; selections remain unit state.</summary>
public sealed class ZoneReinforcementContent
{
    public bool UseBundleEffects { get; }
    public IReadOnlyDictionary<(int Slot, int Level), ReinforcementDefinition> Levels { get; }
    public IReadOnlyList<ReinforcementSetEffect> SetEffects { get; }
    public IReadOnlyList<ReinforcementBundleEffect> BundleEffects { get; }
    public IReadOnlyDictionary<uint, ReinforcementModifier> Modifiers { get; }

    public ZoneReinforcementContent(bool useBundleEffects, IEnumerable<ReinforcementDefinition> levels,
        IEnumerable<ReinforcementSetEffect> setEffects, IEnumerable<ReinforcementBundleEffect> bundleEffects,
        IEnumerable<ReinforcementModifier> modifiers)
    {
        UseBundleEffects = useBundleEffects;
        Levels = levels.ToDictionary(v => (v.Slot, v.Level)).ToFrozenDictionary();
        if (Levels.Values.Any(v => !float.IsFinite(v.ItemLevelBonus)))
            throw new InvalidDataException("Non-finite reinforcement item-level bonus.");
        SetEffects = Array.AsReadOnly(setEffects.ToArray());
        BundleEffects = Array.AsReadOnly(bundleEffects.ToArray());
        Modifiers = modifiers.ToDictionary(v => v.Id).ToFrozenDictionary();
    }

    internal static ZoneReinforcementContent Load(SqliteConnection connection)
    {
        var bundleMode = false;
        Read("SELECT value FROM content_configs WHERE id=360", r => bundleMode = r.GetInt32(0) != 0);
        var levels = new List<ReinforcementDefinition>();
        Read("SELECT COALESCE(slot_type_id,0), COALESCE(level,0), gain_item_level FROM equip_slot_reinforces ORDER BY id", r =>
            levels.Add(new(r.GetInt32(0), r.GetInt32(1), r.GetFloat(2))));
        var sets = new List<ReinforcementSetEffect>();
        Read("SELECT id, COALESCE(reinforce_attribute_id,0), COALESCE(require_reinforce_level,0) FROM equip_slot_reinforce_set_effects ORDER BY id", r =>
            sets.Add(new(U(r, 0), r.GetInt32(1), r.GetInt32(2))));
        var bundles = new List<ReinforcementBundleEffect>();
        Read("""
            SELECT id, COALESCE(require_offense_level,0), COALESCE(require_defense_level,0), COALESCE(require_support_level,0)
            FROM equip_slot_reinforce_bundle_effects ORDER BY bundle_effect_level, id
            """, r => bundles.Add(new(U(r, 0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3))));
        var modifiers = new List<ReinforcementModifier>();
        // 39CE9020 resolves the chosen modifier ID directly, regardless of its slot/trigger metadata.
        Read("SELECT id, unit_attribute_id, unit_modifier_type_id, value FROM equip_slot_reinforce_unit_modifiers ORDER BY id", r =>
            modifiers.Add(new(U(r, 0), U(r, 1), checked((byte)r.GetInt32(2)), r.GetInt32(3))));
        return new(bundleMode, levels, sets, bundles, modifiers);

        void Read(string sql, Action<SqliteDataReader> consume)
        {
            using var command = connection.CreateCommand(); command.CommandText = sql;
            using var reader = command.ExecuteReader(); while (reader.Read()) consume(reader);
        }
    }

    private static uint U(SqliteDataReader reader, int index) => checked((uint)reader.GetInt64(index));
}

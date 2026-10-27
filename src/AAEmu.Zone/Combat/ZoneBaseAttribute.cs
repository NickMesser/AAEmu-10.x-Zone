using AAEmu.Zone.Protocol.Units;

namespace AAEmu.Zone.Combat;

internal enum ZoneBaseAttributeKind { Constant, Formula, VitalFormula, FacetAdjustedFormula, Attribute, Mitigation, EquipmentDefense, EquipmentPower, EquipmentDps, WeaponSpeed, WeaponDamage, DefensiveChance, ArmorType, ArmorCoverage }

/// <summary>Native base-getter dispatch for the 255 defined attributes.</summary>
internal readonly record struct ZoneBaseAttribute(ZoneBaseAttributeKind Kind, uint Input, uint Dependency = 0)
{
    public static bool TryGet(ZoneUnitKind owner, uint attribute, out ZoneBaseAttribute result)
    {
        result = default;
        if (!NativeAttributeNames.DefinedIds.Contains(attribute)) return false;
        // 39BF6C20 selects 39D44A60 / 39D46070 / 39D46470 / 39D45BE0.
        if (owner == ZoneUnitKind.Npc)
        {
            switch (attribute)
            {
                case 8: result = Formula(18); return true;
                case 64: result = Formula(19); return true;
                case 87: result = Related(35); return true;
                case 173: result = Related(175); return true;
                case 36 or 37 or 38 or 41 or 42 or 43 or 46 or 47 or 48: result = Weapon(attribute); return true;
            }
        }
        else if (owner != ZoneUnitKind.Housing)
        {
            switch (attribute)
            {
                case 8: result = new(ZoneBaseAttributeKind.EquipmentDefense, owner == ZoneUnitKind.Slave ? 0u : 18u, 8); return true;
                case 64: result = new(ZoneBaseAttributeKind.EquipmentDefense, 19, 64); return true;
                case 87: result = new(ZoneBaseAttributeKind.EquipmentPower, 35); return true;
                case 173: result = new(ZoneBaseAttributeKind.EquipmentPower, 175); return true;
                case 36 or 37 or 38 or 41 or 42 or 43 or 46 or 47 or 48 when owner != ZoneUnitKind.Slave: result = Weapon(attribute); return true;
                case 140 or 141: result = Constant(100); return true;
                case 142: result = new(ZoneBaseAttributeKind.Mitigation, 39, 8); return true;
                case 144: result = new(ZoneBaseAttributeKind.Mitigation, 40, 8); return true;
                case 146: result = new(ZoneBaseAttributeKind.Mitigation, 41, 64); return true;
                case 188 when owner == ZoneUnitKind.Slave: result = Adjusted(47, 189); return true;
                case 190 when owner == ZoneUnitKind.Slave: result = Adjusted(48, 191); return true;
                case 192 when owner == ZoneUnitKind.Slave: result = Adjusted(49, 193); return true;
            }
        }
        // Shared fallback 39BF7150, including equipment/buff predicates for every owner.
        result = attribute switch
        {
            <= 5 => Formula(attribute + 22),
            6 or 7 => new(ZoneBaseAttributeKind.VitalFormula, attribute + 8),
            11 => Formula(16), 12 => Formula(17), 13 => Formula(20),
            16 => Adjusted(1, 77), 17 => Formula(35), 18 => Adjusted(2, 78),
            22 or 153 => new(ZoneBaseAttributeKind.DefensiveChance, 5, 81),
            177 => new(ZoneBaseAttributeKind.DefensiveChance, 45, 179),
            69 => new(ZoneBaseAttributeKind.ArmorType, 0), 70 => new(ZoneBaseAttributeKind.ArmorCoverage, 69),
            23 => Adjusted(7, 83), 25 => Adjusted(6, 82), 26 => Formula(36),
            28 => Adjusted(11, 88), 30 => Adjusted(10, 86), 31 => Formula(37),
            33 => Formula(21), 34 => Formula(28), 35 => Formula(29),
            58 => Formula(61), 67 => Formula(31), 68 => Formula(32), 71 => Formula(68),
            78 => Formula(64), 83 => Formula(65), 88 => Formula(66), 89 => Formula(30),
            91 => Constant(60000), 150 => Adjusted(10, 151), 151 => Related(86), 152 => Related(31),
            96 or 97 or 98 => new(ZoneBaseAttributeKind.EquipmentDps, attribute - 81),
            174 => Adjusted(42, 185), 175 => Formula(43), 176 => Formula(44), 178 => Adjusted(46, 180),
            181 => Formula(67), 182 => Formula(59), 183 => Formula(60), 206 => Formula(63),
            207 => Formula(50), 208 => Formula(51), 209 => Formula(52), 210 => Formula(53),
            211 => Formula(54), 212 => Formula(55),
            73 or 92 or 95 or 125 or 129 or 131 or 133 or 135 or 137 or 187 or 194 or
                224 or 225 or 226 or 227 or 247 => Constant(100),
            _ => Constant(0)
        };
        return true;
    }

    private static ZoneBaseAttribute Constant(uint value) => new(ZoneBaseAttributeKind.Constant, value);
    private static ZoneBaseAttribute Formula(uint kind) => new(ZoneBaseAttributeKind.Formula, kind);
    private static ZoneBaseAttribute Adjusted(uint kind, uint penalty) => new(ZoneBaseAttributeKind.FacetAdjustedFormula, kind, penalty);
    private static ZoneBaseAttribute Related(uint attribute) => new(ZoneBaseAttributeKind.Attribute, attribute);
    private static ZoneBaseAttribute Weapon(uint attribute) => attribute is 36 or 41 or 46
        ? new(ZoneBaseAttributeKind.WeaponSpeed, 15 + (attribute - 36) / 5)
        : new(ZoneBaseAttributeKind.WeaponDamage, 15 + (attribute - 37) / 5, (attribute - 37) % 5 == 0 ? 1u : 0u);
}

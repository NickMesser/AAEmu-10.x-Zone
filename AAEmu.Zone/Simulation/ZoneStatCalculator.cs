using System.Collections.Frozen;
using AAEmu.Zone.Content;
using AAEmu.Zone.Protocol.Messages;
using AAEmu.Zone.Protocol.Units;

namespace AAEmu.Zone.Simulation;

public sealed record ZoneVitalStats(IReadOnlyList<long> Attributes, long MaximumHealth, long MaximumMana,
    IReadOnlyList<string> UnresolvedSources)
{
    public bool IsComplete => UnresolvedSources.Count == 0;
}

public sealed record ZoneAttributeStats(IReadOnlyDictionary<uint, long> Values, IReadOnlyList<string> UnresolvedSources)
{
    public bool IsComplete => UnresolvedSources.Count == 0;
}

/// <summary>
/// Native formula, constant, alias and facet-adjusted attributes. Unimplemented sources remain explicit;
/// a partial calculation must never be used as the authoritative limit for UnitPoints.
/// </summary>
public sealed class ZoneStatCalculator(ZoneStatContent content)
{
    // FUN_39BF7150 adds the exact f32-derived double at 3A13DED8 for HP/MP before i64 conversion.
    private const double VitalRoundingOffset = 0.05000000074505806;
    private static readonly FrozenDictionary<string, int> AttributeNames = new Dictionary<string, int>
        { ["str"] = 0, ["dex"] = 1, ["sta"] = 2, ["int"] = 3, ["spi"] = 4, ["fai"] = 5 }.ToFrozenDictionary();

    public ZoneVitalStats Calculate(UnitStateSnapshot state, UnitBuffs? currentBuffs = null, ZoneProgressionState? progression = null,
        UnitEquipment? currentEquipment = null, ZoneAttributeOverrides? overrides = null)
    {
        var result = CalculateAttributes(state, [0, 1, 2, 3, 4, 5, 6, 7], currentBuffs, progression, currentEquipment, overrides);
        return new(Array.AsReadOnly(Enumerable.Range(0, 6).Select(index => result.Values[(uint)index]).ToArray()),
            result.Values[6], result.Values[7], result.UnresolvedSources);
    }

    /// <summary>Calculate requested native attributes with the same current equipment, progression and buff dependency graph.</summary>
    public ZoneAttributeStats CalculateAttributes(UnitStateSnapshot state, IEnumerable<uint> requestedAttributes,
        UnitBuffs? currentBuffs = null, ZoneProgressionState? progression = null, UnitEquipment? currentEquipment = null,
        ZoneAttributeOverrides? overrides = null)
    {
        overrides ??= ZoneAttributeOverrides.Empty;
        var requested = requestedAttributes.Distinct().ToArray();
        foreach (var attribute in requested)
            if (!ZoneBaseAttribute.TryGet(state.Identity.Kind, attribute, out _) && !overrides.Values.ContainsKey(attribute))
                throw new NotSupportedException($"Native attribute {attribute} is not implemented.");
        // 39BF6C20 tests the override before touching the unit, modifier sources,
        // formulas, caches or limits. Fully overridden queries need none of them.
        if (requested.All(overrides.Values.ContainsKey))
            return new(requested.ToFrozenDictionary(attribute => attribute, attribute => overrides.Values[attribute]), []);
        content.ValidateTemplate(state.Identity);
        progression ??= ZoneProgressionState.FromSnapshot(state, content, currentBuffs);
        var unresolved = new HashSet<string>(StringComparer.Ordinal);
        if (content.Equipment is null && state.Identity.Kind is ZoneUnitKind.Character or ZoneUnitKind.Slave or ZoneUnitKind.Mate &&
            (currentEquipment ?? state.Equipment).Slots.Any(slot => slot.Item.TemplateId != 0 &&
                (state.Identity.Kind == ZoneUnitKind.Slave || slot.Slot is < 19 or > 25)))
            unresolved.Add("equipment modifiers");
        if (content.Appellations is null && state.Identity.Kind == ZoneUnitKind.Character && state.AppellationStamp != 0)
            unresolved.Add("appellation stamp modifiers");
        if (content.Reinforcements is null && state.Character is { } character && (character.Reinforcements.Count != 0 || character.LevelEffects.Count != 0))
            unresolved.Add("equipment reinforcement modifiers");

        var ownerType = state.Identity.Kind switch
        {
            ZoneUnitKind.Npc or ZoneUnitKind.Mate => "Npc", ZoneUnitKind.Slave => "Slave",
            ZoneUnitKind.Housing => "Housing", _ => ""
        };
        var modifiers = content.Modifiers(ownerType, state.Identity.TemplateId)
            .Select(modifier => (Modifier: modifier, Level: (int)progression.Level, Stack: 1)).ToList();
        if (content.Equipment is { } equipmentContent)
            new ZoneEquipmentCalculator(equipmentContent).Append(content, state, currentEquipment ?? state.Equipment, progression.Level, modifiers, unresolved);
        if (content.Reinforcements is { } reinforcementContent)
            new ZoneReinforcementCalculator(reinforcementContent).Append(content, state, modifiers);
        // 39D1C900 copies fixed packet member 0x994 to state+0x7FBC; the packed title is
        // a different field. 39BF6510 visits the selected merit even during transformation,
        // using descriptor+0x2C (req_app_level), with no eligibility check against unit level.
        if (state.Identity.Kind == ZoneUnitKind.Character && content.Appellations is { } appellations &&
            appellations.Stamps.TryGetValue(unchecked((uint)state.AppellationStamp), out var stamp))
            modifiers.AddRange(content.Modifiers("AppellationMerit", stamp.Id)
                .Select(modifier => (modifier, stamp.RequiredAppellationLevel, 1)));
        var buffs = currentBuffs ?? state.Buffs;
        var active = buffs.Good.Concat(buffs.Bad).Concat(buffs.Hidden).ToArray();
        var curves = new List<(BuffStatCurve Curve, UnitBuff Buff)>();
        foreach (var passiveId in progression.Passives.Current)
        {
            // 39D28D70 skips absent passive definitions and dangling buff references. The configured
            // content contains retired passives whose referenced buff no longer exists.
            if (!content.Buffs.Passives.TryGetValue(passiveId, out var passive) || passive.Active ||
                !content.Buffs.Templates.ContainsKey(passive.BuffId)) continue;
            AddBuffModifiers(passive.BuffId, 0, 1);
        }
        foreach (var buff in active)
        {
            var template = content.Buffs.Template(buff.TemplateId);
            if (template.OneTime) continue; // 39BF6160 / 39BF70F0 / 39BF3C00
            AddBuffModifiers(buff.TemplateId, buff.CasterAbilityLevel, unchecked((int)buff.Stack));
            curves.AddRange(content.Buffs.Curves(buff.TemplateId).Select(curve => (curve, buff)));
        }
        // 39B1B630 selects the secondary level pair only for transformed characters.
        var transformed = state.Identity.Kind == ZoneUnitKind.Character && state.SecondaryLevel != 0;
        var level = transformed ? unchecked((byte)state.SecondaryLevel) : progression.Level;
        var heir = unchecked((byte)(transformed ? state.SecondaryHeirLevel : state.HeirLevel));
        var cache = new Dictionary<(uint Attribute, bool FormulaBuffs), long>();
        var calculating = new HashSet<(uint, bool)>();
        int? gearScore = null;
        var combat = new ZoneEquipmentCombatCalculator(content);
        var values = requested.ToFrozenDictionary(attribute => attribute, attribute => Attribute(attribute, true));
        return new(values, unresolved.Order(StringComparer.Ordinal).ToList().AsReadOnly());

        void AddBuffModifiers(uint templateId, int sourceLevel, int stack)
        {
            modifiers.AddRange(content.Modifiers("Buff", templateId).Select(modifier => (modifier, sourceLevel, stack)));
            // 39BF43A0 applies each satisfied buff-ID/tag condition independently, once per visitor.
            foreach (var condition in content.Buffs.Conditions(templateId))
            {
                if (condition.RequiredBuffId != 0 && active.Any(buff => buff.TemplateId == condition.RequiredBuffId))
                    AddConditional();
                if (condition.RequiredTagId != 0 && active.Any(buff => content.Buffs.HasRuntimeTag(buff.TemplateId, condition.RequiredTagId)))
                    AddConditional();
                void AddConditional() => modifiers.AddRange(content.Modifiers("BuffUnitModifier", condition.Id).Select(modifier => (modifier, 0, 1)));
            }
        }

        long Attribute(uint attribute, bool formulaBuffs)
        {
            if (overrides.Values.TryGetValue(attribute, out var forced)) return forced;
            if (!ZoneBaseAttribute.TryGet(state.Identity.Kind, attribute, out var definition))
            {
                unresolved.Add($"buff dependency on attribute {attribute}");
                return 0;
            }
            var key = (attribute, formulaBuffs);
            if (cache.TryGetValue(key, out var value)) return value;
            if (!calculating.Add(key))
                throw new InvalidDataException($"Cyclic buff stat dependency for attribute {attribute}.");
            try
            {
                if (attribute < 6)
                {
                    var baseValue = ZoneStatMath.ToInt32(Evaluate(attribute + 22));
                    if (attribute < 5 && !transformed && state.Character is { } player && player.SelectedPage < player.StatPages.Count)
                        baseValue = unchecked(baseValue + player.StatPages[(int)player.SelectedPage].Stats[(int)attribute]);
                    value = baseValue;
                }
                else if (attribute == 6 && state.Identity.Kind == ZoneUnitKind.Housing)
                    value = HouseHealth();
                else value = definition.Kind switch
                {
                    ZoneBaseAttributeKind.Constant => definition.Input,
                    ZoneBaseAttributeKind.Formula => ZoneStatMath.ToInt32(Evaluate(definition.Input)),
                    ZoneBaseAttributeKind.VitalFormula => ZoneStatMath.ToInt64((double)Evaluate(definition.Input) + VitalRoundingOffset),
                    ZoneBaseAttributeKind.Attribute => Attribute(definition.Input, true),
                    ZoneBaseAttributeKind.FacetAdjustedFormula => AdjustedFormula(definition.Input, definition.Dependency),
                    ZoneBaseAttributeKind.Mitigation => ZoneStatMath.ToInt64(Evaluate(definition.Input, definition.Dependency)),
                    ZoneBaseAttributeKind.EquipmentDefense => unchecked((definition.Input == 0 ? 0 : ZoneStatMath.ToInt32(Evaluate(definition.Input))) +
                        combat.Defense(state, currentEquipment ?? state.Equipment, definition.Dependency == 64, unresolved)),
                    ZoneBaseAttributeKind.EquipmentPower => EquipmentPower(definition.Input),
                    ZoneBaseAttributeKind.EquipmentDps => combat.DamagePerSecond(state, currentEquipment ?? state.Equipment, level, (byte)definition.Input, unresolved),
                    ZoneBaseAttributeKind.WeaponSpeed => combat.AttackInterval(state, currentEquipment ?? state.Equipment, (byte)definition.Input,
                        dependency => Attribute(dependency, true), unresolved),
                    ZoneBaseAttributeKind.WeaponDamage => combat.Damage(state, currentEquipment ?? state.Equipment, (byte)definition.Input, definition.Dependency != 0,
                        dependency => Attribute(dependency, true), unresolved),
                    ZoneBaseAttributeKind.DefensiveChance => combat.CanDefend(state, currentEquipment ?? state.Equipment, attribute,
                        progression.Passives.Current, active, unresolved) ? AdjustedFormula(definition.Input, definition.Dependency) : 0,
                    ZoneBaseAttributeKind.ArmorType => combat.ArmorType(state, currentEquipment ?? state.Equipment, active, unresolved),
                    ZoneBaseAttributeKind.ArmorCoverage => combat.ArmorCoverage(state, currentEquipment ?? state.Equipment,
                        unchecked((int)Attribute(definition.Input, true)), unresolved),
                    _ => throw new InvalidOperationException("Invalid native base attribute dispatch.")
                };
                // 39BF6C20 passes only the signed low 32 bits of the base getter
                // to 39BF3380. Direct GM overrides return before this narrowing.
                value = ApplyModifiers(attribute, unchecked((int)value), formulaBuffs);
                cache.Add(key, value);
                return value;
            }
            finally { calculating.Remove(key); }
        }

        int AdjustedFormula(uint kind, uint penaltyAttribute)
        {
            // 39D354C0 narrows both dependency getters to i32 before f32 arithmetic.
            // Preserve the negative f32 multiplier stored at 39FC2A60.
            var formula = ZoneStatMath.ToInt32(Evaluate(kind));
            var facets = unchecked((int)Attribute(13, true));
            var penalty = unchecked((int)Attribute(penaltyAttribute, true));
            var adjustment = ZoneStatMath.ToInt32((float)penalty * facets * -0.001f);
            return unchecked(formula - adjustment);
        }

        long EquipmentPower(uint dependency)
        {
            if (transformed) return Attribute(dependency, true);
            var power = combat.Power(state, currentEquipment ?? state.Equipment, dependency == 175, unresolved);
            return ZoneStatMath.ToInt32((float)Attribute(dependency, true) + power * 1000f);
        }

        float Evaluate(uint kind, uint? mitigationAttribute = null)
        {
            // 39B1BF00 / 39B1BF60 / 39B1BFC0 read this dependency even when
            // the selected formula is constant, then store its f32 value in the environment.
            var mitigation = mitigationAttribute is { } dependency ? (float)Attribute(dependency, true) : 0f;
            var formula = content.Formula(state.Identity.Kind, kind);
            return formula.Expression.EvaluateNative(name =>
            {
                if (name == "level") return level;
                if (name == "heir_level") return heir;
                if (mitigationAttribute == 8 && name == "armor" || mitigationAttribute == 64 && name == "magic_resist") return mitigation;
                if (AttributeNames.TryGetValue(name, out var attribute))
                {
                    if (kind is >= 22 and <= 27)
                        throw new InvalidDataException("Core attribute formula recursively references a derived attribute.");
                    return Attribute((uint)attribute, true);
                }
                if (state.Identity.Kind is ZoneUnitKind.Npc or ZoneUnitKind.Mate)
                {
                    var npc = content.Npcs[state.Identity.TemplateId];
                    return name switch
                    {
                        "npc_template" => content.Coefficient(formula.Id, 2, npc.TemplateKey),
                        "npc_kind" => content.Coefficient(formula.Id, 3, npc.KindKey),
                        "npc_grade" => content.Coefficient(formula.Id, 4, npc.GradeKey),
                        "mate_kind" => content.Coefficient(formula.Id, 5, npc.MateKindKey),
                        _ => throw new InvalidDataException($"Unresolved vital formula input '{name}'.")
                    };
                }
                throw new InvalidDataException($"Unresolved vital formula input '{name}'.");
            });
        }

        long HouseHealth()
        {
            // FUN_39D463C0: signed build marker; negative markers scale by action-count progress.
            var house = content.Houses[state.Identity.TemplateId];
            var marker = unchecked((short)state.Identity.CompletedBuildSteps);
            if (marker < 0 && house.BuildActions != 0)
            {
                var actions = checked((uint)house.BuildActions);
                return unchecked(checked((uint)house.Health) / (actions + 1) * (uint)(marker + 1 + actions));
            }
            return house.Health;
        }

        long ApplyModifiers(uint attribute, long baseValue, bool formulaBuffs)
        {
            long additive = 0;
            var percentage = 100;
            foreach (var (modifier, sourceLevel, stack) in modifiers.Where(value => value.Modifier.Attribute == attribute))
            {
                if (modifier.DynamicValue != 0)
                {
                    unresolved.Add($"dynamic modifier {modifier.Id}");
                    continue;
                }
                var modifierValue = modifier.Value;
                if (modifier.LinearLevelBonus != 0)
                {
                    // FUN_39BF3890: i32 product, f32 rounding, then f32 addition to the value.
                    var product = unchecked(modifier.LinearLevelBonus * sourceLevel);
                    modifierValue = ZoneStatMath.ToInt64(MathF.Floor(product * 0.01f + 0.5f) + (float)modifierValue);
                }
                Add(modifier.Type, modifierValue, stack);
            }
            foreach (var (curve, buff) in curves.Where(value => value.Curve.Attribute == attribute))
            {
                var curveValue = 0;
                switch (curve.Kind)
                {
                    case BuffCurveKind.Linear:
                    case BuffCurveKind.Manual:
                        if (buff.Duration == 0 || buff.Tick == 0) continue;
                        var tick = unchecked((int)buff.TickIndex);
                        var total = unchecked((int)(buff.Duration / buff.Tick));
                        if (curve.Kind == BuffCurveKind.Manual)
                        {
                            var values = curve.Values ?? throw new InvalidDataException("Missing manual buff curve values.");
                            if (values.Count != 0) curveValue = values[Math.Clamp(tick, 0, values.Count - 1)];
                        }
                        else if (total > 0)
                            curveValue = tick < 1 ? curve.Start : tick >= total ? curve.End :
                                unchecked((curve.End - curve.Start) * tick) / total + curve.Start;
                        break;
                    case BuffCurveKind.Dynamic:
                        // 39BF6FB0 uses f32 and the exact 0.001f constant at 39F9EDE0.
                        if (curve.Percentage != 0)
                            curveValue = ZoneStatMath.ToInt32((float)Attribute(curve.SourceAttribute, true) * curve.Percentage * 0.001f);
                        break;
                    case BuffCurveKind.Formula:
                        if (!formulaBuffs) continue;
                        var expression = curve.Formula ?? throw new InvalidDataException("Missing buff formula.");
                        curveValue = ZoneStatMath.ToInt32(expression.EvaluateNative(name =>
                        {
                            // Native 39D432E0/39D42F50 read attributes with formula-buff contributions disabled.
                            if (name.StartsWith("attr_", StringComparison.Ordinal) && uint.TryParse(name.AsSpan(5), out var input))
                                return Attribute(input, false);
                            if (name == "pc_level") return state.Identity.Kind == ZoneUnitKind.Character ? level : 0;
                            if (name == "heir_level") return state.Identity.Kind == ZoneUnitKind.Character ? heir : 0;
                            if (name == "gear_score")
                            {
                                if (content.Equipment is null || content.GearScores is null)
                                {
                                    unresolved.Add("gear score");
                                    return 0;
                                }
                                gearScore ??= new ZoneGearScoreCalculator(content.Equipment, content.GearScores, content.Reinforcements)
                                    .Calculate(currentEquipment ?? state.Equipment, state.Character?.Reinforcements).Total;
                                // 39D432E0 first converts the integer score to f32 for formula evaluation.
                                return gearScore.Value;
                            }
                            unresolved.Add($"buff formula input '{name}'");
                            return 0;
                        }));
                        break;
                    default:
                        throw new InvalidDataException($"Unknown buff curve kind {curve.Kind}.");
                }
                Add(curve.ModifierType, curveValue, unchecked((int)buff.Stack));
            }
            // FUN_39BF3380 combines all flat additions before one summed percentage, then clamps.
            var value = unchecked(baseValue + additive);
            if (percentage != 100)
                value = ZoneStatMath.ToInt64((double)percentage * value * 0.01);
            var limits = content.LimitsFor(attribute);
            return Math.Clamp(value, limits.Minimum, limits.Maximum);

            void Add(byte type, long value, int stack)
            {
                if (type == 0) additive = unchecked(additive + value * stack);
                else if (type == 1) percentage = unchecked(percentage + (int)value * stack);
                else throw new InvalidDataException($"Unsupported modifier type {type}.");
            }
        }
    }
}

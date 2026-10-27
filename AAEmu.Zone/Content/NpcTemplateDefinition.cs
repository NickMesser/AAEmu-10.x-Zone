using Microsoft.Data.Sqlite;

namespace AAEmu.Zone.Content;

/// <summary>
/// The 101 scalar columns consumed by native LoadNpcDescs (39B080B0).
/// IDs remain source references; loading a template does not create an actor or execute AI.
/// Name is the source text, before the native localization pass (39AF2BA0).
/// </summary>
public sealed record NpcTemplateDefinition
{
    public uint Id { get; init; }
    public bool AbilityChanger { get; init; }
    public float AbsoluteReturnDistance { get; init; }
    public bool AcceptAggroLink { get; init; }
    public bool ActivateAiAlways { get; init; }
    public bool Aggression { get; init; }
    public float AggroLinkHelpDist { get; init; }
    public bool AggroLinkSightCheck { get; init; }
    public bool AggroLinkSpecialGuard { get; init; }
    public bool AggroLinkSpecialIgnoreNpcAttacker { get; init; }
    public uint AggroLinkSpecialRuleId { get; init; }
    public uint AiFileId { get; init; }
    public int ArmorElementLevel { get; init; }
    public uint ArmorTypeId { get; init; }
    public float AttackStartRangeScale { get; init; }
    public bool Auctioneer { get; init; }
    public bool Banker { get; init; }
    public float BaseSkillDelay { get; init; }
    public bool BaseSkillStrafe { get; init; }
    public uint BaseSkillId { get; init; }
    public bool Blacksmith { get; init; }
    public uint CharRaceId { get; init; }
    public bool CheckBackpack { get; init; }
    public bool CheckTargetUnderTerrain { get; init; }
    public bool CrowdEffect { get; init; }
    public int DecayingSecAfterLooted { get; init; }
    public bool DontPushableLikeGhost { get; init; }
    public uint EngageCombatBgmId { get; init; }
    public uint EngageCombatGiveQuestId { get; init; }
    public uint EquipClothsId { get; init; }
    public uint EquipWeaponsId { get; init; }
    public int ExpAdder { get; init; }
    public float ExpMultiplier { get; init; }
    public bool Expedition { get; init; }
    public uint FactionId { get; init; }
    public bool ForceTargetMeOnAttack { get; init; }
    public uint FriendlyNearQuestId { get; init; }
    public int HeirLevel { get; init; }
    public int HonorPoint { get; init; }
    public int Level { get; init; }
    public bool LookConverter { get; init; }
    public uint MateEquipSlotPackId { get; init; }
    public uint MateKindId { get; init; }
    public int MateReviveDelay { get; init; }
    public int MateReviveHpPercent { get; init; }
    public int MateReviveMpPercent { get; init; }
    public bool Merchant { get; init; }
    public uint MerchantRandomPackId { get; init; }
    public uint ModelId { get; init; }
    public int MultiJump { get; init; }
    public float MultiJumpPowY { get; init; }
    public float MultiJumpPowZ { get; init; }
    public string Name { get; init; } = "";
    public bool NoApplyTotalCustom { get; init; }
    public bool NoExp { get; init; }
    public bool NoPenalty { get; init; }
    public bool NonPushableByActor { get; init; }
    public uint NpcAiClientParamId { get; init; }
    public uint NpcAiParamId { get; init; }
    public uint NpcGradeId { get; init; }
    public uint NpcInteractionSetId { get; init; }
    public uint NpcKindId { get; init; }
    public uint NpcNicknameId { get; init; }
    public uint NpcPostureSetId { get; init; }
    public uint NpcStrafeParamId { get; init; }
    public uint NpcTemplateId { get; init; }
    public float Opacity { get; init; }
    public bool PartyFlag { get; init; }
    public uint PetItemId { get; init; }
    public bool Priest { get; init; }
    public bool PriorVisibility { get; init; }
    public bool RagdollAfterDeathAnim { get; init; }
    public bool Repairman { get; init; }
    public float ReturnDistance { get; init; }
    public bool ReturnWhenEnterHousingArea { get; init; }
    public float RunAwayThreshold { get; init; }
    public float Scale { get; init; }
    public bool ShowFactionTag { get; init; }
    public bool ShowNameTag { get; init; }
    public bool ShowOnBossTelescope { get; init; }
    public float SightFovScale { get; init; }
    public float SightRangeScale { get; init; }
    public bool SkillTrainer { get; init; }
    public string SoState { get; init; } = "";
    public uint SoundPackId { get; init; }
    public bool Specialty { get; init; }
    public uint SpecialtyCoinId { get; init; }
    public bool Stabler { get; init; }
    public bool Teleporter { get; init; }
    public uint TotalCustomId { get; init; }
    public bool TrackFriendship { get; init; }
    public bool TradegoodBuy { get; init; }
    public bool Trader { get; init; }
    public bool UseAbuserList { get; init; }
    public bool UseDdcmsMountSkill { get; init; }
    public bool UseHpBarSplit { get; init; }
    public bool UseModelCameraDistance { get; init; }
    public bool UseRangeMod { get; init; }
    public bool VisibleToCreatorOnly { get; init; }
    public uint WeaponElementId { get; init; }
    public int WeaponElementLevel { get; init; }

    internal const string SelectSql = """
        SELECT id, ability_changer, absolute_return_distance, accept_aggro_link, activate_ai_always, aggression, aggro_link_help_dist, aggro_link_sight_check, aggro_link_special_guard, aggro_link_special_ignore_npc_attacker,
            aggro_link_special_rule_id, ai_file_id, armor_element_level, armor_type_id, attack_start_range_scale, auctioneer, banker, base_skill_delay, base_skill_strafe, base_skill_id,
            blacksmith, char_race_id, check_backpack, check_target_under_terrain, crowd_effect, decaying_sec_after_looted, dont_pushable_like_ghost, engage_combat_bgm_id, engage_combat_give_quest_id, equip_cloths_id,
            equip_weapons_id, exp_adder, exp_multiplier, expedition, faction_id, force_target_me_on_attack, friendly_near_quest_id, heir_level, honor_point, level,
            look_converter, mate_equip_slot_pack_id, mate_kind_id, mate_revive_delay, mate_revive_hp_percent, mate_revive_mp_percent, merchant, merchant_random_pack_id, model_id, multi_jump,
            multi_jump_pow_y, multi_jump_pow_z, name, no_apply_total_custom, no_exp, no_penalty, non_pushable_by_actor, npc_ai_client_param_id, npc_ai_param_id, npc_grade_id,
            npc_interaction_set_id, npc_kind_id, npc_nickname_id, npc_posture_set_id, npc_strafe_param_id, npc_template_id, opacity, party_flag, pet_item_id, priest,
            prior_visibility, ragdoll_after_death_anim, repairman, return_distance, return_when_enter_housing_area, run_away_threshold, scale, show_faction_tag, show_name_tag, show_on_boss_telescope,
            sight_fov_scale, sight_range_scale, skill_trainer, so_state, sound_pack_id, specialty, specialty_coin_id, stabler, teleporter, total_custom_id,
            track_friendship, tradegood_buy, trader, use_abuser_list, use_ddcms_mount_skill, use_hp_bar_split, use_model_camera_distance, use_range_mod, visible_to_creator_only, weapon_element_id,
            weapon_element_level
        FROM npcs ORDER BY id
        """;

    internal static NpcTemplateDefinition Read(SqliteDataReader reader) => new()
    {
        Id = NativeContentReader.UInt32(reader, 0),
        AbilityChanger = NativeContentReader.Boolean(reader, 1),
        AbsoluteReturnDistance = NativeContentReader.Single(reader, 2),
        AcceptAggroLink = NativeContentReader.Boolean(reader, 3),
        ActivateAiAlways = NativeContentReader.Boolean(reader, 4),
        Aggression = NativeContentReader.Boolean(reader, 5),
        AggroLinkHelpDist = NativeContentReader.Single(reader, 6),
        AggroLinkSightCheck = NativeContentReader.Boolean(reader, 7),
        AggroLinkSpecialGuard = NativeContentReader.Boolean(reader, 8),
        AggroLinkSpecialIgnoreNpcAttacker = NativeContentReader.Boolean(reader, 9),
        AggroLinkSpecialRuleId = NativeContentReader.UInt32(reader, 10),
        AiFileId = NativeContentReader.UInt32(reader, 11),
        ArmorElementLevel = NativeContentReader.Int32(reader, 12),
        ArmorTypeId = NativeContentReader.UInt32(reader, 13),
        AttackStartRangeScale = NativeContentReader.Single(reader, 14),
        Auctioneer = NativeContentReader.Boolean(reader, 15),
        Banker = NativeContentReader.Boolean(reader, 16),
        BaseSkillDelay = NativeContentReader.Single(reader, 17),
        BaseSkillStrafe = NativeContentReader.Boolean(reader, 18),
        BaseSkillId = NativeContentReader.UInt32(reader, 19),
        Blacksmith = NativeContentReader.Boolean(reader, 20),
        CharRaceId = NativeContentReader.UInt32(reader, 21),
        CheckBackpack = NativeContentReader.Boolean(reader, 22),
        CheckTargetUnderTerrain = NativeContentReader.Boolean(reader, 23),
        CrowdEffect = NativeContentReader.Boolean(reader, 24),
        DecayingSecAfterLooted = NativeContentReader.Int32(reader, 25),
        DontPushableLikeGhost = NativeContentReader.Boolean(reader, 26),
        EngageCombatBgmId = NativeContentReader.UInt32(reader, 27),
        EngageCombatGiveQuestId = NativeContentReader.UInt32(reader, 28),
        EquipClothsId = NativeContentReader.UInt32(reader, 29),
        EquipWeaponsId = NativeContentReader.UInt32(reader, 30),
        ExpAdder = NativeContentReader.Int32(reader, 31),
        ExpMultiplier = NativeContentReader.Single(reader, 32),
        Expedition = NativeContentReader.Boolean(reader, 33),
        FactionId = NativeContentReader.UInt32(reader, 34),
        ForceTargetMeOnAttack = NativeContentReader.Boolean(reader, 35),
        FriendlyNearQuestId = NativeContentReader.UInt32(reader, 36),
        HeirLevel = NativeContentReader.Int32(reader, 37),
        HonorPoint = NativeContentReader.Int32(reader, 38),
        Level = NativeContentReader.Int32(reader, 39),
        LookConverter = NativeContentReader.Boolean(reader, 40),
        MateEquipSlotPackId = NativeContentReader.UInt32(reader, 41),
        MateKindId = NativeContentReader.UInt32(reader, 42),
        MateReviveDelay = NativeContentReader.Int32(reader, 43),
        MateReviveHpPercent = NativeContentReader.Int32(reader, 44),
        MateReviveMpPercent = NativeContentReader.Int32(reader, 45),
        Merchant = NativeContentReader.Boolean(reader, 46),
        MerchantRandomPackId = NativeContentReader.UInt32(reader, 47),
        ModelId = NativeContentReader.UInt32(reader, 48),
        MultiJump = NativeContentReader.Int32(reader, 49),
        MultiJumpPowY = NativeContentReader.Single(reader, 50),
        MultiJumpPowZ = NativeContentReader.Single(reader, 51),
        Name = NativeContentReader.String(reader, 52),
        NoApplyTotalCustom = NativeContentReader.Boolean(reader, 53),
        NoExp = NativeContentReader.Boolean(reader, 54),
        NoPenalty = NativeContentReader.Boolean(reader, 55),
        NonPushableByActor = NativeContentReader.Boolean(reader, 56),
        NpcAiClientParamId = NativeContentReader.UInt32(reader, 57),
        NpcAiParamId = NativeContentReader.UInt32(reader, 58),
        NpcGradeId = NativeContentReader.UInt32(reader, 59),
        NpcInteractionSetId = NativeContentReader.UInt32(reader, 60),
        NpcKindId = NativeContentReader.UInt32(reader, 61),
        NpcNicknameId = NativeContentReader.UInt32(reader, 62),
        NpcPostureSetId = NativeContentReader.UInt32(reader, 63),
        NpcStrafeParamId = NativeContentReader.UInt32(reader, 64),
        NpcTemplateId = NativeContentReader.UInt32(reader, 65),
        Opacity = NativeContentReader.Single(reader, 66),
        PartyFlag = NativeContentReader.Boolean(reader, 67),
        PetItemId = NativeContentReader.UInt32(reader, 68),
        Priest = NativeContentReader.Boolean(reader, 69),
        PriorVisibility = NativeContentReader.Boolean(reader, 70),
        RagdollAfterDeathAnim = NativeContentReader.Boolean(reader, 71),
        Repairman = NativeContentReader.Boolean(reader, 72),
        ReturnDistance = NativeContentReader.Single(reader, 73),
        ReturnWhenEnterHousingArea = NativeContentReader.Boolean(reader, 74),
        RunAwayThreshold = NativeContentReader.Single(reader, 75),
        Scale = NativeContentReader.Single(reader, 76),
        ShowFactionTag = NativeContentReader.Boolean(reader, 77),
        ShowNameTag = NativeContentReader.Boolean(reader, 78),
        ShowOnBossTelescope = NativeContentReader.Boolean(reader, 79),
        SightFovScale = NativeContentReader.Single(reader, 80),
        SightRangeScale = NativeContentReader.Single(reader, 81),
        SkillTrainer = NativeContentReader.Boolean(reader, 82),
        SoState = NativeContentReader.String(reader, 83),
        SoundPackId = NativeContentReader.UInt32(reader, 84),
        Specialty = NativeContentReader.Boolean(reader, 85),
        SpecialtyCoinId = NativeContentReader.UInt32(reader, 86),
        Stabler = NativeContentReader.Boolean(reader, 87),
        Teleporter = NativeContentReader.Boolean(reader, 88),
        TotalCustomId = NativeContentReader.UInt32(reader, 89),
        TrackFriendship = NativeContentReader.Boolean(reader, 90),
        TradegoodBuy = NativeContentReader.Boolean(reader, 91),
        Trader = NativeContentReader.Boolean(reader, 92),
        UseAbuserList = NativeContentReader.Boolean(reader, 93),
        UseDdcmsMountSkill = NativeContentReader.Boolean(reader, 94),
        UseHpBarSplit = NativeContentReader.Boolean(reader, 95),
        UseModelCameraDistance = NativeContentReader.Boolean(reader, 96),
        UseRangeMod = NativeContentReader.Boolean(reader, 97),
        VisibleToCreatorOnly = NativeContentReader.Boolean(reader, 98),
        WeaponElementId = NativeContentReader.UInt32(reader, 99),
        WeaponElementLevel = NativeContentReader.Int32(reader, 100),
    };
}

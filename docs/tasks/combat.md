<!-- Audit of combat: native x2game-dev_dedicate.dll (ArcheAge 10.0.2.13 r575) vs this repo at 76e439f. Paths rewritten to the src/ layout; line numbers refer to 76e439f. -->

## Combat: skills, effects, buffs, damage/heal, CC, death, stats and formulas (prefix CMB)

**Summary.** The native zone is not a combat-resolution engine. World computes skill effects (damage, heal, dispel, teleport, aggro, etc.) and tells the zone the results via `WZSkillStarted/Fired/Ended/Stopped`, `WZCastingStopped`, `WZUnitDamaged/Healed`, `WZKnockBack/Blink/Impulse`, `WZBuff*`, `WZUnitPoints/Death/Resurrection`. The native DLL contains no per-effect-type classes (no `*Effect@X2@@` RTTI other than client particle/FOV/postprocess effects); all of the `effects`, `damage_effects`, `heal_effects`, `dispel_effects`, `aggro_effects`, `special_effects`, `skill_effects`, `buff_effects` tables only show up in the shared content loaders. What the zone does own is: (a) a mirror of per-unit skill/cooldown/plot/buff state and its side effects on actors (state-flag bits, glide/walk-only/cannot-jump/blank-minded, scale, skill controllers, psychokinesis, taunt); (b) NPC aggro consumption of damage/heal events; (c) the NPC AI skill-selection/use pipeline (`ServerAIUnitSystem::PickSkill/UseSkill/...`) that originates `ZWStartSkill/ZWStopSkill/ZWStopCasting`; (d) the AI-facing Lua `ScriptBindUnit` combat API; (e) the unit stat/formula engine (HP/MP/attributes) used for `UnitPoints` limits and NPC threat. Repo coverage of (e) is strong (~85%: formulas, base getters, equipment, passives, dynamic curves, limits, gear score, buff stacking arrays, buff timing/landing). Coverage of (a) is ~15% (only buff array stacking, scale, timing, remove-on-land, passive grants, death/resurrect flags, combat/target/duel/force-attack flags), of (b) ~30% (`ZoneAggroTable` exists, but the damage/heal packet handlers that feed it are not wired), of (c) and (d) ~0-5% (`ZoneSceneProviders.cs:621-630` throws `NotSupportedException` "no skill executor"), and the skill/plot/cooldown/knockback WZ packets are not decoded at all (they hit the "unimplemented opcode" counter in `ZoneSession.cs:~540`). None of this blocks the first-entry milestone except verifying that the stat path completes for a real character.

### Native opcode/handler inventory (registered in WZ registration `sub_3936F740`; handler addresses paired by packet type via registrar functions)

| Native item | Native evidence | Repo status |
| --- | --- | --- |
| WZSkillStarted (0x2B) -> `ZoneClient` handler `39363460` -> `sub_39456D70` ("X2::GameServer::StartSkill") | active-skill std::map `qword_3A78EBC8`, SkillDesc lookup `39ABF2C0`, unit +32280/+32284 current skill, +32288/+32296 last-channel | absent (no message class, no handler; ZoneSession default branch) |
| WZSkillFired (0x2C) -> `393634B0` -> `39457170` ("FireSkill") | same map, skill-controller dispatch via actor vtable +32/+328/+344 | absent |
| WZSkillEnded (0x2D) -> `39363510` -> `39456B20`; WZSkillStopped (0x2E) -> `39363530` -> `39456600`; WZCastingStopped (0x2F) -> `39363550` -> `39456630` | clears unit +32297 flag / +32280 and starts end-cooldown via cooldown mgr `39BFA070` | absent |
| WZUnitDamaged (0x30) -> `39363580` -> `393225F0` -> `39321C80` (NpcAggroTable damage aggro) | uses `39321340` AddAggro | `ZoneAggroTable.Add` exists (`src/AAEmu.Zone/Combat/ZoneAggroTable.cs:49`), packet/handler absent |
| WZUnitHealed (0x31) -> `393635B0` -> `394576B0` ("By Heal", "X2::GameServer::HealUnit") | iterates healed unit aggressive-NPC list (+34432) and adds scaled heal aggro | absent |
| WZKnockBackUnit (0x32) `393635D0`, WZBlinkUnit (0x33) `39369B10`, WZImpulseUnit (0x34) `39363600`, WZUnitInvisible (0x35) `39363660`, WZOnUnitBlinked (0x23) `39363420` | `39258BB0` (AI notify vtable+1128), actor impulse, stealth set | absent (`IZonePhysicsMotion.ApplyImpulse` seam exists, unused for this) |
| WZSkillCooldownReset `39364D00`, WZSkillCooldownReduce `393652F0`, WZChangeChargeSkillCooldown `393653F0` | unit+31912 cooldown manager `39BF9120/39BF91E0/39BF7970/39BF93B0/39BF9BB0` | absent |
| WZPlotEvent `393636D0` -> `39455730` ("ZonePlotMan::PlayEvent"), PlotEnded `39363760`, PlotCastingStopped `39363780`, PlotChannelingStopped `393637B0` | plot loaders `39C74F90` | absent |
| WZCreateSkillController `39362F50`, WZSkillControllerState `39362F00` | actor vtable +336 / (+320)->+144 | absent; `FindSkillController` returns null (`ZoneActorMovementInputServices.cs:648`) |
| WZBuffCreated `39364D90` -> `39450740` (ZoneBuffMan::Create) | BuffDesc `39ABF310`, stack `39D28760`, post-create `3944FEB0` | partial: `ZoneUnitRegistry.CreateBuff` + `ZoneBuffLifecycle.Create` (array/stack rules, scale, passives); side effects absent |
| WZBuffRemoved `39364DE0` -> `39450420` (Destroy) -> `394501E0` (Cleanup) | state flags, scale restore, cooldown start, taunt cancel | partial (`RemoveBuff`: passives + scale only) |
| WZBuffUpdated `39364E20` -> `3944F730` (Change) | stack-rule-7 tick reset etc. | covered (`ZoneBuffLifecycle.Update`) |
| ZoneBuffMan tick `3944FC90` / gate `3944F850` / advance `39450A70` | dynamic modifiers, remove-on-land -> ZWRemoveBuff | covered (`ZoneBuffTiming.cs`, `ZoneBuffLandingRemoval.cs`, `ZoneUnitRegistry.AdvanceBuffs`) |
| WZUnitPoints `39364C30`, UnitDeath `393633F0` -> `3935D490`, UnitResurrection `39369A60` -> `3935D970` | HP/MP set; die/res side effects | partial (`ZoneUnit.Apply/Die/Resurrect`) |
| WZCombatEngaged `393637E0` -> `392587B0`; WZCombatCleared `39364E70`; ForceAttackSet `39364CC0`; TargetChanged `3936E170`; UnitDuelState `39364EB0`; CvF/FvF relations `39363810/39363830` | flags | covered as flags (`ZoneUnitRegistry` 314-355, `ZoneCombatRelations.cs`) |
| WZUpdateAggro/AggroReset/AggroCopy/FakeDeath | NpcAggroTable | covered (`ZoneSession.cs:398-409`, `ZoneAggroTable.cs`) |
| WZBuffLearned/SkillsReset/AddSwapPassiveBuffs/LevelChanged | passive manager | covered (`ProgressionChanges.cs`, `ZoneProgressionState.cs`) |
| Stat/formula engine (`39B1B630`, `39BF6C20`, `39BF7150`, `39BF3380`, `39B2E220`, `39D432E0`) | unit_formulas, limits, modifiers | covered (`ZoneStatCalculator.cs`, `ZoneBaseAttribute.cs`, `ContentFormula.cs`, docs/unit-stats.md) |
| Gear score / equipment combat stats | `39D432E0`, reinforcement formulas | covered (`ZoneGearScoreCalculator.cs`, `ZoneEquipmentCombatCalculator.cs`) |
| Effect types: Damage/Heal/Dispel/Aggro/Special/Teleport/Knockback/Spawn/etc. | no zone RTTI; tables only in shared loader (`39AD0E90` skill_effects, `39C0A130` buff_effects, `39AD2D30` buff_tick_effects) | not zone-owned (World); no repo work required except content loading for flags (see CMB-04/08) |
| Projectiles, AoE shapes, LoS | loaders `39B06380` projectiles, `39C02BD0` aoe_shapes; zone only validates range for AI (`3925C3D0`) | absent |
| Crowd-control (stun/sleep/root/silence/knock_down/fear) | buff columns `stun, sleep, root, silence, knock_down, crippled, fastened, ragdoll`; unit state flags array +32080 (14 bytes) refreshed by `3925A270` | absent |
| Taunt | `TauntBuffFinder@GameServer`, `NpcAggroTable::TauntedBy/CancelTauntedBy`, `3931F710` | `ZoneAggroTable.Taunt/CancelTaunt` exist; buff-triggered call absent |
| Crowd (aura-like) buff | ZWCreateBuff/RemoveBuff for crowd_buff_id | covered (`ZoneNpcCrowdBuffState.cs`) |
| Buff finders (Glide, WalkOnly, CannotJump, BlankMinded, ImpossibleTarget, RemoveOnLand, DynamicUnitModifier, TriggerSpecialEffect, SkillModifier, AutoLearnPassive, PlayNote, CantEnterBattleField, BuffBlockedChildDoodad) | RTTI `.?AV<Name>@X2@@`; zone-used: `3925C650` (glide), `3925CF40`, `3925D060`, `3925ED90`, `3944F850` (RemoveOnLand/DynamicUnitModifier) | only RemoveOnLand/DynamicUnitModifier equivalents exist; passive learning handled via `ZoneProgressionState`; rest absent |

### Tasks

### CMB-01: Verify the stat/vital path completes for a real first-entry character
- Priority: P0 (verification of existing work; blocks UnitPoints/Resurrect on first entry if any source is unresolved)
- Native evidence: `39364C30` (WZUnitPoints handler writes HP/MP via `3925AB00/3925AC60`), `39BF7150`/`39BF6C20` base getters, `39D432E0` gear score formula input, `3935D970` resurrect refill.
- Repo state: `ZoneStatCalculator.cs` adds "equipment modifiers", "equipment reinforcement modifiers", "appellation stamp modifiers", "gear score", "buff dependency on attribute N", "dynamic modifier N" to `UnresolvedSources` when `ZoneStatContent.Equipment/Reinforcements/Appellations/GearScores/EquipmentCombat` are null (`ZoneStatContent.cs:65-70`); `ZoneUnit.Resurrect` (`ZoneUnit.cs:101`) and `ZoneNpcSimulation.cs:505` throw on incomplete stats.
- Work: run the real character snapshot (zone 129, equipped gear, buffs, appellation) through `ZoneStatCalculator.Calculate` against the live `game.compact` and assert `IsComplete`; log first unresolved source; add a regression test using a real-shaped fixture; make sure every content provider is passed to `ZoneStatContent` in the production host (not only tests).
- Depends on: content DB path (host/H1).
- Confidence: medium

### CMB-02: Add WZ message codecs and opcodes for the skill/damage/knockback/cooldown/plot packets
- Priority: P1
- Native evidence: registrars/handlers WZSkillStarted `39363460`, SkillFired `393634B0`, SkillEnded `39363510`, SkillStopped `39363530`, CastingStopped `39363550`, UnitDamaged `39363580`, UnitHealed `393635B0`, KnockBackUnit `393635D0`, BlinkUnit `39369B10`, ImpulseUnit `39363600`, UnitInvisible `39363660`, OnUnitBlinked `39363420`, SkillCooldownReset `39364D00`, CooldownReduce `393652F0`, ChangeChargeSkillCooldown `393653F0`, PlotEvent `393636D0`, PlotEnded `39363760`, PlotCastingStopped `39363780`, PlotChannelingStopped `393637B0`, CreateSkillController `39362F50`, SkillControllerState `39362F00`, ZombieCharacter `39364380`, AttackOnQuest `3936E3F0`, AttackFaction and FactionImmigrateNpcSpawnerCondition (last two registrars `39384D80` -> `39365450`, `393914B0` -> `3936E940`; assignment to packet name unverified). Registration order in `sub_3936F740`; note my registrar-order enumeration is 1 lower than repo numbering from 0x38 onward (offset-based check: SkillCooldownReset registrar stores at `a1+464` = 0x39), so keep repo `WzOpcodes` values and pair by packet type.
- Repo state: `WzOpcodes.cs` declares most constants (0x2B-0x35, 0x39-0x3D, 0x86-0x89) but `AAEmu.Zone.Protocol/Messages/` has no codec for any of them; `ZoneSession.ReceiveCore` falls to the default "not implemented" counter.
- Work: recover each packet body layout from the `sub_3936xxxx` initializers (e.g. `sub_3936D8E0` for SkillStarted, size 0x90) and the `PacketFunctor` reader; add `readonly record struct` codecs with round-trip tests like `BuffChanges.cs`; add named cases in `ZoneSession.ReceiveCore` that decode and route to the owners in CMB-05/06/10/16/19.
- Depends on: none (decode); owners in CMB-05, CMB-06, CMB-10, CMB-16, CMB-19.
- Confidence: high (handlers) / medium (packet layouts not yet recovered)

### CMB-03: Add outbound ZW opcodes/messages the zone originates for combat
- Priority: P1
- Native evidence: ZWStartSkill (opcode 24, sender `39326730` ServerAIUnitSystem::UseSkill, also `39227440`), ZWStopSkill (25, `39325940`/`39354580`), ZWStopCasting (26, `393259F0`), ZWLongDistSkillControllerCreated (27, `3935DD20`), ZWSkillControllerState (9, `392291B0`), ZWClearCombat (12, `393534D0`/`3935FAC0`), ZWKillNpc (49, `3933EA90`), ZWAggroRemove (65, `3931F790`/`39321340` area), ZWCreateBuff/ZWRemoveBuff senders `39325AF0`/`39325B80` (AI AddBuff/DelBuff), `39352F20`/`39353020` (Lua AddBuff/DelBuff), `39346050`, `39347060`, `39268720` (vehicle-levitation detection), ZWUnitFell `3935C850` ("notify fall ... impactVel").
- Repo state: `ZwOpcodes.cs` lists 0x0A CreateBuff and 0x0B RemoveBuff (with `BuffCreationRequest/BuffRemovalRequest`, used only in `ZoneNpcCrowdBuffState.cs:112,131` and landing removal) but not 0x09, 0x0C, 0x18, 0x19, 0x1A, 0x1B, 0x31, 0x41; no UnitFell/UnitCollision senders.
- Work: add the missing constants and writers (bodies via the `sub_39362xxx` initializers: `39362920` StartSkill, `39362970` StopSkill, `39362980` StopCasting, `39362860` ClearCombat, `39362B40` KillNpc, `39362D00` AggroRemove); route through the session's outbound notification queue (`ZoneUnitRegistry.TryDequeueNotification`).
- Depends on: CMB-11, CMB-12 consumers.
- Confidence: high

### CMB-04: Load full SkillDesc content (the `skills` table and companions)
- Priority: P1
- Native evidence: loader `39AC17C0` (SELECT of ~200 columns: casting_time, cooldown_time, cooldown_tag_id, channeling_*, target_type_id, target_relation_id, min_range, max_range, front_angle, target_area_*, check_obstacle, check_terrain, projectile_id, skill_controller_id, plot_id, ignore_global_cooldown, custom_gcd, default_gcd, weapon_gcd_id, use_condition_bits, stop_casting_*, etc.), `39ABF2C0` (desc lookup by id), `39BFD7D0` skill_controllers, `39B06380` projectiles, `39C02BD0` aoe_shapes, `39C146E0` skill_modifiers, `39AD0E90` skill_effects.
- Repo state: `src/AAEmu.Zone/Content/ZoneSkillContent.cs` loads only `id, ability_id` from `skills` plus `levels.total_exp`.
- Work: extend `ZoneSkillContent` into a typed `SkillDescriptor` reader (struct offsets used by native: +88 cast, +124 cooldown, +304 controller/anim, +380, +396/+400 cooldown tag, +802/+803 flags), plus skill_controllers/projectiles/aoe_shapes/skill_modifiers; keep native NULL-column semantics via `NativeContentReader`.
- Depends on: none.
- Confidence: high

### CMB-05: Implement the zone skill-state mirror (StartSkill/FireSkill/EndSkill/StopSkill/StopCasting)
- Priority: P1
- Native evidence: `39456D70` StartSkill ("invalid desc %u"), `39457170` FireSkill ("invalid desc %u"), `39456B20` EndSkill, `39456600`, `39456630`; global map `qword_3A78EBC8` keyed by skill timeline id (a3); per-unit fields +32280/+32284 (current skill id/tl), +32288/+32296 (last channel skill/flag), +32297 (casting flag); calls skill-controller factory on actor (vtable +32/+328/+344) and AutoAttack toggles; cooldown start `39BFA200` when `desc+400==const && desc+396!=0`.
- Repo state: absent. `UnitCastAction.cs` only decodes the snapshot union (3938DAF0); no active-skill registry, no per-unit cast state on `ZoneUnit`.
- Work: add `ZoneSkillState` (active timelines map + per-unit cast fields) owned by the registry; implement the five handlers with the exact field writes/clears; hook controller dispatch to CMB-16; reset on death (CMB-15) and unit removal.
- Depends on: CMB-02, CMB-04, CMB-10, CMB-16.
- Confidence: medium

### CMB-06: Wire WZUnitDamaged/WZUnitHealed into NPC aggro and AI notification
- Priority: P1
- Native evidence: `393225F0` (damage handler) -> `39321C80` -> `NpcAggroTable::AddAggro` `39321340`; `394576B0` HealUnit (distributes `heal * <float global at sub_390B11E0()+528> / count` aggro across the healed unit's aggressive-NPC list at unit +34432, logs "aggressiveNpcList changed in process"); `Npc::OnAffectedByDamage` `3933C8D0`; AI notify `39258BB0` (sets time +34156, AI vtable +1128); boss warning log `3931D120`.
- Repo state: `ZoneAggroTable.Add/UpdateIncomingMultiplier` exist; `ZoneNpcSimulation` has no damage/heal entry point; opcodes 0x30/0x31 unhandled.
- Work: decode packets (CMB-02); add `ZoneNpcSimulation.UnitDamaged/UnitHealed` that resolve attacker/victim (source type union at +24: 0 skill, 1 npc?, 2/3 other), apply `incoming_aggro_mul` (attr 62) / `aggro_mul` (attr 90) via `ZoneStatCalculator`, call `Aggro.Add`, trigger target re-selection (`Npc::FindNewTargetIfItNeed` `39345770`) and AI notification.
- Depends on: CMB-02.
- Confidence: medium

### CMB-07: Implement ZoneBuffMan create/destroy side effects on the unit
- Priority: P1
- Native evidence: Create `39450740` -> `3944FEB0` (BuffDesc flag bytes +1120..+1134: bit tests at +1123/+1124/+1125/+1127/+1134 drive unit state set via vtable +96 with codes 0,7,1,6,8 reason "buff"; psychokinesis (+1127&2) vtable +168/+176/+72; skill controller install from desc+384 (vtable +328, id counter at ZoneBuffMan+7872, instance +88); scale `3925A390`; taunt `39320FE0`); Destroy `39450420` / Cleanup `394501E0` (reverse of the above, `vtable +344`, end-cooldown via `39BFA200` on desc+64/+72/+84 and buff skill id, taunt-cancel `3931F710`); post-change refresh `3935EC90` / `3925A270` (14-byte unit state-flag mirror at +32080 compared with BuffMan +136, change callback vtable +232, then attr 290 invalidation `39BF3430`) / `3925C650` (glide speed params into actor +107..109 from buff modifiers 7/8/9) / `3944F850` update gate.
- Repo state: `ZoneUnitRegistry.CreateBuff/RemoveBuff` (`ZoneUnitRegistry.cs:424-470`) do array stacking, passive grants and scale only; no state flags, controller install, psychokinesis, taunt or cooldown-on-end.
- Work: add `ZoneBuffEffects` that evaluates BuffDesc flags on create/destroy/refresh and updates a `ZoneUnitStateFlags` (14 flags: stun/sleep/root/silence/etc.) with change callbacks; recompute glide params; call `ZoneAggroTable.Taunt/CancelTaunt` for taunt buffs; start end-cooldowns; keep `CommitBuffs` ordering (create: `3944FEB0` before refresh helpers).
- Depends on: CMB-08, CMB-10, CMB-16.
- Confidence: medium (flag-bit meanings need confirming per column)

### CMB-08: Load the full BuffDesc (buffs table and companion tables)
- Priority: P1
- Native evidence: buffs loader (repo cites `39B42380`; SELECT lists ~230 columns: stun, sleep, root, silence, knock_down, crippled, fastened, ragdoll, blank_minded, cannot_jump, walk_only, gliding*, psychokinesis, stealth/detect_stealth/anti_stealth, taunt/taunt_with_top_aggro, scale/scaleDuration, immune_*/melee/ranged/spell/siege_immune, reflection_*, remove_on_* (attack/damage/death/move/mount/start_skill/use_skill/land...), tick*, crowd_*, aura_*, damage_absorption_*, mana_shield_ratio, skill_controller_id, transform_buff_id, link_buff_id, cooldown_skill_id/time/tag), `39B420B0` tagged_immune_buffs, `buff_modifiers`, `buff_unit_modifiers`, `buff_triggers`, `buff_tick_effects` `39AD2D30`, `buff_effects` `39C0A130`, `buff_breakers`, `buff_tolerances`, `buff_visual_changes`, `unit_status_buff_tags`.
- Repo state: `ZoneBuffContent.cs` reads only kind, stack rule, max stack/charge/lifetime, per_unit_creation, one_time, off_passive*, remove_on_land, gliding, scale(+duration); tags, passive_buffs, buff_passive_buffs, buff_unit_modifiers (conditional), dynamic_unit_modifiers/funcs.
- Work: add the remaining columns needed by CMB-07/09 (CC flags, immunities, stealth, taunt, controller id, cooldown fields, tick) and `tagged_immune_buffs`/`unit_status_buff_tags`; add content tests.
- Depends on: none.
- Confidence: high

### CMB-09: Implement unit crowd-control/status state consumers (stun, sleep, root, silence, walk-only, cannot-jump, blank-minded, glide)
- Priority: P1
- Native evidence: BuffFinder RTTI `GlideBuffFinder` (use `3925C650`), `WalkOnlyBuffFinder` (`3925CF40`), `CannotJumpBuffFinder` (`3925D060`), `BlankMindedBuffFinder` (`3925ED90`), `ImpossibleTargetBuffFinder` (`397AF120`), `CantEnterBattleFieldBuffFinder` (`394E81E0`); log strings "BLANK_MINDED", "CANNOT_CAST_IN_STUN", "IMMUNE", "SILENCE", "TARGET_IMMUNE", "NEED_STEALTH".
- Repo state: absent (no references; movement code has no buff gating). `ZoneBuffTiming`/`ZoneBuffContent` only expose RemoveOnLand/Gliding bits.
- Work: derive `ZoneUnitStatus` from the unit's buffs (via BuffDesc flags) and expose to the movement controller (walk-only, cannot-jump, rooted/stunned locks, glide parameters) and to NPC AI (cannot act when stunned/slept/blank-minded); apply on every buff change (CMB-07 hook).
- Depends on: CMB-07, CMB-08, movement/M1.
- Confidence: medium

### CMB-10: Mirror the per-unit skill cooldown manager
- Priority: P1
- Native evidence: per-unit object at unit+31912: reset `39BF9120`/`39BF91E0`, `39BF7970`, `39BF93B0`, `39BF9BB0`; start `39BFA200` -> `39BF5720` (cooldown computation) -> `39BFA070`; handlers `39364D00` (CooldownReset), `393652F0` (Reduce), `393653F0` (ChargeChange); cooldown-modifying attributes via `39BF6BF0`; `39BF96F0` second-tag cooldown.
- Repo state: absent.
- Work: implement `ZoneCooldownManager` (per skill id, cooldown tag, charge counts, GCD tags) fed by World packets and by local starts (skill end / buff end); expose `GetRemaining(skill)`.
- Depends on: CMB-02, CMB-04.
- Confidence: medium

### CMB-11: Implement the NPC AI skill pipeline (pick, can-use, use, stop)
- Priority: P1
- Native evidence: `ServerAIUnitSystem::PickSkill` `393289C0`, `PickAiSkill` `39329E00`, `PickFlySkill` `393292E0`, `CanUseSkill` `39326980`, `UseSkill` `39326730` (target resolve `390B31B0`, builds ZWStartSkill, notifies AI `+304`, logs "Sending skill(%u) request to world"), `StopSkill` `39325940`, `StopCasting` `393259F0`, `GetBaseSkillInfoResult` `393260E0`, `GetTargetInfo` `39327070`, `SetAttentionTarget` `39325ED0`, `RestoreAiUnitAttr` `39325C20`, requirement check `39361350`, execution `39361590`; skill-eligibility validator at `qword_3A607A48` vtable +96 (cooldown, mana/resources, requirement/range), range test `3925C3D0`.
- Repo state: `ZoneSceneProviders.cs:621-630` `CheckRequirement/CanUseSkill/UseSkill` throw `NotSupportedException("TODO(npc-signal-skills)")`; no `np_skills`/`npc_ai_params`/`ai_skill` content; `ZoneAiCombatClasses.cs` only holds assessment factors.
- Work: load NPC skill lists (`np_skills`, `npc_ai_params`, `ai_command_sets` skill entries); implement requirement evaluation (`unit_reqs`, `skill_reqs`), range/angle/target-relation validation, cooldown check (CMB-10), mana/resource check using `ZoneStatCalculator`, pick-skill weighting, `UseSkill` -> `ZWStartSkill` (CMB-03), `StopSkill/StopCasting`; remove the NotSupported seams.
- Depends on: CMB-03, CMB-04, CMB-10, CMB-13.
- Confidence: medium

### CMB-12: Implement the Lua ScriptBindUnit combat API used by AI scripts
- Priority: P1
- Native evidence: `ScriptBindUnit::CanUseSkill` `393521A0`, `IsInValidSkillRange` `39353140`, `GetTargetDistance` `39352920`, `IsValidNpcTarget` `39352A50`, `ClearSkillController` `39351210`, `StartAiSkillLists` `39354BB0`, `NpcAddAggro` `39353F90`, `NpcStopAutoAttack` `39354580`, `NpcRemoveAggroOutOfRange` `39354150`, `NpcSetMinAps` `39353E50`, AddBuff `39352F20` / DelBuff `39353020`, `NpcAddAlertSafeTarget`, `NpcActivateAggroUpdate`.
- Repo state: `Scripting/` has only `AI.AddCombatClass` (`ZoneAiCombatClassBinding.cs`), goal-pipe/log/track-pattern bindings; none of the combat unit functions.
- Work: add a `ZoneUnitScriptBinding` registering these functions with native argument/return coercion and error logging; implement against CMB-11/06/03.
- Depends on: CMB-11, CMB-06, CMB-03.
- Confidence: medium

### CMB-13: Skill target/range/obstacle validation helpers
- Priority: P1
- Native evidence: `390B31B0` (resolve skill target args against desc and unit), `3925C3D0` (range/angle check; used by `IsInValidSkillRange` `39353140` and CanUseSkill), `ScriptBindUnit::GetTargetDistance` `39352920`; skill desc columns min_range/max_range/front_angle/target_angle/check_obstacle/check_terrain/valid_height; physics ray cast available through `ZoneNativeTerrainPhysicsWorld.SceneQueries.cs`.
- Repo state: absent (scene queries exist but no skill-range/LoS wrapper).
- Work: implement distance/edge-to-edge, angle and height checks per SkillDesc plus optional LoS ray via the existing scene query; unit tests against native-derived cases.
- Depends on: CMB-04.
- Confidence: low-medium

### CMB-14: Complete death/resurrection side effects on the unit
- Priority: P1
- Native evidence: Die `3935D490` ("unit dead. id(%u), name(%s)": AI OnDead vtable +1376, `3925B2C0(unit,1)`, skill/controller cleanup vtable +184, `39BF3430(+7504, 290)` invalidation, zero +32080 state flags, reset +32280/+32284/+32288/+32292/+32297, `+34424=257`, `+34426=1`, vtable +176 for non-NPC, notify `39360CE0`); Resurrect `3935D970` (AI vtable +1384, Lua hook "OnUnitResurrect" via actor script object, `3925E8E0`, `3925B2C0(unit,0)`, log "Unit(%u, %s) is resurrected"); WZUnitPoints on dead unit logs "Received set health(%d) on dead unit".
- Repo state: `ZoneUnit.Die/Resurrect` (`ZoneUnit.cs:88-113`) clear flags/target and apply pose/HP only; `Npcs?.Died(unit)` hook exists.
- Work: reset skill/cast state (CMB-05) and state flags (CMB-07), clear aggro/combat, notify AI, run Lua `OnUnitResurrect`, add the dead-unit-points diagnostic.
- Depends on: CMB-05, CMB-07.
- Confidence: medium

### CMB-15: Implement skill controllers (dash/leap/rope/impulse/floating/move/rotate/anchor/crawl/wandering/flowgraph)
- Priority: P1
- Native evidence: RTTI `ISkillController`, `DashSkillController`, `LeapSkillController`, `RopeSkillController`, `RopeReadySkillController`, `ActorRopeSkillController`, `ImpulseSkillController`, `FloatingSkillController`, `MoveSkillController`, `RotateSkillController`, `AnchorSkillController`, `CrawlSkillController`, `WanderingSkillController`, `FlowGraphSkillController`, `Net*` variants; loader `39BFD7D0`; WZCreateSkillController `39362F50` (actor vtable +336), WZSkillControllerState `39362F00`, ZWSkillControllerState (9) `392291B0`, ZWLongDistSkillControllerCreated (27) `3935DD20`, `ScriptBindUnit::ClearSkillController` `39351210`.
- Repo state: install seam only: `ZoneActorMovementInput.cs:186-221` calls `services.FindSkillController/InstallSkillController`, but `ZoneActorMovementInputServices.cs:648-657` always returns null/throws.
- Work: add `skill_controllers` content reader; implement controller classes against `IZonePhysicsMotion` (impulse/velocity), per-frame update, end/cleanup, replication messages; wire to WZ create/state packets and buff-installed controllers (desc+384).
- Depends on: CMB-02, CMB-04, movement/M1.
- Confidence: medium

### CMB-16: Handle knockback/impulse/blink/invisible packets
- Priority: P1
- Native evidence: KnockBack `393635D0` (AI notify `39258BB0`), Blink `39369B10` (pose set), Impulse `39363600` (`3925C7D0` after actor check vtable +88), OnUnitBlinked `39363420` -> `3921A4F0`, UnitInvisible `39363660` (unit vtable +192).
- Repo state: absent; `IZonePhysicsMotion.ApplyImpulse` and `ZoneMovementSceneServices.Impulse` exist but are not driven by packets.
- Work: after CMB-02 decoding, apply impulse/knockback to actor physics (respecting physics-living ragdoll content `UseRagdollKnockDown`), teleport pose for blink with spatial index update, store invisibility flag.
- Depends on: CMB-02, M1.
- Confidence: medium

### CMB-17: Combat state lifecycle (engage timeout, ZWClearCombat, force-attack, duel)
- Priority: P2
- Native evidence: Engaged `393637E0` -> `392587B0`; Cleared `39364E70` (vtable +184, clears +32956); ZWClearCombat sender `393534D0` / `3935FAC0` (checks +32773 combat flag); duel state `39364EB0` (+32744/+32748); force attack `39364CC0` (+32775).
- Repo state: flag mirroring only (`ZoneUnitRegistry.cs:314-355`).
- Work: implement the zone-side combat-engaged timer/clear logic for NPCs and characters and send ZWClearCombat; use duel/force flags in hostility evaluation (`ZoneRelation.cs`, `ZoneUnitRelationships.cs`).
- Depends on: CMB-03, CMB-11.
- Confidence: low-medium

### CMB-18: Implement ZonePlotMan (plot events mirror)
- Priority: P2
- Native evidence: `39455730` ("X2::GameServer::ZonePlotMan::PlayEvent": invalid plot event type / plot type / skill type), `39455D20` (PlotEnded), `39455490` (PlotCastingStopped), `394555E0` (PlotChannelingStopped); plot loaders `39C74F90` (plots), plot_events/plot_conditions/plot_effects/plot_next_events/plot_aoe_conditions; WZPlotEvent body carries ~1000 bytes (offsets +32..+1021).
- Repo state: absent (`UnitPlotCast` decode only).
- Work: decode and store plot instance state per unit; no effect execution (World-owned); expose to AI (`PlotCastingStopped`) as needed.
- Depends on: CMB-02, CMB-04.
- Confidence: low-medium

### CMB-19: Fall damage and collision notifications to World
- Priority: P2
- Native evidence: ZWUnitFell `3935C850` ("notify fall name (%s), impactVel(%.2f)"), ZWUnitCollision `390AEC80`, ZWUnitCollisionResult, ZWDoodadCollideUnit; `ZWReportMoveHack/DoodadSurfaceHack` (anti-cheat).
- Repo state: absent (no UnitFell sender; `ZwOpcodes` lists 0x10-0x12 but no writers).
- Work: emit UnitFell on landing with impact velocity from the movement/physics post-step; implement UnitCollision path for slave/vehicle collisions.
- Depends on: M1, physics.
- Confidence: medium (cross-area with movement)

### CMB-20: Stealth/detection and invisibility semantics
- Priority: P2
- Native evidence: BuffDesc columns `stealth`, `detect_stealth`, `anti_stealth`, `evade_telescope`; WZUnitInvisible `39363660`; strings "NEED_STEALTH", "STANCE_STEALTH", "URK_STEALTH", `_cacheDatas.antiStealth`; NPC perception uses it.
- Repo state: absent (only AI script constants).
- Work: track unit stealth level/detect level from buffs and apply in NPC target eligibility and `IsValidNpcTarget` (`39352A50`).
- Depends on: CMB-08, CMB-12.
- Confidence: low

### CMB-21: Quest-driven and special combat packets
- Priority: P2
- Native evidence: WZAttackOnQuest `3936E3F0`, FollowUnitOnQuest `3936E4C0`, FollowPathOnQuest `3936E550`, RunCommandSetOnQuest `3936E610`, WZAttackFaction (0x89) `39384D80`/`393914B0` registrars, WZZombieCharacter `39364380`, WZFakeDeath (covered), ZWKillNpc `3933EA90`.
- Repo state: opcodes declared (`WzOpcodes.cs` 0x50, 0x89); no handlers.
- Work: decode and implement NPC quest attack/follow commands, faction attack, zombie flag.
- Depends on: CMB-02, CMB-11.
- Confidence: low

### CMB-22: Initial/system buffs applied by the zone (npc/slave initial buffs, safe-area, zone buffs)
- Priority: P2
- Native evidence: loaders for `npc_initial_buffs`, `slave_initial_buffs`, `game_safe_area_buffs`, `doodad_func_zone_buffs`, `doodad_func_buffs`, `combat_buffs`, `character_idle_buffs` in the shared content loader; ZWCreateBuff senders `39346050`/`39347060`; `ZWEnterArea`/`LeaveArea` (`3932B1B0`).
- Repo state: absent (`ZoneNpcCrowdBuffState` is the only zone-originated buff sender).
- Work: confirm which of these the zone actually triggers via the ZWCreateBuff callers `39346050`/`39347060` and implement them behind the existing `BuffCreationRequest` frames.
- Depends on: CMB-03.
- Confidence: low

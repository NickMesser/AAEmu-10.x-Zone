<!-- Audit of npc-ai: native x2game-dev_dedicate.dll (ArcheAge 10.0.2.13 r575) vs this repo at 76e439f. Paths rewritten to the src/ layout; line numbers refer to 76e439f. -->

## NPC AI, behaviors, spawners, aggro (npc-ai)

Native architecture finding: x2game has NO C++ per-kind AI class hierarchy (no RTTI like *Behavior*/*Patrol*/*Roaming*). AI "kinds" are data and Lua: content tables `ai_files` (id, name, param_template), `npc_ai_params`, `npc_ai_client_params`, `ai_events`, `ai_commands`/command sets, `npc_strafe_params`, `npc_aggro_links`, `npc_chat_bubbles` (SQL strings at 0x3A100350-0x3A101510, 0x3A1508B0, 0x3A150F80), driving Lua `AICharacter`/`AIBehaviour` tables (strings 0x39F8B740/0x39F8B7E0; `AIBehaviour.INTERNAL`, `AnyBehavior`, `GO_TO_SPAWN` transition) that run on CryAISystem goal pipes (cryaisystem_dedicate.dll + cryaction_dedicate.dll CAIHandler). The C++ in x2game supplies: Npc (RTTI `X2::GameServer::Npc`, ctor 0x39345000), NpcManager, NpcSpawner/NpcGroup/NpcGroupSupporter, NpcAggroTable, ServerAIUnitSystem (skill pick/use, attention target, abusers, signal events, `RTTI ServerAIUnitSystem@GameServer@X2`), and ~40 Lua ScriptBindUnit functions (NpcAddAggro, NpcFollowPath, NpcGotoAnchor, StartAiSkillLists, ...). There is no list of named AI kinds ("hold_position", "wild_boar", etc. are not strings in x2game); the kind is whatever Lua file `ai_files` names.

Repo coverage is strong for leaf pieces (aggro table, spawners/groups/events, signals/talks/timers/posture, CAIHandler behavior dispatch, goal-pipe catalog plus 13 of ~43 goal ops, track-pattern catalog, Lua constants): roughly 55-65% of the C++-side NPC logic by function count. The big hole is glue: `ZoneNativeAiProvider` is dependency-gated and throws `NotSupportedException` on every operation (CAISystem instance unrecovered), the managed provider is unwritten (TODO(managed-ai)), ~25 goal-pipe operations throw NotSupported, there is no NPC skill selection/combat loop, no ScriptBindUnit NPC Lua API, no path-finder, and the scene NPC world (`ZoneSceneNpcWorld`) has many throwing members. Note: `src/AAEmu.Zone/Content/ZoneReinforcementContent.cs` / `ZoneReinforcementCalculator` are EQUIPMENT reinforcement (stat visitor), not NPC reinforcement, so they are out of scope here. Delegation: the repo's only native-AI path is ZoneNativeAiProvider (calls into cryaisystem/cryaction); everything else (aggro, spawner, signals, behavior dispatch, goal executor) is managed reimplementation of x2game/CryAction logic.

### Coverage table

| Native item | Native evidence | Repo status |
|---|---|---|
| NpcAggroTable (Add/Update/Taunt/Remove/Return/Cancel/Abusers, capacity 100) | 0x39321340 AddAggro, 0x3931FD30 UpdateAggro, 0x39320FE0 TauntedBy, 0x393211E0 ProcessReturn, 0x3931E2F0 CollectSharedAggroMembers; cvars npc_aggro_cancel_timeout / npc_aggro_cancel_position_method | Present: src/AAEmu.Zone/Combat/ZoneAggroTable.cs, ZoneAggroEntry.cs, ZoneAggroOrdering.cs, IZoneAggroWorld.cs; world impl ZoneNpcSimulation.cs:522+. Linked aggro (39321C80) / acquisition (39345770) throw in ZoneSceneProviders.cs:464-465,496-497 |
| ServerAIUnitSystem (skill pick/use, attention target, abusers, signal, roaming area, strafe desc) | 0x393289C0 PickSkill (+PickAiSkill, PickFlySkill), 0x39326730 UseSkill, 0x39326980 CanUseSkill, 0x39325ED0 SetAttentionTarget, 0x39327360 RegisterAbuser, 0x39326B40 ProcessSignalEvent, 0x39327220 GetRoamingAreaName, 0x39327260 GetNpcStrafeParamDesc | Mostly absent (abusers live inside ZoneAggroTable; signals via ZoneNpcAiSignals) |
| Npc object: ctor, UpdatePosture, CheckPos, ChangeNewAnchor, GotoAnchor, _FollowPath, _FollowUnit, FindNewTargetIfItNeed, OnAffectedByDamage, ActivateAITimeEvent, RunAICommandSet | 0x39345000, 0x390DE390, 0x3933CDF0, 0x39341E00, 0x3933CA70, 0x3933F880, 0x390DFC50, 0x3933C8D0, 0x390DDCF0, 0x3933D790 | Partial: ZoneNpc.cs, ZoneNpcPostureState.cs, ZoneNpcMovementGate.cs, src/AAEmu.Zone/Scripting/ZoneNpcSpawnCommands.cs (command types 2 and 5 only), ZoneNpcActivationState.cs. Anchor/follow motion, CheckPos, FindNewTarget, damage reaction absent |
| NpcSpawner (point/area/group, queue, validate, events, game schedule, tower def) | 0x3934A410 InitFromSpawnerParam, 0x393438D0 QueueSpawn, 0x39347F90 HandleSpawn, 0x39344530 SpawnNpcGroup, 0x39349800 OnEvent, 0x39349F80 Update, 0x39349B90 CmdNpcSpawnerEvent, 0x3934AD80 LoadSpawners, 0x3933C590 IsSpawningTime, 0x393353B0 GameSchedule activate | Present: ZoneNpcSpawnerRegistry/Factory/State/Events/Ordering, Point/Area/GroupSpawner, SpawnPlacement/Collision/Proximity, Content/ZoneNpcSpawner*Content.cs, ZoneGameScheduleController.cs. Persistent/instance-saved spawners not found; area-spawner separation refuses without per-NPC Lua tables (ZoneNpcAreaSpawner.cs:38-41) |
| NpcGroup / NpcGroupSupporter (respawn lost members, group id) | 0x39343310 HandleGroupMemberRemoved, RespawnLostGroupMembers, NpcGroupIdGenerator::AllocId; formation 3934CA40/3934BFB0 | Present: ZoneNpcGroups.cs, ZoneNpcGroupFormation.cs (formation needs live scene owner) |
| NpcManager::Create / HandleNpcControl / HandleNpcSpawnFailed | 0x390E0270 Create, 0x39342750 HandleNpcControl | ZoneNpcManager.cs exists; HandleNpcControl (client-driven NPC) absent |
| CAIHandler Lua behavior dispatch | strings 0x39F8B778, 0x39F8B818, 0x39F8BB50, 0x39F8BBC0; CryAction 30511450, 305108E0 | Present: src/AAEmu.Zone/Scripting/ZoneNpcBehavior.cs, ZoneNpcLuaRuntime.cs, IZoneNpcAiRuntime.cs; no end-to-end run (no live AI actor) |
| Goal pipes (CryAI) | 31156314 / 310F7604, GoalParameters 310F7E58 | Catalog+controller present (ZoneGoalPipeCatalog/Controller/Executor, src/AAEmu.Zone/Scripting/ZoneGoalPipeBinding.cs). Implemented: Continuous, LookAt, Locate, AcquireTarget, Strafe, BodyPosition, Run, IgnoreAll, Timeout, Wait, Signal, WaitSignal, Devalue (+Branch/Random/Clear mapped to a Devalue stub in ZoneGoalOperationFactory.cs). Not implemented: LookAround, Approach, Follow, FollowPath, BackOff, PathFind, Trace, Forget, Hide, Form, Stick, Move, Charge, Animation, AnimationTarget, UseCover, AdjustAim, Proximity, MoveTowards, Dodge, UseSkill, FlyUseSkill, Bind, BindedUseSkill, Pipe |
| Signals / talks / timers / chat bubbles / ai_events | CAISystem send 390B30D0, 3100756C; tables ai_events, npc_chat_bubbles | Present: ZoneNpcAiSignals.cs, ZoneNpcSignals.cs, ZoneNpcTalks.cs, ZoneNpcTimers.cs, src/AAEmu.Zone/Content/ZoneNpcSignalContent.cs |
| Native-AI delegation | cryaisystem_dedicate.dll, AI init 31049268, construct chain 39345000/39348440 | ZoneNativeAiProvider.cs: dependencies Construct, Signal, Activation, Target, Update all unresolved -> NotSupportedException (~lines 354-409); CAISystem instance slot unrecovered |
| Managed AI provider | - | Absent: Contracts/IZoneAiProvider.cs:18, ZoneSceneBackendSelection.cs:25-35 TODO(managed-ai) |
| ScriptBindUnit NPC Lua API (~40 fns) | 0x39353F90 NpcAddAggro, 0x39353DD0 NpcActivateAggroUpdate, strings 0x39FE41A8-0x39FE4488 | Absent (no hits for NpcAddAggro/NpcRemoveAfter/StartAiSkillLists). Only AI constants (ZoneAiScriptConstants.cs), log bindings, combat-class binding, track-pattern binding, goal-pipe binding |
| Track patterns | Lua BeginTrackPattern, 3107F0A8 | Catalog only (ZoneTrackPatternCatalog.cs, ZoneTrackPatternBinding.cs); execution absent |
| Path types / path-finder / AI nav | AI virtual 1C8 / 31058598 FindPathType; AIPointPrefabObject/AINavModifierPrefabObject RTTI | Absent: ZoneNpcInitialSpawnBehavior.cs:65 TODO(path-finder) counts refusals |

### Tasks

### AI-01: Recover the CAISystem instance and unblock native AI construction
- Priority: P1
- Native evidence: AI init 31049268 via PostInit 306F77A2; Npc ctor 0x39345000, actor init 39348440, stance select 31050598; CAISystem send 390B30D0; actor admission 3100756C; IAIObject::Event 6/7.
- Repo state: src/AAEmu.Zone/Native/Ai/ZoneNativeAiProvider.cs ~354-409: all five dependency entries (Construct, Signal, Activation, Target, Update) unresolved; every operation throws NotSupportedException. Module attribution of 39xxxxxx vs 31xxxxxx addresses still open.
- Work:
  - Locate the CAISystem pointer slot beside the gEnv+8 game-service slot.
  - Recover the ABI for the 39345000/39348440 chain and wire CreateNpc/RemoveNpc.
  - Wire signal dispatch, activation events 6/7, attention-target write and per-frame update via IZoneAiFramePump.
  - Flip each dependency IsResolved only when a real call is wired.
- Status: **Partial** — CAISystem instance recovered: gEnv+0x80 (CSystem+0xD0), read and proven at runtime by `ZoneNativeEngine.TryReadAiSystem` (src/AAEmu.Zone/Native/Engine/ZoneNativeEngine.AiSystem.cs): its vtable lies in cryaisystem_dedicate.dll (0x31000000, size 0x220000) and vtable slot 3 is 0x31049268, the AI init PostInit runs; the provider's "CAISystem instance" dependency is now resolved. Construction is now blocked at "native NPC AI actor construction" (callable ABI of 39345000/39348440 and 39xxxxxx module attribution), the next open item above.
- Depends on: framework startup (F1), scene host, AI-06
- Confidence: medium

### AI-02: Decide and build the managed AI provider (TODO(managed-ai))
- Priority: P1
- Native evidence: AI is Lua + CryAISystem goal pipes: AIBehaviour/AICharacter 0x39F8B740/0x39F8B7E0; ai_files/npc_ai_params SQL 0x3A100350/0x3A100430.
- Repo state: absent; ZoneAiBackendKind.Managed exists as enum only (Contracts/ZoneSceneBackendSelection.cs:25-35); Contracts/IZoneAiProvider.cs:18.
- Work:
  - Implement IZoneAiProvider using ZoneNpcActorRegistry.Adopt/Remove, ZoneNpcAiSignals, ZoneGoalPipeController, ZoneNpcBehavior; honor the ZoneNativeAiProvider.IsBound exclusivity gate.
  - Load AI Lua scripts named by ai_files and build per-NPC entity/AI/behavior tables.
  - Implement the AI update tick (CAIHandler update, behavior transitions, alertness).
- Depends on: AI-03, AI-04, AI-06
- Confidence: medium

### AI-03: Implement the ~25 unimplemented goal-pipe operations
- Priority: P1
- Native evidence: goal enum names mirror native (ZoneGoalPipeCatalog.cs:7-14); native pipes 31156314 / 310F7604; GoalParameters 310F7E58.
- Repo state: ZoneGoalOperationFactory.cs final arm throws NotSupportedException for LookAround, Approach, Follow, FollowPath, BackOff, PathFind, Trace, Forget, Hide, Form, Stick, Move, Charge, Animation, AnimationTarget, UseCover, AdjustAim, Proximity, MoveTowards, Dodge, UseSkill, FlyUseSkill, Bind, BindedUseSkill, Pipe. Branch/Random/Clear map to a no-op ZoneDevalueGoal (wrong semantics).
- Work:
  - First wave: Approach, Follow, FollowPath, MoveTowards, BackOff, Move, UseSkill, Pipe, real Branch/Random, Stick, Animation, Proximity.
  - Recover per-goal logic from cryaisystem_dedicate.dll and its movement-request outputs.
  - Add actor-service interfaces and tests per goal.
- Depends on: AI-05, AI-07
- Confidence: medium (op names known; per-goal addresses not catalogued)

### AI-04: Add the ScriptBindUnit NPC Lua API
- Priority: P1
- Native evidence: 0x39353F90 NpcAddAggro, 0x39353DD0 NpcActivateAggroUpdate; strings 0x39FE41A8-0x39FE4488: NpcSetCheckPosContext, NpcStopAutoAttack, NpcAddAlertSafeTarget, NpcRemoveAggroOutOfRange, NpcActivateAITimeEvent, NpcSetMinAps, NpcRemoveAfter, NpcSetInteractable, NpcTeleportTo, NpcGotoAnchor, NpcFollowPath, NpcFollowUnit, NpcOnEnded*, NpcResetUseSkillGoal, NpcSetSkillList, StartAiSkillLists/EndAiSkillLists, ClearCombat/InitCombat, IsInValidSkillRange, StartSkill, CanUseSkill, CreateBuff/RemoveBuff, SetStance, ChangePose, SetDefaultPosture, GetTargetId/GetTargetDistance, IsValidNpcTarget.
- Repo state: absent (Scripting/ has only constants, log bindings, combat-class, goal-pipe, track-pattern bindings).
- Work:
  - Enumerate the full ScriptBindUnit registration table near 0x39353xxx with arg signatures.
  - Bind each onto ZoneNpc / ZoneAggroTable / ZoneNpcActivationState / skill services.
  - Fire NpcOnEndedGotoAnchor/GoToPath/FollowPath callbacks.
- Depends on: AI-02, AI-05, AI-07
- Confidence: high (names), medium (arg details)

### AI-05: NPC locomotion, anchor/leash return, path-following and path-finder
- Priority: P1
- Native evidence: Npc::_FollowPath 0x3933CA70, _FollowUnit 0x3933F880, GotoAnchor 0x39341E00, ChangeNewAnchor 0x3933CDF0, CheckPos; NpcAggroTable::ProcessReturn 0x393211E0; FindPathType AI virtual 1C8 (31058598); AIPoint/AINavModifier prefabs; ai_path_name / ai_path_type_id in the npc spawner SQL (0x3A1508B0); cvar npc_aggro_cancel_position_method.
- Repo state: ZoneNpcMovementGate.cs (30 lines) only; ZoneNpcInitialSpawnBehavior.cs:65 TODO(path-finder) counts refusals; ZoneSceneProviders.cs:316 TODO(npc-relocate) throws; ZoneAggroTable computes return distances but nothing moves the NPC; ZoneNpcSpawnCommands.cs only invokes Lua RunCommandSet.
- Work:
  - Implement path-type resolution and path data (zone AI nav/path graphs).
  - Implement NPC locomotion producing move packets.
  - Implement anchor relocation, GotoAnchor, FollowPath, FollowUnit, GO_TO_SPAWN return and leash/evade behavior.
  - Replace ZoneSceneNpcWorld relocate throw.
- Depends on: terrain/physics (S1), navigation-graphs area
- Confidence: medium

### AI-06: Provide a concrete live IZoneNpcWorld / actor runtime for the scene
- Priority: P1
- Native evidence: NpcManager::Create 0x390E0270, Npc ctor 0x39345000, LoadSpawners 0x3934AD80 (reads zone_server/npc_spawners.g).
- Repo state: ZoneSceneNpcWorld (src/AAEmu.Zone/Scene/ZoneSceneProviders.cs:139+) throws NotSupportedException with TODOs: native-ai (:496), npc-linked-aggro (:465), npc-relocate (:316), housing check (:324-336), creator-hostility (:227), sleep state (:356), adjust-radius (:1072), forbidden-template-list (:1062), buff-landing-physics (:767), scale-transition (:719). Area-spawner separation refuses because per-NPC Lua tables are not published (ZoneNpcAreaSpawner.cs:38-41), producing ~1,800 refusals/min before backoff. ZoneNpcInitialSpawnBehavior notes that an unhandled refusal previously led to "Zone TCP disconnect" 40 s after ZWZoneLoaded.
- Work:
  - Replace each throw with a real owner or an explicit, documented skip.
  - Publish per-NPC Lua tables from ZoneNpcLuaRuntime so spawn separation can be evaluated.
  - Decide whether to disable NPC spawning for the first-entry milestone without dropping the Zone TCP link (P0 if spawning is enabled in the launch mode).
- Depends on: AI-01/02, AI-05
- Confidence: high

### AI-07: NPC skill selection and combat loop
- Priority: P1
- Native evidence: ServerAIUnitSystem::PickSkill 0x393289C0, PickAiSkill, PickFlySkill, UseSkill 0x39326730, CanUseSkill 0x39326980, StopSkill, StopCasting, GetBaseSkillInfoResult; Lua StartAiSkillLists/EndAiSkillLists/NpcSetSkillList/NpcResetUseSkillGoal; Npc::OnAffectedByDamage 0x3933C8D0; CmdNpcRunAiCommandSet.
- Repo state: absent (ZoneAiCombatClasses.cs and src/AAEmu.Zone/Scripting/ZoneAiCombatClassBinding.cs only classify combat classes; grep PickSkill/StartAiSkillLists finds nothing).
- Work:
  - Implement skill-list/phase model and PickAiSkill selection (range, cooldown, conditions).
  - Wire UseSkill/CanUseSkill/StopSkill/AddBuff/DelBuff into the combat/skills area.
  - Implement damage reaction feeding aggro and hit events.
- Depends on: combat/skills area, AI-04
- Confidence: medium

### AI-08: Aggro gaps: acquisition, linked/shared aggro, sleep, cancel timers, retarget
- Priority: P1
- Native evidence: 0x39321C80 linked aggro, 0x39345770 acquisition, CollectSharedAggroMembers 0x3931E2F0, GetGroupAveragePosition, CancelTauntedBy, ProcessAggroCancel, npc_aggro_links (0x3A104340), cvars npc_aggro_cancel_timeout / npc_aggro_cancel_position_method, NpcAddAlertSafeTarget, FindNewTargetIfItNeed 0x390DFC50.
- Repo state: ZoneAggroTable present; ZoneSceneProviders.cs:464-465 and 496-497 throw for linked aggro and acquisition; npc_aggro_links read (ZoneNpcContent.cs:119) but not used for resolution; no sight/alert-range sensing.
- Work:
  - Implement aggro acquisition via sight/alert range against nearby units.
  - Implement linked and group-shared aggro (aggro_link_help_dist, sight check).
  - Implement cancel timers and FindNewTarget.
- Depends on: AI-01/02, spatial query service
- Confidence: medium

### AI-09: Spawner leftovers: persistent/instance-saved spawners, population cap, TowerDef
- Priority: P2
- Native evidence: NpcSpawner::Save, CanSaveInstanceSpawner, "persistent spawner list", "Failed to spawn npc because totalSpawnerCount bigger than maxPopluation", SpawnForDev, GameSchedule activation 0x393353B0, TowerDef::OnQueriedPlayability 0x39FE47D8 string, cvars npc_spawn_mass_on_all_spawners / npc_spawner_event.
- Repo state: spawn/events/group/game-schedule logic present (ZoneNpcSpawnerEvents.cs, ZoneGameScheduleController.cs; TowerDef referenced in spawner events/factory); persistent save, max-population enforcement and a TowerDef controller not found.
- Work:
  - Verify and add max-population enforcement.
  - Add persistent spawner save/restore if instance zones are in scope.
  - Add TowerDef step/state controller if needed.
- Depends on: persistence area
- Confidence: low-medium

### AI-10: Track patterns, strafe params, roaming areas and ai_commands
- Priority: P2
- Native evidence: ServerAIUnitSystem::GetNpcStrafeParamDesc 0x39327260, GetRoamingAreaName 0x39327220, npc_strafe_params SQL (0x3A1045B0), ai_commands SQL (0x3A150F80, LoadAiCommandSetDescs), RunAICommandSet 0x3933D790, BeginTrackPattern 3107F0A8.
- Repo state: ZoneTrackPatternCatalog is descriptor-only; ZoneNpcSpawnCommands implements command types 2 and 5 only; npc_strafe_params / ai_commands tables not read by Content; roaming areas only in src/AAEmu.Zone/Content/ZoneNavigationLegacyAreaContent.cs and ZoneNpcSpawnerPlacementContent.cs.
- Work:
  - Load npc_strafe_params and ai_commands content.
  - Implement remaining command types and CmdNpcRunAiCommandSet.
  - Implement actor-side track-pattern execution.
- Depends on: AI-05
- Confidence: medium

### AI-11: Client-driven NPC control and npc_ai_client_params
- Priority: P2
- Native evidence: NpcManager::HandleNpcControl 0x39342750; X2::GameClient::ClientDrivenNpc::_OnAggroTableChanged; npc_ai_client_params SQL 0x3A100480.
- Repo state: absent (only npc_ai_params read, ZoneNpcContent.cs:125).
- Work: read npc_ai_client_params; handle NPC control packets for client-driven NPCs.
- Depends on: movement protocol area
- Confidence: low

### AI-12: AI debug/diagnostics parity
- Priority: P2
- Native evidence: cvars npc_aggro, npc_aggro_meter, npc_debug_spawner; AiDebugSession::ReceivePacket string 0x3A00A950; "NPC Aggro Table" dump.
- Repo state: ZoneNpcDiagnostics.cs (62 lines); debug drawing has no owner (ZoneSceneProviders.cs:1068).
- Work: optional aggro-table dump and AiDebugSession.
- Depends on: none
- Confidence: medium

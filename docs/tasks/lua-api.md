<!-- Audit of lua-api: native x2game-dev_dedicate.dll (ArcheAge 10.0.2.13 r575) vs this repo at 76e439f. Paths rewritten to the src/ layout; line numbers refer to 76e439f. -->

## Lua script bindings (area: lua-api, prefix LUA)

**Method note.** Native Lua binding tables are NOT all in `x2game-dev_dedicate.c`. The server-side tables are split across modules: `ScriptBindUnit` (X2::GameServer) is in x2game (registrar `sub_39355610`, constructed in server init `sub_39333490`, x2game.c:678621); `CScriptBind_AI` and `CAIHandler` (behaviour state machine) are in `cryaction_dedicate.dll` (the repo cites them as 3050xxxx-3054xxxx, 3202xxxx, 3550xxxx; not in the Hex-Rays export, so names below were extracted from the DLL's .rdata name cluster at file offsets 0x226bf0-0x22ffc0); `Script/Physics/Particle/Sound/System` globals are in `cryscriptsystem.dll`; entity-script callbacks (OnInit, OnSpawn, ...) are in `cryentitysystem_dedicate.dll`. Names are exact; argument signatures were not decompiled except where the repo already cites an address.

**Coverage summary.** The repo hosts its own managed Lua 5.1 (double+int64) state (`ZoneLuaRuntime`) and re-implements a thin slice of the AI script surface: `Script.LoadScript/ReloadScript/UnloadScript`, 14 of 170 `AI.*` functions (goal pipe, track pattern, log, AddCombatClass), 4 entity-table pipe methods (SelectPipe/InsertSubpipe/CancelSubpipe/IsUsingPipe), all 488 AI constant globals (`ZoneAiScriptConstants`, 100% of the native constant cluster), and a faithful CAIHandler port (`ZoneNpcBehavior`: Constructor/Destructor/AnyBehavior/AVAILABLE/INTERNAL/Base/EventToCall/PREVIOUS/FIRST). Missing entirely: the 64-function `ScriptBindUnit` table plus its RACE_*/GENDER_* globals, ~156 other `AI.*` functions, the Entity-table methods, `Script.SetTimer/KillTimer/...`, `System.*` (~150), `Physics.*`, `Particle.*`, `Sound.*`, and most server-to-AI signal names. Crucially, nothing wires the existing bindings into the live scene: `src/AAEmu.Zone/Scene/ZoneSceneLoadingServices.cs:121-126` only does `new ZoneLuaRuntime`, `InstallBindings()` (Script table only) and `ZoneAiLookupTable.Load`; `ZoneGoalPipeBinding`, `ZoneEntityGoalPipeBinding`, `ZoneTrackPatternBinding`, `ZoneAiLogBindings`, `ZoneAiCombatClassBinding`, `ZoneAiScriptConstants.Install` and `ZoneNpcBehavior` are never instantiated outside Scripting/tests. Rough Lua-surface parity: ~10% by function count, ~35% of NPC-AI-critical semantics, but unreachable at runtime.

### Coverage table

| Native table / group | Native evidence | Native count | Repo status |
|---|---|---|---|
| `Script` (cryscriptsystem) | names: ReloadScripts, ReloadScript, ReloadEntityScript, LoadScript, UnloadScript, DumpLoadedScripts, Debug, DebugFull, SetTimer, SetTimerForFunction, KillTimer | 11 | 3/11 `src/AAEmu.Zone/Scripting/ZoneScriptLoader.cs:57-79` (mirrors 3550F140/3550EE70/3552DE20). Absent: ReloadScripts, ReloadEntityScript, DumpLoadedScripts, Debug, DebugFull, SetTimer, SetTimerForFunction, KillTimer |
| `AI` goal-pipe/track/log/combat-class (cryaction) | 30522780/30522810, 3053A790, 305291C0/30529720/30544BF0, 30521480/305211E0, 305417D0 | 14 | 14/14 `ZoneGoalPipeBinding.cs`, `ZoneTrackPatternBinding.cs`, `ZoneAiLogBindings.cs`, `ZoneAiCombatClassBinding.cs` (not wired to scene) |
| `AI` other | cryaction_dedicate.dll name cluster (LUA-03) | 156 | 0/156 |
| Entity-table pipe methods | 3202E630, 32032590, 32032950, 32032D20, 32032E70 | 4 (+ rest of Entity bind) | 4/4 `ZoneEntityGoalPipeBinding.cs` (not wired) |
| AI/behaviour constants (globals) | cryaction cluster: AICPC_*, AGGRO_*, PHASE_TYPE_*, USE_*, USF_*, SUP_*, AITARGETREASON_*, AICC_*, AISS_*, PT_*, NPC_*_BUFF_TYPE, AI_JUMP_*, AIGOALPIPE_*, AI_MOVE_*, AIPARAM_*, IF_*, HM_*, AIANCHOR_*, SIGNALFILTER_*, AIEVENT_*, AIOBJECT_*, AITRACKPAT_*, NAV_*, GROUP_* | 488 | 488/488 `ZoneAiScriptConstants.cs` (Install never called) |
| `ScriptBindUnit` (x2game `sub_39355610`) | 64 methods + RACE_NUIAN/FAIRY/DWARF/ELF/HARIHARAN/FERRE/RETURNED/WARBORN/NONE, GENDER_MALE/FEMALE/NONE | 64 + 12 consts | 0/64, 0/12 (grep NpcAddAggro, GetTargetDistance, RACE_ELF over AAEmu.Zone: no hits) |
| CAIHandler behaviour machine | cryaction strings AIBehaviour, AICharacter, DEFAULT, AVAILABLE, INTERNAL, AnyBehavior, NoBehaviorFound, Constructor, Destructor, Base, EventToCall, StartupBehaviour, OnLoadAI, OnSaveAI, `scripts/ai/aiconfig.lua`; 3050D8F0/3050EA90/305108E0/30511450 | n/a | Mostly `ZoneNpcBehavior.cs`. Absent: aiconfig.lua, OnLoadAI/OnSaveAI, StartupBehaviour, character-script loader, GO_TO_* fallbacks |
| Entity-script callbacks (cryentitysystem_dedicate) | OnInit, OnSpawn, OnShutDown, OnDestroy, OnUpdate, OnTimer, OnEvent, OnReset, OnDamage, OnCollision, OnMove, OnBind, OnUnBind, OnEnter/LeaveArea, OnEnter/LeaveNearArea, OnMoveNearArea, OnBeginState, OnEndState, OnStartGame, OnStartLevel, OnSoundDone, OnPhysicsBreak, OnAnimationEvent, OnStartAnimation, OnEndAnimation, OnProceedFadeArea | ~27 | 0 (no entity-script host) |
| `Physics`, `Particle`, `Sound`, `System` (cryscriptsystem) | Physics: SimulateExplosion, RegisterExplosionShape, RegisterExplosionCrack, RayWorldIntersection, RayTraceCheck, SamplePhysEnvironment (6). Particle: CreateEffect, DeleteEffect, IsEffectAvailable, SpawnEffect, SpawnEffectLine, SpawnParticles, CreateDecal, CreateMatDecal, Attach, Detach (10). Sound ~60. System ~150 (GetEntity, GetEntityByName, GetEntitiesInSphere, SpawnEntity, RemoveEntity, GetCVar, SetCVar, AddCCommand, ExecuteCommand, Log, Warning, GetFrameTime, GetCurrTime, GetTerrainElevation ...) | ~226 | 0 |
| ~90 client UI `ScriptBind*Addon`/widget tables | strings.txt, RTTI `.?AVScriptBind*Addon` | n/a | Not applicable to Zone (client UI); exclude |

### Native to Lua call-in points
Server NPC AI signals go through `sub_3933B970` (AI-object signal; `X2::SendSignalToNpcAi` wrapper `sub_390B30D0` is used for OnTalk/OnTalkEnd) into the entity's AIBehaviour table. In x2game-dev_dedicate.c: `OnNoAggroTarget` (685935), `OnAggroTargetChanged` (685955), `OnAITimeEvent` (686130), `OnPostureChanged` (686190), `OnLostFollowUnit` (686420), `OnAlertTargetChanged` (694612), `OnCommandCompleted` (688420, via sub_3933C0C0), `OnTalk` (689930), `OnTalkEnd` (692810, 692881, 693117, 693295, 694034); state signals `GO_TO_FOLLOW_PATH` (686892), `GO_TO_GOTO_ANCHOR` (687111), `GOTO_IDLE` (687123), `GO_TO_IDLE` (689424), `GO_TO_FOLLOW_UNIT` (689500), `GO_TO_DEAD` (694356), `GO_TO_DESPAWN` (720757); `GO_TO_SPAWN` in cryaction; plus `ACTION_DONE`. Other direct calls: `AI:SetParamX2()` (161690; error strings 0x39F8CA88/0x39F8CAC0), entity `AI.commandSet.RunCommandSet{commands,commanderUnit,runType,canInteract}` (strings 0x39FE0430-0x39FE0478; repo has the table builder in ZoneNpcSpawnCommands.cs), `AI.talkPos` (repo has it), `OnUnitResurrect` (714658), `followPathName/followPathType/followPathSpeed` (strings 0x39FE00A8-0x39FE00E0). Bootstrap: `scripts/main.lua` + `OnInit` (306F7630, cited ZoneNativeEngine.GameFramework.cs:106 as not executed), `scripts/ai/aiconfig.lua`, `Scripts/Entities/Actor/%s.lua`, `Scripts/Entities/Default/GeomEntity.lua`, `scripts/common.lua`.

Repo invokes: Constructor/Destructor/AnyBehavior and behavior-signal dispatch (`ZoneNpcBehavior`), `OnTalk`/`OnTalkEnd` (`ZoneNpcTalks.cs:34,75`), `OnAITimeEvent` (`ZoneNpcSimulation.cs:367`), `OnPostureChanged` (`ZoneNpcPostureState.cs`). Not invoked anywhere (grep absent): OnNoAggroTarget, OnAggroTargetChanged, OnAlertTargetChanged, OnLostFollowUnit, OnCommandCompleted, GO_TO_* / GOTO_IDLE, OnUnitResurrect, SetParamX2, all entity-system callbacks.

### Tasks

### LUA-01: Wire the existing AI Lua bindings into the scene bootstrap
- Priority: P0 (without it `AI.CreateGoalPipe` is nil and every AI script load fails)
- Native evidence: cryaction `CScriptBind_AI` registration (Begin/Create 30522780/30522810, PushGoal 3053A790, track pattern 305291C0/30529720/30544BF0, log 30521480/305211E0, AddCombatClass 305417D0); `scripts/main.lua` bootstrap (306F7630) and `scripts/ai/aiconfig.lua` string in cryaction_dedicate.dll.
- Repo state: bindings exist (`Scripting/Zone*Binding.cs`, `ZoneAiScriptConstants.Install`) but `src/AAEmu.Zone/Scene/ZoneSceneLoadingServices.cs:121-131` only creates the runtime, `InstallBindings()` (Script table) and `ZoneAiLookupTable.Load`. No production call site builds the goal-pipe/track/log/combat-class/entity-pipe bindings or `ZoneAiScriptConstants.Install`.
- Work:
  - Create the global `AI` table before scripts run; call `ZoneAiScriptConstants.Install`.
  - Construct the bindings against it with a real `ZoneGoalPipeCatalog`; install entity-pipe methods on each NPC entity table at spawn.
  - Execute `scripts/main.lua` (+ `OnInit`) and `scripts/ai/aiconfig.lua` in native order, ignoring individual results as 306F7630 does.
- Depends on: LUA-02, LUA-03, NPC spawn (entity table creation)
- Confidence: high

### LUA-02: Implement the `ScriptBindUnit` Lua table (64 methods plus RACE_/GENDER_ globals)
- Priority: P0 for the AI-core subset (GetId, GetName, GetLevel, GetHealth, GetMaxHealth, IsDead, GetTargetId, GetTargetDistance, IsValidNpcTarget, IsFriendly, CanUseSkill, StartSkill, IsInValidSkillRange, InitCombat, ClearCombat, NpcAddAggro, NpcFollowPath/NpcGotoAnchor and their OnEnded callbacks, StartAiSkillLists, NpcSetSkillList, Say, CanMove); the rest P1
- Native evidence: registrar `sub_39355610` (x2game.c:707728) with handlers: GetId 393517C0, GetName 39351830, GetOwnerType 393518E0, GetRace 39351990, GetGender 39351A40, GetLevel 39351AF0, GetHealth 39351BA0, GetMaxHealth 39351CD0, IsDead 39351DD0, GetMana 39351E80, GetMaxMana 39351F90, GetHealthPercentage 39352090, CanUseSkill 393521A0, StartSkill 39352450, IsCasting 39352590, IsChanneling 39352640, StopCasting 39351050, IsControlledByPsychokinesis 393526F0, HaveSkillController 393527C0, CanMove 39352870, GetTargetDistance 39352920, IsValidNpcTarget 39352A50, GetTargetId 39352B90, NpcSetSkillList 39354860, NpcResetUseSkillGoal 393510C0, InitCombat 39351160, ClearCombat 393534D0, ClearSkillController 39351210, Say 39352C40, IsFriendly 39352DF0, StartAiSkillLists 39354BB0, EndAiSkillLists 393512E0, NpcFollowUnit 393536C0, NpcFollowPath 393537D0, NpcOnEndedFollowPath 393538B0, NpcOnEndedGoToPath 39353940, NpcGotoAnchor 39353A00, NpcOnEndedGotoAnchor 39353AC0, NpcTeleportTo 39353B80, NpcSetInteractable 39353C20, NpcRemoveAfter 39353CC0, NpcActivateAggroUpdate 39353DD0, NpcSetMinAps 39353E50, NpcActivateAITimeEvent 39353EE0, NpcAddAggro 39353F90, NpcRemoveAggroOutOfRange 39354150, NpcAddAlertSafeTarget 39354490, NpcStopAutoAttack 39354580, NpcSetCheckPosContext 39354640, SetDefaultPosture 39351390, SetDefaultSoState 393546D0, SetSoState 393547B0, CreateBuff 39352F20, RemoveBuff 39353020, IsInValidSkillRange 39353140, ChangePose 39351410, SaveUnitAttr 39351510; eight server-unsupported stubs that only log "we don't support this for server!": SetHighlighted 39350E10, SetInvisible 39350E40, GetNameTagHeight 39350E70, SetNameTagColor 39350EA0, SetNoCollideActor 39350ED0, SetStance 39350F00, AddAnimationState 39350F30, MoveUnitToPos 39350F60. RACE_*/GENDER_* are set in the same function (tail, ~x2game.c:709100+). Error strings 0x39FE31D0-0x39FE3E88 ("script bind unit : GetUnit() failed with id %u", "phase option error(%s)").
- Repo state: absent (grep for NpcAddAggro, GetTargetDistance, StartAiSkillLists, NpcFollowPath, IsFriendly, SetSoState, RACE_ELF in AAEmu.Zone: 0 hits). Backing services exist in Simulation (aggro, NPC actors, follow-path via ZoneNpcSpawnCommands).
- Work:
  - Add a `ZoneUnitScriptBinding` registering the table with a unit-id first argument and native "can't find unit" nil/0 returns and log texts.
  - Decompile each handler body (all follow 0x39350E10 in x2game-dev_dedicate.c) for exact arg/return conventions.
  - Back each with existing sim services (IZoneNpcBehaviorServices etc.); register the 8 stubs with the same log message.
  - Install RACE_*/GENDER_* with the native numeric values read from the registrar tail.
- Depends on: LUA-01; aggro/skill/buff/combat areas
- Confidence: high on names and addresses, medium on signatures

### LUA-03: Implement the remaining ~156 `AI.*` functions (prioritised)
- Priority: P1 (core subset P0: RegisterWithAI, Event, FreeSignal, Set/GetRefPointPosition, Get/Set/ClearAttentionTargetOf, GetAttentionTargetEntity/Position/Distance, GetAIObjectPosition, GetTypeOf/GetSubTypeOf/GetSpeciesOf, GetGroupOf/GetGroupCount/GetGroupMember, SetCharacter, SetSkillUsePattern, IsMoving, GetAIParameter, ChangeParameter, ChangeMovementAbility, SetPathToFollow, Get/SetStance, FindObjectOfType, Hostile; rest P2)
- Native evidence: cryaction_dedicate.dll cluster 0x226bf0-0x22ffc0 (file order is reverse of registration): GetSpecialAIObjectPos, SetSkillUsePattern, DisablePersistentStick, IsMountedWeaponUsableWithTarget, AggressionImpact, AutoDisable, GetAutoDisable, PlayReadabilitySound, GetInterestStatus, SetInterestStatus, DebugReportHitDamage, GetDirLabelToPoint, IsMoving, CheckMeleeDamage, CanMelee, CanMoveStraightToPoint, IsPunchableObjectValid, MeleePunchableObject, SetRefpointToPunchableObject, CanJumpToPoint, Animation, AddObstructSphere, NotifySurpriseEntityAction, GetProbableTargetPosition, GetObjectRadius, FindStandbySpotInSphere/InShape, SetCurrentHideObjectUnreachable, RegisterDamageRegion, SetRefPointAtDefensePos, IsInSight, IsAgentInTargetFOV, SetRefPointToGrenadeAvoidTarget, EnableCoverFire, SetMinFireTime, GetDistanceAlongPath, GetNearestPathOfTypeInRange, GetPathLoop, GetPointOnPathBySegNo, GetPathSegNoOnPath, GetNearestPointOnPath, SetPointListToFollow, GetPredictedPosAlongPath, GetPathFollowLAPos, SetPathAttributeToFollow, SetPathToFollow, GetNavigationType, RequestAttack, GetGroupTargetCount, GetGroupTarget, GetAlertStatus, GetAlertness, SetPFProperties, SetPFBlockerRadius, SetAdjustPath, SetForcedNavigation, GetUnitCount, SetUnitProperties, GetUnitInRank, SetStance, GetStanceSize, GetStance, GetPlayerThreatLevel, Get/SetExtraPriority, UpTargetPriority, GetLeader, SetFormationUpdate(Sight), ScaleFormation, ChangeFormation, GetFormationPointPosition/Class, AddFormationPoint(Fixed), CreateFormation, Event, GetEnclosingSpace, IsPointInWaterRegion, IsPointInFlightRegion, IntersectsForbidden, NotifyReinfDone, Get/NotifyGroupTacticState, GetGroupTacticPoint, EvalPeek, EvalHidespot, ConstrainPointInsideGenericShape, DistanceToGenericShape, IsPointInsideGenericShape, GetEnclosingGenericShapeOfType, GetNearestHidespot, GetDirectAnchorPos, VerifyAlienTarget, GetHunterApproachParams, GetAlienApproachParams, SetCharacter, GetForwardDir, CreateTempGenericShapeBox, Set/GetTerritoryShapeName, Get/SetRefShapeName, IsSimilarRefPointDirection, Get/SetRefPointDirection, Get/SetRefPointPosition, SetRefPointRadius, GetNearestEntitiesOfType, GetTotalLengthOfPath, Get/SetBeaconPosition, GetAIObjectPosition, GetTargetType, GetAttentionTarget{Type,Entity,Distance,ViewDirection,Direction,Position}, ClearAttentionTargetOf, GetAttentionTargetOf, SetAttentionAnchorOf, SetAttentionTargetOf, GetSubTypeOf, GetTypeOf, GetSpeciesOf, GetAnchor, SoundEvent, FindObjectOfType, Hostile, GetGroupAveragePosition, GetGroupOf, GetGroupMember, GetGroupCount, SetSpeciesThreatMultiplier, SetAssesmentMultiplier, SetIgnorant, FreeSignal, GetCurrentGoalOpName, IsGoalPipe, Set/ModifySmartObjectState(s), SmartObjectEvent, GetLastUsedSmartObject, GetLastSmartObjectExitPoint, AbortAction, ExecuteAction, ChangeMovementAbility, IsEnabled, GetAIParameter, ChangeParameter, RegisterWithAI, RecComment, LogComment, LogProgress. Error strings "CScriptBind_AI::..." in the same region (e.g. "[%s] %d arguments passed, 1 expected)").
- Repo state: only PushLabel/PushGoal/BeginGroup/EndGroup/BeginGoalPipe/CreateGoalPipe/EndGoalPipe, AddCombatClass, BeginTrackPattern/AddPatternNode/AddPatternBranch/EndTrackPattern, LogEvent, Warning exist. Backing state in `src/AAEmu.Zone/Ai/ZoneAiObjectRegistry.cs`, `ZoneAiObjectState.cs`, `ZoneAiSignal.cs`, `ZoneAiCombatClasses.cs`.
- Work:
  - Implement the P0 subset against `ZoneAiObjectRegistry` (ref point, attention target, group, species, parameters).
  - Stub the human-FPS leftovers (formation tactics, readability, cover, alien/hunter, grenade) with native-matching nil returns.
  - Route LogComment/LogProgress/RecComment like LogEvent (30521480).
- Depends on: LUA-01; goal-op execution area (goal ops read RefPoint/attention target)
- Confidence: high on names; medium on which are used by ArcheAge scripts (game_pak not readable)

### LUA-04: Dispatch the missing native-to-AI signals from the simulation
- Priority: P0 for OnAggroTargetChanged/OnNoAggroTarget/GO_TO_IDLE/GO_TO_DEAD/OnCommandCompleted; P1 for the rest
- Native evidence: x2game.c lines 685935, 685955, 686190, 686420, 686892, 687111, 687123, 689424, 689500, 694356, 694612, 688420, 720757, 714658, 161690 (see call-in list above); cryaction GO_TO_SPAWN/GO_TO_IDLE fallbacks.
- Repo state: only OnTalk/OnTalkEnd/OnAITimeEvent/OnPostureChanged are sent (`src/AAEmu.Zone/Npcs/ZoneNpcTalks.cs:34,75`, `ZoneNpcSimulation.cs:367`, `ZoneNpcPostureState.cs`); the others have 0 grep hits.
- Work:
  - Add the signal names at the native trigger points (aggro-table change, follow-unit loss, anchor/path transitions, death, despawn, alert-target change, command-set completion) via the actor signal queue (`ZoneNpcAiSignals.cs`, `ZoneAiSignal.cs`).
  - Implement the `AI:SetParamX2()` call at NPC init with the native error log strings.
  - Keep unknown-signal behaviour per 3050EA90 (`ZoneNpcBehavior`).
- Depends on: LUA-01, LUA-02; NPC aggro/state area
- Confidence: high

### LUA-05: Complete CAIHandler: character scripts, aiconfig, OnLoadAI/OnSaveAI, StartupBehaviour
- Priority: P1
- Native evidence: cryaction strings "can't load script for character [%s]" (x2game strings 0x39F8B558 too), `scripts/ai/aiconfig.lua`, AICharacter/AIBehaviour/AVAILABLE/INTERNAL/DEFAULT, StartupBehaviour, OnLoadAI, OnSaveAI, "Handling AISIGNAL '%s' takes %g ms!", "Failed to find GO_TO_SPAWN behavior transition", cvar `ai_enablewarningserrors`; x2game.c:155587 (GO_TO_SPAWN), 155622 (GO_TO_IDLE).
- Repo state: `src/AAEmu.Zone/Scripting/ZoneNpcBehavior.cs` ports transitions and lookup (3050D8F0, 3050EA90, 305108E0, 30511450); no aiconfig, character-table loader, OnLoadAI/OnSaveAI, slow-signal diagnostic; nothing constructs `ZoneNpcBehavior` outside Scripting.
- Work:
  - Load aiconfig.lua at bootstrap; resolve AICharacter/AIBehaviour tables through `Script.LoadScript` using `ZoneAiLookupTable` results.
  - Construct and register a `ZoneNpcBehavior` per spawned NPC (`ZoneNpcLuaRuntime.Register`).
  - Add StartupBehaviour, GO_TO_SPAWN handling and the timing diagnostic.
- Depends on: LUA-01; NPC spawn
- Confidence: medium

### LUA-06: Provide the entity-script host (entity tables and lifecycle callbacks)
- Priority: P1 (needed for Rain/Tornado/WindArea and any scripted entity; AI code already assumes entity tables via `GetEntityScript`)
- Native evidence: cryentitysystem_dedicate.dll callbacks (list in table); x2game strings 0x39FCD618-0x39FCD670 (`Scripts/Entities/Physics/WindArea.lua`, `Environment/Rain.lua`, `Environment/Tornado.lua`); `Scripts/Entities/Actor/%s.lua`; x2game.c:594222 (OnReset), 667159 (OnCollision), 655563 (OnDeath); "SetScriptTableByDDCMS"; "Failed to get entity script of Npc".
- Repo state: absent. `IZoneNpcActorRuntime.GetEntityScript` is an interface only.
- Work:
  - Define entity-class script loading (Properties/PropertiesInstance, States, Client/Server tables).
  - Create the entity table with an `id` handle (`ZoneLuaHandle`); call OnInit/OnSpawn/OnReset/OnUpdate/OnTimer/OnEvent/OnShutDown at the native lifecycle points.
  - Implement the Entity-bind methods (name list still to be extracted from the Entity bind cluster in cryentitysystem_dedicate.dll, near GetWorldPos 0xd5dc0 / SetWorldPos 0xd5df0).
- Depends on: LUA-01; scene/entity-system area
- Confidence: medium

### LUA-07: Implement remaining `Script.*` functions (timers, reload variants, dumps)
- Priority: P1
- Native evidence: cryscriptsystem.dll names SetTimer, SetTimerForFunction, KillTimer, ReloadScripts, ReloadEntityScript, DumpLoadedScripts, Debug, DebugFull; `lua_use_binary` (0x39FF1A20); "global name already exist in script system : %s" (0x39FF7C58).
- Repo state: `ZoneScriptLoader.cs:57-79` has LoadScript/ReloadScript/UnloadScript only.
- Work:
  - Timers on the simulation clock, callbacks on the script/actor phase.
  - ReloadScripts/ReloadEntityScript via the loader with force; dump/debug as logging.
  - Duplicate-global guard.
- Depends on: LUA-01
- Confidence: medium

### LUA-08: Implement server-meaningful `System.*` and `Physics.*`
- Priority: P2 (P1 for System.GetEntity/GetEntityByName/GetEntitiesInSphere/GetEntitiesByClass/SpawnEntity/RemoveEntity/GetCVar/GetFrameTime/GetCurrTime/Log/Warning/GetTerrainElevation and Physics.RayWorldIntersection)
- Native evidence: cryscriptsystem.dll System names (GetEntity, GetEntityByName, GetEntityIdByName, GetEntities, GetEntitiesByClass, GetEntitiesInSphere(ByClass), GetNearestEntityByClass, GetPhysicalEntitiesInBox(ByClass), SpawnEntity, RemoveEntity, SendScriptEventToAllEntities, GetCVar/SetCVar, AddCCommand, ExecuteCommand, Log/LogAlways/Warning, GetFrameTime, GetCurrTime, GetCurrAsyncTime, GetTerrainElevation, IsValidMapPos, IsPointIndoors, GetWind/SetWind, ScanDirectory, LoadLocalizationXml ...); Physics: RayWorldIntersection, RayTraceCheck, SamplePhysEnvironment, SimulateExplosion, RegisterExplosionShape, RegisterExplosionCrack.
- Repo state: absent.
- Work:
  - Implement the subset (entity queries, cvar, time, terrain elevation, ray queries via `ZoneNativeTerrainPhysicsWorld.SceneQueries`); leave render/UI functions as no-ops.
- Depends on: LUA-06; physics/scene areas
- Confidence: medium (list truncated; signatures not decompiled)

### LUA-09: Provide `Particle.*` and `Sound.*` stub tables
- Priority: P2
- Native evidence: cryscriptsystem.dll Particle (CreateEffect, DeleteEffect, IsEffectAvailable, SpawnEffect, SpawnEffectLine, SpawnParticles, CreateDecal, CreateMatDecal, Attach, Detach) and Sound (Play, PlayEx, LoadSound, Load3DSound, SetSoundPosition ... ~60).
- Repo state: absent.
- Work:
  - Register stub tables with native default returns so shared entity scripts (Rain/Tornado) do not error on the server.
- Depends on: LUA-06
- Confidence: low (server-side behaviour not verified)

### LUA-10: Command-set / quest `RunCommandSet` path
- Priority: P1
- Native evidence: strings 0x39FE0370-0x39FE05C8 ("Failed to run AI command set(%u)", "Unknown ai command", RunCommandSet, commandSet/commands/commanderUnit/runType/canInteract), `X2::GameServer::Npc::RunAICommandSet`, RTTI `WZRunCommandSetOnQuestPacket` (0x3A51A1A8), 39341BD0/39341E00/3933D790.
- Repo state: `src/AAEmu.Zone/Scripting/ZoneNpcSpawnCommands.cs` builds the type2/type5 request; no general RunAICommandSet caller, no WZRunCommandSetOnQuest handler, no commander-unit resolution or native error logs.
- Work:
  - Implement `Npc::RunAICommandSet` end to end (commandSet lookup, commander entity script, native log messages).
  - Hook the World-to-Zone RunCommandSetOnQuest packet.
- Depends on: LUA-01, LUA-02; protocol area (WZ opcode)
- Confidence: medium

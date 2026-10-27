<!-- Audit of rtti-inventory: native x2game-dev_dedicate.dll (ArcheAge 10.0.2.13 r575) vs this repo at 76e439f. Paths rewritten to the src/ layout; line numbers refer to 76e439f. -->

## RTTI inventory (cross-cutting class census)

The strings file holds 5,995 unique MSVC RTTI names. 3,794 mention `Packet` (skipped; 185 are WZ/ZW zone-world packets covered by other auditors). The rest (2,201) split into 1,426 in `X2` (non-packet), about 214 CryEngine-style `I*` interfaces/prefab/UI dialog tasks, about 101 std, plus Concurrency/Json/Gdiplus/MFC/Widget noise. Of the 1,426 X2 non-packet entries, 861 are `DescStorage<...Desc>` and 24 `DescMapStorage<...>` template instantiations (content-table row types, owned by the content-DB auditor), 311 are `X2::GameClient::*` (client-only), and only **23 are `X2::GameServer::*`** plus about 134 shared `X2::*` classes. IMPORTANT: `x2game-dev_dedicate.dll` is the shared game module, so it carries lots of client code (ScriptBind*Addon, Bot*, Editor*, builders, FX); only the `GameServer` namespace plus shared X2 classes with Server*/Net*/Zone* variants are authoritative for the zone. The repo (AAEmu.Zone) implements content loading, terrain/scene/physics, NPC spawners/AI/goal pipes/aggro, faction/siege/time state, equipment/stat calculators, FlowNodes and the WZ/ZW opcode layer. It has NO server-side Unit/Character hierarchy beyond `ZoneUnit`/`ZonePlayerActor`, no skill execution (SkillController family), no doodad/gimmick/slave/ship/housing runtime, no area manager, no quest journal, no move-hack checker. Skipped counts: std 101, Concurrency 4, Json 3, Gdiplus 3, MFC `C*` ~40, Widget/X2UI ~60, `*DlgTask` ~60, `ScriptBind*Addon/Widget` ~120, Prefab/Editor objects ~30, renderer/shader/Font ~25.

### Class census (non-packet)

| Category | Class names | Likely role | Repo equivalent |
|---|---|---|---|
| Managers/singletons (GameServer) | `GameServer::NpcManager`, `CharacterManager`, `HousingManager`, `ServerAreaManager` (+`IServerAreaManager`, string `ServerAreaManager::LoadAreas`), `ServerGimmickManager` (strings), `NpcSpawner`, `NpcGroupIdGenerator`, `NpcGroupSupporter`, `NpcAggroTable` | Zone-side registries; area enter/leave; spawner mgmt | NpcManager -> `src/AAEmu.Zone/Npcs/ZoneNpcManager.cs`; spawners/groups/aggro -> `ZoneNpcAreaSpawner/GroupSpawner/PointSpawner/Groups`, `ZoneAggroTable.cs`. CharacterManager, HousingManager, ServerAreaManager, ServerGimmickManager: absent |
| Unit/actor (shared) | `Unit`, `UnitModel`, `IUnitModel`, `EmptyUnitModel`, `ActorUnitModel`, `PrefabUnitModel`, `ShipUnitModel`, `VehicleUnitModel`, `IUnitModelListener`, `Character`, `Doodad`, `TelescopeUnit`, `Team`, `TeamAccessor/PartyAccessor/RaidTeamAccessor/SoloAccessor`, `SquadBase`, `ICombatant`, `AIUnitProxy`, `AIUnitSystem` | Entity model and ownership; team lookup | `ZoneUnit.cs`, `ZoneUnitRegistry.cs`, `ZoneUnitPose/ScaleState`, `ZonePlayerActorFactory.cs`; team/party accessors, UnitModel layer, Doodad, Telescope: absent |
| Unit/actor (GameServer) | `ServerUnit`, `ServerCharacter`, `Npc`, `ServerUnitOwner`, `ServerAIUnitSystem`, `ScriptBindUnit`, `ServerUnitOwnerManager` (template), `ZoneClient` | Server authoritative unit, owner migration, per-client link | `ZoneNpc.cs`, `ZoneNpcSimulation.cs`, `ZoneNativeAiProvider.cs`, `src/AAEmu.Zone/Scripting/ZoneNpcBehavior.cs`, `src/AAEmu.Zone/WorldLink/ZoneWorldClient.cs` (World link). ServerCharacter/ServerUnitOwner: absent |
| Movement controllers | `IUnitModelMovementController`, `NetActorMovementController`, `NetEmptyUnitModelMovementController`, `NetShipMovementController`, `NetVehicleMovementController`, `VehicleMovementController`, `IShipMovementController`, `ServerShipMovementController`, `ActorMovementController`, `MountHorseController`, `UnMountHorseController`, `FrameSkip` (`ClientShip*Controller` client-only) | Movement-mode drivers per model type | `ZoneActorMovementController.cs` + `ZoneActorMovement*` (actor only). Ship/vehicle/mount/net: absent |
| Skill controllers | `ISkillController`, `INetSkillController`, `MoveSkillController`, `DashSkillController`, `LeapSkillController`, `CrawlSkillController`, `FloatingSkillController`, `RotateSkillController`, `WanderingSkillController`, `ImpulseSkillController`, `AnchorSkillController`, `RopeSkillController`, `RopeReadySkillController`, `ActorRopeSkillController(Impl)`, `FlowGraphSkillController`, `Net{Dash,Leap,Floating,ActorRope}SkillController`, `SlaveSpawn/Despawn/EscapeController`, `ScriptBindSkillTable` | Skill-driven movement/placement (dash, leap, rope, slave spawn) | `ZoneSkillContent.cs` (data only). absent |
| Skills/buffs/combat | `BuffMan`, `GameServer::ZoneBuffMan`, `BuffFinder` + ~15 `*BuffFinder` (Glide, CannotJump, WalkOnly, Taunt, RemoveOnLand, ImpossibleTarget, CantEnterBattleField, PlayNote, SkillModifier, TriggerSpecialEffect, DynamicUnitModifier, AutoLearnPassive, BlankMinded, BuffTag, BuffBlockedChildDoodad), `Calc*/Const*/Append*Visitor` (~20), `BuffVisitor`, `Ballistic`, `AimBox/AimSphere/IAimPoint`, `Raycaster` | Buff container + modifier calculation; aim/ray | `ZoneBuffLifecycle/Timing/LandingRemoval/ZoneBuffContent`, `ZoneStatCalculator`, `ZoneEquipmentCalculator`, `ZoneCombatRelations`. Finders/visitors/ZoneBuffMan: partial (lifecycle only) |
| AI | `IAIUnitSystem`, `ServerAIUnitSystem`, `NpcAggroTable`, `AwaitingUnits`, `NpcGroupSupporter`, `IGoalPipeListener`, `IPuppetProxy`, `IAIPathFindListener`, `AINavModifierPrefabObject` | AI unit system, goal pipes, pathfind | `Simulation/ZoneGoal*.cs`, `ZoneNpcAiSignals.cs`, `ZoneAiObjectRegistry.cs`, `ZoneNavigation*.cs` (NPC-AI auditor) |
| Items/inventory | `GenericInventory`, `ItemChangeSet`, `ItemLifespanTracker`, `Butler` | Server inventory + item timers; butler (pet NPC) | `ZoneEquipmentContent/Lifecycle` only. Inventory, change sets, lifespan: absent. (Bag/Bank/Coffer/Common InventoryManager and Item enchant classes are GameClient; not needed) |
| Doodads | `Doodad`, `ClimbDoodadInfo`, `IZoneDoodadModel`, `GameBreakableEventDispatcher`; about 150 `DoodadFunc*Desc` DescStorages; WZCreateDoodad/WZDoodadChangePhase | Doodad entities, phase funcs | content: none for doodads in `Content/`; runtime absent |
| Gimmicks (physics props) | `ServerGimmick`, `ServerGimmickManager` (strings), `GameBreakableEventDispatcher`, `ZdModelBrush/Crater/Dummy/Prefab/Vegetation` | Breakable/jointed physics props | absent (`ZdModel*` brush/vegetation overlap with scene loaders `ZoneCellBrushResources`, `ZoneCellVegetationResources`) |
| Housing | `GameServer::Housing`, `HousingManager`, `HousingBase`, `HousingAreaChecker` (template), `DominionExpeditionChecker`, ~25 `Housing*Desc` | Player houses, build progress, tax | absent |
| Vehicles/ships/slaves | `ServerShipSimulationController`, `ServerVehicleSimulationController`, `ShipUnitModel`, `VehicleUnitModel`, `Slave*Controller`, `MountHorseController` | Ship/vehicle physics, slave lifecycle | absent |
| Quests | `QuestContextJournal` (shared), ~120 `QuestAct*Desc` | Quest state; mostly World-side except WZ quest-AI commands | absent (only `WZAttackOnQuest/FollowPath` message types) |
| Chat/mail/social | `BubbleChat`, `FactionDesc`, `ExpeditionDesc`, Family manager (client), `PartyAccessor` | Zone only has NPC speech / WZ expedition+faction state | NPC speech: `Protocol/Messages/NpcSpeech.cs`; faction: `ZoneFactionState.cs`. Mail/Friend/Family/Chat: client-only/world-only |
| Expedition/guild | `ExpeditionDesc`, `ExpeditionBuffDesc`, WZ `UnitExpeditionChanged`, `ExpeditionWarState`, `FactionCreated/Dismissed/Independence/SponsorChanged` | Expedition membership mirrored from World | `ZoneFactionState.cs`, `ZoneSiegeState.cs`, `Protocol/Messages/FactionChanges.cs` |
| Trade/auction/crafting | `CraftManager(Impl)`, `Npc{Auctioneer,Trader,Banker,...}` (all GameClient) | UI/interaction with World services | client-only / world-only |
| World/zone | `EnvironmentManager`, `GameScheduleData`, `SafeAreaRuntimeInfo`, `FixedRanking/RankingSnapshot/RankingRewardSnapshot/RankingEntry`, `MoveHackChecker::MovementVerifier`, `ZoneDesc/SubZoneDesc` | Weather/time/env, schedule events, safe areas, rankings, anti-teleport | `ZoneTimeOfDayState`, `ZoneWorldGameClock`, `ZoneGameScheduleController`, `ZoneWorldMapLayout`. EnvironmentManager climate, SafeArea, Ranking, MoveHack: absent |
| Network/session | `GameServer::ZoneClient`, `IZoneClientDelegate`, `IStreamClientDelegate`, `StreamClient`, `IAuthClientDelegate`, `AuthClient`, `TgwWorker`, `ZWUnitMovementsBuffer` | WZ/ZW link to World, TGW worker, stream client | `src/AAEmu.Zone/WorldLink/ZoneWorldClient.cs`, `Protocol/ZoneFrame*`, `ZoneSession.cs`; Tgw absent (verify need) |
| Scripting | `GameServer::ScriptBindUnit`, `ScriptBindSkillTable`, `FlowGraphWrapper/IFlowGraphWrapper`, `FlowGraphValue<int/bool>` | Lua bindings for AI; flow graph wrappers | `Scripting/*`, `ZoneNativeFlowNode*` |
| Physics | `Raycaster`, `Ballistic`, `ZdModel*`, WZ `PhysicsCPU/PhysicalExplosion/RayCasting`, ZW `RayCastingResult/UnitCollision(Result)/DoodadCollideUnit/GimmickCollided` | Zone-side ray/collision services | `ZoneNativeTerrainPhysicsWorld*`, `ZoneOwnedPhysicsBackend`; ray-cast request/response and collision reporting absent |
| Anti-cheat | `MoveHackChecker::MovementVerifier`, ZW `ReportMoveHack/ReportDoodadSurfaceHack/ReportMoleMiner/ReportMoleTrader`, WZ `CheckMoleMiner/Trader` | Move/surface hack detection | absent (opcodes only in ZwOpcodes) |
| Misc | `CryptoSystem`, `StringPile`, `ReloadDescCompleteHandler`, `IUccChange`, `RankingOrderedEntry`, `SandboxOnlineNet` (+WZ/ZW SandboxOnline*) | link crypto, content reload, UCC, sandbox editor | absent |
| Client-only leftovers (not needed) | all 311 `GameClient::*` (Npc{Banker,...} interactions, `*Builder`, `*Positioner`, `Bot*`, `Editor*`, `Fx*`, `SoundManager`, `CameraManager`, `Tutorial`, `HotkeyManager`, `*DataMiner`, `Item{Awaken,Evolving,Smelting,Socket*,GradeEnchant}`, `UseItem*`, `Client*` incl. ClientNpc/ClientDoodad/ClientBuffMan), `ScriptBind*Addon` (~120), `*DlgTask` (~60), `*Widget`, `X2UI`, `IEditor*`, `*PrefabObject`, `CharacterCustomizer`, `ScreenFaderFlowNode` | Presentation/UI/editor | Not needed (FlowNodes already stubbed in repo for shared graph load) |
| Content row types | 861 `DescStorage<*Desc>` + 24 `DescMapStorage<*Desc>`: Quest (~120 act types), DoodadFunc (~150), Indun (~25), Item* (~100), Housing (~25), Siege/Dominion, TowerDef, Hero, Ranking, Craft, Slave/Ship, Tradegood, GameSchedule, Zone/SubZone/ZoneClimate | Zone loads these from compact DB | Repo has ~38 content readers in `Content/` (Buff, Skill, Npc, Loot, Faction, Stat, Equipment, Achievement, Appellation, World, Scene...). Missing readers: RT-09 |

### Tasks

### RT-01: Add ServerUnit / ServerCharacter / ServerUnitOwner class layer and CharacterManager
- Priority: P0 (blocks first-entry milestone)
- Native evidence: `.?AVServerUnit@GameServer@X2@@`, `ServerCharacter@GameServer`, `ServerUnitOwner@GameServer` (0x3A517B10), `CharacterManager@GameServer` (0x3A519298), `ServerUnitOwnerManager` template; strings `X2::GameServer::CreateServerUnitFromStatePackets`, `ServerUnit::LeaveClimbDoodad`, `X2::Unit::{AttachTo,Warp,Impulse,ProcessPassenger}`
- Repo state: `ZoneUnit.cs`, `ZonePlayerActor(Factory).cs`, `ZoneUnitRegistry.cs` exist but no Character/owner abstraction; player actors "incomplete" per TODO.md
- Work:
  - Define Unit -> ServerUnit -> ServerCharacter/Npc split matching native (owner, team, model, passenger/attachment).
  - Build CharacterManager: admission from `ZWJoin`/state packets, removal on `WZUnitRemoved`.
  - Wire `ZonePlayerActorFactory` through it, including `Unit::Warp` and Impulse/Attach hooks.
- Depends on: WZ/ZW packet auditors' state-snapshot tasks
- Confidence: medium

### RT-02: Implement ServerAreaManager (area enter/leave) and EnvironmentManager
- Priority: P1
- Native evidence: `ServerAreaManager@GameServer` (0x3A517AA0), `IServerAreaManager`, string `X2::GameServer::ServerAreaManager::LoadAreas`; ZW `ZWEnterArea`/`ZWLeaveArea`; `EnvironmentManager@X2` (0x3A4DC1E0); `ZoneClimateDesc/ZoneClimateElemDesc/SubZoneDesc`
- Repo state: absent (only `ZoneWorldMapLayout`, `ZoneTimeOfDayState`)
- Work:
  - Load area shapes from level data and track unit membership per frame.
  - Emit `ZWEnterArea`/`ZWLeaveArea` to World.
  - Port EnvironmentManager climate/weather state needed by buffs/AI.
- Depends on: RT-01, content reader for Zone/SubZone/Climate descs
- Confidence: medium

### RT-03: Port the skill-controller family and ZoneBuffMan
- Priority: P1
- Native evidence: ~22 `*SkillController` RTTI (Move/Dash/Leap/Crawl/Floating/Rotate/Wandering/Impulse/Anchor/Rope/ActorRope/FlowGraph + Net variants); strings `X2::GameServer::FireSkill/HealUnit/PlayEffect`, `ServerAIUnitSystem::UseSkill/PickSkill/StopCasting`; WZSkillStarted/Fired/Ended, ZWStartSkill/StopSkill/StopCasting, `WZCreateSkillController`, `ZWSkillControllerState`; `ZoneBuffMan@GameServer` (0x3A527EF0) + ~15 `*BuffFinder` + `Calc*Visitor`s
- Repo state: `ZoneSkillContent.cs`, `ZoneBuffContent.cs`, `ZoneBuffLifecycle.cs` (data + lifecycle); no skill runtime, no controllers
- Work:
  - Skills auditor owns plot/effects; this task covers the controller classes (movement-affecting skills) and ZoneBuffMan finders (jump/walk/glide/taunt restrictions consumed by movement and AI).
  - Implement ZoneBuffMan with modifier visitors feeding `ZoneStatCalculator`.
  - Implement Dash/Leap/Impulse/Anchor/Rope controllers as movement overrides in `ZoneActorMovementController`.
- Depends on: RT-01
- Confidence: medium

### RT-04: Implement doodad runtime (Doodad, phase funcs, climb doodads)
- Priority: P1
- Native evidence: `Doodad@X2`, `ClimbDoodadInfo`, `IZoneDoodadModel@GameServer`; WZCreateDoodad/WZRemoveDoodad/WZDoodadChangePhase/WZUnitBondToDoodad; ZWDoodadCollideUnit, ZWReportDoodadSurfaceHack; ~150 `DoodadFunc*Desc` storages
- Repo state: absent (only mentions in movement controller + model content)
- Work: doodad entity class; phase state machine driven by WZ packets; collision events back to World; climb-doodad support for movement. See doodad auditor for detail.
- Depends on: RT-01
- Confidence: medium

### RT-05: Implement gimmick (breakable/jointed physics prop) subsystem
- Priority: P2
- Native evidence: `ServerGimmick@GameServer` (0x3A518B58), strings `ServerGimmickManager::{CreateGimmick,HandleGimmickMovement,_OnJointBroken,OnBreakableCreatePartEvent}`, `GameBreakableEventDispatcher`, `ZdModel{Brush,Crater,Dummy,Prefab,Vegetation}`; WZGimmick*, ZWGimmick*
- Repo state: absent
- Work: gimmick manager; static/dynamic entity init; joint break/reset; spawn-static-gimmick requests; needs physics joint support.
- Depends on: physics backend (movement auditor)
- Confidence: medium

### RT-06: Implement housing runtime (Housing, HousingManager)
- Priority: P2
- Native evidence: `Housing@GameServer`, `HousingManager@GameServer` (0x3A518C70), `HousingBase`, strings `Housing::SetBuildDone`, `HousingManager::Create`, `OnHouseBuildProgress/Done`; WZHouseState/BuildProgress/BuildDone, ZWRemoveHouse
- Repo state: absent
- Work: house entity from WZHouseState; build-step progress; removal; collision/area checks.
- Depends on: RT-01, RT-04
- Confidence: medium

### RT-07: Implement ship/vehicle/slave simulation controllers
- Priority: P2
- Native evidence: `ServerShipSimulationController` (0x3A512208), `ServerVehicleSimulationController`, `ServerShipMovementController`, `NetShipMovementController`, `NetVehicleMovementController`, `SlaveSpawn/Despawn/EscapeController`, `ShipUnitModel`, `VehicleUnitModel`; WZShipControlChange, WZSlaveMasterChanged, WZEscapeSlave, `HandleEscapeSlave`
- Repo state: absent
- Work: ship/vehicle physics sim; slave spawn/despawn/escape; master change; passenger attach (`Unit::ProcessPassenger`).
- Depends on: RT-01, physics
- Confidence: medium

### RT-08: Implement MoveHackChecker and mole-miner/trader reports
- Priority: P2
- Native evidence: `MovementVerifier@MoveHackChecker@X2`; ZWReportMoveHack, ZWReportDoodadSurfaceHack, ZWReportMoleMiner/Trader, WZCheckMoleMiner/Trader
- Repo state: opcodes only (`ZwOpcodes.cs`)
- Work: server-side movement verification against `ZoneActorMovementCorrection` output; send reports; handle checks.
- Depends on: player movement complete
- Confidence: medium

### RT-09: Add missing content readers for zone-relevant DescStorage types
- Priority: P1
- Native evidence: DescStorage RTTI: `ZoneDesc, SubZoneDesc, ZoneClimate(Elem)Desc, ZoneGroupDesc, ServerConfigDesc, SystemFeatureControlDesc, Doodad*/DoodadFunc*, DoodadPhaseFuncDesc, Housing*, Slave*/ShipModel/Shipyard*, Indun*, InstanceFaction*, Mate*/Mount*, TowerDef*, GuardTower*, BattleField*, GameRule*/GameSafeArea*, ConflictZone*, Siege*, Dominion*, Specialty*, Tradegood*, Ranking*`
- Repo state: ~38 content readers in `Content/`; none for doodad, housing, slave, indun, subzone/climate, safe area, specialty/tradegood
- Work: coordinate with content-DB auditor; add readers in priority order: Zone/SubZone/Climate/SafeArea (RT-02), Doodad* (RT-04), Slave/Ship (RT-07), Indun*/InstanceFaction (RT-10).
- Depends on: content-DB audit
- Confidence: high

### RT-10: Implement instance/indun, battlefield and tower-defense zone logic
- Priority: P2
- Native evidence: `IndunAction*Desc` (7), `IndunEvent*Desc` (9), `IndunRoom/Round/RoomSphere`, `InstanceDifficultInfo`, `InstanceGainRule`, `BattleField*Desc`, `TowerDef*Desc`; WZTowerDef{Start,End,Reload,WaveStart,QueryPlayability}, ZWTowerDefReportPlayability, ZWSaveInstanceSpawner, WZGameActivityStart/End
- Repo state: `Protocol/Messages/SaveInstanceSpawner.cs`, `ZoneSiegeState.cs`; no indun action/event engine
- Work: indun event/action dispatcher (npc killed/spawned -> next round, room cleared, phase change); tower-defense waves; instance-spawner persistence.
- Depends on: RT-04, NPC spawner events
- Confidence: medium

### RT-11: Hook raycast/collision services to World packets
- Priority: P1
- Native evidence: `Raycaster@X2`, WZRayCasting/ZWRayCastingResult, ZWUnitCollision/ZWUnitCollisionResult, ZWUnitFell, WZPhysicsCPU, WZPhysicalExplosion
- Repo state: `ZoneNativeTerrainPhysicsWorld.SceneQueries.cs` (queries exist, not hooked to World packets)
- Work: map WZ ray request to scene query and return ZW result; fall damage `ZWUnitFell` from movement; explosion impulses.
- Depends on: physics world ready
- Confidence: medium

### RT-12: Determine need for TgwWorker / StreamClient / AuthClient / CryptoSystem
- Priority: P2
- Native evidence: `TgwWorker@X2` (0x3A57DF90), `StreamClient`, `IStreamClientDelegate`, `AuthClient`, `IAuthClientDelegate`, `CryptoSystem`, `IZoneClientDelegate`
- Repo state: `src/AAEmu.Zone/WorldLink/ZoneWorldClient.cs` handles World link only
- Work: check xrefs to see whether the dedicated zone opens TGW/stream/auth connections; if not, document as client-only. CryptoSystem is needed only if WZ/ZW frames are encrypted (check `ZoneFrameDecoder`).
- Depends on: none
- Confidence: low

### RT-13: Ranking snapshots and ReloadDesc handling
- Priority: P2
- Native evidence: `FixedRanking`, `RankingSnapshot`, `RankingRewardSnapshot`, `RankingEntry`, `ReloadDescCompleteHandler`, `GameScheduleData`; WZGameSchedule{Start,End,Continue}
- Repo state: `ZoneGameScheduleController.cs` present; ranking and reload absent
- Work: add rank snapshot types only if zone computes them (verify callers); implement GM-triggered content reload.
- Depends on: none
- Confidence: low

### RT-14: Team/party/raid accessors for zone-side aggro sharing and friendly checks
- Priority: P1
- Native evidence: `Team`, `TeamAccessor`, `PartyAccessor`, `RaidTeamAccessor`, `SoloAccessor`, `SquadBase`, `SquadBase::HandleDelegateLeader`; WZSiegeMember, WZJoinResponse
- Repo state: absent (`ZoneUnitRelationships` covers factions only)
- Work: minimal team membership registry on unit (aggro share, friendly checks, hunt credit); populate from World messages.
- Depends on: RT-01
- Confidence: low

### RT-15: Butler and Mate handling on the zone
- Priority: P2
- Native evidence: `Butler@X2`, `ButlerDesc` etc. (client `ClientButler`, `Mate`); `Mate*Desc`, `MountSkillDesc`; WZ has no Mate-specific packets (mate likely a Slave on zone)
- Repo state: absent
- Work: confirm Butler/Mate are modeled as Slave/Npc units in WZ state packets; implement via RT-07.
- Depends on: RT-07
- Confidence: low

#### Explicit client-only judgments (do not scope)
- `GameClient::Npc*` interaction handlers (Banker, Auctioneer, Trader, Teleporter, Trainer...): client UI interaction; World handles service logic.
- `GameClient::*Builder/*Positioner/RectChecker`, `CraftManager`, Item enchant/socket/evolve classes: UI/preview; crafting and enchant authority is on the World.
- `ScriptBind*Addon/Widget`, `*DlgTask`, `X2UI`, `Font*`, `Fx*`, `Sound*`, `Camera*`, `Bot*`, `Editor*`, `*PrefabObject`, `CharacterCustomizer`: client/editor.
- `ClientNpc/ClientDoodad/ClientGimmick/ClientBuffMan/ClientTeam/ClientSquadManager`: client twins of server classes; only the server twin is needed.
- Sandbox editor (`SandboxOnlineNet`, WZ/ZW SandboxOnline*): dev tool; low priority.

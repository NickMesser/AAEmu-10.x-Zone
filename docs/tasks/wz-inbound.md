<!-- Audit of wz-inbound: native x2game-dev_dedicate.dll (ArcheAge 10.0.2.13 r575) vs this repo at 76e439f. Paths rewritten to the src/ layout; line numbers refer to 76e439f. -->

## World->Zone inbound packets (WZ)

The native registration routine `sub_3936F740` (x2game-dev_dedicate.c) calls 133 per-packet registrars (`sub_3937F610` ...), each of which stores a `PacketFunctor<ZoneClient, WorldToZonePacketType, X2::WZ...>` into a fixed slot of a 139-entry table (slot = opcode). 133 slots are populated; 0x1D, 0x38, 0x41, 0x43, 0x79 and 0x83 are unregistered natively (so no packet exists for them). The task brief said 128 RTTI classes; the registration actually names 133 (a few classes lack the `Packet` suffix: `WZSandboxOnline*`, `WZChangeChargeSkillCooldown`, and NpcSpawnerEvent is named `X2::NpcSpawnerEventPacket`). `WzOpcodes.cs` has all 133 constants and the set matches the native set exactly (verified by diff), so (a) is 100%. Decoded message types exist for roughly 56 opcodes, and exactly those 56 are dispatched (`ZoneSession.ReceiveCore` at src/AAEmu.Zone/WorldLink/ZoneSession.cs:322, plus `src/AAEmu.Zone/WorldLink/ZoneStartupState.cs` for join/faction/spawner/time packets pre-readiness). The other 77 hit the `default:` branch, which counts them in `_unimplementedOpcodes` and logs once (ZoneSession.cs:535). The first-entry path (JoinResponse, FactionList/RelationList, SpawnerList, time packets, UnitState, UnitRemoved, UnitMovement, UnitPoints/Death/Equipment/Buff) is decoded and dispatched; the biggest gaps are everything skill/combat-feedback (SkillStarted/Fired/Ended, UnitDamaged/Healed, Plot*), doodads/houses, gimmicks, slaves/ships, attach/hang/bond, quest NPC commands, RunCommand and the "reply" packets (RequestZoneStatus, RequestCombatUnits, RequestUnitFlyingState, RayCasting, TowerDefQueryPlayability, CheckMole*), which also need ZW responses.

Notes on method: body offsets in the table are `a2+N` offsets into the native packet object (payload fields start at +16 after the vtable/header), taken from the handler pseudocode; they are not the wire layout. Native handlers all `return 1` and never terminate the session; unknown unit ids are logged/ignored. Repo "done" means decoded + dispatched, not that every behavior matches the native one.

### Coverage table (all 133 registered WZ packets)

| Opcode | Packet | Native handler | Native handler behavior (one line) | In WzOpcodes.cs | Decoded message in repo | Dispatched in repo |
|---|---|---|---|---|---|---|
| 0x0000 | JoinResponse | sub_393648D0 | sets World join state (a1+104..128 = server ids), refuses with 'world server refused joining' if code!=0; logs 'successfully joined' | yes | ZoneJoinResponse | src/AAEmu.Zone/WorldLink/ZoneStartupState.cs:80 |
| 0x0001 | RunCommand | sub_39366650 | logs 'command from world', runs console command via CmdSystem w/ result sink, replies ZWCommandResponse(id,text<=512) | yes | absent | absent |
| 0x0002 | NpcState | sub_39362D40 | if level loaded -> NpcMgr create from state (unit state at a2+10184); else logs 'OnNpcState: level is not loaded yet' and drops | yes | NpcStateSnapshot | ZoneSession:372 RequireNpcs().Create |
| 0x0003 | NpcSpawnFailed | sub_39362DC0 | NpcMgr spawn-failed cleanup for the spawner (sub_39345DE0) | yes | NpcSpawnRequest.ToFailureFrame (writer only) | ZoneSession:375 SpawnFailed |
| 0x0004 | NpcStartDespawn | sub_39364A10 | looks up unit (type==NPC), fires AI event 'GO_TO_DESPAWN' on its AI | yes | absent (NpcRemoval in NpcNotifications.cs is not this packet) | absent |
| 0x0005 | SpawnerList | sub_39362DF0 | logs 'adding %d persistent spawners', adds list (u8 count +17, entries +20); Last flag (+16) sets join flag |=2 | yes | SpawnerSnapshot | src/AAEmu.Zone/WorldLink/ZoneStartupState.cs:98 |
| 0x0006 | HouseState | sub_39362E50 | HouseMgr (A40+16) create/update house state from body | yes | absent | absent |
| 0x0007 | UnitState | sub_3936B560 | logs 'OnUnitState id,name,type,pos'; CreateServerUnitFromStatePackets: rejects id>max or duplicate id, builds unit by owner-type | yes | UnitStateSnapshot (+Units/*) | ZoneSession:414 Registry.Create + scene admission |
| 0x0008 | UnitRemoved | sub_39364A80 | find unit; switch(owner type 0 char,1 npc,2 slave,3 house,...) -> manager Remove(id,true); unknown id silently returns | yes | raw ReadBc id | ZoneSession:470 RemovePlayer + Registry.Remove |
| 0x0009 | UnitMovement | sub_39362E80 | unless server-flag set (or move type==5) find unit, unit->ReceiveMovement(a2+24), then physics-system notify | yes | UnitMovement (+variants) | ZoneSession:387 EnsureMovementModel+ReceiveMovement |
| 0x000A | UnitAttached | sub_39362F90 | child unit AttachTo(parent a2+20, point a2+28); log 'can't find child' | yes | UnitAttachment record only (state snapshot) | absent |
| 0x000B | UnitDetached | sub_39362FF0 | child unit Detach(point a2+20) | yes | absent | absent |
| 0x000C | UnitHung | sub_39363040 | unit Hang(a2+20,a2+24) (hung on object) | yes | absent | absent |
| 0x000D | UnitUnhung | sub_393630A0 | unit Unhang | yes | absent | absent |
| 0x000E | UnitBondToDoodad | sub_39363100 | unit BondToDoodad(bond data a2+20, a2+44) | yes | UnitBonding record only (state snapshot) | absent |
| 0x000F | UnitUnbondFromDoodad | sub_39363160 | unit UnbondFromDoodad | yes | absent | absent |
| 0x0012 | UnitModelPostureChanged | sub_393631B0 | unit ChangeModelPosture(posture at a2+20) | yes | UnitPosture record only (state snapshot) | absent |
| 0x0010 | SkillControllerState | sub_39362F00 | unit->GetSkillController()->ApplyState(a2+16) | yes | absent | absent |
| 0x0011 | CreateSkillController | sub_39362F50 | unit->CreateSkillController(a2+16) | yes | absent | absent |
| 0x0013 | FactionList | sub_393631E0 | FactionMgr load faction block (u8 n +20, array +24); when count==total(+16) logs 'faction: all factions are loaded' | yes | FactionSnapshot | Startup:90 |
| 0x0014 | FactionRelationList | sub_39363220 | loads inter-faction relations; at total logs 'received all the factions and inter-factions from world_server', sets join flag |=1 | yes | FactionRelationSnapshot | Startup:94 |
| 0x0015 | FactionCreated | sub_39363270 | FactionMgr.Create(faction at +16) | yes | FactionCreated | ZoneSession:348 |
| 0x0016 | FactionSponsorChanged | sub_39363290 | FactionMgr.ChangeSponsor | yes | FactionSponsorChanged | ZoneSession:351 |
| 0x0017 | FactionDismissed | sub_393632B0 | FactionMgr.Dismiss(id) | yes | FactionDismissed | ZoneSession:354 |
| 0x0018 | FactionSetRelationState | sub_393632D0 | FactionMgr.SetRelationState(first,second,state,next,updateTime,changeTime) | yes | FactionRelationChanged | ZoneSession:357 |
| 0x0019 | UnitFactionChanged | sub_3936E0E0 | unit faction change if old==current; for characters refreshes owner UI | yes | UnitFactionChanged | ZoneSession:360 |
| 0x001A | UnitExpeditionChanged | sub_39364BC0 | unit expedition change if old==current; fires update event | yes | UnitExpeditionChanged | ZoneSession:363 |
| 0x001B | ExpeditionWarState | sub_39363310 | FactionMgr.ChangeWar(first,second,started) | yes | ExpeditionWarState | ZoneSession:366 |
| 0x001C | FactionIndependence | sub_39363340 | FactionMgr.DeclareIndependence(old,new,name,createdTime) | yes | FactionIndependence | ZoneSession:369 |
| 0x001E | UnitEquipmentChanged | sub_39363370 | unit ChangeEquipment(slotcount +20, items +24, reinforcement +160, rnd-attr +7232) | yes | EquipmentChanged | ZoneSession:503 |
| 0x001F | UnitEquipmentsRndAttrUnitModifierAvtivateChanged | sub_393633C0 | unit ChangeEquipmentRndAttrActivation(flags a2+20) | yes | EquipmentActivationChanged | ZoneSession:506 |
| 0x0020 | UnitPoints | sub_39364C30 | unit SetHealth/SetMana (i64 +24,+32); logs error if health>0 on dead unit | yes | UnitPoints | ZoneSession:479 |
| 0x0021 | UnitDeath | sub_393633F0 | unit Kill() | yes | UnitDeath | ZoneSession:482 |
| 0x0022 | UnitResurrection | sub_39369A60 | unit Resurrect(pos a2+24 converted from world coords, yaw quat from a2+48) | yes | UnitResurrection | ZoneSession:485 |
| 0x0023 | OnUnitBlinked | sub_39363420 | physics notify OnUnitBlinked(unit, float a2+20) | yes | absent | absent |
| 0x0024 | ForceAttackSet | sub_39364CC0 | sets unit force-attack byte (+32775) | yes | ForceAttackSet | ZoneSession:494 |
| 0x0025 | CombatEngaged | sub_393637E0 | unit EngageCombat | yes | CombatEngaged | ZoneSession:488 |
| 0x0026 | CombatCleared | sub_39364E70 | unit ClearCombat; resets +32956 | yes | CombatCleared | ZoneSession:491 |
| 0x0027 | CvFCombatRelationship | sub_39363810 | CombatRelations char-vs-faction apply (type byte, entries +24) | yes | CharacterCombatRelations | ZoneSession:342 |
| 0x0028 | FvFCombatRelationship | sub_39363830 | CombatRelations faction-vs-faction apply | yes | FactionCombatRelations | ZoneSession:345 |
| 0x0029 | UnitDuelState | sub_39364EB0 | unit duel id/team (+32744/+32748) | yes | UnitDuelState | ZoneSession:500 |
| 0x002A | TargetChanged | sub_3936E170 | unit SetTarget(id +20); if own char and not forced, notifies UI | yes | TargetChanged | ZoneSession:497 |
| 0x002B | SkillStarted | sub_39363460 | SkillMgr OnSkillStarted(caster,skill,target,cast data...) | yes | absent | absent |
| 0x002C | SkillFired | sub_393634B0 | SkillMgr OnSkillFired(...) | yes | absent | absent |
| 0x002D | SkillEnded | sub_39363510 | SkillMgr OnSkillEnded(id, result a2+24) | yes | absent | absent |
| 0x002E | SkillStopped | sub_39363530 | SkillMgr OnSkillStopped(id, reason) | yes | absent | absent |
| 0x002F | CastingStopped | sub_39363550 | SkillMgr OnCastingStopped(4 x u32) | yes | absent | absent |
| 0x0030 | UnitDamaged | sub_39363580 | Combat OnUnitDamaged(damage record at +16) | yes | absent | absent |
| 0x0031 | UnitHealed | sub_393635B0 | Combat OnUnitHealed(heal record +16, flag +280) | yes | absent | absent |
| 0x0033 | KnockBackUnit | sub_393635D0 | unit KnockBack(vec at +20) | yes | absent | absent |
| 0x0032 | BlinkUnit | sub_39369B10 | if local owner: compute blink delta (pos a2+24) vs target unit and apply BlinkUnit | yes | absent | absent |
| 0x0034 | ImpulseUnit | sub_39363600 | if physics supports impulse: unit ApplyImpulse(a2+16) | yes | absent | absent |
| 0x0035 | UnitInvisible | sub_39363660 | unit SetInvisible(byte +20) | yes | absent | absent |
| 0x0036 | UnitFlyingStateChanged | sub_393636A0 | unit SetFlyingState(byte +20) | yes | absent | absent |
| 0x0037 | RequestUnitFlyingState | sub_3936EFD0 | if unit flying state != requested and change allowed -> replies ZWChangeUnitFlyState(unit,state) | yes | absent | absent |
| 0x0039 | SkillCooldownReset | sub_39364D00 | unit cooldown tracker: reset skill/group cooldowns (+20,+24, flags +28..) | yes | absent | absent |
| 0x003A | PlotEvent | sub_393636D0 | PlotMgr OnPlotEvent (caster/target/skill/event data +32..+1021) | yes | absent | absent |
| 0x003B | PlotEnded | sub_39363760 | PlotMgr OnPlotEnded(id) | yes | absent | absent |
| 0x003C | PlotCastingStopped | sub_39363780 | PlotMgr OnPlotCastingStopped(id,..,byte) | yes | absent | absent |
| 0x003D | PlotChannelingStopped | sub_393637B0 | PlotMgr OnPlotChannelingStopped(id,..,byte) | yes | absent | absent |
| 0x003E | BuffCreated | sub_39364D90 | unit BuffMgr.Add(buff data +72, index, caster...) | yes | BuffCreated | ZoneSession:509 |
| 0x003F | BuffRemoved | sub_39364DE0 | unit BuffMgr.Remove(index +20) | yes | BuffRemoved | ZoneSession:515 |
| 0x0040 | BuffUpdated | sub_39364E20 | unit BuffMgr.Update(index,stack,charge,elapsed,reason) | yes | BuffUpdated | ZoneSession:512 |
| 0x0042 | ActivateNpcSpawnersInArea | sub_39363850 | NpcMgr ActivateSpawnersInArea(center vec3 +16, radius +28, flag +32) | yes | absent | absent |
| 0x0044 | UpdateAggro | sub_39363890 | find 3 units; apply aggro Hostile/Friendly (byte +32) to NPC aggro table | yes | AggroUpdate | ZoneSession:398 |
| 0x0045 | AggroReset | sub_3936E200 | NpcMgr lookup npc; reset aggro by selectors | yes | AggroReset | ZoneSession:401 |
| 0x0046 | FakeDeath | sub_3936F060 | unit feign death: iterates abusers, clears from their aggro | yes | FakeDeath | ZoneSession:407 |
| 0x0047 | AggroCopy | sub_39364EF0 | copy aggro table src->dst NPC | yes | AggroCopy | ZoneSession:404 |
| 0x0048 | InteractNPC | sub_39363940 | NpcMgr OnInteractNpc(npcId +20) | yes | NpcInteractionRequest | ZoneSession:410 |
| 0x0049 | InteractNPCEnd | sub_39363960 | NpcMgr OnInteractNpcEnd(npcId +20) | yes | NpcInteractionEnded | ZoneSession:411 |
| 0x004A | SlaveMasterChanged | sub_39364F60 | slave unit master change (ids +24/+32); log 'can't find slave unit' | yes | absent | absent |
| 0x004B | ShipControlChange | sub_39363980 | logs 'ship control packet: get/lost control'; ShipMgr control change(unit,bool) | yes | absent | absent |
| 0x004C | EscapeSlave | sub_3936F280 | unit EscapeSlave(pos a2+24, yaw +48); physics notify | yes | absent (ZoneUnit.cs:21 has only the +A59C field comment) | absent |
| 0x004D | HouseBuildDone | sub_3936E290 | HouseMgr finds house -> BuildDone | yes | absent | absent |
| 0x004E | HouseBuildProgress | sub_3936E340 | HouseMgr finds house -> BuildProgress | yes | absent | absent |
| 0x004F | GmCommand | sub_393639D0 | parses text (<=0x400) as Data; switch(command type +20) 2/3/... -> unit GM handlers | yes | GmCommand/GmCommandData | ZoneSession:326 Registry.ApplyGmCommand |
| 0x0050 | AttackOnQuest | sub_3936E3F0 | NpcMgr: find npc, set quest attack target | yes | absent | absent |
| 0x0051 | FollowUnitOnQuest | sub_3936E4C0 | NpcMgr: npc follow unit (quest) | yes | absent | absent |
| 0x0052 | FollowPathOnQuest | sub_3936E550 | NpcMgr: npc follow path (quest) | yes | absent | absent |
| 0x0053 | RunCommandSetOnQuest | sub_3936E610 | NpcMgr: npc run command set (quest) | yes | absent | absent |
| 0x0054 | TimeOfDay | sub_39363BF0 | TimeOfDay set hour (float +16) | yes | ZoneTimeOfDay | Startup:105 / ZoneSession:530 |
| 0x0055 | DetailedTimeOfDay | sub_39363C10 | TimeOfDay set detailed (hour,speed,start,end) | yes | ZoneDetailedTimeOfDay | Startup:110 / ZoneSession:531 |
| 0x0056 | SandboxOnlineHeightmapAction | sub_39363C40 | logs; forwards to SandboxOnline (GameServer+344) heightmap action, else error 'no sandbox online exists' | yes | absent | absent |
| 0x0057 | SandboxOnlineUndo | sub_39363CB0 | SandboxOnline undo | yes | absent | absent |
| 0x0058 | SandboxOnlinePlayerPos | sub_39363D20 | SandboxOnline player pos | yes | absent | absent |
| 0x0059 | GimmickReloadStatics | sub_39363D90 | GimmickMgr reload statics | yes | absent | absent |
| 0x005A | GimmickCreated | sub_39363DB0 | GimmickMgr create gimmick (struct +16, id +224) | yes | absent | absent |
| 0x005B | GimmickRemoved | sub_39363DE0 | GimmickMgr remove(id) | yes | absent | absent |
| 0x005C | GimmickMovement | sub_39363E00 | GimmickMgr movement(id, state +24) | yes | absent | absent |
| 0x005D | GimmickGrasped | sub_39363E30 | GimmickMgr grasped(id, by, flag) | yes | absent | absent |
| 0x005E | GimmickSetMovement | sub_39363E60 | GimmickMgr set movement | yes | absent | absent |
| 0x005F | PhysicalExplosion | sub_39363E90 | GimmickMgr physical explosion(id, pos, params) | yes | absent | absent |
| 0x0060 | DominionData | sub_39363EE0 | DominionMgr set dominion data (+16) | yes | DominionData | ZoneSession:329 -> ZoneSiegeState |
| 0x0061 | DominionDeleted | sub_39363F00 | DominionMgr delete (empty body) | yes | DominionDeleted | ZoneSession:330 |
| 0x0062 | SiegeState | sub_39363F20 | SiegeMgr set siege state (rules, status, owner faction +3792) | yes | SiegeState | ZoneSession:331 |
| 0x0063 | SiegeMember | sub_39363F50 | SiegeMgr member add/remove (faction, char, bool) | yes | SiegeMember | ZoneSession:332 |
| 0x0064 | SiegeSecondHalf | sub_39363F80 | SiegeMgr second-half flag | yes | SiegeSecondHalf | ZoneSession:333 |
| 0x0065 | TowerDefReload | sub_39363FA0 | TowerDef reload | yes | absent | absent |
| 0x0066 | TowerDefQueryPlayability | sub_39363FC0 | TowerDef query playability (replies ZW report) | yes | absent | absent |
| 0x0067 | TowerDefStart | sub_39363FE0 | TowerDef start | yes | absent | absent |
| 0x0068 | TowerDefEnd | sub_39364000 | TowerDef end | yes | absent | absent |
| 0x0069 | TowerDefWaveStart | sub_39364020 | TowerDef wave start | yes | absent | absent |
| 0x006A | GameScheduleStart | sub_39364050 | GameSchedule start(id) | yes | GameScheduleMessage | ZoneSession:381 -> GameSchedules?.Handle |
| 0x006B | GameScheduleContinue | sub_39364070 | GameSchedule continue(id) | yes | GameScheduleMessage | ZoneSession:382 |
| 0x006C | GameScheduleEnd | sub_39364090 | GameSchedule end(id) | yes | GameScheduleMessage | ZoneSession:383 |
| 0x006D | GameActivityStart | sub_393640B0 | logs 'zone: activity %d started for server %d' only (no-op) | yes | absent | absent |
| 0x006E | GameActivityEnd | sub_393640E0 | logs 'zone: activity %d ended for server %d' only (no-op) | yes | absent | absent |
| 0x006F | NpcControl | sub_39364110 | NpcMgr NpcControl(3 x u32: npc, unit, mode) | yes | absent | absent |
| 0x0070 |  | sub_39364140 | find spawner by id; spawner->OnEvent(msg); log 'Can't find spawner(%u)' | yes | NpcSpawnerEventMessage | ZoneSession:378 |
| 0x0071 | RequestCombatUnits | sub_39369EC0 | find unit; replies ZWResponseCombatUnits with up to 5 combat-unit ids (+faction) | yes | absent | absent |
| 0x0072 | NpcBanned | sub_3936E6A0 | add/remove npc id (+16) in NpcMgr banned set (flag +20) | yes | absent | absent |
| 0x0073 | CreateDoodad | sub_393641A0 | DoodadMgr create doodad from body (+16) | yes | absent | absent |
| 0x0074 | RemoveDoodad | sub_393641D0 | DoodadMgr remove(id) | yes | absent | absent |
| 0x0075 | DoodadChangePhase | sub_393641F0 | DoodadMgr change phase(id, phase) | yes | absent | absent |
| 0x0076 | VegetationCutdown | sub_39364220 | DoodadMgr vegetation cutdown(id) | yes | absent | absent |
| 0x0077 | RequestZoneStatus | sub_39365090 | gathers zone status (counts), replies ZWReportZoneStatus | yes | absent (no ReportZoneStatus opcode in ZwOpcodes.cs either) | absent |
| 0x0078 | PhysicsCPU | sub_393651A0 | sets cvar sys_physics_CPU to value (+16) | yes | absent | absent |
| 0x007A | RayCasting | sub_3936F340 | raycast request: runs world ray query, replies ZW result | yes | absent | absent |
| 0x007B | AiDebug | sub_39364240 | AI debug dispatch (AiDebug singleton) | yes | absent | absent |
| 0x007C | BuffLearned | sub_39365220 | unit learns passive buff (+20) if template valid; refresh stats | yes | BuffLearned | ZoneSession:518 |
| 0x007D | SkillsReset | sub_39365280 | unit resets skills for ability (+20); refresh stats | yes | SkillsReset | ZoneSession:521 |
| 0x007E | LevelChanged | sub_39364270 | unit SetLevel(byte +20) | yes | LevelChanged | ZoneSession:527 |
| 0x007F | DropBackpack | sub_393642B0 | spawn dropped backpack (owner, item data, pos...) | yes | absent | absent |
| 0x0080 | CheckMoleMiner | sub_39364320 | MoleMgr check miner at pos (-> ZWReportMoleMiner) | yes | absent | absent |
| 0x0081 | CheckMoleTrader | sub_39364350 | MoleMgr check trader at pos (-> ZW report) | yes | absent | absent |
| 0x0082 | ZombieCharacter | sub_39364380 | physics: make character zombie (id +16) | yes | absent | absent |
| 0x0084 | ConflictZoneState | sub_393643A0 | ConflictMgr conflict zone state (zoneGroup u16 +16, state +20) | yes | ConflictZoneState | ZoneSession:337 |
| 0x0085 | WorldGameTime | sub_393643E0 | sets server game time from u32 (+16) via GameTime service | yes | (none; raw via Startup) | Startup:115 / ZoneSession:532 |
| 0x0086 | SkillCooldownReduce | sub_393652F0 | unit cooldown tracker: reduce cooldowns by amount | yes | absent | absent |
| 0x0087 | AddSwapPassiveBuffs | sub_39365390 | unit adds swap passive buff (+20); refresh stats | yes | AddSwapPassiveBuffs | ZoneSession:524 |
| 0x0088 | ChangeChargeSkillCooldown | sub_393653F0 | unit charge-skill cooldown change (skill +20, amount) | yes | absent | absent |
| 0x0089 | AttackFaction | sub_39365450 | unit AttackFaction bitmask (8 bits at +20 -> bytes +32956..) | yes | absent (ZoneUnit.AttackFactionFlags exists, set only via snapshot/engage) | absent |
| 0x008A | FactionImmigrateNpcSpawnerCondition | sub_3936E940 | iterates NpcMgr spawner lists evaluating faction-immigration conditions (293 lines) | yes | absent | absent |

Summary count: 133 registered; 56 decoded+dispatched; 77 absent (0 missing from WzOpcodes.cs).

## Tasks

### WZ-01: Make WZ dispatch non-fatal and native-ordered for the first-entry path
- Status: **Partial** — per-packet containment, per-opcode fault counters, pre-load gate for Npc/Schedule/SpawnerEvent, tests; "unit id too big" check not ported: max id sub_39258650 unrecovered
- Priority: P0
- Native evidence: every handler registered by `sub_3936F740` returns 1 and never throws; e.g. `sub_39362D40` (NpcState) logs "OnNpcState: level is not loaded yet!" and drops; `sub_39364A80` (UnitRemoved) returns on unknown id; `sub_3936B3C0` (UnitState) logs "unit id from world server is too big" / "duplicate unit state" and returns; `sub_393648D0` logs "world server refused joining".
- Repo state: `ZoneSession.ReceiveCore` (src/AAEmu.Zone/WorldLink/ZoneSession.cs:322-546) throws `NotSupportedException` when `RelationshipWorld` is null (Siege/Conflict, lines 334/339) or NPC simulation is not configured (`RequireNpcs`, line 548); UnitState already contains containment for named admission refusals (lines 441-463) but other opcodes do not. Comment at 443-447 records that one refusal previously retired the whole zone.
- Work:
  - Wrap per-opcode handling so a malformed/unsupported body or missing service logs and counts (like native) instead of faulting the session; keep the existing unimplemented counter.
  - Gate NpcState / GameSchedule / spawner-event handling on the level-loaded flag exactly as native (log + drop before load).
  - Add duplicate/too-large unit id checks matching `sub_3936B3C0`; make UnitRemoved ignore unknown ids (already a no-op via registry; verify).
  - Add a test that feeds each of the 77 unimplemented opcodes plus malformed bodies and asserts the session survives.
- Depends on: none
- Confidence: high

### WZ-02: Verify the end-to-end first-entry WZ sequence against native handlers
- Priority: P0
- Native evidence: JoinResponse `sub_393648D0`; FactionList `sub_393631E0` ("faction: all factions are loaded"); FactionRelationList `sub_39363220` (sets join flag bit 1, "received all the factions and inter-factions from world_server"); SpawnerList `sub_39362DF0` (Last flag sets join flag bit 2); TimeOfDay `sub_39363BF0`, DetailedTimeOfDay `sub_39363C10`, WorldGameTime `sub_393643E0`; UnitState `sub_3936B560`; UnitMovement `sub_39362E80`; UnitRemoved `sub_39364A80`.
- Repo state: all decoded and dispatched (ZoneStartupState.cs:75-115; ZoneSession.cs:387-478). Join flag bits 1/2 (`a1+136`) readiness semantics are modeled in ZoneStartupState but not confirmed against `sub_393648D0`/`sub_39363220` here; UnitMovement skips the native `ServerFlag==0 || moveType==5` gate (`qword_3D8B0EE0+336`) and the `sub_3935C6E0` "unit is controlled locally" skip.
- Work:
  - Confirm the readiness gate (flag bits 1|2 plus level loaded) before ZWZoneLoaded matches native, and that UnitState for a Character is accepted only after it.
  - Port the movement gate: ignore WZUnitMovement when the server-authoritative flag is set unless Kind==5, and skip units for which `sub_3935C6E0` is true.
  - Port the post-movement physics-system notify (`sub_3921E300`) once the scene exposes it.
  - Record a golden capture of the real World's login sequence (opcode order) as a test fixture.
- Depends on: framework/scene areas for movement admission
- Confidence: medium

### WZ-03: Implement the unit attach/hang/bond/posture/skill-controller family
- Priority: P1
- Native evidence: UnitAttached `sub_39362F90` (child AttachTo parent, point), UnitDetached `sub_39362FF0`, UnitHung `sub_39363040`, UnitUnhung `sub_393630A0`, UnitBondToDoodad `sub_39363100`, UnitUnbondFromDoodad `sub_39363160`, UnitModelPostureChanged `sub_393631B0`, SkillControllerState `sub_39362F00` (unit->SkillController->ApplyState), CreateSkillController `sub_39362F50`. Opcodes 0x0A-0x12.
- Repo state: absent as packets; only snapshot sub-records exist (`UnitAttachment`, `UnitBonding`, `UnitPosture` in src/AAEmu.Zone.Protocol/Units/UnitRelations.cs). No `Read` for the packet bodies; no ZoneUnit attach/hang/bond mutators found.
- Work:
  - Decode each body (ids at +16/+20; reuse UnitAttachment/UnitBonding/UnitPosture readers).
  - Add ZoneUnit methods (Attach/Detach/Hang/Unhang/Bond/Unbond/SetPosture) and dispatch in `ReceiveCore`.
  - SkillController create/state: bind to the existing `SkillControllerType` handling in `UnitMovementData` / ZoneActorMovementController.
  - Log "can't find child/unit" and continue, as native.
- Depends on: WZ-01; movement area (parent-relative movement), slave/vehicle area
- Confidence: medium

### WZ-04: Skill / plot / combat-feedback packets
- Status: **Partial** — decoded and dispatched (`SkillMessages.cs`, `ZoneSkillState.cs`, `ZoneSession.DispatchCore`): SkillStarted 0x2B, SkillFired 0x2C, SkillEnded 0x2D, SkillStopped 0x2E, CastingStopped 0x2F, UnitDamaged 0x30 (layout from World writer; absent from the native capture), UnitHealed 0x31, PlotEvent 0x3A, PlotEnded 0x3B, PlotCastingStopped 0x3C, PlotChannelingStopped 0x3D. State only: active timelines, per-unit cast (timeline/skill/casting flag, mirrors native +32280/+32297 clears in 39456600/39456630), plot timelines, last heal/damage per target; unknown units are counted. The native zone sent no dedicated ZW reply to any of these (capture check). Not done: skill controllers/cooldown start/AutoAttack side effects of StartSkill 39456D70/FireSkill 39457170/EndSkill 39456B20, NPC aggro from heal/damage (394576B0, 393225F0), plot effect execution, cooldown packets 0x39/0x86/0x88; UnitDamaged debug block (flag 0x10) is rejected; position-typed targets are skipped, not retained.
- Priority: P1
- Native evidence: SkillStarted `sub_39363460`, SkillFired `sub_393634B0`, SkillEnded `sub_39363510`, SkillStopped `sub_39363530`, CastingStopped `sub_39363550`, UnitDamaged `sub_39363580`, UnitHealed `sub_393635B0`, PlotEvent `sub_393636D0` (body up to +1021), PlotEnded `sub_39363760`, PlotCastingStopped `sub_39363780`, PlotChannelingStopped `sub_393637B0`, SkillCooldownReset `sub_39364D00`, SkillCooldownReduce `sub_393652F0`, ChangeChargeSkillCooldown `sub_393653F0`. Opcodes 0x2B-0x31, 0x39-0x3D, 0x86, 0x88.
- Repo state: absent (no decoder, no dispatch). Related `UnitCastAction` (src/AAEmu.Zone.Protocol/Units/UnitCastAction.cs) and UnitSkillCaster records exist for snapshots only; ZoneSimulationTick references plot but no WZ plot handling.
- Work:
  - Decode the skill-cast structure (reuse UnitCastAction variants Skill/Plot/Buff/...) for Started/Fired; Ended/Stopped/CastingStopped are small.
  - Implement SkillMgr state (active casts keyed by unit) and feed Lua/AI (`ZoneAiScriptConstants` already references plot).
  - Damage/heal records feed unit health notifications (no health change; UnitPoints carries that).
  - Cooldown tracker per unit (reset by skill/group, reduce by amount, charge-skill change).
- Depends on: skills/buff area, Lua scripting
- Confidence: medium (body layouts need a closer read of each `sub_` target)

### WZ-05: Forced movement / physics unit packets
- Status: **Partial** — UnitFlyingStateChanged 0x0036 done (body `bc objId, u8 flying`; `UnitFlyingStateChanged` in SpawnerAreaActivation.cs; handler 393636A0 -> 39258C90 stores the byte at unit+0x8009, then calls physics-actor vfunc +296; we set `ZoneUnit.IsFlying`, unknown unit counted-and-ignored, the actor notification is not modelled; no unit-level test because building a `UnitStateSnapshot` fixture is heavy). RequestUnitFlyingState 0x0037 and the other packets in this task are not done (0x0037 needs the ZW ChangeUnitFlyState writer and the unidentified 3933B710 predicate).
- Priority: P1
- Native evidence: OnUnitBlinked `sub_39363420`, BlinkUnit `sub_39369B10`, KnockBackUnit `sub_393635D0`, ImpulseUnit `sub_39363600` (gated by physics vfunc+88), UnitInvisible `sub_39363660`, UnitFlyingStateChanged `sub_393636A0`, RequestUnitFlyingState `sub_3936EFD0` (replies ZWChangeUnitFlyState, opcode 0x1C present in ZwOpcodes.cs), ZombieCharacter `sub_39364380`, PhysicalExplosion `sub_39363E90`, EscapeSlave `sub_3936F280`.
- Repo state: absent. `ZoneUnit.cs:21` has a comment for the +A59C field set by EscapeSlave; `IZonePhysicsMotion` / ZoneLocalToWorldMotionAdapter exist and expose impulse; `ZwOpcodes.ChangeUnitFlyState` exists but is unused.
- Work:
  - Decode bodies (unit id at +16; vec3/quaternion params).
  - Map KnockBack/Impulse/Blink onto the existing physics motion adapter; implement invisible and flying state setters on ZoneUnit.
  - RequestUnitFlyingState: emit ZW ChangeUnitFlyState when the state differs and the change is allowed.
- Depends on: physics/native-engine area, ZW outbound area
- Confidence: medium

### WZ-06: NPC / spawner control packets
- Status: **Partial** — ActivateNpcSpawnersInArea 0x0042 done (17 bytes: f32 x,y,z, f32 radius, u8 activate). Native 3933E7E0 flips the Active byte (+355) of every spawner that is not TowerDef-controlled (+353) or schedule-controlled (+354) and has an authored point strictly inside the XY circle (Z ignored); it spawns nothing itself, ZW SpawnNpc follows from the normal scheduler update. Implemented as `ZoneNpcSpawnerFactory.ActivateInArea` (reached via `ZoneSession.Spawners`, null without a scene) writing `Scheduler.Active`. Other packets in this task are not done.
- Priority: P1
- Native evidence: NpcStartDespawn `sub_39364A10` (AI event "GO_TO_DESPAWN"), ActivateNpcSpawnersInArea `sub_39363850` -> `sub_3933E7E0(center, radius, flag)`, NpcControl `sub_39364110`, NpcBanned `sub_3936E6A0`, RequestCombatUnits `sub_39369EC0` (replies ZWResponseCombatUnits 0x2D), AttackOnQuest `sub_3936E3F0`, FollowUnitOnQuest `sub_3936E4C0`, FollowPathOnQuest `sub_3936E550`, RunCommandSetOnQuest `sub_3936E610`, FactionImmigrateNpcSpawnerCondition `sub_3936E940`, AiDebug `sub_39364240`.
- Repo state: NpcState/NpcSpawnFailed/NpcSpawnerEvent/Aggro*/Interact are done (ZoneSession.cs:372-413); the rest absent. `NpcRemoval` (Protocol/Messages/NpcNotifications.cs) is a different (outbound) message, not NpcStartDespawn. Spawner machinery exists (ZoneNpcSpawnerFactory, ZoneNpcSpawnerEvents).
- Work:
  - Add decoders and `ZoneNpcSimulation` entry points: StartDespawn (fire AI event GO_TO_DESPAWN), ActivateSpawnersInArea (radius query of spawners; important so spawners near a joining player activate), NpcControl, Banned set.
  - Quest NPC commands: attack/follow unit/follow path/run-command-set via the existing AI command layer.
  - RequestCombatUnits reply (needs ZW writer; ResponseCombatUnits constant exists).
  - FactionImmigrate condition evaluation across spawner lists (large native routine, 293 lines).
- Depends on: NPC/AI areas, ZW outbound
- Confidence: medium

### WZ-07: Doodad, house and vegetation packets
- Status: **Partial** — WZ 0x0073-0x0076 (CreateDoodad/RemoveDoodad/DoodadChangePhase/VegetationCutdown) are decoded (`DoodadMessages.cs`), tracked in `ZoneDoodadRegistry` and dispatched in `ZoneSession`; verified against 4536 native-baseline create bodies. House packets, DropBackpack and the doodad physics remain open.
- Priority: P1
- Native evidence: CreateDoodad `sub_393641A0`, RemoveDoodad `sub_393641D0`, DoodadChangePhase `sub_393641F0`, VegetationCutdown `sub_39364220` (all call the doodad manager `qword_3A607A80`); HouseState `sub_39362E50` (house mgr at `A40+16`), HouseBuildDone `sub_3936E290`, HouseBuildProgress `sub_3936E340`, DropBackpack `sub_393642B0`.
- Repo state: absent. Content readers for doodads/houses may exist in Content/ but no WZ decode/dispatch (grep for CreateDoodad/HouseState finds nothing).
- Work:
  - Decode doodad create (full doodad state struct at +16 incl. phase/owner/growth), remove, phase change, vegetation id.
  - Add doodad registry and house registry (state, build progress/done) to the scene; houses are also created via UnitState owner-type 3, so share the model.
  - DropBackpack: spawn the backpack doodad/item for the owner position.
- Depends on: scene/doodad area
- Confidence: medium

### WZ-08: Slave / vehicle / ship packets
- Priority: P1
- Native evidence: SlaveMasterChanged `sub_39364F60` ("can't find slave unit; slave id=%u"), ShipControlChange `sub_39363980` ("ship control packet : get/lost control"), EscapeSlave `sub_3936F280`, plus UnitAttached/Bond (WZ-03).
- Repo state: absent. Movement already decodes Vehicle/Ship/Transfer movement kinds (Protocol/Messages/UnitMovement.cs:65-70).
- Work:
  - Decode master change (slave id +16, master data +24..+32) and ship control (unit +16, byte +20).
  - Implement slave master/driver state on ZoneUnit and ship control ownership toggling movement authority.
  - EscapeSlave: teleport/eject using position/yaw then physics notify.
- Depends on: WZ-03, WZ-05, vehicles area
- Confidence: medium

### WZ-09: Gimmick packets
- Priority: P2
- Native evidence: GimmickReloadStatics `sub_39363D90`, GimmickCreated `sub_39363DB0`, GimmickRemoved `sub_39363DE0`, GimmickMovement `sub_39363E00`, GimmickGrasped `sub_39363E30`, GimmickSetMovement `sub_39363E60` (gimmick manager `qword_3A607A78`).
- Repo state: absent as packets (ZoneActorMovement* classes mention gimmick in comments only).
- Work: decode six bodies; add GimmickRegistry (id, template, transform, movement state, grasp owner); expose to scene physics.
- Depends on: physics/scene
- Confidence: medium

### WZ-10: Siege/conflict extras and TowerDef
- Status: **Partial** — TowerDef done as state + playability: five packets decoded (`TowerDefMessages.cs`), dispatched in `ZoneSession.DispatchCore`, `ZoneTowerDefController` answers QueryPlayability per native `393591D0` (reply only when the tower has a phase, a nonzero primary spawner type and >=1 physical spawner of that type in this zone, and is not already running; so zone 129 stays silent as the native capture shows). Start/End/WaveStart keep running-set and wave state but do not yet fire the native spawner event (type 5) into the spot's spawner. GameActivity and AttackFaction not done.
- Priority: P2
- Native evidence: TowerDefReload `sub_39363FA0`, TowerDefQueryPlayability `sub_39363FC0` (replies ZW TowerDefReportPlayability 0x32), TowerDefStart `sub_39363FE0`, TowerDefEnd `sub_39364000`, TowerDefWaveStart `sub_39364020`, GameActivityStart/End `sub_393640B0`/`sub_393640E0` (log only), AttackFaction `sub_39365450`.
- Repo state: Dominion/Siege/ConflictZone are done (ZoneSiegeState.cs). TowerDef packets absent (only spawner-controller content refs). GameActivity absent (native no-op log, trivially add). `ZoneUnit.AttackFactionFlags` exists (ZoneUnit.cs:25) but WZ AttackFaction does not set it.
- Work:
  - TowerDef: decode 5 packets, implement manager and playability reply.
  - GameActivityStart/End: decode and log, no state.
  - AttackFaction: set the 8 flag bytes at +32956 from bitmask byte +20 (note native only sets bits that are 1).
- Depends on: NPC spawner controller, ZW outbound
- Confidence: medium

### WZ-11: GM, command and diagnostics packets
- Priority: P1 (RunCommand, RequestZoneStatus) / P2 (rest)
- Native evidence: RunCommand `sub_39366650` (console exec, replies ZWCommandResponse 0x01), RequestZoneStatus `sub_39365090` (replies ZWReportZoneStatus), PhysicsCPU `sub_393651A0` (cvar `sys_physics_CPU`), RayCasting `sub_3936F340`, AiDebug `sub_39364240`, SandboxOnlineHeightmapAction/Undo/PlayerPos `sub_39363C40/CB0/D20` (error "no sandbox online exists" when absent), CheckMoleMiner `sub_39364320`, CheckMoleTrader `sub_39364350` (ZwOpcodes.ReportMoleMiner exists).
- Repo state: absent. GmCommand (0x4F) is done. `ZwOpcodes.CommandResponse` exists; no ReportZoneStatus opcode defined in ZwOpcodes.cs (gap in ZW area).
- Work:
  - RunCommand: route to the existing console/cvar surface and send CommandResponse(id, text up to 512).
  - RequestZoneStatus: gather loaded-cell / unit counts, define the ZW opcode and send ReportZoneStatus (World may poll this for health).
  - PhysicsCPU: set cvar; RayCasting: world raycast with reply; AiDebug passthrough; Sandbox*: log + error reply (non-sandbox build); MoleMiner/Trader checks.
- Depends on: ZW outbound area, cvar area
- Confidence: medium

### WZ-12: Complete SetFaction/body-fidelity checks of already-dispatched handlers
- Priority: P2
- Native evidence: UnitFactionChanged `sub_3936E0E0` (applies only if `old==current`, then character UI refresh), UnitExpeditionChanged `sub_39364BC0` (same guard + event), TargetChanged `sub_3936E170` (owner-UI notify unless ForceByWorld), UnitPoints `sub_39364C30` (error log on health>0 for dead unit), UnitResurrection `sub_39369A60` (world to local coords via `a1+152`, yaw to quaternion), CombatCleared `sub_39364E70`, BuffLearned/SkillsReset/AddSwapPassive (`sub_39365220/280/390`: trigger stat recompute via `v3+7504` event 290), LevelChanged `sub_39364270`.
- Repo state: handlers exist in ZoneUnitRegistry (ChangeFaction line 382, ChangeTarget 338, UpdatePoints 286, Resurrect 305, LearnBuff 559, ResetSkills 570, AddSwapPassives 578, ChangeLevel 588) but equivalence of side effects (old==current guard, stat-recompute event 290, owner-UI notifications, world-offset conversion `a1+152`) has not been audited.
- Work: diff each repo handler against the cited native routine and add a unit test per behavior (guards, side-effect events, coordinate conversion).
- Depends on: none
- Confidence: low-medium

### WZ-13: Add a generated WZ conformance table test
- Priority: P2
- Native evidence: registration function `sub_3936F740` order (133 registrars; opcode = slot index = (offset/8)-1).
- Repo state: `WzOpcodes.cs` constants match native set; no test asserts coverage or name parity (names like `UnitEquipmentsRndAttrUnitModifierAvtivateChanged` preserve the native typo).
- Work: add a test that lists every WzOpcodes constant and asserts it is either handled by `ReceiveCore`/Startup or listed in an explicit "not yet implemented" allowlist (current 77), so the allowlist shrinks monotonically; document the 6 empty slots (0x1D, 0x38, 0x41, 0x43, 0x79, 0x83).
- Depends on: none
- Confidence: high

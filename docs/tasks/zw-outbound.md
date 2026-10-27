<!-- Audit of zw-outbound: native x2game-dev_dedicate.dll (ArcheAge 10.0.2.13 r575) vs this repo at 76e439f. Paths rewritten to the src/ layout; line numbers refer to 76e439f. -->

## Zone->World packets and the zone's other network streams (ZW)

Coverage: the native DLL has 58 `ZW*` RTTI classes (57 packets/actions plus `ZWUnitMovementsBuffer`) and `WZWorldGameTimePacket`. The ZW opcode is the integer stored at object offset +8 right after the vftable assignment in each sender (for example ZWHeartbeat = 60 at 0x39332510, ZWZoneLoaded = 31 at 0x39332730), so the opcodes below were read from native constructor sites. The 45 opcodes in `src/AAEmu.Zone.Protocol/ZwOpcodes.cs` that I cross-checked match native values. 13 native ZW packets have NO opcode constant: SkillControllerState 9, ClearCombat 12, StartSkill 24, StopSkill 25, StopCasting 26, LongDistSkillControllerCreated 27, SandboxOnlineHeightmapAction 32, SandboxOnlinePlayerPos 34, ReportZoneStatus 47, KillNpc 49, AiDebug 51, DumpNpc 64, AggroRemove 65. Only about 20 constants have a message serializer, and only about 17 packets are emitted live (UnitMovements, ZoneLoaded, Time/DetailedTimeOfDay, SpawnNpc, RemoveNpc, SaveInstanceSpawner, NpcSaid, ChangeTarget, Create/RemoveBuff, the aggro/abuser family, NPC interaction ended/status). Rough coverage: framing and join about 90%, ZW emission about 30%. Biggest gaps: ZWHeartbeat is never sent (serializer exists, unused), ZWUnitLocation is never sent (and repo mislabels native 0x3935E7E0 as a spatial cache), no area/skill/gimmick/hack-report ZWs, and none of the World->Zone requests that expect a ZW reply (zone status, combat units, raycast, mole checks, run command) are handled (they fall into the "unimplemented opcode" counter in `ZoneSession.cs`).

Framing/session: `ZoneFrame.cs` (u16 LE length including the opcode, u16 opcode, body) matches native; no encryption or compression was found on the Zone<->World path, and the repo framing was live-tested against World (comment in `ZoneSession.cs:33`). The join body (0x39368A00 serializer, 0x39366760 sender) is right, but the live join in `Program.cs (pre-restructure):159` sends ip=0, port=0, account=0, instance=0, dev=false, whereas native fills `sv_port`, `vpn_external_ip` and, by server type, `cl_account_id` (type 1) or `instance_id` (type 2). Native has no zone-side reconnect: on disconnect (0x3936D7E0, "zone: client disconnected (reason=%d)") server types 2 and 3 run console command `quit`; type 1 stays alive. Join refusal (handler 0x393648D0) logs "zone: world server refused joining(%s)" using reason table off_3A51AB48 (reasons 0..3). The CT*/TC*/StreamClient families are client-only.

### ZW packet table (opcode read from native constructor site)

| ZW packet | Op | Native sender(s) / trigger | Repo opcode const | Repo serializer | Sent live |
|---|---|---|---|---|---|
| Join | 0x00 | 0x39366760 on TCP connect ("zone: client connected to world") | yes | src/AAEmu.Zone.Protocol/Messages/ZoneJoin.cs | yes (Program.cs (pre-restructure):159, partial fields) |
| CommandResponse | 0x01 | 0x39366650 after WZRunCommand ("command from world: %d %s") | yes | absent | no |
| SpawnNpc | 0x02 | 0x3933EDF0, 0x3933F5A0, 0x39341EF0, 0x39348AF0 (spawner HandleSpawn/SpawnForDev) | yes | src/AAEmu.Zone.Protocol/Messages/NpcSpawnMessages.cs | yes |
| RemoveNpc | 0x03 | 0x3933C3B0, 0x3933F880, 0x39345000, 0x39346050, 0x39348440, 0x39348940 | yes | src/AAEmu.Zone.Protocol/Messages/NpcNotifications.cs | yes |
| SaveInstanceSpawner | 0x05 | 0x39347F90 (persistent spawn) | yes | src/AAEmu.Zone.Protocol/Messages/SaveInstanceSpawner.cs | yes |
| NpcSaid | 0x06 | 0x39326B40 (signal bubble), 0x39352C40 (ScriptBindUnit::Say) | yes | src/AAEmu.Zone.Protocol/Messages/NpcSpeech.cs | yes (signals) |
| RemoveHouse | 0x07 | 0x3933AE70 | yes | absent | no |
| UnitMovements | 0x08 | ZWUnitMovementsPacket (vftable ref line 741198), movement buffers drained per frame | yes | inline in ZoneMovementOutgoing.cs:268 | wired in ZoneSceneHost.cs:294 (unproven live) |
| SkillControllerState | 0x09 | 0x392291B0, 0x39229280, 0x39229350 | ABSENT | absent | no |
| CreateBuff / RemoveBuff | 0x0A / 0x0B | 0x39268720, 0x39325AF0/0x39325B80, 0x39346050, 0x39347060, 0x39352F20/0x39353020 | yes | BuffCreationRequest / BuffRemovalRequest | partial (crowd buff only) |
| ClearCombat | 0x0C | 0x393534D0 (ScriptBindUnit::ClearCombat), 0x3935FAC0 | ABSENT | absent | no |
| MakeAggroTargetHostile | 0x0D | 0x3931F040 (NpcAggroTable::CreateAggro) | yes | NpcNotifications.cs | yes |
| UnitModelPostureChanged | 0x0F | 0x3935C760 | yes | absent | no |
| UnitFell | 0x10 | 0x3935C850 ("notify fall name (%s), impactVel(%.2f)") | yes | absent | no |
| UnitCollision / Result | 0x11 / 0x12 | 0x390AEC80 ("collisinManager::report impact"), 0x390AF3C0 | yes | absent | no |
| ErrorMsgToPlayer | 0x13 | 0x393657C0 | yes | absent | no |
| NpcInteractionEnded | 0x14 | 0x39343BC0 (OnTalkEnd) | yes | src/AAEmu.Zone.Protocol/Messages/NpcInteractions.cs | yes |
| ChangeTarget | 0x17 | 0x3935CCC0 | yes | NpcSpeech.cs (ZoneTargetChanged) | yes |
| StartSkill | 0x18 | 0x39227440 (anchor skill controller), 0x39326730 (ServerAIUnitSystem::UseSkill, "Sending skill(%u) request to world. Target(%u)") | ABSENT | absent | no |
| StopSkill / StopCasting | 0x19 / 0x1A | 0x39325940, 0x393259F0 (ServerAIUnitSystem::StopSkill/StopCasting) | ABSENT | absent | no |
| LongDistSkillControllerCreated | 0x1B | 0x3935DD20 | ABSENT | absent | no |
| ChangeUnitFlyState | 0x1C | 0x39325C20, 0x3936EFD0 | yes | absent | no |
| TimeOfDay / DetailedTimeOfDay | 0x1D / 0x1E | 0x39332400, 0x39332130, 0x393322F0 | yes | src/AAEmu.Zone.Protocol/Messages/ZoneTimeOfDay.cs | yes via report queue (ZoneSceneProviders.cs:926-937) |
| ZoneLoaded | 0x1F | 0x39332730 (empty body) | yes | inline | yes (ZoneSceneCoordinator.cs:181) |
| SandboxOnlineHeightmapAction / PlayerPos | 0x20 / 0x22 | 0x3932D1C0, 0x3932D170 | ABSENT | absent | no (dev tool) |
| EnterArea / LeaveArea | 0x23 / 0x24 | 0x3932B1B0, 0x3932B370 ("area enter/leave event received (event, player eid, area id, area eid)") | yes | absent | no |
| GimmickMovement | 0x25 | 0x39338A60 | yes | absent | no |
| RequestSpawnStaticGimmick | 0x26 | 0x393365A0 ("total %d static gimmicks ... requested for spawn to world server"), before ZoneLoaded | yes | absent | no (interface stub ZoneSceneCoordinator.cs:40) |
| GimmickJointBroken / ResetJoints / RemoveParts / Collided | 0x27 / 0x28 / 0x29 / 0x2A | 0x393377A0, 0x39337680, 0x39338370, 0x39338BF0 | yes | absent | no |
| NpcInteractionStatusChanged | 0x2C | 0x39344F80 | yes | NpcInteractions.cs | yes |
| ResponseCombatUnits | 0x2D | 0x39369EC0 (reply to WZRequestCombatUnits 0x71) | yes | absent | no |
| AggroTargetChanged | 0x2E | 0x3935CCC0 | yes | absent | no |
| ReportZoneStatus | 0x2F | 0x39365090 (reply to WZRequestZoneStatus 0x77) | ABSENT | absent | no |
| RayCastingResult | 0x30 | 0x3936F340 (reply to WZRayCasting 0x7A, uses RayWorldIntersection(Game)) | yes | absent | no |
| KillNpc | 0x31 | 0x3933EA90 | ABSENT | absent | no |
| TowerDefReportPlayability | 0x32 | 0x393591D0 (TowerDef::OnQueriedPlayability) | yes | absent | no |
| AiDebug | 0x33 | 0x3944E590 | ABSENT | absent | no |
| AiAggro | 0x34 | 0x3944DA20 (throttled to >=200 ms, 127-entry batch) | yes | absent | no |
| UnitLocation | 0x35 | 0x3935E7E0 (per frame from tick, caller at line 678449; unit list pos + cell*1024) | yes | absent | no |
| BackpackDropped | 0x36 | 0x3932B930 | yes | absent | no |
| ReportMoleMiner / MoleTrader | 0x37 / 0x38 | 0x39332C50 (reply to WZ CheckMole* 0x80/0x81, "ent_water ray check failed") | yes | absent | no |
| ReportMoveHack | 0x39 | 0x3921BD10, 0x3924B550, 0x39268720 | yes | absent | no |
| ReportDoodadSurfaceHack | 0x3A | 0x394510F0 | yes | absent | no |
| DoodadCollideUnit | 0x3B | 0x394547C0 | yes | absent | no |
| Heartbeat | 0x3C | 0x39332510 (every >=1.0 s, bool = cvar e_water_ocean_simulate_on_zone) | yes | src/AAEmu.Zone.Protocol/Messages/ZoneHeartbeat.cs | NO |
| RegisterNpcAbuser / UnregisterNpcAbusers / ClearNpcAbusers | 0x3D / 0x3E / 0x3F | 0x3931E180, 0x3931DA70, 0x3931DF00 | yes | NpcNotifications.cs | yes |
| DumpNpc | 0x40 | 0x39454400 (HandleGmSearchNpc) | ABSENT | absent | no |
| AggroRemove | 0x41 | 0x3931F790, 0x393210B0 (NpcAggroTable::RemoveFromTable) | ABSENT | absent | no |

### Other native packet families

| Family | Native evidence | Verdict | Repo |
|---|---|---|---|
| NpcSpawnerEventPacket | RTTI 0x3A51A638, vftable refs lines 739087/754708 (function 0x39390AE0), WZ 0x70, strings CmdNpcSpawnerEvent / OnNpcSpawnerEvent | World->Zone only; zone-relevant | handled (ZoneSession.cs:378) |
| StreamClient / TC* / CT* (CTJoin, CTRequestCell, CTUcc*, CTUploadEmblemStream, TCDoodadStream, TCDoodadIds, TCCharInfoQueried, TCHouseFarm, TCEmblem*, TCItemUcc*) | StreamClient vftable in 0x3987C750/0x3987C8C0 (client region), strings "stream: connected to world server", UI error `error_failed_to_connect_to_server`, `ClientUnit::InitClientOnlyUseStream`, `HousingManager::OnUnitModelStreamComplete`; the zone only constructs `ZoneClient` (vftable 0x3A67FF80) | Client->World stream connection; client-only leftovers in the shared DLL, not served by the zone process | not needed |
| CS*/SC* | thousands of client<->World packets | not on the zone wire | n/a |

First-entry requirements (player enters 129, walks, logs out): ZWHeartbeat (time sync), ZWZoneLoaded (done), ZWUnitMovements (wired, unproven live), ZWTimeOfDay/Detailed (done), ZWUnitLocation, ZWEnterArea/LeaveArea (area triggers while walking), ZWUnitFell and ZWErrorMsgToPlayer (walk/jump edge cases), and WZ UnitRemoved handling on logout (already in ZoneSession). Nothing zone-sent is needed for logout itself.

### Tasks

### ZW-01: Emit ZWHeartbeat once per second from the scene tick
- Status: **Done** — ZoneHeartbeatEmitter on the script clock, flag defaults false; queued from UpdateHeartbeat
- Priority: P0
- Native evidence: 0x39332510 sends ZWHeartbeat (opcode 0x3C, body one bool from cvar `e_water_ocean_simulate_on_zone`) when `now - last >= 1.0`; World answers with WZWorldGameTime (0x85).
- Repo state: `src/AAEmu.Zone.Protocol/Messages/ZoneHeartbeat.cs` and tests exist; `src/AAEmu.Zone/Scene/ZoneSceneProviders.cs:967 UpdateHeartbeat()` only calls `TimeOfDayState.Advance` and `_scriptClock.Advance`; no frame is queued anywhere.
- Work:
  - Keep a last-sent timestamp; when the delta is >= 1.0 s queue `new ZoneHeartbeat(flag).ToFrame()` through `_registry.QueueNotification`.
  - Take the flag from configuration (check `x2game.cvar.txt` for the default of `e_water_ocean_simulate_on_zone`).
  - Confirm WorldGameTime replies reach `Startup.Receive` (already routed, `ZoneSession.cs:530`).
  - Test: one frame per second, none below 1.0 s.
- Depends on: tick wiring (H1 in TODO.md)
- Confidence: high

### ZW-02: Send native-correct ZWJoin fields (sv_port, external IP, account/instance by server type)
- Status: **Partial** — instance id and sv_port sent; native server-type switch (account id / external IP) open
- Priority: P1
- Native evidence: 0x39366760 builds ZWJoin from cvars `sv_port`, `vpn_external_ip`, and by server type at `ZoneClient+140`: 1 uses `cl_account_id` (flag = nonzero), 2 uses `instance_id`, 3 neither; protocol 0x4B3 both ways; zone key from the delegate at `+144`; body serializer 0x39368A00.
- Repo state: `Program.cs (pre-restructure):159` sends `new ZoneJoin(1203,1203,zone.Key,0,0,0,0,false)`; `ZoneJoin.cs` already carries all fields.
- Work:
  - Add config/CLI for server type, port, external IP, account id, instance id, dev flag.
  - Pack the IPv4 as native 0x39364540 does.
  - Handle the join-refusal reason table (off_3A51AB48, reasons 0..3 clamped to 3) with a named error instead of the generic exception at `ZoneStartupState.cs:84`.
- Depends on: none
- Confidence: medium (instance semantics not tested against a live World)

### ZW-03: Match native disconnect/lifecycle policy
- Status: **Done** — disconnect + join-refusal named via ZoneWorldLifecycle; World EOF exits with code 3 for server type 2; refusal strings of off_3A51AB48 still positional
- Priority: P1
- Native evidence: 0x3936D7E0 "zone: client disconnected (reason=%d)": types 2/3 run console command "quit"; type 1 continues. Connect failure ("zone: client failed to connect to world", handled in 0x39366760): error for type 2, warning otherwise. No reconnect logic exists in the zone (the SC/CA reconnect packets are game-client).
- Repo state: `ZoneSession.ReceiveAsync` ends on EOF and retires the scene; `Program.cs (pre-restructure)` uses a 30 s connect deadline; no server-type distinction.
- Work:
  - Treat World EOF as a fatal clean shutdown for instance/other types, with the reason logged; make type 1 policy explicit.
  - Distinguish connect failure from mid-session disconnect in exit codes and logs.
- Depends on: ZW-02
- Confidence: medium

### ZW-04: Implement ZWUnitLocation emission (replace the no-send UpdateUnitLocationReports)
- Priority: P1
- Native evidence: 0x3935E7E0 (called from the frame tick, line 678449): if the enable flag (qword_3A638EA0, vtbl+16) is non-zero, iterate the registered unit list (qword_3A638F70..F78) and send ZWUnitLocation (0x35: unit id + vec3 = local position + cell origin * 1024) per unit via sub_39362B70.
- Repo state: `ZwOpcodes.UnitLocation` unused; `ZoneSceneProviders.cs:958 UpdateUnitLocationReports()` calls `UpdateSpatialMembership` and sends nothing (its doc comment equates it with 0x3935E7E0, which is incorrect).
- Work:
  - Add a `ZoneUnitLocation` message (u32 unit id, 3 floats) to Protocol/Messages.
  - Trace what populates the native list and the flag (xrefs to qword_3A638F70 / qword_3A638EA0) and port that.
  - Keep spatial membership update separate; do not conflate.
- Depends on: ZW-01 queue pattern, unit registry
- Confidence: medium

### ZW-05: Add ZWEnterArea / ZWLeaveArea
- Priority: P1
- Native evidence: 0x3932B1B0 and 0x3932B370, strings "area enter event received (event: %d, player eid: %u, area id: %d, area eid%d)" and the leave variant; opcodes 0x23/0x24.
- Repo state: opcode constants only; area content is loaded (`Scene content loaded: ... areas`) but no emission or serializer.
- Work:
  - Reverse the body from the two senders.
  - Hook area trigger enter/leave from the physics/trigger layer for player units.
  - Add serializers and tests.
- Depends on: scene trigger volumes (physics/scene area)
- Confidence: medium

### ZW-06: Add skill ZW packets (StartSkill, StopSkill, StopCasting, SkillControllerState, LongDistSkillControllerCreated)
- Priority: P1
- Native evidence: ops 0x18, 0x19, 0x1A, 0x09, 0x1B; senders 0x39326730 (ServerAIUnitSystem::UseSkill), 0x39227440, 0x39325940, 0x393259F0, 0x392291B0/0x39229280/0x39229350, 0x3935DD20; StartSkill body filler sub_39362920.
- Repo state: no constants, no serializers.
- Work:
  - Add constants and reverse bodies.
  - Emit from NPC AI skill use/stop and skill controllers.
  - Add serializer tests.
- Depends on: NPC AI skill usage (npc-ai area)
- Confidence: medium

### ZW-07: Handle World requests that expect a ZW reply (ZoneStatus, CombatUnits, RayCasting, Mole checks, RunCommand)
- Priority: P1
- Native evidence: replies 0x39365090 (0x2F, to WZ 0x77), 0x39369EC0 (0x2D, to 0x71), 0x3936F340 (0x30, to 0x7A), 0x39332C50 (0x37/0x38, to 0x80/0x81), 0x39366650 (0x01, to WZRunCommand 0x01).
- Repo state: constants for 0x2D/0x30/0x37/0x38/0x01 only (0x2F missing); no serializers; the WZ requests are not cases in `ZoneSession.ReceiveCore` and get counted as unimplemented.
- Work:
  - Add 0x2F; reverse bodies; add handlers in `ReceiveCore` and reply through the notification queue.
  - Raycast and mole checks need the native physics scene.
- Depends on: physics/raycast
- Confidence: medium

### ZW-08: Add unit-state and aggro ZW notifications (UnitFell, UnitModelPostureChanged, ChangeUnitFlyState, UnitCollision/Result, ErrorMsgToPlayer, AggroTargetChanged, KillNpc, ClearCombat, AggroRemove)
- Priority: P1
- Native evidence: 0x3935C850, 0x3935C760, 0x39325C20/0x3936EFD0, 0x390AEC80/0x390AF3C0, 0x393657C0, 0x3935CCC0, 0x3933EA90 (0x31), 0x393534D0/0x3935FAC0 (0x0C), 0x3931F790/0x393210B0 (0x41, NpcAggroTable::RemoveFromTable).
- Repo state: constants exist for most; KillNpc, ClearCombat, AggroRemove have no constant; no serializers.
- Work:
  - Add the three missing constants and all serializers.
  - Emit from the fall callback, posture changes, aggro table removal and NPC kill.
  - Existing abuser/aggro sends in `ZoneNpcSimulation.cs:545-582` are the pattern to follow.
- Depends on: aggro, death, physics callbacks
- Confidence: medium

### ZW-09: Add gimmick ZW packets (RequestSpawnStaticGimmick, Movement, JointBroken, ResetJoints, RemoveParts, Collided)
- Priority: P2
- Native evidence: 0x393365A0 (batch "static gimmicks found and requested for spawn" before ZoneLoaded), 0x39338A60, 0x393377A0, 0x39337680, 0x39338370, 0x39338BF0.
- Repo state: opcodes only; `RequestStaticGimmickSpawns()` is an interface stub (`ZoneSceneCoordinator.cs:40`).
- Work:
  - Implement serializers and emit when gimmick/physics exists.
  - RequestSpawnStaticGimmick belongs to the load sequence, before ZoneLoaded.
- Depends on: gimmick/physics area
- Confidence: medium

### ZW-10: Add anti-cheat and misc report packets (ReportMoveHack, ReportDoodadSurfaceHack, DoodadCollideUnit, BackpackDropped, RemoveHouse, TowerDefReportPlayability, DumpNpc, AiAggro, AiDebug, Sandbox*)
- Priority: P2
- Native evidence: ops 0x39, 0x3A, 0x3B, 0x36, 0x07, 0x32, 0x40, 0x34, 0x33, 0x20/0x22; senders 0x3921BD10/0x3924B550/0x39268720, 0x394510F0, 0x394547C0, 0x3932B930, 0x3933AE70, 0x393591D0, 0x39454400, 0x3944DA20, 0x3944E590, 0x3932D1C0/0x3932D170.
- Repo state: constants for all except 0x33, 0x40, 0x20, 0x22; no serializers.
- Work:
  - Implement as the owning gameplay system lands; Sandbox* and AiDebug are dev-only and can stay absent.
- Depends on: respective gameplay systems
- Confidence: medium

### ZW-11: Reconcile ZwOpcodes.cs with the full native list and add a coverage test
- Status: **Done** — 13 constants added from ctor sites, DoodadCollideUnit comment fixed, ZwOpcodeAndHeartbeatTests
- Priority: P2
- Native evidence: the 13 missing opcodes above; values read at the constructor sites listed in the table.
- Repo state: `ZwOpcodes.cs` has 45 constants; the `DoodadCollideUnit` comment says "without stable opcode" but native is 0x3B (59) at 0x394547C0.
- Work:
  - Add missing constants and fix the comment.
  - Add a test asserting the table against a checked-in native list.
- Depends on: none
- Confidence: high

### ZW-12: Record that CT*/TC*/StreamClient are out of scope for the zone
- Priority: P2
- Native evidence: StreamClient vftable 0x3987C750/0x3987C8C0 in client code with UI error strings; the zone uses only ZoneClient (0x3A67FF80); NpcSpawnerEventPacket (WZ 0x70) is already handled.
- Repo state: absent (nothing needed).
- Work:
  - Note the exclusion in TODO.md so it is not re-audited.
- Depends on: none
- Confidence: medium (not every StreamClient xref traced)

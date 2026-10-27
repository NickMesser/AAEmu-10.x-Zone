<!-- Audit of content-db: native x2game-dev_dedicate.dll (ArcheAge 10.0.2.13 r575) vs this repo at 76e439f. Paths rewritten to the src/ layout; line numbers refer to 76e439f. -->

## Game content database tables and asset files (content-db)

The native zone has no per-table "server only" filter: `sub_39BCA9A0` (DDCMS loader; logs `loaded DDCMS (%d descs, %d links ...)` at string 0x3A135F70) opens `game.compact` (optionally copying it to `game_<pid>.sqlite3`, `failed to copy DDCMS` 0x3A136050), optionally overlays a localized-text db, then calls roughly 290 generated per-table loader functions in sequence, each of which logs `failed: sqlite3_prepare_v2: sql='%s'` / `failed: sqlite3_step: function=Load<X>Descs` and aborts the whole load on failure. Retry wrapper is `sub_39BCE460` (10 tries x 2 s); `reload_ddcms` (0x39FDCF88) reloads at runtime into a double-buffered store (`a1+136076` toggles). I extracted **986 distinct tables** (SELECT ... FROM <table>) from the 89,574-function export; this includes client-oriented tables (UI/icon/ingame-shop/guide) because the shared DDCMS loader reads them unconditionally. The repo (`AAEmu.Zone/Content/*.cs`, ~6.5k lines) has readers for **96 of 986 tables (~10%)**, and several of those are column-partial (`skills` reads 2 of ~190 columns, `buffs` 14 of ~270, `items` 5 of ~75, `holdables` 9 of 34). Absent entirely: `effects`/`skill_effects`/all 58 `*_effects` payload tables, all 135 `doodad_func_*` tables and doodad_funcs/phases/groups, all 119 `quest_*`, `plots`/`plot_*`, `constants`, `options`, `tags`, `sub_zones`, `return_points`, `characters`/`character_*`, `world_groups`/`world_divisions`, zone climate/score tables, most housing/slave/transfer tables. Non-DB assets: the repo covers the Cry cell/terrain files (`heightmap.dat`, `statobjs.dat`, `brush.dat`, `materials.dat`, `vegetation.xml`, `entities.xml`, `world.xml`, `npc_spawners.g`, Lua scripts); it does not load the ServerAreaManager XMLs (`quest_area.xml`, `district.xml`), `return_point.g`, `Prefabs/*.xml`, `world_locations.xml`, `quest_sign_sphere.g`, or gimmick static entities.

Method notes: table list came from `Grep` of inlined SELECT strings (`x2game-dev_dedicate.strings.txt`, 0x3A0F4B..0x3A14..) cross-referenced to enclosing functions in the `.c`; each table typically has two loader functions (a simple one called from the master `sub_39BCA9A0`, and a row-counting variant that first calls `sub_39CCDAC0(<store>, db, "<table>")` for an existence/size check; the second address is shown after `/`). A few tables whose SQL is split across several C string literals have only the SQL string address (0x3A....) as loader evidence or a table-name-literal user; marked with `?`. "zone129" = Y means my judgement that the table is plausibly consumed on the first-entry path (zone 129, player walk) or by the services that path initialises; it is not proof.

### Non-DB asset files loaded by the zone

| native item | native evidence | repo status |
|---|---|---|
| `Game/worlds/%s/world.xml` (ZoneList name/originX/originY) | sub_397BDA60 (str 0x3A09E768) | ZoneWorldMapContent.cs |
| `%s/level_design/zone/%d/zone_server/npc_spawners.g` (`NpcManager::LoadSpawners`) | sub_3934AD80 / sub_39732A60 (str 0x39FE2620) | ZoneNpcSpawnerPlacementContent.cs + Program.cs (pre-restructure) `--spawner-file` (path supplied by CLI, not derived) |
| `%s/level_design/zone/%d/world_server/{quest_area,district}.xml` (`ServerAreaManager::LoadAreas`, "Server Area Manager : total (%d) areas") | sub_3932B530 (str 0x39FDBED0), caller sub_3932B730 | absent |
| `Game/worlds/main_world/level_design/zone/%d/world_server/return_point.g` | sub_397BC620 (str 0x3A09E600) | absent (only DB `return_points` unread too) |
| `Game/worlds/%s/level_design/zone/%d/world_server/district.xml` (`failed to load District`) | sub_397BE0C0 (str 0x3A09E818) | absent |
| `quest_sign_sphere.g` (client/ and server copy) | sub_397A1180 (str 0x3A09BA08, 0x39F8E078) | absent |
| `Prefabs/*.xml` prefab library | sub_390C2130 (str 0x39F8E198) | absent |
| `world_locations.xml` | sub_394988D0, sub_39498B00 (str 0x3A017C80) | absent |
| `X2::GameServer::ZdModelPrefab::Load` / `ZdModelVegetation::Load`, `ServerGimmick::Init/InitStaticEntity` | strings in .strings.txt; `zone doodad load {brush,vegetation,prefab} model fail path` 0x3A00B138.. | absent (ZoneModelContent reads prefab_models rows only) |
| `map_data/doodad_map/%s.dat`, `map_data/npc_map/%s.dat`, `client/transfer_path.xml` | sub_390FDF70, sub_39733360, sub_39742860 -- `X2::GameClient::*` | not zone work (client only) |
| Cry level files (heightmap.dat, cell `statobjs/brush/materials/visareas/vegetation/object/big_object.dat`, `vegetation.xml`, `entities.xml`, `leveldata.xml`, `surface.xml`, `surfacetypes.xml`) | loaded inside Cry3DEngine/CryAction DLLs, not x2game; see other areas | ZoneTerrainAssetFiles.cs, ZoneCellObjectContent.cs, ZoneCellDeferredContent.cs, ZonePhysicalSurface*.cs |
| Lua: `Scripts/AI/lookup.xml`, `Scripts/Entities/*.lua`, `scripts/*` compiled `scriptsbin64/*.alb` | strings 0x39FF1F28 (`/Game%s/*.alb`), 0x39F8E030 | ZoneScriptLoader.cs (`scripts/` -> `scriptsbin64/.alb` switch present) |
| `game/libs/GameTokens/*.xml`, `Libs/MaterialEffects/QuadrupedTypes.xml`, `EngineAssets/raintextures.xml`, `game/libs/flownodes/weather/*.xml`, `system.cfg`, `game/clientdriven.g`, `Game/worlds/is_branch.txt`, `toc.g` | strings 0x3A018C30, 0x39FD3DA0, 0x39FB8A58, 0x39F8C288, 0x39FC7570, 0x39F90EF0, 0x39F9CC90, 0x39FF1598 | absent (mostly client/editor; GameTokens and is_branch used by shared startup) |

### Coverage table summary by subsystem

| subsystem | native tables | repo readers | note |
|---|---|---|---|
| NPC/AI | 28 `npc*` + ai_* (+ spawn/effect link tables) | 10 | npcs full 130-col read; interactions/initial_buffs/nicknames absent |
| Doodads | 144 `doodad_*` + gimmicks, housing_binding_doodads | 1 (`doodad_almighties` ids only) | entire doodad func/phase system absent |
| Skills/buffs/effects/plots | skills, buffs(+13), 58 `*_effects`, effects, skill_*, buff_*, plot_* (8) | ~16 | only stat-relevant columns |
| Items/loot/crafting | 88 `item*`, loot (5), craft (13), equip/wearable | ~27 | items/holdables partial; crafting absent |
| World/zone metadata | zones, zone_groups, sub_zones, world_*, zone_climate*, zone_score*, return_points, map_* | 3 (zones, zone_groups, world_contents) | |
| Quests | 119 `quest_*` + achievements | 3 (achievements) | quests absent |
| Housing | 19 `housing*` + housings | 2 | |
| Slaves/vehicles/transfers | 19 `slave*`, vehicle_models, ship_models, transfers (4), butlers | 5 | |
| Instances/events/siege | 37 instance/indun, game_* (18), tower/siege/dominion (16) | ~4 | |
| UI/ingame-shop/guide/text | ~60 | 0 | not zone work; loaded natively only because loader is shared |


## Tasks

### DB-01: Add the missing global/config tables the first-entry path reads (constants, options, tags, return_points, sub_zones, characters)
- Priority: P0
- Native evidence: loaders `constants` sub_39C80E80, `options` sub_39D1A050, `tags` sub_39BFF050/sub_39C05DF0, `return_points` sub_39AF3090/sub_39B07660, `sub_zones` sub_39CF4C10/sub_39CF5990, `characters` sub_39B03750, `character_default_skills` sub_39B46610, `character_idle_buffs` sub_39C66520, `character_supplies` sub_39B03540, `world_specific_content_configs` sub_39B4CFC0, `world_var_defaults` sub_39CC7880/sub_39CDC7F0, `world_level_configs` sub_39B8F960; SQL strings in the 0x3A0F..0x3A14.. range of strings.txt.
- Repo state: absent for all of these (no `FROM <table>` in any *.cs). Only `zones`, `zone_groups`, `world_contents`, `battle_fields`, `conflict_zones`, `indun_zones`, `festival_zones` are read (ZoneContentDatabase.cs, ZoneSceneContent.cs, ZoneWorldContent.cs).
- Work:
  - Add readers (NativeContentReader pattern) with the full native column lists copied from the strings.txt SQL.
  - Expose `constants`/`options` through a provider consumed by movement/stat code instead of hard-coded values.
  - Load `return_points` + `sub_zones` into ZoneSceneContent so spawn/respawn and sub-zone lookups have data.
  - Load `characters`/`character_*` (race/gender/ability starting data) for player actor creation.
- Depends on: none
- Confidence: medium (loader addresses and absence verified; per-table P0 classification is inferred)

### DB-02: Introduce a unified DDCMS loader orchestrator mirroring sub_39BCA9A0
- Priority: P1
- Native evidence: master `sub_39BCA9A0` (logs 0x3A135F70, 0x3A136008 `failed to open DDCMS at %s`, 0x3A136028/0x3A136050 copy to `game_<pid>.sqlite3`), retry wrapper `sub_39BCE460` (10 tries, 2 s; remove temp 0x3A1360C8), double buffer index at `a1+136076`, `reload_ddcms` 0x39FDCF88, localized-text db overlay (`sub_39BB7BA0`, "failed to load localized text db"), per-table existence/size check `sub_39CCDAC0(store, db, "<table>")`.
- Repo state: each Zone*Content.Load(connection) is invoked ad hoc; ZoneContentDatabase.cs opens read-only/no pooling but there is no single registry, atomic swap, reload or localized-text overlay.
- Work:
  - Create a `ZoneGameDataStore` aggregating every Zone*Content with all-or-nothing load semantics and a swap for hot reload.
  - Log per-loader row counts in the native shape (descs/links).
  - Optional localized text DB overlay for name/string columns.
- Depends on: DB-01
- Confidence: medium

### DB-03: Complete skills/buffs/effects data model (full skills+buffs columns, effects dispatch, skill_effects, buff_triggers, tick effects, 58 *_effects payload tables)
- Priority: P1
- Native evidence: `skills` sub_39AC17C0/sub_39AE6D50 (SQL 0x3A0F6A10, ~190 cols), `buffs` (SQL 0x3A0F7640, ~270 cols; loader ~sub_39AC1930/sub_39AD3980, inferred from table-name literal), `effects` sub_39AD0220 (0x3A0FB5B8 `SELECT id, actual_type, actual_id FROM effects`), `skill_effects` sub_39AD0E90 (0x3A0FB670, `WHERE enable = 't'`), `buff_triggers` sub_39AD2320 (0x3A0FC020), `buff_tick_effects` sub_39AD2D30, `buff_swap_skills` sub_39AD3760, `combat_buffs` (0x3A0FC810), `skill_modifiers` sub_39C146E0, `skill_controllers` sub_39BFD7D0, `skill_reqs`, `skill_products`/`skill_reagents`, `buff_modifiers` (0x3A1467D0), plus 58 `*_effects` tables (damage_effects sub_39C07880, heal_effects sub_39C08BF0, spawn_effects sub_39C0ABD0 ...).
- Repo state: ZoneSkillContent.cs:48 reads only `SELECT id, ability_id FROM skills`; ZoneBuffContent.cs reads 14 buff columns plus tagged_buffs/passive_buffs/buff_unit_modifiers/dynamic_funcs; `effects`, `skill_effects`, `buff_triggers`, `buff_tick_effects`, `buff_modifiers`, `combat_buffs` and all `*_effects` are absent.
- Work:
  - Extend skill/buff definitions to the full native column lists (NULL-default semantics via NativeContentReader).
  - Readers for effects (honouring `enable = 't'` filters) and each *_effects payload table; build the effect-id to (actual_type, actual_id) dispatch.
  - Readers for buff_triggers/tick_effects/swap_skills/combat_buffs/tolerances and the skill_* tag/synergy/alert/controller tables.
- Depends on: none (DB-02 optional)
- Confidence: high

### DB-04: Add plots (skill plot scripting) tables
- Priority: P2
- Native evidence: `plots` sub_39C74F90/sub_39C83EF0, `plot_events` sub_39C74E60/sub_39C83610, `plot_next_events` sub_39C83170, `plot_conditions` sub_39C81920, `plot_event_conditions` sub_39C82340, `plot_aoe_conditions` sub_39C82110, `plot_effects` sub_39C825B0, `plot_auction_config` sub_39CE2740.
- Repo state: absent; ZoneSceneProviders.cs:899 notes "plots: no plot runtime owner TODO(plots)" (native update 39455230).
- Work: readers for the 8 tables; plot graph model; link to skills.plot_id.
- Depends on: DB-03
- Confidence: high

### DB-05: Finish the NPC data set (interactions, initial buffs, nicknames, mount skills, doodad bindings, strafe params, grade configs, spawn-effect links, merchants)
- Priority: P1
- Native evidence: `npc_interactions` sub_39AFDF10, `npc_interaction_sets` sub_39AFE0D0, `npc_binding_unit_buffs` sub_39AFE340, `npc_initial_buffs` sub_39B45DD0, `npc_nicknames` sub_39AF2F60/sub_39B06F30, `npc_nick_buffs` sub_39B46A10, `npc_mount_skills` sub_39B45FE0, `npc_doodad_bindings` sub_39B0CC10, `npc_strafe_params` sub_39AFCD50, `npc_instance_recruiters` sub_39AFF4A0, `npc_grade_configs` sub_39CD69D0, `npc_hp_split_configs` sub_39D18580, `npc_control_effects` sub_39BFDB30, `npc_spawner_spawn_effects`/`_despawn_effects` sub_39C0F3E0/sub_39C0F6D0, `conflict_zone_npc_spawners` sub_39C68E50, `faction_change_npc_spawners` sub_39C6F840, `merchants`/`merchant_packs` sub_39BA3330/sub_39B9B1F0, `mate_equip_packs` sub_39BAFBF0.
- Repo state: present: npcs (NpcTemplateDefinition.cs:126, full column list), npc_spawners/npc_spawner_npcs, npc_groups/members, aggro links, ai_files/ai_events/npc_ai_params, postures, chat bubbles, game_schedule_spawners, tower_def*. Absent: everything listed above.
- Work: add the readers; apply initial/nick buffs at NPC creation; interaction sets for NPC services.
- Depends on: DB-03
- Confidence: high

### DB-06: Add the doodad data model (doodad_almighties full columns, groups, funcs, phase funcs, 135 doodad_func_* tables, modifiers, bundles, place skins, gimmicks)
- Priority: P1
- Native evidence: `doodad_almighties` sub_39BDD8D0/sub_39BE8110 (SQL 0x3A136CA0), `doodad_funcs` sub_39BDD550/sub_39BE5930, `doodad_groups` sub_39BE7730, `doodad_phase_funcs` sub_39BE6A60, `doodad_place_skins` sub_39BE7F60, `doodad_bundles`/`doodad_bundle_doodads` sub_39C641E0/sub_39C64020, `doodad_modifiers` (SQL 0x3A1468E0, sub_39C14D90), `gimmicks` sub_39BDE230/sub_39BEDC50, `item_spawn_doodads` sub_39AF56B0, `game_schedule_doodads` sub_39CD8B30, and 135 `doodad_func_*` (e.g. doodad_func_uses sub_39CF7660, loot_packs sub_39CF6F90, timers sub_39CF4FA0, buffs sub_39CFFE90, spawns sub_39BE2030, area_triggers sub_39CFAA70).
- Repo state: ZoneNpcSpawnerControllerContent.cs:168 reads only `SELECT id FROM doodad_almighties`; gimmicks absent (ZoneSceneProviders.cs:897 TODO(gimmicks), native update 39339B70).
- Work:
  - Full `doodad_almighties` columns plus groups/funcs/phase_funcs.
  - Table-driven readers for the 135 doodad_func_* tables (generate from the strings.txt SQL; each is id plus a few columns).
  - Gimmick table and static/dynamic entity init (see DB-12).
- Depends on: DB-03
- Confidence: high

### DB-07: Items, loot, crafting and economy tables beyond the stat subset
- Priority: P1
- Native evidence: `items` sub_39AC2CF0/sub_39AE9890 (SQL 0x3A0F9370, ~75 cols), item_armors (0x3A0F97C0), item_weapons (0x3A0FA4B0), item_accessories (0x3A0FA2C0), item_procs (0x3A0F8D80), item_proc_bindings (0x3A0FD960), item_grade_buffs/skills (0x3A0FD1A0, 0x3A0FD280), item_categories (0x3A0F8C70), equip_item_sets (0x3A0F8E80), item_summon_mates/slaves (0x3A0FABC0, 0x3A0FAC80), selective_item_effect* (0x3A0F4BD0), 13 craft* tables (`craft_materials` SQL 0x3A0FE5F0), tradegood_* (0x3A0F8F00, 0x3A101400), `merchants`.
- Repo state: present in ZoneEquipmentContent.cs, ZoneEquipmentCombatContent.cs, ZoneReinforcementContent.cs, ZoneLootContent.cs: items (5 of ~75 cols), item_armors/weapons/accessories/backpacks, holdables (9 of 34), item_grades, wearables/wearable_slots/kinds/formulas, equip_slot_reinforces*, item_rnd_attr_*, and the five loot tables. Absent: item_procs, item_proc_bindings, item_grade_buffs/skills, item_categories, equip_item_sets, crafts, summon items, tradegood_*, selective effects.
- Work: widen items/holdables to full columns; add proc/grade-buff/skill/set tables (gear stats and procs); crafts and economy later.
- Depends on: DB-03
- Confidence: high

### DB-08: World/zone metadata completion (world_groups, world_divisions, zone climates, map data, zone_group tables, resurrection times)
- Priority: P1
- Native evidence: `world_groups` sub_39C79300/sub_39C8EF20, `world_divisions` sub_39C5C920/sub_39C6A0C0 (string `World Division Data Build!` 0x3A175650), `zone_climates` sub_39C031F0, `zone_climate_elems` sub_39C03030, `zone_group_banned_tags` sub_39CCC750, `zone_group_dummy_factions` sub_39C888F0, `zone_waitings` sub_39CCC880, `zone_score_*` (sub_39BFEB20 ..), `map_icons` sub_39C5CBD0, `map_resources` sub_39C5CA40, `housing_areas` sub_39BDBC00, `resurrection_waiting_times` (SQL 0x3A136560, sub_39BDC070), `world_level_exp_modifiers` sub_39B8F960.
- Repo state: zones/zone_groups/world_contents/conflict_zones/festival_zones/indun_zones/battle_fields read; the rest absent.
- Work: readers attached to ZoneSceneContent; world_divisions needed for seamless-world/zone-group dispatch.
- Depends on: DB-01
- Confidence: medium

### DB-09: Load ServerAreaManager XMLs (quest_area, district) and return_point.g
- Priority: P1
- Native evidence: `ServerAreaManager::LoadAreas` sub_3932B530, format `%s/level_design/zone/%d/world_server/%s.xml` (0x39FDBED0), called with "quest_area" and "district" from sub_3932B730 (guarded by flags at `qword_3D8B0EE0+289/+288`); log `Server Area Manager : total (%d) areas`; area enter/leave event strings 0x39FDBDB0/0x39FDBE00; `return_point.g` sub_397BC620; `district.xml` (client-style path) sub_397BE0C0 `failed to load District from (%s)`.
- Repo state: absent (no hits for "quest_area", "district", "LoadAreas"). ZoneNavigationAreaContent.cs reads DB area rows, not these files.
- Work: parser for the area XML (shape/trigger geometry per zone), area enter/leave dispatch, return_point.g parser, cross-check with DB `sub_zones`/`return_points`.
- Depends on: DB-01
- Confidence: medium (file formats not decompiled; native code goes through virtual engine calls)

### DB-10: Derive world file paths from zone id instead of CLI arguments
- Priority: P1
- Native evidence: `X2::GameServer::NpcManager::LoadSpawners` sub_3934AD80 (path 0x39FE2620, "loading npc spawners ... done!", `NpcSpawner ignored : id=%d, type=%u`, `failed to load spawner data from %s, %s`).
- Repo state: Program.cs (pre-restructure):62 requires `--spawner-file <npc_spawners.g>`; ZoneAssetPath validates only `game/worlds/<name>`.
- Work: build `game/worlds/<world>/level_design/zone/<zoneKey>/zone_server/npc_spawners.g` via ZoneAssetSource; reproduce native ignored-type/`__none__` handling and messages.
- Depends on: none
- Confidence: high

### DB-11: Slaves, vehicles, transfers and butlers data
- Priority: P2
- Native evidence: `slaves` sub_39AF2D00/sub_39B002E0, `slave_bindings` sub_39B0D3E0, `slave_doodad_bindings` sub_39B0D6C0, `slave_interaction_skills` sub_39B00040, `slave_customizings` sub_39CE0560, `slave_passive_buffs` sub_39B0D160, `transfers` sub_39AF2E30/sub_39B02550, `transfer_paths` sub_39B0E730/sub_39B0E480, `transfer_bindings` sub_39B0E200, `transfer_binding_doodads` sub_39B0DF80.
- Repo state: slaves, slave_equip_*, slave_extend_models, item_slave_equipments, vehicle_models, ship_models present; `transfers` reads id only (ZoneStatContent.cs:153); transfer_paths/bindings and the other slave_* absent.
- Work: add missing readers; transfer path waypoints for the transport runtime.
- Depends on: DB-06
- Confidence: high

### DB-12: ServerGimmick / ZdModelPrefab / ZdModelVegetation resources and Prefabs/*.xml
- Priority: P2
- Native evidence: `X2::GameServer::ServerGimmick::Init/InitStaticEntity/InitDynamicEntity`, `ZdModelPrefab::Load`, `ZdModelVegetation::Load` (strings.txt); `gimmick prefab validation failed` 0x39F8C800; `Prefabs/*.xml` sub_390C2130; `zone doodad load {brush,vegetation,prefab} model fail path` 0x3A00B138/0x3A00B190/0x3A00B1E8; `prefab_elements` SQL 0x3A0F5F50.
- Repo state: prefab_models and model tables read (ZoneModelContent.cs); no gimmick runtime (ZoneSceneProviders.cs:897) and no prefab XML loader.
- Work: Prefabs/*.xml loader via ZoneAssetSource; gimmick table and entity init; doodad model load path handling.
- Depends on: DB-06
- Confidence: medium

### DB-13: Quest tables and quest-related zone hooks
- Priority: P2
- Native evidence: 119 `quest_*` tables (e.g. `quest_act_obj_effect_fires` SQL 0x3A0FAED0, `quest_act_obj_consume_evolving_materials` SQL 0x3A16FB50, `quest_act_obj_crafts` sub_39D0BEF0), `quest_sign_sphere.g` sub_397A1180, `accept_quest_effects` sub_39C0BD40.
- Repo state: achievements/appellation_merits/char_records read (ZoneAchievementContent.cs, ZoneAppellationContent.cs); all quest_* absent.
- Work: only the zone-relevant subset (act_obj kill/interact/use-item, npc `friendly_near_quest_id`, `doodad_func_quest*`, accept/complete effects); quest_sign_sphere.g loader.
- Depends on: DB-03, DB-06
- Confidence: low (which quest tables are zone-side versus world-server-side not determined)

### DB-14: Housing, instance/event/siege and game-rule tables
- Priority: P2
- Native evidence: 19 `housing*` (housings sub_39BDDE30/sub_39BEB780, decorations sub_39BECB70, build_steps sub_39BEACA0, rebuildings sub_39BDDCC0 ...), 37 instance/indun tables (`instances` sub_39B5BF00 ..., `indun_event_npc_spawneds` sub_39C871E0), `game_rule_sets` sub_39C91FF0, `game_rule_events` sub_39C89CC0, `game_safe_area_rules` sub_39C750B0, siege/dominion tables.
- Repo state: housings/housing_build_steps (ZoneStatContent.cs), tower_defs (ZoneNpcSpawnerControllerContent.cs), indun_zones/battle_fields/conflict_zones (ZoneSceneContent.cs) present; remainder absent.
- Work: add as those subsystems are implemented; keep one-reader-per-table convention.
- Depends on: DB-06
- Confidence: medium

### DB-15: Table-schema drift guard (compare repo SELECT lists with native SQL strings)
- Priority: P2
- Native evidence: ~986 SELECT strings in `x2game-dev_dedicate.strings.txt` (failure format 0x3A0F4B50; `SELECT COUNT(*) FROM %s` 0x3A0F6858); existence check `sub_39CCDAC0(store, db, "<table>")`.
- Repo state: no test compares repo column lists to native ones (AAEmu.Zone.Tests).
- Work: test parsing a checked-in fixture of native SELECTs against the repo's SelectSql constants; coverage report listing tables with no reader (the appendix below was produced this way).
- Depends on: none
- Confidence: high

### DB-16: Decide handling of client-only tables loaded by the shared loader
- Priority: P2
- Native evidence: ~60 UI/guide/icon/ingame-shop/customizing tables (`ingameshop_modelview_offsets` sub_39D19B40, `chat_icon_infos` sub_39D1B4A0, `item_guide_*` SQL 0x3A0F9080..0x3A0F92B0, `face_decal_assets` SQL 0x3A0F6540) are loaded unconditionally by sub_39BCA9A0; no server-mode branch observed.
- Repo state: absent intentionally.
- Work: document as out of scope; decide explicitly what to do when a table is missing (native aborts the whole load on any prepare failure).
- Depends on: DB-02
- Confidence: medium

## Appendix: all native-loaded tables (986) by subsystem

"native loader" = master-pass simple loader / size-checked variant (sub_ addresses in the .c). Repo reader = file containing the SELECT; "absent" = no `FROM|JOIN <table>` in any *.cs. `?` = loader inferred from a split SQL string. Subsystem grouping is by name heuristic.

#### Doodad/Housing (185 tables)

| table | native loader | repo reader | used for | zone129 |
|---|---|---|---|---|
| blocked_child_doodads | 0x39C15080 | absent | other gameplay/config data |  |
| dominion_housings | 0x39C5F930/0x39C6BE50 | absent | other gameplay/config data |  |
| doodad_almighties | 0x39BDD8D0/0x39BE8110 | ZoneNpcSpawnerControllerContent.cs | doodad definition/phase/group | Y |
| doodad_bundle_doodads | 0x39C64020 | absent | doodad definition/phase/group |  |
| doodad_bundles | 0x39C641E0 | absent | doodad definition/phase/group |  |
| doodad_func_activate_spheres | 0x39BE16C0 | absent | doodad interaction function params |  |
| doodad_func_area_triggers | 0x39CFAA70 | absent | doodad interaction function params |  |
| doodad_func_attachments | 0x39CF60D0 | absent | doodad interaction function params |  |
| doodad_func_auction_uis | 0x39D00680 | absent | doodad interaction function params |  |
| doodad_func_bank_uis | 0x39D007E0 | absent | doodad interaction function params |  |
| doodad_func_bind_butlers | 0x39BE3E20 | absent | doodad interaction function params |  |
| doodad_func_bindings | 0x39CF5F10 | absent | doodad interaction function params |  |
| doodad_func_buffs | 0x39CFFE90 | absent | doodad interaction function params |  |
| doodad_func_build_condition_infos | 0x39BDD1E0/0x39BE1980 | absent | doodad interaction function params |  |
| doodad_func_build_condition_ui_opens | 0x39BE1820 | absent | doodad interaction function params |  |
| doodad_func_buy_fish_items | 0x39CFD690 | absent | doodad interaction function params |  |
| doodad_func_buy_fish_model_items | 0x39CF4550/0x39CFDAF0 | absent | doodad interaction function params |  |
| doodad_func_buy_fish_models | 0x39CFDCF0 | absent | doodad interaction function params |  |
| doodad_func_buy_fishes | 0x39CFD850 | absent | doodad interaction function params |  |
| doodad_func_change_other_doodad_phases | 0x39BE2960 | absent | doodad interaction function params |  |
| doodad_func_cleanup_logic_links | 0x39CFBCD0 | absent | doodad interaction function params |  |
| doodad_func_climbs | 0x39CF67D0 | absent | doodad interaction function params |  |
| doodad_func_clout_effects | 0x39B46C10 | absent | doodad interaction function params |  |
| doodad_func_clouts | 0x39CF62F0 | absent | doodad interaction function params |  |
| doodad_func_coffer_perms | 0x39CFE9E0 | absent | doodad interaction function params |  |
| doodad_func_coffers | 0x39CFA670 | absent | doodad interaction function params |  |
| doodad_func_competition_points | 0x39BE4F30 | absent | doodad interaction function params |  |
| doodad_func_conditional_uses | 0x39CFA800 | absent | doodad interaction function params |  |
| doodad_func_consume_changer_models | 0x39CF4430/0x39CFD490 | absent | doodad interaction function params |  |
| doodad_func_consume_changers | 0x39CFD300 | absent | doodad interaction function params |  |
| doodad_func_convert_fish_items | 0x39CFDF60 | absent | doodad interaction function params |  |
| doodad_func_convert_fishes | 0x39CFE150 | absent | doodad interaction function params |  |
| doodad_func_craft_acts | 0x39CF4160/0x39CF8150 | absent | doodad interaction function params |  |
| doodad_func_craft_cancels | 0x39CF8540 | absent | doodad interaction function params |  |
| doodad_func_craft_directs | 0x39CF8B10 | absent | doodad interaction function params |  |
| doodad_func_craft_get_items | 0x39CF86A0 | absent | doodad interaction function params |  |
| doodad_func_craft_infos | 0x39CF83E0 | absent | doodad interaction function params |  |
| doodad_func_craft_order_board_ui_opens | 0x39BE2800 | absent | doodad interaction function params |  |
| doodad_func_craft_packs | 0x39CFC150 | absent | doodad interaction function params |  |
| doodad_func_craft_start_crafts | 0x39CF7CD0 | absent | doodad interaction function params |  |
| doodad_func_craft_starts | 0x39CF4040/0x39CF7E90 | absent | doodad interaction function params |  |
| doodad_func_cutdownings | 0x39CF6BB0 | absent | doodad interaction function params |  |
| doodad_func_declare_sieges | 0x39CFCBF0 | absent | doodad interaction function params |  |
| doodad_func_devotes | 0x39CF5200/0x39D01A70 | absent | doodad interaction function params |  |
| doodad_func_dig_terrains | 0x39CF8E60 | absent | doodad interaction function params |  |
| doodad_func_dominion_count_reacts | 0x39BE45C0 | absent | doodad interaction function params |  |
| doodad_func_dominion_tax_in_kinds | 0x39BE1CE0 | absent | doodad interaction function params |  |
| doodad_func_enter_instances | 0x39CFB1F0 | absent | doodad interaction function params |  |
| doodad_func_enter_sys_instances | 0x39CFF1B0 | absent | doodad interaction function params |  |
| doodad_func_evidence_item_loots | 0x39CFAC80 | absent | doodad interaction function params |  |
| doodad_func_exchange_items | 0x39CFE3C0 | absent | doodad interaction function params |  |
| doodad_func_exchanges | 0x39CFE5B0 | absent | doodad interaction function params |  |
| doodad_func_exit_global_induns | 0x39BE3CC0 | absent | doodad interaction function params |  |
| doodad_func_exit_induns | 0x39CFF3E0 | absent | doodad interaction function params |  |
| doodad_func_expedition_portal_ui_opens | 0x39BE57D0 | absent | doodad interaction function params |  |
| doodad_func_expedition_ui_opens | 0x39BE5620 | absent | doodad interaction function params |  |
| doodad_func_fake_uses | 0x39CF77F0 | absent | doodad interaction function params |  |
| doodad_func_finals | 0x39CF4E70/0x39CF7A20 | absent | doodad interaction function params |  |
| doodad_func_fish_schools | 0x39D000D0 | absent | doodad interaction function params |  |
| doodad_func_fx_group_callbacks | 0x39BE2EB0 | absent | doodad interaction function params |  |
| doodad_func_goods_values | 0x39D011C0 | absent | doodad interaction function params |  |
| doodad_func_groups | 0x39BDD680/0x39BE79A0 | absent | doodad interaction function params | Y |
| doodad_func_growths | 0x39CF4D40/0x39CF6960 | absent | doodad interaction function params |  |
| doodad_func_hero_elections | 0x39D01910 | absent | doodad interaction function params |  |
| doodad_func_hide_map_icons | 0x39BE4460 | absent | doodad interaction function params |  |
| doodad_func_house_farms | 0x39CFEB40 | absent | doodad interaction function params |  |
| doodad_func_insert_counters | 0x39D00490 | absent | doodad interaction function params |  |
| doodad_func_instance_difficult_ui_opens | 0x39BE54C0 | absent | doodad interaction function params |  |
| doodad_func_instant_ui_opens | 0x39BE42D0 | absent | doodad interaction function params |  |
| doodad_func_issuance_of_mobilization_order_ui_opens | 0x39D017B0 | absent | doodad interaction function params |  |
| doodad_func_item_changer_ui_opens | 0x39D01650 | absent | doodad interaction function params |  |
| doodad_func_item_changers | 0x39D01430 | absent | doodad interaction function params |  |
| doodad_func_local_development_board_ui_opens | 0x39BE3F80 | absent | doodad interaction function params |  |
| doodad_func_logic_family_providers | 0x39CFBE30 | absent | doodad interaction function params |  |
| doodad_func_logic_family_subscribers | 0x39CFBFC0 | absent | doodad interaction function params |  |
| doodad_func_logics | 0x39CFB6F0 | absent | doodad interaction function params |  |
| doodad_func_loot_items | 0x39CF6D10 | absent | doodad interaction function params |  |
| doodad_func_loot_packs | 0x39CF6F90 | absent | doodad interaction function params |  |
| doodad_func_model_changes | 0x39BDB9B0/0x39BE35B0 | absent | doodad interaction function params |  |
| doodad_func_navi_mark_pos_to_maps | 0x39CF9A00 | absent | doodad interaction function params |  |
| doodad_func_navi_namings | 0x39CF9710 | absent | doodad interaction function params |  |
| doodad_func_navi_open_mailboxes | 0x39CF9870 | absent | doodad interaction function params |  |
| doodad_func_navi_open_portals | 0x39CF9F10 | absent | doodad interaction function params |  |
| doodad_func_navi_remove_timers | 0x39CF9BF0 | absent | doodad interaction function params |  |
| doodad_func_navi_removes | 0x39CF9D80 | absent | doodad interaction function params |  |
| doodad_func_navi_teleports | 0x39CFA070 | absent | doodad interaction function params |  |
| doodad_func_nuons_arrow_ui_opens | 0x39BE2540 | absent | doodad interaction function params |  |
| doodad_func_open_farm_infos | 0x39CFF020 | absent | doodad interaction function params |  |
| doodad_func_open_papers | 0x39CFE820 | absent | doodad interaction function params |  |
| doodad_func_origin_item_time_checks | 0x39BE47B0 | absent | doodad interaction function params |  |
| doodad_func_ownership_times | 0x39BE2B50 | absent | doodad interaction function params |  |
| doodad_func_parent_infos | 0x39CFD1A0 | absent | doodad interaction function params |  |
| doodad_func_parrots | 0x39CFC850 | absent | doodad interaction function params |  |
| doodad_func_play_flow_graphs | 0x39CFCFE0 | absent | doodad interaction function params |  |
| doodad_func_private_coffers | 0x39D00AD0 | absent | doodad interaction function params |  |
| doodad_func_pulse_triggers | 0x39CFB8B0 | absent | doodad interaction function params |  |
| doodad_func_pulses | 0x39CFB540 | absent | doodad interaction function params |  |
| doodad_func_purchases | 0x39CFC9B0 | absent | doodad interaction function params |  |
| doodad_func_puzzle_ins | 0x39CF4670/0x39CFF9F0 | absent | doodad interaction function params |  |
| doodad_func_puzzle_outs | 0x39CF4790 | absent | doodad interaction function params |  |
| doodad_func_puzzle_rolls | 0x39CFF830 | absent | doodad interaction function params |  |
| doodad_func_quest_reacts | 0x39BE3330 | absent | doodad interaction function params |  |
| doodad_func_quests | 0x39CF7120 | absent | doodad interaction function params |  |
| doodad_func_random_store_uis | 0x39BE5330 | absent | doodad interaction function params |  |
| doodad_func_ratio_changes | 0x39CFAE70 | absent | doodad interaction function params |  |
| doodad_func_ratio_respawns | 0x39CFB030 | absent | doodad interaction function params |  |
| doodad_func_react_devotes | 0x39BDD0B0/0x39BE1480 | absent | doodad interaction function params |  |
| doodad_func_recover_items | 0x39CF95B0 | absent | doodad interaction function params |  |
| doodad_func_remove_instances | 0x39CFB3B0 | absent | doodad interaction function params |  |
| doodad_func_remove_items | 0x39CF8CA0 | absent | doodad interaction function params |  |
| doodad_func_require_items | 0x39CF72E0 | absent | doodad interaction function params |  |
| doodad_func_require_quests | 0x39CF74A0 | absent | doodad interaction function params |  |
| doodad_func_resident_balances | 0x39BDB890/0x39BE2350 | absent | doodad interaction function params |  |
| doodad_func_resident_charges | 0x39BE4110 | absent | doodad interaction function params |  |
| doodad_func_resident_townhall_ui_opens | 0x39BE26A0 | absent | doodad interaction function params |  |
| doodad_func_siege_periods | 0x39CFCD50 | absent | doodad interaction function params |  |
| doodad_func_siege_raids | 0x39BE1ED0 | absent | doodad interaction function params |  |
| doodad_func_signs | 0x39CF50D0/0x39CFBAC0 | absent | doodad interaction function params |  |
| doodad_func_skill_hits | 0x39CF9420 | absent | doodad interaction function params |  |
| doodad_func_skip_war_states | 0x39BE51A0 | absent | doodad interaction function params |  |
| doodad_func_spawn_gimmicks | 0x39CFF570 | absent | doodad interaction function params |  |
| doodad_func_spawn_mgmts | 0x39D00260 | absent | doodad interaction function params |  |
| doodad_func_spawn_slave_after_get_items | 0x39BE30E0 | absent | doodad interaction function params |  |
| doodad_func_spawns | 0x39BE2030 | absent | doodad interaction function params |  |
| doodad_func_stamp_makers | 0x39CFC2E0 | absent | doodad interaction function params |  |
| doodad_func_store_uis | 0x39D00940 | absent | doodad interaction function params |  |
| doodad_func_timers | 0x39CF4FA0/0x39CF8800 | absent | doodad interaction function params |  |
| doodad_func_tod_reacts | 0x39CFA420 | absent | doodad interaction function params |  |
| doodad_func_tods | 0x39CFA1D0 | absent | doodad interaction function params |  |
| doodad_func_ucc_imprints | 0x39CFC560 | absent | doodad interaction function params |  |
| doodad_func_uses | 0x39CF7660 | absent | doodad interaction function params |  |
| doodad_func_water_volumes | 0x39CF9250 | absent | doodad interaction function params |  |
| doodad_func_worldmap_texts | 0x39BDD3E0/0x39BE4970 | absent | doodad interaction function params |  |
| doodad_func_zone_buffs | 0x39BE4BB0 | absent | doodad interaction function params |  |
| doodad_func_zone_overlaps | 0x39BE4D70 | absent | doodad interaction function params |  |
| doodad_func_zone_reacts | 0x39CFEE60 | absent | doodad interaction function params |  |
| doodad_funcs | 0x39BDD550/0x39BE5930 | absent | doodad definition/phase/group | Y |
| doodad_groups | 0x39BE7730 | absent | doodad definition/phase/group | Y |
| doodad_item_change_effects | 0x39C0D6A0 | absent | doodad definition/phase/group |  |
| doodad_modifiers | 0x39C14D90 | absent | doodad definition/phase/group |  |
| doodad_phase_funcs | 0x39BE6A60 | absent | doodad definition/phase/group | Y |
| doodad_place_skins | 0x39BE7F60 | absent | doodad definition/phase/group |  |
| dyeable_items | 0x39AF9C90 | absent | other gameplay/config data |  |
| farm_group_doodads | 0x39CD5570 | absent | other gameplay/config data |  |
| game_schedule_doodads | 0x39CD8B30 | absent | game schedules/rules |  |
| game_schedule_housings | 0x39CD90E0 | absent | game schedules/rules |  |
| gimmicks | 0x39BDE230/0x39BEDC50 | absent | other gameplay/config data | Y |
| housing_areas | 0x39BDBC00/0x39BED4F0 | absent | housing definitions | Y |
| housing_binding_doodads | 0x39BEAA50 | absent | housing definitions |  |
| housing_build_steps | 0x39BEACA0 | ZoneStatContent.cs | housing definitions |  |
| housing_deco_limit_elems | 0x39BEA5F0 | absent | housing definitions |  |
| housing_deco_limits | 0x39BEA7E0 | absent | housing definitions |  |
| housing_decorations | 0x39BECB70 | absent | housing definitions |  |
| housing_group_categories | 0x39BECEE0 | absent | housing definitions |  |
| housing_groups | 0x39BDDF90/0x39BED0D0 | absent | housing definitions |  |
| housing_pack_members | 0x39C666D0 | absent | housing definitions |  |
| housing_packs | 0x39C66890 | absent | housing definitions |  |
| housing_rebuilding_pack_rebuildings | 0x39BEA150 | absent | housing definitions |  |
| housing_rebuilding_packs | 0x39BEA380 | absent | housing definitions |  |
| housing_rebuildings | 0x39BDDCC0/0x39BE9E00 | absent | housing definitions |  |
| housing_sizes | 0x39BEB2C0 | absent | housing definitions |  |
| housing_ucc_packs | 0x39BDBAE0/0x39BEB4C0 | absent | housing definitions |  |
| housing_ucc_slots | 0x39BEAEE0 | absent | housing definitions |  |
| housing_view_sizes | 0x39BEB110 | absent | housing definitions |  |
| housings | 0x39BDDE30/0x39BEB780 | ZoneStatContent.cs | housing definitions | Y |
| indun_action_change_doodad_phases | 0x39C860A0 | absent | other gameplay/config data |  |
| indun_event_doodad_phase_changeds | 0x39C87BD0 | absent | other gameplay/config data |  |
| indun_event_doodad_spawneds | 0x39C87690 | absent | other gameplay/config data |  |
| instance_point_doodad_phase_changes | 0x39C6E520 | absent | other gameplay/config data |  |
| item_housing_decorations | 0x39ACB650 | absent | item subtype/extra data |  |
| item_housings | 0x39ACB440 | absent | item subtype/extra data |  |
| item_spawn_doodads | 0x39AF56B0 | absent | item subtype/extra data |  |
| quest_act_con_accept_doodads | 0x39D093B0 | absent | quest definition (zone-side hooks) |  |
| quest_act_con_report_doodads | 0x39D09E60 | absent | quest definition (zone-side hooks) |  |
| quest_act_obj_doodad_phase_checks | 0x39D0C150 | absent | quest definition (zone-side hooks) |  |
| quest_doodad_groups | 0x39BDEA50/0x39BF1690 | absent | quest definition (zone-side hooks) |  |
| quest_doodads | 0x39BF14D0 | absent | quest definition (zone-side hooks) |  |
| siege_faction_doodads | 0x39C6B240 | absent | other gameplay/config data |  |
| slave_doodad_bindings | 0x39B0D6C0 | absent | slave (vehicle/pet) data |  |
| slave_drop_doodads | 0x39B0D9F0 | absent | slave (vehicle/pet) data |  |
| slave_healing_point_doodads | 0x39B0DD00 | absent | slave (vehicle/pet) data |  |
| spawn_gimmick_effects | 0x39C0AF40 | absent | effect-type payload table |  |
| sphere_doodad_interacts | 0x39BEEC80 | absent | other gameplay/config data |  |
| system_doodads | 0x39BE9190 | absent | other gameplay/config data |  |
| transfer_binding_doodads | 0x39B0DF80 | absent | transport (carriage/tram) path |  |

#### Items/loot/crafting/economy (150 tables)

| table | native loader | repo reader | used for | zone129 |
|---|---|---|---|---|
| armor_assets | 0x39ADE070 | absent | wearable/holdable formulas |  |
| armor_element_resists | 0x39AF7C30 | absent | wearable/holdable formulas |  |
| backpack_transform_offsets | 0x39AED990 | absent | other gameplay/config data |  |
| character_equip_packs | 0x39AEEBD0/0x39B03BA0 | absent | other gameplay/config data |  |
| coffer_item_categories | 0x39AEDCC0 | absent | other gameplay/config data |  |
| craft_a_categories | 0x39C78F10/0x39C8D870 | absent | crafting recipes |  |
| craft_b_categories | 0x39C78D80/0x39C8D520 | absent | crafting recipes |  |
| craft_c_categories | 0x39C78C50/0x39C8D1F0 | absent | crafting recipes |  |
| craft_d_categories | 0x39C78B20/0x39C8CF80 | absent | crafting recipes |  |
| craft_line_components | 0x39C8E140 | absent | crafting recipes |  |
| craft_lines | 0x39C791D0/0x39C8E2F0 | absent | crafting recipes |  |
| craft_order_coupons | 0x39C6AE70 | absent | crafting recipes |  |
| craft_pack_crafts | 0x39C8E5C0 | absent | crafting recipes |  |
| craft_packs | 0x39C8E780 | absent | crafting recipes |  |
| craft_products | 0x39AE2B10 | absent | crafting recipes |  |
| crafts | 0x39C790A0/0x39C8DC00 | absent | crafting recipes |  |
| currency_configs | 0x39CDC430 | absent | other gameplay/config data |  |
| customizing_item_asset_colors | 0x39B03DC0 | absent | other gameplay/config data |  |
| customizing_item_assets | 0x39CE07F0 | absent | other gameplay/config data |  |
| enchant_scale_ratios | 0x39CC8190/0x39CDDFF0 | ZoneGearScoreContent.cs | other gameplay/config data |  |
| equip_item_set_bonuses | 0x39ADFDA0 | absent | equipment/reinforce data |  |
| equip_item_sets | 0x39AC2400/0x39ADFFF0 | absent | equipment/reinforce data |  |
| equip_pack_cloths | 0x39AFA3C0 | absent | equipment/reinforce data |  |
| equip_pack_weapons | 0x39AFA770 | absent | equipment/reinforce data |  |
| equip_slot_enchanting_costs | 0x39C63E90 | absent | equipment/reinforce data |  |
| equip_slot_group_maps | 0x39AE02E0 | absent | equipment/reinforce data |  |
| equip_slot_groups | 0x39AE04A0 | absent | equipment/reinforce data |  |
| equip_slot_reinforces | 0x39CDE4C0 | ZoneReinforcementContent.cs | equipment/reinforce data | Y |
| faction_power_grades | 0x39C5FD00/0x39C6CDD0 | absent | faction relations |  |
| freshness_group_items | 0x39AC26D0/0x39AE1230 | absent | other gameplay/config data |  |
| fx_group_fx_items | 0x39B46810 | absent | model/actor/fx/sound resource refs |  |
| fx_items | 0x39AEF210/0x39B05C10 | absent | model/actor/fx/sound resource refs |  |
| game_schedule_beautyshops | 0x39CD8EB0 | absent | game schedules/rules |  |
| gear_rank_details | 0x39CD94C0 | absent | other gameplay/config data |  |
| hero_grades | 0x39CCB5E0/0x39CDA5A0 | absent | other gameplay/config data |  |
| holdables | 0x39AC1B00/0x39ADCD10 | ZoneEquipmentCombatContent.cs, ZoneEquipmentContent.cs (partial cols) | wearable/holdable formulas | Y |
| indestructible_items | 0x39AE6480 | absent | other gameplay/config data |  |
| ingameshop_goods_sort_orders | 0x39D15900/0x39D19930 | absent | other gameplay/config data |  |
| ingameshop_modelview_offsets | 0x39D19B40 | absent | other gameplay/config data |  |
| item_accept_quests | 0x39AF5840 | absent | item subtype/extra data |  |
| item_accessories | 0x39AC9DB0 | ZoneEquipmentContent.cs | item subtype/extra data |  |
| item_armor_assets | 0x39AC9860 | absent | item subtype/extra data |  |
| item_armors | 0x39AC2E60/0x39AE33A0 | ZoneEquipmentContent.cs | item subtype/extra data |  |
| item_asset_transforms | 0x39AC9670 | absent | item subtype/extra data |  |
| item_assets | 0x39ABDC90/0x39ADDB70 | absent | item subtype/extra data |  |
| item_backpacks | 0x39ABDDE0/0x39ACB860 | ZoneEquipmentContent.cs | item subtype/extra data |  |
| item_bags | 0x39AE4440 | absent | item subtype/extra data |  |
| item_bless_uthstins | 0x39AF5B90 | absent | item subtype/extra data |  |
| item_body_parts | 0x39ABE060/0x39ACC610 | absent | item subtype/extra data |  |
| item_cap_scale_forbids | 0x39CC8070/0x39B43730,0x39CDDDE0 | absent | item subtype/extra data |  |
| item_categories | 0x39AC21A0/0x39ADEFF0 | absent | item subtype/extra data |  |
| item_change_mapping_groups | 0x39CC7760/0x39CE4410 | absent | item subtype/extra data |  |
| item_change_mappings | 0x39CD7C10 | absent | item subtype/extra data |  |
| item_configs | 0x39B30590 | ZoneEquipmentContent.cs | item subtype/extra data |  |
| item_conv_epacks | 0x39CD1B80 | absent | item subtype/extra data |  |
| item_conv_exception_filters | 0x39CD19C0 | absent | item subtype/extra data |  |
| item_conv_ppack_members | 0x39CD2060 | absent | item subtype/extra data |  |
| item_conv_ppacks | 0x39CD2220 | absent | item subtype/extra data |  |
| item_conv_products | 0x39CD1DF0 | absent | item subtype/extra data |  |
| item_conv_reagent_filters | 0x39CD1230 | absent | item subtype/extra data |  |
| item_conv_reagents | 0x39C8FF00 | absent | item subtype/extra data |  |
| item_conv_rpack_members | 0x39CD14B0 | absent | item subtype/extra data |  |
| item_conv_rpacks | 0x39CD1670 | absent | item subtype/extra data |  |
| item_conv_sets | 0x39CCA0E0/0x39CD2840 | absent | item subtype/extra data |  |
| item_convs | 0x39CD24D0 | absent | item subtype/extra data |  |
| item_elements | 0x39AF2010/0x39AF7A00 | absent | item subtype/extra data |  |
| item_enchant_ratio_groups | 0x39BAC6A0 | absent | item subtype/extra data |  |
| item_enchant_ratio_items | 0x39BAC6A0 | absent | item subtype/extra data |  |
| item_enchant_ratios | 0x39BAC6A0 | absent | item subtype/extra data |  |
| item_enchanting_gems | 0x39AC30F0/0x39AE3DB0 | absent | item subtype/extra data |  |
| item_expedition_attrs | 0x39AE2FF0 | absent | item subtype/extra data |  |
| item_grade_distributions | 0x39ADE660 | absent | item subtype/extra data |  |
| item_grade_enchant_fail_break_reward_categories | 0x39AE6000 | absent | item subtype/extra data |  |
| item_grade_enchant_fail_break_rewards | 0x39AE6250 | absent | item subtype/extra data |  |
| item_grade_enchanting_supports | 0x39AE5CF0 | absent | item subtype/extra data |  |
| item_grades | 0x39AC1D90/0x39ADE330,0x39B46E10 | ZoneEquipmentCombatContent.cs, ZoneEquipmentContent.cs | item subtype/extra data | Y |
| item_groups | 0x39AC1F00/0x39ADE8E0 | absent | item subtype/extra data |  |
| item_guide_a_categories | 0x39AC2930/0x39AE1BD0 | absent | item subtype/extra data |  |
| item_guide_b_categories | 0x39AC2800/0x39AE1960 | absent | item subtype/extra data |  |
| item_guide_icons | 0x39AE2210 | absent | item subtype/extra data |  |
| item_guide_impls | 0x39AC2A60/0x39AE1F00 | absent | item subtype/extra data |  |
| item_guides | 0x39AC2B90/0x39AE2470 | absent | item subtype/extra data |  |
| item_look_convert_holdables | 0x39CD6DF0 | absent | item subtype/extra data |  |
| item_look_convert_required_items | 0x39CD71A0 | absent | item subtype/extra data |  |
| item_look_convert_wearables | 0x39CD6FB0 | absent | item subtype/extra data |  |
| item_look_converts | 0x39CC7630/0x39CD7580 | absent | item subtype/extra data |  |
| item_look_revert_required_items | 0x39CD7390 | absent | item subtype/extra data |  |
| item_open_papers | 0x39AF59D0 | absent | item subtype/extra data |  |
| item_prices | 0x39AE3180 | absent | item subtype/extra data |  |
| item_proc_bindings | 0x39ADFB80 | absent | item subtype/extra data |  |
| item_procs | 0x39AC22D0/0x39ADF340 | absent | item subtype/extra data |  |
| item_rank_assignments | 0x39CDCA00 | absent | item subtype/extra data |  |
| item_rank_details | 0x39CD9650 | absent | item subtype/extra data |  |
| item_recipes | 0x39AF76E0 | absent | item subtype/extra data |  |
| item_rnd_attr_categories | 0x39AF2140/0x39AF8D20 | absent | item subtype/extra data |  |
| item_rnd_attr_category_elements | 0x39AF8AD0 | absent | item subtype/extra data |  |
| item_rnd_attr_category_groups | 0x39AF2270/0x39AF9900 | absent | item subtype/extra data |  |
| item_rnd_attr_category_properties | 0x39AF8880 | ZoneEquipmentContent.cs | item subtype/extra data |  |
| item_rnd_attr_category_relations | 0x39AF7E80 | absent | item subtype/extra data |  |
| item_secure_exceptions | 0x39AFA260 | absent | item subtype/extra data |  |
| item_set_items | 0x39C8FA60 | absent | item subtype/extra data |  |
| item_sets | 0x39C8FC50 | absent | item subtype/extra data |  |
| item_smelting_items | 0x39C8E9F0 | absent | item subtype/extra data |  |
| item_smelting_probs | 0x39B97330 | absent | item subtype/extra data |  |
| item_smeltings | 0x39C8EC00 | absent | item subtype/extra data |  |
| item_socket_chances | 0x39B94E00/0x39B95370 | absent | item subtype/extra data |  |
| item_socket_changes | 0x39CD7EC0 | absent | item subtype/extra data |  |
| item_socket_level_limits | 0x39B4CD40 | absent | item subtype/extra data |  |
| item_socket_num_limits | 0x39B9B6A0 | absent | item subtype/extra data |  |
| item_sockets | 0x39AC3260/0x39AE40D0 | absent | item subtype/extra data |  |
| item_tools | 0x39ACB2B0 | absent | item subtype/extra data |  |
| item_weapons | 0x39ACA6B0 | ZoneEquipmentContent.cs | item subtype/extra data |  |
| items | 0x39AC2CF0/0x39AE9890 | ZoneEquipmentContent.cs, ZoneLootContent.cs (partial cols) | item master | Y |
| loot_groups | 0x39B432C0 | ZoneLootContent.cs | loot tables | Y |
| loot_packs | 0x39BB48C0 | ZoneLootContent.cs | loot tables | Y |
| loots | 0x39BAD690/0x39BB48C0 | ZoneLootContent.cs | loot tables | Y |
| premium_grades | 0x39CC7F50/0x39CDD460 | absent | other gameplay/config data |  |
| quest_act_con_accept_item_equips | 0x39D08E90 | absent | quest definition (zone-side hooks) |  |
| quest_act_con_accept_item_gains | 0x39D08CD0 | absent | quest definition (zone-side hooks) |  |
| quest_act_con_accept_items | 0x39D08A20 | absent | quest definition (zone-side hooks) |  |
| quest_act_etc_item_obtains | 0x39D0B960 | absent | quest definition (zone-side hooks) |  |
| quest_act_obj_crafts | 0x39D0BEF0 | absent | quest definition (zone-side hooks) |  |
| quest_act_obj_enchant_scale_counts | 0x39D10FA0 | absent | quest definition (zone-side hooks) |  |
| quest_act_obj_item_gathers | 0x39BF2500 | absent | quest definition (zone-side hooks) |  |
| quest_act_obj_item_group_gathers | 0x39D069A0 | absent | quest definition (zone-side hooks) |  |
| quest_act_obj_item_group_uses | 0x39D06D20 | absent | quest definition (zone-side hooks) |  |
| quest_act_obj_item_uses | 0x39BDC3B0/0x39BF2B00 | absent | quest definition (zone-side hooks) |  |
| quest_act_obj_sell_backpack_goods | 0x39D0F160 | absent | quest definition (zone-side hooks) |  |
| quest_act_supply_items | 0x39D0A530 | absent | quest definition (zone-side hooks) |  |
| quest_act_supply_ranked_items | 0x39D0A8A0 | absent | quest definition (zone-side hooks) |  |
| quest_act_supply_remove_items | 0x39D0AAB0 | absent | quest definition (zone-side hooks) |  |
| quest_act_supply_result_ranked_items | 0x39D111D0 | absent | quest definition (zone-side hooks) |  |
| quest_act_supply_selective_items | 0x39D0AF90 | absent | quest definition (zone-side hooks) |  |
| quest_item_group_items | 0x39BF0B20 | absent | quest definition (zone-side hooks) |  |
| quest_item_groups | 0x39BDE7F0/0x39BF0D70 | absent | quest definition (zone-side hooks) |  |
| quest_mail_attachment_items | 0x39D12890 | absent | quest definition (zone-side hooks) |  |
| schedule_items | 0x39CCB950/0x39CDBC90 | absent | other gameplay/config data |  |
| siege_items | 0x39C77CA0/0x39C84370 | absent | other gameplay/config data |  |
| sound_pack_items | 0x39BFD470/0x39C058B0 | absent | model/actor/fx/sound resource refs |  |
| specialty_bundle_items | 0x39AE0B10 | absent | other gameplay/config data |  |
| specialty_bundles | 0x39AE0D30 | absent | other gameplay/config data |  |
| specialty_event_triggers | 0x39CCB350/0x39CD83E0 | absent | other gameplay/config data |  |
| specialty_events | 0x39CCB220/0x39CD80C0 | absent | other gameplay/config data |  |
| today_quest_goal_items | 0x39C7FA60 | absent | other gameplay/config data |  |
| tradegood_categories | 0x39AF2810/0x39AFB710 | absent | other gameplay/config data |  |
| tradegood_priceindices | 0x39AC2530/0x39AE0FA0 | absent | other gameplay/config data |  |
| tradegoods | 0x39AFBB40 | absent | other gameplay/config data |  |
| wearable_kinds | 0x39ADD540 | ZoneEquipmentContent.cs | wearable/holdable formulas |  |
| wearable_slots | 0x39ADD1A0/0x39B2DCE0 | ZoneEquipmentContent.cs, ZoneGearScoreContent.cs | wearable/holdable formulas | Y |
| wearables | 0x39B2D950/0x39B2DAC0 | ZoneEquipmentCombatContent.cs | wearable/holdable formulas | Y |

#### NPC/AI (101 tables)

| table | native loader | repo reader | used for | zone129 |
|---|---|---|---|---|
| aggro_effects | 0x39C09E10 | absent | effect-type payload table |  |
| aggro_links | 0x39AFC750 | ZoneNpcContent.cs | other gameplay/config data | Y |
| ai_command_sets | 0x39C75340/0x39C8F4F0 | absent | other gameplay/config data |  |
| ai_commands | 0x39C751D0/0x39C8F290 | absent | other gameplay/config data |  |
| ai_events | 0x39AEE630/0x39AFD1E0 | ZoneNpcSignalContent.cs | other gameplay/config data | Y |
| ai_files | 0x39AEE180/0x39AFC0A0 | ZoneNpcContent.cs | other gameplay/config data | Y |
| bubble_chats | 0x39CCA240/0x39CD2D10 | absent | other gameplay/config data |  |
| bubble_effects | 0x39BFF4C0/0x39C0CE90 | absent | effect-type payload table |  |
| bubbles | 0x39CD3050 | absent | other gameplay/config data |  |
| climates | 0x39BFEDB0/0x39C02E60 | absent | other gameplay/config data |  |
| conflict_zone_npc_kills | 0x39C68C30 | absent | other gameplay/config data |  |
| conflict_zone_npc_spawners | 0x39C68E50 | absent | other gameplay/config data |  |
| craft_materials | 0x39AE2880 | absent | crafting recipes |  |
| custom_dual_materials | 0x39BFC7A0/0x39C038D0 | absent | other gameplay/config data |  |
| doodad_func_animates | 0x39CF4310/0x39CF9020 | absent | doodad interaction function params |  |
| doodad_func_bubbles | 0x39CFC6C0 | absent | doodad interaction function params |  |
| doodad_func_climate_reacts | 0x39CFECD0 | absent | doodad interaction function params |  |
| doodad_func_npc_interaction_relays | 0x39BE2CE0 | absent | doodad interaction function params |  |
| enhanced_item_material_armor_defaults | 0x39C03FF0 | absent | other gameplay/config data |  |
| enhanced_item_material_weapon_defaults | 0x39C03DE0 | absent | other gameplay/config data |  |
| enhanced_item_materials | 0x39BFC9E0/0x39C03C30 | absent | other gameplay/config data |  |
| equip_slot_reinforce_materials | 0x39CCC290/0x39CDE270 | absent | equipment/reinforce data |  |
| faction_change_npc_spawners | 0x39C6F840 | absent | faction relations |  |
| faction_competition_npc_infos | 0x39B42BF0 | absent | faction relations |  |
| fx_materials | 0x39B05A40 | absent | model/actor/fx/sound resource refs |  |
| gain_merchant_reopen_pack_item_effects | 0x39ACF870 | absent | effect-type payload table |  |
| housing_rebuilding_materials | 0x39BE9C10 | absent | housing definitions |  |
| indun_action_remove_tagged_npcs | 0x39C86260 | absent | other gameplay/config data |  |
| indun_event_no_in_aggro_lists | 0x39C87850 | absent | other gameplay/config data |  |
| indun_event_npc_combat_endeds | 0x39C87500 | absent | other gameplay/config data |  |
| indun_event_npc_combat_starteds | 0x39C87370 | absent | other gameplay/config data |  |
| indun_event_npc_info_broadcastings | 0x39C879E0 | absent | other gameplay/config data |  |
| indun_event_npc_killeds | 0x39C87050 | absent | other gameplay/config data |  |
| indun_event_npc_spawneds | 0x39C871E0 | absent | other gameplay/config data |  |
| item_evolving_materials | 0x39ACB0A0 | absent | item subtype/extra data |  |
| item_summon_mates | 0x39ACCA00 | absent | item subtype/extra data |  |
| kill_npc_without_corpse_effects | 0x39C0C840 | absent | effect-type payload table |  |
| loot_pack_dropping_npcs | 0x39B97850 | ZoneLootContent.cs | loot tables | Y |
| mate_equip_pack_groups | 0x39BAFBF0 | absent | other gameplay/config data |  |
| mate_equip_packs | 0x39BAFBF0 | absent | other gameplay/config data |  |
| mate_equip_slot_packs | 0x39AEE2D0/0x39AFC290 | absent | other gameplay/config data |  |
| merchant_packs | 0x39B9B1F0 | absent | other gameplay/config data |  |
| merchant_random_goods | 0x39D18710 | absent | other gameplay/config data |  |
| merchant_random_groups | 0x39D18960 | absent | other gameplay/config data |  |
| merchant_random_packs | 0x39D15640/0x39D18C00 | absent | other gameplay/config data |  |
| merchant_reopen_goods | 0x39D19070 | absent | other gameplay/config data |  |
| merchant_reopen_groups | 0x39D192B0 | absent | other gameplay/config data |  |
| merchant_reopen_packs | 0x39D157D0/0x39D19580 | absent | other gameplay/config data |  |
| merchants | 0x39BA3330 | absent | other gameplay/config data |  |
| mini_scoreboard_condition_npcs | 0x39AFAE40 | absent | other gameplay/config data |  |
| mini_scoreboard_row_npc_hps | 0x39AFA9C0 | absent | other gameplay/config data |  |
| monitor_npcs | 0x39C65EE0 | absent | other gameplay/config data |  |
| npc_aggro_links | 0x39AFC590 | ZoneNpcContent.cs | NPC template/AI/spawn data | Y |
| npc_ai_client_params | 0x39AEE510/0x39AFCBA0 | absent | NPC template/AI/spawn data |  |
| npc_ai_params | 0x39AEE3F0/0x39AFC9F0 | ZoneNpcContent.cs | NPC template/AI/spawn data | Y |
| npc_binding_unit_buffs | 0x39AFE340 | absent | NPC template/AI/spawn data |  |
| npc_chat_bubbles | 0x39AF2A70/0x39AFCF80 | ZoneNpcSignalContent.cs | NPC template/AI/spawn data |  |
| npc_control_effects | 0x39BFDB30/0x39C0F1C0 | absent | NPC template/AI/spawn data |  |
| npc_doodad_bindings | 0x39B0CC10 | absent | NPC template/AI/spawn data | Y |
| npc_grade_configs | 0x39CD69D0 | absent | NPC template/AI/spawn data |  |
| npc_group_members | 0x39AFE6F0 | ZoneNpcContent.cs | NPC template/AI/spawn data | Y |
| npc_groups | 0x39AEE870/0x39AFE9E0 | ZoneNpcContent.cs | NPC template/AI/spawn data | Y |
| npc_hp_split_configs | 0x39D18580 | absent | NPC template/AI/spawn data |  |
| npc_initial_buffs | 0x39B45DD0 | absent | NPC template/AI/spawn data | Y |
| npc_instance_recruiters | 0x39AFF4A0 | absent | NPC template/AI/spawn data |  |
| npc_interaction_sets | 0x39AFE0D0 | absent | NPC template/AI/spawn data | Y |
| npc_interactions | 0x39AFDF10 | absent | NPC template/AI/spawn data | Y |
| npc_mount_skills | 0x39B45FE0 | absent | NPC template/AI/spawn data |  |
| npc_move_to_zone_effect_items | 0x39C0F880 | absent | NPC template/AI/spawn data |  |
| npc_move_to_zone_effects | 0x39C0FAA0 | absent | NPC template/AI/spawn data |  |
| npc_nick_buffs | 0x39B46A10 | absent | NPC template/AI/spawn data | Y |
| npc_nicknames | 0x39AF2F60/0x39B06F30 | absent | NPC template/AI/spawn data | Y |
| npc_posture_sets | 0x39AFDC70 | ZoneNpcPostureContent.cs | NPC template/AI/spawn data | Y |
| npc_postures | 0x39AEE750/0x39AFDA50 | ZoneNpcPostureContent.cs | NPC template/AI/spawn data | Y |
| npc_spawner_despawn_effects | 0x39C0F6D0 | absent | NPC template/AI/spawn data |  |
| npc_spawner_npcs | 0x39AFECF0 | ZoneNpcSpawnerContent.cs | NPC template/AI/spawn data | Y |
| npc_spawner_spawn_effects | 0x39C0F3E0 | absent | NPC template/AI/spawn data |  |
| npc_spawners | 0x39AEE990/0x39AFEFF0 | ZoneNpcSpawnerContent.cs, ZoneNpcSpawnerControllerContent.cs | NPC template/AI/spawn data | Y |
| npc_strafe_params | 0x39AFCD50 | absent | NPC template/AI/spawn data |  |
| npcs | 0x39AF2BA0/0x39B080B0 | NpcTemplateDefinition.cs, ZoneStatContent.cs (partial cols) | NPC templates | Y |
| quest_act_con_accept_npc_emotions | 0x39D04990/0x39D09020 | absent | quest definition (zone-side hooks) |  |
| quest_act_con_accept_npc_groups | 0x39D0F5A0 | absent | quest definition (zone-side hooks) |  |
| quest_act_con_accept_npc_kills | 0x39D0A3A0 | absent | quest definition (zone-side hooks) |  |
| quest_act_con_accept_npcs | 0x39D08460 | absent | quest definition (zone-side hooks) |  |
| quest_act_con_report_npc_groups | 0x39D0F730 | absent | quest definition (zone-side hooks) |  |
| quest_act_con_report_npcs | 0x39D09C30 | absent | quest definition (zone-side hooks) |  |
| quest_act_obj_aggros | 0x39D0D880 | absent | quest definition (zone-side hooks) |  |
| quest_act_obj_consume_evolving_materials | 0x39D10D70 | absent | quest definition (zone-side hooks) |  |
| quest_act_obj_mate_levels | 0x39D0DE50 | absent | quest definition (zone-side hooks) |  |
| quest_act_obj_npc_kills | 0x39D10330 | absent | quest definition (zone-side hooks) |  |
| quest_act_obj_talk_npc_groups | 0x39D072A0 | absent | quest definition (zone-side hooks) |  |
| quest_act_obj_zone_npc_talks | 0x39D06770 | absent | quest definition (zone-side hooks) |  |
| quest_chat_bubbles | 0x39D05660/0x39D05B00 | absent | quest definition (zone-side hooks) |  |
| quest_monster_npcs | 0x39BF1040 | absent | quest definition (zone-side hooks) |  |
| specialty_npcs | 0x39BAC2A0 | absent | other gameplay/config data |  |
| sphere_bubbles | 0x39BEE430 | absent | other gameplay/config data |  |
| sphere_chat_bubbles | 0x39BDE390/0x39BEE110 | absent | other gameplay/config data |  |
| tagged_npcs | 0x39AFE530/0x39B42920 | absent | other gameplay/config data |  |
| tradegood_materials | 0x39AF2940/0x39AFB8E0 | absent | other gameplay/config data |  |
| zone_climate_elems | 0x39C03030 | absent | zone/zone-group metadata | Y |
| zone_climates | 0x39C031F0 | absent | zone/zone-group metadata | Y |

#### Other (116 tables)

| table | native loader | repo reader | used for | zone129 |
|---|---|---|---|---|
| account_attendance_rewards | 0x39C5C560/0x39C66040 | absent | other gameplay/config data |  |
| allowed_name_chars | 0x39B2E660 | absent | other gameplay/config data |  |
| aoe_diminishings | 0x39BDC250 | absent | other gameplay/config data |  |
| aoe_shapes | 0x39C02BD0 | absent | other gameplay/config data |  |
| arche_pass_categories | 0x39C603C0/0x39C6F340 | absent | other gameplay/config data |  |
| arche_pass_tiers | 0x39C6ECA0 | absent | other gameplay/config data |  |
| arche_passes | 0x39C60260/0x39C6EF20 | absent | other gameplay/config data |  |
| attach_point_icons | 0x39B0CEE0 | absent | other gameplay/config data |  |
| attachments | 0x39ABCEF0/0x39AD1FB0 | absent | other gameplay/config data |  |
| bag_expands | 0x39CD6B70 | absent | other gameplay/config data |  |
| ban_slash_commands | 0x39BAFF40 | absent | other gameplay/config data |  |
| book_contents | 0x39CCAD30/0x39CD46C0 | absent | other gameplay/config data |  |
| book_elems | 0x39CE3FB0 | absent | other gameplay/config data |  |
| book_page_contents | 0x39CCAA90/0x39CD41B0 | absent | other gameplay/config data |  |
| book_pages | 0x39CCAC00/0x39CD43F0 | absent | other gameplay/config data |  |
| books | 0x39CCAE60/0x39CD4DA0 | absent | other gameplay/config data |  |
| char_records | 0x39B9BDA0/0x39CE1330 | ZoneAchievementContent.cs | other gameplay/config data |  |
| character_preview_cloths | 0x39AFBE80 | absent | other gameplay/config data |  |
| character_supplies | 0x39B03540 | absent | other gameplay/config data |  |
| characters | 0x39B03750 | absent | other gameplay/config data | Y |
| chat_commands | 0x39CCA630 | absent | other gameplay/config data |  |
| chat_icon_infos | 0x39D1B4A0 | absent | other gameplay/config data |  |
| chat_spam_rule_details | 0x39C5C440/0x39C64450 | absent | other gameplay/config data |  |
| chat_spam_rules | 0x39C646C0 | absent | other gameplay/config data |  |
| cinema_captions | 0x39D057F0/0x39D13ED0 | absent | other gameplay/config data |  |
| cinema_subtitles | 0x39D05920/0x39D14400 | absent | other gameplay/config data |  |
| cinemas | 0x39D04BD0/0x39D140F0 | absent | other gameplay/config data |  |
| common_farms | 0x39CCB0C0/0x39CD5A20 | absent | other gameplay/config data |  |
| constants | 0x39C80E80 | absent | other gameplay/config data | Y |
| content_configs | 0x39B2D810 | ZoneEquipmentContent.cs, ZoneReinforcementContent.cs | other gameplay/config data | Y |
| custom_font_colors | 0x39CC6E10/0x39CD2B20 | absent | other gameplay/config data |  |
| default_action_bar_actions | 0x39B06D70 | absent | other gameplay/config data |  |
| default_inventory_tab_groups | 0x39ADEB60 | absent | other gameplay/config data |  |
| default_inventory_tabs | 0x39AC2070/0x39ADED20 | absent | other gameplay/config data |  |
| districts | 0x39AEF330/0x39B07370 | absent | other gameplay/config data |  |
| emblem_patterns | 0x39C75470/0x39C8F810 | absent | other gameplay/config data |  |
| expand_expert_limits | 0x39B4A790 | absent | other gameplay/config data |  |
| expert_limits | 0x39B47090/0x39B49ED0 | absent | other gameplay/config data |  |
| family_levels | 0x39C5F2D0/0x39C67BD0 | absent | other gameplay/config data |  |
| family_member_limits | 0x39C67E60 | absent | other gameplay/config data |  |
| family_roles | 0x39C5F400/0x39C680A0 | absent | other gameplay/config data |  |
| farm_groups | 0x39CCAF90/0x39CD5760 | absent | other gameplay/config data |  |
| freshness_groups | 0x39AE1480 | absent | other gameplay/config data |  |
| game_activities | 0x39CCCA50/0x39CE1C40 | absent | game schedules/rules |  |
| game_activity_tasks | 0x39CCCCF0/0x39CE2190 | absent | game schedules/rules |  |
| game_condition_types | 0x39CCCF90/0x39CE2450 | absent | game schedules/rules |  |
| game_point_rank_details | 0x39CD9BB0 | absent | game schedules/rules |  |
| game_rank_rules | 0x39C8A1F0 | absent | game schedules/rules |  |
| game_rule_sets | 0x39C91FF0 | absent | game schedules/rules |  |
| game_score_rules | 0x39C89F00 | absent | game schedules/rules |  |
| game_stances | 0x39BFCB00/0x39C04220 | ZoneActorModelContent.cs | game schedules/rules | Y |
| gameplay_participation_configs | 0x39CD92A0 | absent | other gameplay/config data |  |
| glider_transform_offsets | 0x39AED660 | absent | other gameplay/config data |  |
| hand_ik_offsets | 0x39AECFA0 | absent | other gameplay/config data |  |
| hash_checkers | 0x39C5C680/0x39C662C0 | absent | other gameplay/config data |  |
| heavy_taxes | 0x39C682F0 | absent | other gameplay/config data |  |
| heir_levels | 0x39BF06C0 | absent | other gameplay/config data |  |
| hero_bonus_today_assignments | 0x39CDA750 | absent | other gameplay/config data |  |
| hero_bonuses | 0x39CDAC00 | absent | other gameplay/config data |  |
| hero_conditions | 0x39CCB710/0x39CDAED0 | absent | other gameplay/config data |  |
| hero_rewards | 0x39CDA940 | absent | other gameplay/config data |  |
| heros | 0x39CDB230 | absent | other gameplay/config data |  |
| hotkeys | 0x39D15BA0/0x39D1A2A0 | absent | other gameplay/config data |  |
| icons | 0x39BFD590/0x39C06240 | absent | UI/text only (client-oriented) |  |
| integration_distribution_times | 0x39C700D0 | absent | other gameplay/config data |  |
| integration_distributions | 0x39C702F0 | absent | other gameplay/config data |  |
| intensified_expert_limits | 0x39B4AA70 | absent | other gameplay/config data |  |
| levels | 0x39BDBE80 | ZoneSkillContent.cs (partial cols) | unit stats/formulas | Y |
| local_development_board_types | 0x39C5EDA0/0x39C66CB0 | absent | other gameplay/config data |  |
| local_development_boards | 0x39C5EF00/0x39C66ED0 | absent | other gameplay/config data |  |
| local_developments | 0x39B9E8B0 | absent | other gameplay/config data |  |
| member_weights | 0x39BAD4B0 | absent | other gameplay/config data |  |
| mini_scoreboard_conditions | 0x39AFAFF0 | absent | other gameplay/config data |  |
| mini_scoreboard_rows | 0x39AF2540/0x39AFAB70 | absent | other gameplay/config data |  |
| mini_scoreboards | 0x39AF2670/0x39AFB280 | absent | other gameplay/config data |  |
| name_rules | 0x39BB0100 | absent | other gameplay/config data |  |
| options | 0x39D1A050 | absent | other gameplay/config data | Y |
| pcbang_benefit_lists | 0x39CCC0C0/0x39CDDB20 | absent | other gameplay/config data |  |
| premium_benefit_lists | 0x39CCBDC0/0x39CDD730 | absent | other gameplay/config data |  |
| premium_configs | 0x39CE3380 | absent | other gameplay/config data |  |
| raid_recruit_headcounts | 0x39C698C0 | absent | other gameplay/config data |  |
| raid_recruit_sub_types | 0x39C5F660/0x39C692F0 | absent | other gameplay/config data |  |
| raid_recruit_time_and_expenses | 0x39C69A50 | absent | other gameplay/config data |  |
| raid_recruit_types | 0x39C5F7D0/0x39C69580 | absent | other gameplay/config data |  |
| rank_details | 0x39CD9D70 | absent | other gameplay/config data |  |
| rank_resets | 0x39CDA120 | absent | other gameplay/config data |  |
| rank_tiers | 0x39CCB4B0/0x39CDA2E0 | absent | other gameplay/config data |  |
| ranking_rewards | 0x39CC7DA0/0x39CDD1A0 | absent | other gameplay/config data |  |
| ranking_tabs | 0x39CC7C20/0x39CDCF40 | absent | other gameplay/config data |  |
| rankings | 0x39CC79E0/0x39CDCBF0 | absent | other gameplay/config data |  |
| ranks | 0x39B9E110/0x39B9E6F0 | absent | other gameplay/config data |  |
| recommend_abilities | 0x39B98300 | absent | other gameplay/config data |  |
| replace_chat_keys | 0x39CCA500/0x39CD34F0 | absent | other gameplay/config data |  |
| replace_chats | 0x39CD3700 | absent | other gameplay/config data |  |
| reputation_resets | 0x39CDB7E0 | absent | other gameplay/config data |  |
| reputation_rewards | 0x39CDB5F0 | absent | other gameplay/config data |  |
| resident_conditions | 0x39C66B20 | absent | other gameplay/config data |  |
| resident_rewards | 0x39BA5390 | absent | other gameplay/config data |  |
| server_configs | 0x39CCCE60/0x39CE2D90 | absent | other gameplay/config data |  |
| server_infos | 0x39C60880/0x39C71420 | absent | other gameplay/config data |  |
| skin_colors | 0x39AEEE10/0x39B041A0 | absent | other gameplay/config data |  |
| slash_commands | 0x39CCA960/0x39CE3C20 | absent | other gameplay/config data |  |
| specialties | 0x39AE0940 | absent | other gameplay/config data |  |
| spheres | 0x39BDE530/0x39BEF660 | absent | other gameplay/config data |  |
| survey_forms | 0x39B48540/0x39BA1DD0 | absent | other gameplay/config data |  |
| system_feature_controls | 0x39CDB970 | absent | other gameplay/config data |  |
| taxations | 0x39BDDA60/0x39BE9320 | absent | other gameplay/config data |  |
| total_character_customs | 0x39AE5660 | absent | other gameplay/config data |  |
| ucc_applicables | 0x39B4AD50/0x39B97C50 | absent | other gameplay/config data |  |
| ucc_categories | 0x39C5F1A0/0x39C67620 | absent | other gameplay/config data |  |
| ucc_emblems | 0x39C67110 | absent | other gameplay/config data |  |
| ucc_sub_categories | 0x39C5F030/0x39C672D0 | absent | other gameplay/config data |  |
| ulc_guides | 0x39C60B50/0x39C72770 | absent | other gameplay/config data |  |
| ulcs | 0x39C60CB0/0x39C744D0 | absent | other gameplay/config data |  |
| url_whitelists | 0x39C5CD20/0x39C6B090 | absent | other gameplay/config data |  |
| wi_details | 0x39C816F0 | absent | other gameplay/config data |  |

#### Quests/events/factions/instances (147 tables)

| table | native loader | repo reader | used for | zone129 |
|---|---|---|---|---|
| achievement_categories | 0x39C5E9B0/0x39C63BD0 | absent | achievements/titles |  |
| achievement_objectives | 0x39CE1530 | ZoneAchievementContent.cs | achievements/titles |  |
| achievement_sub_categories | 0x39C5E850/0x39C63960 | ZoneAchievementContent.cs | achievements/titles |  |
| achievements | 0x39C5E6B0/0x39C62FF0 | ZoneAchievementContent.cs | achievements/titles |  |
| appellation_levels | 0x39C8AE30 | absent | achievements/titles |  |
| appellation_merits | 0x39C785C0/0x39C8B020 | ZoneAppellationContent.cs | achievements/titles |  |
| appellations | 0x39C78460/0x39C8AB00 | absent | achievements/titles |  |
| battle_field_random_zones | 0x39C782F0/0x39C8A860 | absent | other gameplay/config data |  |
| battle_fields | 0x39B9F0C0/0x39B9F6C0 | ZoneContentDatabase.cs, ZoneSceneContent.cs | other gameplay/config data |  |
| conflict_zone_quest_completions | 0x39C68A10 | absent | other gameplay/config data |  |
| conflict_zone_realtime_schedules | 0x39C690D0 | absent | other gameplay/config data |  |
| conflict_zones | 0x39B43990/0x39BA56B0 | ZoneSceneContent.cs | other gameplay/config data | Y |
| expedition_levels | 0x39C64930 | absent | other gameplay/config data |  |
| faction_change_limit_nums | 0x39C6F650 | absent | faction relations |  |
| faction_chat_regions | 0x39C77B70/0x39C841C0 | absent | faction relations |  |
| faction_power_records | 0x39C6CB70 | absent | faction relations |  |
| festival_zones | 0x39B51EF0/0x39B52280 | ZoneSceneContent.cs | other gameplay/config data |  |
| game_rule_events | 0x39C89CC0 | absent | game schedules/rules |  |
| game_schedule_quests | 0x39CD8970 | absent | game schedules/rules |  |
| game_schedule_spawners | 0x39CD8CF0 | ZoneNpcSpawnerControllerContent.cs | game schedules/rules | Y |
| game_schedules | 0x39B9C4D0 | ZoneNpcSpawnerControllerContent.cs | game schedules/rules | Y |
| hero_schedules | 0x39CDB3D0 | absent | other gameplay/config data |  |
| indun_action_next_rounds | 0x39C865A0 | absent | other gameplay/config data |  |
| indun_action_round_alarms | 0x39C86730 | absent | other gameplay/config data |  |
| indun_action_send_mail_rewards | 0x39C86940 | absent | other gameplay/config data |  |
| indun_action_set_room_cleareds | 0x39C86400 | absent | other gameplay/config data |  |
| indun_actions | 0x39C86AD0 | absent | other gameplay/config data |  |
| indun_event_difficult_changeds | 0x39C87FE0 | absent | other gameplay/config data |  |
| indun_event_no_alive_ch_in_rooms | 0x39C86EC0 | absent | other gameplay/config data |  |
| indun_event_zone_score_level_changeds | 0x39C87DC0 | absent | other gameplay/config data |  |
| indun_events | 0x39C881A0 | absent | other gameplay/config data |  |
| indun_room_spheres | 0x39C85C40 | absent | other gameplay/config data |  |
| indun_rooms | 0x39C85E00 | absent | other gameplay/config data |  |
| indun_rounds | 0x39C88680 | absent | other gameplay/config data |  |
| indun_zones | 0x39B9F7F0/0x39BA0050 | ZoneSceneContent.cs | other gameplay/config data |  |
| instance_difficult_infos | 0x39C6E330 | absent | other gameplay/config data |  |
| instance_entrance_times | 0x39B5BF00 | absent | other gameplay/config data |  |
| instance_faction_presets | 0x39C5CFA0/0x39C6D460 | absent | other gameplay/config data |  |
| instance_factions | 0x39C6D730 | absent | other gameplay/config data |  |
| instance_gain_rules | 0x39C6E9C0 | absent | other gameplay/config data |  |
| instance_mini_scoreboards | 0x39C60130 | absent | other gameplay/config data |  |
| instance_rank_details | 0x39CD9810 | absent | other gameplay/config data |  |
| instance_reward_bonus_counts | 0x39C6D9B0 | absent | other gameplay/config data |  |
| instance_reward_mail_texts | 0x39C5FF90/0x39C6E080 | absent | other gameplay/config data |  |
| instance_rewards | 0x39C6DBA0 | absent | other gameplay/config data |  |
| instance_ui_kinds | 0x39C5FE30/0x39C6CFF0 | absent | other gameplay/config data |  |
| instances | 0x39B5BF00/0x39BA0180,0x39BA1A20 | absent | other gameplay/config data |  |
| model_quest_cameras | 0x39D13B00 | absent | model/actor/fx/sound resource refs |  |
| pre_completed_achievements | 0x39C62E40 | ZoneAchievementContent.cs | other gameplay/config data |  |
| quest_act_check_complete_components | 0x39D082D0 | absent | quest definition (zone-side hooks) |  |
| quest_act_check_distances | 0x39D07A10 | absent | quest definition (zone-side hooks) |  |
| quest_act_check_guards | 0x39D07C40 | absent | quest definition (zone-side hooks) |  |
| quest_act_check_spheres | 0x39D07DD0 | absent | quest definition (zone-side hooks) |  |
| quest_act_check_timers | 0x39D07F60 | absent | quest definition (zone-side hooks) |  |
| quest_act_con_accept_components | 0x39D097B0 | absent | quest definition (zone-side hooks) |  |
| quest_act_con_accept_level_ranges | 0x39D08860 | absent | quest definition (zone-side hooks) |  |
| quest_act_con_accept_level_ups | 0x39D086D0 | absent | quest definition (zone-side hooks) |  |
| quest_act_con_accept_spheres | 0x39D09620 | absent | quest definition (zone-side hooks) |  |
| quest_act_con_accept_uis | 0x39D0F440 | absent | quest definition (zone-side hooks) |  |
| quest_act_con_auto_completes | 0x39D09AD0 | absent | quest definition (zone-side hooks) |  |
| quest_act_con_fails | 0x39D0A1F0 | absent | quest definition (zone-side hooks) |  |
| quest_act_con_report_journals | 0x39D0A090 | absent | quest definition (zone-side hooks) |  |
| quest_act_obj_aliases | 0x39BDECB0/0x39BF1DF0 | absent | quest definition (zone-side hooks) |  |
| quest_act_obj_cinemas | 0x39D0E550 | absent | quest definition (zone-side hooks) |  |
| quest_act_obj_complete_quest_groups | 0x39D0C620 | absent | quest definition (zone-side hooks) |  |
| quest_act_obj_complete_quests | 0x39D0C390 | absent | quest definition (zone-side hooks) |  |
| quest_act_obj_conditions | 0x39D060A0 | absent | quest definition (zone-side hooks) |  |
| quest_act_obj_conquest_wars | 0x39D0E780 | absent | quest definition (zone-side hooks) |  |
| quest_act_obj_distances | 0x39D05E30 | absent | quest definition (zone-side hooks) |  |
| quest_act_obj_express_fires | 0x39D07060 | absent | quest definition (zone-side hooks) |  |
| quest_act_obj_gain_exp_points | 0x39D10A50 | absent | quest definition (zone-side hooks) |  |
| quest_act_obj_gain_honor_points | 0x39D0EE40 | absent | quest definition (zone-side hooks) |  |
| quest_act_obj_gain_living_points | 0x39D0EFD0 | absent | quest definition (zone-side hooks) |  |
| quest_act_obj_interactions | 0x39BF2D90 | absent | quest definition (zone-side hooks) |  |
| quest_act_obj_invite_team_factions | 0x39D0E9D0 | absent | quest definition (zone-side hooks) |  |
| quest_act_obj_labor_powers | 0x39D0EC10 | absent | quest definition (zone-side hooks) |  |
| quest_act_obj_levels | 0x39D0E0E0 | absent | quest definition (zone-side hooks) |  |
| quest_act_obj_monster_contr_group_hunts | 0x39D07770 | absent | quest definition (zone-side hooks) |  |
| quest_act_obj_monster_contr_hunts | 0x39D074D0 | absent | quest definition (zone-side hooks) |  |
| quest_act_obj_monster_group_hunts | 0x39BF2260 | absent | quest definition (zone-side hooks) |  |
| quest_act_obj_monster_hunts | 0x39BF1FC0 | absent | quest definition (zone-side hooks) |  |
| quest_act_obj_pc_kills | 0x39D10790 | absent | quest definition (zone-side hooks) |  |
| quest_act_obj_send_mails | 0x39D0DBC0 | absent | quest definition (zone-side hooks) |  |
| quest_act_obj_spheres | 0x39BDEDE0/0x39BF3080 | absent | quest definition (zone-side hooks) |  |
| quest_act_obj_talks | 0x39BF2870 | absent | quest definition (zone-side hooks) |  |
| quest_act_obj_zone_kills | 0x39D0BBB0 | absent | quest definition (zone-side hooks) |  |
| quest_act_obj_zone_monster_hunts | 0x39D062D0 | absent | quest definition (zone-side hooks) |  |
| quest_act_obj_zone_quest_completes | 0x39D06520 | absent | quest definition (zone-side hooks) |  |
| quest_act_supply_aa_points | 0x39D0CEF0 | absent | quest definition (zone-side hooks) |  |
| quest_act_supply_actabilities | 0x39D0D530 | absent | quest definition (zone-side hooks) |  |
| quest_act_supply_appellations | 0x39D0B4B0 | absent | quest definition (zone-side hooks) |  |
| quest_act_supply_arche_pass_points | 0x39D0FF20 | absent | quest definition (zone-side hooks) |  |
| quest_act_supply_contribution_points | 0x39D0D3A0 | absent | quest definition (zone-side hooks) |  |
| quest_act_supply_coppers | 0x39D0AE00 | absent | quest definition (zone-side hooks) |  |
| quest_act_supply_crime_points | 0x39D0CBD0 | absent | quest definition (zone-side hooks) |  |
| quest_act_supply_expedition_exps | 0x39D0D210 | absent | quest definition (zone-side hooks) |  |
| quest_act_supply_exps | 0x39D0AC70 | absent | quest definition (zone-side hooks) |  |
| quest_act_supply_faction_changes | 0x39D0F960 | absent | quest definition (zone-side hooks) |  |
| quest_act_supply_family_exps | 0x39D0D6F0 | absent | quest definition (zone-side hooks) |  |
| quest_act_supply_honor_points | 0x39D0C8B0 | absent | quest definition (zone-side hooks) |  |
| quest_act_supply_interactions | 0x39D0B320 | absent | quest definition (zone-side hooks) |  |
| quest_act_supply_jury_points | 0x39D0CD60 | absent | quest definition (zone-side hooks) |  |
| quest_act_supply_living_points | 0x39D0CA40 | absent | quest definition (zone-side hooks) |  |
| quest_act_supply_local_lps | 0x39D0B7D0 | absent | quest definition (zone-side hooks) |  |
| quest_act_supply_lps | 0x39D0B640 | absent | quest definition (zone-side hooks) |  |
| quest_act_supply_reset_quests | 0x39D10BE0 | absent | quest definition (zone-side hooks) |  |
| quest_act_supply_resident_charges | 0x39D0FD60 | absent | quest definition (zone-side hooks) |  |
| quest_act_supply_resident_points | 0x39D0FBA0 | absent | quest definition (zone-side hooks) |  |
| quest_acts | 0x39D11430 | absent | quest definition (zone-side hooks) |  |
| quest_cameras | 0x39D04AB0/0x39D13750 | absent | quest definition (zone-side hooks) |  |
| quest_categories | 0x39BDE690/0x39BF0900 | absent | quest definition (zone-side hooks) |  |
| quest_component_texts | 0x39D054C0/0x39D13CB0 | absent | quest definition (zone-side hooks) |  |
| quest_components | 0x39C748A0/0x39C7DA70 | absent | quest definition (zone-side hooks) |  |
| quest_context_group_members | 0x39BF1960 | absent | quest definition (zone-side hooks) |  |
| quest_context_groups | 0x39BDEB80/0x39BF1B20 | absent | quest definition (zone-side hooks) |  |
| quest_context_texts | 0x39C77160/0x39C7AFF0 | absent | quest definition (zone-side hooks) |  |
| quest_contexts | 0x39C773C0/0x39C90450 | absent | quest definition (zone-side hooks) |  |
| quest_mail_attachments | 0x39D12AA0 | absent | quest definition (zone-side hooks) |  |
| quest_mail_sends | 0x39D12FB0 | absent | quest definition (zone-side hooks) |  |
| quest_mails | 0x39D05320/0x39D12D10 | absent | quest definition (zone-side hooks) |  |
| quest_monster_groups | 0x39BDE920/0x39BF1200 | absent | quest definition (zone-side hooks) |  |
| quest_names | 0x39C77290/0x39C7B210 | absent | quest definition (zone-side hooks) |  |
| quest_supplies | 0x39C7E750 | absent | quest definition (zone-side hooks) |  |
| rank_events | 0x39B528B0/0x39B52C10 | absent | other gameplay/config data |  |
| saga_quest_groups | 0x39C604F0/0x39C6FDA0 | absent | other gameplay/config data |  |
| saga_quests | 0x39C6FBF0 | absent | other gameplay/config data |  |
| siege_extortion_ratios | 0x39C6BCA0 | absent | other gameplay/config data |  |
| siege_faction_dominions | 0x39C6B400 | absent | other gameplay/config data |  |
| siege_faction_troops | 0x39C6B710 | absent | other gameplay/config data |  |
| siege_factions | 0x39C6BA00 | absent | other gameplay/config data |  |
| siege_plans | 0x39B5B3B0 | absent | other gameplay/config data |  |
| siege_zones | 0x39B523B0 | absent | other gameplay/config data |  |
| sphere_accept_quest_quests | 0x39BEEE30 | absent | other gameplay/config data |  |
| sphere_accept_quests | 0x39BEEFF0 | absent | other gameplay/config data |  |
| sphere_quest_mails | 0x39BEF280 | absent | other gameplay/config data |  |
| sphere_quests | 0x39BEE6C0 | absent | other gameplay/config data |  |
| survey_form_question_options | 0x39C60E50/0x39C72A30 | absent | other gameplay/config data |  |
| survey_form_questions | 0x39D15510/0x39D18290 | absent | other gameplay/config data |  |
| system_faction_relations | 0x39C067E0 | ZoneFactionContent.cs | other gameplay/config data | Y |
| system_factions | 0x39BFF180 | ZoneFactionContent.cs | other gameplay/config data | Y |
| system_mail_template | 0x39CE2AC0 | absent | other gameplay/config data |  |
| today_quest_goals | 0x39C749D0/0x39C7FC80 | absent | other gameplay/config data |  |
| today_quest_group_quests | 0x39B50E60 | absent | other gameplay/config data |  |
| today_quest_groups | 0x39C774F0/0x39C7E950 | absent | other gameplay/config data |  |
| today_quest_steps | 0x39C77650/0x39C7F160 | absent | other gameplay/config data |  |
| zone_group_dummy_factions | 0x39C888F0 | absent | zone/zone-group metadata |  |
| zone_waiting_factions | 0x39CE0D10 | absent | zone/zone-group metadata |  |

#### Skills/effects/stats (150 tables)

| table | native loader | repo reader | used for | zone129 |
|---|---|---|---|---|
| ability_preview_packs | 0x39C6C3A0 | absent | other gameplay/config data |  |
| accept_quest_effects | 0x39C0BD40 | absent | effect-type payload table |  |
| account_attribute_effects | 0x39C0D3F0 | absent | effect-type payload table |  |
| account_buffs | 0x39C678F0 | absent | other gameplay/config data |  |
| actability_groups | 0x39C789F0/0x39C8CC40 | absent | other gameplay/config data |  |
| actability_view_group_elems | 0x39C8C7D0 | absent | other gameplay/config data |  |
| actability_view_groups | 0x39C788C0/0x39C8C980 | absent | other gameplay/config data |  |
| armor_grade_buffs | 0x39CDC230 | absent | wearable/holdable formulas |  |
| battle_field_killstreak_skills | 0x39C89A40 | absent | other gameplay/config data |  |
| battle_field_pick_buffs | 0x39C8A6A0 | absent | other gameplay/config data |  |
| buff_breakers | 0x39B41DE0 | absent | buff data/triggers/modifiers |  |
| buff_effects | 0x39C0A130 | absent | buff data/triggers/modifiers |  |
| buff_modifiers | 0x39C14A60 | absent | buff data/triggers/modifiers | Y |
| buff_passive_buffs | 0x39C0A5C0 | ZoneBuffContent.cs | buff data/triggers/modifiers |  |
| buff_skills | 0x39B45BC0 | absent | buff data/triggers/modifiers |  |
| buff_swap_skills | 0x39AD3760 | absent | buff data/triggers/modifiers |  |
| buff_tick_effects | 0x39AD2D30 | absent | buff data/triggers/modifiers | Y |
| buff_tolerance_steps | 0x39AD7DC0 | absent | buff data/triggers/modifiers |  |
| buff_tolerances | 0x39AD7FC0 | absent | buff data/triggers/modifiers |  |
| buff_triggers | 0x39AD2320 | absent | buff data/triggers/modifiers | Y |
| buff_unit_modifiers | 0x39C14260 | ZoneBuffContent.cs | buff data/triggers/modifiers |  |
| buff_visual_changes | 0x39C0A350 | absent | buff data/triggers/modifiers |  |
| buffs | 0x39AC1930? | ZoneBuffContent.cs (partial cols) | buff data/triggers/modifiers | Y |
| change_equipment_buffs | 0x39C15300 | absent | other gameplay/config data |  |
| char_transform_effects | 0x39C10930 | absent | effect-type payload table |  |
| character_default_skills | 0x39B46610 | absent | other gameplay/config data | Y |
| character_idle_buffs | 0x39C66520 | absent | other gameplay/config data | Y |
| cinema_effects | 0x39C0BED0 | absent | effect-type payload table |  |
| cleanup_ucc_effects | 0x39C0DDC0 | absent | effect-type payload table |  |
| combat_buffs | 0x39AD8290 | absent | other gameplay/config data | Y |
| combat_resource_effects | 0x39C0FEC0 | absent | effect-type payload table |  |
| combat_resource_groups | 0x39C60A20/0x39C72430 | absent | other gameplay/config data |  |
| combat_resources | 0x39C5D0F0/0x39C71720 | absent | other gameplay/config data |  |
| combat_sounds | 0x39C05FF0 | absent | other gameplay/config data |  |
| conversion_effects | 0x39C0BB00 | absent | effect-type payload table |  |
| craft_effects | 0x39C0C6B0 | absent | effect-type payload table |  |
| damage_effects | 0x39C07880 | absent | effect-type payload table |  |
| deco_actability_groups | 0x39BDDB90/0x39BE99E0 | absent | other gameplay/config data |  |
| default_skills | 0x39B06B00 | absent | other gameplay/config data |  |
| dispel_effects | 0x39C0CAB0 | absent | effect-type payload table |  |
| dynamic_funcs | 0x39C06D80 | ZoneBuffContent.cs | other gameplay/config data |  |
| dynamic_unit_modifiers | 0x39C07120 | ZoneBuffContent.cs | other gameplay/config data |  |
| effects | 0x39AD0220 | absent | effect id->type dispatch | Y |
| equip_item_attr_modifiers | 0x39BFC680/0x39C03460 | ZoneEquipmentContent.cs | equipment/reinforce data |  |
| equip_slot_reinforce_bundle_effects | 0x39CCC4F0 | ZoneReinforcementContent.cs | effect-type payload table |  |
| equip_slot_reinforce_level_effects | 0x39CDF940 | absent | effect-type payload table |  |
| equip_slot_reinforce_set_effects | 0x39CCC3C0/0x39CDE750 | ZoneReinforcementContent.cs | effect-type payload table |  |
| equip_slot_reinforce_unit_modifiers | 0x39CDF6F0 | ZoneReinforcementContent.cs | equipment/reinforce data |  |
| expedition_buff_grades | 0x39C5EAE0/0x39C64BE0 | absent | other gameplay/config data |  |
| expedition_buffs | 0x39C5EC10/0x39C73A40 | absent | other gameplay/config data |  |
| extend_charge_effects | 0x39C10100 | absent | effect-type payload table |  |
| flying_state_change_effects | 0x39C0DC10 | absent | effect-type payload table |  |
| formula_funcs | 0x39BFD6B0/0x39C06F40 | ZoneBuffContent.cs | unit stats/formulas |  |
| formulas | 0x39C74C10/0x39C81030 | ZoneGearScoreContent.cs | unit stats/formulas | Y |
| gain_loot_pack_item_effects | 0x39C0C430 | absent | effect-type payload table |  |
| game_activity_stages | 0x39CCCBC0/0x39CE1F20 | absent | game schedules/rules |  |
| game_safe_area_buffs | 0x39C8A4B0 | absent | game schedules/rules |  |
| gem_visual_effects | 0x39BFC8C0/0x39C03A80 | absent | effect-type payload table |  |
| grammar_tag_none_types | 0x39CCBC50/0x39CDC5C0 | absent | other gameplay/config data |  |
| heal_effects | 0x39C08BF0 | absent | effect-type payload table |  |
| heir_skill_details | 0x39C5F530/0x39C684C0 | absent | other gameplay/config data |  |
| heir_skills | 0x39C68780 | absent | other gameplay/config data |  |
| honor_point_war_state_texts | 0x39D15A30/0x39D19E20 | absent | other gameplay/config data |  |
| imprint_ucc_effects | 0x39C0C2A0 | absent | effect-type payload table |  |
| impulse_effects | 0x39C0B450 | absent | effect-type payload table |  |
| instance_permission_tags | 0x39C6D270 | absent | other gameplay/config data |  |
| interaction_effects | 0x39C0C070 | absent | effect-type payload table |  |
| item_grade_buffs | 0x39ADD760 | absent | item subtype/extra data |  |
| item_grade_skills | 0x39ADD970 | absent | item subtype/extra data |  |
| item_rnd_attr_unit_modifier_group_sets | 0x39AEDF40/0x39AF8570 | absent | item subtype/extra data |  |
| item_rnd_attr_unit_modifier_groups | 0x39AF8250 | ZoneEquipmentContent.cs | item subtype/extra data |  |
| item_rnd_attr_unit_modifiers | 0x39AF8040 | ZoneEquipmentContent.cs | item subtype/extra data |  |
| level_up_effects | 0x39C0EB60 | absent | effect-type payload table |  |
| linear_funcs | 0x39C069D0 | ZoneBuffContent.cs | other gameplay/config data |  |
| login_stage_abilities | 0x39C5CE40/0x39C6C090 | absent | other gameplay/config data |  |
| loot_actability_groups | 0x39B43500 | ZoneLootContent.cs | loot tables |  |
| mana_burn_effects | 0x39C0B700 | absent | effect-type payload table |  |
| manual_funcs | 0x39C06B90 | ZoneBuffContent.cs | other gameplay/config data |  |
| move_to_location_effects | 0x39C0FD10 | absent | effect-type payload table |  |
| move_to_rez_point_effects | 0x39C0EED0 | absent | effect-type payload table |  |
| np_passive_buffs | 0x39B0C990 | absent | other gameplay/config data |  |
| np_skills | 0x39B0C6C0 | absent | other gameplay/config data |  |
| open_portal_effects | 0x39C0E360 | absent | effect-type payload table |  |
| passive_buffs | 0x39B07BB0 | ZoneBuffContent.cs | other gameplay/config data | Y |
| pcbang_buffs | 0x39AE1710 | absent | other gameplay/config data |  |
| physical_explosion_effects | 0x39C0B240 | absent | effect-type payload table |  |
| play_log_effects | 0x39BFD8F0/0x39C0A770 | absent | effect-type payload table |  |
| plot_aoe_conditions | 0x39C82110 | absent | skill plot (script) graph |  |
| plot_auction_config | 0x39CE2740 | absent | skill plot (script) graph |  |
| plot_conditions | 0x39C81920 | absent | skill plot (script) graph |  |
| plot_effects | 0x39C825B0 | absent | effect-type payload table |  |
| plot_event_conditions | 0x39C82340 | absent | skill plot (script) graph |  |
| plot_events | 0x39C74E60/0x39C83610 | absent | skill plot (script) graph |  |
| plot_next_events | 0x39C83170 | absent | skill plot (script) graph |  |
| plots | 0x39C74F90/0x39C83EF0 | absent | skill plot (script) graph |  |
| priest_buffs | 0x39B07970 | absent | other gameplay/config data |  |
| projectile_params | 0x39B0EA80 | absent | other gameplay/config data |  |
| projectiles | 0x39B06380 | absent | other gameplay/config data |  |
| put_down_backpack_effects | 0x39C0F030 | absent | effect-type payload table |  |
| quest_act_con_accept_buffs | 0x39D09940 | absent | quest definition (zone-side hooks) |  |
| quest_act_con_accept_skills | 0x39D09220 | absent | quest definition (zone-side hooks) |  |
| quest_act_obj_ability_levels | 0x39D0E310 | absent | quest definition (zone-side hooks) |  |
| quest_act_obj_effect_fires | 0x39ACFA30 | absent | quest definition (zone-side hooks) |  |
| quest_act_supply_skills | 0x39D0B190 | absent | quest definition (zone-side hooks) |  |
| recover_exp_effects | 0x39C0E910 | absent | effect-type payload table |  |
| report_crime_effects | 0x39C0CCD0 | absent | effect-type payload table |  |
| reset_aoe_diminishing_effects | 0x39C0ED70 | absent | effect-type payload table |  |
| restore_mana_effects | 0x39C09B40 | absent | effect-type payload table |  |
| scoped_f_effects | 0x39BFDA10/0x39C0D830 | absent | effect-type payload table |  |
| selective_item_effect_elems | 0x39ACFC90 | absent | other gameplay/config data |  |
| selective_item_effects | 0x39ABCD90/0x39ACFEB0 | absent | effect-type payload table |  |
| siege_skills | 0x39C77DD0/0x39C846F0 | absent | other gameplay/config data |  |
| skill_alert_conditions | 0x39AD1980 | absent | skill definitions/reqs/tags |  |
| skill_controllers | 0x39BFD7D0/0x39C00E30 | absent | skill definitions/reqs/tags |  |
| skill_effects | 0x39AD0E90 | absent | skill definitions/reqs/tags | Y |
| skill_map_effects | 0x39BFDC60 | absent | skill definitions/reqs/tags |  |
| skill_modifiers | 0x39C146E0 | absent | skill definitions/reqs/tags |  |
| skill_products | 0x39C07690 | absent | skill definitions/reqs/tags |  |
| skill_reagents | 0x39C074A0 | absent | skill definitions/reqs/tags |  |
| skill_req_skill_tags | 0x39B50AD0 | absent | skill definitions/reqs/tags |  |
| skill_req_skills | 0x39B41840 | absent | skill definitions/reqs/tags |  |
| skill_reqs | 0x39BFF320/0x39C10D20 | absent | skill definitions/reqs/tags |  |
| skill_synergy_buff_tags | 0x39AD17C0 | absent | skill definitions/reqs/tags |  |
| skill_synergy_icons | 0x39AC1690/0x39AD1520 | absent | skill definitions/reqs/tags |  |
| skill_visual_groups | 0x39AEC800 | absent | skill definitions/reqs/tags |  |
| skills | 0x39AC17C0/0x39AE6D50 | ZoneSkillContent.cs (partial cols) | skill definitions/reqs/tags | Y |
| slash_functions | 0x39CD3CF0 | absent | other gameplay/config data |  |
| spawn_effects | 0x39C0ABD0 | absent | effect-type payload table |  |
| special_effects | 0x39C0A920 | absent | effect-type payload table |  |
| sphere_buffs | 0x39BEF430 | absent | other gameplay/config data |  |
| sphere_skills | 0x39BEEA80 | absent | other gameplay/config data |  |
| tagged_buffs | 0x39AD2160/0x39B42380 | ZoneBuffContent.cs | other gameplay/config data | Y |
| tagged_immune_buffs | 0x39B420B0 | absent | other gameplay/config data |  |
| tagged_items | 0x39AE0780/0x39B42650 | absent | other gameplay/config data |  |
| tagged_require_buffs | 0x39B50C90 | absent | other gameplay/config data |  |
| tagged_skills | 0x39AD1DE0/0x39B41B10 | absent | other gameplay/config data | Y |
| tags | 0x39BFF050/0x39C05DF0 | absent | other gameplay/config data | Y |
| target_history_clear_effects | 0x39C107D0 | absent | effect-type payload table |  |
| target_unit_modifiers | (SQL str 0x3A146480) | absent | other gameplay/config data |  |
| tooltip_skill_effects | 0x39AD1BC0 | absent | effect-type payload table |  |
| unit_attribute_limits | 0x39B2EA30 | ZoneStatContent.cs | unit stats/formulas | Y |
| unit_formula_variables | 0x39C81210 | ZoneStatContent.cs | unit stats/formulas | Y |
| unit_formulas | 0x39C74D30/0x39C81430 | ZoneStatContent.cs | unit stats/formulas | Y |
| unit_modifiers | 0x39C13CF0 | ZoneStatContent.cs | unit stats/formulas | Y |
| unit_reqs | 0x39C15580 | ZoneNpcSignalContent.cs | unit stats/formulas | Y |
| unit_status_buff_tags | 0x39C6AC70 | absent | unit stats/formulas |  |
| wearable_formulas | 0x39ABDB70/0x39ADD390 | ZoneEquipmentCombatContent.cs | wearable/holdable formulas |  |
| world_level_exp_modifiers | 0x39B8F960/0x39D1BA70 | absent | world/map division config |  |
| world_message_effects | 0x39BFF5F0/0x39C0D0A0 | absent | effect-type payload table |  |
| zone_group_banned_tags | 0x39CCC750/0x39CE0AC0 | absent | zone/zone-group metadata |  |

#### Slaves/vehicles/transport (61 tables)

| table | native loader | repo reader | used for | zone129 |
|---|---|---|---|---|
| buff_mount_skills | 0x39B459B0 | absent | buff data/triggers/modifiers |  |
| butler_equip_items | 0x39C710D0 | absent | other gameplay/config data |  |
| butler_equip_slots | 0x39C70F10 | absent | other gameplay/config data |  |
| butler_func_garden_expand_slots | 0x39C70A10 | absent | other gameplay/config data |  |
| butler_func_trade_expand_slots | 0x39C70580 | absent | other gameplay/config data |  |
| butler_harvest_grades | 0x39BDE100/0x39BED790 | absent | other gameplay/config data |  |
| butler_harvests | 0x39AE2D70 | absent | other gameplay/config data |  |
| butler_levels | 0x39C60620/0x39C70C50 | absent | other gameplay/config data |  |
| butler_look_items | 0x39C71290 | absent | other gameplay/config data |  |
| butler_specialty_trades | 0x39C707C0 | absent | other gameplay/config data |  |
| butlers | 0x39C60750/0x39C73DB0 | absent | other gameplay/config data |  |
| competition_pves | 0x39C5FA60/0x39C6C720 | absent | other gameplay/config data |  |
| competition_pvps | 0x39C6C5C0 | absent | other gameplay/config data |  |
| competition_tower_defs | 0x39C6C980 | absent | other gameplay/config data |  |
| faction_competition_quest_infos | 0x39B42EC0 | absent | faction relations |  |
| faction_competitions | 0x39C5FBD0/0x39C734C0 | absent | faction relations |  |
| fish_details | 0x39CF5CD0 | absent | other gameplay/config data |  |
| guard_tower_settings | 0x39BE97C0 | absent | other gameplay/config data |  |
| guard_tower_steps | 0x39BE9590 | absent | other gameplay/config data |  |
| item_shipyards | 0x39AF7870 | absent | item subtype/extra data |  |
| item_slave_equipment_grade_spawns | 0x39AF23A0/0x39AF5E10 | absent | item subtype/extra data |  |
| item_slave_equipments | 0x39AF6060 | ZoneEquipmentContent.cs | item subtype/extra data |  |
| item_summon_slaves | 0x39ACCB90 | absent | item subtype/extra data |  |
| mount_attached_skills | 0x39BF01B0 | absent | other gameplay/config data |  |
| mount_poses | 0x39BFCFF0/0x39C05040 | absent | other gameplay/config data |  |
| mount_skills | 0x39BF03A0 | absent | other gameplay/config data |  |
| quest_act_obj_faction_competitions | 0x39D100B0 | absent | quest definition (zone-side hooks) |  |
| quest_act_supply_leadership_points | 0x39D0D080 | absent | quest definition (zone-side hooks) |  |
| repair_slave_effects | 0x39C0E750 | absent | effect-type payload table |  |
| server_transfer_restricted_items | 0x39C6FA30 | absent | other gameplay/config data |  |
| ship_models | 0x39ABD740/0x39ADAF50 | ZoneModelContent.cs | ship model physics |  |
| shipyard_rewards | 0x39C8BDE0 | absent | ship model physics |  |
| shipyard_steps | 0x39C8C050 | absent | ship model physics |  |
| shipyards | 0x39C78760/0x39C8C2C0 | ZoneStatContent.cs | ship model physics |  |
| slave_bindings | 0x39B0D3E0 | absent | slave (vehicle/pet) data |  |
| slave_collision_damages | 0x39AFFDD0 | absent | slave (vehicle/pet) data |  |
| slave_customizing_equip_slots | 0x39CCC620/0x39CE0330 | absent | slave (vehicle/pet) data |  |
| slave_customizings | 0x39CE0560 | absent | slave (vehicle/pet) data |  |
| slave_equip_kind_lists | 0x39AFA040 | ZoneEquipmentContent.cs | slave (vehicle/pet) data |  |
| slave_equip_kinds | 0x39AEE060/0x39AF9E50 | absent | slave (vehicle/pet) data |  |
| slave_equip_slots | 0x39AFF650 | ZoneEquipmentContent.cs | slave (vehicle/pet) data |  |
| slave_extend_model_links | 0x39ABD010/0x39AD8790 | ZoneModelAuxiliaryContent.cs | slave (vehicle/pet) data |  |
| slave_extend_models | 0x39AD8620 | ZoneModelAuxiliaryContent.cs | slave (vehicle/pet) data |  |
| slave_initial_buffs | 0x39B461F0 | absent | slave (vehicle/pet) data |  |
| slave_initial_item_packs | 0x39AEEAB0/0x39AFFB10 | absent | slave (vehicle/pet) data |  |
| slave_initial_items | 0x39AFF920 | absent | slave (vehicle/pet) data |  |
| slave_interaction_skills | 0x39B00040 | absent | slave (vehicle/pet) data |  |
| slave_mount_skills | 0x39B46400 | absent | slave (vehicle/pet) data |  |
| slave_passive_buffs | 0x39B0D160 | absent | slave (vehicle/pet) data |  |
| slaves | 0x39AF2D00/0x39B002E0 | ZoneEquipmentContent.cs, ZoneStatContent.cs | slave (vehicle/pet) data | Y |
| spawn_fish_effects | 0x39C0DA50 | absent | effect-type payload table |  |
| tower_def_map_events | 0x39C85990 | absent | other gameplay/config data |  |
| tower_def_prog_alive_targets | 0x39C85070 | absent | other gameplay/config data |  |
| tower_def_prog_kill_targets | 0x39C84D80 | absent | other gameplay/config data |  |
| tower_def_prog_spawn_targets | 0x39C84A70 | ZoneNpcSpawnerControllerContent.cs | other gameplay/config data |  |
| tower_def_progs | 0x39C77F00/0x39C85360 | ZoneNpcSpawnerControllerContent.cs | other gameplay/config data |  |
| tower_defs | 0x39B9D1F0/0x39B9DDA0 | ZoneNpcSpawnerControllerContent.cs | other gameplay/config data |  |
| transfer_bindings | 0x39B0E200 | absent | transport (carriage/tram) path | Y |
| transfer_paths | 0x39B0E730/0x39B0E480 | absent | transport (carriage/tram) path | Y |
| transfers | 0x39AF2E30/0x39B02550 | ZoneStatContent.cs | transport (carriage/tram) path | Y |
| vehicle_models | 0x39ABD410/0x39AD9960 | ZoneModelContent.cs | vehicle model physics |  |

#### World/zone/models/assets (76 tables)

| table | native loader | repo reader | used for | zone129 |
|---|---|---|---|---|
| actor_models | 0x39ABD220/0x39AD8AD0 | ZoneActorModelContent.cs | model/actor/fx/sound resource refs | Y |
| anim_actions | 0x39BFCE70/0x39C04BB0 | absent | model/actor/fx/sound resource refs |  |
| anim_rules | 0x39C04970 | absent | model/actor/fx/sound resource refs |  |
| anims | 0x39BFCC30/0x39C04620 | absent | model/actor/fx/sound resource refs |  |
| attach_anims | 0x39AECD20 | absent | other gameplay/config data |  |
| attachment_anims | 0x39AECA90 | absent | other gameplay/config data |  |
| blocked_texts | 0x39BFD110/0x39C051F0 | absent | other gameplay/config data |  |
| body_diffuse_maps | 0x39CC6F30/0x39CD5C80 | absent | other gameplay/config data |  |
| body_normal_maps | 0x39CC7090/0x39CD5EB0 | absent | other gameplay/config data |  |
| custom_face_presets | 0x39AE5390 | absent | other gameplay/config data |  |
| custom_hair_textures | 0x39CC82C0/0x39CCED20 | absent | other gameplay/config data |  |
| district_return_points | 0x39B07180 | absent | other gameplay/config data |  |
| express_texts | 0x39CCA760/0x39CD3E80 | absent | other gameplay/config data |  |
| face_decal_assets | 0x39ABDF10/0x39AE5030 | absent | other gameplay/config data |  |
| face_diffuse_maps | 0x39CC7210/0x39CD61D0 | absent | other gameplay/config data |  |
| face_eyelash_maps | 0x39CC74E0/0x39CD6760 | absent | other gameplay/config data |  |
| face_normal_maps | 0x39CC7360/0x39CD6440 | absent | other gameplay/config data |  |
| fx_cam_fovs | 0x39B056B0 | absent | model/actor/fx/sound resource refs |  |
| fx_cgfs | 0x39B04B60 | absent | model/actor/fx/sound resource refs |  |
| fx_chrs | 0x39B04920 | absent | model/actor/fx/sound resource refs |  |
| fx_groups | 0x39B061A0 | absent | model/actor/fx/sound resource refs |  |
| fx_motion_blurs | 0x39B058E0 | absent | model/actor/fx/sound resource refs |  |
| fx_particles | 0x39B04630 | absent | model/actor/fx/sound resource refs |  |
| fx_ropes | 0x39AEEF70/0x39B05180 | absent | model/actor/fx/sound resource refs |  |
| fx_shake_cameras | 0x39B04F00 | absent | model/actor/fx/sound resource refs |  |
| fx_sounds | 0x39B04CC0 | absent | model/actor/fx/sound resource refs |  |
| fx_voices | 0x39AEF0F0/0x39B05500 | absent | model/actor/fx/sound resource refs |  |
| game_safe_area_rules | 0x39C750B0/0x39C91BA0 | absent | game schedules/rules |  |
| global_region_names | 0x39D14A00/0x39D1B650 | absent | other gameplay/config data |  |
| global_world_names | 0x39D14B20/0x39D1B850 | absent | other gameplay/config data |  |
| ignore_texts | 0x39BFD230/0x39C05480 | absent | other gameplay/config data |  |
| instrument_sounds | 0x39C00BD0 | absent | other gameplay/config data |  |
| localized_texts | 0x39BB7BA0 | absent | other gameplay/config data |  |
| map_icons | 0x39C5CBD0/0x39C6A680 | absent | other gameplay/config data |  |
| map_resources | 0x39C5CA40/0x39C6A330 | absent | other gameplay/config data |  |
| model_attach_point_strings | 0x39BDBD30/0x39BEDA60 | absent | model/actor/fx/sound resource refs |  |
| model_bindings | 0x39AED260 | ZoneModelAuxiliaryContent.cs | model/actor/fx/sound resource refs |  |
| model_mutations | 0x39AD3540 | absent | model/actor/fx/sound resource refs |  |
| models | 0x39ABDA40/0x39ADC040 | ZoneModelContent.cs, ZonePlayerActorFactory.cs | model/actor/fx/sound resource refs | Y |
| music_note_limits | 0x39B4A510 | absent | other gameplay/config data |  |
| open_portal_inland_reagents | 0x39C0DF20 | absent | other gameplay/config data |  |
| open_portal_outland_reagents | 0x39C0E140 | absent | other gameplay/config data |  |
| prefab_elements | 0x39ABD920/0x39ADBBB0 | absent | model/actor/fx/sound resource refs |  |
| prefab_models | 0x39ADBDD0 | ZoneModelContent.cs | model/actor/fx/sound resource refs |  |
| replace_chat_texts | 0x39CCA3D0/0x39CD32E0 | absent | other gameplay/config data |  |
| resurrection_waiting_times | 0x39BDC070 | absent | other gameplay/config data |  |
| return_points | 0x39AF3090/0x39B07660 | absent | other gameplay/config data | Y |
| sound_packs | 0x39C05B10 | absent | model/actor/fx/sound resource refs |  |
| sounds | 0x39BFD350/0x39C05680 | absent | model/actor/fx/sound resource refs |  |
| sphere_sounds | 0x39BEE870 | absent | other gameplay/config data |  |
| sub_zones | 0x39CF4C10/0x39CF5990 | absent | other gameplay/config data | Y |
| tip_of_day_groups | 0x39C74AF0/0x39C80140 | absent | UI/text only (client-oriented) |  |
| tip_of_days | 0x39C777B0/0x39C7FF30 | absent | UI/text only (client-oriented) |  |
| ui_avi_subs | 0x39C778E0/0x39C80960 | absent | UI/text only (client-oriented) |  |
| ui_avis | 0x39C77A40/0x39C80BC0 | absent | UI/text only (client-oriented) |  |
| ui_content_info_feature_sets | 0x39D147C0/0x39D1ADA0 | absent | UI/text only (client-oriented) |  |
| ui_content_infos | 0x39D1AFB0 | absent | UI/text only (client-oriented) |  |
| ui_esc_menu_categories | 0x39D15ED0/0x39D1A920 | absent | UI/text only (client-oriented) |  |
| ui_esc_menus | 0x39D14670/0x39D1AB50 | absent | UI/text only (client-oriented) |  |
| ui_hud_right_icon_menus | 0x39D148E0/0x39D1B260 | absent | UI/text only (client-oriented) |  |
| ui_options | 0x39D15D40/0x39D1A590 | absent | UI/text only (client-oriented) |  |
| ui_texts | 0x39BFEEE0/0x39C03690 | absent | UI/text only (client-oriented) |  |
| world_contents | 0x39C5C800/0x39C69C40 | ZoneWorldContent.cs | world/map division config | Y |
| world_divisions | 0x39C5C920/0x39C6A0C0 | absent | world/map division config | Y |
| world_groups | 0x39C79300/0x39C8EF20 | absent | world/map division config | Y |
| world_level_configs | 0x39B8F960 | absent | world/map division config |  |
| world_level_hard_caps | 0x39B8F960 | absent | world/map division config |  |
| world_specific_content_configs | 0x39B4CFC0 | absent | world/map division config |  |
| world_var_defaults | 0x39CC7880/0x39CDC7F0 | absent | world/map division config |  |
| zone_groups | 0x39C78190/0x39C88E20 | ZoneContentDatabase.cs | zone/zone-group metadata | Y |
| zone_score_contents | 0x39BFEC80/0x39C02880 | absent | zone/zone-group metadata |  |
| zone_score_kind_rank_details | 0x39CD9A20 | absent | zone/zone-group metadata |  |
| zone_score_kinds | 0x39BFEB20/0x39C02380 | absent | zone/zone-group metadata |  |
| zone_score_levels | 0x39C02160 | absent | zone/zone-group metadata |  |
| zone_waitings | 0x39CCC880/0x39CE0F80 | absent | zone/zone-group metadata |  |
| zones | 0x39C78030/0x39C88B10 | ZoneContentDatabase.cs, ZoneSceneContent.cs | zone/zone-group metadata | Y |
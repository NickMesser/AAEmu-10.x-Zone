# Managed Unit casting fields

`ZoneUnit.Casting` owns the independent cast and channel fields for one admitted Unit allocation. `CopySnapshot()` returns a copied `ZoneUnitCastingSnapshot`; sending StopCasting does not mutate it. The World acknowledgment consumers write the fields, and death/resurrection clear them in source order. The old `ZoneSkillState.Casts` dictionary remains a packet observation and is not a source for a StopCasting tuple.

## Storage and admission

| Original server Unit offset | Managed field | Admission source |
| --- | --- | --- |
| +32280 | `CastTimeline` | Snapshot +9080 |
| +32284 | `CurrentSkill` | Snapshot +9084 |
| +32288 | `ChannelTimeline` | Snapshot +9088 |
| +32292 | `ChannelSkill` | Snapshot +9092 |
| +32297, byte | `Casting` | Packed flags bit1 |

The first four values are uint32 in the Unit, serialized individually as uint16 when snapshot flags bit7 is set. `UnitStateSnapshot.CastingFields` gives their named, copied view. Its older `HighAbilities` list retains the same four values for compatibility; those values are not ability IDs. Absence of the extension means the native invalid values, all zero. A nonzero casting flag neither supplies nor clears the four identifiers.

`CurrentSkill` and `ChannelSkill` retain the names used by the StopCasting request interface, but their verified plot producers store the plot timeline/control ID. They must not be replaced with the skill descriptor's type ID. Unit lookup uses the World Unit object ID, not `NpcObjectId`, an entity ID, or a template ID. Caster variants 0, 2 (item's owning Unit), and 3 (mount) resolve a Unit for these handlers; variants 1 and 4 do not.

The constructor chain is `39360590 -> 393603D0 -> 39261E30 -> 39D1C1B0 -> 39D1BEF0`. The last routine initializes the four invalid values and casting bytes. Admission subsequently calls `3935D260 -> 39D1C900(snapshot, unit+8)`, which copies the real snapshot fields over those defaults. The managed constructor follows that admission result instead of resetting an admitted cast to zero.

## Acknowledgment writes

| Handler | Verified state change |
| --- | --- |
| Started, success, nonzero timeline (`39456D70`) | Assign `CastTimeline` only when descriptor `timing_id == 1` and packet cast time is nonzero. Retain all other identifiers and the casting byte. |
| Started, success, timeline zero | Skill type 2 sets the independent casting byte. Other skill types leave it unchanged. This path does not issue the normal Started model callback. |
| Fired, success (`39457170`) | Clear cast timeline/current skill, invoke the model callback, then assign channel timeline if signed descriptor `channeling_time > 0`. Retain channel skill and casting byte. |
| Started/Fired, nonzero result code | Deliver the failed model acknowledgment without their successful Unit field writes. A missing Fire timeline first synthesizes a successful zero-cast-time Start, as the original does. |
| Ended (`39456B20`) | Independently clear each matching cast/channel timeline. Its helper argument zero means the current value for each field. Retain both skill fields and the casting byte. |
| SkillStopped (`39456600`) | Clear casting byte only for skill type 2. Its second wire integer is a skill type, not a timeline. Compare all 32 bits. |
| CastingStopped (`39456630`) | Compare the real cast timeline; on a match, clear cast timeline/current skill before resolving and starting World's supplied cooldown duration. Retain channel fields and casting byte. |

`CastingStopped.TimelineId` preserves the uint16 wire interpretation even though its legacy positional member is a `short`. Native `39367030` zero-initializes its uint32 packet member before reading uint16 into it, so values 32768..65535 are not sign-extended.

SkillStarted/SkillFired now retain `SkillCastResult(Code, Detail, Parameter, Flag)` instead of discarding the result tail. `3937E5A0` defines the byte/uint16/uint32/bool layout and defaults `(0,0,0,true)`; code zero selects the successful path. The other copied members are not assigned invented gameplay meanings.

`ZoneUnitRegistry.SkillAcknowledged` is a synchronous `Action<ZoneUnit, ZoneUnitSkillAcknowledgement>`. Kinds are Started, Fired, and Ended. The exact current Unit allocation is supplied so an NPC executor can reject a stale owner. Fire callbacks see cleared cast/current fields and the prior channel timeline; End callbacks see matching timeline clears. End's extra `SkillId` observation comes from the remembered active timeline type, or the original invalid zero when no entry exists; native proxy +40 itself receives the timeline. Failure code and callback order are retained, including when Start and End arrive in one dispatch batch.

## Plot phase and lifetime

`39455730` first resolves the event, its parent plot, and the skill descriptor. The minimal immutable `ZoneSkillContent.PlotEvents` and `PlotIds` catalogs preserve those admission gates. An invalid descriptor does not manufacture Unit fields.

For an admitted Unit caster, a nonzero plot cast time stores that plot timeline in `CurrentSkill`, and a nonzero plot channel time stores it in `ChannelSkill`. Those field writes still occur when the packet's effect-condition flag is false; that flag gates plot effects, not these associations. `39455490` and `394555E0` clear the cast pair or channel pair only when the associated Unit field still equals the plot timeline. `39455D20` (PlotEnded) stops its owned cast phase and retains an owned channel phase.

Both stop-Unit IDs in PlotEvent are retained. The original channel-stop guard checks its separate ID but resolves the retained first stop-Unit ID (`a9`); the managed consumer preserves this behavior. Two executed original controls cover this distinction.

Managed plot ownership captures the exact Unit allocation. Retirement drops its associations, so a later callback cannot clear a replacement Unit that reused the same World ID. A Fire callback that retires its owner also prevents the trailing channel write from reaching either the retired allocation or its successor. This is the managed generation ownership policy, not a claim that the native ID allocator has the same protection.

Death `3935D490` first marks dead, clears casting and its adjacent byte (flags bit10), then dispatches the combat transition. The four identifiers clear afterward. Resurrection `3925E8E0` clears the four identifiers and those bytes before refilling health/mana. Tests observe the death callback phase directly.

This slice does not execute plot effects, animation/controllers, general skill validation, modifier-based cooldown/GCD calculations, or the native global plot manager's delayed cleanup/timer traversal. Plot associations are released by their acknowledged phase completion or Unit retirement. General timed plot cleanup remains a separate dependency; this state owner does not invent a timer expiration or a channel timeline from `Casting`.

## Evidence and checks

Original binary: `Bin64/x2game-dev_dedicate.dll` from the configured 10.0.2.13 client. SHA-256: `50edcd49d0537aa0f94db55ecf22008d7fb9ed937ea02121919199b5bf8bb7ed`.

The offline harness `D:/aaemu-runtime/research/casting/native-casting-oracle.py` executed 42 original PE controls without loading a DLL. Explicit callbacks supply descriptor/Unit/model lookup, pre-existing single-node active/plot maps, game time, cooldown side effects, allocation release/map erase, and model callback capture. Original instructions perform the field comparisons and writes. Eight controls isolate the two timeline-clear helpers; the other 34 exercise complete handlers/association routines. `managed-casting-comparison.json` compares those 34 controls against the managed state and reports zero field or callback-phase disagreements. This is a controlled state-transition comparison, not a full engine execution.

Source extracts are retained beside the harness, including `393932E0.c` (snapshot extension), `39D1C900.c` (admission copy), `39AE6D50.c` (`timing_id`/channeling descriptor layout), and the constructor/death/resurrection/acknowledgment functions named above. Focused repository tests cover actual wire admission, failure-tail decode, unknown descriptors/caster kinds, independent timelines, World cancellation and unsigned IDs, plot gates, death order, and retirement/reentry. Iteration artifacts are isolated under `D:/aaemu-runtime/npc-casting-artifacts`.

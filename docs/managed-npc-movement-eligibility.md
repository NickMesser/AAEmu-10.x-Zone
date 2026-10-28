# NPC movement eligibility and hold-position predicates

The managed actor now evaluates the original `IF_CAN_MOVE47` against its actual World Unit
and model owners. `World.AttachManagedMovement` attaches the concrete eligibility service
before actor admission. An actor cannot substitute another generation that happens to reuse
its numeric World or AI entity ID.

The source chain is CryAI `3115AAA4` case47, CGame v912 (`392A9820`), proxy v328 (World Unit
ID), then ServerAIUnitSystem v184 (`39212E70`). The predicate requires clear stun, root and
sleep flags, model flag1, and a clear CActor psychokinesis latch. Entity visibility/activity,
AI enabled state, health, grounded state and requested or actual velocity are independent.
The goal executor applies the `0x100` NOT bit once; actor services return the raw predicate.

`ZoneUnitMovementStatus` retains separate BuffMan byte counters and copied Unit flags.
The descriptor loader maps actual `stun`, `sleep` and `root` columns to descriptor bits
1134&2,1133&8,1132&0x10. `39D2C030` changes one counter per actual buff instance, independently
of stack/charge. Refresh visitor2 does not add another contribution. `3925A270` publishes
counter!=0 into Unit32080..32082. Explicit death/resurrection clears the copied flags while
retaining the counters; a later successful buff mutation publishes them again. Elapsed
duration does not remove an instance before World's removal acknowledgement. PassiveManager
is a separate owner and does not contribute these three BuffMan counters.

Psychokinesis is an ordered model effect, rather than a count of current buffs. Descriptor
1127&2 comes from `psychokinesis`; float328 is `psychokinesis_speed`. Apply `3944FEB0` calls
model v168, sets CActor548/552 through v176 (`39266BE0`), and clears NPC model flag1. Remove
`39450420` calls the reset, clears the latch, and sets flag1 even if another such buff remains.
The getter v184 (`39266C00`) reads CActor548; actor539 frame-hold is unrelated.

The admitted v168 reset profile has empty actual actor movement requests and cached NPC
controller input. It never guesses that zeroing physical velocity implements the reset.
Unowned nonempty reset state queues a named failure; the runtime retires that exact actor
before subsequent physical preparation or script work. The World buff mutation remains
committed. Constructor state, initial restored effects, final NPC initialization flag1 and
ordered post-enter effects retain their distinct chronology, including deferred admission.

The adjacent distance predicates45/46 use the actual entity world origin. Native
`3103CA08` prefers Entity.v312 and only falls back to the AI object's position when no
entity exists. Every actor admitted here has an entity, so the managed query uses its actual
Unit/model origin. The AI proxy view position may contain stance eye height and is unsuitable.
The scalar operation order is `(dy*dy + dx*dx) + dz*dz`, then float square root;45 compares
`distance <= threshold`,46 compares `distance > threshold`. Both return false for unordered
NaN comparisons. NOT remains the executor's responsibility.

Independent original-instruction controls are retained in the test fixtures:

- `npc-can-move-native-controls.json`:67 full `39212E70` controls with declared Unit/model/
  flag/latch lookup callbacks, including64 complete status/model combinations. Native
  missing-Unit log+true and absent-model false remain recorded; managed retired generations
  refuse rather than acquiring a replacement owner.
- `npc-position-distance-native-controls.json`:52 CryAI predicate/entity-position controls,
  including equality, vertical separation, entity versus view origin, NOT and NaN cases.

Local reproduction is under `D:/aaemu-runtime/research`: `npc-can-move-controls.py` executes
the complete server leaf under Unicorn without loading an engine DLL. Source extracts,
descriptor mapping and status lifecycle controls are under `if47-status/`. Fixtures are
independent observations of the stated native boundaries, rather than full gameplay proof.

Producer contracts are detailed in [Unit status](managed-unit-movement-status.md) and
[model effects](managed-npc-model-effects.md). The [position/proxy audit](managed-npc-position-branches.md)
describes the source ordering of the same scheduled actor's goal phase and signal drain.
The original five-resource follow-up control completes all five corrected owners without
fatal faults. NPC8173 runs150 additional33ms frames through the original hold/idle-hold loop,
including the actual StartSkill40917 request and its unacknowledged timeout path. No World
ACK, skill damage or locomotion is injected. The18 protected script-error invocations across
five NPCs are retained as5 bounded diagnostics. All17 ownership categories retire to zero.

NPC gameplay readiness remains false. These predicates do not provide perception,
navigation, locomotion, all14 Unit statuses, all buff effects or full combat admission.

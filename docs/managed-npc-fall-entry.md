# Source CActor fall-entry admission

`ZoneManagedNpcActorMotion` now tests the original fast-fall entry path before
refusing its unported effects. A large current physical downward velocity alone
does not enter that path.

Original `392E08F0` tests these fields **before** `392D7A10` derives the new frame's
fall speed:

1. Actor bytes `312` and `533` are zero and integer `2312` is below 1.
2. Stored previous fall speed `376` is strictly greater than
   `pl_fall_start_velocity`.
3. Actual entity Z is strictly greater than the renderer terrain height plus
   `pl_fall_start_height`. A nonzero jump field `420` raises that height threshold
   to at least 3; the admitted no-jump constructor profile retains 420 zero.

The admitted ordinary profile owns the zero constructor values in the first
condition. Special-state/jump producers remain separate work. When all owned
entry conditions hold, the implementation still refuses before committing motion
state; it does not invent fall animations, effects or gameplay damage. The native
entry writes state `533=1`, dirty byte `534=1` and calls `392D7830`/`392D78B0`.

The terrain query is specifically I3DEngine virtual `+1064` with its final argument
zero. It uses the actual `ZoneTerrainHeightField.GetHeight` owner, including that
source query's semantics. A cached living floor, highest static roof, water level
or guessed constant is not interchangeable with this terrain input. Missing
terrain ownership refuses when the prior-speed gate needs that query.

Original registration `392B1240` supplies numeric defaults of height 2 and
velocity 10. Disassembly verifies the height load at `392B166C` is `0x40000000`,
passed to registration at `392B3540`; velocity load `392B356F` is `0x41200000`,
passed at `392B358D`. The motion constructor accepts actual terrain and
`ZoneEngineCVars` after its existing model argument. An omitted CVar owner selects
the explicit registration-default profile; production passes its loaded config
and World command-line overrides. Parsed nonfinite thresholds are unadmitted.

The first-landing path remains distinct. `392DABA0` clears constructor byte 2272
and skips its effect branch on that first landing, even after a high fall speed.
Later hard landings at fall speed 5 or above still need the material/effect owner
and refuse in the bounded ordinary profile. The `392E0A70` model landing callback
is separately owned as documented in `managed-npc-actor-motion.md`.

## Concrete 7987 correction

The actual NPC template 7987 and recorded model 970 resolve to ActorModel 433,
`Objects/Env/01_nuia/001_ncom/ncom_revival_c.chr`. Its database values really are
gravity 1000, air resistance 20 and air control 0.9. Physical admission correctly
maps gravity to signed Z acceleration `-1000`; no parameter conversion was fixed.

At its captured engine-local pose `(692.5293,989.2988,230.40353)`, renderer terrain
is 230.16608 and collision support is 230.16609. Its first 33ms physical frame leaves
position unchanged and produces VZ `-11.219999`, while previous actor fall speed
is 0. On the next frame the real body settles at Z 230.16609 with velocity 0. Thus
the original fast-entry branch is never taken. The previous managed guard tested
the current velocity as well as the previous speed and retired this actor on
frame 1, before its valid first landing.

The independent full-scene control now runs the unchanged original 73-script
closure, real model/character/terrain/static/water owners and actual shared
physical frame. Original `GO_TO_IDLE` reaches `hold_position`; one diagnostic
threshold displacement returns through the real CheckPos owner, followed by
replication and a subsequent scheduled AI frame. It reports no script faults,
one position correction, no engine DLLs and zero live owners after retirement.
The displacement is an explicit diagnostic stimulus, not an AI locomotion claim.

## Verification scope

`ManagedNpcActorFallEntryTests` adds four controls to the nine existing motion
tests. They exercise high current speed with low previous speed, actual terrain
and strict/default/overridden thresholds, refusal atomicity, dependency gating and
the repeated hard-landing boundary. All 13 focused tests pass.

External evidence under `D:/aaemu-runtime/research/npc-fall-audit`:

- `body-ground-control.json`: the real resolved request and eight managed body
  steps against the complete authored scene. It isolates physical/ground math;
  it does not execute the original physical engine.
- `native-fast-fall-controls.json` / `native-fast-fall.py`: 11 controls executing
  original `392E08F0`, `392D7A10`, `392E0B50`, `392E0A70` and the first-landing
  `392DABA0` path under Unicorn. Entity position and renderer height are explicit
  ABI callbacks; relevant constructor fields are initialized from source. The
  complete constructor, physical motor, model landing callback and material/damage
  services are outside this instruction control. Engine DLLs are never loaded.
- `fullscene-control.json`: the original 7987 Lua/construction/frame integration
  described above, using an isolated managed build. It preserves failures and
  checks retirement/module ownership; no TCP World or full gameplay parity is
  claimed.

The next effect implementation needs actual fall-state consumers and landing
material/effect ownership. This correction only removes an earlier-than-source
refusal; it does not mark those consumers complete.

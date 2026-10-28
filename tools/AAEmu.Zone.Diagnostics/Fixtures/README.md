# Zone 129 physics controls

`zone129-character-shapes.json` contains 132 copied-value box, capsule and cylinder requests in
the six-cell zone-129 window, whose world-cell origin is `(9,14)`. Centers are raw primitive
centers, not actor origins. A zero sweep requests overlap. No game geometry or engine binaries
are included.

`zone129-character-shapes.native.json` records the original 10.0.2.13 dedicated physics results
from the synchronous static player-collision profile (required/any geometry flags `2/2`). The
diagnostic checks the original binary identity and query target before calling it. Raw return,
contact distance and normal are retained; the common sweep normal reverses the original
query/body orientation. Entity and part IDs describe that native run and are not stable object
identities for comparisons across different scene loads.

Run either configured scene check with:

```text
--scene-shape-probes tools/AAEmu.Zone.Diagnostics/Fixtures/zone129-character-shapes.json
--scene-shape-probe-output results.json
```

Use `--check-managed-scene` with the managed configuration or `--check-scene` with the native
comparison configuration. Both require the original configured assets/content; the latter also
requires the original engine modules. Standalone unit tests require neither.

This corpus retains known differences; it is not a passing full-parity gate:

- 96 separated, clear/intersecting-overlap and outward-motion cases match hit/miss. The 24
  separated sweep hits match materials, with maximum travel difference `0.00055924` m.
- Three of 12 nominal touch overlaps differ within the managed contact band.
- The 24 inward initial-contact cases expose seven hit/miss differences and travel differences
  up to `0.5086479` m. Original queries can skip an initially intersecting face and find a later
  surface; managed blocking currently stops at initial intersection.

Overlap contact order and box manifold points are not unique. Compare overlap hit decisions and
separated sweep distance/material/normal with the declared tolerances; do not require all native
contact fields to be identical. Keep the initial-state and boundary residuals visible.

See [CE-07](../../../docs/tasks/cryengine-removal.md) and the
[shape conventions and source evidence](../../../docs/managed-character-physics.md).

## Full original living-body controls

`zone129-living-recovery.json` and its `.native.json` result contain a complete 32-case original
living-body run. `zone129-living-recovery-followup.json` and its `.native.json` result contain ten
followups, including four repeats: 38 distinct cases overall. These exercise free motion, idle
and moving overlaps against walls/roofs/stairs/bridges, buried player pivots, colliders extending
below the pivot and overlapping living bodies. Inputs are entity origins with authored dimensions;
they are not raw primitive centers. A separate `setupPosition` allows dimensions to be accepted
in clear space before verified type-0 position parameters establish the initial control pose.
The corresponding `.evidence.json` file records authored target/control attribution.

Run the native scene check with:

```text
--scene-living-probes tools/AAEmu.Zone.Diagnostics/Fixtures/zone129-living-recovery.json
--scene-living-probe-output states.json
```

The oracle validates full living bodies in simulation class 3 and their membership in the living
list. World step flag 8 actually steps that list; flag 0 merely advancing the clock is insufficient
evidence. The velocity control uses the complete 24-byte type-1/mode-3 action, including its time
field. The separate verified pose parameter is type 0 and 72 bytes. No position-labelled velocity
action or manually forced recovery is used.

Frames distinguish post-creation state, the verified initial pose and each simulated step. Native
dimension admission can itself move the body, so a creation shift must not be called step recovery.
Read raw simulation pose separately from status pose/interpolation. Native entity identities belong
to the recorded run and are not stable object references.

The controls establish static early unprojection with approximately 0.01 m added clearance and
preserved velocity, bounded buried-pivot support and the original special-stance hover behavior.
They also retain the native idle shortcut: zero actual/requested velocity is only part of a larger
predicate over flying/gravity, suspension, cached floor/normal, retained ground and prior contacts.
A tiny nonzero request defeats that shortcut. The managed static/terrain profile copies the idle
predicate for its owned state; retained supports and the complete original contact state remain
separate limits. These original results support individual rules; they are not a passing claim of
complete managed living-body parity.

## Authored living movement and stance controls

The following four input/result groups contain 60 additional cases and 1,128 original world
steps. Each group also has an `.evidence.json` file recording the independent placement and
source attribution. Counts are world steps, not the sum of all bodies' individual steps.

| Input stem | Cases | World steps | Coverage |
| --- | ---: | ---: | --- |
| `zone129-living-movement` | 42 | 988 | Constructor/explicit thresholds, slope slide/fall/climb, velocity modes, initial support, queued/immediate dimension changes |
| `zone129-living-movement-followup` | 10 | 72 | Air control/inertia/resistance and preflight versus drain for pivot/kind changes |
| `zone129-living-stance-pair` | 4 | 12 | Living-pair radial/axial dimension refusal and capsule-cap admission |
| `zone129-living-stance-queue-survival` | 4 | 56 | Queued preflight returning zero while the later physical application succeeds |

Each stem has `.json` inputs and the unchanged `.native.json` copied results. The native captures
retain post-creation frame `-1`, verified starting frame `0`, every actual step, dimension-call
return values and immediate applied readbacks. All bodies entered the original class-3 living
list. A repeated preliminary smoke run is excluded from the 60-case total.

The same native command shown above accepts these inputs. A managed comparison also requires
the explicitly matched native capture:

```text
--check-managed-scene --config <managed-runtime.json>
--scene-living-probes tools/AAEmu.Zone.Diagnostics/Fixtures/zone129-living-movement.json
--scene-living-native-reference tools/AAEmu.Zone.Diagnostics/Fixtures/zone129-living-movement.native.json
--scene-living-probe-output <managed-states.json>
```

The runner verifies input values, case/body/frame membership and resolved dimensions/dynamics
before registering a body. It seeds only the copied native frame-0 origin, leaving native
creation shifts and unseeded cache metadata visible in `seeds`. Applied geometry, actual velocity,
requested velocity and queued stance reports are separate fields. The declared period rounds to
the nearest 100 ns tick, and the native copied elapsed period must match the single-step profile.
Output cannot overwrite its input or native reference.

This is a comparison of stepping from the same physical starting pose. It does not establish
native creation admission, animated-character behavior, retained moving supports or full gameplay
parity. The native fixtures remain the independent observations; managed results do not replace
them.

The source anchors for this slice are `35025530` (constructor), `35028BF0` (dimensions/dynamics),
`3502A300` (velocity requests), `3502B3F0` (support velocity), `3502CA20` (support probe),
`3502DED0` (living step), `35023200` (queue before preflight) and `35027E10` (dimension
unprojection). Angle fields retain their explicit values; zero is not an unused sentinel.
`useNativeMovementDefaults` leaves the selected angle/support-speed fields unused, while the
remaining explicit diagnostic dynamics still apply. `MaxVelGround` caps support-platform
velocity, not walking speed.

Velocity mode `0` submits ordinary wishes. Mode `3` sets the original one-step velocity bypass
and its separate persistent flag-2 state; it must not be substituted for ordinary input. A
dimension update with `threadSafe: -1` queues before preflight. Both directions are retained:
preflight can succeed before drain refuses, or return zero before drain applies. The diagnostic
`threadSafe: 0` branch applies immediately; production actor requests use the queue.

The original early contact solver's `2.5 * radius` parameter is a per-geometry solver bound.
Its fallback/contact ordering does not imply a universal hard rejection rule. Network correction
tuples also use a separate physical API (`3502AA60`, virtual `+0xC0`), and are not the reset
action numbered 4 or an ordinary mode-3 request. These 60 controls do not execute that network
correction API.

## Network-state correction controls

`zone129-living-corrections.json`, `.native.json` and `.evidence.json` add six free-space
controls and 19 original world steps. They use the same living-probe flags and matched-reference
managed command, with this fixture stem instead. Together with the movement/stance groups,
they contain 66 cases and 1,147 original world steps.

The original X2 producer `392440E0` sends the 84-byte type-4 network tuple to physical virtual
`+0xC0`; the original living vtable resolves it to `3502AA60`. The diagnostic validates that
target and supplies copied tuple fields. This call is separate from the type-4 reset action at
virtual `+0x50`.

The five accepted controls cover forced position, near/far position caps, absent/present original
velocity and a retained prior mode-3 flag. Position remains unchanged immediately and its queued
update appears at the next step. Actual velocity changes immediately. Defined original velocity
updates the wish, and filtered Z then updates both actual and requested Z. The sixth control
returns zero without tail writes when `sameGround` has no retained owner.

The prior-mode-3 control preserves flag `0x2`; the copied one-step byte `1120` is false before and
after that call. Nonzero-byte preservation remains source evidence, not an executed control.
Moving-platform anchors, ground-part corrections and collision traversal of correction poses
are outside these free-space controls.

## Managed controller comparison

The [copied comparison summary](zone129-living-controller.managed-summary.json) records all 66
cases, source fixture hashes and per-case measurements. The managed run completed all 1,147
world steps and captured every expected body frame: 1,229 samples including frame zero.

- Flying state, flag `0x2`, requested velocity and applied dimensions match in all 1,229 samples.
  Dimensions include raw primitive and clamped support half-height, pivot/collider heights,
  primitive kind and the primary part offset.
- All 42 stance returns and six network-correction returns match. The six correction controls
  also match immediate state, queued pose timing and every subsequent copied position/velocity.
- Maximum actual-velocity difference is `0.0000006` m/s. Suspension differences are at most
  `0.0000001266` m offset, `0.00000033` m/s velocity, zero acceleration and `0.00000001` s time.
- The largest position difference is `0.0011` m in four queued/immediate cylinder-growth
  controls. The two steep-slope fall/contact controls differ by at most `0.000543508` m.
  Other captured position differences are at most `0.00003` m.

All 25 samples that copy native byte `1120` match; those values are false. The original 60-case
captures do not contain that byte, and none of these captures copies the live impulse-recovery
timer. The managed report exposes its current timer without claiming an executed numerical
comparison. Source-only branches and moving-support/contact-order limits remain explicit.
These results establish this bounded scene/pose/controller comparison, not full feature parity.

## Terrain-only primitive controls

`zone129-terrain-shapes.json` and its `.native.json` result contain 54 original queries at two
terrain locations. Each explicitly selects `"target": "terrain"`, native object mask `0x100`
with required/any geometry flags `2/2`. Existing inputs omit `target` and retain static selection.
Use the same `--scene-shape-probes` command shown above with the terrain fixture path.
Its `.evidence.json` file retains the independent expected controls and slope-boundary caveats.

Twelve positive surface/near-surface overlaps establish that the terrain profile is eligible.
All six deeply submerged overlaps miss and all six upward surface-crossing sweeps hit. Original
heightfield shape geometry is a top-surface triangle mesh with two-sided sweep contacts; it is
not an infinite solid beneath the heightmap. Terrain rays use a separate callback/material path. Numerical and
material comparison limits are recorded in CE-07; these controls must remain visible when further
controller and terrain work changes the implementation.

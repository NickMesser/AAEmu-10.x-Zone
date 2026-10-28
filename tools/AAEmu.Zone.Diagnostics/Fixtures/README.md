# Zone 129 character-shape controls

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
A tiny nonzero request defeats that shortcut. Managed proactive recovery does not reproduce this
complete state predicate. These original results support individual rules; they are not a passing
claim of complete managed living-body parity.

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

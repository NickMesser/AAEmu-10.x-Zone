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

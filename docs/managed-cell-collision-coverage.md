# Managed cell collision publications

`ZoneManagedCellCollisionCoverage` composes the dedicated static scene's actual brush,
vegetation, XML entity, road, decal and water-row publications. `ZoneManagedSceneActivation`
owns it, and the production navigation adapter uses this owner before returning clearance.
The owner borrows the configured asset source and uses its actual engine CVars. It does not
accept a caller-supplied completeness flag.

## Query scope

Part flags and physical entity selection are independent:

| Input | Value | Meaning |
| --- | --- | --- |
| Character part category | `0x2` | Admitted character proxy parts |
| Ray part category | `0x8000` | Admitted picking/ray proxy parts |
| Navigation entity selection | `0x107` | The source navigation world's selected physical classes |
| Water-area class | `1 << 5`, or `0x20` | Excluded by `0x107`; included by queries selecting areas |

Water certificates bound the possible class of an exact authored row. They do not assert that
every row creates a body, that water is absent, or that a character is dry. Actual water-level
queries retain their separate `ZoneManagedWaterQueries` owner, contour rules and refusals.
Navigation validates that owner's exact ordered source cell references and the initialized
terrain's resolved ocean-level bits. Source order matters to ID deduplication and area selection.

Native physical queries are global across loaded cells. `351AA730` traverses the physical
world's entity/thunk grid; `35084680` filters registered simulation classes and actual physical
bounds. Neither filters by the authored cell that created a body. A complete local snapshot
therefore cannot prove clearance while another loaded cell contains an unclassified possible
contributor. `Check` first verifies the touched cells and then every retained loaded cell.
Authored cell distance, current position and render AABBs never exclude an unknown producer.
An eventual relaxation requires a concrete selected-class exclusion or an owned current
physical influence bound.

## Concrete evidence

Each copied cell snapshot retains its exact input reference, generation, publication revisions,
requested-category completeness, per-family rows, live instance counts and diagnostics.
Classification consumes every retained object/big-object/sector/entity input. A missing read
or malformed document is different from an explicitly absent authored file.

- Brush snapshots come from `ZoneManagedSceneCollision`'s actual publication records. Every
  exact brush row must be accounted for once, with its owned character/ray handles still live.
  A foreign instance restoring a total count cannot replace a missing owned handle.
- Vegetation leases retain the exact completed tagged and sector inputs and source row order.
  Unsupported placement profiles prevent clearance. Configured native skips and successfully
  classified excluded parts remain distinct from published query proxies.
- [Entity certificates](managed-cell-entity-collision.md) retain the original document bytes,
  every XML child, class/script dependencies and particle graphs. Unknown children or changed
  physical producers remain refusals.
- [Road/decal certificates](managed-cell-nonbrush-collision.md) retain the exact source row,
  material dependency and admitted normal factory/compile lifecycle.
- [Water class certificates](managed-water-query-classes.md) retain each exact water row and
  derive exclusion from simulation class5 and the navigation entity-selection mask.

Completeness is category-specific. Losing character geometry invalidates character clearance;
a newly copied observation may still prove the ray category if its own handles remain live.
The older constructor path without a composed owner admits only fully accounted brush/empty
inventories across the entire loaded scene. It cannot silently certify decals or other families.

## Retirement and cached routes

Brush-cell and composed-cell revisions advance on bodyless retirement too. Geometry revisions
alone cannot observe retirement of a cell whose admitted result contains no query proxy.
Retirement invalidates publication before removing only that cell's owned handles. Reentry uses
a fresh owner/generation; old leases cannot remove replacement handles.

Navigation route tokens retain terrain, character geometry, ray geometry, water, brush-cell and
composed-cell revisions. They are checked before another physical request, independently of AI
cadence. Equal-data reloads and add/remove cycles do not restore an old route. Losing any source
water cell also invalidates global water completeness: a contour can cross its authored cell,
so dropping it cannot establish a new dry ceiling in a neighboring cell.

Borrowed source and trace callbacks can retire scene activation while creation/loading is in
progress. Activation checks those boundaries before publishing terrain/collision services and
rolls back locally constructed collision/coverage owners on failure.

General scene/gameplay readiness remains false. Dynamic entity execution, physical mutations,
streaming origins, animated bodies, unowned foliage and populated crowd solving need their own
producers and lifetime controls. See the remaining [populated-cell tasks](tasks/managed-populated-cell-collision.md).

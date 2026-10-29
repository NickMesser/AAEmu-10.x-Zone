# Authored water physical query-class certificates

`ZoneCellWaterQueryClassContent` classifies exact parsed water rows under the normal authored lake/river factory lifetime. A successful certificate says that the factory can create no physical area or a simulation-class-five area. It does not count live bodies, prove query intersection, replace the water-level owner or label water universally nonphysical.

The physical world selects an entity with `entitySelectionMask & (1u << simulationClass)`. Class five therefore contributes bit `0x20`. Navigation selection `0x107` and path dynamic selection `0x50006` exclude this class; `0x20`, `0x127` and all-classes selections include it. Entity selection is separate from player/ray part-category flags such as `0x2` and `0x8000`.

## Normal native lifetime

The addresses below refer to the matching original 10.x images read offline. `cry3dengine_dedicate.dll` has SHA-256 `5e22f8338d6e146939f4f22fc0531d78c7c2313547a1863cadb2cb5e3b8d3caf`; `cryphysics_dedicate.dll` has SHA-256 `b558cd33b93177d3163ecf483368e3c7a0612507257ad73bc87a73ee872e2e20`.

| Operation | Source-backed consequence |
| --- | --- |
| Normal row factory `30223480` | Creates the water node through `300F6CF0`, installs the type-two lake or type-three river contours and physicalizes before installing the final material. Quality, configuration, skip, duplicate volume IDs and empty or invalid physical contours can produce no area. None selects another physical entity family. |
| Physicalizer `300F5E90` | First dephysicalizes the old area through node slot `+520` -> `300F59A0`. With a physics world and valid contour/index arrays, its sole physical creation call is world slot `+664` -> `35047C80` (`AddArea`). It has no static/rigid `CreatePhysicalEntity` or `AddGeometry` branch. |
| Area construction `35042150` | Installs table `351EF598`, writes simulation class five at byte `+71` and retains the actual physics world pointer at `+112`. |
| Post-creation setup | The normal physicalizer reads the area status and applies buoyancy type-12 and foreign-data type-11 parameters to that area. Those calls do not substitute a static or rigid body. |
| Material setter `300F55B0` | Retains the new material, releases the old one and stores node `+288`. It neither physicalizes nor changes node `+360`, the actual area pointer. It runs after physicalization in the normal factory. Null, named and native fallback material branches preserve the same class bound. |
| World query filter | `35084680` (`GetEntitiesInBox`) tests the simulation-class bit stored in world-grid thunk flags. Copied original instructions `3508538F` through `3508539C` confirm `1 << simulationClass`, including class five -> `0x20`. |
| Retirement `300F59A0` | Destroys/removes the existing class-five area through the world; the class-five removal path is `35041BE0`. Retirement adds no replacement body. |
| External mutation boundary | Water table `30289858` slot `+376` -> `300F5430` reads node `+360`; slot `+384` -> `300F5440` can install an arbitrary physical entity. External `SetPhysics`, water edits and other producers are outside this certificate and invalidate its normal-lifetime assumption. |

The certificate does not require a particular live quality or water CVar: the complete audited skipped and admitted normal branches have the same class upper bound. Material, shader and texture readiness are not claimed. The parser must still provide a valid material table reference; a missing named material definition cannot introduce another body through this physicalize-before-material setter sequence. Unsupported mount paths remain explicit refusals rather than inferred resources.

## API and input provenance

Call `Classify(content, row)` with the exact `ZoneCellWaterVolumeObject` in `content.Objects[row.SourceOrder]`. A row parsed from equivalent bytes in another content object is rejected. Each result retains that exact source content and row, file kind, source order/offset, octree node index, raw payload SHA-256, selected material table reference, normalized material name and material branch.

`Outcome` is `CertifiedClassUpperBound`, `Unsupported` or `InvalidData`. Only a certified result has non-null `SimulationClass`/`EntitySelectionClass`. `CanContributeTo(mask)` throws for an unclassified result, even if the supplied mask is zero. For a certified result, true means that an actual area/query owner may be needed; false means only that the mask excludes the certified class.

The first profile admits the verified type-two/type-three horizontal source family, complete finite render and physical vertex arrays, finite ordered authored bounds, finite plane/depth/flow and the normal constructor's render-count prerequisites. Empty physical contours retain a class bound; they are not counted as live areas. Unknown volume types, unowned source semantics and malformed values cannot become successful exclusions. Existing record parsing rejects truncated arrays, negative counts and invalid material references before classification.

The owner caches at most 65,536 exact-row certificates, an explicit replacement resource bound. It has no asset I/O or native dependency. Disposal rejects new classifications and clears its cache. An already returned immutable result remains a statement about its frozen source inputs; scene coverage must independently own the cell generation, physical lifetime and invalidation before unload/replacement.

## Original content and controls

The portable fixture `Fixtures/water-query-class-controls.json` contains all 127 original water payloads from twelve object/big-object files in the six loaded zone-129 cells: 19 type-two rows and 108 type-three rows. The existing concrete water query owner deduplicates those authored rows into 19 physical areas; this classifier does not claim 127 instances. The fixture preserves each unchanged raw payload hash, original file path/full-file hash, material tables and original row provenance. Its filtered transport contains only water records, so transport row indices and offsets are not presented as unchanged full-file indices or offsets.

Six offline controls execute the original area constructor, complete physicalizer, actual area status/parameter methods and named/null material setters. They cover valid contours, velocity arrays, empty contours, invalid index count, mismatched velocity count and an absent physics world. Every created body remains class five, and the actual material setter preserves the area pointer. Forty additional copied class-mask controls cover simulation classes zero through seven under five masks. No original engine module is loaded.

The harness declares allocator/CRT, world `AddArea`/remove, worker synchronization, area-grid publication and material reference callbacks. Its `AddArea` callback returns the original-constructed `CPhysArea`; it does not execute the full contour/PCA builder in this harness. The separate water geometry/query owner owns that path and its independent numerical controls. These results are class/lifetime evidence, not full water geometry or rendering parity.

The 17 permanent focused water tests cover all original payloads, copied native outputs, selecting and excluding masks, explicit unsupported outcomes, malformed/missing inputs, exact row identity, material branches and disposal. Combined with the unchanged road/decal tests, the isolated suite passes 46/46. Reproducible source extracts, the offline harness and complete reports are under `D:/aaemu-runtime/research/cross-cell-collision` on the development machine.

Native physical queries search the global loaded world grid, not the authored source cell. These water certificates can close water-class exclusion across loaded cells for an explicit selection profile; they do not close brush, vegetation, entity, particle or other unknown contributors elsewhere in that world. The global incomplete-coverage barrier must remain until each possible selected contributor has an owned physical representation, a proven exclusion or authoritative physical influence bounds.

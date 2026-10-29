# Managed particle collision content

`ZoneParticleCollisionContent` certifies a frozen, authored particle effect graph that contributes no particle physics or force-generation profile. It reads original assets through `IZoneAssetSource` and calls no engine, renderer, Lua runtime or native library. This certificate is one dependency of a future populated-cell classifier; it does not clear a cell or admit an entity by itself.

## Source-backed profile

The admitted profile is a cold, ordinary `ParticleLibrary` with `ParticleVersion="20"`, an exact registered top-level effect, and explicit descendant emitters. It requires absent `PhysicsType`/`ePhysicsType`, `ForceGeneration`/`eForceGeneration` and `Geometry`/`sGeometry` parameters throughout the complete selected graph. Supported nonphysical parameters are an explicit allowlist drawn from the original target graphs. An unknown parameter, inheritance directive, emitter structure or physical conversion returns `Unsupported`.

The current reader deliberately refuses all explicit physical/force values, including `None`, and any explicit geometry value. It records recognized enum values for diagnostics without claiming their full native parsing/consumer behavior. `SimpleCollision`, `SimplePhysics` and `RigidBody` are actual physics enum labels, rather than invented booleans. Disabled descendants receive the same checks as enabled descendants.

The original 10.x implementation provides these boundaries. Addresses refer to the matching offline `cry3dengine_dedicate` PE/decompile; the reader does not load that PE.

| Native implementation | Verified behavior used here |
| --- | --- |
| `30098EB0` (`FindEffect`) | Exact full-name registry lookup; on a cold miss, `strchr('.')` selects the first component for library autoload, then the full-name lookup is retried. |
| `30097670` (`LoadLibrary`) | Opens `Libs/Particles/<first component>.xml`; visits direct `Particles` children and registers their complete names. There is no longest-filename search. |
| `3009BA10`, `3009F440` | Full registered-name map uses `stricmp`. The managed supported/indexed name profiles are ASCII, with ordinal case-insensitive matching. |
| `CParticleEffect` table `30282B48`, slot `+208` -> `3007E3D0` | Reads versions 14 through 20. The managed implementation admits only the verified version-20 subset, with one `Params` and explicit `Childs/Particles` ownership. |
| `30087450` -> `30086FC0` -> `30086910` | Version-20 load resets the resource/parameter object before field loading. `params+976` physics and `params+1009` force generation become zero; constructor `bEnabled` is true. Missing fields do not import a prior arbitrary value. |
| `30085C10` -> `30076D40`, metadata `3007B0B0` | Loads XML metadata, including actual aliases `PhysicsType`, `ForceGeneration`, `Geometry` and their C++-style names. |
| `3007AC20` (`AddChild`) | Establishes the actual parent pointer at child `+1456` and child vector at `+1464`. A dotted top-level name is not this ownership relation. |
| `30078E80` (`SetName`) | Preserves a top-level supplied full name. With a parent pointer, uses the final dotted component under the actual parent's full name. Only parentless effects publish to the global name map. |
| `3006C110` | Container physics-needed bit derives from own physics kind at resource `+984` (`params+976`) or the actual parent container's bit. All-zero selected trees cannot acquire it from unrelated dotted-name roots. |
| `30064EA0` -> `30061BA0` | Particle birth enters physical entity creation for kinds 2/3; creator selects physical entity type 2 for rigid-body kind 3 and type 5 otherwise. |
| PE enum tables `302D5AF0`, `302D5B50` | Physics: 0 None, 1 SimpleCollision, 2 SimplePhysics, 3 RigidBody. Force: 0 None, 1 Wind, 2 Gravity, 3 Target. |

Requested `X2_EFFECTS.mon_effects.nmon_poison_fog_launch` therefore opens normalized `game/libs/particles/x2_effects.xml` and matches one whole registered key. It does not open `x2_effects.mon_effects.xml`, search a basename, or substitute a child path. A separate preloaded/dotted-library registry profile is not implemented. A library header that disagrees with the cold prefix is unsupported.

Whole original libraries contain unrelated registered names with punctuation and repeated dots, including `doodad_effect..nachashgar_light02red` and `p_pc_test.ytest_14_!`. The library index preserves these complete bounded printable-ASCII keys. It does not reinterpret them as paths or require unrelated graphs to belong to the selected graph's supported profile. The requested and selected graph still pass stricter bounded name/schema checks. Ambiguous case-insensitive registered duplicates refuse the library; child duplicate/reuse/automatic-renaming semantics remain unsupported.

## API, provenance and lifetime

Construct the owner with the actual scene's borrowed `IZoneAssetSource`, then call `Classify(fullEffectName)`. The immutable result contains:

- `Outcome`: `NoPhysicalContribution`, `MissingData`, `Unsupported` or `InvalidData`.
- Requested/resolved full names and source asset path, complete opened byte length, SHA-256, authored library name and particle version.
- Copied emitter records with index/parent index, authored/effective full names, enabled state, recognized physics/force kinds, geometry value and immutable authored parameter dictionary.
- Diagnostics carrying the exact failing emitter/source path. Partial traversal never yields `NoPhysicalContribution`.

`Emitters` counts authored graph nodes, including disabled descendants, rather than currently live engine emitters. `HasNoPhysicalContribution` is true only for the corresponding complete successful outcome. Missing assets/effect names remain distinct from unsupported semantics and malformed input. Read failures propagate through the asset-source contract; they are not converted to successful or missing data.

Libraries and results are cached per owner and exact borrowed source, as immutable snapshots. The owner closes every stream it opens and does not dispose the source. Disposal rejects future classifications and clears caches; already returned immutable certificates remain valid statements about their frozen input. A disposal callback during source reading cannot publish a new cache entry afterward. Failed bounded/XML parses are cached as failures, never partial successes. Actual I/O exceptions do not publish an entry.

Replacement resource bounds, rather than recovered engine limits, are: 16 MiB per library, 1,000,000 XML nodes, XML depth 128, 48 emitter levels, 4,096 selected emitters, 128 parameters per emitter, 1,024 characters per supported name, 128 loaded/in-flight libraries and 65,536 result entries. DTDs, external XML resolution, namespaced directives and unknown processing instructions are refused.

## Original cell `009_014` evidence

The actual full-library probe classifies these nine effects without a per-name success list in the reader. Together they account for all 33 original `ParticleEffect` placements audited in this cell.

| Exact original effect name | Authored nodes | Placements |
| --- | ---: | ---: |
| `x2_sounds.particles.stubble_poison_fog` | 1 | 10 |
| `X2_EFFECTS.mon_effects.nmon_poison_fog_launch` | 5 | 10 |
| `X2_EFFECTS_P.env.elf_light_r` | 4 | 2 |
| `x2_sounds.particles.vendor_elf_bottle` | 1 | 1 |
| `X2_EFFECTS.fire_smoke.torchlight_a` | 10 | 4 |
| `x2_sounds.particles.vender_elf_misc` | 1 | 1 |
| `x2_sounds.particles.vendor_elf_repair` | 1 | 2 |
| `X2_EFFECTS_P.env.elf_light_y` | 4 | 2 |
| `x2_sounds.particles.vendor_elf_food` | 1 | 1 |
| Total | 28 | 33 |

The full original source fingerprints are:

| Asset | Bytes | SHA-256 |
| --- | ---: | --- |
| `game/libs/particles/x2_sounds.xml` | 67,989 | `9434a8bf5a6ae8845faf400c7929b9d1217cfb065af84d440f726fec5c35df45` |
| `game/libs/particles/x2_effects.xml` | 6,426,205 | `95c299f5195d83f463b356d4f5139973b47712169fdba66094491380fc71572b` |
| `game/libs/particles/x2_effects_p.xml` | 5,061,900 | `88a5d03b9690bb4a550ba0f86466069e49785ce5ba18dec535348c791ffded9d` |

Portable tests embed only the unchanged selected subgraphs under their original library metadata. Their reduced fixture hashes are distinct from the complete source hashes above. Permanent controls cover full dotted-name resolution, explicit child ownership, all physical enum labels and aliases, disabled physical children, force consumers, unknown/inherited directives, duplicate names, missing assets, unsupported versions, malformed/DTD input, work bounds, immutable caches and borrowed-source disposal. The external complete-library report and offline source extracts live under `D:/aaemu-runtime/research/managed-particles-stage` and `D:/aaemu-runtime/research/scene-collision-next` on the development machine.

## Required cell integration

A later concrete entity classifier must validate the actual `.ent` and Lua class dependencies, source row properties, parent/archetype/geometry branches, physical masks and current cell generation before composing this content certificate. Dynamic effect/property changes must invalidate or recertify the affected row before coverage can be used. Neither decoded render bounds nor an empty managed particle renderer proves physical clearance.

For the current audited rows, the original class dependencies are `game/entities/particleeffect.ent` (92 bytes, SHA-256 `1ff4380af613bf168a6b8d71329a5fb597e55eeeacb2d90ace735fc20c164081`) and `game/scripts/entities/particle/particleeffect.lua` (4,465 bytes, SHA-256 `9aa8598acf5902bea52de8a33aeec2521fff02e60ff29d9d79cdd570d7fcfdc3`). Those identities are evidence for the future classifier, not validated by this reader. Entity class loading, cell lifetime, script execution, particle rendering/audio and physically active particle effects remain outside this capability. See [the populated-cell collision task](tasks/managed-populated-cell-collision.md) for the other families still required before unchanged original NPC far-return navigation can claim coverage.

The combined suite passes 1,019 tests with no failures/skips; the full analyzer build has 0 errors,
64 existing warnings and no new diagnostics. A source reentry regression also preserves the
shared certificate limit when a callback fills another already-loaded library's result cache.
The final complete-library probe executes core SHA256
`2163927F18A207C502FD7663676A3FAF670595ABC8B36DBBBA1D95B3E1E89609`, identical to the tested core;
all 9 effects/28 nodes pass with 0 forbidden modules and no staged reader assembly. Evidence is
`D:/aaemu-runtime/research/collision-final-particle-content-report.json` and
`collision-leaf-batch-evidence.json`. This is content-reader acceptance; production cell coverage
and the earlier application-entry evidence remain separate.

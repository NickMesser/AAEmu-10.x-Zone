# Managed cell entity collision profiles

`Scene/ZoneManagedCellEntityCollision.cs` classifies actual authored entity rows and their
collision-relevant dependencies. It does not create entities, execute entity scripts, simulate
sound/light/particle output, publish scene geometry, or grant populated-cell coverage. The scene
owns those separate lifetimes and must bind the returned snapshot to its exact current cell input.

## Ownership and input identity

Create `ZoneManagedCellEntityCollision(IZoneAssetSource)` once for the scene's immutable asset
snapshot. The classifier borrows the source and owns a `ZoneParticleCollisionContent` on that
same source. `Classify(ZoneCellEntityContent input, string assetPath)` returns an immutable
`ZoneCellEntityCollisionSnapshot` with:

- The exact `Owner` and `Input` references. Another input object or classifier is a different
  authority even when its bytes happen to match.
- A `Document` dependency containing the supplied provenance path, original retained byte
  length and SHA-256 of **`input.RawDocument`**. Classification never reopens that path or
  substitutes a hash of reserialized XML.
- A row result for every direct `Objects` child, including refused non-Entity children.
  Each retains its original child index, exact `InputRow` when present, GUID/class,
  `CanonicalXmlSha256`, dependency list, area links, optional particle graph and diagnostics.
  The row hash explicitly describes the reader's canonical UTF-8 subtree, not an original
  file byte slice.
- `DocumentDiagnostics` and derived success/refusal counts. No expected input count, cell
  hash allowlist, caller-supplied completeness flag or clear physics query authorizes success.

Successful rows certify no new contribution to native part categories player `0x2` and
ray `0x8000` (`CertifiedPartFlags = 0x8002`). These are distinct from world entity-selection
masks. Every refusal has zero certified flags. A document-level refusal also refuses its rows;
an unknown object child remains an explicit failed table entry.

Class/script dependencies freeze on first read for this classifier. Returned snapshots remain
historical immutable values after disposal. Disposal prevents new classification, clears owned
caches and disposes the particle reader; it never disposes the borrowed asset source. A changed
source, class registration, scripted property, particle effect or entity lifecycle needs a new
certificate and invalidation of any live scene coverage that depended on the old one. A still
readable snapshot is not proof that its cell remains loaded.

## Source-backed class and row prerequisites

Native `32094D70`/`32094E80` consume ordinary `.ent` descriptions. That path initializes custom
creation fields to zero and reads `Name`, `Script` and optional `Invisible`; flag 2's automatic
geometry behavior belongs to separately registered built-ins. The classifier reads the actual
`game/entities/{class}.ent` for each supported class, validates the complete literal XML schema,
requested name and exact reviewed script path. Only AreaShape admits `Invisible="1"`, with no
script. Class children, extra fields, external directives and changed script paths are refused.

The managed certificate is for this ordinary authored resource profile. It does not certify an
external/custom creator, another preexisting class registration, arbitrary Lua monkey patches,
saved runtime state, external callback/FlowGraph injection, or dynamic script execution. Such an
owner must invalidate/refuse the profile rather than treat the ordinary `.ent` as its registration.

The seven standalone original scripts have no `Script.ReloadScript`, include, `require` or
external Lua-file dependency. Exact bytes of every script are verified at runtime against the
reviewed SHA-256 identities preserved in
`tests/AAEmu.Zone.Tests/Fixtures/CellEntityCollision/provenance.json`. An added callback or
helper, even with an unchanged class name, revokes the profile. The `.ent` descriptions are
validated by their complete semantics and retain their actual hashes; harmless formatting
changes need not match an opaque class-file hash.

Native `32097000` has additional geometry, rope, FlowGraph and parent paths. The first profile
requires the ordinary row attributes and one complete reviewed `Properties` or `Area` child.
Geometry, Archetype, ParentGUID/ParentId, Material overrides, instance properties, EntityLinks,
FlowGraph, Rope, unknown attributes/children and case-aliased duplicate attributes are refused.
Numeric vectors/scalars must be finite and supported, not accepted through a partially parsed
default. The Mission/Objects wrapper, all object children, class XML and nested property data
are validated too. DTDs/external entities and processing instructions are never resolved.

## Physical eligibility of the eight profiles

| Class | Actual dependency and source proof | First admitted schema |
| --- | --- | --- |
| AmbientVolume | Original sound script; normal initialization sets CLIENT_ONLY/VOLUME_SOUND and calls legacy NetPresent, with no geometry/physical creator | Complete flat sound property table |
| RandomSoundVolume | Original standalone sound script; server initialization owns sound/local state, not a physical body | Complete flat random-sound property table |
| ReverbVolume | Original sound script; SetSoundEffectRadius reaches sound proxy state only | Complete flat reverb property table |
| SoundEventSpot | Original standalone sound script; Server.OnInit sets local state and calls the validated empty dedicated NetPresent leaf; shared/client callbacks remain audio-only | Complete enabled/culling/obstruction/once/play/sound-name property table; no external geometry or FlowGraph |
| Light | Original Light.lua and native light-slot creator; the resulting light's physicalize/dephysicalize entries are empty | Complete Color/Options/Projector/Style/Test tables; no FlowGraph, external projector texture or unreviewed clip profile |
| FogVolume | Original FogVolume.lua and exact native type-seven render-node creator; physical getter is full null, setter and physicalization slots are empty | Complete original finite fog property table, volume type zero and one positive XYZ Size table; no external resources, extra nested tables or FlowGraph |
| AreaShape | Actual scriptless Invisible class; Points selects ordinary area-manager polygon type 0 | Complete polygon/height, same-input certified sound links, empty TimeOfDay/SkyBox |
| ParticleEffect | Actual original class/script plus the entire concrete `ZoneParticleCollisionContent` result | Complete emitter property table, empty attachment/smart-object selectors, supported full effect graph |

The sound constants are CLIENT_ONLY 256 and VOLUME_SOUND 4096. `SetFlags3202EE80` mode 0 ORs
flags; no inference is made that client-only entities do not exist on the dedicated side.
`NetPresent32031030` is a validated empty dedicated leaf. `SetSoundEffectRadius32031EC0`
resolves sound proxy 3; matching proxy table `320D8D48` virtual 200 is `32091290`, which writes
only its radius field. Audio resource/playback readiness is not claimed or substituted with
fake script services.

Light's original path is `3204B2D0` -> entity virtual 848/`3200F430` -> render proxy `32089570`
-> 3DEngine virtual 1712/`3014F290` -> constructor `3022BEC0`. Matching light table `3029AFF8`
maps physicalize 400 and dephysicalize 520 to empty `30020AD0`. This is a light-slot proof,
not an assumption based on missing Physics keys or a renderer being absent. Texture/audio/editor
strings do not become a claim that those output resources have loaded or rendered successfully.

SoundEventSpot's original `Server.OnInit` sets `bBlockNow=0` and calls `NetPresent(0)`.
Its `OnSpawn` sets CLIENT_ONLY, and its editor/client/shared callbacks load or control audio,
without geometry, physicalization, child entities or external script dependencies. The sound
name is retained property data, not proof that sound playback is implemented. This new class
is not automatically added to the polygon AreaShape link profile, whose existing three sound
classes and exact same-input link requirements remain unchanged.

FogVolume's binding `3203C390` parses properties through `32039CA0` and calls entity virtual
864. Matching constructor `32013B30` installs primary table `320CD998`, whose slot 864 is
`3200F5B0`. That leaf reaches render proxy `320897E0`, which requests type **7** through
3DEngine virtual 1568/`301395F0`; case 7 constructs `CFogVolumeRenderNode` at `300C38D0`.
The adjacent `3200F4B0`/`320896A0` path requests type 5 and is not the fog path. Matching
fog table `30287198` maps `GetPhysics` 376 to full-null `300C0990`, `SetPhysics` 384 to
empty `300C09A0` and physicalize/dephysicalize 400/520 to empty `30020AD0`.

Fog setter 624/`300C2DC0` copies dimensions, color, density, softness and falloff values,
computes render bounds/directions and updates render fields; it has no physical producer.
The original parser clamps volume type, dimensions and several scalar properties, but the
first certificate admits only the complete finite original type-zero profile. It refuses
missing/nested/invalid Size, other volume values and unknown fields rather than guessing
their semantics. Native rendering may load built-in fog materials; this physical-absence
certificate makes no assertion that shaders, materials or render output are ready.

Area proxy constructor `3208C1E0` owns an area-manager record. `3208CD80` checks Points first,
selects type 0 via `32098220`, and transforms the polygon through `3208C570` -> `3209DC10`.
That branch does not create a physical entity. Its different VolumeRadius/Gravity/Bezier path
selects type 3 and reaches physical area type 9 through `3208C870`; this remains refused.
The original empty TimeOfDay/SkyBox children are accepted; nonempty unreviewed environment
data is refused. Area-linked GUIDs must resolve uniquely within this exact input to individually
dependency-certified AmbientVolume/RandomSoundVolume/ReverbVolume rows. A missing, ambiguous,
cross-cell, non-sound or failed target revokes the area result.

Particle emitter slots alone cannot prove physical absence. The classifier first checks the
actual ParticleEffect `.ent` and original script, then passes the authored full effect name to
its owned particle reader. Every actual explicit descendant, even disabled, must pass that
reader's version-20 default/no-physics/no-force profile. Missing effects/libraries, nested
physical particles, geometry, force generation, inheritance and unknown parameters remain
explicit failures. Particle diagnostic paths and complete library hash/version provenance are
retained. See [the particle reader profile](managed-particle-collision-content.md) for the
underlying reader; its concrete owner is `Content/ZoneParticleCollisionContent.cs`.

## Validation and remaining boundaries

The permanent fixtures retain the complete original cell 009_014 entities document, eight `.ent`
files and seven scripts, with byte hashes and checkout preservation. Three further unchanged
complete entity documents preserve all 34 SoundEventSpot rows and the one FogVolume row in
cells 011_014, 011_015 and 010_014. Their source indices remain indices in the full original
documents, not a filtered/renumbered transport. They reuse the existing
particle-reader graph fixtures rather than duplicating another particle implementation.
Focused controls verify every original row and link, unknown row/class/proxy data, changed or
missing dependencies, nested physical particles, duplicate and invalid links, malformed XML,
source identity, immutable cache lifetime, disposal/reentry and I/O failure behavior. Valid new
rows and a genuinely empty authored document are also tested; original counts and hashes are
observations, not acceptance keys. Replacement byte/depth/row/link work limits are managed
bounds, not purported native format constants.
The original six-profile isolated run passed 44/44 controls. The extension adds original-row,
dependency, additional-producer and complete fog-schema controls, plus copied original fog
publication/physical-accessor outputs. Iteration builds use separate entity-scene artifacts;
they do not write the replayed main artifacts. The extended focused suite passes 64/64.

A separate read-only probe through the configured original directory/archive asset source
classified all 74 rows: 33 ParticleEffect, 15 RandomSoundVolume, 13 Light, 6 AmbientVolume,
5 AreaShape and 2 ReverbVolume, with zero refusals. It resolved all 23 area/sound links and
nine complete original particle graphs containing 28 emitter nodes. Its raw document SHA-256
is `C5F294240DF725956560E86CA8917EB54F2DD3C56186C2A8EDD22C151C7D9DB9`.
The process measured 60 loaded modules and zero CryEngine/X2/native-Lua modules; no original
DLL was loaded. Evidence with every row/resource hash and compiled/source hashes is at
`D:/aaemu-runtime/research/cell-entity-classifier/original-resource-results.json`.

The additional 35 rows receive dependency-backed certificates without changing any of the
original 74 target rows. The two Light rows with FlowGraphs, animated objects, elevator,
geometry entities, boid children and unowned particle profiles remain explicit failures.
No distance from cell 009_014 or authored/render AABB is used to exclude another cell's
possible physical contribution.

The subsequent configured-source six-cell probe accounts for 809 direct Objects children:
782 Entity rows and 27 other Object rows. It returns 557 certificates and 252 refusals,
including all 35 new sound/fog certificates and no forbidden engine modules. The earlier
six-profile owner would refuse those 35 additional rows; this extension does not turn the
remaining 252 into physical exclusions. The raw non-Entity children comprise 11 OccluderArea,
seven Portal, six VisArea and three VoxelObject records. They remain explicit refused entries,
rather than disappearing from an Entity-only census. Complete results and exact executing
core/source SHA-256 values are retained in
`D:/aaemu-runtime/research/cross-cell-collision/cross-cell-entity-final-results.json`.

The new offline fog harness executes the actual complete `3200F5B0` leaf, render-slot creation
and reuse, type-seven factory, full fog constructor, material-holder constructor, property
setter and render-bounds math. Fresh original-size and reused/resized controls both return
null from the real physics getter, ignore an external `SetPhysics` pointer and preserve node
state across the actual empty physicalization slots. It declares renderer resource lookup,
fog material shader binding, render registry/slot publication, render quality and CRT dependencies;
it does not claim shader/render parity or a live original entity world. Exact images, declarations
and results are retained in `Fixtures/CellEntityCollision/fog-node-native-controls.json`.
The reproducible offline harness/source audit is under
`D:/aaemu-runtime/research/cross-cell-collision`. No original module is loaded.

This evidence is managed dependency classification against original resources and source
analysis of the native leaves. It is not an executed live original-entity/physics oracle.
Entity script execution, audio/render readiness, area event simulation, dynamic class/proxy
changes and unreviewed classes remain separate work. Live cell coverage additionally requires
the current exact input/owner, complete vegetation/road/other-family certificates and current
physical-publication tokens. This classifier alone cannot admit an NPC route or enable gameplay.

# Managed NPC actor construction

`ZoneManagedNpcActorConstruction` owns the recovered CActor construction state for one exact NPC generation. It resolves `npc.Unit.Snapshot.ModelId` through `ZoneModelContent`. The NPC template's independently cached `npc.Model` is not a fallback for a different World model.

```csharp
using var construction = constructions.Create(handle, npc);
//Prepare/adopt the actor and create its actual physical body through their existing owners.
var installed = construction.Physicalized(body, frame);
var collider = new ZoneAnimatedColliderController(current, body,
    installed.Inertia, installed.InertiaAcceleration, spectatorCollisions);
//Pass construction.Health to the entity script-effects owner.
```

The scene owns the returned construction lease and the body separately. Construction does not step physics, publish a Unit pose, allocate a second proxy, load an engine module or infer combat health. The physicalization event validates the actual body generation, model, actor-model identity and scale before installing the copied animated parameters. Reading them before an event or after body retirement refuses. A body's current-owner callback cannot commit an installation after retiring construction.

The scalar record is the nineteen fields written by the original descriptor installer, not the complete native GameParams structure. Source-order stance installation uses the existing `ZoneActorStanceCatalog`, including its last-duplicate and missing-row rules. Game multipliers and inertia remain unscaled; stance dimensions use the World entity scale. Live scale changes need a separate owner.

## Original 10.x chain

| Producer | Verified behavior |
|---|---|
| `ActorUnitModel::Init`39277D00 | Resolves the actual model descriptor and calls39277AC0 for an NPC class. |
| Class selection39277AC0 and startup registration392B9010 | ActorModel descriptor+196 is `MovementId`:1/3 selects Quadruped,2 selects Monster, every other value selects CPlayer. This is independent of the NPC's Puppet type and AI-object radius. Offline RTTI identifies the three vtables as Quadruped39FD3888, Monster39FD3398 and CPlayer39FD4E58. |
| Base constructor392E6BC0 | Constructs stats+136 through392E7D80, GameParams+1132 through392E86A0, physics parameters+1304 through392E8820, and the nine stance slots. Unit association pointers104/112 start null; the IActor health getters consequently return100. Stats160/184 belong to water state. |
| Base Init392E16B0 | Installs Entity/GameObject/movement-controller ownership, obtains AnimatedCharacter, then invokes actor virtual568 and392DF860 with the resolved ActorModel descriptor. |
| Descriptor installer392DF300 | Writes max-grab mass/volume, eleven movement multipliers plus grab multiplier, inertia/acceleration, jump height and lean shift/angle. It separately installs source-order stance rows and scales authored dimensions. CPlayer's override392FC590 calls this installer before its additional look-IK fields. |
| Body physicalization392E2F20 | Constructs dimensions/dynamics from the installed actor fields, invokes the entity's actual physicalization, then actor virtual560. |
| Postphysicalization virtual560 | Quadruped392EF640, Monster392EDAD0 and CPlayer392FE1F0 each call392E1CA0, then copy CActor1256/1260/1356 to AnimatedCharacter372/376/380. They OR parameter flags with0x18 and set byte400 to1. The copied tuple preserves all other existing parameter bytes. |
| Animated getter/setter306634C0/3065FCC0 | Getter returns the existing40-byte block at368; setter copies it. These methods alone do not immediately rewrite living-body dynamics. Collider arbitration30669A20 and the separately gated movement-mode consumer30664190 perform those effects. |

The generated model script contains graph names, model paths and AI movement speeds. Original `npc.ActorTemplate.gameParams` also contains Lua inertia30/accel0, while BasicActor contains20/11. Those similar fields are not substituted for the descriptor values in this recovered native construction/physicalization chain. For example, original actor_models7/8 (models16/17, elf male/female) have MovementId0, inertia0, acceleration0 and impulse-recovery time1, so this chain installs0/0/1.

A read-only census of the original zone129 spawner references resolves99 NPC templates to33 actor-model rows:93 select CPlayer,3 Monster and3 Quadruped. Their installed inertia/acceleration/recovery tuples are0/0/1 for64 templates,20/11/1 for32 and4/11/1 for3. CPlayer is the original implementation class here; it does not turn an NPC Unit into a player or change the script's `IsPlayer` identity. This census describes authored references, not executed population or successful gameplay admission.

## Health and event boundaries

The original IActor health getters392D7400/392D7460 prefer client Unit112, then server Unit104, and return100 while both pointers are null. After server association, current health reads the Unit's live signed64 precise points with the native division/remainder rounding; maximum health queries its actual stat6. The dedicated managed profile owns the server association, and requires complete current vital stats for a maximum lookup. CActor stats160/184 are water state; the earlier0/10 health interpretation was incorrect.

Entity `Server.OnInit` runs during model creation. Afterwards3925B660 invokes ServerUnit virtual240/3935CA90, which calls `OnServerUnitCreated`392E0190 and associates the exact Unit before NPC group/skill and character initialization. The managed post-OnInit hook preserves that order. Lua health results are invariant decimal **strings**, matching3930D390/3930D4B0, with no float conversion. See [actual health ownership and controls](managed-npc-health.md).

Initial AnimatedCharacter reset30660860 stores the system frame in468, sets current-frame464 to−1 and serialization-frame480 to0. Update sampling30663C70 obtains the system frame for464. Serializer30661CD0 copies464 to480 when reading. Force-rebuild virtual296/3065FB70 only sets byte2508. Thus the existing collider API's `PhysicalizedFrame` label refers to the frame-gate field468; it is not proof that every subsequent body physicalization rewrites that field. Initial construction and physicalization may share a frame. Later resets, serialization loads and body replacements require their actual event owners, not an unconditional frame reset.

The same reset clears all seven request/reason slots, selected mode1956, requested/applied flags1960/1964 and force byte2508. It does not manufacture a forced update. The per-frame consumer is entity event29: EntitySystem virtual32/3201D130 visits prephysics registrations, and3200FEF0 forwards the event with elapsed seconds to proxies. AnimatedCharacter30662840 handles29, refreshes its character owners, then calls306622D0. That samples frame/delta through3066A9B0→30663C70 and, when delta is positive, executes3066B9A0, collider arbitration30669A20 and movement-mode inertia30664190 in order. The scene's prephysics hook can drive the existing arbiter with its one sampled frame; it must not add another physics Step or assume the remaining animation/movement-mode work is implemented.

## Outgoing state still has separate producers

ActorUnitModel39278AC0 derives outgoing low flags from CActor124&0x1000, on-ground timer148, transition byte416 and jump byte418. Delta comes from the actual movement request+44 when request flags include0x20; otherwise it uses zero. Alertness comes from the actual actor proxy's behavior (or its separate native Unit fallback). None is inferred from Puppet urgency or a request velocity.

The per-frame ground classification392E0C80/392E0B50 uses flight, cached-floor, jump, ladder and other actor state before updating timers and transition bytes. A raw body's `IsGrounded` value does not by itself port those producers. The production movement encoder therefore keeps its explicit metadata admission boundary. Full GameParams, player IK, ragdolls, actor reset/revive, special locomotion, animated movement-mode ownership and complete Puppet/behavior gameplay remain separate work.

`ManagedNpcActorConstructionTests` exercise descriptor mapping, actual World-model versus template-model identity, every native class-selector branch, real body→animated collider parameter consumption, preservation of the sole pose/velocity writer, wrong-body and callback retirement, reused numeric identities and atomic invalid-content refusal. Research extracts and the read-only original database census are in `D:/aaemu-runtime/research/npc-construction-*.c` and `npc-construction-source-evidence.json`; tests require neither those local files nor the client.

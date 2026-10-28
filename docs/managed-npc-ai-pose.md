# Managed NPC AI proxy pose

`ZoneManagedNpcAiPose` owns the original actor-to-AI pose transfer for admitted upright, unparented NPC bodies. Bind after actor adoption, real body creation and construction's physicalization event. Refresh at the scene's chosen postphysical publication boundary. The caller owns that frame ordering; the pose owner performs no physics Step and writes no Unit/body transform.

```csharp
var poses = new ZoneManagedNpcAiPose(actors, bodies);
var pose = poses.Bind(actor, construction);
var copied = pose.Refresh();
```

`copied.EntityPosition` is the physical entity origin. `copied.Position` is the AI view position, computed from the entity origin plus the accepted actor stance's `ViewOffset`, rotated by the independent CActor view quaternion. They are not interchangeable. The scene's later AI-position refresh/search callbacks must preserve this admitted proxy position rather than overwrite it with the Unit origin.

The initial no-steering profile owns CActor movement output0 and controller look target0. Body facing and the zero-output fallback use actual body orientation. Falling velocity and desired Puppet movement controls do not become AI movement direction. A nonzero lower-level body wish without the CActor output producer is a named refusal before any AI state mutation.

## Original fields and transfer

| Original source | Verified contract |
|---|---|
| CAIObject serialization3103E638 | PredictedPosition132, BodyDirection144, MoveDirection156, View168. All are initially zero through3103D7F8. |
| Original getters | Body virtual128→310098BC returns144; Move virtual144→310098C4 returns156; View virtual160→310098CC returns168. |
| Original setters | Body136→3103CDD8, Move152→3103CE08, View168→3103CE38 each copy then normalize through3100D534. Squared length<=FLT_EPSILON produces zero; otherwise multiply by1/sqrt. |
| ActorMovementController state | Constructor392EAEB0 installs table39FD3280. Getter virtual8→392EC580 returns the copied state at controller192. |
| Controller refresh392E99D0 | Copies the entity origin, derives world forward from Entity world quaternion328 and writes controller204 body direction. Move direction316 uses CActor876 when its squared length>.01; otherwise uses Entity local quaternion272 forward. |
| View direction and position | If controller look target376 is nonzero, normalize its difference from the **previous** cached view position228, with world-forward fallback for a near-zero difference. Then update view position228 to entity origin plus CActor848 rotation times virtual392/392D6A70's accepted stance view offset. The order matters when the entity moves. |
| CActor movement output | CPlayerMovement39302A20 and QuadrupedMovement392F0E60 copy their actual output request velocity to CActor876. This is neither arbitrary desired Puppet direction nor the resulting physical velocity. |
| Initial view quaternion | ActorUnitModel39277D00 calls actor virtual368 with the initial model rotation.392FCC00/392ECAC0/392EEF40 copy it to CActor848, independently of entity rotation and the movement request quaternion. |
| CAIProxy BodyInfo | Constructor30516370 installs table30725888. Virtual24→305159C0 gets the actual movement-controller state, mapping body direction to BodyInfo48, movement direction to60, view direction to24 and view position to12. |
| CAIObject3103DA78 | Its nearby same-entity proxy branch copies BodyInfo12 to AI Position108, then invokes the three independent direction setters. The proximity gate compares BodyInfo entity origin against the supplied entity origin; same-owner samples select this branch. It does not substitute a scale-bearing entity matrix column. |

The managed physical admission already refuses parents/seats/tilted ground bodies. Its entity world and local rotations are therefore equal. The pose owner retains the distinction in the source contract and refuses profiles requiring the missing parent transform rather than assume it remains equal.

## Copied upstream state and reset boundary

`CopyActorMovementOutput`, `CopyControllerLookTarget` and `CopyActorViewRotation` accept the actual corresponding producer outputs. They are not convenience steering APIs. The immutable snapshot retains these inputs for inspection.

Raw controller construction zeroes the cached view position. Reset392EAA70 can immediately refresh it through392E99D0 when CSystem virtual320 is0. Offline CSystem table3660E0C8 maps that getter to3650B430 (field2424); its setter3650B420 is used by CryAction306F8DD0/307044B0 for the load mode and restores0. Raw constructor zero must therefore not be advertised as the final fresh/reset cache.

A first no-look `Refresh` does not read the previous cache and establishes an owned value. A nonzero copied look target requires either that refresh or `CopyControllerViewPosition` from the actual reset/update producer. Otherwise refresh refuses atomically. Subsequent view-target calculations use the previously sampled position before committing the new one.

The owner validates exact scene actor, construction, body reference, source NPC, model and scale identities. Body-current callbacks cannot publish after retiring another owner. Disposal removes the pose lease without disposing the independent actor/body/construction. Retained snapshots remain immutable observations.

## Look goals and turning remain separate

COPLookAt310DAD28 uses Move for angular initialization, reference Body for `refpointdir`, and actor Body or View for completion. The existing neutral goal interface names match those getters. Reference-position setter3115F6A4 takes actor Move; similarity30525690 compares reference Move with actor Body. Tests use three distinct vectors so equal dummy defaults cannot conceal a mislabeled getter.

The native turn path is more than a positional assignment. CPipeUser311683DC copies loose attention into its output tuple. Puppet3116CCBC adds target/perception/path selection, vertical clamps and smoothing through3116BD2C. CAIProxy30518020 builds a movement request using look position, use-body-direction and special-look flags, then invokes the actual controller request handler392EAA30→392EA280. Controller392EAFC0 applies class/link/animation/look-limit gates before producing look target376 and rotation/movement outputs.

This pose owner does not claim those unported producers. It does not read `LookState` and invent a controller target, turn the body directly, or report a look goal complete because a copied target exists. The current no-steering production profile can publish live facing/view position; actual look-driven turning still needs that motor pipeline.

`ManagedNpcAiPoseTests` cover live quaternion facing and stance offset, distinct Body/Move/View consumers, falling versus desired movement, refusal of unowned wishes, previous-position timing, unknown reset-cache refusal, native normalization thresholds and exact-owner/callback retirement. Source extracts are local research artifacts at `D:/aaemu-runtime/research/npc-aipose-*.c`; the tests require no original DLL, script, database or native module.

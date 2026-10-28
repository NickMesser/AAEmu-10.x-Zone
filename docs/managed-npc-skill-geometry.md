# Managed NPC skill range geometry

`ZoneManagedNpcSkillGeometry` implements the ordinary ActorUnitModel sphere profile used by the native Unit skill-location path. It consumes the live model transform and the resolved actor descriptor. It does not read a living capsule, a render bound, a navigation radius, or a guessed entity center as a substitute for the native aim geometry.

## Verified producer

`3925C3D0` obtains caster/target model contexts through virtual +416. For ActorUnitModel, vtable `39FC4168` resolves that slot to `39264FE0`. It obtains virtual +1360's actual model transform and calls `39CE7870` with the position, raw quaternion, and uniform scale. Binary instruction `39265013` explicitly loads the fourth float argument from transform +28 into XMM3; the decompiler omits that argument from the call text.

Actor creation `39277D00` installs an `AimSphere` via `39CE8020`. Its local center is `(0,0,ActorModel.Height)`, and its local radius is `ActorModel.Radius`. The binary loads actor descriptor +172 at `392780D8` for center Z, and descriptor +264 at `392780F5` into XMM2 for the radius. That radius argument is also omitted in the decompiled call text. An authored `AddBox` adds another aim volume and changes the enclosing caster query sphere; this slice refuses that compound profile.

`39CE6650 -> 3929D760` scales and rotates the local center, narrows its world coordinates to float before storing them in native double fields, and multiplies the radius by the actual uniform scale. The managed helper preserves those operations and the raw quaternion; it does not normalize it.

The configured content gives NPC template7987 model970, which resolves to ActorModel433: Height2.5, Radius2.5, AddBoxfalse. Its skill14133 has min0/max25, target angle360, valid height0, and no obstacle check. Two such actors at equal height and with origins30 units apart have a signed aim-sphere distance25 and pass the original range predicate. Center-distance validation would incorrectly reject that case. No character-skeleton bounds reader is required for this verified ordinary actor producer.

## Public integration

```csharp
new ZoneManagedNpcSkillGeometry(registry, models, copyActualModelTransform);
```

The transform callback returns `ZoneNpcSkillModelTransform(ModelId, Position, Orientation, UniformScale)` for the exact current Unit, in the engine scene's local coordinates. Production must supply it from the actual model/body owner. The service checks registry allocation identity before and after copying, rejects a different model ID, and rechecks both allocations before returning a result. It has no pose writer or frame step.

Methods:

- `byte ValidateLocation(caster, target, skill, minimumRange, maximumRange)` consumes already resolved skill/equipment/modifier ranges. It returns native0 success,14 too close,15 too far, or13 invalid height.
- `float Distance(caster, target)` implements `3925AF80`'s fallback/picker metric: the smaller of native float model-origin distance and the signed aim-volume distance. It does not clamp overlapping geometry to zero.
- `CopyAimSphere(unit)` returns the copied transformed sphere for inspection.

`39CE6AA0` measures the caster's enclosing query sphere against the target's aim-volume list through the `3A5A4310` pair table. This admitted profile has one sphere on each side, so the exact pair is `39D464F0`: double center separation, minus the float sum of radii, finally returned as float.

`39CE6C20` admits a positive minimum only when distance is strictly greater than it; equality rejects with14. The maximum is inclusive. A nonpositive minimum bypasses its lower-bound predicate. Valid height is evaluated after range, using absolute aim-center Z difference; edge-to-edge mode subtracts the float sum of radii and clamps at zero. Equality with valid height is admitted.

`39259B00` supplies the raw resolved angle; there is no external division by two in this call. `39CEE2F0` admits360 or greater immediately, and nonpositive angles bypass the angle predicate in `39CE6C20`. Finite-radius partial angular cones remain unported. Obstacle checks require the original ClientRaycaster's masks and exact owner ignores, so a reached obstacle branch explicitly refuses. A prior native range/height rejection still returns its real code before those later branches.

The service also refuses unresolved/non-Actor models, AddBox compounds, Big-target `39CE81A0` validation, unowned model transitions, unresolved equipment angles, nonpositive actual scale, and nonfinite/unrepresentable inputs. Prefab aim helpers, mesh/point/cylinder target volumes, partial-angle failure16, and obstacle failure12 are separate dependencies. Basic Unit eligibility, target masks, requirements, cooldown/modifier calculation, and World request/ACK ownership belong to the NPC skill executor.

## Original comparison

Original binary SHA-256: `50edcd49d0537aa0f94db55ecf22008d7fb9ed937ea02121919199b5bf8bb7ed` (`Bin64/x2game-dev_dedicate.dll` from the configured10.0.2.13 source).

`D:/aaemu-runtime/research/skill-geometry/native-skill-geometry.py` executes the original `39CE73F0`, `39CE6650/3929D760`, `39D464F0`, and complete `39CE6C20` instructions under Unicorn. It loads no DLL and uses no geometry callbacks. Controlled single-sphere context vectors are supplied in emulated memory. `native-skill-geometry-controls.json` contains29 controls: coordinates near8000, exact range/minimum boundaries, overlapping spheres, center/edge height boundaries, raw quaternions, uniform scale, and disabled/full-circle angle branches.

`managed-skill-geometry-comparison.json` reports29 controls and zero failures: world sphere center/radius bits, signed-distance bits, and validation codes all match. The controlled comparison does not cover the refused geometry profiles. Seven focused repository tests additionally cover actual World model selection, separation of fallback and validation metrics, exact allocation retirement, and explicit dependency refusals. Build/test artifacts are isolated at `D:/aaemu-runtime/npc-skill-geometry-artifacts`.

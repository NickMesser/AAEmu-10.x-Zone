# Managed NPC reference and look objects

`ZoneManagedNpcPointObjects` owns the two dummy objects allocated by the original
CPipeUser constructor `31163974`: reference subtype2 and look subtype3. They retain
entity ID0 and independent state. `3105AF48` stores these allocations in the dummy
container, separate from the nearest-object type index; registering them as normal
type0 actors would change queries. Names follow `31159444`'s `<AIName>_RefPoint`
and `<AIName>_LookAt` forms.

The reference starts with zero position and directions. The look object starts at
zero with direction `(1,0,0)`. They are not initialized from a guessed actor facing.
AI-object offsets are independent: position108, predicted position132, body144,
move156 and view168. These directions are used by the actual locate/look goal
services. Entity position queries continue to return the entity origin, while the
AI proxy publishes the stance's view position.

## Reference changes

CryAction `30524ED0` sets reference position from a vector, the current attention
target, or a numeric entity ID's AI position. The numeric target uses the original
single-precision integer conversion. Missing entity/attention results return no
Lua values. Successful setters return numeric1. Named and formation resolution
still require their original scene owners and retain a named refusal.

Position-only changes use the actor's **move** direction. `305252F0` normalizes an
explicit reference direction with the original `FLT_MIN` denominator; an exactly
zero direction succeeds without mutation. Tiny nonzero directions are retained.
The dummy's body/move setters copy the supplied direction, while its view setter
normalizes separately with `3100D534`'s epsilon rule. `30525690` compares horizontal
reference move direction with actor body direction using the strict original
threshold. Radius is the independent `+192` field and may be negative.

Actual reference mutation records actor event19. A position component delta above
`.001`, with an active pipe, sends listener event7 to the innermost pipe. It uses the
existing notification guard and does not reselect the pipe or recreate goals.

## Look targets and retirement

`3115BE74` updates the owned look point and loose-attention state. Forced look
selection survives ordinary look-point updates. `self`, `refpoint` and
`lookat_target` resolve their actual owners; the latter exists only when loose
attention is enabled. Retirement removes all other actors' argument, attention
and loose-look references to either dummy, and invalidates their exact lease.

This supplies real goal inputs. It does not synthesize the missing CPuppet look
motor, actor turning, animation requests or physical movement from those inputs.

## Evidence

`ManagedNpcPointObjectsTests` includes18 controls executed from the original
CryAction `305252F0`/`30525690` instructions in an offline emulator. Every normalized
direction float bit and similarity result matches, including zero, subnormal,
overflowing squared-length and threshold cases. The fixture is embedded at
`tests/AAEmu.Zone.Tests/Fixtures/npc-reference-native-controls.json` with source
SHA256 and an explicit scope declaration. Script/entity lookups and the setter
are ABI callbacks in that oracle; it does not independently validate CryAI's
dummy setter or event dispatch. Separate managed integration tests exercise
the actual dummy inventory, look goals, notifications and generation retirement.

Finite-coordinate validation is a managed admission policy. Original native
functions are not claimed to reject every nonfinite input the same way.

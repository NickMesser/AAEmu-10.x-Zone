# Managed NPC living relocation

`ZoneManagedNpcRelocation` implements the physical portion of the original NPC
translation operation. It borrows `ZoneManagedNpcPhysics` and the scene's existing
backend. It does not call `Step`, advance a clock, write a Unit/spatial/AI pose, or
send movement replication.

## Publication contract

```csharp
var relocation = new ZoneManagedNpcRelocation(npcBodies);
var copied = relocation.Relocate(actor, engineLocalDestination, result =>
{
    //The scene has already preflighted these actual owners.
    //Publish result.Current.Status.Position through the sole Unit/model/spatial
    //publisher, then refresh the owned AI proxy pose and replication sampler.
});
```

The caller preflights actual model/Unit/spatial/AI and replication owners before
entering this operation. In the admitted unparented profile, the pre-operation
entity/model position must agree with the copied physical position. Rotation and
applied scale remain unchanged. The result copies the exact actor generation,
model ID, scale, requested position, previous/current body snapshots, number of
earlier body commands flushed, and whether entity translation changed. Accepted
actor stance slots, applied physical dimensions and the last stance-drain outcome
remain separate fields; the owner does not select a slot from a requested shape.

Physical effects precede the callback. The callback runs once inside a shared
nonreentrant backend boundary; recursive relocation, a second wrapper's relocation,
and scene `Step` are refused. A publisher exception reports failure after any
already committed ordered effects; the owner does not return a successful result
or roll physical state back across a scene publication. Retirement during a
callback prevents subsequent publication/body writes and cannot reach a fresh
generation with the same numeric ID. Commands issued by the callback follow the
reset and remain pending for the next scene step.

## Recovered actions and surviving state

The original path is Unit `3925A7C0` -> ActorUnitModel `39269040`. It copies the
existing quaternion/uniform-scale/translation transform, replaces translation,
and dispatches these operations before the entity transform write:

1. Physical Action4 with its reset field `+4=1`.
2. Physical Action10 with `+4/+8=0x80000000` and `+12..+39=0`.
3. Entity virtual `+296`, flags0 for NPC/model kind1 (also kind5).

Living Action `3502A300` implements the two actions directly. Action4 zeros actual
velocity `448..456` and requested velocity `460..468`, sets flying bit1 at952,
zeros suspension offset556 and velocity560, and sets suspension time568 to1.
Action10 copies its zero vector to both velocities and sets persistent bit2.
It does not represent an Action1 mode3 request.

These actions preserve suspension acceleration564, impulse recovery timer608,
cached floor572 and normal484..492, other controller flag bits, and the one-shot
override1120. The replacement preserves those copied controller fields. It clears
the public verified support/material and obsolete recovery result at relocation;
the new location has no validated support, while native auxiliary floor/normal
caches remain available to the next ordinary living step. Geometry, dynamics,
material, collision mode, mass, gravity and dimensions remain installed.

Entity `+296` is `32016DB0`, forwarding to `32012940`. With unchanged orientation
and scale, it changes all translation components only if any single component
differs by **strictly more than `.0001f`**. The reset actions still run when the
entity translation is unchanged. The result therefore distinguishes requested
position from actual current position. There is no movement sweep along an
explicit relocation; ordinary movement continues to use scene collision.

## Ordered requests and explicit scheduling scope

Native `35023200`/`35023650` share one parameter/action queue. The managed backend
now keeps velocity, stance and network-correction pose requests in one call-ordered
log, with their existing per-kind pending state and immediate C0 velocity/wish tail
writes. The existing scene Step drains this log instead of grouping corrections,
stances and velocity requests by type.

Relocation synchronously flushes earlier requests for that exact body in call
order, then resets it. It does not discard an accepted stance or another actor's
commands. Exact-generation retirement removes its queued work; a replacement
generation cannot consume it. Requests created during a drain belong to the next
drain. A query callback adding further commands during relocation's flush produces
a named failure rather than silently placing them before or after the reset.

This is a single-thread synchronous flush policy. Native world-thread scheduling,
cross-entity command ordering and arbitrary physical classes are not claimed.
The production position-check caller runs after the scene Step, when its earlier
commands have already drained. Other physical classes, live scale/model changes,
parented/attached profiles, moving supports and other unowned physical state must
be admitted by their actual owners before this operation can serve them.

## Verification

`ManagedNpcRelocationTests` exercises nonzero actual/wish velocity, persistent
mode flags/one-shot override, floor-cache versus verified-contact state, unchanged
rotation/scale/dynamics, the strict translation threshold, invalid-value refusal
before drain, both stance/correction orders, accepted/applied stance independence,
publisher reentry/failure, generation retirement/reentry, other actor queues, and
requests created after the reset. Existing living/C0/stance, NPC body and collider
tests pass through the new ordered-drain seam.

The external evidence `D:/aaemu-runtime/research/native-npc-relocation-controls.json`
contains13 executed original-instruction controls and binary SHA256 hashes.
`native-npc-relocation.py` executes the original Unit/model/living-action/queue
instructions under Unicorn without loading any engine DLL. Model-transform lookup,
the final entity publication boundary, CRT memset and physical-world thread
classification are explicit ABI callbacks. It verifies action payload/order,
unchanged raw quaternion/scale, kind1/kind5 flags0 versus other-kind0x2000, physical
absence, and the preserved reset fields. The final entity callback does not execute
the complete entity event/spatial/replication chain; the `.0001f` gate is supported
by `32012940` source and independent managed boundary tests. This evidence does not
claim a fully admitted NPC position-check or gameplay profile.

# Original NPC return state

The managed scene runs the original `scripts/ai/behaviors/x2/common/return_state.lua`.
The behavior's constructor, completion, timeout and destructor remain Lua code. The
scene provides the following concrete leaves; it does not synthesize completion of
the `approach` operation or replace that operation with a teleport.

## Return dependencies and ownership

| Original path | Managed owner | Meaning |
| --- | --- | --- |
| `AI.GetAlertness` / `3052AE10` | `ZoneManagedNpcActorMotion.CopyPuppetAlertness` | Reads the independent Puppet alertness, not `ZoneNpcBehavior.Alertness`. |
| `AI.GetRefPointPosition` / `305360D0` | `ZoneManagedNpcPointObjects` | Returns a new Lua vector copied from the actor's indexed reference-point dummy. |
| `unit:NpcTeleportTo` / `39353B80` | `ZoneManagedNpcPositionChecks.Teleport` and `ZoneManagedNpcRelocation` | Applies the actual model/body translation route and publishes the result through the existing scene publisher. |
| `unit:CreateBuff` / `39352F20` | `ZoneManagedNpcGameScriptRequests` | Queues `BuffCreationRequest` to World; it never inserts a local buff. |
| `unit:RemoveBuff` / `39353020` | `ZoneManagedNpcGameScriptRequests` | Queues template removal with invalid instance0/reason10. World ACK remains authoritative. |
| `ClearAICombat` | Original `BasicAI` method and existing combat/request owners | Clears the admitted fresh/no-threat combat record, requests cast cancellation, then clears attention. |
| aggro and AI time-event activation | Current `ZoneNpc` fields and `ZoneNpcTimers` | The original constructor/destructor mutates the same generation's actual state. |

`3052AE10` resolves Entity456 → AI64 → IPuppet128 (`31177A88`, full Puppet4160).
It returns a float or no value when that interface is absent. `CAIHandler` behavior
alertness changes after a behavior constructor and is a different field. The current
bounded Puppet profile owns the original constructor value0; later proxy alertness
transfer (`311680BC`) is a separate producer and must not be inferred from a Lua
behavior table.

The reference getter resolves Entity456 → AI48 → IPipeUser216 → dummy120. It does
not fall back to an entity origin or the AI eye position. The original completion
callback compares this position with `entity:GetPos()` in three dimensions and
teleports only when squared distance is strictly greater than4.

## Teleport call chain

The matching10.x PE confirms that the generated `393579B0` wrapper requests a
`ScriptAny10` vector. Missing or invalid vector conversion returns no Lua values
without invoking the leaf. `396115C0` dispatches this wrapper for the registered
`39353B80` leaf; the binary preserves its vector argument in R8 despite the
decompiler's incomplete function signature.

The actual call chain is:

```
39353B80 → 3933B460 → Unit168/3925A7C0
  → model1256/392656A0 (copy transform)
  → replace translation
  → model1336/39269040
  → physical actions4(reset1),10 when the model has active physics
  → Entity296 transform publication
```

The managed route preserves orientation and scale, flushes earlier exact-body
commands in source order, and uses the existing sole publisher for Unit pose,
spatial membership, model transform and AI pose. It never steps physics. Exact
generation checks surround each external callback/publication boundary. A retired
Lua receiver cannot relocate a replacement with the same World Unit ID.

This leaf performs no CheckPos region or distance test, posture restoration,
`3933B510` replication-cache reset, or position-correction count update. Those
effects belong to the independent CheckPos caller. The ordinary movement sampler
can observe the translated model later.

A preceding source-owned turn may already have changed the logical entity
quaternion while its physical orientation command is still queued. Relocation
preflight requires the logical quaternion to match the actual latest queued
orientation metadata, or the applied quaternion when none is pending. The
existing ordered relocation flush applies that command before reset/translation;
publication verifies the resulting quaternion before writing the Unit pose. A
physical-only queued rotation that disagrees with the logical model refuses
before the flush. No applied-body state is fabricated during preflight.

## Requests and remaining profiles

`39352F20` sends `ZWCreateBuffPacket` opcode10 through `39362830`, then returns
Lua true. The managed request uses the current actor's World Unit ID and template
ID. Scene requests are queued under the exact script actor scope and unsent
requests are discarded at retirement. No local buff list or model effect changes
until the existing World acknowledgment arrives.

The actual return constructor can request a restoration skill and buff when the
actual Puppet alertness is combat. Completing that profile requires the real
Puppet alertness producer, the `unit:StartSkill` script ABI and its authoritative
cast/controller owner. Adding a buff request does not admit those other leaves.
The optional `ClearSkillController` branch and nonempty-threat/saved-flying
`ClearCombat` branches also retain explicit refusals until their actual model,
hostility and restoration effects are owned.

Auto-disable is an independent source-backed proxy countdown owner. Fresh startup
grace, explicit always-update requests, the nonhidden multiplayer GameObject
policy and later restoration must remain distinct. The return constructor saves
the actual getter result; the destructor restores it only when that result was
true.

## Permanent controls

`ManagedNpcReturnStateTests` embeds unmodified original files:

| File | SHA256 |
| --- | --- |
| `return_state.lua` | `268432f6c18c7ecd1c2a621e26f106ae3b8a2c8134f0d5e2f0383413e8bfcc8b` |
| `scripts/Utils/Math.lua` | `bdbc5c55124bc3e0998cf986f25f2d5ae448ef7d07d513d8e5394ebd95a84e79` |

The controls cover independent Puppet/behavior alertness; actual teleport
publication and preserved cache/posture; invalid vectors and retired receivers;
strict horizontal/vertical two-metre completion; and interrupted constructor/
destructor restoration. Additional real Lua teleport and CheckPos controls prove
pending multi-turn order, reset velocity, exact final quaternion publication and
refusal of mismatched logical/physical rotation metadata. The constructor control uses its real
`goReturnState=false` branch because that fixture does not claim an Approach
service. The separate unchanged timeout callback starts from an explicitly copied
count20, proves that19 events do not complete, then observes the real teleport and
signal owners on event20. These bounded controls do not claim an end-to-end normal
Approach path. `ManagedNpcGameScriptServicesTests` verifies the real buff request
and retirement scope without optimistic local mutation.

Source/PE research uses the configured original10.x client and offline inspection;
none of these controls loads a CryEngine module.

# Managed NPC group queries

`ZoneManagedNpcGroups` owns the group record produced by actual CPuppet parameters and
later NPC initialization. `AI.GetGroupOf` reads that record; `AI.GetLeader` queries the
owned group map and its separate leader allocation. This slice supports groups whose
original leader/formation producer has not executed. The first authored movement leader
refuses admission because its formation and Lua consumers remain unowned.

## Construction and script ordering

The constructor takes the scene's borrowed `IZoneLuaRuntime` and loaded `ZoneNpcContent`.
Bind a generation lease after actor/script adoption and before `Server.OnInit`:

```csharp
var lease = groups.BindEntity(binding);
```

In the existing after-OnInit hook, before NPC skill children and the character constructor:

```csharp
groups.ApplyInitialParameters(actor);
groups.InstallAuthoredGroup(actor);
```

The scene owns the returned lease. Dispose it before the script binding; actor or binding
retirement independently revokes access. The group owner borrows Lua and never disposes it.
An old lease cannot remove a different registration. Queries prune retired registrations.
Positive group allocations remain present after the final member leaves, as the original
map does; owner disposal retires the map.

Spawner detach calls `groups.ClearActorGroup(actor)` while the exact actor is still live.
Original39344AA0 passes its empty record through AI virtual512/3103D1C8; the type and ID
globals3A607DA4/3A607D74 are zero-filled, and the member byte is explicitly zero. The
managed consumer removes actual membership and copies `{0,0,0}` without changing
`Properties.groupid` or `AI.formationTension`. Initial/child-installation phases remain
completed. Repeated clearing cannot allocate an AIGroup0, and an old generation cannot
clear a replacement member. This uses the admitted absent-CLeader profile; it does not
implement tactical formation teardown for an unowned leader.

Initial parameters read the actual entity table after authored OnInit. This
boundary follows the original fresh server construction:
`3935D260` calls `3925B660`, whose model creation completes Entity/ScriptProxy
`Server.OnInit` before Unit virtual 240 dispatches `3935CA90`. That handler first
associates the real Unit with CActor and then calls model virtual 1016
(`392654D0`), virtual 1280 (`39280060`), and `3927F610`. The type-5 branch invokes
the actual CGame parameter reader `392ADC00`. Original `BasicAI.Server.OnInit`
has its `self:RegisterAI()` call commented out. Thus this profile observes actual
OnInit property mutations; it does not claim the separate property-change or
dynamic re-registration lifecycle.

A numeric
`Properties.groupid` takes precedence over `PropertiesInstance.groupid`, including zero
and negative values. A wrong-type or missing first value permits the second lookup.
Missing numeric values leave the native parameter default0. Script values pass through
float32 and native signed32 truncation before negative IDs clamp to0. Nonfinite values
receive a named managed refusal. Out-of-range finite float conversion follows the native
integer-indefinite result, which then clamps to0.

The fresh constructor record is `{type=0,id=0,member=0}`. Applying the same ID does not
register membership or create an AIGroup0. A later valid `Spawn.Group.Type` overrides the
full type/ID/member record and writes the original `Properties.groupid` and
`AI.formationTension` inputs. An unknown group descriptor preserves the actual initial
record; the wire ID alone does not manufacture a valid group. Signed32-positive runtime
IDs, including50000, retain their full identity. The native signed16 traversal quirks of
other group consumers are outside these query APIs.

## Source-backed membership and leader absence

The following original call chain produces the initial group:

| Original function | Relevant behavior |
|---|---|
| CAIObject3103D7F8 | Initializes type212 from its zero-filled default, ID216=0, member220=0. |
| CAIObject3103E638 | Serializes `type`, `grpId`, and `grpMemIdx` at those offsets. |
| AIParams392A10F0 | Initializes parameter120 to0. Parameter116 is species. |
| CGame392ADC00 | Reads `Properties.groupid`, then `PropertiesInstance.groupid`, into parameter120. |
| Entity32012370 | Creates the CPuppet and invokes its IAIAgent parameter setter. |
|3116BC4C ->310068BC ->310063BC | Applies parameter120, clamps negatives, and changes membership only when the ID changes. |
| NPC39346960 | Installs a resolvable `Spawn.Group`, updates its actual Lua inputs, and invokes the first movement-leader producer. |

`CAIObject+520` returns the full group record through3103CD98. `+504` returns its ID.
The setter310063BC removes old membership3105B238, writes the ID3103D198, then adds
membership3105B058. Positive IDs create an AIGroup through31059B48/3101BDD0. Removal
does not erase that group allocation. These operations preserve the actual arriving
member order and exact actor generation in the managed query owner.

AIGroup has a separate CLeader pointer at232, explicitly initialized null by3101BDD0.
AISystem.GetGroup1608/31054078 returns that CLeader through31018FA0. GetGroupLeader1096/
31049B58 then reads its association through CLeader560/31009894. Ordinary membership
does not select an actor. Consequently an owned group with no invoked leader producer
has an actually absent leader, even if content marks a member `IsLeader`.

NPC39346960 scans the source-order member vector for the first flag bit2, represented by
`IsMoveLeader`. Only that arriving member calls AISystem816/31050DD8. This invokes
3104A488 to create or update CLeader association, then defines real formation content
and executes authored `CreateFormation`/command-set behavior. The managed owner refuses
this branch before changing the group record or its Lua outputs. A later row with the
same flag is not the first producer. Neither `IsLeader`, allocation order, proximity nor
the first current member substitutes for this native operation.

## Scope and validation

Thirteen focused tests cover constructor/map absence, parameter precedence and float
conversion, post-OnInit writes, valid authored override and tension, absent content,
source-order movement-leader refusal, same-ID copies, ID50000, invalid metadata,
generation retirement, retained empty groups, and actual spawner clearing without Lua
mutation or stale-owner writes. The isolated build and tests pass with
no new-file compiler warnings.

Fourteen independent original binary leaf controls execute copied3103D198 and3103D1C8
instructions. They verify negative clamping, changed-ID virtual dispatch before copying
type/member, same-ID suppression, full record copying and the raw negative-ID overwrite
case. The last case remains outside managed authored-group admission. The virtual setter
is observed through a controlled callback; the probe does not execute the complete AI
group manager or formation system. It loads no CryEngine, X2 or Lua modules.

External evidence:

- `D:/aaemu-runtime/research/npc-group-native-controls.json`
- `D:/aaemu-runtime/research/npc-group-leaf-controls.py`
- `D:/aaemu-runtime/research/npc-group-*.c`
- `D:/aaemu-runtime/research/npc-group-position-next.md`

Full CLeader construction/formation, AIGroup tactical member data, registered group
listeners, group-halved smart-object notifications and dynamic group changes remain
separate consumers. These APIs admit the current identity/membership/query subset;
global NPC gameplay readiness stays closed. Position-check admission composes these
queries with its actual region, relocation, replication and posture owners; group identity
alone does not admit the rest of that operation.

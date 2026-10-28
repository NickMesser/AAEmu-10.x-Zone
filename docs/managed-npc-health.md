# NPC actor health

Actor health is a live view of the associated Unit. The original Lua methods return signed64
values formatted as decimal **strings**. The managed binding preserves that representation,
including values above double's exact integer range. It owns no separate float health cache.

## Original binding and getters

The matching dedicated binary resolves these slots:

| Stage | GetHealth | GetMaxHealth |
|---|---|---|
| Literal registration in3930EE60 |648738: `GetHealth` ->39310CF0 |648761: `GetMaxHealth` ->39310B40 |
| Script binding thunk |39310CF0, slot240 |39310B40, slot248 |
| CScriptBind_Actor table39FD5B88 |3930D390 |3930D4B0 |
| IActor returned by ActorSystem | slot176 | slot184 |
| CActor primary table39FD2E28 |392D7400 |392D7460 |

The CActor constructor392E6BC0 writes39FD2E28 at the primary object address. ActorSystem
30501650 returns its stored actor pointer unchanged. The binding formats the returned int64
with `XlI64toA(..., 10)` and returns ScriptAnyValue string tag6. It returns string `"0"` when
its actor lookup fails. Managed callbacks instead revoke access to retired generations;
they cannot query a replacement actor through an old entity binding.

The matching 10.x ScriptSystem confirms the final Lua representation, rather than only
the binding's intermediate tag. Its type-name jump table at35509BEC maps tag5 to
`bigNumber`, tag6 to `String`, and tag7 to `Table`. `CFunctionHandler` vtable3557B848
slot80 dispatches35509ED0 ->3550D3B0. Tag6 selects3550D42A ->3554B290 ->3554B220,
which pushes Lua TValue string tag4. Tag5 instead selects3554B200 and pushes signed64
integer tag-2. Decimal health text is therefore not the big-number variant.

The original less-than355693D0 and less-or-equal35569520 operators normalize integer
tag-2 to number tag3, compare types, and enter355546A0 (`luaG_ordererror`) for string4
versus either numeric type before metamethod fallback. A direct `GetHealth()<=0` call
must fail; the managed VM does not add coercion to hide it. Original inherited
`BasicActor.Server.OnUpdate` calls `IsDead`, whose loose source contains that comparison.
Whether the actual dedicated NPC callback is scheduled or overridden is a separate
admission question. The full-scene managed probe records the callback failure explicitly.

Both getters prefer the client Unit pointer at CActor112, then the server Unit pointer104.
When neither exists, the native getter returns100. The admitted dedicated profile owns
only the server association.

Current health reads Unit32096, represented by `ZoneUnit.PreciseHealth`:

```csharp
preciseHealth / 100 + (preciseHealth % 100 != 0 ? 1L : 0L)
```

Original instructions use signed division. This preserves the source's unusual negative
raw-field behavior too; ordinary World point producers separately clamp admitted points.
Maximum health calls39258D10(unit, 6, 0), which delegates to the native attribute resolver.
The managed view uses the Unit's current complete `Stats.MaximumHealth`, and refuses a
missing or incomplete stat owner. It does not substitute a constructor fallback for an
associated Unit whose stat dependencies are missing.

## Association timing and lifecycle

CActor392E6BC0 initializes104/112 null. ActorUnitModel39277D00 creates the entity with
initialization enabled, so its actual `Server.OnInit` runs before model creation returns.
Unit3925B660 then publishes the model and invokes Unit virtual240. ServerUnit's primary
table39FE5598 resolves that slot to3935CA90, which obtains the actual model actor and invokes
CActor504/392E0190. That function stores the server Unit at104 and runs392DA1B0.

Accordingly the managed construction creates an unassociated `ZoneManagedNpcActorHealth`
view. The existing after-OnInit component hook invokes `AssociateServerUnit` with the
exact expected Unit before group installation, skill children and the character constructor.
The initialization test observes strings `"100"`/`"100"` during OnInit and strings `"4"`/`"17"`
after association in the character constructor. The actual model/construction owner supplies
that event; merely having a Unit ID does not establish association early.

The view follows `Registry.UpdatePoints`, progression/equipment/buff stat publication and
other actual Unit producers. There is no optimistic copy or independent `CopyFields` setter.
Expected Unit identity and actor-generation currency revoke old views after retirement.
Client association and an actor surviving after its Unit is removed remain outside the
admitted NPC profile.

## Correction and validation

The former interpretation of CActor stats160/184 as current0/maximum10 health was wrong.
Those fields belong to water state. The construction and script-effects owners, long-valued
service interface, decimal-string host binding and affected assertions have been corrected.

New controls exercise exact decimal strings above2^53, actual World point acknowledgments,
live maximum-stat changes, missing/partial-stat refusal, exact Unit identity and generation
retirement. The host test also verifies that protected health/numeric comparisons fail
while preserving every high64 decimal digit. Existing construction/effects/runtime controls
exercise both initialization phases.

Fifteen original binary controls execute copied392D7400 getter instructions and the original
392D7460 selection prefix. Its external stat-calculator tail is redirected to a declared
signed64 callback, verifying selection priority and attribute6/mode0 arguments. These controls
are not a complete native stat-calculator or Lua-binding execution. The probe loads no CryEngine,
X2 or Lua modules. Evidence and the reproducible bounded probe remain external:

- `D:/aaemu-runtime/research/npc-health-native-controls.json`
- `D:/aaemu-runtime/research/npc-health-leaf-controls.py`
- `D:/aaemu-runtime/research/npc-health-392D7400.c`
- `D:/aaemu-runtime/research/npc-health-392D7460.c`
- `D:/aaemu-runtime/research/npc-health-3925B660.c`
- `D:/aaemu-runtime/research/npc-health-3935CA90.c`

An additional23 executed ScriptSystem controls cover7 original EndFunction/PushAny cases
and16 original numeric/mixed-type operator cases. All8 mixed string/numeric cases entered
the original order-error branch. Only the string interner and the error-handler observation
were redirected to declared callbacks: no collector, string/string comparison, metamethod,
or full VM was executed. The error callback's sentinel return is not a native Lua result.
No CryEngine, ScriptSystem, X2, or Lua DLL was loaded. Reproduction and complete copied
outcomes remain at `D:/aaemu-runtime/research/npc-health-script-boundary-controls.py`
and `npc-health-script-boundary-controls.json`.

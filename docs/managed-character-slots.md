# Managed character slots and attachment visibility

`ZoneManagedCharacterSlots` owns an initial character slot0 for an exact actor generation. It reads the actual resolved `ActorModel.ModelFile`, its CDF when present, and the referenced CHR compiled-bone identities. Resource metadata is immutable and shared; mutable attachment visibility belongs to each lease.

```csharp
var slots = new ZoneManagedCharacterSlots(assets, IsCurrent,
    ZoneCharacterAttachmentPublicationProfile.BoneOnly);
var lease = slots.BindInitial(handle, actorModel);
var result = slots.HideAttachment(handle, slot, name, hide, hideShadow);
```

The caller resolves `actorModel` from the actual Unit-state model ID. The NPC template's cached model can differ. Bind before executing entity initialization, retain the lease, and dispose it during generation retirement. Asset callbacks cannot publish after retirement. Unbound/stale actor operations refuse; they do not report a successful absent lookup. Existing immutable observations remain copies after visibility changes.

The scope is an initial component inventory and the complete visibility effect of unbound bone sockets. It does not provide an animated skeleton, renderer, equipment binding system or character physicalization.

## Original source contracts

| Contract | Original10x evidence |
|---|---|
| Dedicated attachment type gate | The `zone` command306FC330 uses CSystem mode2, starts with flags `(4096 or4624) \| 2`, and skips the client OR0x8004. Framework+240/30702230 calls306F0470, which sets environment byte288 to `(flags&4)!=0`. Both31622080 and31621D70 reject non-bone types when this byte is0. BoneOnly is therefore the actual dedicated-zone branch. |
| CDF parsing |315A7E30 requires the first element to be `Model`, reads its `File`, then `AttachmentList/Attachment`;3161C7E0 parses each declaration. |
| Attachment names |3161C841→31503BA0 changes backslashes to slashes and ASCII uppercase to lowercase.31616EE0 compares lookup names with `_stricmp`; it does not rewrite query slashes. Duplicate creation refuses, retaining the first actually created name. |
| Bone prerequisite |31621D70 resolves the named skeleton joint and refuses a missing joint before allocating the attachment. CModelSkeleton31590D90 uses a lowercase CRC32 identity. |
| Initial visibility |3162261F stores authored Flags and calls HideAttachment with Flags&1. CAttachment315076E0 initializes its bound-object, renderer and instance fields to null. |
| Script visibility call |32030990 invokes attachment virtual168(hide), then192/208(hideShadow).31617790 writes primary visibility and both secondary values.316175E0 has no further effect when the bound-object and renderer fields are null. The owner implements all three copied visibility values for that case. |

CDF attachment types are Bone0, Face1, Skin2 and Camera4. Alignment values are None0, Bone1, Skin2 and Custom3. Binding kinds are File1, Entity3 and Effect5. Raw rotation uses XML w,x,y,z; the public quaternion uses System.Numerics x,y,z,w. Position, scale, bone names, binding names, material and all source attributes are retained. They are not converted into an evaluated animation or world transform.

The CHR reader supports CryTek container0744/0745 and compiled-bones chunkACDC0000/version0801. Cry3DEngine30252600's reflected `CryBoneDescData_Comp` verifies a48-byte header,324-byte records, controller ID at208, mass at212 and a48-byte name at216. The reader does not interpret the remaining physics/bind-pose data. It bounds file/chunk counts, ranges and names, rejects overlapping chunks, and refuses unknown formats. Strict malformed-XML/numeric/path validation is a managed admission policy.

Two real direct-CHR resources contain duplicate bone-name CRCs: `sarracenia_bogs.chr` and `smallgiant.chr`. Their identities remain inventory data. A request to select one of those ambiguous bone targets refuses because the native winner has not been recovered; no zone129 CDF socket depends on such a target.

## Original zone129 validation

The authored zone-server spawner file references100 spawner types. Resolving NPC/group members through the original database yields99 NPC templates and32 distinct model paths:14 CDF and18 direct CHR resources.

An independent managed probe opened the original523,291-entry `game_pak-` read-only and loaded all32 models. It retained119 authored attachment declarations, published43 bone sockets in the dedicated profile, updated42 unbound sockets and refused the one bound effect. All32 source assets matched their loose original bytes. All32 leases retired, and process-module checks found no CryEngine, X2, TBB, xlcommon or native Lua module before or after the probe.

Elf male/female each retain24 authored declarations and publish8 bone sockets in this profile. `BasicAI:OnInit` calls `HideAttachment(0,"Animated LAW",true,true)`; that name is absent from every one of the32 actual initial inventories. This supports the original absent-name branch with authored evidence.

The bound exception is `earthelemental_small.cdf`, whose bone attachment `body` uses `AttachmentType="effect"` and `X2_EFFECTS.mon_effects.earthelemental_small_sand_continue`. It remains a named particle/bound-object refusal. Present file, entity, effect and client-only character attachments cannot gain visibility success from copied flags alone. `AllAuthoredTypes` retains those names but refuses their unowned effects.

Focused `ManagedCharacterInventoryTests` cover authored transforms/identity, absent versus present lookup, visibility copies, atomic effect refusals, duplicate/missing bones, malformed resources, source-callback retirement and replacement generations.

Local research artifacts, not repository dependencies: `D:/aaemu-runtime/research/character-inventory-source-evidence.json`, `character-zone129-census.json` and `character-zone129-managed-validation.json`. Dynamic equipment/attachments, bound objects, skeletal pose evaluation, skinning and rendering remain separate work. An initial inventory must not be presented as current after an unowned dynamic character mutation.

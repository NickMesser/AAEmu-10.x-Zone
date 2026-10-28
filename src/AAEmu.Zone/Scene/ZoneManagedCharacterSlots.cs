using AAEmu.Zone.Actors;
using AAEmu.Zone.Content;
using static AAEmu.Zone.Content.ZoneCharacterSkeletonContent;

namespace AAEmu.Zone.Scene;

/// <summary>The original attachment factory gates non-bone types on gEnv byte0x120.</summary>
public enum ZoneCharacterAttachmentPublicationProfile { BoneOnly, AllAuthoredTypes }
public enum ZoneManagedAttachmentHideResult { AbsentSlot, AbsentAttachment, Updated }

/// <summary>
/// Copied visibility state. The script's hideShadow value is forwarded to both secondary
/// visibility methods192/208; it is not a render-node, particle or collision ownership claim.
/// </summary>
public sealed record ZoneManagedAttachmentVisibility(ZoneCharacterAttachmentDefinition Definition,
    bool Hidden, bool SecondaryVisibilityHidden, string? VisibilityRefusal);

/// <summary>
/// Scene-thread owner of initial character slot0 and authored attachment identities. Loading the
/// actual ModelFile and its skeleton precedes publication. Dynamic equipment/attachment bindings,
/// animation, renderer instances and bound particle/entity/static objects are not supplied here.
/// </summary>
public sealed class ZoneManagedCharacterSlots : IDisposable
{
    private readonly IZoneAssetSource _assets;
    private readonly Func<ZoneActorHandle, bool> _isCurrent;
    private readonly ZoneCharacterAttachmentPublicationProfile _profile;
    private readonly Dictionary<string, ZoneCharacterDefinitionContent> _definitions = new(StringComparer.Ordinal);
    private readonly Dictionary<ZoneActorHandle, ZoneManagedCharacterSlotOwner> _live = [];
    public bool IsDisposed { get; private set; }
    public int LiveCount => _live.Count;
    public int CachedDefinitionCount => _definitions.Count;

    public ZoneManagedCharacterSlots(IZoneAssetSource assets, Func<ZoneActorHandle, bool> isCurrent,
        ZoneCharacterAttachmentPublicationProfile profile = ZoneCharacterAttachmentPublicationProfile.BoneOnly)
    {
        _assets = assets ?? throw new ArgumentNullException(nameof(assets));
        _isCurrent = isCurrent ?? throw new ArgumentNullException(nameof(isCurrent));
        if (!Enum.IsDefined(profile)) throw new ArgumentOutOfRangeException(nameof(profile));
        _profile = profile;
    }

    public ZoneManagedCharacterSlotOwner BindInitial(ZoneActorHandle actor, ZoneActorModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        RequireCurrent(actor);
        if (_live.ContainsKey(actor)) throw new InvalidOperationException("The actor generation already owns its character slots.");
        var name = ZoneCharacterDefinitionContent.AssetName(model.Definition.ModelFile);
        if (!_definitions.TryGetValue(name, out var content))
        {
            content = ZoneCharacterDefinitionContent.Load(_assets, name);
            // Asset-source callbacks may retire the actor or the complete scene.
            RequireCurrent(actor);
            _definitions.TryAdd(name, content);
        }
        RequireCurrent(actor);
        if (_live.ContainsKey(actor)) throw new InvalidOperationException("Character slots were bound during a resource callback.");
        var owner = new ZoneManagedCharacterSlotOwner(this, actor, model.Id, content, _profile);
        _live.Add(actor, owner);
        return owner;
    }

    /// <summary>A stale/unadmitted actor is a refusal, never a successful absent-character lookup.</summary>
    public ZoneManagedAttachmentHideResult HideAttachment(ZoneActorHandle actor, int slot, string name, bool hide, bool hideShadow)
    {
        RequireCurrent(actor);
        if (!_live.TryGetValue(actor, out var owner))
            throw new NotSupportedException("entity.HideAttachment requires this generation's loaded character-slot inventory.");
        return owner.HideAttachment(slot, name, hide, hideShadow);
    }

    public ZoneManagedCharacterSlotOwner? Find(ZoneActorHandle actor)
    {
        RequireCurrent(actor);
        return _live.GetValueOrDefault(actor);
    }

    internal void RequireOwner(ZoneManagedCharacterSlotOwner owner)
    {
        ObjectDisposedException.ThrowIf(owner.IsDisposed, owner);
        RequireCurrent(owner.Actor);
        // The current predicate is external and may dispose or replace the owner.
        ObjectDisposedException.ThrowIf(owner.IsDisposed, owner);
        if (!_live.TryGetValue(owner.Actor, out var current) || !ReferenceEquals(current, owner))
            throw new InvalidOperationException("Character-slot activation no longer belongs to this actor generation.");
    }

    internal void Retire(ZoneManagedCharacterSlotOwner owner)
    {
        if (_live.TryGetValue(owner.Actor, out var current) && ReferenceEquals(current, owner)) _live.Remove(owner.Actor);
    }

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        foreach (var owner in _live.Values.ToArray()) owner.Dispose();
        _definitions.Clear();
    }

    private void RequireCurrent(ZoneActorHandle actor)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (actor.UnitObjectId == 0 || actor.Generation == 0 || !_isCurrent(actor))
            throw new InvalidOperationException("Character operations require the exact active actor generation.");
        ObjectDisposedException.ThrowIf(IsDisposed, this);
    }
}

/// <summary>One generation's mutable visibility state over shared immutable authored resources.</summary>
public sealed class ZoneManagedCharacterSlotOwner : IDisposable
{
    private readonly ZoneManagedCharacterSlots _scene;
    private readonly Dictionary<string, ZoneManagedAttachmentVisibility> _attachments = new(StringComparer.Ordinal);
    private readonly List<string> _diagnostics = [];
    public ZoneActorHandle Actor { get; }
    public uint ActorModelId { get; }
    public ZoneCharacterDefinitionContent Definition { get; }
    public bool IsDisposed { get; private set; }
    public IReadOnlyList<string> Diagnostics { get; }
    public int InitialSlot => 0;
    public int AttachmentCount => _attachments.Count;

    internal ZoneManagedCharacterSlotOwner(ZoneManagedCharacterSlots scene, ZoneActorHandle actor,
        uint actorModelId, ZoneCharacterDefinitionContent content, ZoneCharacterAttachmentPublicationProfile profile)
    {
        _scene = scene; Actor = actor; ActorModelId = actorModelId; Definition = content;
        Diagnostics = _diagnostics.AsReadOnly();
        foreach (var attachment in content.Attachments)
        {
            //31622080 and31621D70 return null for all non-bone types with gEnv+0x120=0.
            if (profile == ZoneCharacterAttachmentPublicationProfile.BoneOnly && attachment.Kind != ZoneCharacterAttachmentKind.Bone)
                continue;
            //31621D70 rejects an unresolved bone before allocating/publishing its attachment.
            if (attachment.Kind == ZoneCharacterAttachmentKind.Bone && attachment.BoneIndex is null)
            {
                _diagnostics.Add($"Attachment '{attachment.Name}' was not created: skeleton bone '{attachment.BoneName}' is absent.");
                continue;
            }
            if (_attachments.ContainsKey(attachment.Name))
            {
                _diagnostics.Add($"Attachment '{attachment.Name}' was not created: the first case-insensitive name already exists.");
                continue;
            }
            string? refusal = null;
            if (attachment.Binding.Length != 0)
                refusal = $"Attachment '{attachment.Name}' requires unowned {attachment.BindingKind} bound-object effects: '{attachment.Binding}'.";
            else if (attachment.BindingKind != ZoneCharacterAttachmentBindingKind.File)
                refusal = $"Attachment '{attachment.Name}' requires unowned {attachment.BindingKind} attachment construction.";
            else if (attachment.Kind != ZoneCharacterAttachmentKind.Bone)
                refusal = $"Attachment '{attachment.Name}' requires unowned {attachment.Kind} character construction.";
            var hidden = (attachment.Flags & 1) != 0; //3162261F applies Flags then HideAttachment(Flags&1).
            _attachments.Add(attachment.Name, new(attachment, hidden, hidden, refusal));
        }
    }

    public bool HasSlot(int slot) { _scene.RequireOwner(this); return slot == InitialSlot; }

    public ZoneManagedAttachmentVisibility? FindAttachment(int slot, string name)
    {
        _scene.RequireOwner(this);
        ArgumentNullException.ThrowIfNull(name);
        return slot == InitialSlot ? _attachments.GetValueOrDefault(LowerAscii(CString(name))) : null;
    }

    public IReadOnlyList<ZoneManagedAttachmentVisibility> CopyAttachments()
    {
        _scene.RequireOwner(this);
        return Array.AsReadOnly(_attachments.Values.ToArray());
    }

    internal ZoneManagedAttachmentHideResult HideAttachment(int slot, string name, bool hide, bool hideShadow)
    {
        _scene.RequireOwner(this);
        ArgumentNullException.ThrowIfNull(name);
        if (slot != InitialSlot) return ZoneManagedAttachmentHideResult.AbsentSlot;
        var key = LowerAscii(CString(name)); //31616EE0 uses _stricmp; query slashes are not rewritten.
        if (!_attachments.TryGetValue(key, out var attachment)) return ZoneManagedAttachmentHideResult.AbsentAttachment;
        if (attachment.VisibilityRefusal is { } refusal) throw new NotSupportedException(refusal);
        //31617790 writes all three visibility bytes; the script then overwrites192/208 with
        //hideShadow. Empty-binding bone sockets have no bound object or renderer instance.
        _attachments[key] = attachment with { Hidden = hide, SecondaryVisibilityHidden = hideShadow };
        return ZoneManagedAttachmentHideResult.Updated;
    }

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        _scene.Retire(this);
        _attachments.Clear();
    }
}

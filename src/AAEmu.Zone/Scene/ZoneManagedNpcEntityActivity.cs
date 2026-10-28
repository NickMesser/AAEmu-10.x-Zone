using AAEmu.Zone.Ai;
using AAEmu.Zone.Npcs;

namespace AAEmu.Zone.Scene;

/// <summary>The owned CEntity activity fields, independent of AI enablement and gameplay readiness.</summary>
public readonly record struct ZoneManagedNpcEntityActivityState(bool Active, bool Hidden, uint Flags,
    bool NpcInitializationCompleted);

public sealed partial class ZoneManagedNpcActor : IZoneNpcActivationEntity, IZoneNpcActivationAi
{
    //32013B30 clears both active bit1 and hidden bit0x10.39277D00 supplies spawn0x200;
    //CActor.Init392E16B0 then adds0x400. This profile has no later arbitrary entity-flag writer.
    public bool EntityActive { get; private set; }
    public bool EntityHidden { get; private set; }
    public uint EntityFlags { get; private set; } = 0x600;
    public bool EntityInitializationCompleted { get; private set; }

    public ZoneManagedNpcEntityActivityState CopyEntityActivity()
    {
        RequireEntityActivityProfile();
        return new(EntityActive, EntityHidden, EntityFlags, EntityInitializationCompleted);
    }

    /// <summary>
    ///3200E050 replaces active bit1 and clears temporary activation. The managed profile owns
    ///ordinary activation only; AI SetActive and provider scheduling do not write this field.
    /// </summary>
    public void SetEntityActive(bool active)
    { RequireEntityActivityProfile(); EntityActive = active; }

    /// <summary>32010460's copied headless hidden field, consumed by the actual script-update gate.</summary>
    public void SetEntityHidden(bool hidden)
    { RequireEntityActivityProfile(); EntityHidden = hidden; }

    /// <summary>
    ///Copy an actual CEntity flag change. Update-while-hidden0x200000 requires the unported
    ///link/bound-update producer and is refused before changing this ordinary NPC profile.
    ///This does not implement unrelated renderer or entity flag effects.
    /// </summary>
    public void SetEntityFlags(uint flags)
    {
        RequireEntityActivityProfile();
        if ((flags & 0x200000) != 0)
            throw new NotSupportedException("NPC entity flag0x200000 requires the actual linked/update-while-hidden entity profile.");
        EntityFlags = flags;
    }

    /// <summary>
    ///3201F7A0/3201FF50 admit active entities to the ordinary update list.32010BD0 also
    ///rejects hidden entities unless their actual0x200000 flag admits that separate profile.
    ///Inactive/hidden time does not enter CScriptProxy's accumulated update time.
    /// </summary>
    public bool IsEntityScriptUpdateEligible
    {
        get { RequireEntityActivityProfile(); return EntityActive && !EntityHidden; }
    }

    /// <summary>39346960: after character construction, deactivate the fresh NPC before model flag1.</summary>
    internal void CompleteEntityInitialization()
    {
        RequireEntityActivityProfile();
        if (EntityInitializationCompleted) throw new InvalidOperationException("NPC entity initialization has already completed.");
        Npc.Activation.SetActive(false, new InitialActivationServices(this));
        RequireEntityActivityProfile();
        EntityInitializationCompleted = true;
    }

    private void RequireEntityActivityProfile()
    {
        _scene.Require(this);
        if ((EntityFlags & 0x200000) != 0 || Npc.Unit.Snapshot.Attachment.Point != -1 || Npc.Unit.Snapshot.Bonding.Point != -1)
            throw new NotSupportedException("NPC entity activity requires the ordinary unparented profile; linked and update-while-hidden producers are not owned.");
    }

    void IZoneNpcActivationEntity.Hide(bool hidden) => SetEntityHidden(hidden);
    void IZoneNpcActivationEntity.Activate(bool active) => SetEntityActive(active);
    IZoneNpcActivationAi? IZoneNpcActivationEntity.GetAiObject()
    { RequireEntityActivityProfile(); return this; }
    ZoneActorActivationState? IZoneNpcActivationAi.GetActorActivation()
    { RequireEntityActivityProfile(); return Entry.Activation; }
    void IZoneNpcActivationAi.SendActivationEvent(int eventId)
    { RequireEntityActivityProfile(); _scene.ActorActivationEvent(Entry, eventId); }

    //Initial39346960 uses only SetActive's entity/AI leaves. Proximity belongs to the
    //world's concrete services with its actual spatial index and configured sight ranges.
    private sealed class InitialActivationServices(ZoneManagedNpcActor actor) : IZoneNpcActivationServices
    {
        public bool HasUnit => actor.HasActor;
        public float PlayerSightRange => throw new InvalidOperationException("Initial NPC deactivation does not sample proximity.");
        public float PlayerSightRangeBig => throw new InvalidOperationException("Initial NPC deactivation does not sample proximity.");
        public System.Numerics.Vector3 GetPosition() => throw new InvalidOperationException("Initial NPC deactivation does not sample position.");
        public IZoneNpcActivationEntity? GetEntity() => actor.HasActor ? actor : null;
        public bool IsDedicated => true;
        public bool GlobalAiUpdateAlways => actor.Scene.GlobalAiUpdateAlways;
    }
}

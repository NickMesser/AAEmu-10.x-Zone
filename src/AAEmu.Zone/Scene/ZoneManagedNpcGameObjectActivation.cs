namespace AAEmu.Zone.Scene;

/// <summary>Actual scene domain, not a visibility callback or an inference from an absent renderer.</summary>
public enum ZoneManagedNpcGameObjectActivationProfile
{
    Unowned,
    //306F0470: server flag2 and no single-player0x40 set gEnv289/290. The original
    //visibility-timeout cvar remains its registered0; custom visibility profiles are separate.
    MultiplayerWorldServer
}

public sealed partial class ZoneManagedNpcActor
{
    /// <summary>
    ///CAIProxy80/30514DA0 runs once before handler240 in30518020. Its GameObject240
    ///consumer returns actual entity activity, which gates the proxy's subsequent update.
    /// </summary>
    public bool UpdateProxyActivationPolicy()
    {
        _scene.Require(this);
        Entry.UpdateEntityPolicy();
        _scene.Require(this);
        return EntityActive;
    }

    internal void ApplyGameObjectAiActivationPolicy(int policy)
    {
        _scene.Require(this);
        if (policy is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(policy));
        //305E0C80 reevaluates only on an actual policy change. An unchanged request
        //still returns current activity; it does not implicitly reactivate an entity.
        if (EntityUpdatePolicy == policy) return;
        if (EntityHidden)
            throw new NotSupportedException("Hidden NPC GameObject activation requires the actual extension update-slot inventory and forced-update consumers.");
        if (policy == 2 && _scene.GameObjectActivationProfile != ZoneManagedNpcGameObjectActivationProfile.MultiplayerWorldServer)
            throw new NotSupportedException("Conditional NPC GameObject activation requires the actual multiplayer World-server/default-visibility profile.");
        //305DEEC0: nonhidden policy1 is unconditional. Policy2 on the admitted network
        //server uses305DF1D0's gEnv289&&290 visible branch (visibility-timeout default0).
        //That true AI result bypasses extension-slot/forced-update alternatives entirely.
        EntityUpdatePolicy = policy;
        SetEntityActive(true); //305E0AD0 ->305DFB00 -> IEntity336/3200E050.
        //Actual CActor.Init392E16B0 set GameObject prephysics mode2 before AI registration.
        //For this nonhidden/AI-updating branch it remains enabled; no new physical step.
    }
}

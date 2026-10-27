using AAEmu.Zone.Native.Engine;

namespace AAEmu.Zone.Native.FlowNodes;

/// <summary>
/// Constructs all twenty real X2 node families in their original CRT registration order.
/// Providers belong to the supplied engine and remain caller-owned through node retirement;
/// this builder retains them through factory callbacks but neither creates nor disposes them.
/// </summary>
internal static class ZoneNativeFlowNodeFactories
{
    /// <summary>
    /// Returns a complete, unadopted registry for the engine's factory-builder callback.
    /// Construction validates dependencies without creating nodes or invoking native services.
    /// Native configuration values and engine-backed PostFX/FOV calls bind during callbacks.
    /// On failure, only successfully constructed, unpublished factories are released.
    /// </summary>
    internal static ZoneNativeFlowNodeRegistration Create(
        ZoneNativeEngine engine,
        IZoneNativeFlowGameModeServices gameMode,
        IZoneNativeFlowActorSensorServices actorSensor,
        IZoneNativeFlowScreenFaderServices crysisFader,
        IZoneNativeFlowUiScreenFader uiFader,
        IZoneNativeFlowTornadoServices tornado,
        IZoneNativeFlowPlayerServices player)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(gameMode);
        ArgumentNullException.ThrowIfNull(actorSensor);
        ArgumentNullException.ThrowIfNull(crysisFader);
        ArgumentNullException.ThrowIfNull(uiFader);
        ArgumentNullException.ThrowIfNull(tornado);
        ArgumentNullException.ThrowIfNull(player);

        var runtime = new ZoneNativeFlowNodeRuntime(engine);
        runtime.ValidateForRegistration();
        // Reject missing provider lifetimes before allocating any factory. Each dependency
        // is revalidated by its factory and again before native registry publication.
        gameMode.ValidateForRegistration();
        actorSensor.ValidateForRegistration();
        crysisFader.ValidateForRegistration();
        uiFader.ValidateForRegistration();
        tornado.ValidateForRegistration();
        player.ValidateForRegistration();

        var factories = new IZoneNativeFlowNodeFactoryOwner[20];
        var count = 0;
        try
        {
            // Node allocation sizes below are separate from the 0x18 ordinary / 0x20
            // singleton factory storage. Original entries run from 39F6AD50 through
            // 39F72DB0; CG2AutoRegFlowNodeSingleton alone owns the retained node cache.
            Add("Crysis:TornadoWander", 0x40, false,
                _ => ZoneNativeFlowTornadoNodes.CreateTornadoWander(tornado), tornado.ValidateForRegistration);
            Add("Game:ActorSensor", 0x50, false,
                _ => ZoneNativeFlowSensorNodes.CreateActorSensor(actorSensor), actorSensor.ValidateForRegistration);
            Add("Game:DifficultyLevel", 0x10, true,
                _ => ZoneNativeFlowGameModeNodes.CreateDifficulty(gameMode), gameMode.ValidateForRegistration);
            Add("Camera:OverrideFOV", 0x18, true,
                _ => ZoneNativeFlowNodes.CreateOverrideFov(engine), runtime.ValidateForRegistration);
            Add("CrysisFX:ScreenFader", 0x28, false,
                _ => ZoneNativeFlowScreenFaderNodes.CreateCrysis(crysisFader), crysisFader.ValidateForRegistration);
            Add("Crysis:HitInfo", 0x40, false,
                _ => ZoneNativeFlowSensorNodes.CreateHitInfo(), runtime.ValidateForRegistration);
            Add("Crysis:ExplosionInfo", 0x40, false,
                _ => ZoneNativeFlowSensorNodes.CreateExplosionInfo(), runtime.ValidateForRegistration);
            Add("Crysis:FreezeEntity", 0x10, true,
                _ => ZoneNativeFlowPlayerNodes.CreateFreezeEntity(), runtime.ValidateForRegistration);
            Add("Game:PlayerStaging", 0x10, true,
                _ => ZoneNativeFlowPlayerNodes.CreatePlayerStaging(player), player.ValidateForRegistration);
            Add("Game:PlayerLink", 0x10, true,
                _ => ZoneNativeFlowPlayerNodes.CreatePlayerLink(player), player.ValidateForRegistration);
            AddPostEffect("CrysisFX:PostFXGlobal");
            AddPostEffect("CrysisFX:PostFXScreenFrost");
            AddPostEffect("CrysisFX:PostFXKuwaharaFilter");
            AddPostEffect("CrysisFX:PostFXWaterDroplets");
            AddPostEffect("CrysisFX:PostFXBloodSplats");
            AddPostEffect("CrysisFX:PostFXGlow");
            AddPostEffect("CrysisFX:PostFXGlittering");
            Add("Multiplayer:MP", 0x60, false,
                activation => ZoneNativeFlowGameModeNodes.CreateMultiplayer(gameMode, activation),
                gameMode.ValidateForRegistration);
            Add("Multiplayer:GameType", 0x40, false,
                _ => ZoneNativeFlowGameModeNodes.CreateGameType(), runtime.ValidateForRegistration);
            Add("X2UI:ScreenFader", 0x18, false,
                _ => ZoneNativeFlowScreenFaderNodes.CreateX2Ui(uiFader), uiFader.ValidateForRegistration);

            // This constructor checks all names/order and interfaces again. Ownership passes
            // only on success; no fallible builder work follows the transfer.
            return new ZoneNativeFlowNodeRegistration(factories);
        }
        catch (Exception creationError)
        {
            List<Exception>? failures = null;
            List<IZoneNativeFlowNodeFactoryOwner>? retained = null;
            for (var index = count - 1; index >= 0; --index)
            {
                try { factories[index].ReleaseUnpublished(); }
                catch (Exception cleanupError)
                {
                    (failures ??= [creationError]).Add(cleanupError);
                    (retained ??= []).Add(factories[index]);
                }
            }
            if (failures is null) throw;
            // Keep the original failure first and retain any owner whose cleanup rejected.
            // Successfully released factories are never handed back or released twice.
            var error = new AggregateException(
                "Flow-node factory construction failed, and some unpublished factories could not be released.", failures);
            error.Data["UnreleasedFlowNodeFactories"] = retained!.AsReadOnly();
            throw error;
        }

        void Add(string name, int size, bool singleton,
            Func<ZoneNativeFlowActivation?, IZoneNativeFlowNodeBehavior> create, Action validate)
        {
            // The fixed array is allocated before native storage. Assignment cannot grow a
            // collection or lose a completed constructor to a later allocation failure.
            factories[count] = new ZoneNativeFlowNodeFactory(name, size, singleton, runtime, create, validate);
            ++count;
        }

        void AddPostEffect(string name) => Add(name, 0x10, true,
            _ => ZoneNativeFlowNodes.CreatePostEffect(engine, name), runtime.ValidateForRegistration);
    }
}

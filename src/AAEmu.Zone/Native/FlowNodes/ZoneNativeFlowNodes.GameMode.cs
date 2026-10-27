namespace AAEmu.Zone.Native.FlowNodes;

/// <summary>
/// Live services read by the three X2 game-mode nodes. The owner supplies the current game
/// state; these operations must not replace unavailable state with dedicated-server constants.
/// </summary>
internal interface IZoneNativeFlowGameModeServices
{
    /// <summary>Read-only admission of this owner's real dependencies and current lifetime.</summary>
    void ValidateForRegistration();

    /// <summary>The current signed difficulty value read by 39316C90 at game-CVar storage +104.</summary>
    int ReadDifficultyLevel();

    /// <summary>
    /// Preserve the optional game check followed by game+410 and framework+148 in 3931C740.
    /// An absent game or absent returned client actor is false. A present game with an invalid
    /// framework is an ownership failure, rather than an absent-client result.
    /// </summary>
    bool HasClientActor();

    /// <summary>The actual EntitySystem+70 lookup, using the full unsigned authored entity ID.</summary>
    bool EntityExists(uint entityId);

    /// <summary>The live nonzero debug flag read at the game's CVar owner +284.</summary>
    bool IsFlowGraphDebugLoggingEnabled { get; }

    void LogFlowGraph(string message);
}

/// <summary>
/// Behavior for Game:DifficultyLevel, Multiplayer:MP and Multiplayer:GameType. The common
/// adapter owns native interface storage, callback containment, ports and reference counts.
/// Difficulty uses its factory's singleton lifetime; MP and GameType create fresh clone state.
/// </summary>
internal static class ZoneNativeFlowGameModeNodes
{
    private const uint ConfigurationFlagsAndMask = 0xfffff08f;
    private const uint ConfigurationFlagsOrMask = 0x80;

    internal static IZoneNativeFlowNodeBehavior CreateDifficulty(IZoneNativeFlowGameModeServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return new Difficulty(services);
    }

    internal static IZoneNativeFlowNodeBehavior CreateMultiplayer(IZoneNativeFlowGameModeServices services,
        ZoneNativeFlowActivation? creationActivation)
    {
        ArgumentNullException.ThrowIfNull(services);
        return new Multiplayer(services, creationActivation);
    }

    internal static IZoneNativeFlowNodeBehavior CreateGameType() => new GameType();

    private abstract class Behavior : IZoneNativeFlowNodeBehavior
    {
        public abstract ZoneNativeFlowNodeConfiguration Configuration { get; }

        public abstract void ProcessEvent(int eventId, ZoneNativeFlowActivation activation);

        // All three actual tables use 39710D00 (true) and 392A49F0 (empty), respectively.
        public bool SerializeXml(ZoneNativeFlowActivation activation, nint xmlNodeReference, bool loading) => true;

        public void Serialize(ZoneNativeFlowActivation activation, nint serializer) { }

        public virtual void Destroy() { }
    }

    private sealed class Difficulty(IZoneNativeFlowGameModeServices services) : Behavior
    {
        private static readonly ZoneNativeFlowNodeConfiguration NodeConfiguration = new(
            Array.AsReadOnly(new ZoneNativeFlowInputPort[]
            {
                new("Trigger", ZoneNativeFlowValue.Void(), "Trigger to get difficulty level.")
            }),
            Array.AsReadOnly(new ZoneNativeFlowOutputPort[]
            {
                new("Easy", 0, "Easy"),
                new("Normal", 0, "Normal"),
                new("Hard", 0, "Hard"),
                new("Delta", 0, "Delta")
            }),
            "Get difficulty level.", ConfigurationFlagsAndMask, ConfigurationFlagsOrMask);

        public override ZoneNativeFlowNodeConfiguration Configuration => NodeConfiguration;

        public override void ProcessEvent(int eventId, ZoneNativeFlowActivation activation)
        {
            // 39316C90: unsigned(value - 1) < 3. The fourth declared output is not activated
            // for value 4 in this image. Read the service only after the native trigger gate.
            if (eventId != 1 || !activation.IsPortActive(0)) return;
            var output = unchecked((uint)(services.ReadDifficultyLevel() - 1));
            if (output < 3)
                activation.ActivateBooleanOutput((byte)output, true);
        }
    }

    private sealed class Multiplayer : Behavior
    {
        private static readonly ZoneNativeFlowNodeConfiguration NodeConfiguration = new(
            Array.AsReadOnly(new ZoneNativeFlowInputPort[]
            {
                new("GameEndTime", ZoneNativeFlowValue.Float(0f),
                    "Number of seconds remaining at which to trigger the EndGameNear output")
            }),
            Array.AsReadOnly(new ZoneNativeFlowOutputPort[]
            {
                new("EnteredGame", 0, "Triggered when local player enters the game"),
                new("EndGameNear", 0, "Triggered when game-ending condition is near"),
                new("EndGameInvalid", 0, "Triggered when game-ending condition is invalidated"),
                new("GameWon", 0, "Triggered when local player's team wins the game"),
                new("GameLost", 0, "Triggered when local player's team loses the game"),
                new("GameTied", 0, "Triggered when neither team wins the game")
            }),
            "MP MPNode", ConfigurationFlagsAndMask, ConfigurationFlagsOrMask);

        private readonly IZoneNativeFlowGameModeServices _services;

        // 3931C580/393C3680 construct an empty owned list. No insertion operation is exposed
        // by this node's native event/clone/serialization callbacks; do not invent a producer.
        // Keep the complete 3931C740 traversal and retirement behavior for that owned state.
        private readonly List<uint> _watchedEntityIds = [];
        private ZoneNativeFlowActivation _activation;
        private bool _endGameNear;

        internal Multiplayer(IZoneNativeFlowGameModeServices services, ZoneNativeFlowActivation? creationActivation)
        {
            _services = services;
            // Native construction registers updates, but event 3 captures the activation.
            // Clone reaches this same constructor with fresh list/transition state.
            if (creationActivation is { } activation && activation.Graph != 0)
                activation.SetRegularlyUpdated(true);
        }

        public override ZoneNativeFlowNodeConfiguration Configuration => NodeConfiguration;

        public override void ProcessEvent(int eventId, ZoneNativeFlowActivation activation)
        {
            if (eventId == 3)
            {
                _activation = activation;
                return;
            }
            if (eventId != 0 || !_services.HasClientActor()) return;

            if (_watchedEntityIds.Count == 0)
            {
                if (_endGameNear)
                {
                    if (_services.IsFlowGraphDebugLoggingEnabled)
                        _services.LogFlowGraph("MP flowgraph: EndGameInvalid");
                    _activation.ActivateBooleanOutput(2, true);
                    _endGameNear = false;
                }
                return;
            }

            for (var index = 0; index < _watchedEntityIds.Count;)
            {
                if (!_services.EntityExists(_watchedEntityIds[index]))
                {
                    _watchedEntityIds.RemoveAt(index);
                    continue;
                }
                if (!_endGameNear)
                {
                    if (_services.IsFlowGraphDebugLoggingEnabled)
                        _services.LogFlowGraph("--MP flowgraph: EndGameNear");
                    _activation.ActivateBooleanOutput(1, true);
                    _endGameNear = true;
                }
                index++;
            }
            // Native invalidation happens on a later update if this pass erased the last ID.
        }

        public override void Destroy()
        {
            // 3931C6D0 retires the list; it makes no graph SetRegularlyUpdated(false) call.
            _watchedEntityIds.Clear();
            _activation = default;
        }
    }

    private sealed class GameType : Behavior
    {
        private static readonly ZoneNativeFlowNodeConfiguration NodeConfiguration = new(
            Array.AsReadOnly(new ZoneNativeFlowInputPort[]
            {
                new("GetGameType", ZoneNativeFlowValue.Void(), "Activate this to retrigger relevent outputs")
            }),
            Array.AsReadOnly(new ZoneNativeFlowOutputPort[]
            {
                new("InstantAction", 0, "Triggered on level load if InstantAction game"),
                new("TeamAction", 0, "Triggered on level load if TeamAction game"),
                new("PowerStruggle", 0, "Triggered on level load if PowerStruggle game"),
                new("Mmo", 0, "Triggered on level load if MMORPG game"),
                new("GameRulesName", 6, "Outputs the current game rules name")
            }),
            "MP GameTypeNode", ConfigurationFlagsAndMask, ConfigurationFlagsOrMask);

        private ZoneNativeFlowActivation _activation;

        public override ZoneNativeFlowNodeConfiguration Configuration => NodeConfiguration;

        public override void ProcessEvent(int eventId, ZoneNativeFlowActivation activation)
        {
            // 393191E0 only copies context on event 3. It returns without output on event 1
            // and every other event; port descriptions do not establish executable behavior.
            if (eventId == 3) _activation = activation;
        }

        public override void Destroy() => _activation = default;
    }
}

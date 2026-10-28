using AAEmu.Zone.Protocol;
using AAEmu.Zone.WorldState;

namespace AAEmu.Zone.WorldLink;

// WZ-01 / ZW-03: per-packet containment and the World-disconnect record. Kept out of ZoneSession.cs
// so the cited receive-loop line numbers stay stable.
public sealed partial class ZoneSession
{
    private readonly Dictionary<ushort, int> _handlerFaults = [];
    private readonly Dictionary<ushort, int> _levelGateDrops = [];

    /// <summary>
    /// Per-opcode count of packets whose handling failed (malformed body, unsupported or unconfigured
    /// service, handler exception). Native handlers always return 1 (3936F740 registrations), so these
    /// are logged once per opcode, counted, and dropped; the session is never ended by them.
    /// </summary>
    public IReadOnlyDictionary<ushort, int> HandlerFaults => _handlerFaults;

    /// <summary>Per-opcode count of packets dropped because the level was not loaded (native 39362D40 "OnNpcState: level is not loaded yet!").</summary>
    public IReadOnlyDictionary<ushort, int> LevelGateDrops => _levelGateDrops;

    /// <summary>Set once the receive loop ended because World closed the connection (not cancellation or scene retirement).</summary>
    public bool WorldDisconnected { get; private set; }

    /// <summary>
    /// A coordinated scene is loaded by construction (AdoptScene requires a completed load); a snapshot
    /// session counts as loaded once it owns an NPC simulation or schedule controller.
    /// </summary>
    public bool IsLevelLoaded => _scene is not null || Registry.Npcs is not null || GameSchedules is not null;

    /// <summary>The scene's TowerDef manager; null for a session without a coordinated scene (and the test seam override).</summary>
    public ZoneTowerDefController? TowerDefs => TowerDefsOverride ?? _scene?.TowerDefs;

    internal ZoneTowerDefController? TowerDefsOverride { get; set; }

    /// <summary>Test seam: a session over a caller stream with no World join, scene or content.</summary>
    internal static ZoneSession CreateDetached(Stream connection) => new(connection);

    private ZoneSession(Stream connection)
    {
        _connection = connection;
        Startup = new();
        Registry = new();
        Factions = new(Registry, [], [], null);
    }

    /// <summary>Feeds one frame through the same guarded dispatch the receive loop uses.</summary>
    internal void ReceiveForTest(ZoneFrame frame) => Receive(frame);

    // NpcState (39362D40), spawner events and game schedules are ignored natively before the level loads.
    private bool PassesLevelGate(ZoneFrame frame)
    {
        if (frame.Opcode is not (WzOpcodes.NpcState or WzOpcodes.NpcSpawnFailed or WzOpcodes.NpcSpawnerEvent
            or WzOpcodes.GameScheduleStart or WzOpcodes.GameScheduleContinue or WzOpcodes.GameScheduleEnd)
            || IsLevelLoaded) return true;
        _levelGateDrops.TryGetValue(frame.Opcode, out var seen);
        _levelGateDrops[frame.Opcode] = seen + 1;
        if (seen == 0)
            Console.WriteLine($"WZ opcode 0x{frame.Opcode:X4}: level is not loaded yet; dropped (native 39362D40 parity).");
        return false;
    }

    private void RecordHandlerFault(ushort opcode, Exception error)
    {
        _handlerFaults.TryGetValue(opcode, out var seen);
        _handlerFaults[opcode] = seen + 1;
        if (seen == 0)
            Console.WriteLine($"WZ opcode 0x{opcode:X4} handling failed ({error.GetType().Name}: {error.Message}); " +
                "dropped and counted, session continues (native handlers always return 1).");
    }

    private void MarkDisconnect(Exception? receiveError, CancellationToken cancellation)
    {
        if (receiveError is not null || cancellation.IsCancellationRequested || _scene?.IsRetired == true) return;
        WorldDisconnected = true;
        Console.WriteLine(ZoneWorldLifecycle.DisconnectMessage(ZoneWorldLifecycle.DisconnectEndOfStream));
    }
}

/// <summary>Native World-link lifecycle policy: disconnect (3936D7E0) and join refusal (393648D0).</summary>
public static class ZoneWorldLifecycle
{
    /// <summary>Reason value this client reports for an orderly TCP close by World (native reasons are socket-layer codes).</summary>
    public const int DisconnectEndOfStream = 0;

    /// <summary>Process exit code for a mid-session World disconnect, distinct from connect failure (1) and signal shutdown (130).</summary>
    public const int WorldDisconnectedExitCode = 3;

    /// <summary>This replacement runs the dedicated server type (ZoneSceneLoadingServices.ServerType).</summary>
    public const int DedicatedServerType = 2;

    /// <summary>3936D7E0: "zone: client disconnected (reason=%d)".</summary>
    public static string DisconnectMessage(int reason) => $"zone: client disconnected (reason={reason})";

    /// <summary>3936D7E0: server types 2 and 3 run console "quit"; type 1 stays alive.</summary>
    public static bool QuitsOnDisconnect(int serverType) => serverType is 2 or 3;

    /// <summary>
    /// 393648D0 table off_3A51AB48, index clamped to 3. The native strings are not recovered in this
    /// repo; names are positional until read from the binary.
    /// </summary>
    public static string JoinRefusalName(int reason) => $"off_3A51AB48[{Math.Clamp(reason, 0, 3)}]";

    /// <summary>393648D0: "zone: world server refused joining(%s)".</summary>
    public static string JoinRefusalMessage(int reason) => $"zone: world server refused joining({JoinRefusalName(reason)}) reason={reason}";
}

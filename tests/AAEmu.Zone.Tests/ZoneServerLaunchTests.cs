using AAEmu.Zone.Hosting;

namespace AAEmu.Zone.Tests;

public sealed class ZoneServerLaunchTests
{
    /// <summary>The argument list AAEmu-10.x World's ZoneHostCommandLine.Build produces with default ZoneHost settings.</summary>
    private static readonly string[] WorldLaunch =
    [
        "-dedicated",
        "+world_ip", "127.0.0.1", "+world_port", "1240",
        "+world_serveraddr", "127.0.0.1", "+world_serverport", "1240",
        "+zone", "w_gweonid_forest_1", "+sv_map", "w_gweonid_forest_1",
        "+instance", "7", "+sv_port", "65001",
        "+db_location", "game/db/game_decrypted.sqlite3",
        "+e_render", "0", "+r_Driver", "Null",
        "+npc_move_skip_standing", "0", "+npc_move_skip_disabledAI", "0", "+npc_movement_skip", "0",
        "+ai_systemupdate", "1",
    ];

    private static Func<string, string?> Environment(params (string Name, string Value)[] values) =>
        name => values.FirstOrDefault(value => value.Name == name).Value;

    [Test]
    public async Task WorldSupervisorLaunchIsAcceptedAndMapped()
    {
        var launch = ZoneServerLaunch.Parse(WorldLaunch, Environment(
            (ZoneServerLaunch.ConfigEnvironment, "C:/zone/zone.json"),
            (ZoneServerLaunch.SaveDirectoryEnvironment, "C:/logs/zone-7"),
            (ZoneServerLaunch.LogNameEnvironment, "zone-7.log"),
            (ZoneServerLaunch.NativeDllEnvironment, "C:/AA/Bin64/x2game-dev_dedicate.dll")));
        await Assert.That(launch.LaunchedByWorld).IsTrue();
        await Assert.That(launch.ConfigPath).IsEqualTo("C:/zone/zone.json");
        await Assert.That(launch.WorldHost).IsEqualTo("127.0.0.1");
        await Assert.That(launch.WorldPort).IsEqualTo((ushort?)1240);
        await Assert.That(launch.ZoneName).IsEqualTo("w_gweonid_forest_1");
        await Assert.That(launch.InstanceId).IsEqualTo((uint?)7);
        await Assert.That(launch.ServerPort).IsEqualTo((ushort?)65001);
        await Assert.That(launch.ContentDatabasePath).IsEqualTo(Path.GetFullPath("game/db/game_decrypted.sqlite3"));
        await Assert.That(launch.SaveDirectory).IsEqualTo("C:/logs/zone-7");
        await Assert.That(launch.LogName).IsEqualTo("zone-7.log");
        await Assert.That(launch.NativeDll).IsEqualTo("C:/AA/Bin64/x2game-dev_dedicate.dll");
        await Assert.That(launch.IgnoredCVars.Order(StringComparer.Ordinal).SequenceEqual(new[]
            { "ai_systemupdate", "e_render", "npc_move_skip_disabledAI", "npc_move_skip_standing", "npc_movement_skip", "r_Driver" })).IsTrue();
    }

    [Test]
    public async Task RepeatedCVarKeepsTheLastValueAndExplicitConfigWins()
    {
        // World appends ExtraArguments after its own CVars, so a repeated CVar must override.
        var launch = ZoneServerLaunch.Parse([.. WorldLaunch, "+world_port", "1250", "+zone_config", "C:/cvar.json", "--config", "C:/explicit.json"],
            Environment((ZoneServerLaunch.ConfigEnvironment, "C:/env.json")));
        await Assert.That(launch.WorldPort).IsEqualTo((ushort?)1250);
        await Assert.That(launch.ConfigPath).IsEqualTo("C:/explicit.json");

        var fromCVar = ZoneServerLaunch.Parse([.. WorldLaunch, "+zone_config", "C:/cvar.json"],
            Environment((ZoneServerLaunch.ConfigEnvironment, "C:/env.json")));
        await Assert.That(fromCVar.ConfigPath).IsEqualTo("C:/cvar.json");
    }

    [Test]
    public async Task ManualLaunchAndMalformedArgumentsAreReportedByName()
    {
        var manual = ZoneServerLaunch.Parse(["--config", "zone.json", "--spawner-file", "npc_spawners.g"], Environment());
        await Assert.That(manual.LaunchedByWorld).IsFalse();
        await Assert.That(manual.SpawnerFile).IsEqualTo("npc_spawners.g");
        await Assert.That(manual.InstanceId).IsNull();

        await Assert.That(() => ZoneServerLaunch.Parse(["--host"], Environment())).Throws<ArgumentException>();
        await Assert.That(() => ZoneServerLaunch.Parse(["+zone"], Environment())).Throws<ArgumentException>();
        await Assert.That(() => ZoneServerLaunch.Parse(["--config", "a", "--config", "b"], Environment())).Throws<ArgumentException>();
        await Assert.That(() => ZoneServerLaunch.Parse(["+sv_port", "0"], Environment()).ServerPort).Throws<ArgumentException>();
        await Assert.That(() => ZoneServerLaunch.Parse(["+instance", "-1"], Environment()).InstanceId).Throws<ArgumentException>();
    }

    [Test]
    public async Task SpawnerAssetMatchesWorldResolverLayout()
    {
        await Assert.That(ZoneServerStartup.NpcSpawnersAsset("main_world", 129))
            .IsEqualTo("game/worlds/main_world/level_design/zone/129/zone_server/npc_spawners.g");
    }
}

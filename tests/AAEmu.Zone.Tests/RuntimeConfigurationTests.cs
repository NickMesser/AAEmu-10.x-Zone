using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

using AAEmu.Zone.Content;
using AAEmu.Zone.Hosting;

namespace AAEmu.Zone.Tests;

public sealed class RuntimeConfigurationTests
{
    [Test]
    public async Task ConfigurationAnchorsPathsAndKeepsOrderedReadOnlySourcesWithoutStartingRuntimeServices()
    {
        using var fixture = new Fixture();
        var before = SHA256.HashData(File.ReadAllBytes(fixture.DatabasePath));
        var configuration = ZoneRuntimeConfiguration.LoadFile(fixture.ConfigurationPath);
        await Assert.That(configuration.ContentDatabasePath).IsEqualTo(fixture.DatabasePath);
        await Assert.That(configuration.EngineDirectory).IsEqualTo(Path.Combine(fixture.Root, "engine"));
        await Assert.That(configuration.LogDirectory).IsEqualTo(Path.Combine(fixture.Root, "logs"));
        await Assert.That(configuration.AssetSources.Select(source => source.Path).SequenceEqual(new[]
            { Path.Combine(fixture.Root, "primary"), Path.Combine(fixture.Root, "fallback") })).IsTrue();
        await Assert.That(configuration.WorldMapAsset).IsEqualTo(Fixture.WorldAsset);
        await Assert.That(configuration.WorldPort).IsEqualTo((ushort)12345);
        await Assert.That(configuration.FramePeriod).IsEqualTo((TimeSpan?)TimeSpan.FromMilliseconds(20));
        await Assert.That(configuration.TerrainTexturePoolSize).IsEqualTo((int?)3);
        // Reading configuration must not create its configured runtime directories.
        await Assert.That(Directory.Exists(configuration.EngineDirectory)).IsFalse();
        await Assert.That(Directory.Exists(configuration.LogDirectory)).IsFalse();

        using (var database = new ZoneContentDatabase(configuration.ContentDatabasePath))
        using (var assets = configuration.OpenAssets())
        {
            var zone = configuration.ResolveZone(database);
            await Assert.That(zone.Name).IsEqualTo("test_zone");
            await Assert.That(zone.Key).IsEqualTo(456);
            var layout = assets.LoadLayout(zone);
            await Assert.That(layout.World.Header.Name).IsEqualTo("Primary map");
            await Assert.That(layout.World.RequestedWorldName).IsEqualTo("test_world");
            await Assert.That(layout.Origin).IsEqualTo(new ZoneMapCoordinate(0, 0));
            await Assert.That(ReadAsset(assets, "game/priority.txt")).IsEqualTo("primary");
            await Assert.That(ReadAsset(assets, "game/fallback-only.txt")).IsEqualTo("fallback");
            await Assert.That(ReadAsset(assets, "game/empty.txt")).IsEqualTo("");

            using var borrowed = assets.OpenRead("game/priority.txt")!;
            await Assert.That(borrowed.CanWrite).IsFalse();
            assets.Dispose();
            await Assert.That(() => assets.OpenRead("game/priority.txt")).Throws<ObjectDisposedException>();
            using var reader = new StreamReader(borrowed);
            await Assert.That(reader.ReadToEnd()).IsEqualTo("primary");
        }
        await Assert.That(SHA256.HashData(File.ReadAllBytes(fixture.DatabasePath)).SequenceEqual(before)).IsTrue();
        await Assert.That(Directory.GetFiles(Path.GetDirectoryName(fixture.DatabasePath)!).Length).IsEqualTo(1);
        await Assert.That(Directory.Exists(configuration.EngineDirectory)).IsFalse();
        await Assert.That(Directory.Exists(configuration.LogDirectory)).IsFalse();
    }

    [Test]
    public async Task DuplicateConfigurationAndAmbiguousOrMismatchedContentCannotSelectAnotherWorldSource()
    {
        using var fixture = new Fixture();
        var json = fixture.ConfigurationJson();
        await Assert.That(() => ZoneRuntimeConfiguration.Read(json[..^1] + ",\"zoneKey\":456}", fixture.ConfigurationDirectory))
            .Throws<InvalidDataException>();
        await Assert.That(() => ZoneRuntimeConfiguration.Read(
            fixture.ConfigurationJson(contentDatabasePath: "../content/compact.sqlite"), fixture.ConfigurationDirectory))
            .Throws<InvalidDataException>();
        using var database = new ZoneContentDatabase(fixture.DatabasePath);
        var wrongKey = ZoneRuntimeConfiguration.Read(fixture.ConfigurationJson(zoneKey: 457), fixture.ConfigurationDirectory);
        await Assert.That(() => wrongKey.ResolveZone(database)).Throws<InvalidDataException>();
        var ambiguous = ZoneRuntimeConfiguration.Read(fixture.ConfigurationJson(zoneName: "ambiguous", zoneKey: 457), fixture.ConfigurationDirectory);
        await Assert.That(() => ambiguous.ResolveZone(database)).Throws<InvalidDataException>();

        var configuration = ZoneRuntimeConfiguration.LoadFile(fixture.ConfigurationPath);
        var zone = configuration.ResolveZone(database);
        using var assets = configuration.OpenAssets();
        await Assert.That(() => assets.LoadLayout(zone with { Key = 457 })).Throws<ArgumentException>();
        fixture.WritePrimaryWorld("""
            <Zone id="456" name="test_zone"><cellList><cell x="0" y="0"/></cellList></Zone>
            <Zone id="456" name="test_zone"><cellList><cell x="1" y="1"/></cellList></Zone>
            """);
        await Assert.That(() => assets.LoadLayout(zone)).Throws<InvalidDataException>();
        // A valid lower-priority world must not conceal a mismatched authoritative source.
        fixture.WritePrimaryWorld("<Zone id='999' name='test_zone'><cellList><cell x='0' y='0'/></cellList></Zone>");
        await Assert.That(() => assets.LoadLayout(zone)).Throws<InvalidDataException>();
        await Assert.That(File.Exists(Path.Combine(fixture.Root, "fallback", Fixture.WorldAsset.Replace('/', Path.DirectorySeparatorChar)))).IsTrue();
    }

    private static string ReadAsset(IZoneAssetSource source, string path)
    {
        using var stream = source.OpenRead(path) ?? throw new InvalidDataException($"Missing fixture asset {path}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Test]
    public async Task WorldLaunchOverlayReplacesEndpointZoneAndLogsWithoutTouchingTheBase()
    {
        using var fixture = new Fixture();
        var configuration = ZoneRuntimeConfiguration.LoadFile(fixture.ConfigurationPath);
        var launched = configuration.WithLaunch("10.0.0.2", 1300, "test_zone", 456, null, Path.Combine(fixture.Root, "world-logs"));
        await Assert.That(launched.WorldHost).IsEqualTo("10.0.0.2");
        await Assert.That(launched.WorldPort).IsEqualTo((ushort)1300);
        await Assert.That(launched.ZoneName).IsEqualTo("test_zone");
        await Assert.That(launched.LogDirectory).IsEqualTo(Path.Combine(fixture.Root, "world-logs"));
        await Assert.That(launched.ContentDatabasePath).IsEqualTo(configuration.ContentDatabasePath);
        await Assert.That(launched.EngineDirectory).IsEqualTo(configuration.EngineDirectory);
        await Assert.That(configuration.WorldPort).IsEqualTo((ushort)12345);
        await Assert.That(configuration.LogDirectory).IsEqualTo(Path.Combine(fixture.Root, "logs"));
        await Assert.That(launched.SpawnerFile).IsNull();
        using (var database = new ZoneContentDatabase(launched.ContentDatabasePath))
            await Assert.That(launched.ResolveZone(database).Key).IsEqualTo(456);

        await Assert.That(() => configuration.WithLaunch("http://bad:1", null, "test_zone", 456, null, null)).Throws<ArgumentException>();
        await Assert.That(() => configuration.WithLaunch(null, null, "test_zone", 0, null, null)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => configuration.WithLaunch(null, null, "test_zone", 456, "C:/x/compact.sqlite", null)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task LuaLibraryComesFromTheEnvironmentThenTheConfigurationWithNoDefault()
    {
        using var fixture = new Fixture();
        var unset = ZoneRuntimeConfiguration.LoadFile(fixture.ConfigurationPath);
        await Assert.That(unset.LuaLibraryPath).IsNull();
        await Assert.That(() => ZoneSceneHost.ResolveLuaLibrary(unset, _ => null)).Throws<ArgumentException>();

        var json = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(fixture.ConfigurationJson())!;
        json["luaLibraryPath"] = JsonSerializer.SerializeToElement("../lua/lua51.dll");
        File.WriteAllText(fixture.ConfigurationPath, JsonSerializer.Serialize(json));
        var configured = ZoneRuntimeConfiguration.LoadFile(fixture.ConfigurationPath);
        var expected = Path.Combine(fixture.Root, "lua", "lua51.dll");
        await Assert.That(configured.LuaLibraryPath).IsEqualTo(expected);
        await Assert.That(ZoneSceneHost.ResolveLuaLibrary(configured, _ => null)).IsEqualTo(expected);
        await Assert.That(configured.WithLaunch(null, null, "test_zone", 456, null, null).LuaLibraryPath).IsEqualTo(expected);

        var overridden = Path.Combine(fixture.Root, "override.dll");
        await Assert.That(ZoneSceneHost.ResolveLuaLibrary(configured,
            name => name == ZoneSceneHost.LuaLibraryEnvironment ? overridden : null)).IsEqualTo(overridden);
    }

    private sealed class Fixture : IDisposable
    {
        public const string WorldAsset = "game/worlds/test_world/world.xml";
        public string Root { get; } = Directory.CreateTempSubdirectory("aaemu-zone-runtime-").FullName;
        public string ConfigurationDirectory => Path.Combine(Root, "configuration");
        public string ConfigurationPath => Path.Combine(ConfigurationDirectory, "runtime.json");
        public string DatabasePath => Path.Combine(Root, "content", "game.compact");

        public Fixture()
        {
            Directory.CreateDirectory(ConfigurationDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = DatabasePath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                // These identities are synthetic test rows, not configured game-content IDs.
                command.CommandText = """
                    CREATE TABLE zone_groups(id INTEGER PRIMARY KEY, name TEXT);
                    CREATE TABLE zones(id INTEGER PRIMARY KEY, zone_key INTEGER, name TEXT, group_id INTEGER);
                    INSERT INTO zone_groups VALUES(7, 'Fixture group');
                    INSERT INTO zones VALUES(123, 456, 'test_zone', 7),
                        (124, 457, 'ambiguous', 7), (125, 458, 'AMBIGUOUS', 7);
                    """;
                command.ExecuteNonQuery();
            }
            WritePrimaryWorld("<Zone id='456' name='test_zone'><cellList><cell x='0' y='0'/></cellList></Zone>");
            Write("fallback", WorldAsset, World("Fallback map", "<Zone id='456' name='test_zone'><cellList><cell x='1' y='1'/></cellList></Zone>"));
            Write("primary", "game/priority.txt", "primary");
            Write("fallback", "game/priority.txt", "fallback");
            Write("fallback", "game/fallback-only.txt", "fallback");
            Write("primary", "game/empty.txt", "");
            Write("fallback", "game/empty.txt", "must not replace the existing empty file");
            File.WriteAllText(ConfigurationPath, ConfigurationJson());
        }

        public string ConfigurationJson(int zoneKey = 456, string zoneName = "TEST_ZONE",
            string contentDatabasePath = "../content/game.compact") => JsonSerializer.Serialize(new
        {
            contentDatabasePath,
            engineDirectory = "../engine",
            logDirectory = "../logs",
            zoneName,
            zoneKey,
            worldName = "test_world",
            worldMapAsset = WorldAsset,
            worldHost = "127.0.0.1",
            worldPort = 12345,
            locale = "C",
            assetSources = new[] { new { kind = "directory", path = "../primary" }, new { kind = "directory", path = "../fallback" } },
            terrainTexturePoolSize = 3,
            framePeriodMilliseconds = 20
        });

        public void WritePrimaryWorld(string rows) => Write("primary", WorldAsset, World("Primary map", rows));

        private static string World(string name, string rows) =>
            $"<World name='{name}' resolution='1024' unitSize='2' cellXCount='2' cellYCount='2'><ZoneList>{rows}</ZoneList></World>";

        private void Write(string source, string virtualPath, string text)
        {
            var path = Path.Combine(Root, source, virtualPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }

        public void Dispose()
        {
            var relative = Path.GetRelativePath(Path.GetTempPath(), Root);
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidOperationException("Fixture cleanup escaped its temporary directory.");
            Directory.Delete(Root, recursive: true);
        }
    }
}

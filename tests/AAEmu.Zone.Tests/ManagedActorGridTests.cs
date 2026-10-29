using System.Numerics;
using System.Text.Json;
using AAEmu.Zone.Ai;
using AAEmu.Zone.Scene;

namespace AAEmu.Zone.Tests;

public sealed class ManagedActorGridTests
{
    [Test]
    public async Task OriginalInstructionsAgreeWithMembershipCachedPositionsSlotsAndRotatingSlices()
    {
        using var stream = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream(
            "AAEmu.Zone.Tests.Fixtures.npc-actor-grid-native-controls.json")!;
        using var document = JsonDocument.Parse(stream); var errors = new List<string>();
        foreach (var control in document.RootElement.GetProperty("controls").EnumerateArray())
        {
            var label = control.GetProperty("name").GetString(); var request = control.GetProperty("request");
            var config = request.TryGetProperty("configuration", out var sourceConfig)
                ? sourceConfig.EnumerateArray().Select(value => value.GetInt32()).ToArray() : [0, 0, 7, 7];
            var registry = new ZoneAiObjectRegistry();
            using var grids = new ZoneManagedActorGrids(registry, new(config[0], config[1], config[2], config[3]));
            var objects = new Dictionary<string, ZoneAiObjectState>();
            var registrations = new Dictionary<string, ZoneAiObjectRegistry.Registration>();
            var leases = new Dictionary<string, ZoneManagedActorGridLease>();
            foreach (var actor in request.GetProperty("actors").EnumerateArray())
            {
                var name = actor.GetProperty("name").GetString()!; var kind = actor.GetProperty("kind").GetString();
                var initial = actor.TryGetProperty("initialCachedPosition", out var initialPosition) ? Vector(initialPosition) :
                    actor.TryGetProperty("position", out var position) ? Vector(position) : Vector3.Zero;
                var state = new ZoneAiObjectState { Position = actor.TryGetProperty("position", out var livePosition) ? Vector(livePosition) : initial,
                    Enabled = !actor.TryGetProperty("enabled", out var enabled) || enabled.GetBoolean() };
                var registration = registry.Register(kind == "player" ? (short)100 : (short)5, state, state);
                var lease = kind == "player" ? grids.BindPlayer(registration, state, actor.GetProperty("key").GetUInt64(), initial) :
                    grids.BindPuppet(registration, state, actor.GetProperty("key").GetUInt64(), initial, state.Enabled);
                objects.Add(name, state); registrations.Add(name, registration); leases.Add(name, lease);
            }
            var expected = control.GetProperty("results").EnumerateArray().ToArray();
            var index = 0; uint puppetCursor = 0, playerCursor = 0;
            foreach (var action in request.GetProperty("actions").EnumerateArray())
            {
                var kind = action.GetProperty("kind").GetString();
                var updated = Array.Empty<string>(); var query = Array.Empty<string>();
                switch (kind)
                {
                    case "move": objects[action.GetProperty("name").GetString()!].Position = Vector(action.GetProperty("position")); break;
                    case "enable":
                        var name = action.GetProperty("name").GetString()!; var enabled = action.GetProperty("enabled").GetBoolean();
                        objects[name].Enabled = enabled; leases[name].SetEnabled(enabled); break;
                    case "remove":
                        name = action.GetProperty("name").GetString()!;
                        leases[name].Dispose(); registrations[name].Dispose(); break;
                    case "slice":
                        var advance = grids.AdvanceCacheSlice(); puppetCursor = advance.PuppetCursor; playerCursor = advance.PlayerCursor;
                        updated = advance.UpdatedEntries.Select(entry => Name(entry.Object)).ToArray(); break;
                    case "query":
                        var result = grids.QueryBucket(action.GetProperty("bucket").GetInt32(),
                            Vector(action.GetProperty("position")), action.GetProperty("radius").GetSingle());
                        query = result.Entries.Select(entry => Name(entry.Object)).ToArray(); break;
                }
                var wanted = expected[index];
                if (puppetCursor != wanted.GetProperty("puppetCursor").GetUInt32()) Error("puppetCursor");
                if (playerCursor != wanted.GetProperty("playerCursor").GetUInt32()) Error("playerCursor");
                if (!updated.SequenceEqual(wanted.GetProperty("updated").EnumerateArray().Select(value => value.GetString()))) Error("updated order");
                if (!query.SequenceEqual(wanted.GetProperty("query").EnumerateArray().Select(value => value.GetString()))) Error("query order");
                foreach (var entry in wanted.GetProperty("entries").EnumerateArray())
                {
                    var name = entry.GetProperty("name").GetString()!; var lease = leases[name];
                    if (lease.IsDisposed)
                    { if (entry.GetProperty("isMember").GetBoolean() || lease.IsCurrent) Error(name + " retirement"); continue; }
                    var actual = lease.CopyState();
                    if (actual.IsMember != entry.GetProperty("isMember").GetBoolean()) Error(name + " membership");
                    if (actual.Column != entry.GetProperty("column").GetInt32() || actual.Row != entry.GetProperty("row").GetInt32() ||
                        actual.Slot != entry.GetProperty("slot").GetInt32()) Error(name + " cell/slot");
                    if (actual.IsMember && actual.CachedPosition != Vector(entry.GetProperty("cachedPosition"))) Error(name + " cached position");
                    if (objects[name].Position != Vector(entry.GetProperty("position"))) Error(name + " live position");
                }
                index++;
                void Error(string field) => errors.Add($"{label}/{index} {kind}: {field}");
            }
            string Name(ZoneAiObjectState? state) => objects.Single(pair => ReferenceEquals(pair.Value, state)).Key;
        }
        await Assert.That(string.Join(Environment.NewLine, errors)).IsEqualTo("");
    }

    [Test]
    public async Task UnknownPublicRegistrationsCannotDisappearFromAClaimedCompleteCensus()
    {
        var registry = new ZoneAiObjectRegistry(); using var grids = new ZoneManagedActorGrids(registry, new(0, 0, 7, 7));
        var self = new ZoneAiObjectState(); using var registration = registry.Register(5, self, self);
        using var actor = grids.BindPuppet(registration, self, 1, Vector3.Zero, true);
        var point = new ZoneAiObjectState(); using var unknown = registry.Register(2, point, point);
        await Assert.That(grids.CopyUnknownRegistrations().Single()).IsEqualTo(unknown);
        await Assert.That(() => grids.QueryBucket(0, Vector3.Zero, 30)).Throws<NotSupportedException>();
        await Assert.That(() => grids.AdvanceCacheSlice()).Throws<NotSupportedException>();
        using var declared = grids.BindNonActor(unknown);
        await Assert.That(grids.QueryBucket(0, Vector3.Zero, 30).Entries.Single().Object).IsEqualTo(self);
        declared.Dispose();
        await Assert.That(() => grids.QueryBucket(0, Vector3.Zero, 30)).Throws<NotSupportedException>();
        unknown.Dispose();
        await Assert.That(grids.QueryBucket(0, Vector3.Zero, 30).Entries.Count).IsEqualTo(1);
    }

    [Test]
    public async Task CachedPositionIsIndependentOfLiveMovementAndRefreshesEvenBelowTheSetPosDirtyThreshold()
    {
        var registry = new ZoneAiObjectRegistry(); using var grids = new ZoneManagedActorGrids(registry, new(0, 0, 7, 7));
        var state = new ZoneAiObjectState { Position = new(8000, 7000, 100) };
        using var registration = registry.Register(5, state, state);
        using var actor = grids.BindPuppet(registration, state, 1, Vector3.Zero, true);
        await Assert.That(grids.QueryBucket(0, Vector3.Zero, 1).Entries.Count).IsEqualTo(1);
        await Assert.That(grids.QueryBucket(0, state.Position, 1).Entries.Count).IsEqualTo(0);
        grids.AdvanceCacheSlice();
        var old = actor.CopyState(); state.Position += new Vector3(.01f, 0, 0);
        await Assert.That(actor.CopyState().CachedPosition).IsEqualTo(old.CachedPosition);
        grids.AdvanceCacheSlice();
        await Assert.That(actor.CopyState().CachedPosition).IsEqualTo(state.Position);
        await Assert.That(old.CachedPosition).IsNotEqualTo(state.Position);
    }

    [Test]
    public async Task RetirementInvalidatesCopiedQueriesAndReentryWithTheSameIdGetsANewRegistration()
    {
        var registry = new ZoneAiObjectRegistry(); using var grids = new ZoneManagedActorGrids(registry, new(0, 0, 7, 7));
        var old = new ZoneAiObjectState { EntityId = 9 }; using var registration = registry.Register(5, old, old);
        using var actor = grids.BindPuppet(registration, old, 1, Vector3.Zero, true);
        var query = grids.QueryBucket(0, Vector3.Zero, 1);
        await Assert.That(grids.IsCurrent(query)).IsTrue();
        registration.Dispose();
        await Assert.That(actor.IsCurrent).IsFalse();
        await Assert.That(grids.IsCurrent(query)).IsFalse();
        await Assert.That(grids.QueryBucket(0, Vector3.Zero, 1).Entries.Count).IsEqualTo(0);
        var fresh = new ZoneAiObjectState { EntityId = 9, Position = new(100, 100, 0) };
        using var nextRegistration = registry.Register(5, fresh, fresh);
        using var next = grids.BindPuppet(nextRegistration, fresh, 1, fresh.Position, true);
        await Assert.That(query.Entries.Single().Object).IsEqualTo(old);
        await Assert.That(grids.QueryBucket(0, fresh.Position, 1).Entries.Single().Object).IsEqualTo(fresh);
        await Assert.That(() => actor.SetEnabled(true)).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task NonfiniteSelectedPoseDoesNotPartiallyRefreshTheEarlierPlayerSlice()
    {
        var registry = new ZoneAiObjectRegistry(); using var grids = new ZoneManagedActorGrids(registry, new(0, 0, 7, 7));
        var puppet = new ZoneAiObjectState(); using var puppetRegistration = registry.Register(5, puppet, puppet);
        using var p = grids.BindPuppet(puppetRegistration, puppet, 1, Vector3.Zero, true);
        var player = new ZoneAiObjectState(); using var playerRegistration = registry.Register(100, player, player);
        using var other = grids.BindPlayer(playerRegistration, player, 2, Vector3.Zero);
        var revision = grids.Revision; player.Position = new(100, 100, 0); puppet.Position = new(float.NaN, 0, 0);
        await Assert.That(() => grids.AdvanceCacheSlice()).Throws<InvalidDataException>();
        await Assert.That(grids.Revision).IsEqualTo(revision);
        await Assert.That(other.CopyState().CachedPosition).IsEqualTo(Vector3.Zero);
        await Assert.That(p.CopyState().CachedPosition).IsEqualTo(Vector3.Zero);
        puppet.Position = new(200, 200, 0);
        var update = grids.AdvanceCacheSlice();
        await Assert.That(update.UpdatedEntries.Select(entry => entry.Object!).ToArray()).IsEquivalentTo([player, puppet]);
        await Assert.That(other.CopyState().CachedPosition).IsEqualTo(player.Position);
    }

    [Test]
    public async Task SourceDimensionsUseZoneExtentWhileEngineLocalCoordinatesAreClampedWithoutWorldOriginSubtraction()
    {
        var configuration = new ZoneManagedActorGridConfiguration(30, 40, 37, 47);
        await Assert.That(configuration.Columns).IsEqualTo(128);
        await Assert.That(configuration.Rows).IsEqualTo(128);
        var registry = new ZoneAiObjectRegistry(); using var grids = new ZoneManagedActorGrids(registry, configuration);
        var state = new ZoneAiObjectState { Position = new(8000, 7000, 0) };
        using var registration = registry.Register(5, state, state);
        using var actor = grids.BindPuppet(registration, state, 1, state.Position, true);
        await Assert.That(actor.CopyState().Column).IsEqualTo(125);
        await Assert.That(actor.CopyState().Row).IsEqualTo(109);
        await Assert.That(grids.QueryBucket(0, state.Position, 1).Entries.Count).IsEqualTo(1);
    }

    private static Vector3 Vector(JsonElement value)
    { var array = value.EnumerateArray().Select(item => item.GetSingle()).ToArray(); return new(array[0], array[1], array[2]); }
}

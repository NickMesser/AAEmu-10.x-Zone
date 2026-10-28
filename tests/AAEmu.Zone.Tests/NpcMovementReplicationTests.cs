using System.Numerics;
using System.Text.Json;
using AAEmu.Zone.Hosting;
using AAEmu.Zone.Npcs;

namespace AAEmu.Zone.Tests;

public sealed class NpcMovementReplicationTests
{
    [Test]
    public async Task OriginalSamplerInstructionsAgreeForEveryCacheFieldAcross45Controls()
    {
        using var stream = typeof(NpcMovementReplicationTests).Assembly.GetManifestResourceStream(
            "AAEmu.Zone.Tests.Fixtures.npc-replication-native-controls.json")!;
        using var json = JsonDocument.Parse(stream);
        var comparisons = 0;
        foreach (var scenario in json.RootElement.GetProperty("cases").EnumerateArray())
        {
            var cfg = scenario.GetProperty("settings");
            var settings = new ZoneNpcMovementReplicationSettings(cfg.GetProperty("movementSkip").GetBoolean(),
                cfg.GetProperty("syncPeriodMilliseconds").GetInt32(), cfg.GetProperty("skipDisabledAi").GetInt32(),
                cfg.GetProperty("skipStanding").GetInt32(), cfg.GetProperty("fastestSync").GetBoolean());
            var state = new ZoneNpcMovementReplicationState(0, 0);
            foreach (var control in scenario.GetProperty("steps").EnumerateArray())
            {
                var input = control.GetProperty("input");
                if (Bool(input, "reset")) state.Reset(settings);
                var source = new ZoneNpcMovementReplicationInput(Vector(input.GetProperty("position")),
                    Rotation(input.GetProperty("rotation")), Vector(input.GetProperty("velocity")),
                    (sbyte)input.GetProperty("stance").GetInt32(), UInt(input, "controllerId"),
                    (byte)UInt(input, "controllerPhase"), Bool(input, "keepSyncing"),
                    input.TryGetProperty("guardedAllowStrafing", out var slope) && slope.ValueKind != JsonValueKind.Null ? slope.GetBoolean() : null,
                    input.GetProperty("modelCanReplicate").GetBoolean());
                var decision = state.Sample(source, input.GetProperty("active").GetBoolean(),
                    input.GetProperty("now").GetUInt32(), input.GetProperty("characterCount").GetUInt64(), settings);
                var expected = control.GetProperty("expected");
                var expectedCache = new ZoneNpcMovementReplicationCache(expected.GetProperty("lastSync").GetUInt32(),
                    expected.GetProperty("disabledCalls").GetInt32(), expected.GetProperty("standingCalls").GetInt32(),
                    Vector(expected.GetProperty("position")), Rotation(expected.GetProperty("rotation")),
                    Vector(expected.GetProperty("velocity")), (sbyte)expected.GetProperty("stance").GetInt32(),
                    expected.GetProperty("controllerId").GetUInt32(), (byte)expected.GetProperty("controllerPhase").GetInt32(),
                    expected.GetProperty("cachedAllowStrafing").GetBoolean());
                await Assert.That(state.CopyState()).IsEqualTo(expectedCache);
                await Assert.That(decision.Send).IsEqualTo(control.GetProperty("send").GetBoolean());
                await Assert.That(decision.AdditionalMovementFlags).IsEqualTo((byte)control.GetProperty("additionalMovementFlags").GetInt32());
                comparisons++;
            }
        }
        await Assert.That(comparisons).IsEqualTo(45);
    }

    [Test]
    public async Task ResetChangesOnlyTheThreeNativeFieldsAndInitialRandomCountersUseHighProduct()
    {
        var state = new ZoneNpcMovementReplicationState(uint.MaxValue, 0x80000000);
        await Assert.That(state.CopyState().DisabledCalls).IsEqualTo(99);
        await Assert.That(state.CopyState().StandingCalls).IsEqualTo(50);
        var settings = new ZoneNpcMovementReplicationSettings();
        var input = new ZoneNpcMovementReplicationInput(new(2, 3, 4), Quaternion.Identity, new(5, 6, 7), 1, 37, 2, GuardedAllowStrafing: true);
        await Assert.That(state.Sample(input, true, 100, 0, settings).Send).IsTrue();
        var before = state.CopyState(); state.Reset(settings);
        await Assert.That(state.CopyState()).IsEqualTo(before with { LastSync = 0, DisabledCalls = 2000, StandingCalls = 400 });
    }

    [Test]
    public async Task ActualCvarDefaultsAndDedicatedOverridesRetainSignedCounterAndPeriodValues()
    {
        await Assert.That(ZoneNpcMovementReplicationSettings.From(ZoneEngineCVars.Empty)).IsEqualTo(new ZoneNpcMovementReplicationSettings());
        var values = ZoneEngineCVars.Parse("npc_move_sync_period=25\nnpc_move_skip_disabledAI=-1\nnpc_movement_skip=0",
            new Dictionary<string, string> { [ZoneEngineCVars.NpcMoveSyncPeriod] = "-1", [ZoneEngineCVars.NpcEnableFastestSync] = "1" });
        await Assert.That(ZoneNpcMovementReplicationSettings.From(values)).IsEqualTo(new ZoneNpcMovementReplicationSettings(false, -1, -1, 400, true));
    }

    private static Vector3 Vector(JsonElement e) => new(e[0].GetSingle(), e[1].GetSingle(), e[2].GetSingle());
    private static Quaternion Rotation(JsonElement e) => new(e[0].GetSingle(), e[1].GetSingle(), e[2].GetSingle(), e[3].GetSingle());
    private static uint UInt(JsonElement e, string name) => e.TryGetProperty(name, out var value) ? value.GetUInt32() : 0;
    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var value) && value.GetBoolean();
}

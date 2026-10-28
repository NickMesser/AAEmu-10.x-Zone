using AAEmu.Zone.Hosting;
using AAEmu.Zone.Protocol.Messages;
using AAEmu.Zone.Scene;
using AAEmu.Zone.WorldState;

namespace AAEmu.Zone.Tests;

public sealed class ZoneTimeOfDayEngineUpdateTests
{
    [Test]
    public async Task HourAdvancesByDefault()
    {
        using var state = new ZoneTimeOfDayState();
        state.SetTime(10f);
        state.Advance(100f);
        await Assert.That(state.Hour).IsGreaterThan(10f);
    }

    [Test]
    public async Task HourStaysFrozenWhenEngineUpdateIsOff_AndNoReportsFollow()
    {
        using var state = new ZoneTimeOfDayState { EngineUpdateEnabled = false };
        state.Receive(new ZoneTimeOfDay(10f));
        state.CreateInitialReport();
        for (var i = 0; i < 100; i++)
        {
            state.Advance(0.5f);
            await Assert.That(state.CreateTimeReport(0.5f)).IsNull();
        }
        await Assert.That(state.Hour).IsEqualTo(10f);
    }

    [Test]
    public async Task WorldCorrectionStillMovesFrozenHourAndIsReportedOnce()
    {
        using var state = new ZoneTimeOfDayState { EngineUpdateEnabled = false };
        state.Receive(new ZoneTimeOfDay(10f));
        state.CreateInitialReport();
        state.Receive(new ZoneTimeOfDay(12f));
        await Assert.That(state.CreateTimeReport(2f)).IsNotNull();
        await Assert.That(state.CreateTimeReport(2f)).IsNull();
    }

    [Test]
    public async Task CVarsReadDedicatedCfgAndCommandLineOverrides()
    {
        const string cfg = "e_render = 0\r\n--e_time_of_day_engine_update = 1\r\ne_time_of_day_engine_update = 0\r\n";
        await Assert.That(ZoneEngineCVars.Parse(cfg).GetBool(ZoneEngineCVars.TimeOfDayEngineUpdate, true)).IsFalse();
        var overrides = new Dictionary<string, string> { ["e_time_of_day_engine_update"] = "1" };
        await Assert.That(ZoneEngineCVars.Parse(cfg, overrides).GetBool(ZoneEngineCVars.TimeOfDayEngineUpdate, true)).IsTrue();
        await Assert.That(ZoneEngineCVars.Parse(null).GetBool(ZoneEngineCVars.TimeOfDayEngineUpdate, true)).IsTrue();
        await Assert.That(ZoneEngineCVars.Parse("# only comments").GetBool(ZoneEngineCVars.TimeOfDayEngineUpdate, false)).IsFalse();
    }

    [Test]
    public async Task ContinentHostIsServerType3AndInstancesAre2()
    {
        await Assert.That(ZoneServerType.For(0)).IsEqualTo(3);
        await Assert.That(ZoneServerType.For(7)).IsEqualTo(2);
    }
}

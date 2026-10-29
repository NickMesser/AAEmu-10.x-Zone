using System.Globalization;
using System.Text.Json;
using AAEmu.Zone.Scripting;

namespace AAEmu.Zone.Tests;

public sealed class LuaScriptAnyTests
{
    [Test]
    public async Task TypedBooleanReadsMatchOriginalTableAndScriptSystemInstructions()
    {
        using var stream = typeof(LuaScriptAnyTests).Assembly.GetManifestResourceStream(
            "AAEmu.Zone.Tests.Fixtures.npc-script-any-boolean-native-controls.json")!;
        using var source = JsonDocument.Parse(stream);
        using var lua = new ZoneManagedLuaRuntime();
        var checkedControls = 0;
        foreach (var control in source.RootElement.GetProperty("controls").EnumerateArray())
        {
            using var table = lua.CreateTable();
            using var function = lua.CreateFunction(_ => []);
            var text = control.GetProperty("value").GetString()!;
            object? value = control.GetProperty("luaType").GetInt32() switch
            {
                0 => null,
                1 => text == "True",
                2 => new ZoneLuaHandle(1),
                3 => text switch
                {
                    "nan" => double.NaN, "inf" => double.PositiveInfinity, "-inf" => double.NegativeInfinity,
                    "9007199254740993" => 9007199254740993L,
                    _ => double.Parse(text, CultureInfo.InvariantCulture),
                },
                4 => text[1..^1],
                5 => table,
                6 => function,
                _ => throw new InvalidDataException("Unknown native Lua value kind."),
            };
            var accepted = ZoneLuaScriptAny.TryBoolean(value, out var result);
            await Assert.That(accepted).IsEqualTo(control.GetProperty("accepted").GetBoolean());
            if (accepted) await Assert.That(result).IsEqualTo(control.GetProperty("boolean").GetBoolean());
            checkedControls++;
        }
        await Assert.That(checkedControls).IsEqualTo(19);
    }
}

using AAEmu.Zone.Content;

namespace AAEmu.Zone.Tests;

public sealed class ContentFormulaTests
{
    [Test]
    [Arguments("2 + 3 * 4", 14f)]
    [Arguments("(2 + 3) * 4", 20f)]
    [Arguments("2^3^2", 512f)]
    [Arguments("-2^2 + 2^-2", -3.75f)]
    [Arguments("1e2 + .25 - 5E-1", 99.75f)]
    [Arguments("floor(-1.1) + abs(-3) + sqrt(9)", 4f)]
    [Arguments("log(100) + min(2, 3) + max(4, 5)", 9f)]
    [Arguments("clamp(-1, 0, 10) + clamp(20, 0, 10) + clamp(5, 0, 10)", 15f)]
    [Arguments("if_negative(-1, 7, log(0)) + if_positive(0, log(0), 3)", 10f)]
    [Arguments("if_zero(0, 8, log(0)) + if_negative(0, log(0), 2)", 10f)]
    [Arguments("(16777216 + 1) - 16777216", 0f)]
    [Arguments("min(sqrt(-1), 3) + max(sqrt(-1), 4) + clamp(sqrt(-1), 5, 10)", 12f)]
    public async Task ArithmeticUsesNativeFloatAndConditionalSemantics(string text, float expected)
    {
        await Assert.That(new ContentFormula(text).Evaluate(_ => throw new InvalidOperationException())).IsEqualTo(expected);
    }

    [Test]
    public async Task DivisionByZeroDoesNotEvaluateItsNumerator()
    {
        var formula = new ContentFormula("unavailable / (2 - 2) + 6 / 2");
        await Assert.That(formula.Variables.SetEquals(["unavailable"])).IsTrue();
        await Assert.That(formula.Evaluate(_ => throw new InvalidOperationException())).IsEqualTo(3f);
        var variables = new ContentFormula("level * 2 + heir_level");
        await Assert.That(variables.Evaluate(name => name == "level" ? 55 : 28)).IsEqualTo(138f);
    }

    [Test]
    public async Task MalformedExpressionsAndResourceExhaustionAreRejected()
    {
        foreach (var text in new[] { "1 +", "1 2", "(1", "abs(1,2)", "clamp(1,2)", "run(1)", "1..2", "1e999", "level; code" })
            await Assert.That(() => new ContentFormula(text)).Throws<InvalidDataException>();
        await Assert.That(() => new ContentFormula(new string('(', 65) + "1" + new string(')', 65))).Throws<InvalidDataException>();
        await Assert.That(() => new ContentFormula(new string('1', 4097))).Throws<InvalidDataException>();
        await Assert.That(() => new ContentFormula("sqrt(-1)").Evaluate(_ => 0)).Throws<InvalidDataException>();
    }
}

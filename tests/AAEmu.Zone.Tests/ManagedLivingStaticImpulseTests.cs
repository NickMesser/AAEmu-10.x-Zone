using System.Numerics;
using AAEmu.Zone.Physics;

namespace AAEmu.Zone.Tests;

public sealed class ManagedLivingStaticImpulseTests
{
    [Test]
    public async Task SmallInwardContactUsesOnePointZeroOneResponseAndPreservesItsTangent()
    {
        var contact = ZoneLivingMovementMath.ApplyStaticContactImpulse(new(3, -4, -.5f), Vector3.UnitZ, 80);
        await Assert.That(contact.WasCapped).IsFalse();
        await Near(contact.InwardNormalVelocity, -.5f);
        await Near(contact.Impulse, new(0, 0, 40.4f), .00001f);
        await Near(contact.Velocity, new(3, -4, .005f));
        var movingOut = ZoneLivingMovementMath.ApplyStaticContactImpulse(new(3, -4, .5f), Vector3.UnitZ, 80);
        await Assert.That(movingOut.Velocity).IsEqualTo(new Vector3(3, -4, .5f));
        await Assert.That(movingOut.Impulse).IsEqualTo(Vector3.Zero);
    }

    [Test]
    public async Task StrictCapBoundaryRetainsEqualityAndChangesAtTheNextFloat()
    {
        // Independent scalar calculation for mass1 / +Z: speed*1.01f is
        // exactly1 at3f7d7721 and3f7d7722, then greater than1 at3f7d7723.
        var equalitySpeed = BitConverter.Int32BitsToSingle(0x3f7d7721);
        var equal = ZoneLivingMovementMath.ApplyStaticContactImpulse(new(0, 0, -equalitySpeed), Vector3.UnitZ, 1);
        await Assert.That(equal.WasCapped).IsFalse();
        await Assert.That(equal.Impulse.Z).IsEqualTo(1f);
        var nextSpeed = MathF.BitIncrement(equalitySpeed);
        var next = ZoneLivingMovementMath.ApplyStaticContactImpulse(new(0, 0, -nextSpeed), Vector3.UnitZ, 1);
        await Assert.That(next.WasCapped).IsFalse();
        await Assert.That(next.Impulse.Z).IsEqualTo(1f);
        var cappedSpeed = MathF.BitIncrement(nextSpeed);
        var above = ZoneLivingMovementMath.ApplyStaticContactImpulse(new(0, 0, -cappedSpeed), Vector3.UnitZ, 1);
        await Assert.That(above.WasCapped).IsTrue();
        await Near(above.Velocity.Z, -cappedSpeed * .5f);
        var below = ZoneLivingMovementMath.ApplyStaticContactImpulse(new(0, 0, -MathF.BitDecrement(equalitySpeed)), Vector3.UnitZ, 1);
        await Assert.That(below.WasCapped).IsFalse();
        await Assert.That(below.Impulse.Z).IsEqualTo(MathF.BitDecrement(1f));
    }

    [Test]
    public async Task ExecutedSlopeImpactUsesTheCappedImpulseBeforeTheSeparateGravityStage()
    {
        var normal = new Vector3(.6037414f, -.07371297f, .7937649f);
        var contact = ZoneLivingMovementMath.ApplyStaticContactImpulse(new(0, 0, -2.5f), normal, 80);
        await Assert.That(contact.WasCapped).IsTrue();
        await Near(contact.Velocity + new Vector3(0, 0, -.25f),
            new(.5990359f, -.07313846f, -1.9624214f));
        await Near(Vector3.Dot(contact.Velocity, normal), -2.5f * normal.Z * .5f);
    }

    [Test]
    public async Task LargeContactResponseRespectsMassAndRefusesUnrepresentableInputs()
    {
        foreach (var mass in new[] { 1f, 80f, 250f })
        {
            var contact = ZoneLivingMovementMath.ApplyStaticContactImpulse(new(-4, 3, 2), Vector3.UnitX, mass);
            await Assert.That(contact.WasCapped).IsTrue();
            await Near(contact.Impulse, new(2 * mass, 0, 0), .0001f);
            await Near(contact.Velocity, new(-2, 3, 2));
        }
        await Assert.That(() => ZoneLivingMovementMath.ApplyStaticContactImpulse(Vector3.Zero, Vector3.UnitZ, 0))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => ZoneLivingMovementMath.ApplyStaticContactImpulse(new(0, 0, -float.MaxValue), Vector3.UnitZ, 80))
            .Throws<ArgumentOutOfRangeException>();
    }

    private static async Task Near(Vector3 actual, Vector3 expected, float tolerance = .000002f)
    {
        await Near(actual.X, expected.X, tolerance); await Near(actual.Y, expected.Y, tolerance); await Near(actual.Z, expected.Z, tolerance);
    }
    private static async Task Near(float actual, float expected, float tolerance = .000002f) =>
        await Assert.That(MathF.Abs(actual - expected)).IsLessThanOrEqualTo(tolerance);
}

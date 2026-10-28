using System.Numerics;
using AAEmu.Zone.Scene;

namespace AAEmu.Zone.Tests;

public sealed class ManagedEntityOrientationTests
{
    [Test]
    public async Task NumericAxesTruncateAfterFloatConversionAndOtherArgumentsKeepY()
    {
        await Assert.That(ZoneManagedEntityOrientation.AxisFromScriptArgument(.999999999999d)).IsEqualTo(1);
        await Assert.That(ZoneManagedEntityOrientation.AxisFromScriptArgument(.99d)).IsEqualTo(0);
        await Assert.That(ZoneManagedEntityOrientation.AxisFromScriptArgument(2.9d)).IsEqualTo(2);
        await Assert.That(ZoneManagedEntityOrientation.AxisFromScriptArgument(-.9d)).IsEqualTo(0);
        foreach (var value in new object?[] { null, "0", true, new object() })
            await Assert.That(ZoneManagedEntityOrientation.AxisFromScriptArgument(value)).IsEqualTo(1);
        foreach (var value in new[] { double.NaN, double.PositiveInfinity, 2147483648d })
            await Assert.That(ZoneManagedEntityOrientation.AxisFromScriptArgument(value)).IsEqualTo(int.MinValue);
        await Assert.That(ZoneManagedEntityOrientation.GetDirectionVector(Quaternion.Identity, 3, -19)).IsEqualTo(new Vector3(0, 3, 0));
    }

    [Test]
    public async Task UniformScaleAndMirroringSurviveTheRawQuaternionDirectionGetter()
    {
        var quarterTurn = new Quaternion(0, 0, .5f, .5f);
        // This deliberately non-unit quaternion expands to columns(.5,.5,0),(-.5,.5,0),(0,0,1).
        await Assert.That(ZoneManagedEntityOrientation.GetDirectionVector(quarterTurn, 3, 0)).IsEqualTo(new Vector3(1.5f, 1.5f, 0));
        await Assert.That(ZoneManagedEntityOrientation.GetDirectionVector(quarterTurn, 3)).IsEqualTo(new Vector3(-1.5f, 1.5f, 0));
        await Assert.That(ZoneManagedEntityOrientation.GetDirectionVector(quarterTurn, -2, 2)).IsEqualTo(new Vector3(0, 0, -2));
        await Assert.That(ZoneManagedEntityOrientation.GetDirectionVector(new Quaternion(0, 0, -.5f, -.5f), 3, 0))
            .IsEqualTo(ZoneManagedEntityOrientation.GetDirectionVector(quarterTurn, 3, 0));
        //Original32012F50/3203CB90 gives positive zero for the mirrored identity's off-axis terms.
        var mirrored = ZoneManagedEntityOrientation.GetDirectionVector(Quaternion.Identity, -2, 0);
        await Assert.That(BitConverter.SingleToUInt32Bits(mirrored.Y)).IsEqualTo(0u);
        await Assert.That(BitConverter.SingleToUInt32Bits(mirrored.Z)).IsEqualTo(0u);
    }

    [Test]
    public async Task ComposedParentMatrixKeepsNonuniformScaleShearAndTranslationIndependence()
    {
        var parent = new Matrix4x4(2, 1, .25f, 0, .5f, 3, -.5f, 0, -.25f, .75f, 4, 0, 8000, 7900, 350, 1);
        // A local quarter turn swaps the first two parent rows and negates the former first row.
        var local = new Matrix4x4(0, 1, 0, 0, -1, 0, 0, 0, 0, 0, 1, 0, 4, 5, 6, 1);
        var composed = local * parent;
        await Assert.That(ZoneManagedEntityOrientation.GetDirectionVector(composed, 0)).IsEqualTo(new Vector3(.5f, 3, -.5f));
        await Assert.That(ZoneManagedEntityOrientation.GetDirectionVector(composed)).IsEqualTo(new Vector3(-2, -1, -.25f));
        await Assert.That(ZoneManagedEntityOrientation.GetDirectionVector(composed, 2)).IsEqualTo(new Vector3(-.25f, .75f, 4));
        composed.Translation = new(-9999, 12000, -80);
        await Assert.That(ZoneManagedEntityOrientation.GetDirectionVector(composed)).IsEqualTo(new Vector3(-2, -1, -.25f));
    }
}

using System.Reflection;
using System.Runtime.InteropServices;
using AAEmu.Zone.Native.Physics;

namespace AAEmu.Zone.Tests;

public sealed class NativeLivingDiagnosticLayoutTests
{
    [Test]
    public async Task OriginalLivingVelocityActionIncludesTheValidatedTimeField()
    {
        // Original3502A300 reads through+23;35038670 validates the float dt at+20.
        // Metadata verification loads no engine module and catches unsafe truncation.
        var type = typeof(ZoneNativeTerrainPhysicsWorld).GetNestedType("DiagnosticLivingVelocityAction", BindingFlags.NonPublic)!;
        await Assert.That(Marshal.SizeOf(type)).IsEqualTo(24);
        foreach (var (field, offset) in new[] { ("Type", 0), ("Direction", 4), ("JumpMode", 16), ("RequestedTime", 20) })
            await Assert.That(Marshal.OffsetOf(type, field).ToInt32()).IsEqualTo(offset);
        var pose = typeof(ZoneNativeTerrainPhysicsWorld).GetNestedType("LivingPose", BindingFlags.NonPublic)!;
        await Assert.That(Marshal.SizeOf(pose)).IsEqualTo(72);
        foreach (var (field, offset) in new[] { ("Type", 0), ("Position", 4), ("Orientation", 16), ("Scale", 32),
                     ("Matrix", 40), ("Matrix3", 48), ("SimulationClass", 56), ("RecalculateBounds", 60),
                     ("BoundsOnly", 64), ("ThreadSafe", 68) })
            await Assert.That(Marshal.OffsetOf(pose, field).ToInt32()).IsEqualTo(offset);
    }
}

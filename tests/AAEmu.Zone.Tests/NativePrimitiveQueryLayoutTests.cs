using System.Reflection;
using System.Runtime.InteropServices;
using AAEmu.Zone.Native.Physics;

namespace AAEmu.Zone.Tests;

public sealed class NativePrimitiveQueryLayoutTests
{
    [Test]
    public async Task OriginalPrimitiveQueryAbiMatchesRecoveredBinaryOffsets()
    {
        // These sizes/offsets are the original350964F0/35093100 contract. No engine
        // module is loaded by checking managed metadata; a layout regression is unsafe.
        await Layout("PrimitiveRequest", 96,
            ("Kind", 24), ("Primitive", 32), ("Sweep", 40), ("ObjectTypes", 52), ("Contacts", 56),
            ("RequiredGeometryFlags", 64), ("AnyGeometryFlags", 68), ("IntersectionParameters", 72),
            ("SkipCount", 80), ("SkipEntities", 88));
        await Layout("PrimitiveBox", 64, ("M00", 0), ("M11", 16), ("M22", 32),
            ("Oriented", 36), ("Center", 40), ("HalfExtents", 52));
        await Layout("PrimitiveAxial", 32, ("Center", 0), ("Axis", 12), ("Radius", 24), ("HalfHeight", 28));
        await Layout("PrimitiveContact", 136, ("Distance", 0), ("Position", 8), ("Normal", 20),
            ("PhysicalMaterialId", 56), ("EntityId", 60), ("PartId", 64));
    }

    private static async Task Layout(string name, int bytes, params (string Field, int Offset)[] fields)
    {
        var type = typeof(ZoneNativeTerrainPhysicsWorld).GetNestedType(name, BindingFlags.NonPublic) ??
            throw new InvalidOperationException($"The native query profile {name} is missing.");
        await Assert.That(Marshal.SizeOf(type)).IsEqualTo(bytes);
        foreach (var (field, offset) in fields)
            await Assert.That(Marshal.OffsetOf(type, field).ToInt32()).IsEqualTo(offset);
    }
}

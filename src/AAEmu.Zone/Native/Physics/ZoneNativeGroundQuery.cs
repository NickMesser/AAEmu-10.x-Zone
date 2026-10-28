using System.Numerics;

using AAEmu.Zone.Physics;

namespace AAEmu.Zone.Native.Physics;

/// <summary>
/// Adapts the scene's REAL native terrain to the owned authority's minimal ground query.
///
/// The point of this adapter is that owned physics does NOT reimplement terrain: it asks the
/// engine's own raycast, exactly like the scene's ground sampling already does. If the engine
/// changes its collision, the owned authority changes with it, and the ground can never drift away
/// from what the world actually contains.
/// </summary>
internal sealed class ZoneNativeGroundQuery : ZoneOwnedPlayerPhysics.IGroundQuery
{
    private readonly ZoneNativeTerrainPhysicsWorld _world;

    internal ZoneNativeGroundQuery(ZoneNativeTerrainPhysicsWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        _world = world;
    }

    /// <inheritdoc/>
    public bool TryGround(Vector3 from, Vector3 to, out Vector3 position, out ushort physicalMaterialId)
    {
        // Terrain first: the heightfield is the authoritative floor and the cheaper answer.
        if (_world.RaycastTerrain(from, to - from, out var terrain))
        {
            position = terrain.Position;
            physicalMaterialId = terrain.PhysicalMaterialId;
            Trace($"HIT  terrain from=({from.X}, {from.Y}, {from.Z}) ground=({terrain.Position.X}, {terrain.Position.Y}, {terrain.Position.Z}) material={terrain.PhysicalMaterialId}");
            return true;
        }
        // Then the STATIC scene. Measured need (round 64): the loaded cells publish static objects in
        // columns where no terrain heightfield exists - a player standing there free-fell through the
        // world, because a terrain-only ray cannot see a building. Standing on real static geometry is
        // not an approximation: it is the engine's own scene query, and a surface you can stand on is
        // a surface, whether it was authored as heightfield or as a model. Still never an invented
        // floor - both misses return false exactly as before.
        if (_world.RaycastStaticScene(from, to - from, out var scene))
        {
            position = scene.Position;
            physicalMaterialId = scene.PhysicalMaterialId;
            Trace($"HIT  static  from=({from.X}, {from.Y}, {from.Z}) ground=({scene.Position.X}, {scene.Position.Y}, {scene.Position.Z}) material={scene.PhysicalMaterialId}");
            return true;
        }
        Trace($"MISS from=({from.X}, {from.Y}, {from.Z}) to=({to.X}, {to.Y}, {to.Z})");
        position = default;
        physicalMaterialId = 0;
        return false;
    }

    private int _traces;

    private void Trace(string message)
    {
        if (_traces >= 6) return;
        _traces++;
        Console.WriteLine($"GROUND {message} (trace {_traces}/6)");
    }
}

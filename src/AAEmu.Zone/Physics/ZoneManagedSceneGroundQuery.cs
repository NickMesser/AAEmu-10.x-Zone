using System.Numerics;

using AAEmu.Zone.Actors;

namespace AAEmu.Zone.Physics;

/// <summary>Character support over terrain and authored static meshes, without selecting roofs overhead.</summary>
public sealed class ZoneManagedSceneGroundQuery(
    ZoneManagedTerrainGeometry terrain, ZoneManagedStaticGeometry? statics = null,
    Func<ZoneActorHandle, float>? groundPivotHeight = null) : ZoneOwnedPlayerPhysics.IGroundQuery
{
    /// <summary>
    /// Current character-controller allowance for a rising walking surface. Authored stance step
    /// parameters and a complete stair solver remain a separate movement-fidelity task.
    /// </summary>
    public const float MaximumStaticGroundRise = .3f;
    public float ContactTolerance => .002f;
    public float GetGroundPivotHeight(ZoneActorHandle actor) => groundPivotHeight?.Invoke(actor) ?? 0;

    public bool TryGround(Vector3 from, Vector3 to, out Vector3 position, out ushort physicalMaterialId)
    {
        var found = terrain.RaycastTerrain(from, to - from, out var hit);
        if (statics is not null && statics.Raycast(from, to - from, out var other) && (!found || other.Distance < hit.Distance))
        {
            found = true;
            hit = other;
        }
        position = hit.Position;
        physicalMaterialId = hit.PhysicalMaterialId;
        return found;
    }

    public bool TryCharacterGround(Vector3 previousPosition, Vector3 nextPosition, bool wasGrounded,
        out Vector3 position, out ushort physicalMaterialId)
    {
        position = default;
        physicalMaterialId = 0;
        if (nextPosition.Z > previousPosition.Z + ContactTolerance) return false;
        ZonePhysicsHit support;
        if (wasGrounded)
        {
            // A grounded controller can follow a nearby rising slope. Constrain terrain too:
            // a wide terrain-only column would incorrectly pull cave occupants onto the hill.
            var top = previousPosition.Z + MaximumStaticGroundRise;
            var bottom = MathF.Min(previousPosition.Z - 4000f, nextPosition.Z - 1f);
            if (!SupportRay(new(nextPosition.X, nextPosition.Y, top), new(0, 0, bottom - top), out support)) return false;
        }
        else if (!SupportRay(new(nextPosition.X, nextPosition.Y, nextPosition.Z + ContactTolerance),
            new(0, 0, -ContactTolerance * 2), out support))
        {
            // Falling support must have been crossed on the actual foot trajectory. An endpoint
            // column alone can snap a character upward onto a platform it passed beneath.
            if (!SupportRay(previousPosition, nextPosition - previousPosition, out var crossed)) return false;
            var height = crossed.Position.Z - ((nextPosition.X - crossed.Position.X) * crossed.Normal.X +
                (nextPosition.Y - crossed.Position.Y) * crossed.Normal.Y) / crossed.Normal.Z;
            if (!SupportRay(new(nextPosition.X, nextPosition.Y, height + ContactTolerance),
                new(0, 0, -ContactTolerance * 2), out support)) return false;
        }
        position = support.Position;
        physicalMaterialId = support.PhysicalMaterialId;
        return true;
    }

    private bool SupportRay(Vector3 origin, Vector3 displacement, out ZonePhysicsHit hit)
    {
        var found = terrain.RaycastTerrain(origin, displacement, out hit) &&
            hit.Normal.Z >= ZoneManagedPhysicsBackend.MinimumWalkableNormalZ;
        if (statics is not null && statics.Raycast(origin, displacement, out var other,
            ZoneManagedPhysicsBackend.MinimumWalkableNormalZ) && (!found || other.Distance < hit.Distance))
        {
            hit = other;
            found = true;
        }
        return found;
    }
}

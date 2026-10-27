using System.Runtime.InteropServices;

namespace AAEmu.Zone.Simulation;

public sealed unsafe partial class ZoneNativeEngine
{
    // CSystem retains the native grid when a physics adapter retires. Never key its readiness by
    // an adapter, a numeric world address shared with a later process generation, or a body handle.
    private readonly ZoneNativeTerrainPhysicsWorld.TerrainEntityGridState _terrainEntityGrid = new();

    internal void RequireTerrainEntityGrid()
    {
        VerifyAccess();
        VerifyAvailable();
        if (!_operationActive)
            throw new InvalidOperationException("Validate the terrain entity grid under the engine operation gate.");
        if (!_terrainEntityGrid.Configured)
            throw new InvalidOperationException("ConfigureTerrainEntityGrid must complete before native brush creation.");
        try { ZoneNativeTerrainPhysicsWorld.ValidateTerrainEntityGrid(_physicsWorld, _terrainEntityGrid); }
        catch (Exception error) { Poison(error); throw; }
    }

    internal sealed partial class PhysicsLease
    {
        internal ZoneNativeTerrainPhysicsWorld.TerrainEntityGridState EntityGridState => _engine._terrainEntityGrid;

        internal void RequireTerrainEntityGridSetupGate()
        {
            ThrowIfFaulted();
            if (!_engine._operationActive)
                throw new InvalidOperationException("Check terrain initialization under the engine operation gate.");
            var terrainModule = _engine.RequiredModule("cry3dengine_dedicate.dll");
            try
            {
                _engine.ValidateStaticObjectState(Marshal.ReadIntPtr(Environment + 0x58), terrainModule, ModuleHandle);
            }
            catch (Exception error) { Poison(error); throw; }
            // CTerrain300CFEC0 skips IPhysicalWorld+58 when this native process mode is enabled.
            if (Marshal.ReadByte(terrainModule + 0x2fa949) != 0)
                throw new InvalidOperationException("The native terrain initialization mode does not permit entity-grid setup.");
        }
    }
}

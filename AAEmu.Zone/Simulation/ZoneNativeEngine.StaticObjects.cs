using System.Runtime.InteropServices;

namespace AAEmu.Zone.Simulation;

public sealed partial class ZoneNativeEngine
{
    private StaticObjectLease? _staticObjectBorrower;

    /// <summary>
    /// Borrows this engine's real static-model services. The adapter retains its native models
    /// and dependent resources until retirement, before CSystem destroys 3DEngine and physics.
    /// Model availability alone does not load cell objects or establish a playable scene.
    /// </summary>
    public ZoneNativeStaticObjectServices CreateStaticObjectServices()
    {
        using var operation = EnterOperation();
        if (_staticObjectBorrower is not null)
            throw new InvalidOperationException("This engine already has a static-object service borrower.");
        var terrainModule = RequiredModule("cry3dengine_dedicate.dll");
        var physicsModule = RequiredModule("cryphysics_dedicate.dll");
        var threeDEngine = Marshal.ReadIntPtr(_environment + 0x58);
        ValidateStaticObjectState(threeDEngine, terrainModule, physicsModule);
        var lease = new StaticObjectLease(this, threeDEngine, terrainModule, physicsModule);
        _staticObjectBorrower = lease;
        try
        {
            // Read-only construction. Native allocation starts only after the adapter is rooted.
            var services = new ZoneNativeStaticObjectServices(lease);
            lease.Resource = services;
            return services;
        }
        catch (Exception error) { Poison(error); throw; }
    }

    private void ValidateStaticObjectState(nint threeDEngine, nint terrainModule, nint physicsModule)
    {
        if (threeDEngine == 0 || Marshal.ReadIntPtr(_environment) != _system ||
            Marshal.ReadIntPtr(_environment + 0x58) != threeDEngine ||
            Marshal.ReadIntPtr(terrainModule + 0x33f588) != _environment ||
            Marshal.ReadIntPtr(terrainModule + 0x2fa8c8) != threeDEngine)
            throw new InvalidOperationException("Static-object services do not belong to this CSystem environment.");
        ValidatePhysicsState(physicsModule);
    }

    internal sealed class StaticObjectLease
    {
        private readonly ZoneNativeEngine _engine;
        private readonly nint _terrainVtable;
        private bool _released;
        internal ZoneNativeEngine Engine => _engine;
        internal nint System { get; }
        internal nint Environment { get; }
        internal nint ThreeDEngine { get; }
        internal nint TerrainModule { get; }
        internal nint PhysicsWorld { get; }
        internal nint PhysicsModule { get; }
        internal ZoneNativeStaticObjectServices? Resource { get; set; }

        internal StaticObjectLease(ZoneNativeEngine engine, nint threeDEngine, nint terrainModule, nint physicsModule)
        {
            _engine = engine;
            System = engine._system;
            Environment = engine._environment;
            ThreeDEngine = threeDEngine;
            TerrainModule = terrainModule;
            PhysicsWorld = engine._physicsWorld;
            PhysicsModule = physicsModule;
            _terrainVtable = Marshal.ReadIntPtr(threeDEngine);
        }

        internal IDisposable Enter()
        {
            var operation = _engine.EnterOperation();
            try { RequireActiveOperation(); return operation; }
            catch { operation.Dispose(); throw; }
        }

        internal void RequireActiveOperation()
        {
            ThrowIfFaulted(); // Reject stale managed owners before native reads.
            if (!_engine._operationActive)
                throw new InvalidOperationException("Static-object operations require the shared engine gate.");
            try
            {
                if (_engine._system != System || _engine._environment != Environment ||
                    _engine._physicsWorld != PhysicsWorld)
                    throw new InvalidOperationException("The static-object service generation changed.");
                _engine.ValidateStaticObjectState(ThreeDEngine, TerrainModule, PhysicsModule);
                if (Marshal.ReadIntPtr(ThreeDEngine) != _terrainVtable)
                    throw new InvalidOperationException("The static-object service implementation changed.");
            }
            catch (Exception error) { Poison(error); throw; }
        }

        internal void ThrowIfFaulted()
        {
            _engine.VerifyAccess();
            _engine.VerifyAvailable();
            if (_released || !ReferenceEquals(_engine._staticObjectBorrower, this))
                throw new ObjectDisposedException(nameof(StaticObjectLease));
        }

        internal void Poison(Exception error) => _engine.Poison(error);

        internal void RequireTerrainEntityGrid() => _engine.RequireTerrainEntityGrid();

        internal void Release()
        {
            RequireActiveOperation();
            _engine._staticObjectBorrower = null;
            Resource = null;
            _released = true;
        }
    }
}

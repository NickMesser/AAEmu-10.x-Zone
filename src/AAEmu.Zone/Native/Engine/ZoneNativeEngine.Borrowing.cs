using System.Runtime.InteropServices;

using AAEmu.Zone.Native.Physics;

namespace AAEmu.Zone.Native.Engine;

public sealed partial class ZoneNativeEngine
{
    private bool _operationActive;
    private PhysicsLease? _physicsBorrower;
    private readonly HashSet<StreamLease> _streamBorrowers = [];
    private readonly HashSet<ushort> _physicalIdReservations = [];

    /// <summary>
    /// Borrows this engine's exact, initially empty physical world. The returned adapter owns
    /// its terrain and plain bodies, not the world. Retire it before disposing the engine.
    /// This admission precedes physics stepping/workers and queued scene-query integration.
    /// </summary>
    public ZoneNativeTerrainPhysicsWorld CreatePhysicsWorld()
    {
        using var operation = EnterOperation();
        if (_physicsBorrower is not null)
            throw new InvalidOperationException("This engine already has a physical-world borrower.");
        var module = RequiredModule("cryphysics_dedicate.dll");
        ValidatePhysicsState(module);
        RequireEmptyTerrain();
        var lease = new PhysicsLease(this, module);
        _physicsBorrower = lease; // Keep partial native/resource ownership rooted on failure.
        try
        {
            var adapter = ZoneNativeTerrainPhysicsWorld.Borrow(lease);
            lease.Resource = adapter;
            return adapter;
        }
        catch (Exception error) { Poison(error); throw; }
    }

    /// <summary>
    /// Borrows the real CSystem stream service. Requests own their native buffers and callbacks;
    /// the adapter must drain and retire them before the engine can shut down.
    /// </summary>
    public ZoneNativeTerrainStreams CreateTerrainStreams()
    {
        using var operation = EnterOperation();
        var stream = ZoneNativePhysicsModule.Virtual<PointerCall>(_system, 0x230)(_system);
        ValidateStreamState(stream);
        var lease = new StreamLease(this, stream);
        _streamBorrowers.Add(lease);
        try
        {
            var adapter = new ZoneNativeTerrainStreams(lease);
            lease.Resource = adapter;
            return adapter;
        }
        catch (Exception error) { Poison(error); throw; }
    }

    private EngineOperation EnterOperation()
    {
        VerifyAccess();
        VerifyAvailable(); // Reject stale handles before reading any native memory.
        if (_operationActive)
            throw new InvalidOperationException("Native engine operations cannot reenter an active operation or callback.");
        _operationActive = true;
        return new EngineOperation(this);
    }

    private void VerifyAvailable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_fault is not null || !_ready || _disposing)
            throw new InvalidOperationException("The native engine is unavailable or faulted; its process lifetime remains retained.", _fault);
        try
        {
            _context!.ThrowIfFaulted();
            if (_usesGameFramework) RequireGameServicePublication();
            _gameFlowNodes?.ThrowIfFaulted();
        }
        catch (Exception error) { Poison(error); throw; }
    }

    private void Poison(Exception error) => Interlocked.CompareExchange(ref _fault, error, null);

    private void ValidatePhysicsState(nint module)
    {
        if (Marshal.ReadIntPtr(module + 0x37c6d8) != _environment ||
            Marshal.ReadIntPtr(_environment + 0x70) != _physicsWorld ||
            _physicsWorld == 0 || Marshal.ReadIntPtr(_physicsWorld) != module + 0x1f0af8)
            throw new InvalidOperationException("The borrowed native world no longer belongs to this engine environment.");
        if (Marshal.ReadInt32(_physicsWorld + 0x5798) != 0)
            throw new InvalidOperationException("Borrowed physics requires the pre-update world with no native physics workers.");
        // Queries are synchronous on this same gate. Explicit static loading completion has
        // its own zero-time/queue/maintenance admission. Actor frames, queued queries and
        // worker producers are not exposed; worker count alone is not a drain guarantee.
    }

    private void ValidateStreamState(nint stream)
    {
        if (stream == 0 || Marshal.ReadIntPtr(_system + 0xa80) != stream ||
            Marshal.ReadIntPtr(stream) != _systemModule + 0x11ace8)
            throw new InvalidOperationException("The borrowed stream service no longer belongs to this CSystem.");
    }

    private void RequireNoBorrowers()
    {
        if (_operationActive || _physicsBorrower is not null || _streamBorrowers.Count != 0 ||
            _entityBorrower is not null || _staticObjectBorrower is not null)
            throw new InvalidOperationException("Finish active engine operations and retire all physics/stream/entity/static-object borrowers before shutting down CSystem.");
        // These bridges retain real P1/extension owners and graph listeners. Empty ordinary
        // resource-lease lists do not prove those native callback sources have retired.
        _flowActorRegistry?.VerifyNoLiveBindingsForShutdown();
        VerifyFlowTornadoBindingsForShutdown();
    }

    private sealed class EngineOperation(ZoneNativeEngine owner) : IDisposable
    {
        private ZoneNativeEngine? _owner = owner;
        public void Dispose()
        {
            if (_owner is not { } current) return;
            current.VerifyAccess();
            _owner = null;
            current._operationActive = false;
        }
    }

    /// <summary>Internal engine-issued capability; an arbitrary native pointer is not adoption.</summary>
    internal sealed partial class PhysicsLease
    {
        private readonly ZoneNativeEngine _engine;
        internal ZoneNativeEngine Engine => _engine;
        private bool _released;
        internal nint World { get; }
        internal nint System { get; }
        internal nint Environment { get; }
        internal nint ModuleHandle { get; }
        internal string BinaryDirectory => _engine._options.BinaryDirectory;
        internal HashSet<ushort> ReservedLivingIds => _engine._physicalIdReservations;
        internal ZoneNativeTerrainPhysicsWorld? Resource { get; set; }

        internal PhysicsLease(ZoneNativeEngine engine, nint module)
        {
            _engine = engine;
            World = engine._physicsWorld;
            System = engine._system;
            Environment = engine._environment;
            ModuleHandle = module;
        }

        internal IDisposable Enter()
        {
            var operation = _engine.EnterOperation();
            try
            {
                VerifyActive();
                try { _engine.ValidatePhysicsState(ModuleHandle); }
                catch (Exception error) { Poison(error); throw; }
                return operation;
            }
            catch { operation.Dispose(); throw; }
        }

        internal void ThrowIfFaulted()
        {
            _engine.VerifyAccess();
            _engine.VerifyAvailable();
            VerifyActive();
        }

        internal void ValidateForeignLease(IZoneNativePhysicsForeignDataLease lease, uint entityId)
        {
            VerifyActive();
            if (lease is not IZoneNativeEnginePhysicsForeignDataLease owned || !ReferenceEquals(owned.Engine, _engine))
                throw new InvalidOperationException("Borrowed actor physics requires this engine's exclusive IEntity/body association capability.");
            if (owned.EntityId != entityId)
                throw new InvalidOperationException("The physical body's identity must match its exact engine entity owner.");
        }

        internal void Poison(Exception error) => _engine.Poison(error);

        internal void Release()
        {
            _engine.VerifyAccess();
            VerifyActive();
            if (!_engine._operationActive)
                throw new InvalidOperationException("Retire the physical-world borrower under its active engine operation.");
            _engine._physicsBorrower = null;
            Resource = null;
            _released = true;
            // The reservation ledger belongs to the still-live native world generation.
        }

        private void VerifyActive()
        {
            if (_released || !ReferenceEquals(_engine._physicsBorrower, this))
                throw new ObjectDisposedException(nameof(PhysicsLease));
        }
    }

    internal sealed class StreamLease
    {
        private readonly ZoneNativeEngine _engine;
        private bool _released;
        internal nint StreamEngine { get; }
        internal nint SystemModule => _engine._systemModule;
        internal ZoneNativeTerrainStreams? Resource { get; set; }

        internal StreamLease(ZoneNativeEngine engine, nint stream) { _engine = engine; StreamEngine = stream; }

        internal IDisposable Enter()
        {
            var operation = _engine.EnterOperation();
            try
            {
                VerifyActive();
                try { _engine.ValidateStreamState(StreamEngine); }
                catch (Exception error) { Poison(error); throw; }
                return operation;
            }
            catch { operation.Dispose(); throw; }
        }

        internal void ThrowIfFaulted()
        {
            _engine.VerifyAccess();
            _engine.VerifyAvailable();
            VerifyActive();
        }

        internal void Poison(Exception error) => _engine.Poison(error);

        internal void Release()
        {
            _engine.VerifyAccess();
            VerifyActive();
            if (!_engine._operationActive)
                throw new InvalidOperationException("Retire the stream borrower under its active engine operation.");
            _engine._streamBorrowers.Remove(this);
            Resource = null;
            _released = true;
        }

        private void VerifyActive()
        {
            if (_released || !_engine._streamBorrowers.Contains(this))
                throw new ObjectDisposedException(nameof(StreamLease));
        }
    }
}

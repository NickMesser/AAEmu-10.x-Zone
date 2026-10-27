using System.Runtime.InteropServices;

using AAEmu.Zone.Native.Entities;

namespace AAEmu.Zone.Native.Engine;

public sealed partial class ZoneNativeEngine
{
    private EntityLease? _entityBorrower;

    /// <summary>
    /// Borrows this engine's actual EntitySystem and character services. The adapter owns its
    /// exact native entities and slots and must retire them before CSystem's EntitySystem shutdown.
    /// This does not create the replacement GameObject/ActorSystem or admit a player to World.
    /// </summary>
    public ZoneNativeEntityServices CreateEntityServices()
    {
        using var operation = EnterOperation();
        if (_entityBorrower is not null)
            throw new InvalidOperationException("This engine already has an entity-service borrower.");
        var entities = Marshal.ReadIntPtr(_environment + 0x88);
        if (entities == 0) throw new InvalidOperationException("CSystem has no live entity service.");
        var lease = new EntityLease(this, entities, RequiredModule("cryentitysystem_dedicate.dll"),
            RequiredModule("cryanimation.dll"));
        _entityBorrower = lease;
        try
        {
            // Construction only captures/validates services. Resource allocation begins after
            // the strongly rooted adapter has been returned to the caller.
            var services = new ZoneNativeEntityServices(lease);
            lease.Resource = services;
            return services;
        }
        catch (Exception error) { Poison(error); throw; }
    }

    internal sealed class EntityLease
    {
        private readonly ZoneNativeEngine _engine;
        private readonly nint _entityVtable;
        private readonly nint _characterManager;
        private bool _released;
        internal ZoneNativeEngine Engine => _engine;
        internal nint EntitySystem { get; }
        internal nint EntityModule { get; }
        internal nint CharacterModule { get; }
        internal nint SystemModule { get; }
        internal nint TerrainModule { get; }
        internal nint PhysicsModule { get; }
        internal nint System { get; }
        internal nint Environment { get; }
        internal ZoneNativeEntityServices? Resource { get; set; }

        internal EntityLease(ZoneNativeEngine engine, nint entities, nint entityModule, nint characterModule)
        {
            _engine = engine;
            EntitySystem = entities;
            EntityModule = entityModule;
            CharacterModule = characterModule;
            // Cell XML/proxy services use the same identity-checked modules already owned by
            // CSystem. Resolve them through this engine, never from an external native pointer.
            SystemModule = engine.RequiredModule("crysystem.dll");
            TerrainModule = engine.RequiredModule("cry3dengine_dedicate.dll");
            PhysicsModule = engine.RequiredModule("cryphysics_dedicate.dll");
            System = engine._system;
            Environment = engine._environment;
            _entityVtable = Marshal.ReadIntPtr(entities);
            _characterManager = Marshal.ReadIntPtr(Environment + 0xa8);
        }

        internal IDisposable Enter()
        {
            var operation = _engine.EnterOperation();
            try { RequireActiveOperation(); return operation; }
            catch { operation.Dispose(); throw; }
        }

        /// <summary>
        /// Internal foreign-body lease work runs inside physics' already-held engine operation.
        /// Validate the same owner and service generation without recursively entering that gate.
        /// </summary>
        internal void RequireActiveOperation()
        {
            ThrowIfFaulted(); // Reject stale managed owners before native memory access.
            if (!_engine._operationActive)
                throw new InvalidOperationException("Entity ownership changes require the active shared engine operation.");
            try
            {
                if (_engine._system != System || _engine._environment != Environment ||
                    Marshal.ReadIntPtr(Environment) != System ||
                    Marshal.ReadIntPtr(Environment + 0x88) != EntitySystem ||
                    Marshal.ReadIntPtr(EntitySystem) != _entityVtable ||
                    _characterManager == 0 || Marshal.ReadIntPtr(Environment + 0xa8) != _characterManager)
                    throw new InvalidOperationException("The borrowed entity/character service no longer belongs to this engine generation.");
            }
            catch (Exception error) { Poison(error); throw; }
        }

        internal void ThrowIfFaulted()
        {
            _engine.VerifyAccess();
            _engine.VerifyAvailable();
            if (_released || !ReferenceEquals(_engine._entityBorrower, this))
                throw new ObjectDisposedException(nameof(EntityLease));
        }

        internal void Poison(Exception error) => _engine.Poison(error);

        internal void Release()
        {
            RequireActiveOperation();
            _engine._entityBorrower = null;
            Resource = null;
            _released = true;
        }
    }
}

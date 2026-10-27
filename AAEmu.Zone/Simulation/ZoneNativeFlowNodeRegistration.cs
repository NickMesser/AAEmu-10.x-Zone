using System.Runtime.InteropServices;
using System.Text;

namespace AAEmu.Zone.Simulation;

/// <summary>
/// Registers supplied real factories through CFlowSystem+30 (30561500). This owner creates
/// neither factories nor nodes. Its caller supplies a verified CryAction image and the current
/// engine-owned FlowSystem, and serializes registration under the engine operation gate.
/// </summary>
internal sealed unsafe class ZoneNativeFlowNodeRegistration
{
    private const int MaximumTypes = 4096;
    private const int FlowSystemTableRva = 0x230148;
    private const int RegisterTypeRva = 0x61500;

    // Actual X2 CRT/list order, not native type IDs. The singleton caches belong to their
    // respective factory owners and are independent of FlowSystem's factory references.
    private static readonly string[] RequiredNames =
    [
        "Crysis:TornadoWander",
        "Game:ActorSensor",
        "Game:DifficultyLevel",
        "Camera:OverrideFOV",
        "CrysisFX:ScreenFader",
        "Crysis:HitInfo",
        "Crysis:ExplosionInfo",
        "Crysis:FreezeEntity",
        "Game:PlayerStaging",
        "Game:PlayerLink",
        "CrysisFX:PostFXGlobal",
        "CrysisFX:PostFXScreenFrost",
        "CrysisFX:PostFXKuwaharaFilter",
        "CrysisFX:PostFXWaterDroplets",
        "CrysisFX:PostFXBloodSplats",
        "CrysisFX:PostFXGlow",
        "CrysisFX:PostFXGlittering",
        "Multiplayer:MP",
        "Multiplayer:GameType",
        "X2UI:ScreenFader"
    ];

    private static readonly byte[] InvalidTypeName = "InvalidType\0"u8.ToArray();
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private readonly Factory[] _factories;
    private readonly ushort[] _registeredIds;
    private GCHandle _lifetimeRoot;
    private nint _flowSystem;
    private nint _cryActionModule;
    private Exception? _fault;
    private Phase _phase;
    private bool _adopted;
    private bool _operationActive;

    /// <summary>
    /// A successful constructor transfers exclusive cleanup responsibility for all supplied
    /// factory owners. On constructor failure the caller still owns every supplied factory.
    /// Construction performs only read-only interface admission and managed allocation.
    /// </summary>
    internal ZoneNativeFlowNodeRegistration(IReadOnlyList<IZoneNativeFlowNodeFactoryOwner> factories)
    {
        ArgumentNullException.ThrowIfNull(factories);
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("Native flow registration requires Windows x64.");
        if (factories.Count != RequiredNames.Length)
            throw new ArgumentException("Supply all 20 real X2 flow-node factories in their native registration order.", nameof(factories));

        var captured = new Factory[RequiredNames.Length];
        var owners = new HashSet<IZoneNativeFlowNodeFactoryOwner>(ReferenceEqualityComparer.Instance);
        var pointers = new HashSet<nint>();
        for (var i = 0; i < captured.Length; i++)
        {
            var owner = factories[i] ?? throw new ArgumentException("A real factory owner is missing.", nameof(factories));
            if (!string.Equals(owner.Name, RequiredNames[i], StringComparison.Ordinal) || !owners.Add(owner))
                throw new ArgumentException($"Factory {i} must exclusively own '{RequiredNames[i]}'.", nameof(factories));
            owner.ValidateForRegistration();
            owner.ThrowIfFaulted();
            var pointer = owner.FactoryPointer;
            if (pointer == 0 || !pointers.Add(pointer))
                throw new ArgumentException("Each factory must expose distinct, real native interface storage.", nameof(factories));
            var table = Marshal.ReadIntPtr(pointer);
            Require(table != 0, "A supplied factory has no native interface table.");
            var methods = new nint[4];
            for (var slot = 0; slot < methods.Length; slot++)
            {
                methods[slot] = Marshal.ReadIntPtr(table + slot * 8);
                Require(methods[slot] != 0, "A supplied factory has an incomplete native interface.");
            }
            Require(string.Equals(owner.Name, RequiredNames[i], StringComparison.Ordinal) && owner.FactoryPointer == pointer,
                "A factory changed its identity during read-only construction admission.");
            captured[i] = new Factory(owner, RequiredNames[i], pointer, table, methods,
                Encoding.UTF8.GetBytes(RequiredNames[i] + '\0'));
        }
        _factories = captured;
        _registeredIds = new ushort[captured.Length];
    }

    /// <summary>
    /// One-time transfer to an engine. The engine assigns its ownership field only after this
    /// succeeds; a rejected second adoption must not clean up the first engine's resources.
    /// </summary>
    internal void AdoptByEngine()
    {
        BeginOperation();
        try
        {
            Require(!_adopted && _phase == Phase.Unpublished, "The registration owner has already been adopted or used.");
            _adopted = true;
        }
        finally { _operationActive = false; }
    }

    /// <summary>
    /// Invoked once by the real IGame+10 callback. Native registration copies each name,
    /// retains the registry reference and consumes the passed smart-pointer reference.
    /// Any uncertain callback/call result permanently prevents replay and retains this owner.
    /// </summary>
    internal void Register(nint flowSystem, nint verifiedCryActionModule)
    {
        BeginOperation();
        try
        {
            Require(_adopted && _phase == Phase.Unpublished, "Register the adopted factory owner exactly once.");
            RetainLifetime();
            _phase = Phase.Registering;
            _flowSystem = flowSystem;
            _cryActionModule = verifiedCryActionModule;
            ValidateFactories();
            var before = ReadRegistry();
            var additional = _factories.Count(factory => Find(before, factory.NameBytes) < 0);
            Require(before.Length + additional <= MaximumTypes,
                "CFlowSystem has no room for the complete X2 factory set.");
            var register = Marshal.GetDelegateForFunctionPointer<RegisterTypeCall>(_cryActionModule + RegisterTypeRva);

            for (var index = 0; index < _factories.Length; index++)
            {
                var factory = _factories[index];
                var existing = Find(before, factory.NameBytes);
                var expectedId = existing < 0 ? before.Length : existing;
                factory.Reference = ReferencePhase.AcquireStarted;
                // A supplied reverse thunk can originate from a different managed delegate
                // type with this exact ABI. Invoke its unmanaged entrypoint directly instead
                // of asking Marshal to cast the original delegate to a private wrapper type.
                ((delegate* unmanaged[Stdcall]<nint, void>)factory.Methods[0])(factory.Pointer);
                factory.Reference = ReferencePhase.Acquired;
                ValidateFactories();
                RequireSameRegistry(before, ReadRegistry());

                var passedReference = factory.Pointer;
                ushort id;
                fixed (byte* name = factory.NameBytes)
                {
                    factory.Reference = ReferencePhase.RegistrationStarted;
                    id = register(_flowSystem, (nint)name, &passedReference, 2);
                    //30561500 releases this passed reference on every normal return. It does
                    // not clear the storage. Never add a caller-side finally/Release here.
                    factory.Reference = ReferencePhase.Consumed;
                }
                ValidateFactories();
                Require(passedReference == factory.Pointer, "Native registration changed the passed smart-pointer storage.");
                var after = ReadRegistry();
                Require(id == expectedId && after.Length == before.Length + (existing < 0 ? 1 : 0),
                    "Native registration did not preserve the expected type ID and registry count.");
                for (var row = 0; row < before.Length; row++)
                {
                    if (row == existing)
                        Require(after[row].Name == before[row].Name, "An override replaced the existing native name storage.");
                    else
                        Require(after[row] == before[row], "Registration changed an unrelated native factory record.");
                }
                Require(after[id].Factory == factory.Pointer && NameEquals(after[id].Name, factory.NameBytes) &&
                    Find(after, factory.NameBytes) == id,
                    "The returned native type ID does not identify the supplied real factory.");
                _registeredIds[index] = id;
                factory.Reference = ReferencePhase.Registered;
                before = after;
            }
            _phase = Phase.Registered;
        }
        catch (Exception error)
        {
            if (_phase == Phase.Registering) Poison(error);
            throw;
        }
        finally { _operationActive = false; }
    }

    internal void ThrowIfFaulted()
    {
        VerifyThread();
        if (_fault is not null)
            throw new InvalidOperationException("Native flow-node registration ownership is uncertain and remains retained.", _fault);
        foreach (var factory in _factories) factory.Owner.ThrowIfFaulted();
    }

    internal IReadOnlyList<ushort> RegisteredTypeIds
    {
        get
        {
            ThrowIfFaulted();
            Require(_phase == Phase.Registered, "Native factory IDs are available only after complete registration.");
            return Array.AsReadOnly(_registeredIds);
        }
    }

    /// <summary>
    /// Construction rollback only. An adopting engine may call this before its process Claim,
    /// never after publishing native callbacks or entering native startup.
    /// </summary>
    internal void ReleaseUnpublished()
    {
        BeginOperation();
        try
        {
            Require(_phase == Phase.Unpublished, "A used factory owner requires actual native teardown.");
            RetainLifetime();
            _phase = Phase.Releasing;
            for (var i = _factories.Length - 1; i >= 0; i--)
            {
                _factories[i].Owner.ReleaseUnpublished();
                ThrowIfFaulted();
            }
            CompleteRelease();
        }
        catch (Exception error)
        {
            if (_phase == Phase.Releasing) Poison(error);
            throw;
        }
        finally { _operationActive = false; }
    }

    /// <summary>
    /// Requires actual completed framework shutdown. The former FlowSystem/CSystem are dead;
    /// only the real factory owners may retire cached nodes using their proved remaining services.
    /// </summary>
    internal void RetireAfterFrameworkShutdown()
    {
        BeginOperation();
        try
        {
            Require(_phase == Phase.Registered, "Retire a completely registered owner after actual framework shutdown.");
            _phase = Phase.Retiring;
            // Original module on-exit cleanup runs in reverse construction order. Do not read
            // the old FlowSystem to infer whether it has been destroyed.
            for (var i = _factories.Length - 1; i >= 0; i--)
            {
                _factories[i].Owner.RetireAfterFrameworkShutdown();
                ThrowIfFaulted();
            }
            _flowSystem = 0;
            _phase = Phase.Retired;
        }
        catch (Exception error)
        {
            if (_phase == Phase.Retiring) Poison(error);
            throw;
        }
        finally { _operationActive = false; }
    }

    internal void ReleaseAfterNativeModules()
    {
        BeginOperation();
        try
        {
            Require(_phase == Phase.Retired, "Retire factory-owned nodes before releasing native modules and callback storage.");
            _phase = Phase.Releasing;
            for (var i = _factories.Length - 1; i >= 0; i--)
            {
                _factories[i].Owner.ReleaseAfterNativeModules();
                ThrowIfFaulted();
            }
            CompleteRelease();
        }
        catch (Exception error)
        {
            if (_phase == Phase.Releasing) Poison(error);
            throw;
        }
        finally { _operationActive = false; }
    }

    private void ValidateFactories()
    {
        ThrowIfFaulted();
        foreach (var factory in _factories)
        {
            factory.Owner.ValidateForRegistration();
            factory.Owner.ThrowIfFaulted();
            Require(factory.Owner.FactoryPointer == factory.Pointer &&
                string.Equals(factory.Owner.Name, factory.Name, StringComparison.Ordinal) &&
                Marshal.ReadIntPtr(factory.Pointer) == factory.Table,
                "A factory changed its retained native identity.");
            for (var slot = 0; slot < factory.Methods.Length; slot++)
                Require(Marshal.ReadIntPtr(factory.Table + slot * 8) == factory.Methods[slot],
                    "A factory changed a retained native callback.");
        }
    }

    private RegistryRecord[] ReadRegistry()
    {
        Require(_flowSystem != 0 && _cryActionModule != 0 &&
            Marshal.ReadIntPtr(_flowSystem) == _cryActionModule + FlowSystemTableRva &&
            Marshal.ReadIntPtr(_cryActionModule + FlowSystemTableRva + 0x30) == _cryActionModule + RegisterTypeRva,
            "The current FlowSystem does not match the verified CryAction registration implementation.");
        var begin = (nuint)Marshal.ReadIntPtr(_flowSystem + 0x48);
        var end = (nuint)Marshal.ReadIntPtr(_flowSystem + 0x50);
        var capacity = (nuint)Marshal.ReadIntPtr(_flowSystem + 0x58);
        Require(begin != 0 && end >= begin && capacity >= end &&
            (end - begin) % 16 == 0 && (capacity - begin) % 16 == 0 &&
            (end - begin) / 16 > 0 && (end - begin) / 16 <= MaximumTypes,
            "CFlowSystem has an invalid native type registry.");
        var records = new RegistryRecord[(int)((end - begin) / 16)];
        for (var i = 0; i < records.Length; i++)
        {
            var address = (nint)begin + i * 16;
            records[i] = new RegistryRecord(Marshal.ReadIntPtr(address), Marshal.ReadIntPtr(address + 8));
            Require(records[i].Name != 0, "A native type registry record has no name storage.");
        }
        Require(NameEquals(records[0].Name, InvalidTypeName) && records[0].Factory == 0,
            "CFlowSystem has not retained its actual InvalidType constructor record.");
        return records;
    }

    private static int Find(RegistryRecord[] records, byte[] name)
    {
        var found = -1;
        for (var i = 0; i < records.Length; i++)
        {
            if (!NameEquals(records[i].Name, name)) continue;
            Require(found < 0, "The native type registry contains duplicate physical name records.");
            found = i;
        }
        return found;
    }

    private static bool NameEquals(nint pointer, byte[] bytes)
    {
        for (var i = 0; i < bytes.Length; i++)
            if (Marshal.ReadByte(pointer, i) != bytes[i]) return false;
        return true;
    }

    private static void RequireSameRegistry(RegistryRecord[] expected, RegistryRecord[] actual)
    {
        Require(expected.Length == actual.Length, "A reference callback changed the native registry count.");
        for (var i = 0; i < expected.Length; i++)
            Require(expected[i] == actual[i], "A reference callback changed the native registry.");
    }

    private void BeginOperation()
    {
        VerifyThread();
        if (_operationActive)
        {
            var error = new InvalidOperationException("Native flow-node ownership does not permit reentrant operations.");
            Poison(error);
            throw error;
        }
        _operationActive = true;
        try
        {
            ThrowIfFaulted();
            Require(_phase != Phase.Released, "The factory owner has already been released.");
        }
        catch
        {
            _operationActive = false;
            throw;
        }
    }

    private void RetainLifetime()
    {
        if (!_lifetimeRoot.IsAllocated) _lifetimeRoot = GCHandle.Alloc(this);
    }

    private void Poison(Exception error)
    {
        _fault ??= error;
        _phase = Phase.Faulted;
        RetainLifetime();
    }

    private void CompleteRelease()
    {
        _cryActionModule = 0;
        _phase = Phase.Released;
        if (_lifetimeRoot.IsAllocated) _lifetimeRoot.Free();
    }

    private void VerifyThread()
    {
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new InvalidOperationException("Native flow-node ownership belongs to the engine thread.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Factory
    {
        internal readonly IZoneNativeFlowNodeFactoryOwner Owner;
        internal readonly string Name;
        internal readonly byte[] NameBytes;
        internal readonly nint Pointer;
        internal readonly nint Table;
        internal readonly nint[] Methods;
        internal ReferencePhase Reference;

        internal Factory(IZoneNativeFlowNodeFactoryOwner owner, string name, nint pointer, nint table, nint[] methods, byte[] nameBytes)
        {
            Owner = owner;
            Name = name;
            NameBytes = nameBytes;
            Pointer = pointer;
            Table = table;
            Methods = methods;
        }
    }

    private readonly record struct RegistryRecord(nint Name, nint Factory);
    private enum Phase { Unpublished, Registering, Registered, Retiring, Retired, Releasing, Released, Faulted }
    private enum ReferencePhase { None, AcquireStarted, Acquired, RegistrationStarted, Consumed, Registered }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate ushort RegisterTypeCall(nint flowSystem, nint name, nint* factory, int selector);
}

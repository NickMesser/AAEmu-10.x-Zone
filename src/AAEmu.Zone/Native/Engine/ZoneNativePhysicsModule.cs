using System.ComponentModel;
using System.Reflection.PortableExecutable;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

using AAEmu.Zone.Native.Physics;

namespace AAEmu.Zone.Native.Engine;

// One original module owns a global environment, factory list and event listener. Their roots
// survive individual worlds. There is deliberately no FreeLibrary or finalizer path.
internal sealed unsafe class ZoneNativePhysicsModule
{
    private static readonly object ModuleGate = new();
    private static ZoneNativePhysicsModule? _instance;
    private static Exception? _loadFailure;
    private readonly List<FileStream> _identityLocks = [];
    private readonly List<Delegate> _hostDelegates = [];
    private readonly List<nint> _hostAllocations = [];
    private readonly Dictionary<nint, FactoryRecord> _factories = [];
    private readonly HashSet<nint> _listeners = [];
    private Exception? _hostError;
    private ZoneNativeTerrainPhysicsWorld? _activeOwner;
    private nint _handle;
    internal nint System { get; private set; }
    internal nint Environment { get; private set; }
    internal string DirectoryPath { get; }
    internal FactoryCall Factory { get; private set; } = null!;
    internal FreeCall Delete { get; private set; } = null!;
    internal FreeCall DeleteArray { get; private set; } = null!;
    internal nint Handle => _handle;
    internal PlaceholderCall IsPlaceholder { get; private set; } = null!;
    internal FreeCall PurgeRetiredEvents { get; private set; } = null!;

    // Exact configured dependency set actually loaded by the original isolated Windows probe.
    private static readonly (string Name, string Sha256)[] Identities =
    [
        ("cryphysics_dedicate.dll", "B558CD33B93177D3163ECF483368E3C7A0612507257AD73BC87A73EE872E2E20"),
        ("xlcommon.dll", "D81BA53E5DF0DC6B5031D3D423A6A2720288D6CA40BE0768AAE85E2F30EA4113"),
        ("MSVCP100.dll", "5BB420FBE28315F2117376052BB8488CE84A3398DDA65005B8AE1F792017E9A8"),
        ("tbbmalloc_proxy.dll", "F003D7DB853A92B8A1F254676A414D9199CFB0AE72E8560B9552A476156AE80E"),
        ("MSVCR100.dll", "1D1A1AE540BA132F998D60D3622F0297B6E86AE399332C3B47462D7C0F560A36"),
        ("xldiag.dll", "7E7CF632CC87AEB4B641231EDD52EE369D749FDDDD6B56B5013CFACABAB983E0"),
        ("dbghelp.dll", "13F0C74845318B52B76E6000564B1A99C37DE48422B44AC74D034FA222C65A23"),
        ("tbbmalloc.dll", "DABF31E107CE884063087B2ED47616BAA2AF915554284ECFA73109162FFA1B82"),
        ("tbb.dll", "6BA883C5821A25D6FBFD1C1F788C63ABE7DE66AAE23B26328B97FB2365A6D2FC")
    ];

    private ZoneNativePhysicsModule(string directory) => DirectoryPath = directory;

    internal static ZoneNativePhysicsModule Acquire(string directory, ZoneNativeTerrainPhysicsWorld owner)
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("The original terrain physics backend requires Windows x64.");
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        lock (ModuleGate)
        {
            if (_loadFailure is not null)
                throw new InvalidOperationException("Native physics module initialization failed; its process lifetime is retained.", _loadFailure);
            if (_instance is null)
            {
                var candidate = new ZoneNativePhysicsModule(directory);
                candidate.ValidateFiles(); // No native state or persistent roots exist if admission fails.
                try { ZoneNativeProcessOwnership.Claim(candidate, "standalone CryPhysics"); }
                catch
                {
                    foreach (var stream in candidate._identityLocks) stream.Dispose();
                    candidate._identityLocks.Clear();
                    throw;
                }
                _instance = candidate; // Root before native loading or reverse callbacks can occur.
                try { candidate.Initialize(); }
                catch (Exception error)
                {
                    _loadFailure = error;
                    throw;
                }
            }
            if (!string.Equals(_instance.DirectoryPath, directory, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("This process already owns physics from another binary directory.");
            if (_instance._activeOwner is not null)
                throw new InvalidOperationException("Only one active native physics world is supported per process.");
            _instance._activeOwner = owner; // Also retains faulted/leaked owners and callback roots.
            return _instance;
        }
    }

    // A real CSystem already owns this image and its environment. This path only validates and
    // borrows it: no factory, module loading, standalone host allocation or global writes occur.
    internal static ZoneNativePhysicsModule Borrow(ZoneNativeEngine.PhysicsLease lease)
    {
        var module = new ZoneNativePhysicsModule(Path.TrimEndingDirectorySeparator(Path.GetFullPath(lease.BinaryDirectory)));
        try
        {
            module.ValidateFiles(borrowed: true);
            module._handle = GetModuleHandleW(Identities[0].Name);
            if (module._handle == 0 || module._handle != lease.ModuleHandle)
                throw new InvalidOperationException("The engine's CryPhysics module identity changed.");
            module.System = lease.System;
            module.Environment = lease.Environment;
            module.ValidateWorld(lease.World);
            if (Marshal.ReadIntPtr(module._handle + 0x37c6d8) != lease.Environment ||
                Marshal.ReadIntPtr(lease.Environment) != lease.System ||
                Marshal.ReadIntPtr(lease.Environment + 0x70) != lease.World)
                throw new InvalidOperationException("The borrowed physical world does not belong to the captured CSystem environment.");
            module.BindRetirementFunctions();
            return module;
        }
        catch
        {
            module.ReleaseBorrowedIdentityLocks();
            throw;
        }
    }

    internal void ReleaseBorrowedIdentityLocks()
    {
        foreach (var stream in _identityLocks) stream.Dispose();
        _identityLocks.Clear();
    }

    private void ValidateFiles(bool borrowed = false)
    {
        try
        {
            foreach (var (name, expected) in Identities)
            {
                var path = Path.Combine(DirectoryPath, name);
                var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                _identityLocks.Add(stream);
                var digest = Convert.ToHexString(SHA256.HashData(stream));
                if (!string.Equals(digest, expected, StringComparison.Ordinal))
                    throw new InvalidDataException($"Unsupported native physics binary identity: {name}.");
                stream.Position = 0;
                using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
                if (pe.PEHeaders.CoffHeader.Machine != Machine.Amd64)
                    throw new InvalidDataException($"Native dependency is not Windows x64: {name}.");
                var existing = GetModuleHandleW(name);
                if (existing == 0)
                {
                    if (borrowed) throw new InvalidOperationException($"The engine has not loaded the required native dependency: {name}.");
                    continue;
                }
                if (!borrowed && name == "cryphysics_dedicate.dll")
                    throw new InvalidOperationException("CryPhysics is already loaded outside this backend's ownership.");
                ValidateLoadedPath(existing, path);
            }
        }
        catch
        {
            foreach (var stream in _identityLocks) stream.Dispose();
            _identityLocks.Clear();
            throw;
        }
    }

    private void Initialize()
    {
        // Windows resolves dependencies from this DLL's directory and system directories. No
        // process current-directory or SetDllDirectory changes are made.
        _handle = LoadLibraryExW(Path.Combine(DirectoryPath, Identities[0].Name), 0, 0x900);
        if (_handle == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot load the original physics module.");
        foreach (var (name, _) in Identities)
        {
            var dependency = GetModuleHandleW(name);
            if (dependency == 0) throw new InvalidOperationException($"Native dependency was not loaded: {name}.");
            ValidateLoadedPath(dependency, Path.Combine(DirectoryPath, name));
        }
        var factory = GetProcAddress(_handle, "CreatePhysicalWorld");
        if (factory != _handle + 0x1dfef0) throw new InvalidDataException("Unexpected native physics factory address.");
        Factory = Marshal.GetDelegateForFunctionPointer<FactoryCall>(factory);
        BindRetirementFunctions();
        Environment = Allocate(0x1e0);
        var registry = Object(0x40, (0x30, new RegisterFactoriesCall(RegisterFactories)));
        var events = Object(0x20, (8, new ListenerCall(AddListener)), (16, new ListenerCall(RemoveListener)));
        var logger = Object(0xa0, (0x98, new FreeCall(FlushEmptyLog)));
        System = Object(0x558,
            (0x30, new PointerCall(_ => Environment)),
            (0x550, new PointerCall(_ => registry)),
            (0xb0, new IntegerCall(_ => 0)),
            (0x290, new PointerCall(_ => events)),
            (0x178, new IntegerCall(_ => 2)));
        Marshal.WriteIntPtr(Environment, System);
        Marshal.WriteIntPtr(Environment + 0xa0, logger);
        // +121/+122 keep native fresh-CSystem defaults. +70 is assigned the actual world after
        // construction. The native non-server ray-limit path reads that world's physics vars.
    }

    private static void ValidateLoadedPath(nint module, string expected)
    {
        var path = new StringBuilder(32768);
        var count = GetModuleFileNameW(module, path, path.Capacity);
        if (count == 0 || count >= path.Capacity ||
            !string.Equals(Path.GetFullPath(path.ToString()), expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Native dependency is loaded from an unsupported path: {Path.GetFileName(expected)}.");
    }

    private void BindRetirementFunctions()
    {
        Delete = Marshal.GetDelegateForFunctionPointer<FreeCall>(_handle + 0x1eafe2);
        DeleteArray = Marshal.GetDelegateForFunctionPointer<FreeCall>(_handle + 0x1eaf64);
        IsPlaceholder = Marshal.GetDelegateForFunctionPointer<PlaceholderCall>(_handle + 0x760c0);
        PurgeRetiredEvents = Marshal.GetDelegateForFunctionPointer<FreeCall>(_handle + 0x79b30);
    }

    private nint Allocate(int size)
    {
        var value = (nint)NativeMemory.AllocZeroed((nuint)size);
        if (value == 0) throw new OutOfMemoryException();
        _hostAllocations.Add(value);
        return value;
    }

    private nint Object(int tableSize, params (int Offset, Delegate Function)[] slots)
    {
        var table = Allocate(tableSize);
        var instance = Allocate(nint.Size);
        foreach (var (offset, function) in slots)
        {
            _hostDelegates.Add(function);
            Marshal.WriteIntPtr(table + offset, Marshal.GetFunctionPointerForDelegate(function));
        }
        Marshal.WriteIntPtr(instance, table);
        return instance;
    }

    // Registry36562FA0 accepts a linked list of (factory,next). Retain the actual factory identities
    // needed by this module; arbitrary engine-module construction/registry queries are not exposed.
    private void RegisterFactories(nint receiver, nint node)
    {
        try
        {
            var visited = new HashSet<nint>();
            while (node != 0)
            {
                if (!visited.Add(node) || visited.Count > 64)
                    throw new InvalidDataException("Invalid native factory registration list.");
                var factory = Marshal.ReadIntPtr(node);
                if (factory != 0)
                {
                    var name = Marshal.PtrToStringAnsi(Virtual<PointerCall>(factory, 0)(factory)) ?? "";
                    var classId = Marshal.PtrToStructure<Guid>(Virtual<PointerCall>(factory, 8)(factory));
                    nint interfaces = 0;
                    nuint count = 0;
                    Virtual<InterfacesCall>(factory, 0x10)(factory, &interfaces, &count);
                    if (count > 64 || (count != 0 && interfaces == 0))
                        throw new InvalidDataException("Invalid native factory interface list.");
                    var ids = new Guid[(int)count];
                    for (var i = 0; i < ids.Length; i++) ids[i] = Marshal.PtrToStructure<Guid>(interfaces + i * 16);
                    _factories[factory] = new(name, classId, ids);
                }
                node = Marshal.ReadIntPtr(node + 8);
            }
        }
        catch (Exception error) { RecordHostError(error); }
    }

    private byte AddListener(nint receiver, nint listener)
    {
        try { return (byte)(listener != 0 && _listeners.Add(listener) ? 1 : 0); }
        catch (Exception error) { RecordHostError(error); return 0; }
    }

    private byte RemoveListener(nint receiver, nint listener)
    {
        try { return (byte)(_listeners.Remove(listener) ? 1 : 0); }
        catch (Exception error) { RecordHostError(error); return 0; }
    }

    private static void FlushEmptyLog(nint receiver) { } // This host has no attached native log stream.
    private void RecordHostError(Exception error) => _hostError ??= error;
    internal void ThrowHostError()
    {
        if (_hostError is not null) ExceptionDispatchInfo.Capture(_hostError).Throw();
    }
    internal void SetWorld(nint world) => Marshal.WriteIntPtr(Environment + 0x70, world);
    internal void ValidateWorld(nint world)
    {
        if (world == 0 || Marshal.ReadIntPtr(world) != _handle + 0x1f0af8)
            throw new InvalidDataException("Unexpected native physical world identity.");
    }
    internal void ReleaseClaim(ZoneNativeTerrainPhysicsWorld owner)
    {
        lock (ModuleGate)
        {
            if (!ReferenceEquals(_activeOwner, owner)) throw new InvalidOperationException("Native world ownership changed.");
            SetWorld(0);
            _activeOwner = null;
        }
    }

    internal static T Virtual<T>(nint instance, int offset) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance) + offset));
    private sealed record FactoryRecord(string Name, Guid ClassId, Guid[] Interfaces);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate nint FactoryCall(nint system);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void FreeCall(nint pointer);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate ulong PlaceholderCall(nint world, nint entity);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint PointerCall(nint receiver);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int IntegerCall(nint receiver);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void RegisterFactoriesCall(nint receiver, nint nodes);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate byte ListenerCall(nint receiver, nint listener);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void InterfacesCall(nint receiver, nint* interfaces, nuint* count);
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint LoadLibraryExW(string path, nint file, uint flags);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandleW(string name);
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetModuleFileNameW(nint module, StringBuilder path, int size);
    [DllImport("kernel32", CharSet = CharSet.Ansi, ExactSpelling = true)] private static extern nint GetProcAddress(nint module, string name);
    [DllImport("kernel32")] internal static extern uint GetCurrentThreadId();
}

using System.ComponentModel;
using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

using AAEmu.Zone.Content;
using AAEmu.Zone.Hosting;
using AAEmu.Zone.Native.FlowNodes;

namespace AAEmu.Zone.Native.Engine;

/// <summary>
/// Owns one real dedicated CSystem, optionally created and retired by its real GameFramework.
/// Neither creation path starts X2Startup or the native Zone loop.
/// Create and dispose on ZoneSimulationThread. Borrowed physics and terrain streams share this
/// owner's admission gate; scene loading, update scheduling and gameplay admission remain separate.
/// The native DLL search directory and working directory belong to this dedicated process.
/// A failed native initialization/retirement pins the owner and requires process termination.
/// </summary>
public sealed unsafe partial class ZoneNativeEngine : IDisposable
{
    private readonly ZoneSimulationThread _thread;
    private readonly uint _nativeThread;
    private readonly ZoneNativeEngineOptions _options;
    private readonly bool _usesGameFramework;
    private readonly List<FileStream> _identityLocks = [];
    private readonly HashSet<string> _admittedModules = new(StringComparer.OrdinalIgnoreCase);
    private ZoneNativeEngineStartupContext? _context;
    private ZoneNativeEngineParameters? _parameters;
    private ZoneNativeEngineConsole? _console;
    private FileStream? _gamePak;
    private nint _commonModule;
    private nint _systemModule;
    private nint _system;
    private nint _environment;
    private nint _physicsWorld;
    private string? _originalWorkingDirectory;
    private VoidCall? _cleanupExceptionHandler;
    private bool _exceptionHandlerStarted;
    private bool _claimed;
    private bool _ready;
    private bool _disposing;
    private bool _disposed;
    private Exception? _fault;
    private Action<string>? _phaseTrace;

    private ZoneNativeEngine(ZoneNativeEngineOptions options, ZoneSimulationThread thread, bool usesGameFramework)
    {
        _options = options;
        _usesGameFramework = usesGameFramework;
        _thread = thread;
        _nativeThread = GetCurrentThreadId();
    }

    public static ZoneNativeEngine Create(ZoneNativeEngineOptions options) => CreateCore(options, false);

    /// <summary>
    /// Framework startup requires the complete game content and real FlowNode factory owners.
    /// The options-only form cannot supply IGame and is rejected before native admission.
    /// </summary>
    public static ZoneNativeEngine CreateWithFramework(ZoneNativeEngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        throw new NotSupportedException("Framework startup requires owned game content and all real X2 FlowNode factories. Use the internal game-service composition path once those factories are implemented.");
    }

    internal static ZoneNativeEngine CreateWithFramework(ZoneNativeEngineOptions options,
        ZoneModelContent models, ZoneModelAuxiliaryContent auxiliary, ZoneNativeFlowNodeRegistration flowNodes)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(auxiliary);
        ArgumentNullException.ThrowIfNull(flowNodes);
        return CreateCore(options, true, models, auxiliary, _ => flowNodes);
    }

    internal static ZoneNativeEngine CreateWithFramework(ZoneNativeEngineOptions options,
        ZoneModelContent models, ZoneModelAuxiliaryContent auxiliary,
        Func<ZoneNativeEngine, ZoneNativeFlowNodeRegistration> createFlowNodes)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(auxiliary);
        ArgumentNullException.ThrowIfNull(createFlowNodes);
        return CreateCore(options, true, models, auxiliary, createFlowNodes);
    }

    /// <summary>
    /// Composition entry point for the production framework startup: both real model catalogs plus
    /// every engine-owned X2 provider, composed into the complete twenty-family registration through
    /// the engine's factory-builder callback. The caller owns the returned engine until shutdown and
    /// must invoke this on ZoneSimulationThread.
    /// </summary>
    internal static ZoneNativeEngine CreateWithFramework(ZoneNativeEngineOptions options,
        ZoneModelContent models, ZoneModelAuxiliaryContent auxiliary, Action<string>? phaseTrace)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(auxiliary);
        return CreateCore(options, true, models, auxiliary, CreateOwnedFlowNodes, phaseTrace);
    }

    /// <summary>
    /// Builds all six providers from this engine and hands them to the real factory builder. Every
    /// provider is managed-only at this point and belongs to this engine generation; the builder
    /// releases any partially constructed factory set on failure.
    /// </summary>
    private static ZoneNativeFlowNodeRegistration CreateOwnedFlowNodes(ZoneNativeEngine engine) =>
        ZoneNativeFlowNodeFactories.Create(engine,
            engine.GetFlowGameModeServices(),
            engine.GetFlowActorRegistry(),
            engine.GetFlowCrysisScreenFaderProvider(),
            engine.GetFlowUiScreenFaderProvider(),
            engine.GetFlowTornadoServices(),
            engine.GetFlowPlayerServices());

    private static ZoneNativeEngine CreateCore(ZoneNativeEngineOptions options, bool usesGameFramework,
        ZoneModelContent? models = null, ZoneModelAuxiliaryContent? auxiliary = null,
        Func<ZoneNativeEngine, ZoneNativeFlowNodeRegistration>? createFlowNodes = null,
        Action<string>? phaseTrace = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("The original engine requires Windows x64.");
        var thread = ZoneSimulationThread.Current ?? throw new InvalidOperationException("Create the engine inside ZoneSimulationThread.RunAsync.");
        thread.VerifyAccess();
        var owner = new ZoneNativeEngine(options, thread, usesGameFramework) { _phaseTrace = phaseTrace };
        try
        {
            owner.TracePhase("ValidateInputs");
            owner.ValidateInputs();
            if (usesGameFramework)
            {
                owner.TracePhase("PrepareGameServices");
                owner.PrepareGameServices(models!, auxiliary!, createFlowNodes!);
            }
            owner._context = new ZoneNativeEngineStartupContext();
            owner.TracePhase("Claim");
            ZoneNativeProcessOwnership.Claim(owner, "a dedicated CSystem");
            owner._claimed = true; // Root before any DLL initialization or reverse callback.
            owner.Initialize();
            owner._ready = true;
            owner.TracePhase("EngineReady");
            return owner;
        }
        catch (Exception error)
        {
            owner._fault = error;
            if (!owner._claimed)
            {
                try { owner.FreeUnclaimedInputs(); }
                catch (Exception cleanupError)
                {
                    throw new AggregateException("Engine initialization and unpublished resource cleanup failed.", error, cleanupError);
                }
            }
            // After admission even an incomplete native call may have installed globals/threads.
            // No guessed rollback or finalizer can release its still-borrowed callback storage.
            throw;
        }
    }

    private void ValidateInputs()
    {
        if (!Directory.Exists(_options.WorkingDirectory))
            throw new DirectoryNotFoundException("The configured native working directory does not exist.");
        foreach (var (name, digest) in Identities)
        {
            if (name == "cryaction_dedicate.dll" && !_usesGameFramework) continue;
            var path = Path.Combine(_options.BinaryDirectory, name);
            // Native's conditional AI loader accepts an absent module. If a different
            // module appears/loads later, service validation must refuse this generation.
            if (name == "cryaisystem_dedicate.dll" && !File.Exists(path)) continue;
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            _identityLocks.Add(stream);
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(stream)), digest, StringComparison.Ordinal))
                throw new InvalidDataException($"Unsupported native engine binary identity: {name}.");
            stream.Position = 0;
            using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
            if (pe.PEHeaders.CoffHeader.Machine != Machine.Amd64)
                throw new InvalidDataException($"Native engine dependency is not Windows x64: {name}.");
            _admittedModules.Add(name);
            var existing = GetModuleHandleW(name);
            if (existing == 0) continue;
            if (name.StartsWith("cry", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"An engine module already exists outside this owner's lifetime: {name}.");
            ValidateModulePath(existing, path);
        }
        ValidateVc9Runtime(requireLoaded: false);
        RejectNativeGameModules();
        _gamePak = new FileStream(_options.GamePakPath, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    private void Initialize()
    {
        TracePhase("Initialize");
        _originalWorkingDirectory = Directory.GetCurrentDirectory();
        Directory.CreateDirectory(_options.SaveDirectory);
        if (!SetCurrentDirectoryW(_options.WorkingDirectory) || !SetDllDirectoryW(_options.BinaryDirectory))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot set the dedicated engine process paths.");
        // Engine-internal LoadLibrary calls use the real dedicated host DLL directory. Do not
        // call XlSetWorkingDir: it derives paths from dotnet/apphost and can replace a junction.
        _console = new ZoneNativeEngineConsole();
        _console.Initialize(); // Root the handles before any process console state changes.
        _commonModule = Load("xlcommon.dll");
        Export<VoidCall>(_commonModule, "?XlRandomInit@@YAXXZ")();
        Export<TextCall>(_commonModule, "?XlSetProduct@@YAXPEBD@Z")("ArcheAge");
        Export<TextCall>(_commonModule, "?XlSetSaveGameDir@@YAXPEBD@Z")(_options.SaveDirectory);
        var resolvedSaveBytes = new byte[0x104];
        byte saveResult;
        fixed (byte* buffer = resolvedSaveBytes)
            saveResult = Export<GetTextCall>(_commonModule, "?XlGetSaveGameDir@@YA_NPEADH@Z")((nint)buffer, resolvedSaveBytes.Length);
        var end = Array.IndexOf(resolvedSaveBytes, (byte)0);
        if (saveResult == 0 || end <= 0 || resolvedSaveBytes.Take(end).Any(value => value > 0x7f))
            throw new InvalidDataException("The native save directory was not returned as supported terminated ASCII.");
        var resolvedSave = Encoding.ASCII.GetString(resolvedSaveBytes, 0, end);
        if (!Path.IsPathFullyQualified(resolvedSave) || !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(resolvedSave)),
            _options.SaveDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The native save directory differs from the configured external directory.");
        _parameters = new ZoneNativeEngineParameters(_options, GetModuleHandleW(null), _context!.Pointer,
            resolvedSave, ReadGamePakStamp(_gamePak!.SafeFileHandle));
        _cleanupExceptionHandler = Export<VoidCall>(_commonModule, "?CleanupExceptionHandler@@YAXXZ");
        _exceptionHandlerStarted = true; // Keep roots even if initialization itself fails.
        // The dedicated host does not gate engine startup on this diagnostic result.
        _ = Export<InitExceptionCall>(_commonModule, "?InitExceptionHandler@@YA_NPEBD0_N@Z")(
            _options.LogName, resolvedSave, _options.FullDump ? (byte)1 : (byte)0);
        _systemModule = Load("crysystem.dll");
        if (_usesGameFramework)
        {
            TracePhase("InitializeGameFramework");
            // Keep our verified CrySystem module reference through the framework's internal
            // CSystem release. Its own Init creates the sole system with startup+988 left null.
            _system = InitializeGameFramework();
        }
        else
        {
            var factory = GetProcAddress(_systemModule, "CreateSystemInterface");
            if (factory != _systemModule + 0xdf100)
                throw new InvalidDataException("Unexpected native CSystem factory identity.");
            _system = Marshal.GetDelegateForFunctionPointer<CreateSystemCall>(factory)(_parameters.Pointer);
        }
        if (_system == 0) throw new InvalidOperationException("The native CSystem factory returned null; inspect the external engine log.");
        _context.ThrowIfFaulted();
        TracePhase("ValidateServices (initial)");
        ValidateServices();
        if (_usesGameFramework)
        {
            TracePhase("PublishGameServices");
            PublishGameServices();
            TracePhase("CompleteGameFrameworkInitialization (PostInit)");
            CompleteGameFrameworkInitialization();
            // Post-init runs native scripts. Recheck final module/service ownership and
            // empty terrain before publishing this initialized engine to scene callers.
            TracePhase("ValidateServices (post-init)");
            ValidateServices();
        }
    }

    /// <summary>
    /// Reports one phase as it is entered, for an ordered startup/shutdown diagnostic. The last
    /// reported phase is therefore the first failing one. This performs no native work and is
    /// absent (a single null check) unless a caller supplies an observer.
    /// </summary>
    private void TracePhase(string phase) => _phaseTrace?.Invoke(phase);

    private void ValidateServices()
    {
        // These are exact supported identities, not user-supplied pointer providers.
        if (Marshal.ReadIntPtr(_system) != _systemModule + 0x10e0c8 || VirtualAddress(_system, 8) != _systemModule + 0xc8270)
            throw new InvalidDataException("Unexpected native CSystem release identity.");
        if (VirtualAddress(_system, 0x368) != _systemModule + 0xb9c0 ||
            ZoneNativePhysicsModule.Virtual<ByteCall>(_system, 0x368)(_system) == 0)
            throw new InvalidDataException("CSystem did not select dedicated engine modules.");
        _environment = ZoneNativePhysicsModule.Virtual<PointerCall>(_system, 0x30)(_system);
        if (_environment != _system + 0x50 || Marshal.ReadIntPtr(_environment) != _system)
            throw new InvalidDataException("CSystem did not supply its own engine environment.");
        _physicsWorld = Marshal.ReadIntPtr(_environment + 0x70);
        var physicsModule = RequiredModule("cryphysics_dedicate.dll");
        if (_physicsWorld == 0 || Marshal.ReadIntPtr(_physicsWorld) != physicsModule + 0x1f0af8)
            throw new InvalidDataException("CSystem did not supply the supported dedicated physical world.");
        if (Marshal.ReadIntPtr(_environment + 0xa8) == 0 || Marshal.ReadIntPtr(_environment + 0x18) == 0 ||
            Marshal.ReadIntPtr(_environment + 0x88) == 0)
            throw new InvalidDataException("CSystem did not initialize character, renderer and entity services.");
        foreach (var name in new[] { "cryanimation.dll", "cryrendernull.dll", "cryentitysystem_dedicate.dll",
            "cry3dengine_dedicate.dll", "cryscriptsystem.dll" }) RequiredModule(name);
        if (VirtualAddress(_system, 0x230) != _systemModule + 0xc8240 ||
            ZoneNativePhysicsModule.Virtual<PointerCall>(_system, 0x230)(_system) == 0)
            throw new InvalidDataException("CSystem did not supply its real stream engine.");
        foreach (var (name, _) in Identities)
        {
            var module = GetModuleHandleW(name);
            if (module == 0) continue;
            if (!_admittedModules.Contains(name))
                throw new InvalidDataException($"CSystem loaded a module absent from this generation's verified inputs: {name}.");
            ValidateModulePath(module, Path.Combine(_options.BinaryDirectory, name));
        }
        ValidateVc9Runtime(requireLoaded: true);
        RejectNativeGameModules();
        RequireEmptyTerrain(); // No external terrain ownership has been admitted yet.
    }

    private void VerifyAccess()
    {
        _thread.VerifyAccess();
        if (GetCurrentThreadId() != _nativeThread)
            throw new InvalidOperationException("The engine operation moved to another native thread.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        VerifyAccess();
        if (_fault is not null || !_ready || _disposing)
            throw new InvalidOperationException("The native engine lifetime is faulted or already retiring; retain it until process termination.", _fault);
        RequireNoBorrowers(); // A premature parent Dispose is rejected without poisoning it.
        _disposing = true;
        try
        {
            _context!.ThrowIfFaulted();
            if (_usesGameFramework) RequireGameServicePublication();
            RequireEmptyTerrain();
            // Every registered resource must finish detach/drain before EntitySystem shutdown.
            if (_usesGameFramework) ShutdownGameFramework();
            else ZoneNativePhysicsModule.Virtual<ReleaseCall>(_system, 8)(_system);
            _system = 0;
            _environment = 0;
            _physicsWorld = 0;
            _physicalIdReservations.Clear();
            if (_usesGameFramework) RetireGameFactories();
            _context.ThrowIfFaulted();
            if (_exceptionHandlerStarted) _cleanupExceptionHandler!();
            _exceptionHandlerStarted = false;
            ReleaseGameFrameworkModule();
            FreeModule(ref _systemModule);
            FreeModule(ref _commonModule);
            _context.ThrowIfFaulted();
            // Keep all reverse thunks/strings until System shutdown AND host DLL unloading.
            FreeManagedInputs();
            _console?.Dispose();
            _console = null;
            if (_originalWorkingDirectory is not null && !SetCurrentDirectoryW(_originalWorkingDirectory))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot restore the process working directory.");
            // SetDllDirectory belongs to the dedicated process. Keep it until process exit;
            // its former default/empty state cannot be distinguished by GetDllDirectory.
            _ready = false;
            _disposed = true;
        }
        catch (Exception error) { _fault = error; throw; }
    }

    private void RequireEmptyTerrain()
    {
        for (var i = 0; i < 5; i++)
            if (Marshal.ReadIntPtr(_physicsWorld + 0x620 + i * 8) != 0)
                throw new InvalidOperationException("Engine bootstrap cannot retire a physical world containing unowned terrain replicas.");
    }

    private void FreeManagedInputs()
    {
        ReleaseGameServices();
        _parameters?.Dispose();
        _parameters = null;
        _context?.Dispose();
        _context = null;
        _gamePak?.Dispose();
        _gamePak = null;
        foreach (var stream in _identityLocks) stream.Dispose();
        _identityLocks.Clear();
    }

    private void FreeUnclaimedInputs()
    {
        // No callback was published and no native startup ran. A factory's own uncertain
        // rollback retains that factory through its registry root; it must not prevent these
        // independent unpublished allocations/file handles from being released as well.
        var failures = new List<Exception>();
        void Release(Action action)
        {
            try { action(); }
            catch (Exception error) { failures.Add(error); }
        }
        Release(() => { _gameFlowNodes?.ReleaseUnpublished(); _gameFlowNodes = null; });
        Release(ReleaseFlowFaderProviders);
        Release(ReleaseFlowPlayerServices);
        Release(ReleaseFlowGameModeServices);
        Release(() => { _gameServices?.Release(); _gameServices = null; });
        Release(() => { _parameters?.Dispose(); _parameters = null; });
        Release(() => { _context?.Dispose(); _context = null; });
        Release(() => { _gamePak?.Dispose(); _gamePak = null; });
        foreach (var stream in _identityLocks) Release(stream.Dispose);
        _identityLocks.Clear();
        if (failures.Count != 0) throw new AggregateException("Unpublished engine resources could not all be released.", failures);
    }

    private nint Load(string name)
    {
        var module = LoadLibraryExW(Path.Combine(_options.BinaryDirectory, name), 0, 0x900);
        if (module == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), $"Cannot load engine module {name}.");
        ValidateModulePath(module, Path.Combine(_options.BinaryDirectory, name));
        return module;
    }

    private nint RequiredModule(string name)
    {
        var module = GetModuleHandleW(name);
        if (module == 0) throw new InvalidDataException($"CSystem did not load required module {name}.");
        ValidateModulePath(module, Path.Combine(_options.BinaryDirectory, name));
        return module;
    }

    private void RejectNativeGameModules()
    {
        foreach (var name in new[] { "x2game-dev_dedicate.dll", "x2game-dev_dedicate.dll.aaemu-original",
            "x2game-dev.dll", "x2game.dll", "crygame.dll", "cryaction_dedicate.dll", "cryaction.dll" })
        {
            var module = GetModuleHandleW(name);
            if (module == 0) continue;
            if (name == "cryaction_dedicate.dll" && _usesGameFramework && IsOwnedGameFrameworkModule(module)) continue;
            throw new InvalidOperationException("The engine owner cannot share a process with an unowned native game/framework.");
        }
    }

    private static void ValidateVc9Runtime(bool requireLoaded)
    {
        // The real isolated C# physics run resolved this Microsoft VC90 assembly through
        // Windows publisher policy. The Bin64 MSVCR90 copy is not the loaded runtime.
        var directory = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.Windows),
            "WinSxS", "amd64_microsoft.vc90.crt_1fc8b3b9a1e18e3b_9.0.30729.9635_none_08e2c157a83ed5da");
        foreach (var name in new[] { "msvcr90.dll", "msvcp90.dll" })
        {
            // A basename lookup outside the dependency's activation context can return null
            // for an already loaded SxS module. Inspect this process's actual module mappings.
            using var process = Process.GetCurrentProcess();
            var modules = process.Modules.Cast<ProcessModule>()
                .Where(module => string.Equals(Path.GetFileName(module.FileName), name, StringComparison.OrdinalIgnoreCase)).ToArray();
            foreach (var module in modules) ValidateModulePath(module.BaseAddress, Path.Combine(directory, name));
            if (requireLoaded && modules.Length == 0)
                throw new InvalidDataException($"The configured engine did not load its supported Windows VC9 runtime: {name}.");
        }
    }

    private static void ValidateModulePath(nint module, string expected)
    {
        var path = new StringBuilder(32768);
        var count = GetModuleFileNameW(module, path, path.Capacity);
        if (count == 0 || count >= path.Capacity || !string.Equals(Path.GetFullPath(path.ToString()), expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Native engine dependency came from another directory: {Path.GetFileName(expected)}.");
    }

    private static void FreeModule(ref nint module)
    {
        if (module == 0) return;
        var previous = module;
        module = 0; // An uncertain release cannot be retried against the same handle.
        if (!FreeLibrary(previous)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot release the engine module.");
    }

    private static long ReadGamePakStamp(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read the configured game_pak stamp.");
        var lastWrite = ((ulong)info.LastWriteHigh << 32) | info.LastWriteLow;
        var size = ((ulong)info.SizeHigh << 32) | info.SizeLow;
        return unchecked((long)(lastWrite + size));
    }

    private static nint VirtualAddress(nint instance, int offset) => Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance) + offset);
    private static T Export<T>(nint module, string name) where T : Delegate
    {
        var pointer = GetProcAddress(module, name);
        return pointer != 0 ? Marshal.GetDelegateForFunctionPointer<T>(pointer) : throw new MissingMethodException($"Missing native export {name}.");
    }

    [StructLayout(LayoutKind.Sequential)] private struct FileInformation
    {
        internal uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, LastWriteLow,
            LastWriteHigh, VolumeSerialNumber, SizeHigh, SizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void VoidCall();
    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Ansi)] private delegate void TextCall(string value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate byte GetTextCall(nint output, int capacity);
    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Ansi)] private delegate byte InitExceptionCall(string log, string save, byte fullDump);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint CreateSystemCall(nint parameters);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint PointerCall(nint receiver);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate byte ByteCall(nint receiver);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void ReleaseCall(nint receiver);
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint LoadLibraryExW(string path, nint file, uint flags);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandleW(string? name);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] private static extern uint GetModuleFileNameW(nint module, StringBuilder path, int capacity);
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetCurrentDirectoryW(string path);
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetDllDirectoryW(string path);
    [DllImport("kernel32", CharSet = CharSet.Ansi, ExactSpelling = true)] private static extern nint GetProcAddress(nint module, string name);
    [DllImport("kernel32", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FreeLibrary(nint module);
    [DllImport("kernel32")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
}

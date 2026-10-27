using System.Runtime.InteropServices;

namespace AAEmu.Zone.Simulation;

/// <summary>
/// Owns the real framework and its single CSystem through the original null-system Init
/// branch. All calls belong to the parent engine startup/final shutdown; there is no
/// independent framework disposal or native gameplay loop.
/// </summary>
public sealed partial class ZoneNativeEngine
{
    private nint _gameFrameworkModule;
    private nint _gameFramework;
    private readonly nint[] _gameFrameworkProviders = new nint[4];
    private bool _gameFrameworkInitializationStarted;
    private bool _gameFrameworkInitialized;
    private bool _gameFrameworkPostInitializationStarted;
    private bool _gameFrameworkPostInitializationReturned;
    private bool _gameFrameworkShutdownStarted;
    private bool _gameFrameworkShutdownReturned;

    private nint InitializeGameFramework()
    {
        using var operation = EnterGameFrameworkPhase(shutdown: false);
        if (_gameFrameworkInitializationStarted || _system != 0)
            throw new InvalidOperationException("The framework must create this engine's sole CSystem exactly once.");
        _gameFrameworkInitializationStarted = true;
        var parameters = _parameters!.Pointer;
        RequireGameFramework(Marshal.ReadIntPtr(parameters + 0x988) == 0 &&
            Marshal.ReadInt32(parameters + 0x940) == 2 &&
            Marshal.ReadByte(parameters + 0x9ac) != 0 && Marshal.ReadByte(parameters + 0x977) == 0,
            "The framework requires the retained dedicated startup profile with no existing CSystem.");
        // The engine is already process-rooted and holds an extra verified CrySystem reference.
        // Native307049E0 obtains its own reference and creates the sole system. Do not write a
        // fabricated module handle or change the network profile to force initialization.
        _gameFrameworkModule = Load("cryaction_dedicate.dll");
        RequireGameFramework(Marshal.ReadIntPtr(_gameFrameworkModule + 0x30d6f8) == 0 &&
            Marshal.ReadIntPtr(_gameFrameworkModule + 0x30d6f0) == 0 &&
            Marshal.ReadIntPtr(_gameFrameworkModule + 0x30d700) == 0,
            "CryAction already contains an unowned framework, environment or event listener.");
        var factory = GetProcAddress(_gameFrameworkModule, "CreateGameFramework");
        RequireGameFramework(factory == _gameFrameworkModule + 0x20ce00,
            "The native GameFramework factory has an unexpected identity.");
        _gameFramework = InvokeGameFramework(() =>
            Marshal.GetDelegateForFunctionPointer<GameFrameworkFactoryCall>(factory)());
        RequireGameFramework(_gameFramework == _gameFrameworkModule + 0x30f330 &&
            Marshal.ReadIntPtr(_gameFrameworkModule + 0x30d6f8) == _gameFramework,
            "CreateGameFramework did not publish its actual static owner.");
        RequireGameFrameworkTable();
        RequireGameFramework(Marshal.ReadIntPtr(_gameFramework + 0x18) == 0 &&
            Marshal.ReadIntPtr(_gameFramework + 0x50) == 0,
            "The new native framework already owns a system.");
        var initialized = InvokeGameFramework(() =>
            BindGameFramework<GameFrameworkInitCall>(0x28, 0x2049e0)(_gameFramework, parameters));
        RequireGameFramework(initialized != 0,
            "Native GameFramework Init returned false; retain its partial process ownership.");
        var system = Marshal.ReadIntPtr(_gameFramework + 0x18);
        ValidateGameFrameworkOwner(system);
        _gameFrameworkInitialized = true;
        return system;
    }

    private void CompleteGameFrameworkInitialization()
    {
        using var operation = EnterGameFrameworkPhase(shutdown: false);
        if (!_gameFrameworkInitialized || _gameFrameworkPostInitializationStarted)
            throw new InvalidOperationException("Run native framework post-initialization once after successful Init.");
        ValidateGameFrameworkOwner(_system);
        RequireGameServicePublication();
        RequireGameFramework(_environment == _system + 0x50 &&
            Marshal.ReadIntPtr(_gameFramework + 0x558) == 0,
            "The framework must enter post-initialization in its exact engine environment without a game context.");
        for (var i = 0; i < _gameFrameworkProviders.Length; i++)
            RequireGameFramework(Marshal.ReadIntPtr(_system + GameFrameworkMirrorOffset(i)) == 0,
                "CSystem already exposes a framework provider outside this initialization.");
        _gameFrameworkPostInitializationStarted = true;
        var completed = InvokeGameFramework(() =>
            BindGameFramework<GameFrameworkPhaseCall>(0x30, 0x1f7630)(_gameFramework));
        RequireGameFramework(completed != 0,
            "Native GameFramework post-initialization returned false; retain this engine generation.");
        RequireGameFramework(_gameFlowNodesRegistered,
            "Framework PostInit returned without completing the owned game FlowNode registrations.");
        ValidateGameFrameworkOwner(_system);
        RequireGameServicePublication();
        // Read every slot before rejecting, so the failure names the exact provider pair and the
        // complete observed state instead of only the first mismatching index.
        var observedProviders = new nint[_gameFrameworkProviders.Length];
        var observedMirrors = new nint[_gameFrameworkProviders.Length];
        for (var i = 0; i < observedProviders.Length; i++)
        {
            observedProviders[i] = Marshal.ReadIntPtr(_gameFramework + GameFrameworkProviderOffset(i));
            observedMirrors[i] = Marshal.ReadIntPtr(_system + GameFrameworkMirrorOffset(i));
        }
        var providerSnapshot = string.Join(", ", Enumerable.Range(0, observedProviders.Length).Select(index =>
            $"[{index}] framework+0x{GameFrameworkProviderOffset(index):X}=0x{observedProviders[index]:X16} system+0x{GameFrameworkMirrorOffset(index):X}=0x{observedMirrors[index]:X16}"));
        for (var i = 0; i < observedProviders.Length; i++)
        {
            RequireGameFramework(observedProviders[i] != 0 && observedMirrors[i] == observedProviders[i],
                $"The actual framework did not publish its required native provider {i}. Observed: {providerSnapshot}.");
            _gameFrameworkProviders[i] = observedProviders[i];
        }
        RequireGameFramework(Marshal.ReadIntPtr(_gameFrameworkProviders[0]) == _gameFrameworkModule + 0x230148,
            "The framework did not construct the supported CFlowSystem implementation.");
        RequireGameFramework(Marshal.ReadIntPtr(_gameFramework + 0x558) == 0,
            "Post-initialization entered an unowned native game context.");
        //306F7630 executes scripts/main.lua and OnInit but ignores their individual results.
        // A normal phase return records provider ownership, not Lua or scene readiness.
        _gameFrameworkPostInitializationReturned = true;
    }

    private void ShutdownGameFramework()
    {
        using var operation = EnterGameFrameworkPhase(shutdown: true);
        if (!_gameFrameworkInitialized || !_gameFrameworkPostInitializationReturned || _gameFrameworkShutdownStarted)
            throw new InvalidOperationException("An incomplete or already retiring framework remains pinned to its process.");
        ValidateGameFrameworkOwner(_system);
        for (var i = 0; i < _gameFrameworkProviders.Length; i++)
            RequireGameFramework(Marshal.ReadIntPtr(_gameFramework + GameFrameworkProviderOffset(i)) == _gameFrameworkProviders[i] &&
                Marshal.ReadIntPtr(_system + GameFrameworkMirrorOffset(i)) == _gameFrameworkProviders[i],
                "A framework provider changed outside its engine lifetime.");
        RequireGameFramework(Marshal.ReadIntPtr(_gameFramework + 0x558) == 0,
            "Retire the native game context before final framework shutdown.");
        // Retire renderer/texture/callback ownership while the actual renderer, CSystem and every
        // native module are still alive. The later factory-cache phase runs after the framework
        // destructor has already released CSystem, so it cannot release fader textures.
        ReleaseFlowFaderProviders();
        _gameFrameworkShutdownStarted = true;
        // Original host39A15840 invokes +58.306F79A0 supplies destructor flag0 to +2C0,
        // preserving static framework storage.307050C0 releases its owned CSystem at the
        // original interior point, before final framework callbacks/resources are removed.
        InvokeGameFramework(() =>
        {
            BindGameFramework<GameFrameworkShutdownCall>(0x58, 0x1f79a0)(_gameFramework);
            return 0;
        });
        // CSystem and its environment are now dead: inspect only still-mapped Action storage.
        RequireGameFramework(Marshal.ReadIntPtr(_gameFrameworkModule + 0x30d6f8) == 0 &&
            Marshal.ReadIntPtr(_gameFrameworkModule + 0x30d700) == 0 &&
            Marshal.ReadIntPtr(_gameFramework + 0x18) == 0 &&
            Marshal.ReadIntPtr(_gameFramework + 0x50) == 0,
            "Native framework shutdown retained its system, module ownership or event listener.");
        _gameFrameworkShutdownReturned = true;
    }

    private void ReleaseGameFrameworkModule()
    {
        if (!_usesGameFramework) return;
        VerifyAccess();
        RequireGameFramework(_disposing && !_operationActive && _gameFrameworkShutdownReturned &&
            _system == 0 && _environment == 0 && _physicsWorld == 0,
            "Retain CryAction until native framework and CSystem shutdown have completed.");
        _context!.ThrowIfFaulted();
        FreeModule(ref _gameFrameworkModule);
        _gameFramework = 0;
        Array.Clear(_gameFrameworkProviders);
        // Root releases its extra CrySystem handle next, then common/native callback inputs.
    }

    private bool IsOwnedGameFrameworkModule(nint module) =>
        _usesGameFramework && module != 0 && module == _gameFrameworkModule &&
        _gameFrameworkInitializationStarted && !_gameFrameworkShutdownReturned;

    private EngineOperation EnterGameFrameworkPhase(bool shutdown)
    {
        VerifyAccess();
        if (!_usesGameFramework || !_claimed || _disposed || _fault is not null ||
            _operationActive || (shutdown ? !_disposing || !_ready : _disposing || _ready))
            throw new InvalidOperationException("The framework phase is outside its exclusive engine startup or final shutdown.", _fault);
        if (shutdown) RequireNoBorrowers();
        _context!.ThrowIfFaulted();
        _operationActive = true;
        return new EngineOperation(this);
    }

    private void ValidateGameFrameworkOwner(nint system)
    {
        RequireGameFrameworkTable();
        RequireGameFramework(Marshal.ReadIntPtr(_gameFrameworkModule + 0x30d6f8) == _gameFramework &&
            system != 0 && Marshal.ReadIntPtr(_gameFramework + 0x18) == system &&
            Marshal.ReadIntPtr(_gameFramework + 0x50) == _systemModule,
            "The framework no longer owns this engine's exact CSystem and native module reference.");
        RequireGameFramework(Marshal.ReadIntPtr(system) == _systemModule + 0x10e0c8 &&
            Marshal.ReadIntPtr(_gameFrameworkModule + 0x30d6f0) == system + 0x50 &&
            Marshal.ReadIntPtr(system + 0x50) == system,
            "The framework did not initialize the supported CSystem environment.");
    }

    private void RequireGameFrameworkTable() => RequireGameFramework(_gameFramework != 0 &&
        Marshal.ReadIntPtr(_gameFramework) == _gameFrameworkModule + 0x264938,
        "The native GameFramework implementation identity changed.");

    private T BindGameFramework<T>(int slot, int relativeAddress) where T : Delegate
    {
        RequireGameFrameworkTable();
        var target = VirtualAddress(_gameFramework, slot);
        RequireGameFramework(target == _gameFrameworkModule + relativeAddress, "The native GameFramework ABI changed.");
        return Marshal.GetDelegateForFunctionPointer<T>(target);
    }

    private T InvokeGameFramework<T>(Func<T> call)
    {
        try
        {
            var result = call();
            // Startup has no ready engine yet; shutdown may already have destroyed CSystem.
            // Validate only retained managed callback state here, never a freed native service.
            VerifyAccess();
            _context!.ThrowIfFaulted();
            _gameFlowNodes?.ThrowIfFaulted();
            if (_fault is not null) throw new InvalidOperationException("The engine faulted during its native framework phase.", _fault);
            return result;
        }
        catch (Exception error) { Poison(error); throw; }
    }

    private void RequireGameFramework(bool condition, string message)
    {
        if (condition) return;
        var error = new InvalidOperationException(message);
        Poison(error);
        throw error;
    }

    private static int GameFrameworkProviderOffset(int index) => index switch
    {
        0 => 0x4f8,
        1 => 0x510,
        2 => 0x520,
        3 => 0x518,
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    /// <summary>
    /// The exact CSystem member each framework provider is published into. PostInit 306F7630 calls
    /// CSystem vtable +0x300/+0x308/+0x310/+0x318 with framework+0x4F8/+0x510/+0x520/+0x518, and the
    /// verified setters at CrySystem RVAs 0xB6C0/0xB6D0/0xB6E0/0xB6F0 store into 0x118/0x120/0x128/0x138.
    /// Provider 3 is therefore mirrored at +0x138, not at +0x130: CrySystem 0xB700 (+0x320, storing
    /// +0x130) is never called by this phase. The framework field offset and the mirror offset are
    /// independent; do not derive one from the other.
    /// </summary>
    private static int GameFrameworkMirrorOffset(int index) => index switch
    {
        0 => 0x118,
        1 => 0x120,
        2 => 0x128,
        3 => 0x138,
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint GameFrameworkFactoryCall();
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate byte GameFrameworkInitCall(nint framework, nint parameters);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate byte GameFrameworkPhaseCall(nint framework);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GameFrameworkShutdownCall(nint framework);
}

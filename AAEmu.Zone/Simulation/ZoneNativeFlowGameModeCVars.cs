using System.Runtime.InteropServices;

namespace AAEmu.Zone.Simulation;

/// <summary>
/// Owns the backing integers and borrowed name/help strings for the two game-mode CVars.
/// The real console owns the registered CXConsoleVariableIntRef objects. No finalizer or
/// early unregister path can release backing storage while those native objects still exist.
/// </summary>
internal sealed unsafe class ZoneNativeFlowGameModeCVars
{
    private const int ConsoleTableRva = 0x1198f8;
    private const int RegisterIntRefRva = 0xa4690;
    private const int FindVariableRva = 0x9d8d0;
    private const int IntRefTableRva = 0x119dd8;
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private readonly ZoneNativeEngine _engine;
    private readonly Variable[] _variables =
    [
        new("g_difficultyLevel", "Difficulty level", 0x802),
        new("i_debug_mp_flowgraph", "Displays info on the MP flowgraph node", 2)
    ];
    private nint _integers;
    private nint _console;
    private nint _systemModule;
    private GCHandle _nativeLifetimeRoot;
    private Phase _phase;
    private Exception? _fault;

    internal ZoneNativeFlowGameModeCVars(ZoneNativeEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
        engine.ValidateFlowFactoryConstruction();
        try
        {
            _integers = (nint)NativeMemory.AllocZeroed(2 * sizeof(int));
            if (_integers == 0) throw new OutOfMemoryException();
            foreach (var variable in _variables)
            {
                variable.NamePointer = Marshal.StringToCoTaskMemUTF8(variable.Name);
                variable.HelpPointer = Marshal.StringToCoTaskMemUTF8(variable.Help);
            }
        }
        catch
        {
            FreeBacking();
            _phase = Phase.Released;
            throw;
        }
    }

    internal bool IsInitialized => _phase == Phase.Initialized;

    /// <summary>
    /// Managed admission only. Factory construction can precede native game publication;
    /// initialization is a separate required operation before framework PostInit begins.
    /// </summary>
    internal void ValidateForRegistration()
    {
        VerifyThread();
        if (_phase is not (Phase.Unpublished or Phase.Initialized) || _fault is not null)
            throw new InvalidOperationException("The game-mode CVar owner cannot admit factories in its current lifetime.", _fault);
    }

    /// <summary>
    /// Call once under the held game callback after IGame publication and before PostInit.
    /// Both names must be absent: native duplicate registration returns the old object without
    /// binding or initializing our supplied integer. Existing foreign state is never replaced.
    /// </summary>
    internal void InitializeUnderCallback()
    {
        VerifyThread();
        if (_phase != Phase.Unpublished)
            throw new InvalidOperationException("Initialize the game-mode CVars exactly once.", _fault);
        var environment = _engine.RequireFlowCallbackEnvironment();
        var module = _engine.RequireFlowCallbackModule("crysystem.dll");
        var console = Marshal.ReadIntPtr(environment + 0x38);
        ValidateConsole(console, module);
        foreach (var variable in _variables)
        {
            if (Find(console, module, variable.NamePointer) != 0)
                throw new InvalidOperationException($"Cannot bind owned game-mode storage: native CVar '{variable.Name}' already exists.");
        }

        // Root storage before the first native operation that can retain its addresses.
        _nativeLifetimeRoot = GCHandle.Alloc(this);
        _console = console;
        _systemModule = module;
        _phase = Phase.Initializing;
        try
        {
            for (var index = 0; index < _variables.Length; index++)
            {
                var variable = _variables[index];
                var name = new NativeName { Kind = 0, Reserved = 0, Text = variable.NamePointer };
                //392B2480/392B3C2B: signed default0, exact flags/help, null change callback,
                // final byte1. 365A4690 only adds flag800000 when that last byte is zero;
                // it is not permission to overwrite an existing registered variable.
                variable.Pointer = ((delegate* unmanaged[Stdcall]<nint, NativeName*, int*, int, uint,
                    nint, nint, byte, nint>)(module + RegisterIntRefRva))(
                    console, &name, (int*)_integers + index, 0, variable.Flags,
                    variable.HelpPointer, 0, 1);
                _engine.ThrowIfFlowCallbackFaulted();
                ValidateVariable(index);
            }
            _phase = Phase.Initialized;
        }
        catch (Exception error)
        {
            // A native variable can already borrow our memory. Shutdown must retire the
            // console before this owner can release even partially registered backing.
            _fault = error;
            _phase = Phase.Faulted;
            throw;
        }
    }

    internal int ReadDifficultyLevel() => Read(0);
    internal bool ReadDebugEnabled() => Read(1) != 0;

    internal void RequireInitialized()
    {
        VerifyThread();
        if (_phase != Phase.Initialized || _fault is not null)
            throw new InvalidOperationException("Game-mode FlowNodes require initialized live native CVars.", _fault);
    }

    private int Read(int index)
    {
        RequireInitialized();
        var environment = _engine.RequireFlowCallbackEnvironment();
        if (Marshal.ReadIntPtr(environment + 0x38) != _console ||
            _engine.RequireFlowCallbackModule("crysystem.dll") != _systemModule)
            throw new InvalidOperationException("The game-mode CVar console generation changed.");
        ValidateConsole(_console, _systemModule);
        ValidateVariable(index);
        // The actual int-ref setter writes this address. Pending native configuration is
        // applied by 365A3C30 during registration; no managed default substitutes for it.
        return Marshal.ReadInt32(_integers, index * sizeof(int));
    }

    private nint Find(nint console, nint module, nint namePointer)
    {
        var name = new NativeName { Kind = 0, Reserved = 0, Text = namePointer };
        var result = ((delegate* unmanaged[Stdcall]<nint, NativeName*, nint>)(module + FindVariableRva))(console, &name);
        _engine.ThrowIfFlowCallbackFaulted();
        return result;
    }

    private void ValidateVariable(int index)
    {
        var variable = _variables[index];
        var found = Find(_console, _systemModule, variable.NamePointer);
        if (variable.Pointer == 0 || found != variable.Pointer ||
            Marshal.ReadIntPtr(found) != _systemModule + IntRefTableRva ||
            Marshal.ReadIntPtr(found + 0x30) != _console ||
            Marshal.ReadIntPtr(found + 0x40) != _integers + index * sizeof(int))
            throw new InvalidOperationException($"Native CVar '{variable.Name}' does not retain this owner's registered integer storage.");
    }

    private static void ValidateConsole(nint console, nint module)
    {
        if (console == 0 || Marshal.ReadIntPtr(console) != module + ConsoleTableRva ||
            Marshal.ReadIntPtr(module + ConsoleTableRva + 0x48) != module + RegisterIntRefRva ||
            Marshal.ReadIntPtr(module + ConsoleTableRva + 0xc0) != module + FindVariableRva)
            throw new InvalidOperationException("The game-mode CVar owner requires the actual supported CrySystem console.");
    }

    /// <summary>Construction rollback only, before any native CVar can borrow this storage.</summary>
    internal void ReleaseUnpublished()
    {
        VerifyThread();
        if (_phase != Phase.Unpublished)
            throw new InvalidOperationException("Published or uncertain CVar backing must survive native console shutdown.");
        FreeBacking();
        _phase = Phase.Released;
    }

    /// <summary>
    /// Call under the completed-framework-shutdown operation. The engine verifies that its
    /// real CSystem is already gone; no retired console/CVar pointer is dereferenced here.
    /// </summary>
    internal void RetireAfterFrameworkShutdown()
    {
        VerifyThread();
        if (_phase is Phase.Retired or Phase.Released)
            throw new InvalidOperationException("Retire the game-mode CVar owner only once.");
        if (_engine.RequireFlowCallbackEnvironment(destruction: true) != 0)
            throw new InvalidOperationException("Game-mode CVar backing remains borrowed by the live native console.");
        _console = 0;
        _systemModule = 0;
        foreach (var variable in _variables) variable.Pointer = 0;
        _phase = Phase.Retired;
    }

    /// <summary>
    /// Root's final module-release phase only. This performs no native call or pointer read;
    /// the verified preceding shutdown phase is what ended every backing-storage borrow.
    /// </summary>
    internal void ReleaseAfterNativeModules()
    {
        VerifyThread();
        if (_phase != Phase.Retired)
            throw new InvalidOperationException("Retire the actual console before releasing game-mode CVar storage.");
        FreeBacking();
        _phase = Phase.Released;
        if (_nativeLifetimeRoot.IsAllocated) _nativeLifetimeRoot.Free();
    }

    private void FreeBacking()
    {
        foreach (var variable in _variables)
        {
            if (variable.NamePointer != 0) Marshal.FreeCoTaskMem(variable.NamePointer);
            if (variable.HelpPointer != 0) Marshal.FreeCoTaskMem(variable.HelpPointer);
            variable.NamePointer = 0;
            variable.HelpPointer = 0;
        }
        if (_integers != 0) NativeMemory.Free((void*)_integers);
        _integers = 0;
    }

    private void VerifyThread()
    {
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new InvalidOperationException("Use game-mode CVar ownership on its captured engine thread.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeName
    {
        internal uint Kind;
        internal uint Reserved;
        internal nint Text;
    }

    private sealed class Variable(string name, string help, uint flags)
    {
        internal string Name { get; } = name;
        internal string Help { get; } = help;
        internal uint Flags { get; } = flags;
        internal nint NamePointer;
        internal nint HelpPointer;
        internal nint Pointer;
    }

    private enum Phase { Unpublished, Initializing, Initialized, Faulted, Retired, Released }
}

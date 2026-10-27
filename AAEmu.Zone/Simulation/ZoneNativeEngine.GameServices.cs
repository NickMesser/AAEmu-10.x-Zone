using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using AAEmu.Zone.Content;

namespace AAEmu.Zone.Simulation;

public sealed partial class ZoneNativeEngine
{
    private GameServices? _gameServices;
    private ZoneNativeFlowNodeRegistration? _gameFlowNodes;
    private bool _gamePublicationStarted;
    private bool _gamePublished;
    private bool _gameFlowNodesRegistered;

    private void PrepareGameServices(ZoneModelContent models, ZoneModelAuxiliaryContent auxiliary,
        Func<ZoneNativeEngine, ZoneNativeFlowNodeRegistration> createFlowNodes)
    {
        VerifyAccess();
        // Real family bindings capture this owner before native admission. The builder owns
        // rollback of any factory set that fails to reach a complete returned registry owner.
        var flowNodes = createFlowNodes(this) ?? throw new InvalidOperationException("The real FlowNode factory builder returned no registration owner.");
        flowNodes.AdoptByEngine();
        _gameFlowNodes = flowNodes; // Transfer before fallible descriptor/table construction.
        _gameServices = new GameServices(this, models, auxiliary);
    }

    /// <summary>
    /// Whether the engine publishes the IGame callback the character-spawn path calls
    /// (+0xE8). Every unbound slot is an <see cref="UnsupportedGameCall"/> that FailFasts the
    /// process, so a caller that needs one MUST ask first: the first real client login proved the
    /// admission path enters that slot and takes the whole zone host down with it
    /// ("Unsupported native IGame callback +0xE8. This isolated engine must terminate.").
    /// </summary>
    internal bool CharacterSpawnCallbackPublished => _gameServices?.CharacterSpawnPublished == true;

    /// <summary>
    /// Probe-only escape hatch: bind ONE currently-fatal IGame slot to a managed string provider so
    /// the native side's real contract can be observed in a disposable process. Null in the host.
    /// </summary>
    internal static int? ResearchStringSlot { get; set; }

    /// <summary>The string the research slot answers with (empty when null).</summary>
    internal static string? ResearchStringValue { get; set; }

    /// <summary>Magic value: answer the research slot with a zeroed page instead of text.</summary>
    internal const string ZeroAnswerToken = "(zero)";

    /// <summary>Magic value: answer with a real object whose vtable +0x10 logs when invoked.</summary>
    internal const string ObjectAnswerToken = "(object)";

    /// <summary>Magic value: answer with an object whose EVERY vtable entry and data field reports itself.</summary>
    internal const string InstrumentedAnswerToken = "(instrumented)";

    /// <summary>Magic value: answer null - the control for "is the crash caused by our return value?".</summary>
    internal const string NullAnswerToken = "(null)";

    private const int ResearchVtableEntries = 32;
    private const int ResearchObjectBytes = 0x100;
    private const int ResearchDataFields = 0x80;
    private const int PointerSize = 8; // x64 only; PointerSize is unavailable outside unsafe.

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint ResearchStubCall();

    private static readonly List<ResearchStubCall> ResearchStubs = [];
    private static readonly List<ResearchStubCall> ResearchFieldStubs = [];
    private static nint _researchVtable;

    /// <summary>Builds a vtable whose every entry reports its own offset when called.</summary>
    private static void BuildResearchVtable()
    {
        if (_researchVtable != 0) return;
        _researchVtable = Marshal.AllocHGlobal(ResearchVtableEntries * PointerSize);
        for (var entry = 0; entry < ResearchVtableEntries; entry++)
        {
            var captured = entry * PointerSize;
            var stub = new ResearchStubCall(() =>
            {
                Console.WriteLine($"RESEARCH consumer called vtable +0x{captured:X} with no arguments.");
                Console.Out.Flush();
                return 0;
            });
            ResearchStubs.Add(stub);
            GC.KeepAlive(stub);
            Marshal.WriteIntPtr(_researchVtable, entry * PointerSize, Marshal.GetFunctionPointerForDelegate(stub));
        }
    }

    /// <summary>A data field that reports its own offset when called through as a function pointer.</summary>
    private static nint _researchFieldStub(int offset)
    {
        if (ResearchFieldStubs.Count > offset / PointerSize)
            return Marshal.GetFunctionPointerForDelegate(ResearchFieldStubs[offset / PointerSize]);
        var captured = offset;
        var stub = new ResearchStubCall(() =>
            {
                Console.WriteLine($"RESEARCH consumer called the data field at +0x{captured:X} as a function pointer.");
                Console.Out.Flush();
                return 0;
            });
        ResearchFieldStubs.Add(stub);
        GC.KeepAlive(stub);
        return Marshal.GetFunctionPointerForDelegate(stub);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint ResearchVtablePlus10Call();

    private static ResearchVtablePlus10Call? _researchVtablePlus10;

    /// <summary>
    /// The +0x10 entry of the research object's vtable. If the native consumer calls it, the
    /// "returns an object whose vtable +0x10 is invoked" reading is proven at runtime rather than
    /// inferred from a decompile.
    /// </summary>
    private static nint ResearchObjectPlus10()
    {
        Console.WriteLine("RESEARCH the consumer called the returned object's vtable +0x10 with no arguments.");
        Console.Out.Flush();
        return 0;
    }

    private const int ZeroAnswerBytes = 64 * 1024;
    private static nint ResearchStringPointer;

    /// <summary>
    /// Prints the NATIVE return addresses of the call that reached this reverse-P/Invoke stub.
    /// A managed stack trace collapses a native caller into one opaque frame, so the addresses are
    /// read straight out of each frame: that is what turns "some slot is fatal" into a decompilable
    /// call site (research only - the production placeholders still FailFast).
    /// </summary>
    private static void ReportNativeCallers(int slot)
    {
        try
        {
            // StackFrame.GetIPAddress is internal; reflection is the only supported way to read a
            // reverse-P/Invoke's native return addresses, and it is the whole point of this probe.
            var getIp = typeof(System.Diagnostics.StackFrame).GetMethod("GetIPAddress",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var trace = new System.Diagnostics.StackTrace(true);
            for (var index = 0; index < trace.FrameCount; index++)
            {
                var frame = trace.GetFrame(index);
                if (frame is null) continue;
                var method = frame.GetMethod();
                var address = getIp?.Invoke(frame, null);
                if (address is not nint ip || ip == nint.Zero) continue;
                Console.WriteLine($"RESEARCH IGame +0x{slot:X} native caller {index}: 0x{ip.ToInt64():X} (managed: {method?.DeclaringType?.Name}.{method?.Name})");
            }
        }
        catch (Exception error)
        {
            Console.WriteLine($"RESEARCH IGame +0x{slot:X} caller capture failed: {error.Message}");
        }
    }

    private void PublishGameServices()    {
        using var operation = EnterGameFrameworkPhase(shutdown: false);
        RequireGameFramework(_gameServices is not null && _gameFlowNodes is not null &&
            _gameFrameworkInitialized && !_gameFrameworkPostInitializationStarted && !_gamePublicationStarted,
            "Publish the complete owned game service once between framework Init and PostInit.");
        ValidateGameFrameworkOwner(_system);
        _gameFlowNodes!.ThrowIfFaulted();
        RequireGameFramework(_environment == _system + 0x50 && Marshal.ReadIntPtr(_system + 0x58) == 0 &&
            VirtualAddress(_system, 0x2f8) == _systemModule + 0xb6b0,
            "CSystem has a foreign game publication or an unsupported game setter.");
        //3650B6B0 only stores the borrowed pointer. The claimed engine roots every allocation
        // before entering native code, including uncertain calls that never return normally.
        _gamePublicationStarted = true;
        InvokeGameFramework(() =>
        {
            ZoneNativePhysicsModule.Virtual<PublishGameCall>(_system, 0x2f8)(_system, _gameServices!.Pointer);
            return 0;
        });
        _gamePublished = true;
        RequireGameServicePublication();
        // Original game CVars precede framework PostInit. Real console registration can now
        // use this published game's held operation; factory construction did no native work.
        _flowGameModeServices?.InitializeUnderCallback();
    }

    private void RequireGameServicePublication()
    {
        RequireGameFramework(_gamePublished && _gameServices is not null && _system != 0 &&
            _environment == _system + 0x50 && Marshal.ReadIntPtr(_system + 0x58) == _gameServices.Pointer,
            "The native game publication no longer belongs to this engine generation.");
        _gameFlowNodes!.ThrowIfFaulted();
    }

    internal void VerifyFlowCallbackAccess() => VerifyGameCallback();

    internal void ValidateFlowFactoryConstruction()
    {
        VerifyAccess();
        if (!_usesGameFramework || _disposed || _disposing || _gameFrameworkShutdownStarted || _fault is not null)
            throw new InvalidOperationException("FlowNode factories require their live framework engine owner.", _fault);
    }

    internal void ThrowIfFlowCallbackFaulted()
    {
        VerifyAccess();
        if (_fault is not null) throw new InvalidOperationException("The owning FlowNode engine is faulted.", _fault);
        // Managed state only, with no recursive registry/factory validation or native reads.
        _context?.ThrowIfFaulted();
    }

    internal void RecordFlowCallbackFault(Exception error) => Poison(error);

    internal nint RequireFlowCallbackEnvironment(bool destruction = false)
    {
        VerifyGameCallback();
        if (_system == 0)
        {
            if (destruction && _disposing && _gameFrameworkShutdownReturned) return 0;
            throw new InvalidOperationException("This FlowNode callback requires a live native engine environment.");
        }
        // Native graph/node teardown precedes CSystem release inside framework shutdown.
        // Later factory-cache destruction takes the explicit completed-System branch above.
        if (_environment == 0 || _environment != _system + 0x50)
            throw new InvalidOperationException("The FlowNode callback has no owning engine environment.");
        return _environment;
    }

    internal nint RequireFlowCallbackModule(string exactName)
    {
        VerifyGameCallback();
        if (_gameFrameworkModule == 0 || !_admittedModules.Contains(exactName))
            throw new InvalidOperationException("This FlowNode callback requires a retained, verified native module.");
        return RequiredModule(exactName);
    }

    internal nint GetFlowCallbackClientActor()
    {
        _ = RequireFlowCallbackEnvironment();
        //306F9930 follows the optional native game context and its client-channel entity ID
        // through306E5710 to ActorSystem+10. Null is an actual provider result, not a mode guess.
        return BindGameFramework<PointerCall>(0x148, 0x1f9930)(_gameFramework);
    }

    private void VerifyGameCallback()
    {
        VerifyAccess();
        if (!_claimed || !_gamePublished || !_operationActive || _disposed || _fault is not null)
            throw new InvalidOperationException("A game callback escaped its owning engine operation.", _fault);
        // Do not read CSystem here: nested shutdown callbacks can outlive its native release.
        _context!.ThrowIfFaulted();
        _gameFlowNodes!.ThrowIfFaulted();
    }

    private void RegisterGameFlowNodes()
    {
        VerifyGameCallback();
        RequireGameFramework(_gameFrameworkPostInitializationStarted && !_gameFrameworkPostInitializationReturned &&
            !_disposing && !_ready, "Register game FlowNodes only during the actual framework PostInit callback.");
        RequireGameServicePublication();
        ValidateGameFrameworkOwner(_system);
        var flowSystem = BindGameFramework<PointerCall>(0xb8, 0x1f7ad0)(_gameFramework);
        RequireGameFramework(flowSystem != 0 && flowSystem == Marshal.ReadIntPtr(_gameFramework + 0x4f8) &&
            flowSystem == Marshal.ReadIntPtr(_system + 0x118),
            "The game callback requires this framework's published FlowSystem.");
        // Native+10 ignores registration IDs; the owned registry validates each actual result.
        // This is a leaf under the already-held startup operation, not EnterOperation reentry.
        _gameFlowNodes!.Register(flowSystem, _gameFrameworkModule);
        _gameFlowNodesRegistered = true;
    }

    private void RetireGameFactories()
    {
        using var operation = EnterGameFrameworkPhase(shutdown: true);
        RequireGameFramework(_gameFrameworkShutdownReturned && _system == 0 && _gameFrameworkModule != 0,
            "Retire game factory caches after graphs/framework/System and before native module release.");
        _gameFlowNodes!.RetireAfterFrameworkShutdown();
        _flowGameModeServices?.RetireAfterFrameworkShutdown();
    }

    private void ReleaseGameServices()
    {
        if (_gameFlowNodes is not null)
        {
            if (_claimed)
            {
                RequireGameFramework(_gameFrameworkShutdownReturned && _system == 0 &&
                    _gameFrameworkModule == 0 && _systemModule == 0 && _commonModule == 0,
                    "Retain game-service callback storage through all native module releases.");
                _gameFlowNodes.ReleaseAfterNativeModules();
            }
            else _gameFlowNodes.ReleaseUnpublished();
            _gameFlowNodes = null;
        }
        ReleaseFlowGameModeServices();
        ReleaseFlowPlayerServices();
        _gameServices?.Release();
        _gameServices = null;
        _gamePublished = false;
    }

    /// <summary>
    /// Main IGame identity only; the reviewed consumers read its vtable, not original X2 fields.
    /// Engine-owned storage has no independent public disposal, finalizer or native delete path.
    /// </summary>
    private sealed unsafe class GameServices
    {
        private const int SlotCount = 152; //39FC6E48 through39FC7300; next word is not a slot.
        private readonly ZoneNativeEngine _engine;
        private readonly Delegate[] _callbacks = new Delegate[SlotCount];
        private ZoneNativeModelDescriptors? _models;
        private nint _identity;
        private nint _table;

        internal GameServices(ZoneNativeEngine engine, ZoneModelContent models, ZoneModelAuxiliaryContent auxiliary)
        {
            _engine = engine;
            try
            {
                _models = new ZoneNativeModelDescriptors(models, auxiliary);
                _models.ValidateAiStartupModels();
                _table = Marshal.AllocHGlobal(SlotCount * PointerSize);
                _identity = Marshal.AllocHGlobal(PointerSize);
                for (var index = 0; index < SlotCount; index++)
                {
                    var diagnostic = $"Unsupported native IGame callback +0x{index * 8:X}. This isolated engine must terminate; see the first-entry TODO.";
                    Bind(index * 8, new UnsupportedGameCall(_ => Environment.FailFast(diagnostic)));
                }
                Bind(0x10, new GamePostInitCall(Register));
                Bind(0x138, new GameModelCall(LookupModel));
                Bind(0x410, new PointerCall(GetFramework));
                // IGame +0xE8 - the character-spawn query. The native consumer NULL-CHECKS the
                // answer: a probe spawn asked for it 8 times and tolerated null every time, and with
                // null the WHOLE sequence completed (spawn -> load-character -> initialize) and the
                // native character was created (id 783). Before this binding the slot was a fatal
                // placeholder, which is what killed the zone host on the first real client login.
                // Null is the truthful answer while the managed host publishes no player state
                // object: it says "not available", not "pretend one exists". A real state object is
                // the named follow-up (TODO(player-lifecycle): publish it and answer it here).
                Bind(CharacterSpawnSlot, new PointerCall(_ => 0));
                // RESEARCH OVERRIDE (probes only): an unknown slot may be answered with a string
                // so its real contract can be observed instead of guessed. The production host
                // never sets this, so the fatal placeholders above stay authoritative there.
                if (ResearchStringSlot is int researchSlot)
                {
                    var slot = researchSlot / 8;
                    if (slot < 0 || slot >= SlotCount)
                        throw new ArgumentOutOfRangeException(nameof(ResearchStringSlot), researchSlot, "IGame slot is outside the table.");
                    // Three research answers, each a DISCRIMINATOR rather than a guess:
                    //   "(zero)"    a 64 KB zeroed page: a valid empty string, so a char* caller
                    //               would continue and an object caller would fault on a null vtable.
                    //   "(object)"  a real object with a vtable whose +0x10 logs when invoked: if
                    //               the consumer calls it, the contract is PROVEN, not inferred.
                    //   anything else  a NUL-terminated string.
                    var answer = ResearchStringValue;
                    if (answer == NullAnswerToken)
                    {
                        // CONTROL: answer null. If the consumer null-checks (as FUN_3930d780 does)
                        // the run continues, which proves the crash is caused by a NON-null answer;
                        // if it faults here too, the crash is not about the return value at all.
                        ResearchStringPointer = 0;
                        ResearchStringValue = string.Empty;
                    }
                    else if (answer == ObjectAnswerToken || answer == InstrumentedAnswerToken)
                    {
                        // An instrumented object: every vtable entry AND every data field points at a
                        // stub that reports which one the consumer invoked. This turns "it crashed
                        // after the callback" into "it called vtable +0x18" - the only way to learn
                        // the real contract without guessing.
                        BuildResearchVtable();
                        var researchObject = Marshal.AllocHGlobal(ResearchObjectBytes);
                        for (var offset = 0; offset < ResearchObjectBytes; offset += PointerSize)
                            Marshal.WriteIntPtr(researchObject, offset, 0);
                        Marshal.WriteIntPtr(researchObject, 0, _researchVtable);
                        for (var offset = PointerSize; offset < ResearchDataFields; offset += PointerSize)
                            Marshal.WriteIntPtr(researchObject, offset, _researchFieldStub((offset / PointerSize - 1) * PointerSize));
                        ResearchStringPointer = researchObject;
                        ResearchStringValue = string.Empty;
                    }
                    else
                    {
                        if (answer == ZeroAnswerToken)
                        {
                            ResearchStringPointer = Marshal.AllocHGlobal(ZeroAnswerBytes);
                            for (var offset = 0; offset < ZeroAnswerBytes; offset += PointerSize)
                                Marshal.WriteIntPtr(ResearchStringPointer, offset, 0);
                            ResearchStringValue = string.Empty;
                        }
                        else
                        {
                            ResearchStringValue = answer ?? string.Empty;
                            ResearchStringPointer = Marshal.StringToCoTaskMemUTF8(ResearchStringValue);
                        }
                    }
                    Bind(researchSlot, new PointerCall(_ =>
                    {
                        Console.WriteLine($"RESEARCH IGame +0x{researchSlot:X} called; answering {(answer == NullAnswerToken ? "NULL (control)" : answer == InstrumentedAnswerToken ? "an INSTRUMENTED object (every vtable entry and data field reports itself)" : answer == ObjectAnswerToken ? "an OBJECT with a logging vtable+0x10" : answer == ZeroAnswerToken ? $"a {ZeroAnswerBytes}-byte ZEROED page" : $"a NUL-terminated string ({ResearchStringValue.Length} chars)")}.");
                        Console.Out.Flush();
                        return ResearchStringPointer;
                    }));
                }
                Marshal.WriteIntPtr(_identity, _table);
            }
            catch { Release(); throw; }
        }

        internal nint Pointer => _identity != 0 ? _identity :
            throw new ObjectDisposedException(nameof(GameServices));

        /// <summary>IGame +0xE8: the callback the character spawn path reaches (a null-checked state query).</summary>
        private const int CharacterSpawnSlot = 0xE8;

        /// <summary>True once that slot carries a real binding rather than the fatal placeholder.</summary>
        internal bool CharacterSpawnPublished => _callbacks[CharacterSpawnSlot / 8] is not UnsupportedGameCall;

        private byte Register(nint self)
        {
            try
            {
                ValidateReceiver(self);
                _engine.RegisterGameFlowNodes();
                return 1;
            }
            catch (Exception error)
            {
                TerminateCallback("IGame FlowNode registration failed (+0x10).", error);
                throw; // Unreachable after FailFast; no exception crosses the native callback.
            }
        }

        private nint LookupModel(nint self, uint modelId)
        {
            try
            {
                ValidateReceiver(self);
                return _models!.LookupModel(modelId);
            }
            catch (Exception error)
            {
                TerminateCallback("IGame model lookup failed (+0x138).", error);
                throw; // Unreachable after FailFast; no exception crosses the native callback.
            }
        }

        private void ValidateReceiver(nint self)
        {
            _engine.VerifyGameCallback();
            if (self != Pointer || Marshal.ReadIntPtr(_identity) != _table)
                throw new InvalidOperationException("Native code used a foreign or changed IGame identity.");
        }

        private nint GetFramework(nint self)
        {
            try
            {
                ValidateReceiver(self);
                // Native39DB6BF0 returns the framework retained at original game+28.
                return _engine._gameFramework;
            }
            catch (Exception error)
            {
                TerminateCallback("IGame framework lookup failed (+0x410).", error);
                throw;
            }
        }

        [DoesNotReturn]
        private void TerminateCallback(string message, Exception error)
        {
            _engine.Poison(error);
            // The native framework ignores +10's return, and AI immediately dereferences model
            // results. Neither a fake return nor a managed exception crossing this ABI is safe.
            Environment.FailFast(message, error);
        }

        private void Bind(int offset, Delegate callback)
        {
            _callbacks[offset / 8] = callback;
            Marshal.WriteIntPtr(_table + offset, Marshal.GetFunctionPointerForDelegate(callback));
        }

        internal void Release()
        {
            // Called by construction rollback before publication, or final engine-owned teardown.
            _models?.Dispose();
            _models = null;
            if (_identity != 0) Marshal.FreeHGlobal(_identity);
            if (_table != 0) Marshal.FreeHGlobal(_table);
            _identity = 0;
            _table = 0;
            Array.Clear(_callbacks);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void PublishGameCall(nint system, nint game);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void UnsupportedGameCall(nint self);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate byte GamePostInitCall(nint self);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint GameModelCall(nint self, uint modelId);
}

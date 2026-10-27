using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace AAEmu.Zone.Simulation;

/// <summary>
/// Owns a real behavior family's native factory, nine-slot nodes, reference counts and stable
/// configuration storage. The supplied creator implements actual Create and fresh-state Clone;
/// singleton factories use the proven retained-self Clone. No behavior callback has a fallback.
/// </summary>
internal sealed unsafe class ZoneNativeFlowNodeFactory : IZoneNativeFlowNodeFactoryOwner
{
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private readonly IZoneNativeFlowNodeRuntime _runtime;
    private readonly Func<ZoneNativeFlowActivation?, IZoneNativeFlowNodeBehavior> _create;
    private readonly Action _validateFamily;
    private readonly bool _singleton;
    private readonly int _nodeSize;
    private readonly List<Delegate> _callbacks = [];
    private readonly Dictionary<nint, Node> _nodes = [];
    private readonly Dictionary<ZoneNativeFlowNodeConfiguration, ZoneNativeFlowNodeConfigurationStorage> _configurations =
        new(ReferenceEqualityComparer.Instance);
    private nint _factory;
    private nint _factoryTable;
    private nint _nodeTable;
    private nint _name;
    private Node? _cached;
    private GCHandle _lifetimeRoot;
    private Exception? _fault;
    private int _factoryReferences;
    private int _callbackDepth;
    private bool _nativeStarted;
    private bool _retiring;
    private bool _retired;
    private bool _released;

    public string Name { get; }
    public nint FactoryPointer => _factory;

    /// <summary>
    /// Creates only managed/native interface storage, without invoking behavior or native code.
    /// For ordinary factories, the family must prove that Clone uses this same fresh creator.
    /// The creator returns a fresh behavior; singleton reuse is owned by this adapter.
    /// </summary>
    internal ZoneNativeFlowNodeFactory(string name, int nativeNodeSize, bool singleton,
        IZoneNativeFlowNodeRuntime runtime, Func<ZoneNativeFlowActivation?, IZoneNativeFlowNodeBehavior> create,
        Action validateFamily)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (name.Contains('\0')) throw new ArgumentException("A factory name cannot contain NUL.", nameof(name));
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(create);
        ArgumentNullException.ThrowIfNull(validateFamily);
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("Native flow-node callbacks require Windows x64.");
        if (nativeNodeSize < 0x10 || nativeNodeSize % 8 != 0)
            throw new ArgumentOutOfRangeException(nameof(nativeNodeSize));
        Name = name;
        _nodeSize = nativeNodeSize;
        _singleton = singleton;
        _runtime = runtime;
        _create = create;
        _validateFamily = validateFamily;
        runtime.ValidateForRegistration();
        validateFamily();
        try
        {
            _factoryTable = Allocate(4 * 8);
            _nodeTable = Allocate(9 * 8);
            Set(_factoryTable, 0, new ReferenceCall(FactoryAddRef));
            Set(_factoryTable, 8, new ReferenceCall(FactoryRelease));
            Set(_factoryTable, 0x10, new SmartPointerCall(FactoryCreate));
            Set(_factoryTable, 0x18, new MemoryCall(FactoryMemory));
            Set(_nodeTable, 0, new ReferenceCall(NodeAddRef));
            Set(_nodeTable, 8, new ReferenceCall(NodeRelease));
            Set(_nodeTable, 0x10, new SmartPointerCall(NodeClone));
            Set(_nodeTable, 0x18, new ConfigurationCall(NodeConfiguration));
            Set(_nodeTable, 0x20, new XmlCall(NodeXml));
            Set(_nodeTable, 0x28, new SerializeCall(NodeSerialize));
            Set(_nodeTable, 0x30, new EventCall(NodeEvent));
            Set(_nodeTable, 0x38, new MemoryCall(NodeMemory));
            Set(_nodeTable, 0x40, new DeleteCall(NodeDelete));
            var bytes = ZoneNativeFlowValues.StringBytes(name);
            _name = Allocate(bytes.Length);
            bytes.CopyTo(new Span<byte>((void*)_name, bytes.Length));
            _factory = Allocate(singleton ? 0x20 : 0x18);
            Marshal.WriteIntPtr(_factory, _factoryTable);
            Marshal.WriteIntPtr(_factory + 8, _name);
        }
        catch { FreeInterfaceStorage(); throw; }
    }

    public void ValidateForRegistration()
    {
        VerifyThread();
        ThrowIfFaulted();
        if (_retiring || _retired || _released || _factory == 0 || Marshal.ReadIntPtr(_factory) != _factoryTable ||
            Marshal.ReadIntPtr(_factory + 8) != _name)
            throw new InvalidOperationException("The real factory is outside its retained registration lifetime.");
        _runtime.ValidateForRegistration();
        _validateFamily();
    }

    public void ThrowIfFaulted()
    {
        VerifyThread();
        if (_fault is not null) throw new InvalidOperationException("Native node ownership remains pinned after a callback fault.", _fault);
        _runtime.ThrowIfFaulted(); // Managed state only, including after CSystem is destroyed.
    }

    public void ReleaseUnpublished()
    {
        VerifyThread();
        ThrowIfFaulted();
        if (_released || _nativeStarted || _callbackDepth != 0 || _nodes.Count != 0)
            throw new InvalidOperationException("Only unused native factory storage can be released without framework teardown.");
        FreeInterfaceStorage();
        _released = true;
    }

    public void RetireAfterFrameworkShutdown()
    {
        VerifyThread();
        ThrowIfFaulted();
        _runtime.VerifyCallback();
        if (_retiring || _retired || _released || _callbackDepth != 0 || _factoryReferences != 0)
            throw new InvalidOperationException("Retire actual graph/registry references before factory-owned caches.");
        RetainLifetime();
        _retiring = true;
        try
        {
            if (_cached is not null)
            {
                if (_cached.References != 1 || _nodes.Count != 1)
                    throw new InvalidOperationException("Native graph references still retain a singleton node.");
                ReleaseNode(_cached);
                _cached = null;
                Marshal.WriteIntPtr(_factory + 0x18, 0);
            }
            if (_nodes.Count != 0) throw new InvalidOperationException("Native graph references still retain ordinary nodes.");
            foreach (var configuration in _configurations.Values) configuration.Retire();
            _runtime.ThrowIfFaulted();
            _retired = true;
        }
        catch (Exception error) { RecordFault(error); throw; }
    }

    public void ReleaseAfterNativeModules()
    {
        VerifyThread();
        ThrowIfFaulted();
        if (!_retired || _released || _callbackDepth != 0 || _factoryReferences != 0 || _nodes.Count != 0)
            throw new InvalidOperationException("Retire all native node payloads before releasing callback storage.");
        FreeInterfaceStorage();
        _released = true;
        if (_lifetimeRoot.IsAllocated) _lifetimeRoot.Free();
    }

    private void FactoryAddRef(nint self) => Callback(() =>
    {
        RequireFactory(self);
        _factoryReferences = checked(_factoryReferences + 1);
    });

    private void FactoryRelease(nint self) => Callback(() =>
    {
        RequireFactory(self);
        if (_factoryReferences <= 0) throw new InvalidOperationException("A factory reference was released without acquisition.");
        _factoryReferences--;
        // Original CG2 static factories do not free themselves on Release. Interface storage
        // remains alive for all registry callbacks and is released only after native modules.
    });

    private nint FactoryCreate(nint self, nint result, nint activation) => Callback(() =>
    {
        RequireFactory(self);
        RequireResult(result);
        Node node;
        if (_singleton)
        {
            if (_cached is null)
            {
                _cached = CreateNode(activation);
                AddReference(_cached); // The original factory's independent singleton cache.
                Marshal.WriteIntPtr(_factory + 0x18, _cached.Pointer);
            }
            node = _cached;
        }
        else node = CreateNode(activation);
        ReturnReference(node, result);
        return result;
    });

    private nint NodeClone(nint self, nint result, nint activation) => Callback(() =>
    {
        var node = RequireNode(self);
        RequireResult(result);
        ReturnReference(_singleton ? node : CreateNode(activation), result);
        return result;
    });

    private void NodeAddRef(nint self) => Callback(() => AddReference(RequireNode(self)));
    private void NodeRelease(nint self) => Callback(() => ReleaseNode(RequireNode(self)));

    private void NodeConfiguration(nint self, nint target) => Callback(() =>
    {
        var node = RequireNode(self);
        Configuration(node.Behavior.Configuration).CopyTo(target);
    });

    private byte NodeXml(nint self, nint activation, nint xmlReference, byte loading) => Callback(() =>
    {
        var node = RequireNode(self);
        return node.Behavior.SerializeXml(Activation(node, activation), xmlReference, loading != 0) ? (byte)1 : (byte)0;
    });

    private void NodeSerialize(nint self, nint activation, nint serializer) => Callback(() =>
    {
        var node = RequireNode(self);
        node.Behavior.Serialize(Activation(node, activation), serializer);
    });

    private void NodeEvent(nint self, int eventId, nint activation) => Callback(() =>
    {
        var node = RequireNode(self);
        node.Behavior.ProcessEvent(eventId, Activation(node, activation));
    });

    private nint NodeDelete(nint self, uint flags) => Callback(() =>
    {
        var node = RequireNode(self);
        if (node.References > 0) throw new InvalidOperationException("Do not directly destroy a referenced flow node.");
        DestroyNode(node, flags);
        return self;
    });

    private void FactoryMemory(nint self, nint sizer) => Callback(() =>
    {
        RequireFactory(self);
        AddMemory(sizer, self, _singleton ? 0x20 : 0x18);
    });

    private void NodeMemory(nint self, nint sizer) => Callback(() =>
    {
        RequireNode(self);
        AddMemory(sizer, self, _nodeSize);
    });

    private Node CreateNode(nint activation)
    {
        // The owner is rooted before invoking a real creator, which may call native services.
        var input = activation == 0 ? (ZoneNativeFlowActivation?)null : ZoneNativeFlowActivation.Read(_runtime, activation, 256);
        var behavior = _create(input) ?? throw new InvalidOperationException("The real creator returned no flow-node behavior.");
        if (_nodes.Values.Any(existing => ReferenceEquals(existing.Behavior, behavior)))
            throw new InvalidOperationException("A real creator reused a behavior already owned by another native node.");
        var node = new Node(Allocate(_nodeSize), behavior);
        _nodes.Add(node.Pointer, node);
        Marshal.WriteIntPtr(node.Pointer, _nodeTable);
        if (behavior is IZoneNativeFlowNodeStorageBehavior storage) storage.BindNativeStorage(node.Pointer, _nodeSize);
        _runtime.ThrowIfFaulted();
        RequireNode(node.Pointer);
        return node;
    }

    private ZoneNativeFlowActivation Activation(Node node, nint pointer) =>
        ZoneNativeFlowActivation.Read(_runtime, pointer, node.Behavior.Configuration.Inputs.Count);

    private ZoneNativeFlowNodeConfigurationStorage Configuration(ZoneNativeFlowNodeConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (!_configurations.TryGetValue(configuration, out var storage))
        {
            storage = new ZoneNativeFlowNodeConfigurationStorage(_runtime, configuration);
            _configurations.Add(configuration, storage);
        }
        return storage;
    }

    private static void AddReference(Node node)
    {
        node.References = checked(node.References + 1);
        Marshal.WriteInt32(node.Pointer + 8, node.References);
    }

    private void ReleaseNode(Node node)
    {
        node.References = checked(node.References - 1);
        Marshal.WriteInt32(node.Pointer + 8, node.References);
        if (node.References <= 0) DestroyNode(node, 1); //3920C3E0 -> deleting destructor +40.
    }

    private void DestroyNode(Node node, uint flags)
    {
        node.Destroying = true;
        node.Behavior.Destroy();
        _runtime.ThrowIfFaulted();
        if ((flags & 1) != 0)
        {
            _nodes.Remove(node.Pointer);
            NativeMemory.Free((void*)node.Pointer);
        }
        // A non-deleting destructor retains its storage and therefore prevents final release
        // until a separately proved owner can retire it. It cannot become a usable live node.
    }

    private static void ReturnReference(Node node, nint result)
    {
        Marshal.WriteIntPtr(result, node.Pointer);
        AddReference(node);
    }

    private Node RequireNode(nint pointer)
    {
        if (!_nodes.TryGetValue(pointer, out var node) || node.Destroying ||
            Marshal.ReadIntPtr(pointer) != _nodeTable || Marshal.ReadInt32(pointer + 8) != node.References)
            throw new InvalidOperationException("The callback does not identify this owner's live flow node.");
        return node;
    }

    private void RequireFactory(nint self)
    {
        if (self != _factory || self == 0 || Marshal.ReadIntPtr(self) != _factoryTable || _retired || _retiring)
            throw new InvalidOperationException("The callback does not identify this owner's active factory.");
    }

    private static void RequireResult(nint result)
    {
        if (result == 0) throw new InvalidOperationException("The native smart-pointer result storage is missing.");
    }

    private static void AddMemory(nint sizer, nint pointer, int size)
    {
        if (sizer == 0) throw new InvalidOperationException("The native memory sizer is missing.");
        var method = Marshal.ReadIntPtr(Marshal.ReadIntPtr(sizer) + 0x30);
        ((delegate* unmanaged[Stdcall]<nint, nint, nuint, byte>)method)(sizer, pointer, (nuint)size);
    }

    private void Callback(Action action) => Callback(() => { action(); return 0; });

    private T Callback<T>(Func<T> action)
    {
        try
        {
            VerifyThread();
            ThrowIfFaulted();
            _runtime.VerifyCallback();
            if (_released || _retired) throw new InvalidOperationException("A native callback reached retired factory storage.");
            RetainLifetime();
            _nativeStarted = true;
            _callbackDepth++;
            try
            {
                var result = action();
                ThrowIfFaulted();
                return result;
            }
            finally { _callbackDepth--; }
        }
        catch (Exception error) { FailCallback(error); throw; }
    }

    private void RetainLifetime()
    {
        if (!_lifetimeRoot.IsAllocated) _lifetimeRoot = GCHandle.Alloc(this);
    }

    private void RecordFault(Exception error)
    {
        _fault ??= error;
        RetainLifetime();
        _runtime.RecordFault(error);
    }

    [DoesNotReturn]
    private void FailCallback(Exception error)
    {
        try { RecordFault(error); }
        catch (Exception recordingError) { error = new AggregateException(error, recordingError); }
        Environment.FailFast($"The real native flow-node callback for '{Name}' failed; no callback result was fabricated.", error);
    }

    private void Set(nint table, int offset, Delegate callback)
    {
        _callbacks.Add(callback);
        Marshal.WriteIntPtr(table + offset, Marshal.GetFunctionPointerForDelegate(callback));
    }

    private static nint Allocate(int size)
    {
        var pointer = (nint)NativeMemory.AllocZeroed((nuint)size);
        if (pointer == 0) throw new OutOfMemoryException();
        return pointer;
    }

    private void FreeInterfaceStorage()
    {
        NativeMemory.Free((void*)_factory); _factory = 0;
        NativeMemory.Free((void*)_name); _name = 0;
        NativeMemory.Free((void*)_factoryTable); _factoryTable = 0;
        NativeMemory.Free((void*)_nodeTable); _nodeTable = 0;
        _callbacks.Clear();
    }

    private void VerifyThread()
    {
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new InvalidOperationException("Real flow-node ownership belongs to the engine thread.");
    }

    private sealed class Node(nint pointer, IZoneNativeFlowNodeBehavior behavior)
    {
        internal readonly nint Pointer = pointer;
        internal readonly IZoneNativeFlowNodeBehavior Behavior = behavior;
        internal int References;
        internal bool Destroying;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void ReferenceCall(nint self);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint SmartPointerCall(nint self, nint result, nint activation);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void ConfigurationCall(nint self, nint configuration);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate byte XmlCall(nint self, nint activation, nint xmlNodeReference, byte loading);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void SerializeCall(nint self, nint activation, nint serializer);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void EventCall(nint self, int eventId, nint activation);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void MemoryCall(nint self, nint sizer);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint DeleteCall(nint self, uint flags);
}

using System.Runtime.InteropServices;
using System.Text;

using AAEmu.Zone.Native.Engine;

namespace AAEmu.Zone.Native.FlowNodes;

/// <summary>Uses the actual engine operation; callbacks do not enter a second native gate.</summary>
internal sealed class ZoneNativeFlowNodeRuntime(ZoneNativeEngine engine) : IZoneNativeFlowNodeRuntime
{
    public void ValidateForRegistration() => engine.ValidateFlowFactoryConstruction();
    public void VerifyCallback() => engine.VerifyFlowCallbackAccess();
    public void ThrowIfFaulted() => engine.ThrowIfFlowCallbackFaulted();
    public void RecordFault(Exception error) => engine.RecordFlowCallbackFault(error);
    public nint RequireCryActionModule() => engine.RequireFlowCallbackModule("cryaction_dedicate.dll");
}

/// <summary>
/// The real CryAction configurable-variant helpers. Only managed defaults exist before engine
/// initialization. Complex payloads use native allocation/copy/destruction, never fake type tables.
/// </summary>
internal static unsafe class ZoneNativeFlowValues
{
    private static readonly int[] DescriptorRvas = [0x2c5d10, 0x2c5cd0, 0x2c5c90, 0x2c5c50, 0x2c5b00, 0x2c5df0, 0x2c5fe0, 0x2c5c10];

    internal static nint Descriptor(IZoneNativeFlowNodeRuntime runtime, int type)
    {
        runtime.VerifyCallback();
        if ((uint)type >= DescriptorRvas.Length) throw new ArgumentOutOfRangeException(nameof(type));
        var module = runtime.RequireCryActionModule();
        var descriptor = module + DescriptorRvas[type];
        if (Marshal.ReadInt32(descriptor) != type || Marshal.ReadIntPtr(descriptor + 8) == 0 ||
            Marshal.ReadIntPtr(descriptor + 0x18) == 0)
            throw new InvalidOperationException("The native flow-value descriptor changed.");
        return descriptor;
    }

    internal static void Initialize(IZoneNativeFlowNodeRuntime runtime, nint storage, ZoneNativeFlowValue value)
    {
        var descriptor = Descriptor(runtime, value.NativeType);
        // Native typed port defaults set bit0; void defaults leave it clear. Bit1 belongs to
        // runtime input activation and is not set by configuration construction.
        Marshal.WriteIntPtr(storage, descriptor | (value.NativeType == 0 ? 0 : 1));
        ulong source = 0;
        switch (value.NativeType)
        {
            case 0: break;
            case 1: source = unchecked((uint)(int)value.Data!); break;
            case 2: source = unchecked((ulong)(long)value.Data!); break;
            case 3: source = unchecked((uint)BitConverter.SingleToInt32Bits((float)value.Data!)); break;
            case 4: source = (uint)value.Data!; break;
            case 7: source = (bool)value.Data! ? 1UL : 0; break;
            case 5:
            {
                //305507F0 assigns raw XYZ into a valid existing variant, preserving low flags.
                Marshal.WriteIntPtr(storage, Descriptor(runtime, 0) | 1);
                var vector = (System.Numerics.Vector3)value.Data!;
                var module = runtime.RequireCryActionModule();
                ((delegate* unmanaged[Stdcall]<nint, System.Numerics.Vector3*, void>)(module + 0x507f0))(storage, &vector);
                runtime.ThrowIfFaulted();
                if (Marshal.ReadIntPtr(storage + 8) == 0)
                    throw new OutOfMemoryException("Native flow Vec3 construction returned no owned payload.");
                return;
            }
            case 6:
            {
                Marshal.WriteIntPtr(storage, Descriptor(runtime, 0) | 1);
                var bytes = StringBytes((string)value.Data!);
                var module = runtime.RequireCryActionModule();
                nint text = 0;
                fixed (byte* data = bytes)
                {
                    //305093D0 constructs a genuine CryString with the native allocator/header.
                    ((delegate* unmanaged[Stdcall]<nint*, byte*, nuint, nint>)(module + 0x93d0))(&text, data, (nuint)(bytes.Length - 1));
                }
                runtime.ThrowIfFaulted();
                ((delegate* unmanaged[Stdcall]<nint, nint*, void>)(module + 0x508c0))(storage, &text);
                runtime.ThrowIfFaulted();
                ((delegate* unmanaged[Stdcall]<nint*, void>)Marshal.ReadIntPtr(descriptor + 8))(&text);
                runtime.ThrowIfFaulted();
                return;
            }
            default: throw new NotSupportedException("The flow-value type has no native construction binding.");
        }
        ((delegate* unmanaged[Stdcall]<nint, ulong*, void>)Marshal.ReadIntPtr(descriptor + 0x18))(storage + 8, &source);
        runtime.ThrowIfFaulted();
    }

    internal static void Destroy(IZoneNativeFlowNodeRuntime runtime, nint storage)
    {
        runtime.VerifyCallback();
        var descriptor = Marshal.ReadIntPtr(storage) & ~3;
        var type = Marshal.ReadInt32(descriptor);
        if (descriptor != Descriptor(runtime, type))
            throw new InvalidOperationException("A configuration default changed native descriptor ownership.");
        ((delegate* unmanaged[Stdcall]<nint, void>)Marshal.ReadIntPtr(descriptor + 8))(storage + 8);
        runtime.ThrowIfFaulted();
    }

    internal static bool TryGetFloat(IZoneNativeFlowNodeRuntime runtime, nint storage, out float value)
    {
        runtime.VerifyCallback();
        var module = runtime.RequireCryActionModule();
        float result = 0;
        //305753E0 uses the actual conversion visitor, including CryString sscanf semantics.
        var converted = ((delegate* unmanaged[Stdcall]<nint, float*, byte>)(module + 0x753e0))(storage, &result);
        runtime.ThrowIfFaulted();
        value = result;
        return converted != 0;
    }

    internal static byte[] StringBytes(string text)
    {
        var zero = text.IndexOf('\0');
        if (zero >= 0) text = text[..zero];
        return Encoding.UTF8.GetBytes(text + '\0');
    }
}

/// <summary>Stable configuration arrays are retired after graphs and before native modules.</summary>
internal sealed unsafe class ZoneNativeFlowNodeConfigurationStorage
{
    private readonly IZoneNativeFlowNodeRuntime _runtime;
    private readonly ZoneNativeFlowNodeConfiguration _configuration;
    private readonly List<nint> _allocations = [];
    private readonly List<nint> _values = [];
    private nint _inputs;
    private nint _outputs;
    private nint _description;
    private bool _started;
    private bool _ready;
    private bool _retired;

    internal ZoneNativeFlowNodeConfigurationStorage(IZoneNativeFlowNodeRuntime runtime, ZoneNativeFlowNodeConfiguration configuration)
    { _runtime = runtime; _configuration = configuration; }

    internal void CopyTo(nint target)
    {
        _runtime.VerifyCallback();
        if (target == 0 || _retired) throw new InvalidOperationException("Native configuration storage is unavailable.");
        if (!_ready) Initialize();
        Marshal.WriteIntPtr(target, _inputs);
        Marshal.WriteIntPtr(target + 8, _outputs);
        var flags = unchecked((uint)Marshal.ReadInt32(target + 0x10));
        Marshal.WriteInt32(target + 0x10, unchecked((int)((flags & _configuration.FlagsAndMask) | _configuration.FlagsOrMask)));
        Marshal.WriteIntPtr(target + 0x18, _description);
    }

    private void Initialize()
    {
        if (_started) throw new InvalidOperationException("Do not replay partial native configuration construction.");
        _started = true;
        _inputs = Allocate(checked((_configuration.Inputs.Count + 1) * 0x30));
        _outputs = Allocate(checked((_configuration.Outputs.Count + 1) * 0x20));
        _description = StoreString(_configuration.Description);
        for (var i = 0; i < _configuration.Inputs.Count; i++)
        {
            var row = _configuration.Inputs[i];
            var target = _inputs + i * 0x30;
            Marshal.WriteIntPtr(target, StoreString(row.Name));
            Marshal.WriteIntPtr(target + 8, StoreString(row.HumanName));
            Marshal.WriteIntPtr(target + 0x10, StoreString(row.Description));
            Marshal.WriteIntPtr(target + 0x18, StoreString(row.UiConfig));
            _values.Add(target + 0x20); // Retain uncertain construction before native callbacks.
            ZoneNativeFlowValues.Initialize(_runtime, target + 0x20, row.DefaultValue);
        }
        var terminator = _inputs + _configuration.Inputs.Count * 0x30 + 0x20;
        _values.Add(terminator);
        ZoneNativeFlowValues.Initialize(_runtime, terminator, ZoneNativeFlowValue.Void());
        for (var i = 0; i < _configuration.Outputs.Count; i++)
        {
            var row = _configuration.Outputs[i];
            var target = _outputs + i * 0x20;
            Marshal.WriteIntPtr(target, StoreString(row.Name));
            Marshal.WriteIntPtr(target + 8, StoreString(row.HumanName));
            Marshal.WriteIntPtr(target + 0x10, StoreString(row.Description));
            Marshal.WriteInt32(target + 0x18, row.NativeType);
        }
        _ready = true;
    }

    internal void Retire()
    {
        if (_retired) throw new InvalidOperationException("Native configuration storage already retired.");
        if (_started && !_ready) throw new InvalidOperationException("Partial native configuration remains pinned.");
        for (var i = _values.Count - 1; i >= 0; i--) ZoneNativeFlowValues.Destroy(_runtime, _values[i]);
        foreach (var pointer in _allocations) NativeMemory.Free((void*)pointer);
        _allocations.Clear();
        _values.Clear();
        _retired = true;
    }

    private nint Allocate(int bytes)
    {
        var pointer = (nint)NativeMemory.AllocZeroed((nuint)bytes);
        if (pointer == 0) throw new OutOfMemoryException();
        _allocations.Add(pointer);
        return pointer;
    }

    private nint StoreString(string? text)
    {
        if (text is null) return 0;
        var bytes = ZoneNativeFlowValues.StringBytes(text);
        var pointer = Allocate(bytes.Length);
        bytes.CopyTo(new Span<byte>((void*)pointer, bytes.Length));
        return pointer;
    }
}

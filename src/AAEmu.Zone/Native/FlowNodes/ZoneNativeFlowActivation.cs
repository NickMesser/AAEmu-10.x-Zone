using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace AAEmu.Zone.Native.FlowNodes;

/// <summary>
/// Exact copied 0x30-byte activation context. Its graph/entity/input pointers are borrowed,
/// not retained independently of the owning live node. Operations require that node's current
/// engine callback; keeping this value does not extend the native graph's lifetime.
/// </summary>
internal readonly unsafe struct ZoneNativeFlowActivation
{
    private readonly ulong _word0, _word1, _word2, _word3, _word4, _word5;
    private readonly IZoneNativeFlowNodeRuntime? _runtime;
    private readonly int _inputCount;

    public nint Graph => (nint)_word0;
    public ushort NodeId => (ushort)_word1;
    public nint Entity => (nint)_word2;
    public byte ConnectionPort => (byte)_word3;
    public nint InputPorts => (nint)_word4;
    public nint UserData => (nint)_word5;
    public static ZoneNativeFlowActivation Empty => new(null, 0, 0, 0, 0, 0xff, 0, 0);

    private ZoneNativeFlowActivation(IZoneNativeFlowNodeRuntime? runtime, int inputCount,
        ulong word0, ulong word1, ulong word2, ulong word3, ulong word4, ulong word5)
    {
        _runtime = runtime;
        _inputCount = inputCount;
        _word0 = word0; _word1 = word1; _word2 = word2;
        _word3 = word3; _word4 = word4; _word5 = word5;
    }

    internal static ZoneNativeFlowActivation Read(IZoneNativeFlowNodeRuntime runtime, nint pointer, int inputCount)
    {
        runtime.VerifyCallback();
        if (pointer == 0) return new(runtime, inputCount, 0, 0, 0, 0xff, 0, 0);
        var words = (ulong*)pointer;
        return new(runtime, inputCount, words[0], words[1], words[2], words[3], words[4], words[5]);
    }

    public void CopyTo(nint target)
    {
        if (target == 0) throw new ArgumentNullException(nameof(target));
        var words = (ulong*)target;
        words[0] = _word0; words[1] = _word1; words[2] = _word2;
        words[3] = _word3; words[4] = _word4; words[5] = _word5;
    }

    public bool IsPortActive(int index) => (Marshal.ReadByte(Input(index)) & 2) != 0;

    public int GetNativePortType(int index)
    {
        var descriptor = Marshal.ReadIntPtr(Input(index)) & ~3;
        if (descriptor == 0) throw new InvalidOperationException("A native input value has no descriptor.");
        return Marshal.ReadInt32(descriptor);
    }

    public float GetFloat(int index)
    {
        RequireType(index, 3);
        return BitConverter.Int32BitsToSingle(Marshal.ReadInt32(Input(index) + 8));
    }

    public bool TryGetFloat(int index, out float value) => ZoneNativeFlowValues.TryGetFloat(Runtime(), Input(index), out value);

    public int GetInt32(int index)
    {
        RequireType(index, 1);
        return Marshal.ReadInt32(Input(index) + 8);
    }

    public uint GetEntityId(int index)
    {
        RequireType(index, 4);
        return unchecked((uint)Marshal.ReadInt32(Input(index) + 8));
    }

    public Vector3 GetVector3(int index)
    {
        RequireType(index, 5);
        var vector = Marshal.ReadIntPtr(Input(index) + 8);
        if (vector == 0) throw new InvalidOperationException("The native Vec3 input has no owned payload.");
        return *(Vector3*)vector;
    }

    public bool GetBoolean(int index)
    {
        RequireType(index, 7);
        return Marshal.ReadByte(Input(index) + 8) != 0;
    }

    public bool TryGetBoolean(int index, out bool value)
    {
        if (GetNativePortType(index) != 7) { value = false; return false; }
        value = Marshal.ReadByte(Input(index) + 8) != 0;
        return true;
    }

    public string GetString(int index)
    {
        RequireType(index, 6);
        return ReadString(Marshal.ReadIntPtr(Input(index) + 8));
    }

    public bool TryGetString(int index, out string value)
    {
        if (GetNativePortType(index) != 6) { value = string.Empty; return false; }
        value = ReadString(Marshal.ReadIntPtr(Input(index) + 8));
        return true;
    }

    public void ActivateBooleanOutput(byte port, bool value)
    {
        var target = GraphInterface(0);
        byte payload = value ? (byte)1 : (byte)0;
        ((delegate* unmanaged[Stdcall]<nint, uint, byte*, void>)Marshal.ReadIntPtr(Marshal.ReadIntPtr(target)))(target, Address(port), &payload);
        Runtime().ThrowIfFaulted();
    }

    public void ActivateFloatOutput(byte port, float value)
    {
        var target = GraphInterface(0x20);
        ((delegate* unmanaged[Stdcall]<nint, uint, float*, void>)Marshal.ReadIntPtr(Marshal.ReadIntPtr(target)))(target, Address(port), &value);
        Runtime().ThrowIfFaulted();
    }

    public void ActivateEntityIdOutput(byte port, uint value)
    {
        var target = GraphInterface(0x18);
        ((delegate* unmanaged[Stdcall]<nint, uint, uint*, void>)Marshal.ReadIntPtr(Marshal.ReadIntPtr(target)))(target, Address(port), &value);
        Runtime().ThrowIfFaulted();
    }

    public void ActivateInt32Output(byte port, int value)
    {
        var target = GraphInterface(0x30);
        ((delegate* unmanaged[Stdcall]<nint, uint, int*, void>)Marshal.ReadIntPtr(Marshal.ReadIntPtr(target)))(target, Address(port), &value);
        Runtime().ThrowIfFaulted();
    }

    public void ActivateVector3Output(byte port, Vector3 value)
    {
        var target = GraphInterface(0x10);
        ((delegate* unmanaged[Stdcall]<nint, uint, Vector3*, void>)Marshal.ReadIntPtr(Marshal.ReadIntPtr(target)))(target, Address(port), &value);
        Runtime().ThrowIfFaulted();
    }

    public void SetRegularlyUpdated(bool enabled)
    {
        var target = GraphInterface(0);
        var method = Marshal.ReadIntPtr(Marshal.ReadIntPtr(target) + 0x138);
        RequireModuleAddress(Runtime(), method);
        ((delegate* unmanaged[Stdcall]<nint, ushort, byte, void>)method)(target, NodeId, enabled ? (byte)1 : (byte)0);
        Runtime().ThrowIfFaulted();
    }

    public int QueryGraphType(int query)
    {
        var target = GraphInterface(0);
        var method = Marshal.ReadIntPtr(Marshal.ReadIntPtr(target) + 0x50);
        RequireModuleAddress(Runtime(), method);
        var result = ((delegate* unmanaged[Stdcall]<nint, int, int>)method)(target, query);
        Runtime().ThrowIfFaulted();
        return result;
    }

    private uint Address(byte port) => NodeId | ((uint)port << 16) | 0x01000000;

    private nint Input(int index)
    {
        Runtime().VerifyCallback();
        if ((uint)index >= (uint)_inputCount) throw new ArgumentOutOfRangeException(nameof(index));
        if (InputPorts == 0) throw new InvalidOperationException("The native activation has no input values.");
        return InputPorts + index * 0x10;
    }

    private void RequireType(int index, int type)
    {
        if (GetNativePortType(index) != type)
            throw new InvalidOperationException($"Native input {index} requires exact flow-value type {type}.");
    }

    private IZoneNativeFlowNodeRuntime Runtime() => _runtime ??
        throw new InvalidOperationException("An empty activation has no live native callback owner.");

    private nint GraphInterface(int offset)
    {
        var runtime = Runtime();
        runtime.VerifyCallback();
        if (Graph == 0) throw new InvalidOperationException("The activation has no native graph.");
        var target = Graph + offset;
        var table = Marshal.ReadIntPtr(target);
        RequireModuleAddress(runtime, table);
        RequireModuleAddress(runtime, Marshal.ReadIntPtr(table));
        return target;
    }

    private static void RequireModuleAddress(IZoneNativeFlowNodeRuntime runtime, nint address)
    {
        var module = runtime.RequireCryActionModule();
        var nt = Marshal.ReadInt32(module + 0x3c);
        var size = unchecked((uint)Marshal.ReadInt32(module + nt + 0x50));
        if (address == 0 || (nuint)address < (nuint)module || (nuint)address - (nuint)module >= size)
            throw new InvalidOperationException("The borrowed flow graph has an unexpected CryAction interface.");
    }

    private static string ReadString(nint pointer)
    {
        if (pointer == 0) throw new InvalidOperationException("A native CryString input has no storage.");
        var length = Marshal.ReadInt32(pointer - 8);
        if (length < 0) throw new InvalidOperationException("A native CryString input has an invalid length.");
        var bytes = new ReadOnlySpan<byte>((void*)pointer, length);
        var zero = bytes.IndexOf((byte)0);
        if (zero >= 0) bytes = bytes[..zero];
        return Encoding.UTF8.GetString(bytes);
    }
}

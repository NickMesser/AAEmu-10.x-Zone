using System.Numerics;

namespace AAEmu.Zone.Native.FlowNodes;

/// <summary>Every callback has a concrete family implementation; there are no default node behaviors.</summary>
internal interface IZoneNativeFlowNodeBehavior
{
    ZoneNativeFlowNodeConfiguration Configuration { get; }
    void ProcessEvent(int eventId, ZoneNativeFlowActivation activation);
    bool SerializeXml(ZoneNativeFlowActivation activation, nint xmlNodeReference, bool loading);
    void Serialize(ZoneNativeFlowActivation activation, nint serializer);
    void Destroy();
}

/// <summary>
/// Optional, proved family-specific payload/secondary-interface initialization. The main table
/// and reference count remain adapter-owned. This hook must not publish an unretained borrower.
/// </summary>
internal interface IZoneNativeFlowNodeStorageBehavior
{
    void BindNativeStorage(nint node, int byteLength);
}

/// <summary>
/// The engine's already-held callback operation. Read-only construction admission must work
/// before native modules are loaded; native addresses are acquired only inside actual callbacks.
/// Fault inspection never dereferences CSystem and remains valid through final module release.
/// </summary>
internal interface IZoneNativeFlowNodeRuntime
{
    void ValidateForRegistration();
    void VerifyCallback();
    void ThrowIfFaulted();
    void RecordFault(Exception error);
    nint RequireCryActionModule();
}

internal sealed record ZoneNativeFlowInputPort
{
    public string Name { get; }
    public ZoneNativeFlowValue DefaultValue { get; }
    public string? Description { get; }
    public string? HumanName { get; }
    public string? UiConfig { get; }

    public ZoneNativeFlowInputPort(string name, ZoneNativeFlowValue defaultValue,
        string? description = null, string? humanName = null, string? uiConfig = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (name.Contains('\0')) throw new ArgumentException("A port name cannot contain a NUL terminator.", nameof(name));
        Name = name;
        DefaultValue = defaultValue;
        Description = description;
        HumanName = humanName;
        UiConfig = uiConfig;
    }
}

internal sealed record ZoneNativeFlowOutputPort
{
    public string Name { get; }
    public int NativeType { get; }
    public string? Description { get; }
    public string? HumanName { get; }

    public ZoneNativeFlowOutputPort(string name, int nativeType, string? description = null, string? humanName = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (name.Contains('\0')) throw new ArgumentException("A port name cannot contain a NUL terminator.", nameof(name));
        if (nativeType is < -1 or > 7) throw new ArgumentOutOfRangeException(nameof(nativeType));
        Name = name;
        NativeType = nativeType;
        Description = description;
        HumanName = humanName;
    }
}

/// <summary>
/// Managed authored configuration. Native callbacks write only input/output pointers, the
/// exact masked flags update and description; unrelated caller-owned config bytes are preserved.
/// </summary>
internal sealed class ZoneNativeFlowNodeConfiguration
{
    public IReadOnlyList<ZoneNativeFlowInputPort> Inputs { get; }
    public IReadOnlyList<ZoneNativeFlowOutputPort> Outputs { get; }
    public string? Description { get; }
    public uint FlagsAndMask { get; }
    public uint FlagsOrMask { get; }

    public ZoneNativeFlowNodeConfiguration(IReadOnlyList<ZoneNativeFlowInputPort> inputs,
        IReadOnlyList<ZoneNativeFlowOutputPort> outputs, string? description, uint flagsAndMask, uint flagsOrMask)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(outputs);
        if (inputs.Count > 256 || outputs.Count > 256)
            throw new ArgumentException("Native flow port addresses use one-byte port indices.");
        var inputRows = inputs.ToArray();
        var outputRows = outputs.ToArray();
        if (inputRows.Any(row => row is null) || outputRows.Any(row => row is null))
            throw new ArgumentException("Port definitions cannot contain null rows.");
        Inputs = Array.AsReadOnly(inputRows);
        Outputs = Array.AsReadOnly(outputRows);
        Description = description;
        FlagsAndMask = flagsAndMask;
        FlagsOrMask = flagsOrMask;
    }
}

/// <summary>
/// A managed default, never a fabricated native variant descriptor. Native type helpers and
/// their real copy/destruction operations are bound lazily from the verified CryAction image.
/// </summary>
internal readonly record struct ZoneNativeFlowValue
{
    internal int NativeType { get; }
    internal object? Data { get; }
    private ZoneNativeFlowValue(int nativeType, object? data) { NativeType = nativeType; Data = data; }
    public static ZoneNativeFlowValue Void() => new(0, null);
    public static ZoneNativeFlowValue Int32(int value) => new(1, value);
    public static ZoneNativeFlowValue Int64(long value) => new(2, value);
    public static ZoneNativeFlowValue Float(float value) => new(3, value);
    public static ZoneNativeFlowValue EntityId(uint value) => new(4, value);
    public static ZoneNativeFlowValue Vector3(Vector3 value) => new(5, value);
    public static ZoneNativeFlowValue String(string value) => new(6, value ?? throw new ArgumentNullException(nameof(value)));
    public static ZoneNativeFlowValue Boolean(bool value) => new(7, value);
}

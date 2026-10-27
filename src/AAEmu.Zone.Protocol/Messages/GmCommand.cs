using System.Text;
using AAEmu.Zone.Protocol.Units;

namespace AAEmu.Zone.Protocol.Messages;

/// <summary>WZGmCommand, native body serializer 3936C5F0.</summary>
public sealed class GmCommand
{
    public const byte SetAttribute = 2;
    public const byte ClearAttribute = 3;
    public const byte ChangeMode = 30;
    public const int MaximumParameterBytes = 1023;
    // Native name table 3A5A2B30, lookup 39BFB140.
    private static readonly string[] ModeNames =
        ["invincible", "cooldown", "combatlog", "almighty", "debug_plot", "test_siege", "live", "zone_permission", "test_integration"];
    private readonly byte[] _parameters;
    public uint ObjectId { get; }
    public byte Command { get; }
    public ReadOnlySpan<byte> Parameters => _parameters;

    public GmCommand(uint objectId, byte command, ReadOnlySpan<byte> parameters)
    {
        if (parameters.Length > MaximumParameterBytes)
            throw new InvalidDataException("GM parameters exceed the native 1023-byte limit.");
        ObjectId = objectId;
        Command = command;
        _parameters = parameters.ToArray();
    }

    public static GmCommand FromText(uint objectId, byte command, string parameters) =>
        new(objectId, command, Encoding.UTF8.GetBytes(parameters));

    public static string ModeParameters(int mode, sbyte value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(mode);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(mode, ModeNames.Length);
        return FormattableString.Invariant($"{ModeNames[mode]},{value}");
    }

    public bool TryReadMode(out int mode, out sbyte value)
    {
        mode = -1;
        value = 0;
        if (Command != ChangeMode || !GmCommandData.TryRead(Parameters, out var data) || data!.Children.Count < 2) return false;
        var name = data.Children[0].Value;
        for (var index = 0; index < ModeNames.Length; index++)
        {
            var candidate = ModeNames[index];
            if (candidate.Length != name.Length) continue;
            var matches = true;
            for (var offset = 0; offset < name.Length; offset++)
            {
                var c = name[offset];
                if (c is >= (byte)'A' and <= (byte)'Z') c += 'a' - 'A';
                if (c != candidate[offset]) { matches = false; break; }
            }
            if (!matches) continue;
            mode = index;
            value = unchecked((sbyte)data.Children[1].Int32Value);
            return true;
        }
        return false;
    }

    public ZoneFrame ToFrame() => new(WzOpcodes.GmCommand, Write());

    public bool TryReadAttributeChange(out uint attribute, out long? value)
    {
        attribute = 0;
        value = null;
        if (Command is not (SetAttribute or ClearAttribute) || !GmCommandData.TryRead(Parameters, out var data) ||
            data!.Children.Count < (Command == SetAttribute ? 2 : 1) ||
            !NativeAttributeNames.TryGetId(data.Children[0].Value, out attribute)) return false;
        if (Command == SetAttribute) value = data.Children[1].Int64Value;
        return true;
    }

    public static GmCommand Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body);
        var objectId = reader.ReadBc();
        var command = reader.ReadByte();
        var parameters = reader.ReadBytes(reader.ReadUInt16());
        var message = new GmCommand(objectId, command, parameters);
        reader.EnsureFullyConsumed();
        return message;
    }

    public byte[] Write()
    {
        var writer = new ZoneWriter();
        writer.WriteBc(ObjectId);
        writer.WriteByte(Command);
        writer.WriteUInt16((ushort)_parameters.Length);
        writer.WriteBytes(_parameters);
        return writer.ToArray();
    }
}

namespace AAEmu.Zone.Protocol.Messages;

public enum GameScheduleOperation : ushort
{
    Start = WzOpcodes.GameScheduleStart,
    Continue = WzOpcodes.GameScheduleContinue,
    End = WzOpcodes.GameScheduleEnd
}

/// <summary>Native39368040/70/A0: each body is one unsigned schedule ID.</summary>
public sealed record GameScheduleMessage(GameScheduleOperation Operation, uint ScheduleId)
{
    public static GameScheduleMessage Read(ushort opcode, ReadOnlySpan<byte> body)
    {
        var operation = (GameScheduleOperation)opcode;
        Validate(operation);
        var reader = new ZoneReader(body);
        var value = new GameScheduleMessage(operation, reader.ReadUInt32());
        reader.EnsureFullyConsumed();
        return value;
    }

    public ZoneFrame ToFrame()
    {
        Validate(Operation);
        var writer = new ZoneWriter(); writer.WriteUInt32(ScheduleId);
        return new((ushort)Operation, writer.ToArray());
    }

    private static void Validate(GameScheduleOperation operation)
    {
        if (operation is not (GameScheduleOperation.Start or GameScheduleOperation.Continue or GameScheduleOperation.End))
            throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown game-schedule operation.");
    }
}

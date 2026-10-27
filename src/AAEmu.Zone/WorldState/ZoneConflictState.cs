using AAEmu.Zone.Protocol.Messages;

namespace AAEmu.Zone.WorldState;

/// <summary>3932C780: World conflict updates must match the current scene's nonzero group.</summary>
public sealed class ZoneConflictState(ushort sceneGroupId)
{
    public ushort GroupId { get; private set; }
    // Native initialized data 3A517B70 is { group=0, state=7 }; joining does not copy ConflictType into it.
    public byte State { get; private set; } = 7;

    public bool Apply(ConflictZoneState message)
    {
        if (sceneGroupId == 0 || message.ZoneGroupId != sceneGroupId) return false;
        GroupId = message.ZoneGroupId;
        State = message.State; // All byte values are accepted; the native handler widens to u32.
        return true;
    }

    public bool Apply(ReadOnlySpan<byte> body) => Apply(ConflictZoneState.Read(body));
}

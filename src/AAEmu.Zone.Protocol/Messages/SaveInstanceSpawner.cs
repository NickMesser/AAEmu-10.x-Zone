namespace AAEmu.Zone.Protocol.Messages;

/// <summary>
/// ZWSaveInstanceSpawner, initializer39362700 and body39368B10. The sender39347F90 supplies
/// the native NPC identity (Npc+18), which is distinct from the unit's object identity.
/// </summary>
public readonly record struct SaveInstanceSpawner(uint SpawnerId, uint NpcId, bool TemplateChanged)
{
    public static SaveInstanceSpawner Read(ReadOnlySpan<byte> body)
    {
        var reader = new ZoneReader(body);
        var result = new SaveInstanceSpawner(reader.ReadUInt32(), reader.ReadBc(), reader.ReadBoolean());
        reader.EnsureFullyConsumed();
        return result;
    }

    public ZoneFrame ToFrame()
    {
        var writer = new ZoneWriter();
        writer.WriteUInt32(SpawnerId);
        writer.WriteBc(NpcId);
        writer.WriteBoolean(TemplateChanged);
        return new(ZwOpcodes.SaveInstanceSpawner, writer.ToArray());
    }
}

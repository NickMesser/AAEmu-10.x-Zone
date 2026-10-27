namespace AAEmu.Zone.Protocol.Units;

public sealed record UnitExpertise(uint Experience, sbyte Order);
public sealed record CharacterVisualOptions(ReadOnlyMemory<byte> Stp, byte Flags, byte CosplayVisual);
public sealed record CharacterStatPage(IReadOnlyList<int> Stats, uint NormalCount, uint SpecialCount);
public sealed record EquipmentReinforcement(int Slot, byte Level, int Experience);
/// <param name="EffectId">Selected row ID in equip_slot_reinforce_unit_modifiers, not its parent level-effect ID.</param>
public sealed record EquipmentLevelEffect(byte Slot, sbyte Level, uint EffectId);

public sealed record UnitCharacterData(IReadOnlyList<UnitExpertise> Expertise, IReadOnlyList<byte> ActiveExpertise,
    uint DuelObjectId, sbyte DuelTeam, byte ExpeditionWarSide, CharacterVisualOptions VisualOptions, int PremiumGrade,
    IReadOnlyList<CharacterStatPage> StatPages, uint SelectedPage, uint ExtendMaxStats, uint ApplyExtendCount,
    IReadOnlyList<EquipmentReinforcement> Reinforcements, IReadOnlyList<EquipmentLevelEffect> LevelEffects)
{
    internal static UnitCharacterData Read(ref ZoneReader reader)
    {
        // FUN_39CB8150: 29 entries, independent byte count of active expertise IDs.
        var expertise = new UnitExpertise[29];
        for (var i = 0; i < expertise.Length; i++)
            expertise[i] = new(reader.ReadUInt32(), reader.ReadSByte());
        var active = reader.ReadBytes(reader.ReadByte()).ToArray();
        if (active.Any(id => id is < 1 or > 29))
            throw new InvalidDataException("Active expertise ID is outside the native 1..29 table.");
        // 39D1C900 maps member 0x9A6 to ServerUnit+0x7FE8, also written by WZUnitDuelState.
        var duelObject = reader.ReadBc();
        var duelTeam = reader.ReadSByte();
        // Packet member 0x9A9 -> ServerUnit+0x7FF4, also set by 392587E0 for expedition war.
        var warSide = reader.ReadByte();
        // FUN_3937E700: six STP bytes, one packed flag byte, one cosplay visual byte.
        var visual = new CharacterVisualOptions(reader.ReadBytes(6).ToArray(), reader.ReadByte(), reader.ReadByte());
        var premium = reader.ReadInt32();
        // FUN_39390F20: i32 vector length, five signed stat values + two unsigned counters per page.
        var pages = new CharacterStatPage[reader.ReadCount(28)];
        for (var i = 0; i < pages.Length; i++)
        {
            var stats = new int[5];
            for (var j = 0; j < stats.Length; j++)
                stats[j] = reader.ReadInt32();
            pages[i] = new(Array.AsReadOnly(stats), reader.ReadUInt32(), reader.ReadUInt32());
        }
        var selected = reader.ReadUInt32();
        var extendMax = reader.ReadUInt32();
        var extendCount = reader.ReadUInt32();
        // FUN_39390BA0 / FUN_393916B0 use map insertion: first duplicate key wins.
        var reinforcementCount = reader.ReadCount(9);
        var reinforcements = new Dictionary<int, EquipmentReinforcement>();
        for (var i = 0; i < reinforcementCount; i++)
        {
            var entry = new EquipmentReinforcement(reader.ReadInt32(), reader.ReadByte(), reader.ReadInt32());
            reinforcements.TryAdd(entry.Slot, entry);
        }
        var levelCount = reader.ReadCount(6);
        var levels = new Dictionary<(byte, sbyte), EquipmentLevelEffect>();
        for (var i = 0; i < levelCount; i++)
        {
            var entry = new EquipmentLevelEffect(reader.ReadByte(), reader.ReadSByte(), reader.ReadUInt32());
            levels.TryAdd((entry.Slot, entry.Level), entry);
        }
        return new(Array.AsReadOnly(expertise), Array.AsReadOnly(active), duelObject, duelTeam, warSide, visual, premium,
            Array.AsReadOnly(pages), selected, extendMax, extendCount,
            reinforcements.Values.OrderBy(entry => entry.Slot).ToList().AsReadOnly(),
            levels.Values.OrderBy(entry => entry.Slot).ThenBy(entry => entry.Level).ToList().AsReadOnly());
    }
}

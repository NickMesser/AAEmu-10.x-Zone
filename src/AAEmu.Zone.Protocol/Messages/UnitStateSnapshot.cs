using AAEmu.Zone.Protocol.Units;

namespace AAEmu.Zone.Protocol.Messages;

public sealed record UnitPackedState(uint PlotDebugLevel, uint Reserved7Fb0, uint Reserved7Fb4, uint Appellation,
    uint FactionId, uint ExpeditionId, uint FamilyId, uint EquipmentPublic, uint Offline, uint ExpeditionWarFactionId, uint PvpHonor);

/// <summary>
/// Complete native WZUnitState body. FUN_393932E0 serializes the shared body; FUN_39394CC0
/// appends buffs and action. Read completes and validates the entire envelope before returning.
/// </summary>
public sealed class UnitStateSnapshot
{
    public required uint ObjectId { get; init; }
    public required string Name { get; init; }
    public required byte WorldId { get; init; }
    public required byte RegionId { get; init; }
    public required bool GlobalWorld { get; init; }
    public required UnitIdentity Identity { get; init; }
    public required string MasterName { get; init; }
    public required UnitPosition Position { get; init; }
    public required float Scale { get; init; }
    public required sbyte Level { get; init; }
    public required sbyte HeirLevel { get; init; }
    public required sbyte SecondaryLevel { get; init; }
    public required sbyte SecondaryHeirLevel { get; init; }
    public required ReadOnlyMemory<byte> SlotSelectors { get; init; }
    public required uint ModelId { get; init; }
    public required UnitEquipment Equipment { get; init; }
    public required UnitAppearance Appearance { get; init; }
    public required uint TargetId { get; init; }
    public required long PreciseHealth { get; init; }
    public required long PreciseMana { get; init; }
    public required UnitAttachment Attachment { get; init; }
    public required UnitBonding Bonding { get; init; }
    public required UnitPosture Posture { get; init; }
    public required sbyte ActiveWeapon { get; init; }
    public required uint StateType { get; init; }
    // Fixed packet member 0x994 -> native state+0x7FBC. PackedState.Appellation is
    // the separate active title (member 0x995), not the selected merit/stamp.
    public required int AppellationStamp { get; init; }
    public required uint VehicleDyeing { get; init; }
    public required bool TemporaryFaction { get; init; }
    public required IReadOnlyList<uint> Skills { get; init; }
    public required IReadOnlyList<uint> PassiveBuffs { get; init; }
    public required UnitHeading Heading { get; init; }
    public required byte RaceGender { get; init; }
    public required UnitPackedState PackedState { get; init; }
    public required ushort Flags { get; init; }
    public required byte AttackFactionFlags { get; init; }
    public required UnitFirstHitter? FirstHitter { get; init; }
    public required IReadOnlyList<ushort> HighAbilities { get; init; }
    public required IReadOnlyList<sbyte> GmModes { get; init; }
    public required UnitCharacterData? Character { get; init; }
    public required UnitBuffs Buffs { get; init; }
    public required UnitAction Action { get; init; }

    public static UnitStateSnapshot Read(ReadOnlySpan<byte> body)
    {
        if (body.Length > ZoneFrame.MaxBodySize)
            throw new InvalidDataException("Unit snapshot exceeds the native Zone frame limit.");
        var reader = new ZoneReader(body);
        var snapshot = Read(ref reader, includeAction: true);
        reader.EnsureFullyConsumed();
        return snapshot;
    }

    // WZNpcState shares the complete unit body and buffs, but has no action tail.
    internal static UnitStateSnapshot Read(ref ZoneReader reader, bool includeAction)
    {
        var objectId = reader.ReadBc();
        var name = reader.ReadString(1024);
        var worldId = reader.ReadByte();
        var regionId = reader.ReadByte();
        var global = reader.ReadBoolean();
        var identity = UnitIdentity.Read(ref reader);
        var masterName = reader.ReadString(128);
        var position = UnitPosition.Read(ref reader);
        var scale = reader.ReadSingle();
        var level = reader.ReadSByte();
        var heirLevel = reader.ReadSByte();
        var secondaryLevel = reader.ReadSByte();
        var secondaryHeir = reader.ReadSByte();
        var slots = reader.ReadBytes(4).ToArray();
        var modelId = reader.ReadUInt32();
        var equipment = UnitEquipment.Read(ref reader, identity.Kind);
        var appearance = UnitAppearance.Read(ref reader);
        var target = reader.ReadBc();
        var health = reader.ReadInt64();
        var mana = reader.ReadInt64();
        var attachment = UnitAttachment.Read(ref reader);
        var bonding = UnitBonding.Read(ref reader);
        var posture = UnitPosture.Read(ref reader);
        var weapon = reader.ReadSByte();
        var skillCount = reader.ReadByte();
        var passiveCount = reader.ReadByte();
        var stateType = reader.ReadUInt32();
        var appellationStamp = reader.ReadInt32();
        var vehicleDyeing = reader.ReadUInt32();
        var temporaryFaction = reader.ReadBoolean();
        var skills = Array.AsReadOnly(reader.ReadPisc(skillCount));
        var passives = Array.AsReadOnly(reader.ReadPisc(passiveCount));
        var heading = identity.Kind == ZoneUnitKind.Housing
            ? new UnitHeading(HouseYaw: reader.ReadSingle())
            : new UnitHeading(reader.ReadSByte(), reader.ReadSByte(), reader.ReadSByte());
        var raceGender = reader.ReadByte();
        var first = reader.ReadPisc(4);
        var second = reader.ReadPisc(3);
        var third = reader.ReadPisc(4);
        var packed = new UnitPackedState(first[0], first[1], first[2], first[3], second[0], second[1], second[2],
            third[0], third[1], third[2], third[3]);
        var flags = reader.ReadUInt16();
        var attackFlags = reader.ReadByte();
        var firstHitter = (flags & (1 << 8)) != 0 ? new UnitFirstHitter(reader.ReadBc(), reader.ReadUInt32()) : null;
        var highAbilities = new ushort[(flags & (1 << 7)) != 0 ? 4 : 0];
        for (var i = 0; i < highAbilities.Length; i++)
            highAbilities[i] = reader.ReadUInt16();
        var gmModes = new sbyte[(flags & (1 << 6)) != 0 ? 9 : 0];
        for (var i = 0; i < gmModes.Length; i++)
            gmModes[i] = reader.ReadSByte();
        var character = identity.Kind == ZoneUnitKind.Character ? UnitCharacterData.Read(ref reader) : null;
        var buffs = UnitBuffs.Read(ref reader);
        var action = includeAction ? UnitAction.Read(ref reader) : new UnitAction(0);
        return new()
        {
            ObjectId = objectId, Name = name, WorldId = worldId, RegionId = regionId, GlobalWorld = global,
            Identity = identity, MasterName = masterName, Position = position, Scale = scale, Level = level,
            HeirLevel = heirLevel, SecondaryLevel = secondaryLevel, SecondaryHeirLevel = secondaryHeir,
            SlotSelectors = slots, ModelId = modelId, Equipment = equipment, Appearance = appearance,
            TargetId = target, PreciseHealth = health, PreciseMana = mana, Attachment = attachment, Bonding = bonding,
            Posture = posture, ActiveWeapon = weapon, StateType = stateType, AppellationStamp = appellationStamp,
            VehicleDyeing = vehicleDyeing, TemporaryFaction = temporaryFaction, Skills = skills, PassiveBuffs = passives,
            Heading = heading, RaceGender = raceGender, PackedState = packed, Flags = flags, AttackFactionFlags = attackFlags,
            FirstHitter = firstHitter, HighAbilities = Array.AsReadOnly(highAbilities), GmModes = Array.AsReadOnly(gmModes),
            Character = character, Buffs = buffs, Action = action
        };
    }
}

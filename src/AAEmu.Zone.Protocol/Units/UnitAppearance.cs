namespace AAEmu.Zone.Protocol.Units;

public sealed record UnitHair(byte Race, byte Gender, ulong VisualRaceExpiredTime, byte VisualRace, byte VisualGender,
    uint HairColor, uint HornColor, uint DefaultHairColor, uint TwoToneHair, float TwoToneFirstWidth, float TwoToneSecondWidth);
public sealed record UnitBody(uint SkinColor, uint DiffuseMap, uint NormalMap, float Weight);
public sealed record MovableDecal(uint Id, float Weight, float Scale, float Rotate, short MoveX, short MoveY);
public sealed record UnitFace(MovableDecal MovableDecal, IReadOnlyList<uint> FixedDecalIds, IReadOnlyList<uint> MapIds,
    IReadOnlyList<float> FixedDecalWeights, float NormalMapWeight, uint LipColor, uint LeftPupilColor,
    uint RightPupilColor, uint EyebrowColor, uint MakeupColor, ReadOnlyMemory<byte> Modifiers);

public sealed record UnitAppearance(byte Extension, UnitHair? Hair, UnitBody? Body, UnitFace? Face)
{
    internal static UnitAppearance Read(ref ZoneReader reader)
    {
        // FUN_393881E0: 0=none, 1=hair, 2=hair/body; all other values include the face.
        var extension = reader.ReadByte();
        if (extension == 0)
            return new(extension, null, null, null);
        var hair = new UnitHair(reader.ReadByte(), reader.ReadByte(), reader.ReadUInt64(), reader.ReadByte(),
            reader.ReadByte(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(),
            reader.ReadSingle(), reader.ReadSingle());
        if (extension == 1)
            return new(extension, hair, null, null);
        var body = new UnitBody(reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadSingle());
        if (extension == 2)
            return new(extension, hair, body, null);
        var movable = new MovableDecal(reader.ReadUInt32(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
            reader.ReadInt16(), reader.ReadInt16());
        var decals = Array.AsReadOnly(reader.ReadPisc(6));
        var maps = Array.AsReadOnly(reader.ReadPisc(3));
        var weights = new float[6];
        for (var i = 0; i < weights.Length; i++)
            weights[i] = reader.ReadSingle();
        var normalWeight = reader.ReadSingle();
        var lip = reader.ReadUInt32();
        var leftPupil = reader.ReadUInt32();
        var rightPupil = reader.ReadUInt32();
        var eyebrow = reader.ReadUInt32();
        var makeup = reader.ReadUInt32();
        var size = reader.ReadUInt16();
        if (size > 128)
            throw new InvalidDataException("Face modifier blob exceeds the native 128-byte limit.");
        var face = new UnitFace(movable, decals, maps, Array.AsReadOnly(weights), normalWeight, lip,
            leftPupil, rightPupil, eyebrow, makeup, reader.ReadBytes(size).ToArray());
        return new(extension, hair, body, face);
    }
}

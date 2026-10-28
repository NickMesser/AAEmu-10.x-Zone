using System.Buffers.Binary;
using System.Text;

namespace AAEmu.Zone.Content;

/// <summary>
/// The initial, unanimated character's CActor392D8F00 swim threshold. This is a scalar
/// selected from the authored default bone-to-world pose, not a stance/collider size.
/// An animated skeleton or the no-selected-joint AABB fallback needs its own producer.
/// </summary>
public sealed record ZoneCharacterInitialSwimThreshold(string SkeletonAssetName, int BoneIndex,
    string BoneName, float Height)
{
    //392D8F00's table39FD23A8 is46,45,5,4,3,2. CryAnimation315B049D..315B16B1
    //constructs the name-CRC -> semantic-ID map;31590E60's source-order pass stores
    //the last matching joint. These names are not guessed from an actor model name.
    private static readonly string[] Names =
        ["Bone_Swim", "camera", "Bip01 Spine3", "Bip01 Spine2", "Bip01 Spine1", "Bip01 Spine"];

    public static ZoneCharacterInitialSwimThreshold Load(IZoneAssetSource assets, ZoneCharacterDefinitionContent definition)
    {
        ArgumentNullException.ThrowIfNull(assets); ArgumentNullException.ThrowIfNull(definition);
        using var input = assets.OpenRead(definition.SkeletonAssetName) ??
            throw new FileNotFoundException("The admitted character's default skeleton is absent.", definition.SkeletonAssetName);
        var bytes = ZoneCharacterSkeletonContent.ReadBounded(input, ZoneCharacterSkeletonContent.MaximumFileBytes);
        using var verified = new MemoryStream(bytes, false);
        var skeleton = ZoneCharacterSkeletonContent.Read(verified);
        if (!skeleton.Bones.SequenceEqual(definition.Skeleton.Bones))
            throw new InvalidDataException("The character skeleton changed after its slot inventory was admitted.");

        ZoneCharacterBone? selected = null;
        foreach (var name in Names)
        {
            var key = NameCrc(name);
            var candidates = skeleton.Bones.Where(bone => NameCrc(bone.Name) == key).ToArray();
            if (candidates.Length == 0) continue;
            selected = candidates[^1];
            break;
        }
        if (selected is null)
            throw new NotSupportedException("CActor swim-threshold selection has no authored semantic joint; its animated-character AABB fallback is not owned.");

        var version = U32(bytes, 12);
        var table = checked((int)U32(bytes, 16));
        var count = checked((int)U32(bytes, table));
        var stride = version == 0x745 ? 20 : 16;
        var chunk = -1;
        for (var i = 0; i < count; i++)
        {
            var at = table + 4 + i * stride;
            if (U32(bytes, at) == 0xacdc0000) chunk = checked((int)U32(bytes, at + 8));
        }
        //CompiledBones0801 stores its default B2W Matrix34 at record+276. Translation
        //is the fourth column;392D8F00 takes the absolute QuatT translation.Z, not
        //the local parent offset or the entity's scale-bearing world matrix.
        var matrix = checked(chunk + 48 + selected.Index * 324 + 276);
        for (var i = 0; i < 12; i++)
            if (!float.IsFinite(F32(bytes, matrix + i * 4)))
                throw new InvalidDataException("The authored default bone pose contains a nonfinite matrix component.");
        var height = F32(bytes, matrix + 44);
        if (height < 0)
            throw new NotSupportedException("The dry upright NPC profile requires a nonnegative authored swim threshold.");
        return new(definition.SkeletonAssetName, selected.Index, selected.Name, height);
    }

    private static uint U32(byte[] bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at, 4));
    private static float F32(byte[] bytes, int at) => BitConverter.Int32BitsToSingle(
        BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at, 4)));
    private static uint NameCrc(string name)
    {
        var crc = uint.MaxValue;
        foreach (var value in Encoding.UTF8.GetBytes(ZoneCharacterSkeletonContent.LowerAscii(
            ZoneCharacterSkeletonContent.CString(name))))
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
        }
        return ~crc;
    }
}

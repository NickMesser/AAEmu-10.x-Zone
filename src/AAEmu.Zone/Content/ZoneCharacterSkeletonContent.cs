using System.Buffers.Binary;
using System.Collections.Frozen;
using System.Text;

namespace AAEmu.Zone.Content;

/// <summary>One authored compiled-bone identity. This is not a running skeleton or an animation pose.</summary>
public sealed record ZoneCharacterBone(int Index, uint ControllerId, string Name);

/// <summary>
/// Bounded CHR CryTek0744/0745 container and CompiledBones0801 identity reader. The original
/// loader uses a48-byte header and324-byte records, with the48-byte bone name at record+216.
/// Skinning, physics bones, bind-pose conversion and animation evaluation remain separate.
/// </summary>
public sealed class ZoneCharacterSkeletonContent
{
    public const int MaximumFileBytes = 128 * 1024 * 1024;
    public const int MaximumChunkCount = 262144;
    public const int MaximumBoneCount = 65535;
    private readonly FrozenDictionary<uint, int> _boneIds;
    private readonly FrozenSet<uint> _ambiguousBoneIds;
    public uint ContainerVersion { get; }
    public uint CompiledBonesVersion => 0x801;
    public IReadOnlyList<ZoneCharacterBone> Bones { get; }
    /// <summary>Retained duplicate identities. Their native winner is not guessed by the metadata reader.</summary>
    public IReadOnlyList<uint> AmbiguousBoneNameCrcs { get; }

    private ZoneCharacterSkeletonContent(uint version, List<ZoneCharacterBone> bones)
    {
        ContainerVersion = version;
        Bones = bones.AsReadOnly();
        var ids = new Dictionary<uint, int>();
        var ambiguous = new HashSet<uint>();
        foreach (var bone in bones)
            if (!ids.TryAdd(NameCrc(bone.Name), bone.Index))
                ambiguous.Add(NameCrc(bone.Name));
        _boneIds = ids.ToFrozenDictionary();
        _ambiguousBoneIds = ambiguous.ToFrozenSet();
        AmbiguousBoneNameCrcs = Array.AsReadOnly(ambiguous.Order().ToArray());
    }

    /// <summary>CModelSkeleton31590D90 resolves the lowercase CRC32, rather than a display-name comparison.</summary>
    public int? FindBone(string name)
    {
        var crc = NameCrc(name);
        if (_ambiguousBoneIds.Contains(crc))
            throw new NotSupportedException($"Compiled bone lookup '{name}' has an unresolved duplicate lowercase CRC32 identity.");
        return _boneIds.TryGetValue(crc, out var index) ? index : null;
    }

    public static ZoneCharacterSkeletonContent Read(Stream input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var bytes = ReadBounded(input, MaximumFileBytes);
        if (bytes.Length < 24 || !bytes.AsSpan(0, 8).SequenceEqual("CryTek\0\0"u8) || U32(bytes, 8) != 0xffff0000)
            throw new InvalidDataException("Character skeleton requires the verified CryTek chunk header.");
        var version = U32(bytes, 12);
        if (version is not (0x744 or 0x745))
            throw new NotSupportedException($"Character container version0x{version:X} is not implemented.");
        var table = (long)U32(bytes, 16);
        if (table < 20 || table > bytes.Length - 4)
            throw new InvalidDataException("Character chunk table exceeds the file bounds.");
        var count = (long)U32(bytes, (int)table);
        var stride = version == 0x745 ? 20 : 16;
        var tableEnd = table + 4 + count * stride;
        if (count > MaximumChunkCount || tableEnd > bytes.Length)
            throw new InvalidDataException("Character chunk table is truncated or exceeds the managed bound.");
        var entries = new List<(uint Type, uint Version, int Offset, int Id, int Length)>((int)count);
        var identities = new HashSet<int>();
        for (var i = 0; i < count; i++)
        {
            var at = (int)(table + 4 + i * stride);
            var offset = (long)U32(bytes, at + 8);
            var id = I32(bytes, at + 12);
            var length = version == 0x745 ? (long)U32(bytes, at + 16) : 0;
            if (!identities.Add(id) || offset < 20 || offset > bytes.Length || length > bytes.Length - offset ||
                offset >= table && offset < tableEnd)
                throw new InvalidDataException("Character chunks have duplicate identities or invalid bounds.");
            entries.Add((U32(bytes, at), U32(bytes, at + 4), (int)offset, id, (int)length));
        }
        var ordered = entries.OrderBy(entry => entry.Offset).ToArray();
        for (var i = 0; i < ordered.Length; i++)
        {
            var entry = ordered[i];
            var end = i + 1 < ordered.Length ? ordered[i + 1].Offset : bytes.Length;
            if (entry.Offset < table) end = Math.Min(end, (int)table);
            var length = version == 0x744 ? end - entry.Offset : entry.Length;
            if (length <= 0 || (long)entry.Offset + length > end)
                throw new InvalidDataException("Character chunks overlap another chunk or their table.");
            ordered[i] = entry with { Length = length };
        }
        var chunks = ordered.Where(entry => entry.Type == 0xacdc0000).ToArray();
        if (chunks.Length != 1)
            throw new InvalidDataException("Character requires exactly one compiled-bones chunk.");
        var chunk = chunks[0];
        if (chunk.Version != 0x801)
            throw new NotSupportedException($"Compiled-bones version0x{chunk.Version:X} is not implemented.");
        if (chunk.Length < 48 || (chunk.Length - 48) % 324 != 0 ||
            U32(bytes, chunk.Offset) != chunk.Type || U32(bytes, chunk.Offset + 4) != chunk.Version ||
            U32(bytes, chunk.Offset + 8) != chunk.Offset || I32(bytes, chunk.Offset + 12) != chunk.Id)
            throw new InvalidDataException("Compiled-bones0801 local header or record length is inconsistent.");
        var boneCount = (chunk.Length - 48) / 324;
        if (boneCount is 0 or > MaximumBoneCount)
            throw new InvalidDataException("Character bone count is empty or exceeds the managed bound.");
        var bones = new List<ZoneCharacterBone>(boneCount);
        for (var i = 0; i < boneCount; i++)
        {
            var at = chunk.Offset + 48 + i * 324;
            var nameBytes = bytes.AsSpan(at + 216, 48);
            var nul = nameBytes.IndexOf((byte)0);
            if (nul <= 0) throw new InvalidDataException("Compiled bone name is empty or lacks its bounded terminator.");
            var name = new UTF8Encoding(false, true).GetString(nameBytes[..nul]);
            bones.Add(new(i, U32(bytes, at + 208), name));
        }
        return new(version, bones);
    }

    internal static byte[] ReadBounded(Stream input, int maximumBytes)
    {
        using var output = new MemoryStream();
        var buffer = new byte[32768];
        int count;
        while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
        {
            if (output.Length + count > maximumBytes)
                throw new InvalidDataException("Character resource exceeds the managed file-size bound.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    internal static string CString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var at = value.IndexOf('\0');
        return at < 0 ? value : value[..at];
    }

    internal static string LowerAscii(string value) => string.Create(value.Length, value, static (output, source) =>
    {
        for (var i = 0; i < source.Length; i++)
            output[i] = source[i] is >= 'A' and <= 'Z' ? (char)(source[i] + 32) : source[i];
    });

    private static uint NameCrc(string name)
    {
        var crc = uint.MaxValue;
        foreach (var value in Encoding.UTF8.GetBytes(LowerAscii(CString(name))))
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
        }
        return ~crc;
    }

    private static uint U32(byte[] bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at, 4));
    private static int I32(byte[] bytes, int at) => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at, 4));
}

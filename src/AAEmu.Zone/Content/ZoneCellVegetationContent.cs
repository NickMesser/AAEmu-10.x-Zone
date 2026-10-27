using System.Buffers.Binary;
using System.Numerics;

namespace AAEmu.Zone.Content;

/// <summary>
/// One untagged 64-byte vegetation.dat row consumed by 302253C0 through 302221D0.
/// Position and bounds are sector-local. Native creation adds
/// ((engineCellX * 4 + LocalX) &lt;&lt; 8, (engineCellY * 4 + LocalY) &lt;&lt; 8, 0).
/// RawPayload retains every authored bit; scale clamping, quality gates, group binding,
/// cell metadata and physicalization belong to the native instance owner.
/// </summary>
public sealed class ZoneCellVegetationInstance
{
    private readonly byte[] _file;
    public int SourceOrder { get; }
    public int SourceOffset { get; }
    public int SectorIndex { get; }
    public int RowIndex { get; }
    public int SerializedLength => ZoneCellVegetationContent.InstanceBytes;
    public ReadOnlySpan<byte> RawPayload => _file.AsSpan(SourceOffset, SerializedLength);
    public Vector3 BoundsMin => ZoneCellObjectValues.Vector(RawPayload, 0);
    public Vector3 BoundsMax => ZoneCellObjectValues.Vector(RawPayload, 12);
    public uint Word18 => ZoneCellObjectValues.UInt(RawPayload, 0x18);
    public uint Flags1C => ZoneCellObjectValues.UInt(RawPayload, 0x1c);
    public uint Flags20 => ZoneCellObjectValues.UInt(RawPayload, 0x20);
    public byte Byte24 => RawPayload[0x24];
    public byte Byte25 => RawPayload[0x25];
    public byte Byte26 => RawPayload[0x26];
    public Vector3 Position => ZoneCellObjectValues.Vector(RawPayload, 0x27);
    public float AuthoredScale => ZoneCellObjectValues.Float(RawPayload, 0x33);
    /// <summary>
    /// Signed index into the separate CObjManager world vegetation group vector (stride 0x510).
    /// The file declares no groups, models or materials. Negative and unresolved indices remain
    /// intact here; native 302221D0 rejects them against the current world catalog.
    /// </summary>
    public short VegetationGroupIndex => BinaryPrimitives.ReadInt16LittleEndian(RawPayload[0x37..]);
    public byte Byte39 => RawPayload[0x39];
    public byte Byte3A => RawPayload[0x3a];
    public byte Byte3B => RawPayload[0x3b];
    public byte Byte3C => RawPayload[0x3c];
    public byte Byte3D => RawPayload[0x3d];
    public byte Byte3E => RawPayload[0x3e];
    public byte Byte3F => RawPayload[0x3f];

    internal ZoneCellVegetationInstance(byte[] file, int order, int offset, int sector, int row)
    { _file = file; SourceOrder = order; SourceOffset = offset; SectorIndex = sector; RowIndex = row; }
}

/// <summary>One of the sixteen signed offset/size descriptors, ordered X outer and Y inner.</summary>
public sealed class ZoneCellVegetationSector
{
    private readonly byte[] _file;
    public int SourceIndex { get; }
    public int LocalX => SourceIndex / 4;
    public int LocalY => SourceIndex % 4;
    /// <summary>Authored absolute file offset. Native 3015E930 does not seek when ByteLength is zero.</summary>
    public int SourceOffset { get; }
    public int ByteLength { get; }
    public ReadOnlySpan<byte> RawPayload => ByteLength == 0 ? ReadOnlySpan<byte>.Empty : _file.AsSpan(SourceOffset, ByteLength);
    public IReadOnlyList<ZoneCellVegetationInstance> Instances { get; }

    internal ZoneCellVegetationSector(byte[] file, int index, int offset, int length,
        ZoneCellVegetationInstance[] instances)
    {
        _file = file; SourceIndex = index; SourceOffset = offset; ByteLength = length;
        Instances = Array.AsReadOnly(instances);
    }
}

/// <summary>
/// Complete version-1 vegetation.dat input: 132-byte header and sixteen independently addressed
/// sectors of untagged 64-byte rows. 3015E930 supplies every row to 302253C0 with limit zero.
/// This reader preserves source order, signed group references and the entire owned file snapshot.
/// As the native loader seeks each sector separately, overlapping ranges are read again and bytes
/// outside those ranges are retained in RawFile rather than interpreted as additional instances.
/// Input streams remain open. Decoding alone does not install the world group catalog or collision.
/// </summary>
public sealed class ZoneCellVegetationContent
{
    public const int HeaderBytes = 132;
    public const int SectorCount = 16;
    public const int InstanceBytes = 64;
    // Managed admission bounds, not recovered native allocation limits.
    public const int MaximumFileBytes = 64 * 1024 * 1024;
    public const int MaximumInstanceCount = MaximumFileBytes / InstanceBytes;
    private readonly byte[] _file;
    public uint Version => 1;
    public int FileLength => _file.Length;
    public ReadOnlySpan<byte> RawFile => _file;
    public IReadOnlyList<ZoneCellVegetationSector> Sectors { get; }
    /// <summary>Every row in native sector/row order, including rows with an unresolved group index.</summary>
    public IReadOnlyList<ZoneCellVegetationInstance> Instances { get; }
    /// <summary>Sum of sector sizes; repeated/overlapping native reads are counted separately.</summary>
    public int ReferencedSectorBytes => checked(Instances.Count * InstanceBytes);

    private ZoneCellVegetationContent(byte[] file, ZoneCellVegetationSector[] sectors,
        ZoneCellVegetationInstance[] instances)
    { _file = file; Sectors = Array.AsReadOnly(sectors); Instances = Array.AsReadOnly(instances); }

    public static ZoneCellVegetationContent Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var buffer = new MemoryStream();
        var scratch = new byte[81920];
        int count;
        while ((count = stream.Read(scratch)) != 0)
        {
            if (buffer.Length + count > MaximumFileBytes)
                throw new InvalidDataException("Cell vegetation file exceeds the managed allocation bound.");
            buffer.Write(scratch, 0, count);
        }
        return ReadOwned(buffer.ToArray());
    }

    public static ZoneCellVegetationContent Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaximumFileBytes)
            throw new InvalidDataException("Cell vegetation file exceeds the managed allocation bound.");
        return ReadOwned(bytes.ToArray());
    }

    private static ZoneCellVegetationContent ReadOwned(byte[] file)
    {
        if (file.Length < HeaderBytes) throw new InvalidDataException("Truncated cell vegetation header.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(file) != 1)
            throw new InvalidDataException("Unsupported cell vegetation version.");
        var offsets = new int[SectorCount];
        var sizes = new int[SectorCount];
        var totalCount = 0;
        for (var i = 0; i < SectorCount; i++)
        {
            offsets[i] = ZoneCellObjectValues.Int(file, 4 + i * 4);
            sizes[i] = ZoneCellObjectValues.Int(file, 68 + i * 4);
            if (sizes[i] < 0 || sizes[i] % InstanceBytes != 0)
                throw new InvalidDataException($"Invalid vegetation sector {i} byte length {sizes[i]}.");
            // Empty descriptor offsets are deliberately not dereferenced or normalized.
            if (sizes[i] != 0 && (offsets[i] < 0 || sizes[i] > file.Length || offsets[i] > file.Length - sizes[i]))
                throw new InvalidDataException($"Vegetation sector {i} exceeds the file bounds.");
            var rowCount = sizes[i] / InstanceBytes;
            if (rowCount > MaximumInstanceCount - totalCount)
                throw new InvalidDataException("Cell vegetation rows exceed the managed allocation bound.");
            totalCount += rowCount;
        }
        var sectors = new ZoneCellVegetationSector[SectorCount];
        var instances = new ZoneCellVegetationInstance[totalCount];
        var order = 0;
        for (var i = 0; i < SectorCount; i++)
        {
            var rows = new ZoneCellVegetationInstance[sizes[i] / InstanceBytes];
            for (var row = 0; row < rows.Length; row++)
            {
                var instance = new ZoneCellVegetationInstance(file, order, offsets[i] + row * InstanceBytes, i, row);
                rows[row] = instance;
                instances[order++] = instance;
            }
            sectors[i] = new(file, i, offsets[i], sizes[i], rows);
        }
        return new(file, sectors, instances);
    }
}

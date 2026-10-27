using System.Numerics;
using System.Text;

namespace AAEmu.Zone.Content;

/// <summary>One indexed256-byte first-NUL name slot; this format has no per-slot authored word.</summary>
public sealed class ZoneCellDeferredName
{
    private readonly byte[] _file;
    public int Index { get; }
    public int SourceOffset => 4 + Index * 256;
    public string Name { get; }
    public ReadOnlySpan<byte> RawNameStorage => _file.AsSpan(SourceOffset, 256);
    internal ZoneCellDeferredName(byte[] file, int index, string name)
    { _file = file; Index = index; Name = name; }
}

/// <summary>
///3017D130/3017C870: signed count followed by256-byte name slots. Every slot and the complete
/// input survive, including empty names, bytes after each first NUL and unused trailing data.
/// The managed reader admits bounded ASCII names; it does not guess another path encoding.
/// </summary>
public sealed class ZoneCellDeferredNameTable
{
    private readonly byte[] _file;
    public int AuthoredCount { get; }
    public int FileLength => _file.Length;
    public int ConsumedBytes => 4 + AuthoredCount * 256;
    public ReadOnlySpan<byte> RawFile => _file;
    public ReadOnlySpan<byte> TrailingData => _file.AsSpan(ConsumedBytes);
    public IReadOnlyList<ZoneCellDeferredName> Names { get; }

    internal ZoneCellDeferredNameTable(byte[] file, int count, ZoneCellDeferredName[] names)
    { _file = file; AuthoredCount = count; Names = Array.AsReadOnly(names); }
}

/// <summary>
/// One untagged128-byte deferred brush record consumed by3017BA50/3021EE90/3021E840.
/// Index validation, model/material loading, sector translation and actual native publication
/// are separate. Invalid signed table indices remain intact; the native consumer skips them.
/// </summary>
public sealed class ZoneCellDeferredBrush
{
    private readonly byte[] _file;
    public int SourceOrder { get; }
    public int SourceOffset { get; }
    public int SectorIndex { get; }
    public int RowIndex { get; }
    public int SerializedLength => ZoneCellDeferredContent.BrushBytes;
    public ReadOnlySpan<byte> RawPayload => _file.AsSpan(SourceOffset, SerializedLength);
    public Vector3 BoundsMin => ZoneCellObjectValues.Vector(RawPayload, 0);
    public Vector3 BoundsMax => ZoneCellObjectValues.Vector(RawPayload, 12);
    public uint Word18 => ZoneCellObjectValues.UInt(RawPayload, 0x18);
    public uint Flags1C => ZoneCellObjectValues.UInt(RawPayload, 0x1c);
    public uint Flags20 => ZoneCellObjectValues.UInt(RawPayload, 0x20);
    public byte Byte24 => RawPayload[0x24];
    public byte Byte25 => RawPayload[0x25];
    public byte Byte26 => RawPayload[0x26];
    public ZoneCellObjectTransform AuthoredTransform => ZoneCellObjectTransform.Read(RawPayload, 0x43);
    public int MaterialIndex => ZoneCellObjectValues.Int(RawPayload, 0x73);
    public uint Word77 => ZoneCellObjectValues.UInt(RawPayload, 0x77);
    public int ModelIndex => ZoneCellObjectValues.Int(RawPayload, 0x7b);
    public byte Byte7F => RawPayload[0x7f];

    internal ZoneCellDeferredBrush(byte[] file, int order, int offset, int sector, int row)
    { _file = file; SourceOrder = order; SourceOffset = offset; SectorIndex = sector; RowIndex = row; }
}

/// <summary>
/// One of256 independently addressed brush regions in X-outer/Y-inner order. Native30172560
/// uses floor(ByteLength/128); remaining bytes are retained explicitly, not decoded as a row.
/// Empty offsets are preserved without dereferencing. Overlapping regions remain independent.
/// </summary>
public sealed class ZoneCellDeferredSector
{
    private readonly byte[] _file;
    public int SourceIndex { get; }
    public int LocalX => SourceIndex / 16;
    public int LocalY => SourceIndex % 16;
    public int SourceOffset { get; }
    public int ByteLength { get; }
    public int ConsumedRecordBytes => Instances.Count * ZoneCellDeferredContent.BrushBytes;
    public ReadOnlySpan<byte> RawPayload => ByteLength == 0 ? ReadOnlySpan<byte>.Empty : _file.AsSpan(SourceOffset, ByteLength);
    public ReadOnlySpan<byte> TrailingData => RawPayload[ConsumedRecordBytes..];
    public IReadOnlyList<ZoneCellDeferredBrush> Instances { get; }
    internal ZoneCellDeferredSector(byte[] file, int index, int offset, int length, ZoneCellDeferredBrush[] instances)
    { _file = file; SourceIndex = index; SourceOffset = offset; ByteLength = length; Instances = Array.AsReadOnly(instances); }
}

/// <summary>
/// Content inputs for3017CC30's deferred cell pass: statobjs.dat, materials.dat and brush.dat.
/// The two tables have signed counts and256-byte names. The brush header contains one preserved
/// word,256 signed offsets and256 signed lengths; native30172560 does not validate a version word.
/// Every file byte survives, including gaps, region tails, overlaps and unreferenced bytes.
/// This reader enumerates all authored regions; native distance/position gates decide which
/// regions become active. It supplies neither a native IWorld nor an engine loading operation.
/// Input streams remain open. Missing files must remain a distinct caller-level outcome.
/// </summary>
public sealed class ZoneCellDeferredContent
{
    public const int BrushHeaderBytes = 2052;
    public const int SectorCount = 256;
    public const int BrushBytes = 128;
    // Managed admission bounds, not recovered native allocation or corruption behavior.
    public const int MaximumFileBytes = 64 * 1024 * 1024;
    public const int MaximumBrushCount = MaximumFileBytes / BrushBytes;
    private readonly byte[] _brushFile;
    public ZoneCellDeferredNameTable ModelTable { get; }
    public ZoneCellDeferredNameTable MaterialTable { get; }
    public IReadOnlyList<ZoneCellDeferredName> ModelNames => ModelTable.Names;
    public IReadOnlyList<ZoneCellDeferredName> MaterialNames => MaterialTable.Names;
    public uint BrushHeaderWord => ZoneCellObjectValues.UInt(_brushFile, 0);
    public int BrushFileLength => _brushFile.Length;
    public ReadOnlySpan<byte> RawBrushFile => _brushFile;
    public IReadOnlyList<ZoneCellDeferredSector> Sectors { get; }
    public IReadOnlyList<ZoneCellDeferredBrush> Brushes { get; }
    /// <summary>Sum of authored region lengths, counting overlapping references again.</summary>
    public long ReferencedRegionBytes { get; }
    public int ReferencedRecordBytes => Brushes.Count * BrushBytes;

    private ZoneCellDeferredContent(ZoneCellDeferredNameTable models, ZoneCellDeferredNameTable materials,
        byte[] brushFile, ZoneCellDeferredSector[] sectors, ZoneCellDeferredBrush[] brushes, long regionBytes)
    {
        ModelTable = models; MaterialTable = materials; _brushFile = brushFile;
        Sectors = Array.AsReadOnly(sectors); Brushes = Array.AsReadOnly(brushes);
        ReferencedRegionBytes = regionBytes;
    }

    public static ZoneCellDeferredContent Read(Stream statobjs, Stream materials, Stream brushes)
    {
        ArgumentNullException.ThrowIfNull(statobjs);
        ArgumentNullException.ThrowIfNull(materials);
        ArgumentNullException.ThrowIfNull(brushes);
        return ReadOwned(ReadFile(statobjs), ReadFile(materials), ReadFile(brushes));
    }

    public static ZoneCellDeferredContent Read(ReadOnlySpan<byte> statobjs,
        ReadOnlySpan<byte> materials, ReadOnlySpan<byte> brushes)
    {
        if (statobjs.Length > MaximumFileBytes || materials.Length > MaximumFileBytes || brushes.Length > MaximumFileBytes)
            throw new InvalidDataException("A deferred cell file exceeds the managed allocation bound.");
        return ReadOwned(statobjs.ToArray(), materials.ToArray(), brushes.ToArray());
    }

    private static byte[] ReadFile(Stream stream)
    {
        using var buffer = new MemoryStream();
        var scratch = new byte[81920];
        int count;
        while ((count = stream.Read(scratch)) != 0)
        {
            if (buffer.Length + count > MaximumFileBytes)
                throw new InvalidDataException("A deferred cell file exceeds the managed allocation bound.");
            buffer.Write(scratch, 0, count);
        }
        return buffer.ToArray();
    }

    private static ZoneCellDeferredNameTable ReadNames(byte[] file, string kind)
    {
        if (file.Length < 4) throw new InvalidDataException($"Truncated deferred {kind} count.");
        var count = ZoneCellObjectValues.Int(file, 0);
        if (count < 0 || count > (file.Length - 4) / 256)
            throw new InvalidDataException($"Invalid deferred {kind} signed count {count}.");
        var names = new ZoneCellDeferredName[count];
        for (var i = 0; i < count; i++)
        {
            var slot = file.AsSpan(4 + i * 256, 256);
            var length = slot.IndexOf((byte)0);
            if (length < 0) throw new InvalidDataException($"Deferred {kind} name {i} has no bounded NUL terminator.");
            for (var position = 0; position < length; position++)
                if (slot[position] > 0x7f)
                    throw new InvalidDataException($"Deferred {kind} name {i} is outside the admitted ASCII encoding.");
            names[i] = new(file, i, Encoding.ASCII.GetString(slot[..length]));
        }
        return new(file, count, names);
    }

    private static ZoneCellDeferredContent ReadOwned(byte[] modelsFile, byte[] materialsFile, byte[] brushFile)
    {
        var models = ReadNames(modelsFile, "model");
        var materials = ReadNames(materialsFile, "material");
        if (brushFile.Length < BrushHeaderBytes) throw new InvalidDataException("Truncated deferred brush region header.");
        var offsets = new int[SectorCount];
        var lengths = new int[SectorCount];
        var totalCount = 0;
        long regionBytes = 0;
        for (var i = 0; i < SectorCount; i++)
        {
            offsets[i] = ZoneCellObjectValues.Int(brushFile, 4 + i * 4);
            lengths[i] = ZoneCellObjectValues.Int(brushFile, 0x404 + i * 4);
            if (lengths[i] < 0)
                throw new InvalidDataException($"Deferred brush region {i} has a negative byte length.");
            if (lengths[i] != 0 && (offsets[i] < 0 || lengths[i] > brushFile.Length || offsets[i] > brushFile.Length - lengths[i]))
                throw new InvalidDataException($"Deferred brush region {i} exceeds its file bounds.");
            var count = lengths[i] / BrushBytes; // Native right shift: preserve fractional-row tails.
            if (count > MaximumBrushCount - totalCount)
                throw new InvalidDataException("Repeated deferred brush regions exceed the managed allocation bound.");
            totalCount += count;
            regionBytes += lengths[i];
        }
        var sectors = new ZoneCellDeferredSector[SectorCount];
        var all = new ZoneCellDeferredBrush[totalCount];
        var order = 0;
        for (var sector = 0; sector < SectorCount; sector++)
        {
            var rows = new ZoneCellDeferredBrush[lengths[sector] / BrushBytes];
            for (var row = 0; row < rows.Length; row++)
            {
                var instance = new ZoneCellDeferredBrush(brushFile, order,
                    offsets[sector] + row * BrushBytes, sector, row);
                rows[row] = instance;
                all[order++] = instance;
            }
            sectors[sector] = new(brushFile, sector, offsets[sector], lengths[sector], rows);
        }
        return new(models, materials, brushFile, sectors, all, regionBytes);
    }
}

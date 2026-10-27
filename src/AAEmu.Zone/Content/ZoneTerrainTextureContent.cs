using System.Text;

namespace AAEmu.Zone.Content;

/// <summary>One 12-byte cover.ctc layer description consumed by 300D9F20.</summary>
public sealed record ZoneTerrainTextureLayer(ushort Dimension, ushort OpaqueValue,
    uint TextureFormat, uint ByteCount);

/// <summary>
/// The metadata prefix of a CRY/type-100/version-8 terrain cover file (300D9F20).
/// Reading leaves the caller's stream open at the texture payload. This does not decode
/// that payload, assign indices to terrain nodes, or allocate the engine's texture pools.
/// Truncated metadata is rejected explicitly; native does not check every later read.
/// </summary>
public sealed class ZoneTerrainTextureContent
{
    private readonly short[] _indices;

    public ushort DeclaredLayerCount { get; }
    public ushort Flags { get; }
    public float SunShadowIntensity { get; }
    public IReadOnlyList<ZoneTerrainTextureLayer> Layers { get; }
    public ReadOnlySpan<short> NodeIndices => _indices;
    /// <summary>Native per-cell index-section length, excluding both headers and layer descriptions.</summary>
    public int IndexSectionByteCount => 2 + _indices.Length * 2;
    public int ConsumedBytes => 16 + Layers.Count * 12 + IndexSectionByteCount;

    private ZoneTerrainTextureContent(ushort declaredLayerCount, ushort flags, float shadowIntensity,
        ZoneTerrainTextureLayer[] layers, short[] indices)
    {
        DeclaredLayerCount = declaredLayerCount;
        Flags = flags;
        SunShadowIntensity = shadowIntensity;
        Layers = Array.AsReadOnly(layers);
        _indices = indices;
    }

    public static ZoneTerrainTextureContent LoadFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Read(stream);
    }

    public static ZoneTerrainTextureContent Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        try
        {
            if (reader.ReadUInt32() != 0x00595243 || reader.ReadUInt16() != 100)
                throw new InvalidDataException("Terrain texture signature must be CRY/type 100.");
            if (reader.ReadUInt16() != 8)
                throw new InvalidDataException("Terrain texture version must be 8.");

            var layerCount = reader.ReadUInt16();
            var flags = reader.ReadUInt16();
            var shadowIntensity = reader.ReadSingle();
            // Native reads at most three descriptions even if the declared count is larger.
            // Its runtime table has a two-layer cell stride; preserve the input here without
            // claiming that three-layer runtime allocation/overlap is supported.
            var layers = new ZoneTerrainTextureLayer[Math.Min((int)layerCount, 3)];
            for (var i = 0; i < layers.Length; i++)
                layers[i] = new(reader.ReadUInt16(), reader.ReadUInt16(), reader.ReadUInt32(), reader.ReadUInt32());

            var indices = new short[reader.ReadUInt16()];
            for (var i = 0; i < indices.Length; i++) indices[i] = reader.ReadInt16();
            return new(layerCount, flags, shadowIntensity, layers, indices);
        }
        catch (EndOfStreamException exception)
        {
            throw new InvalidDataException("Terrain texture metadata is truncated.", exception);
        }
    }
}

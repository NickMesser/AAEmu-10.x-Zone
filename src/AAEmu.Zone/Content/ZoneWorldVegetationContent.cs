using System.Collections.ObjectModel;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace AAEmu.Zone.Content;

/// <summary>One 30107F70 group row. Unconsumed render/editor fields remain copied source attributes.</summary>
public sealed class ZoneWorldVegetationGroup
{
    public int SourceOrder { get; }
    public int Id { get; }
    public string Guid { get; }
    public string Name { get; }
    public string ModelFileName { get; }
    public string MaterialName { get; }
    public int MinimumConfigSpec { get; }
    public bool AlignToTerrain { get; }
    public IReadOnlyDictionary<string, string> Attributes { get; }
    public IReadOnlyDictionary<string, int> IntegerAttributes { get; }
    public IReadOnlyDictionary<string, float> FloatAttributes { get; }
    internal ZoneWorldVegetationGroup(int sourceOrder, XElement row)
    {
        SourceOrder = sourceOrder;
        var attributes = row.Attributes().ToDictionary(attribute => attribute.Name.LocalName, attribute => attribute.Value, StringComparer.Ordinal);
        var integers = new Dictionary<string, int>(StringComparer.Ordinal);
        var numbers = new Dictionary<string, float>(StringComparer.Ordinal);
        foreach (var name in IntegerNames)
        {
            if (!attributes.TryGetValue(name, out var text) || !int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                throw new NotSupportedException($"Vegetation group row {sourceOrder} has no supported native numeric attribute '{name}'.");
            if (name.StartsWith('b') && value is not (0 or 1))
                throw new NotSupportedException($"Vegetation boolean attribute '{name}' requires the verified 0/1 XML profile.");
            integers.Add(name, value);
        }
        foreach (var name in FloatNames)
        {
            if (!attributes.TryGetValue(name, out var text) || !float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !float.IsFinite(value))
                throw new NotSupportedException($"Vegetation group row {sourceOrder} has no finite supported native numeric attribute '{name}'.");
            numbers.Add(name, value);
        }
        //The comparison loader explicitly requires these fields instead of fabricating unspecified numeric inputs.
        Id = int.Parse(attributes["id"], CultureInfo.InvariantCulture);
        if (Id < 0 || Id > short.MaxValue) throw new NotSupportedException("Vegetation group ID exceeds the admitted signed serialized group-index profile.");
        MinimumConfigSpec = int.Parse(attributes["minConfigSpec"], CultureInfo.InvariantCulture);
        AlignToTerrain = int.Parse(attributes["bAlignToTerrain"], CultureInfo.InvariantCulture) != 0;
        Guid = attributes.GetValueOrDefault("guid", ""); Name = attributes.GetValueOrDefault("name", "");
        ModelFileName = attributes.GetValueOrDefault("modelFileName", ""); MaterialName = attributes.GetValueOrDefault("matName", "");
        Attributes = new ReadOnlyDictionary<string, string>(attributes);
        IntegerAttributes = new ReadOnlyDictionary<string, int>(integers);
        FloatAttributes = new ReadOnlyDictionary<string, float>(numbers);
    }
    private static readonly string[] IntegerNames = ["id", "bHideability", "bHideabilitySecondary", "bPickable", "bComplexBending",
        "bCastShadow", "bCastShadowExtraRange", "bCastShadowTerrain", "bRecvShadow", "bPrecShadow", "bUseAlphaBlending",
        "bUseSprites", "bSpriteCastShadow", "bRandomRotation", "bAlignToTerrain", "bUseTerrainColor", "bAffectedByVoxels",
        "minConfigSpec", "nMaterialLayers", "doodadType"];
    private static readonly string[] FloatNames = ["fBending", "fSpriteDistRatio", "fShadowDistRatio", "fMaxViewDistRatio", "fBrightness",
        "fDensity", "fElevationMax", "fElevationMin", "fSize", "fSizeVar", "fSlopeMax", "fSlopeMin", "minDistSameObject", "minDistOtherObject", "aiRadius"];
}

/// <summary>30109F90's source-order rows, later-ID replacement and sorted-ID 30108AD0 publication order.</summary>
public sealed class ZoneWorldVegetationContent
{
    public string WorldName { get; }
    public string AssetPath { get; }
    public IReadOnlyList<ZoneWorldVegetationGroup> Rows { get; }
    public IReadOnlyDictionary<int, ZoneWorldVegetationGroup> Groups { get; }
    public IReadOnlyList<ZoneWorldVegetationGroup> PublicationOrder { get; }
    private ZoneWorldVegetationContent(string worldName, string path, ZoneWorldVegetationGroup[] rows)
    {
        WorldName = worldName; AssetPath = path; Rows = Array.AsReadOnly(rows);
        var groups = new Dictionary<int, ZoneWorldVegetationGroup>();
        foreach (var row in rows) groups[row.Id] = row;
        Groups = new ReadOnlyDictionary<int, ZoneWorldVegetationGroup>(groups);
        PublicationOrder = Array.AsReadOnly(groups.Values.OrderBy(row => row.Id).ToArray());
    }
    public static ZoneWorldVegetationContent Load(IZoneAssetSource assets, string worldName)
    {
        ArgumentNullException.ThrowIfNull(assets); ArgumentException.ThrowIfNullOrWhiteSpace(worldName);
        var path = ZoneAssetPath.Validate($"game/worlds/{worldName}/vegetation.xml");
        using var input = assets.OpenRead(path) ?? throw new FileNotFoundException("The authored world vegetation catalog is absent.", path);
        return Read(input, worldName);
    }
    public static ZoneWorldVegetationContent Read(Stream input, string worldName)
    {
        ArgumentNullException.ThrowIfNull(input); ArgumentException.ThrowIfNullOrWhiteSpace(worldName);
        var path = ZoneAssetPath.Validate($"game/worlds/{worldName}/vegetation.xml");
        using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null, MaxCharactersInDocument = 32 * 1024 * 1024, CloseInput = false });
        var xml = XDocument.Load(reader);
        var list = xml.Root?.Element("groupList") ?? throw new InvalidDataException("The vegetation catalog has no authored groupList.");
        var children = list.Elements().ToArray();
        if (children.Length > 32768) throw new InvalidDataException("Vegetation catalog exceeds the bounded signed-index resource profile.");
        return new(worldName, path, children.Select((row, index) => new ZoneWorldVegetationGroup(index, row)).ToArray());
    }
}

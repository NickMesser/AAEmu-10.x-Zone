using System.Collections.Frozen;
using System.Globalization;
using System.Numerics;
using System.Xml;
using System.Xml.Linq;
using static AAEmu.Zone.Content.ZoneCharacterSkeletonContent;

namespace AAEmu.Zone.Content;

public enum ZoneCharacterAttachmentKind { Bone = 0, Face = 1, Skin = 2, Camera = 4 }
public enum ZoneCharacterAttachmentAlignment { None = 0, Bone = 1, Skin = 2, Custom = 3 }
public enum ZoneCharacterAttachmentBindingKind { File = 1, Entity = 3, Effect = 5 }

/// <summary>
/// Authored attachment declaration, not an animated world transform. CDF quaternions are w,x,y,z;
/// Rotation exposes System.Numerics x,y,z,w without changing the authored values or units.
/// </summary>
public sealed record ZoneCharacterAttachmentDefinition(string Name, ZoneCharacterAttachmentKind Kind,
    ZoneCharacterAttachmentAlignment Alignment, string BoneName, int? BoneIndex, Quaternion Rotation,
    Vector3 Position, float Scale, uint Flags, ZoneCharacterAttachmentBindingKind BindingKind,
    string Binding, string Material, IReadOnlyDictionary<string, string> SourceAttributes);

/// <summary>
/// Initial character metadata from the actual ModelFile CDF or CHR and its compiled-bone identities.
/// This does not infer equipment bindings, skeletal poses, cloth, particles or renderer instances.
/// </summary>
public sealed class ZoneCharacterDefinitionContent
{
    public const int MaximumDefinitionBytes = 4 * 1024 * 1024;
    public const int MaximumAttachmentCount = 4096;
    public string SourceAssetName { get; }
    public string SkeletonAssetName { get; }
    public string Material { get; }
    public ZoneCharacterSkeletonContent Skeleton { get; }
    public IReadOnlyList<ZoneCharacterAttachmentDefinition> Attachments { get; }
    public int Customize { get; }

    private ZoneCharacterDefinitionContent(string source, string skeletonName, string material,
        ZoneCharacterSkeletonContent skeleton, List<ZoneCharacterAttachmentDefinition> attachments, int customize)
    {
        SourceAssetName = source; SkeletonAssetName = skeletonName; Material = material;
        Skeleton = skeleton; Attachments = attachments.AsReadOnly(); Customize = customize;
    }

    public static ZoneCharacterDefinitionContent Load(IZoneAssetSource assets, string modelFile)
    {
        ArgumentNullException.ThrowIfNull(assets);
        var source = AssetName(modelFile);
        var extension = Path.GetExtension(source);
        if (extension == ".chr") return new(source, source, "", LoadSkeleton(assets, source), [], 0);
        if (extension != ".cdf")
            throw new NotSupportedException($"Initial character ModelFile '{source}' is neither the verified CDF nor CHR profile.");
        using var input = assets.OpenRead(source) ?? throw new FileNotFoundException("Character definition is absent.", source);
        using var bounded = new MemoryStream(ReadBounded(input, MaximumDefinitionBytes), false);
        using var reader = XmlReader.Create(bounded, new()
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
            MaxCharactersInDocument = MaximumDefinitionBytes, MaxCharactersFromEntities = 0
        });
        var root = XDocument.Load(reader).Root;
        if (root?.Name != "CharacterDefinition")
            throw new InvalidDataException("Character CDF root is not CharacterDefinition.");
        //315A7E30 takes the first element and requires its literal Model tag.
        var model = root.Elements().FirstOrDefault();
        if (model?.Name != "Model") throw new InvalidDataException("Character CDF must begin with its Model element.");
        var skeletonName = AssetName(Text(model, "File"));
        if (Path.GetExtension(skeletonName) != ".chr")
            throw new NotSupportedException($"CDF base resource '{skeletonName}' is not the verified CHR skeleton profile.");
        var skeleton = LoadSkeleton(assets, skeletonName);
        var list = root.Element("AttachmentList");
        var declarations = list?.Elements("Attachment").ToArray() ?? [];
        if (declarations.Length > MaximumAttachmentCount)
            throw new InvalidDataException("CDF attachment count exceeds the managed bound.");
        var attachments = new List<ZoneCharacterAttachmentDefinition>(declarations.Length);
        foreach (var element in declarations)
        {
            var name = LowerAscii(Text(element, "AName").Replace('\\', '/'));
            var kind = Text(element, "Type") switch
            {
                "CA_BONE" => ZoneCharacterAttachmentKind.Bone,
                "CA_FACE" => ZoneCharacterAttachmentKind.Face,
                "CA_SKIN" => ZoneCharacterAttachmentKind.Skin,
                "CA_CAMERA" => ZoneCharacterAttachmentKind.Camera,
                var value => throw new NotSupportedException($"CDF attachment '{name}' has unimplemented Type '{value}'.")
            };
            var alignment = LowerAscii(Text(element, "AlignBoneAttachment")) switch
            {
                "" => ZoneCharacterAttachmentAlignment.None, "bone" => ZoneCharacterAttachmentAlignment.Bone,
                "skin" => ZoneCharacterAttachmentAlignment.Skin, "custom" => ZoneCharacterAttachmentAlignment.Custom,
                var value => throw new NotSupportedException($"CDF attachment '{name}' has unimplemented alignment '{value}'.")
            };
            var bindingKind = LowerAscii(Text(element, "AttachmentType")) switch
            {
                "" => ZoneCharacterAttachmentBindingKind.File, "effect" => ZoneCharacterAttachmentBindingKind.Effect,
                "entity" => ZoneCharacterAttachmentBindingKind.Entity,
                var value => throw new NotSupportedException($"CDF attachment '{name}' has unimplemented binding kind '{value}'.")
            };
            var boneName = Text(element, "BoneName");
            var rotation = Numbers(element, "Rotation", [1, 0, 0, 0]);
            var position = Numbers(element, "Position", [0, 0, 0]);
            var scale = Number(element, "scale", 1);
            var quaternion = new Quaternion(rotation[1], rotation[2], rotation[3], rotation[0]);
            if (!float.IsFinite(quaternion.LengthSquared()) || quaternion.LengthSquared() <= 0 || scale <= 0)
                throw new InvalidDataException($"CDF attachment '{name}' has an invalid rotation or scale.");
            attachments.Add(new(name, kind, alignment, boneName, kind == ZoneCharacterAttachmentKind.Bone ? skeleton.FindBone(boneName) : null,
                quaternion, new(position[0], position[1], position[2]), scale, Unsigned(element, "Flags"), bindingKind,
                Text(element, "Binding"), Text(element, "Material"),
                element.Attributes().ToFrozenDictionary(attribute => attribute.Name.ToString(), attribute => attribute.Value, StringComparer.Ordinal)));
        }
        var customize = list is null ? 0 : Unsigned(list, "customize");
        return new(source, skeletonName, Text(model, "Material"), skeleton, attachments, customize < 10 ? (int)customize : 0);
    }

    /// <summary>Resource names are game-root relative; an already explicit game/ prefix is retained once.</summary>
    public static string AssetName(string name)
    {
        var relative = LowerAscii(ZoneAssetPath.Validate(CString(name).Replace('\\', '/')));
        return relative.StartsWith("game/", StringComparison.Ordinal) ? relative : "game/" + relative;
    }

    private static ZoneCharacterSkeletonContent LoadSkeleton(IZoneAssetSource assets, string name)
    {
        using var input = assets.OpenRead(name) ?? throw new FileNotFoundException("Character skeleton is absent.", name);
        return ZoneCharacterSkeletonContent.Read(input);
    }

    private static string Text(XElement element, string name) => (string?)element.Attribute(name) ?? "";
    private static uint Unsigned(XElement element, string name)
    {
        var text = Text(element, name);
        if (text.Length == 0) return 0;
        if (uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) return value;
        throw new InvalidDataException($"CDF attribute '{name}' is not a bounded unsigned integer.");
    }
    private static float Number(XElement element, string name, float fallback)
    {
        var text = Text(element, name);
        return text.Length == 0 ? fallback : ParseNumber(text, name);
    }
    private static float[] Numbers(XElement element, string name, float[] fallback)
    {
        var text = Text(element, name);
        if (text.Length == 0) return fallback;
        var fields = text.Split(',');
        if (fields.Length != fallback.Length) throw new InvalidDataException($"CDF attribute '{name}' has the wrong component count.");
        return fields.Select(field => ParseNumber(field, name)).ToArray();
    }
    private static float ParseNumber(string text, string name)
    {
        if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && float.IsFinite(value)) return value;
        throw new InvalidDataException($"CDF attribute '{name}' is not a finite scalar.");
    }
}

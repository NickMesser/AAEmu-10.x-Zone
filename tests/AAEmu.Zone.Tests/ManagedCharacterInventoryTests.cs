using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Content;
using AAEmu.Zone.Scene;

namespace AAEmu.Zone.Tests;

public sealed class ManagedCharacterInventoryTests
{
    [Test]
    public async Task AuthoredCdfResolvesSkeletonAndRetainsTransformsWithoutInventingAPose()
    {
        var assets = Fixture("""
            <Attachment AName="MainHand" Type="CA_BONE" BoneName="HAND" AlignBoneAttachment="Custom"
                Rotation="0.9238795,0,0,0.38268343" Position="1,2,3" scale="0.9" Flags="1" Binding=""/>
            <Attachment AName="body" Type="CA_SKIN"/>
            """);
        BinaryPrimitives.WriteUInt32LittleEndian(assets.Files["game/objects/base.chr"].AsSpan(44 + 48 + 208, 4), 0x78561a24);
        var content = ZoneCharacterDefinitionContent.Load(assets, "Objects\\Test.CDF");
        await Assert.That(content.SourceAssetName).IsEqualTo("game/objects/test.cdf");
        await Assert.That(content.SkeletonAssetName).IsEqualTo("game/objects/base.chr");
        await Assert.That(content.Skeleton.Bones.Count).IsEqualTo(2);
        await Assert.That(content.Skeleton.Bones[0].ControllerId).IsEqualTo(0x78561a24u);
        await Assert.That(content.Skeleton.FindBone("hAnD")).IsEqualTo(1);
        await Assert.That(content.Skeleton.FindBone("missing")).IsNull();
        var attachment = content.Attachments[0];
        await Assert.That(attachment.Name).IsEqualTo("mainhand");
        await Assert.That(attachment.BoneIndex).IsEqualTo(1);
        await Assert.That(attachment.Position).IsEqualTo(new Vector3(1, 2, 3));
        await Assert.That(attachment.Rotation).IsEqualTo(new Quaternion(0, 0, .38268343f, .9238795f));
        await Assert.That(attachment.Scale).IsEqualTo(.9f);
        await Assert.That(attachment.Alignment).IsEqualTo(ZoneCharacterAttachmentAlignment.Custom);
        await Assert.That(attachment.SourceAttributes["AName"]).IsEqualTo("MainHand");
    }

    [Test]
    public async Task RealSlotLookupSeparatesAbsentNamesFromPresentMutableUnboundSockets()
    {
        var assets = Fixture("""
            <Attachment AName="MainHand" Type="CA_BONE" BoneName="hand" Flags="1"/>
            <Attachment AName="body" Type="CA_SKIN" Flags="1"/>
            """);
        var actor = new ZoneActorHandle(100, 1);
        using var scene = new ZoneManagedCharacterSlots(assets, handle => handle == actor);
        await Assert.That(() => scene.HideAttachment(actor, 0, "Animated LAW", true, true)).Throws<NotSupportedException>();
        using var owner = scene.BindInitial(actor, Model());
        await Assert.That(owner.Definition.Attachments.Count).IsEqualTo(2);
        await Assert.That(owner.AttachmentCount).IsEqualTo(1);
        await Assert.That(owner.HasSlot(0)).IsTrue();
        await Assert.That(owner.HasSlot(2)).IsFalse();
        await Assert.That(scene.HideAttachment(actor, 2, "mainhand", true, true)).IsEqualTo(ZoneManagedAttachmentHideResult.AbsentSlot);
        await Assert.That(scene.HideAttachment(actor, 0, "Animated LAW", true, true)).IsEqualTo(ZoneManagedAttachmentHideResult.AbsentAttachment);
        await Assert.That(scene.HideAttachment(actor, 0, "body", false, false)).IsEqualTo(ZoneManagedAttachmentHideResult.AbsentAttachment);
        var initial = owner.FindAttachment(0, "MAINHAND")!;
        await Assert.That(initial.Hidden).IsTrue();
        await Assert.That(initial.SecondaryVisibilityHidden).IsTrue();
        await Assert.That(scene.HideAttachment(actor, 0, "MainHand\0ignored", false, true)).IsEqualTo(ZoneManagedAttachmentHideResult.Updated);
        var updated = owner.FindAttachment(0, "mainhand")!;
        await Assert.That(updated.Hidden).IsFalse();
        await Assert.That(updated.SecondaryVisibilityHidden).IsTrue();
        await Assert.That(initial.Hidden).IsTrue(); //Copied old observations cannot mutate with the owner.
        await Assert.That(updated.Definition.Flags).IsEqualTo(1u);
    }

    [Test]
    public async Task BoundParticleOrObjectAndUnownedClientConstructionRefuseBeforeVisibilityMutation()
    {
        var assets = Fixture("""
            <Attachment AName="effect" Type="CA_BONE" BoneName="root" AttachmentType="effect" Binding="X2_EFFECTS.authored" Flags="1"/>
            <Attachment AName="model" Type="CA_BONE" BoneName="hand" Binding="objects/actual.cgf" Flags="1"/>
            <Attachment AName="body" Type="CA_SKIN" Flags="1"/>
            """);
        var actor = new ZoneActorHandle(100, 1);
        using var scene = new ZoneManagedCharacterSlots(assets, _ => true, ZoneCharacterAttachmentPublicationProfile.AllAuthoredTypes);
        using var owner = scene.BindInitial(actor, Model());
        foreach (var name in new[] { "effect", "model", "body" })
        {
            var before = owner.FindAttachment(0, name)!;
            await Assert.That(before.VisibilityRefusal).IsNotNull();
            await Assert.That(() => scene.HideAttachment(actor, 0, name, false, false)).Throws<NotSupportedException>();
            await Assert.That(owner.FindAttachment(0, name)).IsEqualTo(before);
        }
        await Assert.That(scene.HideAttachment(actor, 0, "Animated LAW", true, true)).IsEqualTo(ZoneManagedAttachmentHideResult.AbsentAttachment);
    }

    [Test]
    public async Task OriginalBoneAndDuplicateNameAdmissionRetainsFirstActuallyCreatedAttachment()
    {
        var assets = Fixture("""
            <Attachment AName="socket" Type="CA_BONE" BoneName="absent" Flags="0"/>
            <Attachment AName="Socket" Type="CA_BONE" BoneName="hand" Flags="1"/>
            <Attachment AName="SOCKET" Type="CA_BONE" BoneName="root" Flags="0"/>
            <Attachment AName="path\socket" Type="CA_BONE" BoneName="hand"/>
            """);
        var actor = new ZoneActorHandle(100, 1);
        using var scene = new ZoneManagedCharacterSlots(assets, _ => true);
        using var owner = scene.BindInitial(actor, Model());
        await Assert.That(owner.Definition.Attachments.Count).IsEqualTo(4);
        await Assert.That(owner.AttachmentCount).IsEqualTo(2);
        await Assert.That(owner.Diagnostics.Count).IsEqualTo(2);
        await Assert.That(owner.FindAttachment(0, "socket")!.Definition.BoneIndex).IsEqualTo(1);
        await Assert.That(owner.FindAttachment(0, "socket")!.Hidden).IsTrue();
        await Assert.That(owner.FindAttachment(0, "path/socket")).IsNotNull();
        await Assert.That(owner.FindAttachment(0, "path\\socket")).IsNull();
    }

    [Test]
    public async Task DirectChrHasVerifiedEmptyInitialAttachmentsAndCachesOnlyImmutableResources()
    {
        var assets = Fixture("<Attachment AName='socket' Type='CA_BONE' BoneName='hand'/>");
        var current = new HashSet<ZoneActorHandle> { new(100, 1), new(101, 1), new(102, 1) };
        using var scene = new ZoneManagedCharacterSlots(assets, current.Contains);
        using var first = scene.BindInitial(new(100, 1), Model());
        using var second = scene.BindInitial(new(101, 1), Model());
        await Assert.That(ReferenceEquals(first.Definition, second.Definition)).IsTrue();
        scene.HideAttachment(first.Actor, 0, "socket", true, false);
        await Assert.That(second.FindAttachment(0, "socket")!.Hidden).IsFalse();
        using var direct = scene.BindInitial(new(102, 1), Model("objects/base.chr"));
        await Assert.That(direct.HasSlot(0)).IsTrue();
        await Assert.That(direct.Definition.Attachments.Count).IsEqualTo(0);
        await Assert.That(scene.HideAttachment(direct.Actor, 0, "socket", true, true)).IsEqualTo(ZoneManagedAttachmentHideResult.AbsentAttachment);
    }

    [Test]
    public async Task RetirementAndReplacementCannotReuseOrMutateTheOldSlotActivation()
    {
        var assets = Fixture("<Attachment AName='socket' Type='CA_BONE' BoneName='hand'/>");
        var actor = new ZoneActorHandle(100, 1);
        using var scene = new ZoneManagedCharacterSlots(assets, handle => handle == actor);
        var old = scene.BindInitial(actor, Model());
        await Assert.That(() => scene.BindInitial(actor, Model())).Throws<InvalidOperationException>();
        actor = new(100, 2);
        await Assert.That(() => old.FindAttachment(0, "socket")).Throws<InvalidOperationException>();
        using var replacement = scene.BindInitial(actor, Model());
        old.Dispose(); old.Dispose();
        await Assert.That(scene.LiveCount).IsEqualTo(1);
        await Assert.That(scene.Find(actor)).IsEqualTo(replacement);
        scene.Dispose();
        await Assert.That(replacement.IsDisposed).IsTrue();
        await Assert.That(scene.LiveCount).IsEqualTo(0);
        await Assert.That(() => replacement.HasSlot(0)).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task ResourceCallbacksCannotPublishAfterGenerationRetirementOrSceneDisposal()
    {
        var assets = Fixture("<Attachment AName='socket' Type='CA_BONE' BoneName='hand'/>");
        var active = true;
        using var scene = new ZoneManagedCharacterSlots(assets, _ => active);
        assets.Opened = _ => active = false;
        await Assert.That(() => scene.BindInitial(new(100, 1), Model())).Throws<InvalidOperationException>();
        await Assert.That(scene.LiveCount).IsEqualTo(0);
        await Assert.That(scene.CachedDefinitionCount).IsEqualTo(0);
        active = true;
        assets.Opened = _ => scene.Dispose();
        await Assert.That(() => scene.BindInitial(new(100, 1), Model())).Throws<ObjectDisposedException>();
        await Assert.That(scene.LiveCount).IsEqualTo(0);
    }

    [Test]
    public async Task MissingMalformedOrUnsupportedResourcesNeverBecomeAnEmptyLoadedSlot()
    {
        var assets = Fixture("<Attachment AName='socket' Type='CA_UNKNOWN' BoneName='hand'/>");
        using var scene = new ZoneManagedCharacterSlots(assets, _ => true);
        await Assert.That(() => scene.BindInitial(new(100, 1), Model())).Throws<NotSupportedException>();
        await Assert.That(scene.LiveCount).IsEqualTo(0);
        assets.Files.Remove("game/objects/base.chr");
        await Assert.That(() => scene.BindInitial(new(100, 1), Model())).Throws<FileNotFoundException>();
        var bytes = Skeleton("root");
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40, 4), uint.MaxValue);
        using var oversized = new MemoryStream(bytes);
        await Assert.That(() => ZoneCharacterSkeletonContent.Read(oversized)).Throws<InvalidDataException>();
        bytes = Skeleton("root");
        bytes.AsSpan(44 + 48 + 216, 48).Fill((byte)'a');
        using var unterminated = new MemoryStream(bytes);
        await Assert.That(() => ZoneCharacterSkeletonContent.Read(unterminated)).Throws<InvalidDataException>();
        await Assert.That(() => ZoneCharacterDefinitionContent.AssetName("../objects/a.chr")).Throws<ArgumentException>();
    }

    [Test]
    public async Task AuthoredDuplicateBoneNamesRemainInventoryDataButCannotChooseAnUnverifiedBindingTarget()
    {
        var assets = Fixture("");
        assets.Files["game/objects/base.chr"] = Skeleton("root", "ROOT");
        var content = ZoneCharacterDefinitionContent.Load(assets, "objects/base.chr");
        await Assert.That(content.Skeleton.Bones.Count).IsEqualTo(2);
        await Assert.That(content.Skeleton.AmbiguousBoneNameCrcs.Count).IsEqualTo(1);
        await Assert.That(() => content.Skeleton.FindBone("root")).Throws<NotSupportedException>();
        await Assert.That(content.Skeleton.FindBone("not-a-bone")).IsNull();
        using var scene = new ZoneManagedCharacterSlots(assets, _ => true);
        using var direct = scene.BindInitial(new(100, 1), Model("objects/base.chr"));
        await Assert.That(scene.HideAttachment(direct.Actor, 0, "Animated LAW", true, true)).IsEqualTo(ZoneManagedAttachmentHideResult.AbsentAttachment);
        assets.Files["game/objects/test.cdf"] = Encoding.UTF8.GetBytes("<CharacterDefinition><Model File='objects/base.chr'/><AttachmentList><Attachment AName='socket' Type='CA_BONE' BoneName='root'/></AttachmentList></CharacterDefinition>");
        await Assert.That(() => scene.BindInitial(new(101, 1), Model())).Throws<NotSupportedException>();
        await Assert.That(scene.LiveCount).IsEqualTo(1);
    }

    private static ZoneActorModel Model(string path = "objects/test.cdf") => new(new() { Id = 7, ModelFile = path }, []);

    private static Assets Fixture(string attachments)
    {
        var assets = new Assets();
        assets.Files.Add("game/objects/base.chr", Skeleton("root", "hand"));
        assets.Files.Add("game/objects/test.cdf", Encoding.UTF8.GetBytes("<CharacterDefinition><Model File='game/objects/base.chr'/><AttachmentList>" + attachments + "</AttachmentList></CharacterDefinition>"));
        return assets;
    }

    private static byte[] Skeleton(params string[] names)
    {
        const int chunk = 44;
        var size = 48 + names.Length * 324;
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.UTF8, true);
        writer.Write("CryTek\0\0"u8); writer.Write(0xffff0000u); writer.Write(0x745u); writer.Write(20);
        writer.Write(1); writer.Write(0xacdc0000u); writer.Write(0x801u); writer.Write(chunk); writer.Write(19); writer.Write(size);
        writer.Write(0xacdc0000u); writer.Write(0x801u); writer.Write(chunk); writer.Write(19); writer.Write(new byte[size - 16]);
        var result = output.ToArray();
        for (var i = 0; i < names.Length; i++) Encoding.UTF8.GetBytes(names[i]).CopyTo(result, chunk + 48 + i * 324 + 216);
        return result;
    }

    private sealed class Assets : IZoneAssetSource
    {
        internal Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
        internal Action<string>? Opened { get; set; }
        public Stream? OpenRead(string virtualPath)
        {
            Opened?.Invoke(virtualPath);
            return Files.TryGetValue(virtualPath, out var bytes) ? new MemoryStream(bytes, false) : null;
        }
    }
}

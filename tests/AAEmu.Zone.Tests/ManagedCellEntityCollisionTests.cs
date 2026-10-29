using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using AAEmu.Zone.Content;
using AAEmu.Zone.Scene;

namespace AAEmu.Zone.Tests;

public sealed class ManagedCellEntityCollisionTests
{
    private const string Cell = "game/worlds/main_world/cells/009_014/client/entities.xml";
    private const string LightScript = "game/scripts/entities/lights/light.lua";

    [Test]
    public async Task FogSourceCertificateMatchesCopiedOriginalFreshAndResizedRenderNodeControls()
    {
        using var source = Assets.Original();
        var assembly = typeof(ManagedCellEntityCollisionTests).Assembly;
        using var stream = assembly.GetManifestResourceStream(assembly.GetName().Name + ".Fixtures.CellEntityCollision.fog-node-native-controls.json")!;
        using var native = JsonDocument.Parse(stream);
        await Assert.That(native.RootElement.GetProperty("engineModuleLoads").GetInt32()).IsEqualTo(0);
        await Assert.That(native.RootElement.GetProperty("entityPrimaryTable").GetString()).IsEqualTo("0x320cd998");
        await Assert.That(native.RootElement.GetProperty("entityLoadFogTarget").GetString()).IsEqualTo("0x3200f5b0");
        foreach (var control in native.RootElement.GetProperty("controls").EnumerateArray())
        {
            var document = OriginalDependencyDocument(source, "FogVolume");
            var size = First(document, "FogVolume").Element("Properties")!.Element("Size")!;
            var half = control.GetProperty("actualHalfSize").EnumerateArray().Select(value => value.GetSingle()).ToArray();
            foreach (var (axis, index) in new[] { ("x", 0), ("y", 1), ("z", 2) })
                size.SetAttributeValue(axis, (half[index] * 2).ToString(System.Globalization.CultureInfo.InvariantCulture));
            using var classifier = new ZoneManagedCellEntityCollision(source);
            var result = classifier.Classify(ZoneCellEntityContent.Read(document.ToString()), "game/fog-render-control.xml");
            await Assert.That(result.Rows.Single(row => row.ClassName == "FogVolume").CertifiedPartFlags).IsEqualTo(0x8002u);
            await Assert.That(control.GetProperty("physicalPointer").GetInt64()).IsEqualTo(0L);
            await Assert.That(control.GetProperty("externalSetPhysicsIgnored").GetBoolean()).IsTrue();
            await Assert.That(control.GetProperty("physicalizationPreservesNode").GetBoolean()).IsTrue();
        }
    }

    [Test]
    public async Task AllThirtyFiveOriginalSoundSpotAndFogRowsKeepExactCrossCellInputsAndDependencyProof()
    {
        AssertNoEngineModules();
        using var source = Assets.Original();
        using var classifier = new ZoneManagedCellEntityCollision(source);
        var sounds = 0; var fog = 0;
        foreach (var cell in new[] { "011_014", "011_015", "010_014" })
        {
            var path = "game/worlds/main_world/cells/" + cell + "/client/entities.xml";
            var input = ZoneCellEntityContent.Read(source.Files[path]);
            var result = classifier.Classify(input, path);
            await Assert.That(result.Document.Sha256).IsEqualTo(Convert.ToHexString(SHA256.HashData(source.Files[path])));
            await Assert.That(ReferenceEquals(result.Input, input)).IsTrue();
            await Assert.That(result.Rows.Count).IsEqualTo(input.Objects!.Children.Count);
            foreach (var row in result.Rows.Where(row => row.ClassName is "SoundEventSpot" or "FogVolume"))
            {
                await Assert.That(row.Outcome).IsEqualTo(ZoneCellEntityCollisionOutcome.NoPhysicalContribution);
                await Assert.That(row.CertifiedPartFlags).IsEqualTo(0x8002u);
                await Assert.That(ReferenceEquals(row.InputRow, input.Entities.Single(original => original.SourceChildIndex == row.SourceChildIndex))).IsTrue();
                await Assert.That(row.Dependencies.Count).IsEqualTo(2);
                await Assert.That(row.Dependencies.All(dependency => dependency.Sha256 == Convert.ToHexString(SHA256.HashData(source.Files[dependency.AssetPath])))).IsTrue();
                if (row.ClassName == "SoundEventSpot") sounds++; else fog++;
            }
            await Assert.That(result.Rows.Where(row => row.ClassName is "AnimObject" or "BasicEntity" or "Birds" or "Chickens" or "Fish")
                .All(row => row.Outcome == ZoneCellEntityCollisionOutcome.Unsupported)).IsTrue();
        }
        await Assert.That(sounds).IsEqualTo(34);
        await Assert.That(fog).IsEqualTo(1);
        AssertNoEngineModules();
    }

    [Test]
    [Arguments("SoundEventSpot", "geometry")]
    [Arguments("SoundEventSpot", "flowgraph")]
    [Arguments("SoundEventSpot", "properties")]
    [Arguments("SoundEventSpot", "creator")]
    [Arguments("FogVolume", "geometry")]
    [Arguments("FogVolume", "flowgraph")]
    [Arguments("FogVolume", "properties")]
    [Arguments("FogVolume", "creator")]
    public async Task NewSoundAndFogProfilesCannotHideAdditionalPhysicalOrDynamicProducers(string type, string mutation)
    {
        using var source = Assets.Original();
        var document = OriginalDependencyDocument(source, type);
        var entity = First(document, type);
        var guid = entity.Attribute("EntityGuid")!.Value;
        if (mutation == "geometry") entity.SetAttributeValue("Geometry", "objects/solid.cgf");
        if (mutation == "flowgraph") entity.Add(new XElement("FlowGraph"));
        if (mutation == "properties") entity.Element("Properties")!.SetAttributeValue("Physicalize", "1");
        if (mutation == "creator")
        {
            var path = "game/entities/" + type.ToLowerInvariant() + ".ent";
            var description = XElement.Parse(Encoding.UTF8.GetString(source.Files[path]));
            description.SetAttributeValue("CustomCreator", "CreateRigidBody");
            source.Files[path] = Encoding.UTF8.GetBytes(description.ToString());
        }
        using var classifier = new ZoneManagedCellEntityCollision(source);
        var result = classifier.Classify(ZoneCellEntityContent.Read(document.ToString()), "game/test-mutated-entity.xml");
        var failure = result.Rows.Single(row => row.EntityGuid == Convert.ToUInt64(guid, 16));
        await Assert.That(failure.Outcome).IsEqualTo(ZoneCellEntityCollisionOutcome.Unsupported);
        await Assert.That(failure.CertifiedPartFlags).IsEqualTo(0u);
    }

    [Test]
    [Arguments("SoundEventSpot", false)]
    [Arguments("SoundEventSpot", true)]
    [Arguments("FogVolume", false)]
    [Arguments("FogVolume", true)]
    public async Task NewSoundAndFogScriptDependenciesMustExistAndRetainTheReviewedBytes(string type, bool missing)
    {
        using var source = Assets.Original();
        var document = OriginalDependencyDocument(source, type);
        var path = "game/scripts/entities/" + (type == "FogVolume" ? "render/fogvolume.lua" : "sound/soundeventspot.lua");
        if (missing) source.Files.Remove(path);
        else source.Files[path] = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(source.Files[path]) + "\nScript.ReloadScript('physical.lua');");
        using var classifier = new ZoneManagedCellEntityCollision(source);
        var result = classifier.Classify(ZoneCellEntityContent.Read(document.ToString()), "game/test-dependency-entity.xml");
        var rows = result.Rows.Where(row => row.ClassName == type).ToArray();
        await Assert.That(rows.Length).IsGreaterThan(0);
        await Assert.That(rows.All(row => row.Outcome == (missing ? ZoneCellEntityCollisionOutcome.MissingData : ZoneCellEntityCollisionOutcome.Unsupported))).IsTrue();
        await Assert.That(rows.All(row => row.Dependencies.Any(dependency => dependency.AssetPath == path))).IsTrue();
    }

    [Test]
    [Arguments("missing-size")]
    [Arguments("nested-size")]
    [Arguments("negative-size")]
    [Arguments("nonfinite-size")]
    [Arguments("unknown-volume")]
    [Arguments("unknown-property")]
    public async Task FogRequiresTheWholeFiniteReviewedPropertiesAndSizeProfile(string mutation)
    {
        using var source = Assets.Original(); var document = OriginalDependencyDocument(source, "FogVolume");
        var properties = First(document, "FogVolume").Element("Properties")!;
        if (mutation == "missing-size") properties.Element("Size")!.Remove();
        if (mutation == "nested-size") properties.Element("Size")!.Add(new XElement("PhysicalGeometry"));
        if (mutation == "negative-size") properties.Element("Size")!.SetAttributeValue("x", "-1");
        if (mutation == "nonfinite-size") properties.Element("Size")!.SetAttributeValue("y", "NaN");
        if (mutation == "unknown-volume") properties.SetAttributeValue("VolumeType", "9");
        if (mutation == "unknown-property") properties.SetAttributeValue("Material", "unowned");
        using var classifier = new ZoneManagedCellEntityCollision(source);
        var result = classifier.Classify(ZoneCellEntityContent.Read(document.ToString()), "game/test-fog-entity.xml");
        await Assert.That(result.Rows.Single(row => row.ClassName == "FogVolume").Outcome).IsEqualTo(ZoneCellEntityCollisionOutcome.Unsupported);
    }

    private static XDocument OriginalDependencyDocument(Assets source, string type) => XDocument.Parse(Encoding.UTF8.GetString(
        source.Files["game/worlds/main_world/cells/" + (type == "FogVolume" ? "010_014" : "011_014") + "/client/entities.xml"]));

    [Test]
    public async Task AllOriginalRowsResolveActualClassesScriptsParticleGraphsAndTwentyThreeSoundLinks()
    {
        AssertNoEngineModules();
        using var source = Assets.Original();
        var input = source.Input();
        using var classifier = new ZoneManagedCellEntityCollision(source);
        var result = classifier.Classify(input, Cell);
        await Assert.That(ReferenceEquals(result.Input, input)).IsTrue();
        await Assert.That(ReferenceEquals(result.Owner, classifier)).IsTrue();
        await Assert.That(result.Document.Sha256).IsEqualTo("C5F294240DF725956560E86CA8917EB54F2DD3C56186C2A8EDD22C151C7D9DB9");
        await Assert.That(result.Document.ByteLength).IsEqualTo(source.Files[Cell].Length);
        await Assert.That(result.DocumentDiagnostics.Count).IsEqualTo(0);
        await Assert.That(result.Rows.Count).IsEqualTo(74);
        await Assert.That(result.NoPhysicalContributionCount).IsEqualTo(74);
        await Assert.That(result.RefusedCount).IsEqualTo(0);
        await Assert.That(result.Dependencies.Count).IsEqualTo(15);
        await Assert.That(source.Opened.Count).IsEqualTo(14);
        await Assert.That(source.Closed).IsEqualTo(14);
        await Assert.That(source.Opened.Contains(Cell)).IsFalse();
        foreach (var row in result.Rows)
        {
            await Assert.That(ReferenceEquals(row.InputRow, input.Entities[row.SourceChildIndex])).IsTrue();
            await Assert.That(row.CertifiedPartFlags).IsEqualTo(0x8002u);
            await Assert.That(row.Diagnostics.Count).IsEqualTo(0);
            await Assert.That(row.CanonicalXmlSha256).IsEqualTo(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(row.InputRow!.Xml.Xml))));
            await Assert.That(row.Dependencies.Count).IsGreaterThan(0);
        }
        var particles = result.Rows.Where(row => row.ClassName == "ParticleEffect").ToArray();
        await Assert.That(particles.Length).IsEqualTo(33);
        await Assert.That(particles.Select(row => row.ParticleGraph).Distinct().Count()).IsEqualTo(9);
        await Assert.That(particles.Select(row => row.ParticleGraph!).Distinct().Sum(graph => graph.Emitters.Count)).IsEqualTo(28);
        var areas = result.Rows.Where(row => row.ClassName == "AreaShape").ToArray();
        await Assert.That(areas.Length).IsEqualTo(5);
        await Assert.That(areas.Sum(row => row.AreaLinks.Count)).IsEqualTo(23);
        foreach (var link in areas.SelectMany(row => row.AreaLinks))
        {
            var target = result.Rows[link.TargetSourceChildIndex];
            await Assert.That(target.EntityGuid).IsEqualTo(link.EntityGuid);
            await Assert.That(target.ClassName).IsEqualTo(link.TargetClassName);
            await Assert.That(target.CanonicalXmlSha256).IsEqualTo(link.TargetCanonicalXmlSha256);
            await Assert.That(target.ClassName is "AmbientVolume" or "RandomSoundVolume" or "ReverbVolume").IsTrue();
        }
        await Assert.That(() => ((IList<ZoneCellEntityCollisionRow>)result.Rows).Clear()).Throws<NotSupportedException>();
        await Assert.That(() => ((IList<ZoneCellEntityCollisionDependency>)result.Dependencies).Clear()).Throws<NotSupportedException>();
        AssertNoEngineModules();
    }

    [Test]
    [Arguments("Geometry", "objects/solid.cgf")]
    [Arguments("Archetype", "SolidArchetype")]
    [Arguments("ParentGUID", "0")]
    [Arguments("ParentId", "0")]
    [Arguments("Material", "objects/override.mtl")]
    [Arguments("Script", "scripts/physical.lua")]
    [Arguments("CustomCreator", "createBody")]
    [Arguments("Physicalize", "1")]
    public async Task ActualUnsafeRowAttributesRevokeTheRowRatherThanRelyOnItsClassName(string attribute, string value)
    {
        using var source = Assets.Original();
        var input = Mutate(source, document => First(document, "Light").SetAttributeValue(attribute, value));
        using var classifier = new ZoneManagedCellEntityCollision(source);
        var result = classifier.Classify(input, Cell);
        await Assert.That(result.RefusedCount).IsEqualTo(1);
        var row = result.Rows.Single(row => row.Outcome != ZoneCellEntityCollisionOutcome.NoPhysicalContribution);
        await Assert.That(row.ClassName).IsEqualTo("Light");
        await Assert.That(row.CertifiedPartFlags).IsEqualTo(0u);
        await Assert.That(row.Diagnostics.Single().Message.Contains(attribute, StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    [Arguments("FlowGraph")]
    [Arguments("Rope")]
    [Arguments("Properties2")]
    [Arguments("EntityLinks")]
    [Arguments("Physics")]
    public async Task UnownedProxyAndScriptDataCannotHideBehindAReviewedLightScript(string child)
    {
        using var source = Assets.Original();
        var input = Mutate(source, document => First(document, "Light").Add(new XElement(child)));
        using var classifier = new ZoneManagedCellEntityCollision(source);
        var result = classifier.Classify(input, Cell);
        await Assert.That(result.RefusedCount).IsEqualTo(1);
        await Assert.That(result.Rows.Single(row => row.Outcome != ZoneCellEntityCollisionOutcome.NoPhysicalContribution).CertifiedPartFlags).IsEqualTo(0u);
    }

    [Test]
    [Arguments("name")]
    [Arguments("script")]
    [Arguments("invisible")]
    [Arguments("creator")]
    [Arguments("child")]
    public async Task ActualClassDescriptionMustProveTheOrdinaryRegistrationFields(string mutation)
    {
        using var source = Assets.Original();
        var node = XElement.Parse(Encoding.UTF8.GetString(source.Files["game/entities/light.ent"]));
        if (mutation == "name") node.SetAttributeValue("Name", "GeomEntity");
        if (mutation == "script") node.SetAttributeValue("Script", "Scripts/Entities/Other.lua");
        if (mutation == "invisible") node.SetAttributeValue("Invisible", 1);
        if (mutation == "creator") node.SetAttributeValue("CustomCreator", "CreateBody");
        if (mutation == "child") node.Add(new XElement("Creator", "unowned"));
        source.Files["game/entities/light.ent"] = Encoding.UTF8.GetBytes(node.ToString());
        using var classifier = new ZoneManagedCellEntityCollision(source);
        var result = classifier.Classify(source.Input(), Cell);
        var lights = result.Rows.Where(row => row.ClassName == "Light").ToArray();
        await Assert.That(lights.Length).IsEqualTo(13);
        await Assert.That(lights.All(row => row.Outcome == ZoneCellEntityCollisionOutcome.Unsupported)).IsTrue();
        await Assert.That(source.Opened.Contains(LightScript)).IsFalse();
    }

    [Test]
    public async Task ScriptBytesAndMissingDependenciesAreCheckedEvenWhenNamesAndRowsMatch()
    {
        foreach (var missing in new[] { false, true })
        {
            using var source = Assets.Original();
            if (missing) source.Files.Remove(LightScript);
            else source.Files[LightScript] = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(source.Files[LightScript]) + "\nScript.ReloadScript('physical.lua');");
            using var classifier = new ZoneManagedCellEntityCollision(source);
            var result = classifier.Classify(source.Input(), Cell);
            var lights = result.Rows.Where(row => row.ClassName == "Light").ToArray();
            await Assert.That(lights.All(row => row.Outcome == (missing ? ZoneCellEntityCollisionOutcome.MissingData : ZoneCellEntityCollisionOutcome.Unsupported))).IsTrue();
            await Assert.That(lights.All(row => row.Dependencies.Any(item => item.AssetPath == LightScript))).IsTrue();
            await Assert.That(result.NoPhysicalContributionCount).IsEqualTo(61);
        }
    }

    [Test]
    [Arguments("unknown-parameter")]
    [Arguments("nested-physical")]
    [Arguments("missing-library")]
    [Arguments("missing-effect")]
    [Arguments("attachment")]
    public async Task ParticleRowsRequireTheActualWholeGraphAndSafeEntityEmitterProperties(string mutation)
    {
        using var source = Assets.Original();
        var input = source.Input();
        if (mutation is "missing-effect" or "attachment")
            input = Mutate(source, document => First(document, "ParticleEffect").Element("Properties")!
                .SetAttributeValue(mutation == "attachment" ? "AttachType" : "ParticleEffect", mutation == "attachment" ? "Physics" : "x2_sounds.missing"));
        else
        {
            const string library = "game/libs/particles/x2_sounds.xml";
            if (mutation == "missing-library") source.Files.Remove(library);
            else
            {
                var xml = XDocument.Parse(Encoding.UTF8.GetString(source.Files[library]));
                var root = xml.Root!.Elements("Particles").Single(node => node.Attribute("Name")!.Value == "particles.stubble_poison_fog");
                if (mutation == "unknown-parameter") root.Element("Params")!.SetAttributeValue("UnownedCollision", "true");
                else root.Add(new XElement("Childs", new XElement("Particles", new XAttribute("Name", "hidden"),
                    new XElement("Params", new XAttribute("Enabled", "false"), new XAttribute("PhysicsType", "RigidBody")))));
                source.Files[library] = Encoding.UTF8.GetBytes(xml.ToString());
            }
        }
        using var classifier = new ZoneManagedCellEntityCollision(source);
        var result = classifier.Classify(input, Cell);
        var failures = result.Rows.Where(row => row.ClassName == "ParticleEffect" && row.Outcome != ZoneCellEntityCollisionOutcome.NoPhysicalContribution).ToArray();
        await Assert.That(failures.Length).IsGreaterThan(0);
        await Assert.That(failures.All(row => row.CertifiedPartFlags == 0)).IsTrue();
        await Assert.That(result.Rows.Where(row => row.ClassName != "ParticleEffect").All(row => row.Outcome == ZoneCellEntityCollisionOutcome.NoPhysicalContribution)).IsTrue();
        if (mutation == "nested-physical")
            await Assert.That(failures.All(row => row.ParticleGraph!.Emitters.Any(emitter => emitter.PhysicsType == ZoneParticlePhysicsKind.RigidBody))).IsTrue();
    }

    [Test]
    [Arguments("VolumeRadius")]
    [Arguments("Gravity")]
    [Arguments("BezierPoints")]
    [Arguments("TimeOfDay")]
    [Arguments("SkyBox")]
    public async Task PhysicalAreaBranchesAndUnreviewedEnvironmentDataRemainUnsupported(string mutation)
    {
        using var source = Assets.Original();
        var input = Mutate(source, document =>
        {
            var area = First(document, "AreaShape").Element("Area")!;
            if (mutation is "TimeOfDay" or "SkyBox") area.Element(mutation)!.SetAttributeValue("Resource", "unowned");
            else if (mutation == "BezierPoints") area.Add(new XElement("BezierPoints", new XElement("Point")));
            else area.SetAttributeValue(mutation, "10");
        });
        using var classifier = new ZoneManagedCellEntityCollision(source);
        var result = classifier.Classify(input, Cell);
        await Assert.That(result.RefusedCount).IsEqualTo(1);
        await Assert.That(result.Rows.Single(row => row.Outcome != ZoneCellEntityCollisionOutcome.NoPhysicalContribution).ClassName).IsEqualTo("AreaShape");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task AreaLinksResolveOnlyUniqueCertifiedSoundsInsideThisExactInput(bool knownNonSound)
    {
        using var source = Assets.Original();
        var input = Mutate(source, document => First(document, "AreaShape").Element("Area")!.Element("Entities")!.Elements().First()
            .SetAttributeValue("EntityGuid", knownNonSound ? First(document, "Light").Attribute("EntityGuid")!.Value : "123456789abcdef0"));
        using var classifier = new ZoneManagedCellEntityCollision(source);
        var result = classifier.Classify(input, Cell);
        await Assert.That(result.RefusedCount).IsEqualTo(1);
        var failure = result.Rows.Single(row => row.Outcome != ZoneCellEntityCollisionOutcome.NoPhysicalContribution);
        await Assert.That(failure.ClassName).IsEqualTo("AreaShape");
        await Assert.That(failure.Diagnostics.Any(item => item.Message.Contains("link", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task AFailedSoundDependencyRevokesItsLinkedAreaProofAsWell()
    {
        using var source = Assets.Original();
        source.Files.Remove("game/scripts/entities/sound/reverbvolume.lua");
        using var classifier = new ZoneManagedCellEntityCollision(source);
        var result = classifier.Classify(source.Input(), Cell);
        await Assert.That(result.Rows.Where(row => row.ClassName == "ReverbVolume").All(row => row.Outcome == ZoneCellEntityCollisionOutcome.MissingData)).IsTrue();
        await Assert.That(result.Rows.Any(row => row.ClassName == "AreaShape" && row.Outcome == ZoneCellEntityCollisionOutcome.Unsupported)).IsTrue();
    }

    [Test]
    public async Task DuplicateGuidAndNonEntityObjectsCannotDisappearFromTheResultTable()
    {
        using var source = Assets.Original();
        var input = Mutate(source, document =>
        {
            var lights = document.Descendants("Entity").Where(row => row.Attribute("EntityClass")?.Value == "Light").ToArray();
            lights[1].SetAttributeValue("EntityGuid", lights[0].Attribute("EntityGuid")!.Value);
            document.Root!.Element("Objects")!.Add(new XElement("Prefab", new XAttribute("File", "unowned.xml")));
        });
        using var classifier = new ZoneManagedCellEntityCollision(source);
        var result = classifier.Classify(input, Cell);
        await Assert.That(result.Rows.Count).IsEqualTo(75);
        await Assert.That(result.RefusedCount).IsEqualTo(3);
        await Assert.That(result.Rows[^1].InputRow).IsNull();
        await Assert.That(result.Rows[^1].SourceChildIndex).IsEqualTo(74);
    }

    [Test]
    [Arguments("sibling")]
    [Arguments("processing-instruction")]
    [Arguments("text")]
    public async Task WholeDocumentDirectivesCannotLeaveRowsFalselyCertified(string mutation)
    {
        using var source = Assets.Original();
        var document = XDocument.Parse(Encoding.UTF8.GetString(source.Files[Cell]));
        if (mutation == "sibling") document.Root!.Add(new XElement("ExternalEntities", new XAttribute("Source", "unowned.xml")));
        if (mutation == "processing-instruction") document.AddFirst(new XProcessingInstruction("external", "source='unowned.xml'"));
        if (mutation == "text") document.Root!.Element("Objects")!.AddFirst(new XText("unowned-data"));
        var input = ZoneCellEntityContent.Read(document.ToString());
        using var classifier = new ZoneManagedCellEntityCollision(source);
        var result = classifier.Classify(input, Cell);
        await Assert.That(result.DocumentDiagnostics.Count).IsGreaterThan(0);
        await Assert.That(result.Rows.Count).IsEqualTo(74);
        await Assert.That(result.NoPhysicalContributionCount).IsEqualTo(0);
    }

    [Test]
    public async Task OriginalByteIdentityIsNotReplacedByAReopenedPathOrCanonicalXml()
    {
        using var source = Assets.Original();
        var original = source.Input();
        source.Files[Cell] = Encoding.UTF8.GetBytes("<Mission><Objects><Entity EntityClass='GeomEntity'/></Objects></Mission>");
        using var classifier = new ZoneManagedCellEntityCollision(source);
        var result = classifier.Classify(original, Cell);
        await Assert.That(result.Document.Sha256).IsEqualTo(Convert.ToHexString(SHA256.HashData(original.RawDocument)));
        await Assert.That(result.NoPhysicalContributionCount).IsEqualTo(74);
        await Assert.That(source.Opened.Contains(Cell)).IsFalse();
        var equivalent = ZoneCellEntityContent.Read(Encoding.UTF8.GetString(original.RawDocument).Replace("\n", "\r\n", StringComparison.Ordinal));
        var different = classifier.Classify(equivalent, Cell);
        await Assert.That(ReferenceEquals(different.Input, original)).IsFalse();
        await Assert.That(different.Document.Sha256 == result.Document.Sha256).IsFalse();
    }

    [Test]
    public async Task ValidNewRowsAndAnActuallyEmptyDocumentAreNotCertifiedByAnOriginalCountOrHashAllowlist()
    {
        using var source = Assets.Original();
        var input = Mutate(source, document =>
        {
            var light = new XElement(First(document, "Light"));
            light.SetAttributeValue("Name", "AnotherAuthoredLight");
            light.SetAttributeValue("EntityGuid", "123456789abcdef0");
            light.SetAttributeValue("Pos", "1,2,3");
            document.Root!.Element("Objects")!.Add(light);
        });
        using var classifier = new ZoneManagedCellEntityCollision(source);
        var result = classifier.Classify(input, "game/worlds/other/cells/001_002/client/entities.xml");
        await Assert.That(result.Rows.Count).IsEqualTo(75);
        await Assert.That(result.NoPhysicalContributionCount).IsEqualTo(75);
        await Assert.That(result.Document.Sha256 == "C5F294240DF725956560E86CA8917EB54F2DD3C56186C2A8EDD22C151C7D9DB9").IsFalse();
        var empty = classifier.Classify(ZoneCellEntityContent.Read("<Mission><Objects/></Mission>"), Cell);
        await Assert.That(empty.Rows.Count).IsEqualTo(0);
        await Assert.That(empty.DocumentDiagnostics.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ClassResourceByteAndDepthBoundsRefuseWithoutParsingAnUnownedPartialDependency()
    {
        foreach (var overDepth in new[] { false, true })
        {
            using var source = Assets.Original();
            source.Files["game/entities/light.ent"] = overDepth
                ? Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("<n>", 130)) + string.Concat(Enumerable.Repeat("</n>", 130)))
                : new byte[ZoneManagedCellEntityCollision.MaximumDependencyBytes + 1];
            using var classifier = new ZoneManagedCellEntityCollision(source);
            var result = classifier.Classify(source.Input(), Cell);
            await Assert.That(result.Rows.Where(row => row.ClassName == "Light").All(row => row.CertifiedPartFlags == 0)).IsTrue();
            await Assert.That(result.RefusedCount).IsEqualTo(13);
            await Assert.That(source.Opened.Contains(LightScript)).IsFalse();
            await Assert.That(result.Rows.First(row => row.ClassName == "Light").Diagnostics.Single().Message.Contains("bound", StringComparison.Ordinal)).IsTrue();
        }
    }

    [Test]
    public async Task FrozenDependenciesAndOwnerRetirementDoNotPretendMutableAssetsStayCurrent()
    {
        using var source = Assets.Original();
        var input = source.Input();
        var classifier = new ZoneManagedCellEntityCollision(source);
        var snapshot = classifier.Classify(input, Cell);
        source.Files[LightScript] = Encoding.UTF8.GetBytes("Light={OnInit=function(self) self:Physicalize(0,{type=1}) end}");
        var frozen = classifier.Classify(input, Cell);
        await Assert.That(frozen.NoPhysicalContributionCount).IsEqualTo(74);
        classifier.Dispose();
        await Assert.That(classifier.IsDisposed).IsTrue();
        await Assert.That(source.Disposed).IsFalse();
        await Assert.That(snapshot.NoPhysicalContributionCount).IsEqualTo(74);
        await Assert.That(() => classifier.Classify(input, Cell)).Throws<ObjectDisposedException>();
        using var replacement = new ZoneManagedCellEntityCollision(source);
        var changed = replacement.Classify(input, Cell);
        await Assert.That(changed.RefusedCount).IsEqualTo(13);
        await Assert.That(ReferenceEquals(changed.Owner, snapshot.Owner)).IsFalse();
    }

    [Test]
    public async Task BorrowedSourceDisposalReentryAndIoFailuresCannotPublishPartialSuccess()
    {
        using var source = Assets.Original();
        var input = source.Input();
        using var disposed = new ZoneManagedCellEntityCollision(source);
        source.Opening = _ => disposed.Dispose();
        await Assert.That(() => disposed.Classify(input, Cell)).Throws<ObjectDisposedException>();
        await Assert.That(source.Closed).IsEqualTo(1);
        source.Opening = null;
        using var classifier = new ZoneManagedCellEntityCollision(source);
        source.Opening = _ => classifier.Classify(input, Cell);
        await Assert.That(() => classifier.Classify(input, Cell)).Throws<InvalidOperationException>();
        source.Opening = null;
        source.ThrowIo = true;
        await Assert.That(() => classifier.Classify(input, Cell)).Throws<IOException>();
        source.ThrowIo = false;
        await Assert.That(classifier.Classify(input, Cell).NoPhysicalContributionCount).IsEqualTo(74);
        await Assert.That(source.Disposed).IsFalse();
    }

    [Test]
    [Arguments("<!DOCTYPE Entity [<!ENTITY external SYSTEM 'file:///never-opened'>]><Entity Name='Light' Script='Scripts/Entities/Lights/Light.lua'>&external;</Entity>")]
    [Arguments("<Entity Name='Light'")]
    public async Task MalformedClassResourcesRemainExplicitFailuresForEveryAffectedRow(string xml)
    {
        using var source = Assets.Original();
        source.Files["game/entities/light.ent"] = Encoding.UTF8.GetBytes(xml);
        using var classifier = new ZoneManagedCellEntityCollision(source);
        var result = classifier.Classify(source.Input(), Cell);
        await Assert.That(result.Rows.Where(row => row.ClassName == "Light").All(row => row.Outcome == ZoneCellEntityCollisionOutcome.InvalidData)).IsTrue();
        await Assert.That(source.Opened.Contains(LightScript)).IsFalse();
    }

    private static XElement First(XDocument document, string type) => document.Root!.Element("Objects")!.Elements("Entity")
        .First(row => row.Attribute("EntityClass")?.Value == type);
    private static ZoneCellEntityContent Mutate(Assets source, Action<XDocument> mutation)
    {
        var document = XDocument.Parse(Encoding.UTF8.GetString(source.Files[Cell]));
        mutation(document);
        return ZoneCellEntityContent.Read(document.ToString());
    }
    private static void AssertNoEngineModules()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var process = Process.GetCurrentProcess();
        var forbidden = process.Modules.Cast<ProcessModule>().Select(module => module.ModuleName.ToLowerInvariant())
            .Where(name => name.StartsWith("cry", StringComparison.Ordinal) && !name.StartsWith("crypt", StringComparison.Ordinal) ||
                name.StartsWith("x2game", StringComparison.Ordinal) || name.StartsWith("tbb", StringComparison.Ordinal) ||
                name is "xlcommon.dll" or "lua51.dll" or "lua5.1.dll").ToArray();
        if (forbidden.Length != 0) throw new InvalidOperationException("Entity classification loaded forbidden modules: " + string.Join(", ", forbidden));
    }
    private sealed class Assets(Dictionary<string, byte[]> files) : IZoneAssetSource, IDisposable
    {
        internal Dictionary<string, byte[]> Files { get; } = files;
        internal List<string> Opened { get; } = [];
        internal Action<string>? Opening;
        internal bool ThrowIo, Disposed;
        internal int Closed;
        internal static Assets Original() => new(ReadOriginalFiles());
        private static Dictionary<string, byte[]> ReadOriginalFiles()
        {
            var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var assembly = typeof(ManagedCellEntityCollisionTests).Assembly;
            var prefix = assembly.GetName().Name + ".Fixtures.CellEntityCollision.";
            using var manifest = assembly.GetManifestResourceStream(prefix + "provenance.json") ?? throw new InvalidOperationException("Original entity fixtures are not embedded.");
            using var json = JsonDocument.Parse(manifest);
            foreach (var resource in json.RootElement.GetProperty("resources").EnumerateArray())
            {
                var path = resource.GetProperty("asset").GetString()!;
                using var stream = assembly.GetManifestResourceStream(prefix + path.Replace('/', '.'))!;
                using var copy = new MemoryStream(); stream.CopyTo(copy);
                var bytes = copy.ToArray();
                if (Convert.ToHexString(SHA256.HashData(bytes)) != resource.GetProperty("sha256").GetString())
                    throw new InvalidOperationException("Original fixture bytes changed: " + path);
                result.Add(path, bytes);
            }
            foreach (var library in new[] { "x2_sounds", "x2_effects", "x2_effects_p" })
            {
                using var stream = assembly.GetManifestResourceStream(assembly.GetName().Name + ".Fixtures.particle-" + library + ".xml")!;
                using var copy = new MemoryStream(); stream.CopyTo(copy);
                result.Add("game/libs/particles/" + library + ".xml", copy.ToArray());
            }
            return result;
        }
        internal ZoneCellEntityContent Input() => ZoneCellEntityContent.Read(Files[Cell]);
        public Stream? OpenRead(string path)
        {
            if (ThrowIo) throw new IOException("Controlled actual dependency read failure.");
            Opening?.Invoke(path); Opened.Add(path);
            return Files.TryGetValue(path, out var bytes) ? new Tracked(this, bytes) : null;
        }
        public void Dispose() => Disposed = true;
        private sealed class Tracked(Assets owner, byte[] bytes) : MemoryStream(bytes, false)
        {
            private bool _closed;
            protected override void Dispose(bool disposing)
            { if (disposing && !_closed) { _closed = true; owner.Closed++; } base.Dispose(disposing); }
        }
    }
}

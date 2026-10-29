using System.Diagnostics;
using System.Text;
using AAEmu.Zone.Content;

namespace AAEmu.Zone.Tests;

public sealed class ZoneParticleCollisionContentTests
{
    [Test]
    public async Task OriginalNineEffectGraphsClassifyAllTwentyEightNodesAndRetainActualLibraryProvenance()
    {
        AssertNoEngineModules();
        using var source = new Assets();
        foreach (var library in new[] { "x2_sounds", "x2_effects", "x2_effects_p" })
        {
            using var stream = typeof(ZoneParticleCollisionContentTests).Assembly.GetManifestResourceStream(
                typeof(ZoneParticleCollisionContentTests).Assembly.GetName().Name + ".Fixtures.particle-" + library + ".xml")!;
            using var output = new MemoryStream(); stream.CopyTo(output);
            source.Files.Add("game/libs/particles/" + library + ".xml", output.ToArray());
        }
        using var content = new ZoneParticleCollisionContent(source);
        (string Name, int Nodes, int Placements)[] original = [
            ("x2_sounds.particles.stubble_poison_fog", 1, 10),
            ("X2_EFFECTS.mon_effects.nmon_poison_fog_launch", 5, 10),
            ("X2_EFFECTS_P.env.elf_light_r", 4, 2),
            ("x2_sounds.particles.vendor_elf_bottle", 1, 1),
            ("X2_EFFECTS.fire_smoke.torchlight_a", 10, 4),
            ("x2_sounds.particles.vender_elf_misc", 1, 1),
            ("x2_sounds.particles.vendor_elf_repair", 1, 2),
            ("X2_EFFECTS_P.env.elf_light_y", 4, 2),
            ("x2_sounds.particles.vendor_elf_food", 1, 1)
        ];
        var nodes = 0;
        foreach (var row in original)
        {
            var certificate = content.Classify(row.Name);
            await Assert.That(certificate.Outcome).IsEqualTo(ZoneParticleCollisionOutcome.NoPhysicalContribution);
            await Assert.That(certificate.Diagnostics.Count).IsEqualTo(0);
            await Assert.That(certificate.Emitters.Count).IsEqualTo(row.Nodes);
            await Assert.That(certificate.Resource.ParticleVersion).IsEqualTo(20);
            await Assert.That(certificate.Resource.Sha256.Length).IsEqualTo(64);
            await Assert.That(certificate.Resource.ByteLength).IsGreaterThan(0);
            foreach (var emitter in certificate.Emitters)
            {
                await Assert.That(emitter.PhysicsType).IsEqualTo(ZoneParticlePhysicsKind.None);
                await Assert.That(emitter.ForceGeneration).IsEqualTo(ZoneParticleForceKind.None);
                await Assert.That(emitter.Geometry).IsNull();
                if (emitter.ParentIndex is { } parent) await Assert.That(parent).IsLessThan(emitter.Index);
            }
            nodes += certificate.Emitters.Count;
        }
        await Assert.That(nodes).IsEqualTo(28);
        await Assert.That(original.Sum(row => row.Placements)).IsEqualTo(33);
        await Assert.That(source.Opened.Count).IsEqualTo(3);
        await Assert.That(source.Closed).IsEqualTo(3);
        AssertNoEngineModules();
    }

    [Test]
    public async Task ColdFirstDotAndExactRegisteredDottedNameDoNotGuessAnotherLibraryOrParent()
    {
        using var source = new Assets();
        source.Add("lib", "<Particles Name='group'><Params PhysicsType='RigidBody'/><Childs>" +
            "<Particles Name='child'><Params PhysicsType='SimplePhysics'/></Particles></Childs></Particles>" +
            "<Particles Name='group.child'><Params/></Particles>");
        source.Files["game/libs/particles/lib.group.xml"] = Encoding.UTF8.GetBytes(Library("lib.group", "<Particles Name='child'><Params PhysicsType='RigidBody'/></Particles>"));
        using var content = new ZoneParticleCollisionContent(source);
        var result = content.Classify("LIB.group.child");
        await Assert.That(result.HasNoPhysicalContribution).IsTrue();
        await Assert.That(result.ResolvedEffectName).IsEqualTo("lib.group.child");
        await Assert.That(result.Emitters.Count).IsEqualTo(1);
        await Assert.That(source.Opened.Single()).IsEqualTo("game/libs/particles/lib.xml");
        await Assert.That(content.Classify("lib.missing.child").Outcome).IsEqualTo(ZoneParticleCollisionOutcome.MissingData);
    }

    [Test]
    public async Task ChildNamesRemainExplicitOwnedDescendantsAndAreNotInventedRegisteredRoots()
    {
        using var source = new Assets();
        source.Add("lib", "<Particles Name='root'><Params/><Childs><Particles Name='author.child'><Params/></Particles></Childs></Particles>");
        using var content = new ZoneParticleCollisionContent(source);
        var root = content.Classify("lib.root");
        await Assert.That(root.HasNoPhysicalContribution).IsTrue();
        await Assert.That(root.Emitters[1].AuthoredName).IsEqualTo("author.child");
        await Assert.That(root.Emitters[1].FullName).IsEqualTo("lib.root.child");
        await Assert.That(root.Emitters[1].ParentIndex).IsEqualTo(0);
        await Assert.That(content.Classify("lib.root.child").HasNoPhysicalContribution).IsFalse();
    }

    [Test]
    public async Task UnrelatedAuthoredPunctuationNamesStayExactIndexKeysWithoutBecomingResourcePaths()
    {
        using var source = new Assets();
        source.Add("lib", "<Particles Name='doodad_effect..nachashgar_light02red'><Params PhysicsType='RigidBody'/></Particles>" +
            "<Particles Name='p_pc_test.ytest_14_!'><Params PhysicsType='SimplePhysics'/></Particles>" +
            "<Particles Name='root'><Params/></Particles>");
        using var content = new ZoneParticleCollisionContent(source);
        await Assert.That(content.Classify("lib.root").HasNoPhysicalContribution).IsTrue();
        await Assert.That(content.Classify("lib.doodad_effect..nachashgar_light02red").Outcome).IsEqualTo(ZoneParticleCollisionOutcome.Unsupported);
        await Assert.That(content.Classify("lib.p_pc_test.ytest_14_!").Outcome).IsEqualTo(ZoneParticleCollisionOutcome.Unsupported);
        await Assert.That(source.Opened.Count).IsEqualTo(1);
    }

    [Test]
    [Arguments("PhysicsType", "SimpleCollision", ZoneParticlePhysicsKind.SimpleCollision)]
    [Arguments("PhysicsType", "SimplePhysics", ZoneParticlePhysicsKind.SimplePhysics)]
    [Arguments("PhysicsType", "RigidBody", ZoneParticlePhysicsKind.RigidBody)]
    [Arguments("ePhysicsType", "RigidBody", ZoneParticlePhysicsKind.RigidBody)]
    [Arguments("ePhysicsType", "None", ZoneParticlePhysicsKind.None)]
    public async Task RealPhysicsValuesAndActualAliasesCannotProduceTheDefaultOnlyCertificate(string parameter, string value, ZoneParticlePhysicsKind expected)
    {
        using var source = new Assets();
        source.Add("lib", $"<Particles Name='root'><Params/><Childs><Particles Name='hidden'><Params Enabled='false' {parameter}='{value}'/></Particles></Childs></Particles>");
        using var content = new ZoneParticleCollisionContent(source);
        var result = content.Classify("lib.root");
        await Assert.That(result.Outcome).IsEqualTo(ZoneParticleCollisionOutcome.Unsupported);
        await Assert.That(result.Emitters[1].PhysicsType).IsEqualTo(expected);
        await Assert.That(result.Emitters[1].Enabled).IsEqualTo(false);
        await Assert.That(result.Diagnostics.Any(diagnostic => diagnostic.SourcePath.Contains("hidden", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    [Arguments("ForceGeneration", "Wind", ZoneParticleForceKind.Wind)]
    [Arguments("eForceGeneration", "Gravity", ZoneParticleForceKind.Gravity)]
    [Arguments("ForceGeneration", "Target", ZoneParticleForceKind.Target)]
    public async Task ForceConsumersAreExplicitEvenWithNoPhysicsBodyKind(string parameter, string value, ZoneParticleForceKind expected)
    {
        using var source = new Assets(); source.Add("lib", $"<Particles Name='root'><Params {parameter}='{value}'/></Particles>");
        using var content = new ZoneParticleCollisionContent(source);
        var result = content.Classify("lib.root");
        await Assert.That(result.Outcome).IsEqualTo(ZoneParticleCollisionOutcome.Unsupported);
        await Assert.That(result.Emitters.Single().PhysicsType).IsEqualTo(ZoneParticlePhysicsKind.None);
        await Assert.That(result.Emitters.Single().ForceGeneration).IsEqualTo(expected);
    }

    [Test]
    [Arguments("PhysicsType='FuturePhysics'")]
    [Arguments("Geometry='objects/solid.cgf'")]
    [Arguments("sGeometry='objects/solid.cgf'")]
    [Arguments("Inheritance='external'")]
    [Arguments("PhysicsType='None' ePhysicsType='RigidBody'")]
    [Arguments("PhysicsType='None' physicstype='RigidBody'")]
    [Arguments("CollideDynamicObjects='true'")]
    public async Task UnknownPhysicalSemanticsAliasesAndInheritanceRemainUnsupported(string parameters)
    {
        using var source = new Assets(); source.Add("lib", $"<Particles Name='root'><Params {parameters}/></Particles>");
        using var content = new ZoneParticleCollisionContent(source);
        var result = content.Classify("lib.root");
        await Assert.That(result.HasNoPhysicalContribution).IsFalse();
        await Assert.That(result.Outcome).IsEqualTo(ZoneParticleCollisionOutcome.Unsupported);
        await Assert.That(result.Diagnostics.Count).IsGreaterThan(0);
    }

    [Test]
    [Arguments("<Particles Name='root'><Params/><Inherit Name='unowned'/></Particles>")]
    [Arguments("<Particles Name='root'><Params/><Childs Source='other'/></Particles>")]
    [Arguments("<Particles Name='root'><Params/><Childs><External Name='other'/></Childs></Particles>")]
    [Arguments("<Particles Name='root'/>")]
    [Arguments("<Particles Name='root'><Params/><Params/></Particles>")]
    [Arguments("<Particles Name='root'><Params/><Childs><Particles Name='a'><Params/></Particles><Particles Name='A'><Params/></Particles></Childs></Particles>")]
    public async Task UnknownOrAmbiguousEmitterOwnershipDoesNotCollapseToAnEmptyClearGraph(string effect)
    {
        using var source = new Assets(); source.Add("lib", effect);
        using var content = new ZoneParticleCollisionContent(source);
        await Assert.That(content.Classify("lib.root").Outcome).IsEqualTo(ZoneParticleCollisionOutcome.Unsupported);
    }

    [Test]
    public async Task MissingResourcesUnsupportedVersionsAndDuplicateRegisteredNamesRemainDistinct()
    {
        using var source = new Assets();
        source.Add("old", "<Particles Name='root'><Params/></Particles>", version: "19");
        source.Add("duplicate", "<Particles Name='root'><Params/></Particles><Particles Name='ROOT'><Params PhysicsType='RigidBody'/></Particles>");
        using var content = new ZoneParticleCollisionContent(source);
        await Assert.That(content.Classify("absent.root").Outcome).IsEqualTo(ZoneParticleCollisionOutcome.MissingData);
        await Assert.That(content.Classify("old.root").Outcome).IsEqualTo(ZoneParticleCollisionOutcome.Unsupported);
        await Assert.That(content.Classify("duplicate.root").Outcome).IsEqualTo(ZoneParticleCollisionOutcome.Unsupported);
        await Assert.That(content.Classify("duplicate.root").Resource.Sha256.Length).IsEqualTo(64);
    }

    [Test]
    [Arguments("<!DOCTYPE ParticleLibrary [<!ENTITY external SYSTEM 'file:///not-opened'>]><ParticleLibrary Name='lib' ParticleVersion='20'>&external;</ParticleLibrary>")]
    [Arguments("<ParticleLibrary Name='lib' ParticleVersion='20'><Particles")]
    [Arguments("<?external target='other'?><ParticleLibrary Name='lib' ParticleVersion='20'><Particles Name='root'><Params/></Particles></ParticleLibrary>")]
    [Arguments("<ParticleLibrary Name='lib' ParticleVersion='20'><Particles Name='root'><Params/><?external target='other'?></Particles></ParticleLibrary>")]
    public async Task MalformedAndExternalXmlCannotYieldAnOwnedPartialLibrary(string xml)
    {
        using var source = new Assets(); source.Files["game/libs/particles/lib.xml"] = Encoding.UTF8.GetBytes(xml);
        using var content = new ZoneParticleCollisionContent(source);
        var result = content.Classify("lib.root");
        await Assert.That(result.Outcome).IsEqualTo(ZoneParticleCollisionOutcome.InvalidData);
        await Assert.That(result.HasNoPhysicalContribution).IsFalse();
        await Assert.That(result.Resource.Sha256.Length).IsEqualTo(64);
        await Assert.That(source.Closed).IsEqualTo(1);
    }

    [Test]
    public async Task ByteAndEmitterDepthWorkBoundsRefuseBeforeClaimingCompleteTraversal()
    {
        using var oversized = new Assets { Oversized = true };
        using (var content = new ZoneParticleCollisionContent(oversized))
            await Assert.That(content.Classify("lib.root").Outcome).IsEqualTo(ZoneParticleCollisionOutcome.InvalidData);
        using var source = new Assets();
        var effect = "<Particles Name='leaf'><Params/></Particles>";
        for (var i = 0; i <= ZoneParticleCollisionContent.MaximumEmitterDepth; i++)
            effect = "<Particles Name='root'><Params/><Childs>" + effect + "</Childs></Particles>";
        source.Add("lib", effect);
        using var nested = new ZoneParticleCollisionContent(source);
        await Assert.That(nested.Classify("lib.root").Outcome).IsEqualTo(ZoneParticleCollisionOutcome.Unsupported);
    }

    [Test]
    public async Task EmitterCountBoundAllowsACompleteGraphAtTheLimitAndRefusesItsNextAuthoredChild()
    {
        var children = string.Concat(Enumerable.Range(1, ZoneParticleCollisionContent.MaximumEmitterCount - 1)
            .Select(index => "<Particles Name='n" + index + "'><Params/></Particles>"));
        using var source = new Assets();
        source.Add("lib", "<Particles Name='root'><Params/><Childs>" + children + "</Childs></Particles>");
        using (var content = new ZoneParticleCollisionContent(source))
        {
            var result = content.Classify("lib.root");
            await Assert.That(result.HasNoPhysicalContribution).IsTrue();
            await Assert.That(result.Emitters.Count).IsEqualTo(ZoneParticleCollisionContent.MaximumEmitterCount);
        }
        source.Add("lib", "<Particles Name='root'><Params/><Childs>" + children +
            "<Particles Name='overflow'><Params PhysicsType='RigidBody'/></Particles></Childs></Particles>");
        using var over = new ZoneParticleCollisionContent(source);
        await Assert.That(over.Classify("lib.root").Outcome).IsEqualTo(ZoneParticleCollisionOutcome.Unsupported);
    }

    [Test]
    public async Task ImmutableSourceSnapshotsAndBorrowedSourceLifetimeStaySeparateFromTheReturnedCertificate()
    {
        using var source = new Assets(); source.Add("lib", "<Particles Name='root'><Params Speed='0'/></Particles>");
        var content = new ZoneParticleCollisionContent(source);
        var certificate = content.Classify("lib.root");
        source.Add("lib", "<Particles Name='root'><Params PhysicsType='RigidBody'/></Particles>");
        await Assert.That(ReferenceEquals(certificate, content.Classify("lib.root"))).IsTrue();
        await Assert.That(source.Opened.Count).IsEqualTo(1);
        content.Dispose();
        await Assert.That(source.Disposed).IsFalse();
        await Assert.That(() => content.Classify("lib.root")).Throws<ObjectDisposedException>();
        await Assert.That(certificate.HasNoPhysicalContribution).IsTrue();
        await Assert.That(certificate.Emitters.Single().AuthoredParameters["Speed"]).IsEqualTo("0");
        await Assert.That(() => ((IList<ZoneParticleCollisionEmitter>)certificate.Emitters).Clear()).Throws<NotSupportedException>();
        using var replacement = new ZoneParticleCollisionContent(source);
        var changed = replacement.Classify("lib.root");
        await Assert.That(changed.HasNoPhysicalContribution).IsFalse();
        await Assert.That(changed.Resource.Sha256 == certificate.Resource.Sha256).IsFalse();
    }

    [Test]
    public async Task InvalidColdNamesNeverEscapeTheLibraryAssetBoundaryAndIoFailuresAreNotSwallowed()
    {
        using var source = new Assets(); using var content = new ZoneParticleCollisionContent(source);
        foreach (var invalid in new[] { "../root", "lib/other.root", "lib..root", "@level.root", "lib.*", "lib:root" })
            await Assert.That(content.Classify(invalid).Outcome).IsEqualTo(ZoneParticleCollisionOutcome.Unsupported);
        await Assert.That(source.Opened.Count).IsEqualTo(0);
        source.ThrowIo = true;
        await Assert.That(() => content.Classify("lib.root")).Throws<IOException>();
        await Assert.That(content.LoadedLibraryCount).IsEqualTo(0);
        source.ThrowIo = false; source.Add("lib", "<Particles Name='root'><Params/></Particles>");
        await Assert.That(content.Classify("lib.root").HasNoPhysicalContribution).IsTrue();
    }

    [Test]
    public async Task DisposalDuringBorrowedSourceReadCannotPublishANewCertificateOrRetainTheStream()
    {
        using var source = new Assets(); source.Add("lib", "<Particles Name='root'><Params/></Particles>");
        using var content = new ZoneParticleCollisionContent(source);
        source.Opening = content.Dispose;
        await Assert.That(() => content.Classify("lib.root")).Throws<ObjectDisposedException>();
        await Assert.That(content.LoadedLibraryCount).IsEqualTo(0);
        await Assert.That(source.Closed).IsEqualTo(1);
        await Assert.That(source.Disposed).IsFalse();
    }

    [Test]
    public async Task ReentrantSourceReadCannotPublishPastTheSharedCertificateLimit()
    {
        using var source = new Assets();
        source.Add("cached", "<Particles Name='root'><Params/></Particles>");
        source.Add("outer", "<Particles Name='root'><Params/></Particles>");
        using var content = new ZoneParticleCollisionContent(source);
        var existing = content.Classify("cached.root");
        source.Opening = () =>
        {
            source.Opening = null;
            for (var index = 1; index < ZoneParticleCollisionContent.MaximumCertificates; index++)
                content.Classify("cached.missing" + index);
        };
        var result = content.Classify("outer.root");
        await Assert.That(result.Outcome).IsEqualTo(ZoneParticleCollisionOutcome.Unsupported);
        await Assert.That(ReferenceEquals(content.Classify("cached.root"), existing)).IsTrue();
        await Assert.That(source.Closed).IsEqualTo(2);
        await Assert.That(content.LoadedLibraryCount).IsEqualTo(2);
    }

    private static string Library(string name, string effects, string version = "20") =>
        "<ParticleLibrary Name='" + name + "' ParticleVersion='" + version + "'>" + effects + "</ParticleLibrary>";
    private static void AssertNoEngineModules()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var process = Process.GetCurrentProcess();
        var forbidden = process.Modules.Cast<ProcessModule>().Select(module => module.ModuleName.ToLowerInvariant())
            .Where(name => (name.StartsWith("cry", StringComparison.Ordinal) && !name.StartsWith("crypt", StringComparison.Ordinal)) || name.StartsWith("x2game", StringComparison.Ordinal) ||
                name.StartsWith("tbb", StringComparison.Ordinal) || name is "xlcommon.dll" or "lua51.dll" or "lua5.1.dll").ToArray();
        if (forbidden.Length != 0) throw new InvalidOperationException("Particle content loaded forbidden modules: " + string.Join(", ", forbidden));
    }
    private sealed class Assets : IZoneAssetSource, IDisposable
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
        public List<string> Opened { get; } = [];
        public int Closed;
        public bool Oversized, ThrowIo, Disposed;
        public Action? Opening;
        public void Add(string library, string effects, string version = "20") =>
            Files["game/libs/particles/" + library + ".xml"] = Encoding.UTF8.GetBytes(Library(library, effects, version));
        public Stream? OpenRead(string path)
        {
            if (ThrowIo) throw new IOException("Controlled actual source read failure.");
            Opening?.Invoke();
            Opened.Add(path);
            if (Oversized) return new OversizedStream();
            return Files.TryGetValue(path, out var bytes) ? new TrackedStream(this, bytes) : null;
        }
        public void Dispose() => Disposed = true;
        private sealed class TrackedStream(Assets owner, byte[] bytes) : MemoryStream(bytes, false)
        { protected override void Dispose(bool disposing) { if (disposing) owner.Closed++; base.Dispose(disposing); } }
        private sealed class OversizedStream : MemoryStream
        { public override long Length => ZoneParticleCollisionContent.MaximumLibraryBytes + 1L; }
    }
}

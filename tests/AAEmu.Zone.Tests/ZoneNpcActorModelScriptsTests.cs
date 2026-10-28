using System.Globalization;
using AAEmu.Zone.Content;
using AAEmu.Zone.Scripting;

namespace AAEmu.Zone.Tests;

public sealed class ZoneNpcActorModelScriptsTests
{
    [Test]
    public async Task GeneratedModelTableUsesPhysicalStancePositionsAndNativeSpeedTriples()
    {
        using var lua = new ZoneManagedLuaRuntime();
        using var scripts = new ZoneNpcActorModelScripts(lua);
        var (model, actor) = Model();
        using var source = scripts.RetainSourceActor(model, actor);
        await Assert.That(source.Get("AnimationGraph")).IsEqualTo("animations/model.xml");
        await Assert.That(source.Get("UpperBodyGraph")).IsEqualTo("animations/upper.xml");
        using var properties = (ZoneLuaReference)source.Get("Properties")!;
        await Assert.That(properties.Get("fileModel")).IsEqualTo("objects/model.chr");
        await Assert.That(properties.Get("clientFileModel")).IsEqualTo("objects/model.chr");
        using var ability = (ZoneLuaReference)source.Get("AIMovementAbility")!;
        using var groups = (ZoneLuaReference)ability.Get("AIMovementSpeeds")!;
        // Independent native argument order: offsets8,0,40,48,56,16,64 of the
        // physical stance pointer vector. These rows intentionally have unrelated IDs.
        foreach (var (name, expectedSlow) in new (string, double)[]
            { ("Relaxed", 11), ("Combat", 1), ("Stealth", 51), ("Crouch", 61), ("Prone", 71), ("Swim", 21), ("Fly", 81) })
        {
            using var group = (ZoneLuaReference)groups.Get(name)!;
            using var slow = (ZoneLuaReference)group.Get("Slow")!;
            await Assert.That(Number(slow, 1)).IsEqualTo(expectedSlow);
        }
        using var relaxed = (ZoneLuaReference)groups.Get("Relaxed")!;
        foreach (var (name, expected) in new (string, double[])[]
            { ("Slow", [11, 8.8, 13.200001]), ("Walk", [12, 9.6, 14.400001]),
              ("Run", [13, 10.400001, 13]), ("Sprint", [14, 11.2, 14]) })
        {
            using var speed = (ZoneLuaReference)relaxed.Get(name)!;
            for (var component = 0; component < 3; component++)
                await Assert.That(Number(speed, component + 1)).IsEqualTo(expected[component]);
        }
    }

    [Test]
    public async Task SinglePrecisionMultiplicationPrecedesSixDecimalLuaSerialization()
    {
        using var lua = new ZoneManagedLuaRuntime();
        using var scripts = new ZoneNpcActorModelScripts(lua);
        var (model, actor) = Model(specialSpeed: .12437312304973602f);
        using var source = scripts.RetainSourceActor(model, actor);
        lua.SetGlobal("observed", source);
        using var result = lua.Execute("return observed.AIMovementAbility.AIMovementSpeeds.Combat.Slow[2]");
        // Native SSE mulss: .09949850291013718 -> %f .099499.
        // Multiplying in double would serialize .099498 and is a different table.
        await Assert.That(Convert.ToDouble(result[0], CultureInfo.InvariantCulture)).IsEqualTo(.099499d);
    }

    [Test]
    public async Task OriginalNpcCreateRunsOnceAndCachedClassReferencesRemainOwned()
    {
        using var lua = new ZoneManagedLuaRuntime();
        using var scripts = new ZoneNpcActorModelScripts(lua);
        using var bootstrap = lua.Execute("""
            createCount=0
            npc={Create=function(self,name,source)
              createCount=createCount+1
              _G[name]={originalSource=source,marker=createCount}
            end}
            """);
        var (model, actor) = Model();
        using var first = scripts.RetainNpcClass(model, actor);
        using var second = scripts.RetainNpcClass(model, actor);
        lua.SetGlobal("first", first); lua.SetGlobal("second", second);
        using var result = lua.Execute("return rawequal(first,second),createCount,rawequal(first.originalSource,elf_male)");
        await Assert.That(result[0] is true).IsTrue();
        await Assert.That(result[1]).IsEqualTo(1L);
        await Assert.That(result[2] is true).IsTrue();
        await Assert.That(ZoneNpcActorModelScripts.NpcClassName(model)).IsEqualTo("npc_elf_male");
        scripts.Dispose();
        await Assert.That(first.Get("marker")).IsEqualTo(1L);
        await Assert.That(lua.IsDisposed).IsFalse();
        await Assert.That(() => scripts.RetainNpcClass(model, actor)).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task IncompleteUnmatchedAndCollidingDatabaseIdentitiesDoNotBecomeGenericActors()
    {
        using var lua = new ZoneManagedLuaRuntime();
        using var scripts = new ZoneNpcActorModelScripts(lua);
        var (model, actor) = Model();
        var shortActor = new ZoneActorModel(actor.Definition, actor.Stances.Take(8));
        await Assert.That(() => scripts.RetainSourceActor(model, shortActor)).Throws<NotSupportedException>();
        await Assert.That(lua.GetGlobal(model.Name)).IsNull();
        await Assert.That(scripts.Count).IsEqualTo(0);
        await Assert.That(() => scripts.RetainSourceActor(model with { SubtypeId = 88 }, actor)).Throws<ArgumentException>();
        using var source = scripts.RetainSourceActor(model, actor);
        await Assert.That(() => scripts.RetainSourceActor(model with { Id = 99 }, actor)).Throws<NotSupportedException>();
        await Assert.That(scripts.Count).IsEqualTo(1);
    }

    [Test]
    public async Task CStringNamesAndNativeFormatterLimitsArePreservedWithoutMeshInference()
    {
        using var lua = new ZoneManagedLuaRuntime();
        using var scripts = new ZoneNpcActorModelScripts(lua);
        var (model, actor) = Model();
        using var source = scripts.RetainSourceActor(model with { Name = "elf_male\0ignored" }, actor);
        await Assert.That(ZoneNpcActorModelScripts.NpcClassName(model with { Name = "elf_male\0ignored" })).IsEqualTo("npc_elf_male");
        await Assert.That(lua.GetGlobal("ignored")).IsNull();
        await Assert.That(() => scripts.RetainSourceActor(model with { Name = "different-model" }, actor)).Throws<NotSupportedException>();
        await Assert.That(() => ZoneNpcActorModelScripts.NpcClassName(model with { Name = new string('x', 124) })).Throws<NotSupportedException>();
        var oversized = new ZoneActorModel(actor.Definition with { ModelFile = new string('x', 3000) }, actor.Stances);
        await Assert.That(() => scripts.RetainSourceActor(model with { Name = "oversized" }, oversized)).Throws<NotSupportedException>();
        await Assert.That(lua.GetGlobal("oversized")).IsNull();
    }

    private static double Number(ZoneLuaReference table, int index) =>
        Convert.ToDouble(table.Get(index), CultureInfo.InvariantCulture);

    private static (ZoneModelDefinition Model, ZoneActorModel Actor) Model(float? specialSpeed = null)
    {
        var actor = new ZoneActorModel(new()
        {
            Id = 7, AnimationGraph = "animations/model.xml", UpperbodyGraph = "animations/upper.xml", ModelFile = "objects/model.chr",
        }, Enumerable.Range(0, 9).Select(index => new ZoneGameStanceDefinition
        {
            Id = (uint)(index % 2), ActorModelId = 7, StanceId = 20 - index,
            AiMoveSpeedSlow = index == 0 && specialSpeed is { } speed ? speed : 10 * index + 1,
            AiMoveSpeedWalk = 10 * index + 2, AiMoveSpeedRun = 10 * index + 3, AiMoveSpeedSprint = 10 * index + 4,
        }));
        return (new() { Id = 27, Name = "elf_male", Subtype = "ActorModel", SubtypeId = 7 }, actor);
    }
}

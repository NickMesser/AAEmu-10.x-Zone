using AAEmu.Zone.Content;
using AAEmu.Zone.Npcs;
using AAEmu.Zone.Protocol;
using AAEmu.Zone.Protocol.Units;
using AAEmu.Zone.Scene;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace AAEmu.Zone.Tests;

public sealed class ManagedNpcPostureConstructionTests
{
    [Test]
    public async Task OwnedCacheMatchesOriginalDedicatedDriverAndActionLeafControls()
    {
        using var stream = typeof(ManagedNpcPostureConstructionTests).Assembly.GetManifestResourceStream(
            "AAEmu.Zone.Tests.Fixtures.npc-posture-native-controls.json")!;
        using var evidence = JsonDocument.Parse(stream);
        var root = evidence.RootElement;
        var actions = new ZoneAnimationActionContent(root.GetProperty("actionRows").EnumerateArray().Select(
            row => new ZoneAnimationActionDefinition(row.GetProperty("id").GetUInt32(), row.GetProperty("actionState").GetInt32())));
        using var fixture = new Fixture();
        using var state = new ZoneManagedNpcPostureState(Content(), actions, () => 1, _ => { });
        var owner = state.Bind(fixture.Actor.Actor, new(fixture.Model, false, [], new(0, false)));
        try
        {
            foreach (var control in root.GetProperty("controls").EnumerateArray())
            {
                var operation = control.GetProperty("operation").GetString();
                if (operation == "set-cache" && !owner.AnimationDriverPresent)
                {
                    owner.Dispose();
                    owner = state.Bind(fixture.Actor.Actor, new(fixture.Model, true, [], new(0, false)));
                }
                if (operation != "reset") state.ApplyFromWorld(fixture.Actor.Actor,
                    new(control.GetProperty("action").GetUInt32(), control.GetProperty("activate").GetBoolean()), false);
                await Assert.That(owner.CurrentAnimationId).IsEqualTo(control.GetProperty("current").GetUInt32());
                await Assert.That(owner.Actions.SequenceEqual(control.GetProperty("cache").EnumerateArray().Select(x => x.GetUInt32()))).IsTrue();
            }
            await Assert.That(root.GetProperty("controls").GetArrayLength()).IsEqualTo(25);
        }
        finally { owner.Dispose(); }
    }

    [Test]
    public async Task ActualAnimationActionRowsDriveTheOwnedCacheWithoutInventingGraphEffects()
    {
        using var connection = new SqliteConnection("Data Source=:memory:"); connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE anim_actions(id INTEGER PRIMARY KEY,action_state_id INTEGER);INSERT INTO anim_actions VALUES(0,0),(50,2),(77,3),(88,1)";
        command.ExecuteNonQuery();
        var actions = ZoneAnimationActionContent.Load(connection);
        await Assert.That(actions.Actions.Count).IsEqualTo(4);
        await Assert.That(actions.Actions[0].ActionState).IsEqualTo(0);
        await Assert.That(actions.Actions[77].ActionState).IsEqualTo(3);
        using var fixture = new Fixture();
        using var posture = new ZoneManagedNpcPostureState(Content(), actions, () => 1, _ => { });
        using var owner = posture.Bind(fixture.Actor.Actor, new(fixture.Model, true, [], new(50, true)));
        await Assert.That(owner.CurrentAnimationId).IsEqualTo(50u);
        posture.ApplyFromWorld(fixture.Actor.Actor, new(77, true), false);
        await Assert.That(owner.CurrentAnimationId).IsEqualTo(77u);
        posture.ApplyFromWorld(fixture.Actor.Actor, new(77, false), false);
        await Assert.That(owner.CurrentAnimationId).IsEqualTo(50u);
        await Assert.That(() => posture.ApplyFromWorld(fixture.Actor.Actor, new(88, true), false)).Throws<NotSupportedException>();
        await Assert.That(owner.CurrentAnimationId).IsEqualTo(50u);
    }

    [Test]
    public async Task DedicatedConstructionProvesAbsentDriverAndExecutesDefaultPostureNotifications()
    {
        using var fixture = new Fixture(cachedTemplateModel: new() { Id = 99, Subtype = "PrefabModel" });
        var frames = new List<ZoneFrame>();
        //An actually absent dedicated graph skips descriptor lookup entirely, even for missing actions.
        using var posture = new ZoneManagedNpcPostureState(Content(), new ZoneAnimationActionContent([]), () => 1, frames.Add);
        using var owner = posture.BindDedicatedConstruction(fixture.Actor.Actor, fixture.Construction, fixture.Models, ZoneNpcSkillContent.Empty);
        await Assert.That(owner.DriverOrigin).IsEqualTo(ZoneNpcPostureDriverOrigin.FreshDedicatedActorModel);
        await Assert.That(owner.AnimationDriverPresent).IsFalse();
        await Assert.That(owner.Kind).IsEqualTo(ZoneModelKind.Actor);
        await Assert.That(fixture.Actor.Actor.Npc.Model.Id).IsEqualTo(99u);
        await Assert.That(fixture.Actor.Actor.Npc.Unit.Snapshot.ModelId).IsEqualTo(27u);
        var pose = fixture.Actor.Actor.Npc.Unit.Pose;
        posture.SetDefaultPosture(fixture.Actor.Actor, false);
        await Assert.That(owner.CurrentAnimationId).IsEqualTo(2u);
        await Assert.That(owner.Posture).IsEqualTo(new ActorPosture(50, false));
        await Assert.That(frames.Count).IsEqualTo(1);
        await Assert.That(ReadPosture(frames[0])).IsEqualTo((fixture.Actor.Actor.Npc.ObjectId, false, 50u, false));
        await Assert.That(fixture.Actor.Actor.Npc.Unit.Pose).IsEqualTo(pose);
        posture.SetDefaultPosture(fixture.Actor.Actor, false);
        await Assert.That(frames.Count).IsEqualTo(2); //Original disabled repeats still notify World.
    }

    [Test]
    public async Task ActualNpcChildConditionInventorySuppressesPostureWithoutInferringFromPickedSkills()
    {
        using var fixture = new Fixture(); var frames = new List<ZoneFrame>();
        var skills = new ZoneNpcSkillContent([new(11, 999999, 1, 0, 0)], []);
        using var posture = new ZoneManagedNpcPostureState(Content(), new ZoneAnimationActionContent([]), () => 1, frames.Add);
        using var owner = posture.BindDedicatedConstruction(fixture.Actor.Actor, fixture.Construction, fixture.Models, skills);
        posture.SetDefaultPosture(fixture.Actor.Actor, false);
        await Assert.That(frames.Count).IsEqualTo(0);
        await Assert.That(owner.Posture.AnimationAction).IsEqualTo(0u);
        await Assert.That(owner.CurrentAnimationId).IsEqualTo(2u);
    }

    [Test]
    public async Task ConstructionRetirementRevokesThePostureModelAndCrossGenerationBindingRefuses()
    {
        using var fixture = new Fixture(); using var other = new Fixture();
        using var posture = new ZoneManagedNpcPostureState(Content(), new ZoneAnimationActionContent([]), () => 1, _ => { });
        await Assert.That(() => posture.BindDedicatedConstruction(fixture.Actor.Actor, other.Construction, fixture.Models, ZoneNpcSkillContent.Empty))
            .Throws<InvalidOperationException>();
        using var owner = posture.BindDedicatedConstruction(fixture.Actor.Actor, fixture.Construction, fixture.Models, ZoneNpcSkillContent.Empty);
        fixture.Construction.Dispose();
        await Assert.That(owner.IsCurrent).IsFalse();
        await Assert.That(posture.Find(fixture.Actor.Actor)).IsNull();
        await Assert.That(() => owner.CurrentAnimationId).Throws<InvalidOperationException>();
        await Assert.That(() => posture.SetDefaultPosture(fixture.Actor.Actor, false)).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task PostureLeaseDiscardsOnlyItsOwnUnsentWorldNotificationScope()
    {
        using var fixture = new Fixture(); var units = fixture.Base.Admission.Registry;
        var directSends = 0;
        using var posture = new ZoneManagedNpcPostureState(Content(), new ZoneAnimationActionContent([]), () => 1, _ => directSends++, units);
        var owner = posture.BindDedicatedConstruction(fixture.Actor.Actor, fixture.Construction, fixture.Models, ZoneNpcSkillContent.Empty);
        var unrelated = new ZoneFrame(0x7FFE, new byte[] { 1 });
        units.QueueNotification(unrelated, fixture.Actor.Actor.Npc.Unit, new object());
        posture.SetDefaultPosture(fixture.Actor.Actor, false);
        await Assert.That(directSends).IsEqualTo(0);
        owner.Dispose();
        var frames = new List<ZoneFrame>();
        while (units.TryDequeueNotification(out var frame)) frames.Add(frame);
        await Assert.That(frames.Count).IsEqualTo(1);
        await Assert.That(frames[0].Opcode).IsEqualTo(unrelated.Opcode);
    }

    private static ZoneNpcPostureContent Content() => new([new(11, 50, 0, 0, "")], [new(0, 0)]);
    private static (uint ObjectId, bool Looted, uint Animation, bool Activate) ReadPosture(ZoneFrame frame)
    {
        var reader = new ZoneReader(frame.Body.Span); var id = reader.ReadBc();
        if (reader.ReadByte() != 4) throw new InvalidDataException("Wrong actor posture union discriminator.");
        var result = (id, reader.ReadBoolean(), reader.ReadUInt32(), reader.ReadBoolean()); reader.EnsureFullyConsumed(); return result;
    }
    private sealed class Fixture : IDisposable
    {
        public ManagedNpcScriptEffectsTests.Fixture Base { get; } = new();
        public ManagedNpcScriptEffectsTests.Admitted Actor { get; }
        public ZoneModelDefinition Model { get; } = new() { Id = 27, Name = "actual_posture_actor", Subtype = "ActorModel", SubtypeId = 7 };
        public ZoneModelContent Models { get; }
        private readonly ZoneManagedNpcActorConstruction _construction;
        public ZoneManagedNpcActorConstructionLease Construction { get; }
        public Fixture(ZoneModelDefinition? cachedTemplateModel = null)
        {
            Models = new([Model], actorModels: new ZoneActorModelContent([new() { Id = 7, ModelFile = "objects/actual_character.cdf" }],
                Enumerable.Range(1, 2).Select(id => new ZoneGameStanceDefinition
                { Id = (uint)id, ActorModelId = 7, StanceId = id, SizeX = .3f, SizeY = .3f, SizeZ = .3f, HeightCollider = 1.2f, UseCapsule = true })));
            Actor = Base.Admit(model: cachedTemplateModel ?? Model, unitModelId: Model.Id);
            _construction = new(Base.Admission.Registry, Base.Admission.Catalog, Models);
            Construction = _construction.Create(Actor.Actor.Handle, Actor.Actor.Npc);
        }
        public void Dispose() { _construction.Dispose(); Actor.Dispose(); Base.Dispose(); }
    }
}

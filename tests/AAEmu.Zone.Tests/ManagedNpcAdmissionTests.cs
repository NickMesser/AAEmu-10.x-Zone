using System.Numerics;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Ai;
using AAEmu.Zone.Content;
using AAEmu.Zone.Hosting;
using AAEmu.Zone.Npcs;
using AAEmu.Zone.Protocol;
using AAEmu.Zone.Protocol.Messages;
using AAEmu.Zone.Protocol.Units;
using AAEmu.Zone.Scene;
using AAEmu.Zone.Scripting;
using AAEmu.Zone.Units;
using AAEmu.Zone.WorldLink;

namespace AAEmu.Zone.Tests;

public sealed class ManagedNpcAdmissionTests
{
    [Test]
    public async Task CatalogAdmitsOneExactOwnerAndRejectsRetiredStateReuse()
    {
        using var fixture = new Fixture();
        var first = fixture.Npc();
        var actor = fixture.Catalog.Register(first);
        await Assert.That(fixture.Catalog.Register(first)).IsEqualTo(actor);
        await Assert.That(ReferenceEquals(fixture.Catalog.Find(actor), first)).IsTrue();
        var competing = fixture.Npc();
        await Assert.That(() => fixture.Catalog.Register(competing)).Throws<InvalidOperationException>();
        await Assert.That(fixture.Catalog.Retire(first)).IsTrue();
        var retired = fixture.Catalog.GetAdmission(actor)!;
        await Assert.That(retired.Phase).IsEqualTo(ZoneSceneNpcAdmissionPhase.Retired);
        await Assert.That(fixture.Catalog.Find(actor)).IsNull();
        await Assert.That(() => fixture.Catalog.GetKind(actor)).Throws<InvalidOperationException>();
        await Assert.That(() => fixture.Catalog.Register(first)).Throws<InvalidOperationException>();

        var next = fixture.Catalog.Register(competing);
        await Assert.That(next.Generation).IsEqualTo(actor.Generation + 1);
        await Assert.That(fixture.Catalog.TryGet(first)).IsNull();
        await Assert.That(fixture.Catalog.Retire(actor)).IsFalse();
        await Assert.That(fixture.Catalog.Retire(first)).IsFalse();
        await Assert.That(ReferenceEquals(fixture.Catalog.Find(next), competing)).IsTrue();
        await Assert.That(retired.Phase).IsEqualTo(ZoneSceneNpcAdmissionPhase.Retired);
    }

    [Test]
    public async Task ScheduledLifecycleDoesNotClaimAuthoredBehaviorOrOpenGameplayGate()
    {
        using var fixture = new Fixture();
        var npc = fixture.Npc();
        fixture.World.Initialize(npc, null);
        var actor = fixture.Catalog.TryGet(npc)!.Value;
        var status = fixture.Catalog.GetAdmission(actor)!;
        await Assert.That(status.NpcId).IsEqualTo(npc.Id);
        await Assert.That(status.TemplateId).IsEqualTo(npc.TemplateId);
        await Assert.That(status.Phase).IsEqualTo(ZoneSceneNpcAdmissionPhase.Scheduled);
        await Assert.That(fixture.Provider.GetState(actor).UpdatePeriod).IsEqualTo((TimeSpan?)TimeSpan.FromMilliseconds(20));
        await Assert.That(fixture.World.HasAiActor(npc)).IsFalse();
        await Assert.That(fixture.Provider.Count).IsEqualTo(1);

        fixture.World.Remove(npc);
        await Assert.That(fixture.Provider.Count).IsEqualTo(0);
        await Assert.That(fixture.Catalog.GetAdmission(actor)!.Phase).IsEqualTo(ZoneSceneNpcAdmissionPhase.Retired);
        await Assert.That(status.Phase).IsEqualTo(ZoneSceneNpcAdmissionPhase.Scheduled);
    }

    [Test]
    public async Task OldRemovalCannotRetireTheReplacementProviderOrActivationOwner()
    {
        using var fixture = new Fixture();
        var first = fixture.Npc();
        fixture.World.Initialize(first, null);
        var old = fixture.Catalog.TryGet(first)!.Value;
        fixture.World.Remove(first);
        var replacement = fixture.Npc();
        fixture.World.Initialize(replacement, null);
        var current = fixture.Catalog.TryGet(replacement)!.Value;
        var activation = fixture.World.GetActivationServices(replacement);

        fixture.World.Remove(first);
        await Assert.That(current.Generation).IsGreaterThan(old.Generation);
        await Assert.That(fixture.Provider.Count).IsEqualTo(1);
        await Assert.That(ReferenceEquals(fixture.Catalog.Find(current), replacement)).IsTrue();
        await Assert.That(ReferenceEquals(fixture.World.GetActivationServices(replacement), activation)).IsTrue();
        await Assert.That(fixture.Provider.GetState(current).Active).IsTrue();
        await Assert.That(() => fixture.Provider.GetState(old)).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task RefusedConstructionPublishesARefusalWithoutAProviderBindingAndCanRetireCleanly()
    {
        using var fixture = new Fixture(_ => throw new NotSupportedException("Authored constructor is unavailable."));
        var npc = fixture.Npc();
        fixture.World.Initialize(npc, null);
        var actor = fixture.Catalog.TryGet(npc)!.Value;
        var status = fixture.Catalog.GetAdmission(actor)!;
        await Assert.That(status.Phase).IsEqualTo(ZoneSceneNpcAdmissionPhase.Refused);
        await Assert.That(status.Refusal).IsEqualTo("Authored constructor is unavailable.");
        await Assert.That(fixture.Provider.Count).IsEqualTo(0);
        await Assert.That(fixture.World.AiBindRefusalCount).IsEqualTo(1);
        await Assert.That(fixture.World.HasAiActor(npc)).IsFalse();
        fixture.World.Remove(npc);
        fixture.World.Remove(npc);
        await Assert.That(fixture.Catalog.Find(actor)).IsNull();
        await Assert.That(fixture.Catalog.GetAdmission(actor)!.Phase).IsEqualTo(ZoneSceneNpcAdmissionPhase.Retired);
    }

    [Test]
    public async Task ThrowingBehaviorCleanupStillSealsTheExactManagedGeneration()
    {
        using var fixture = new Fixture(_ => new ZoneManagedAiActor(retire: () => throw new InvalidOperationException("retire control")));
        var npc = fixture.Npc();
        fixture.World.Initialize(npc, null);
        var actor = fixture.Catalog.TryGet(npc)!.Value;
        await Assert.That(() => fixture.World.Remove(npc)).Throws<AggregateException>();
        await Assert.That(fixture.Provider.Count).IsEqualTo(0);
        await Assert.That(fixture.Catalog.TryGet(npc)).IsNull();
        await Assert.That(fixture.Catalog.GetAdmission(actor)!.Phase).IsEqualTo(ZoneSceneNpcAdmissionPhase.Retired);
        fixture.World.Remove(npc);
    }

    [Test]
    public async Task StableRuntimeFacadeDrainsOnlyExactSignalReadyOwnersWithoutOpeningGameplay()
    {
        using var fixture = new Fixture();
        var facade = fixture.World.AiRuntime;
        var runtime = new RecordingRuntime();
        fixture.World.AttachAiRuntime(runtime);
        await Assert.That(ReferenceEquals(facade, fixture.World.AiRuntime)).IsTrue();
        var npc = fixture.Npc();
        runtime.Owner = npc;
        fixture.Publish(npc);
        fixture.World.Initialize(npc, null);
        await Assert.That(fixture.World.HasSignalActor(npc)).IsTrue();
        await Assert.That(fixture.World.HasAiActor(npc)).IsFalse();

        npc.AiSignals.Send("OnAITimeEvent");
        await Assert.That(npc.AiSignals.Pending.Count).IsEqualTo(1);
        npc.AiSignals.Update();
        await Assert.That(runtime.Constructors).IsEqualTo(1);
        await Assert.That(runtime.Signals.Single().Name).IsEqualTo("OnAITimeEvent");
        await Assert.That(runtime.Signals.Single().SenderEntityId).IsEqualTo(runtime.EntityId);
        await Assert.That(facade.FrameTimeSeconds).IsEqualTo(.02f);
        fixture.World.Remove(npc);
        await Assert.That(fixture.World.HasSignalActor(npc)).IsFalse();
        npc.AiSignals.Send("OnAITimeEvent");
        await Assert.That(npc.AiSignals.Pending.Count).IsEqualTo(0);
    }

    [Test]
    public async Task RuntimeAttachmentIsOnceOnlyAndCannotChangeAnAdmittedScene()
    {
        using (var fixture = new Fixture())
        {
            await Assert.That(() => fixture.World.AttachAiRuntime(fixture.World.AiRuntime)).Throws<InvalidOperationException>();
            fixture.World.AttachAiRuntime(new RecordingRuntime());
            await Assert.That(() => fixture.World.AttachAiRuntime(new RecordingRuntime())).Throws<InvalidOperationException>();
        }
        using var admitted = new Fixture();
        admitted.World.Initialize(admitted.Npc(), null);
        await Assert.That(() => admitted.World.AttachAiRuntime(new RecordingRuntime())).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task SignalReadyNpcTimerUsesOriginalPhaseIntervalAndDeathGateBeforeRuntimeDispatch()
    {
        uint now = 1000;
        var model = new ZoneModelDefinition { Id = 27, Name = "elf_male", Subtype = "ActorModel", SubtypeId = 7 };
        var content = new ZoneNpcContent([new() { Id = 11, ModelId = model.Id }], [], [], [], [], models: new([model]));
        var stats = new ZoneStatContent([], [], [new(11, 0, 0, 0, 0)], [], [], [], [], [], []);
        using var fixture = new Fixture(content: content, stats: stats, now: () => now);
        var runtime = new RecordingRuntime(); fixture.World.AttachAiRuntime(runtime);
        var npc = fixture.CreateFromWire(model.Id);
        runtime.Owner = npc;
        await Assert.That(fixture.World.HasAiActor(npc)).IsFalse();
        await Assert.That(fixture.World.HasSignalActor(npc)).IsTrue();
        npc.Timers.ActivateAiTimeEvent(true, 100);

        now = 1099; fixture.Simulation.Update(0);
        await Assert.That(npc.AiSignals.Pending.Count).IsEqualTo(0);
        now = 1100; fixture.Simulation.Update(1); // Correct interval, wrong native phase.
        await Assert.That(npc.AiSignals.Pending.Count).IsEqualTo(0);
        fixture.Simulation.Update(4);
        await Assert.That(npc.Timers.LastAiTimeEvent).IsEqualTo(1100u);
        await Assert.That(npc.AiSignals.Pending.Single().Name).IsEqualTo("OnAITimeEvent");
        fixture.Simulation.UpdateAiSignals();
        await Assert.That(runtime.Signals.Single().Name).IsEqualTo("OnAITimeEvent");
        await Assert.That(runtime.Signals.Single().SenderEntityId).IsEqualTo(runtime.EntityId);
        await Assert.That(npc.AiSignals.Pending.Count).IsEqualTo(0);

        npc.AggroEnabled = false;
        now = 1300; fixture.Simulation.Update(8); fixture.Simulation.UpdateAiSignals();
        await Assert.That(runtime.Signals.Count).IsEqualTo(2); // AI timer is independent of aggro.
        npc.Unit.Die(null);
        now = 1500; fixture.Simulation.Update(12); fixture.Simulation.UpdateAiSignals();
        await Assert.That(runtime.Signals.Count).IsEqualTo(2);
        await Assert.That(npc.Timers.LastAiTimeEvent).IsEqualTo(1300u);
        fixture.World.Remove(npc);
        now = 1700; fixture.Simulation.Update(16); fixture.Simulation.UpdateAiSignals();
        await Assert.That(runtime.Signals.Count).IsEqualTo(2);
    }

    private sealed class RecordingRuntime : IZoneNpcAiRuntime
    {
        public ZoneNpc? Owner { get; set; }
        public uint EntityId => Owner!.ObjectId + 2_000_000;
        public int Constructors { get; private set; }
        public List<ZoneAiSignal> Signals { get; } = [];
        public bool IsEnabled(ZoneNpc npc) => ReferenceEquals(npc, Owner);
        public bool CanReceiveSignals(ZoneNpc npc) => IsEnabled(npc);
        public bool HasSignalHandler(ZoneNpc npc) => IsEnabled(npc);
        public bool HasPipeUser(ZoneNpc npc) => IsEnabled(npc);
        public uint? GetEntityId(ZoneUnit unit) => ReferenceEquals(unit, Owner?.Unit) ? EntityId : null;
        public uint GetUnitObjectId(uint entityId) => entityId == EntityId ? Owner!.ObjectId : 0;
        public ZoneLuaReference? GetEntityScript(uint entityId) => null;
        public Vector3? GetEntityPosition(uint entityId) => null;
        public void SignalReceived(ZoneNpc npc, string name) { }
        public float FrameTimeSeconds => .02f;
        public bool StartAuxiliaryBehavior(ZoneNpc npc, string name, int kind) => false;
        public bool IsAuxiliaryBehaviorFinished(ZoneNpc npc) => true;
        public void CaptureTalkPosition(ZoneNpc npc) { }
        public void RunPendingConstructors(ZoneNpc npc) => Constructors++;
        public void DispatchScriptSignal(ZoneNpc npc, ZoneAiSignal signal) => Signals.Add(signal);
        public void ReleaseScriptState(ZoneNpc npc) { }
    }

    internal sealed class Fixture : IDisposable
    {
        private static int _nextObjectId = 200_000;
        internal static uint FreshObjectId() => (uint)Interlocked.Increment(ref _nextObjectId);
        private readonly uint _objectId = FreshObjectId();
        private readonly ZoneUnitRegistry _registry;
        private readonly ZoneStartupState _startup;
        private readonly ZoneNpcContent _content;
        private readonly ZoneNpcSimulation _simulation;
        private readonly List<ZoneNpc> _owners = [];
        public ZoneSceneNpcCatalog Catalog { get; } = new();
        public ZoneUnitRegistry Registry => _registry;
        public ZoneNpcSimulation Simulation => _simulation;
        public ZoneManagedAiProvider Provider { get; }
        public ZoneSceneNpcWorld World { get; }

        public Fixture(Func<ZoneActorHandle, IZoneManagedAiActor>? create = null, ZoneNpcContent? content = null,
            ZoneStatContent? stats = null, Func<uint>? now = null, ZoneUnitSpatialIndex? spatialIndex = null,
            Func<ZoneUnit, Vector3>? modelPosition = null, ZoneSpatialRegionBounds? sceneBounds = null,
            ZoneEngineCVars? engineCVars = null)
        {
            _registry = sceneBounds is null ? new(spatialIndex: spatialIndex, modelPosition: modelPosition) :
                new(statContent: stats ?? throw new ArgumentNullException(nameof(stats)));
            _startup = new(localGameMilliseconds: now ?? (() => 1000));
            _startup.EngineCVars = engineCVars ?? ZoneEngineCVars.Empty;
            _content = content ?? new([], [], [], [], []);
            Provider = new(Catalog.GetKind, create ?? (_ => new ZoneManagedAiActor()));
            World = new(_registry, _startup, _content, modelPosition ?? (_ => Vector3.Zero), () => null,
                Catalog, Provider, new(), TimeSpan.FromMilliseconds(20), new(), null);
            if (sceneBounds is { } bounds)
            {
                var notes = new ZoneSceneReadinessNotes();
                _registry.InitializeSceneServices(new(World, new ZoneSceneUnitUpdateServices(_registry, _startup, notes),
                    new ZoneSceneMovementServices(_startup, notes), modelPosition ?? throw new ArgumentNullException(nameof(modelPosition))), _content);
                _registry.InitializeSceneSpatialIndex(bounds);
                _simulation = _registry.Npcs!;
            }
            else _simulation = new(_registry, stats ?? new([], [], [], [], [], [], [], [], []), World, _content);
        }

        public ZoneNpc CreateFromWire(uint modelId, Vector3? position = null)
        {
            var writer = new ZoneWriter();
            writer.WriteBc(_objectId); writer.WriteString("timer-control");
            writer.WriteByte(0); writer.WriteByte(0); writer.WriteBoolean(false);
            new UnitIdentity(ZoneUnitKind.Npc, NpcObjectId: _objectId + 1_000_000, TemplateId: 11).Write(writer);
            writer.WriteString("");
            if (position is { } local)
            {
                var packed = UnitMovementQuantization.Position(ZoneMovementPoseMath.ToUnitWorldPosition(local));
                writer.WriteUInt32(packed.QuantizedX); writer.WriteUInt32(packed.QuantizedY);
                writer.WriteUInt16((ushort)packed.QuantizedZ);
                writer.WriteByte((byte)((packed.QuantizedZ >> 16) | (packed.NegativeX ? 0x80u : 0) | (packed.NegativeY ? 0x40u : 0)));
            }
            else { writer.WriteUInt32(0); writer.WriteUInt32(0); writer.WriteUInt16(0); writer.WriteByte(0); }
            writer.WriteSingle(1); writer.WriteSByte(1); writer.WriteSByte(0); writer.WriteSByte(0); writer.WriteSByte(0);
            writer.WriteBytes(new byte[4]); writer.WriteUInt32(modelId);
            writer.WriteUInt64(0); writer.WriteByte(0); // No equipment or appearance extension.
            writer.WriteBc(0); writer.WriteInt64(100); writer.WriteInt64(100);
            writer.WriteSByte(-1); writer.WriteSByte(-1); // No attachment/bonding.
            writer.WriteByte(0); writer.WriteBoolean(false); writer.WriteSByte(0);
            writer.WriteByte(0); writer.WriteByte(0); // No skills/passives.
            writer.WriteUInt32(0); writer.WriteInt32(0); writer.WriteUInt32(0); writer.WriteBoolean(false);
            writer.WriteSByte(0); writer.WriteSByte(0); writer.WriteSByte(0); writer.WriteByte(0);
            writer.WritePisc(0, 0, 0, 0); writer.WritePisc(0, 0, 0); writer.WritePisc(0, 0, 0, 0);
            writer.WriteUInt16(0); writer.WriteByte(0);
            writer.WriteByte(0); writer.WriteByte(0); writer.WriteByte(0); // Empty buff lists, no action tail.
            var metadata = new NpcStateMetadata(0, 0, 0, 0, new(ZoneUnitKind.Character), false, false, 0, new(), 0, new());
            var frame = NpcStateSnapshot.CreateFrame(metadata, writer.ToArray());
            if (_simulation.Create(frame.Body.Span) != NpcCreationResult.Created)
                throw new InvalidOperationException("The complete NPC wire fixture was not admitted.");
            var npc = _simulation.Npcs[_objectId + 1_000_000];
            _owners.Add(npc);
            return npc;
        }

        public ZoneNpc Npc(uint objectId = 0, uint npcId = 0, ZoneModelDefinition? model = null, uint? unitModelId = null,
            UnitHeading? heading = null, ZoneSkillContent? skillContent = null, bool cleanSkillState = false)
        {
            if (objectId == 0) objectId = _objectId;
            if (npcId == 0) npcId = _objectId + 1_000_000;
            var source = UnitStateSnapshot.Read(Convert.FromHexString(UnitStateSnapshotTests.PlayerEnterBody));
            var snapshot = new UnitStateSnapshot
            {
                ObjectId = objectId, Name = "admission-control", Identity = new(ZoneUnitKind.Npc, NpcObjectId: npcId, TemplateId: 11),
                WorldId = source.WorldId, RegionId = source.RegionId, GlobalWorld = source.GlobalWorld,
                MasterName = source.MasterName, Position = source.Position, Scale = source.Scale, Level = source.Level,
                HeirLevel = source.HeirLevel, SecondaryLevel = source.SecondaryLevel, SecondaryHeirLevel = source.SecondaryHeirLevel,
                SlotSelectors = source.SlotSelectors, ModelId = unitModelId ?? model?.Id ?? source.ModelId, Equipment = source.Equipment, Appearance = source.Appearance,
                TargetId = source.TargetId, PreciseHealth = source.PreciseHealth, PreciseMana = source.PreciseMana,
                Attachment = source.Attachment, Bonding = source.Bonding, Posture = source.Posture, ActiveWeapon = source.ActiveWeapon,
                StateType = source.StateType, AppellationStamp = source.AppellationStamp, VehicleDyeing = source.VehicleDyeing,
                TemporaryFaction = source.TemporaryFaction, Skills = source.Skills, PassiveBuffs = cleanSkillState ? [] : source.PassiveBuffs,
                Heading = heading ?? source.Heading, RaceGender = source.RaceGender, PackedState = source.PackedState, Flags = cleanSkillState ? (ushort)(source.Flags & ~2) : source.Flags,
                AttackFactionFlags = source.AttackFactionFlags, FirstHitter = source.FirstHitter,
                HighAbilities = cleanSkillState ? [] : source.HighAbilities, GmModes = source.GmModes, Character = null,
                Buffs = cleanSkillState ? new([], [], []) : source.Buffs, Action = source.Action,
            };
            var unit = new ZoneUnit(snapshot, null, skillContent: skillContent);
            var spawn = new NpcStateMetadata(0, 0, 0, 0, new(ZoneUnitKind.Character), false, false, 0,
                new(0), 0, new(0, 0, 0));
            var npc = new ZoneNpc(unit, spawn, 0, _simulation, new() { Id = 11 }, model ?? new() { Id = source.ModelId }, 0,
                World, _ => null, _ => { }, ZoneNpcSignalContent.Empty, _simulation.Diagnostics);
            _owners.Add(npc);
            return npc;
        }

        public void Publish(ZoneNpc npc) => _registry.AddPrepared(npc.Unit);

        public void Dispose()
        {
            foreach (var npc in _owners)
            {
                World.Remove(npc);
                npc.Activation.Dispose(); npc.AiSignals.Dispose(); npc.Signals.Dispose(); npc.Talks.Dispose();
            }
            Provider.Dispose();
            _startup.CloseClocks();
        }
    }
}

using System.Numerics;
using AAEmu.Zone.Ai;
using AAEmu.Zone.Combat;
using AAEmu.Zone.Content;
using AAEmu.Zone.Physics;
using AAEmu.Zone.Protocol;
using AAEmu.Zone.Protocol.Messages;
using AAEmu.Zone.Scene;
using AAEmu.Zone.Scripting;
using AAEmu.Zone.Terrain;

namespace AAEmu.Zone.Tests;

public sealed class ManagedNpcSkillPatternTests
{
    [Test]
    public async Task RealUnitRequestWaitsForWorldAndAcknowledgmentsDriveItsFieldsAndCooldown()
    {
        using var fixture = new Fixture(); fixture.SetList("{100}");
        using var goal = fixture.Owner.CreateUseSkillGoal(fixture.Actor, 4);
        await Assert.That(goal.Execute()).IsEqualTo(1); //request authored skill info
        await Assert.That(fixture.Actor.Npc.AiSignals.Pending.Single().Name).IsEqualTo("OnRequestSkillInfo");
        await Assert.That(goal.Execute()).IsEqualTo(1); //pick
        await Assert.That(goal.Execute()).IsEqualTo(1); //request to World
        var request = ReadRequest(fixture.Frames.Single());
        await Assert.That(request).IsEqualTo((fixture.Actor.Npc.ObjectId, 100u, fixture.Actor.Npc.Unit.TargetId, false));
        await Assert.That(fixture.Actor.Npc.Unit.Casting.CastTimeline).IsEqualTo(0u);
        await Assert.That(fixture.Actor.Npc.Unit.Cooldowns.GetRemaining(100)).IsEqualTo(0u);
        await Assert.That(goal.Execute()).IsEqualTo(1); //a sent request is not success
        fixture.Start(100, 17, 200);
        await Assert.That(fixture.Actor.Npc.Unit.Casting.CastTimeline).IsEqualTo(17u);
        await Assert.That(fixture.Owner.CopyState(fixture.Actor).AcknowledgementState).IsEqualTo(1);
        fixture.Fire(100, 17);
        await Assert.That(fixture.Actor.Npc.Unit.Casting.CastTimeline).IsEqualTo(0u);
        await Assert.That(fixture.Actor.Npc.Unit.Cooldowns.GetRemaining(100, false)).IsEqualTo(8000u);
        await Assert.That(goal.Execute()).IsEqualTo(2); //nonchannel Fire does not require End
        fixture.SetList("{100}");
        await Assert.That(fixture.Owner.Pick(fixture.Actor, 0).Failures.Single().Code).IsEqualTo((byte)8);
        fixture.Actor.Npc.Unit.Cooldowns.Advance(8000);
        await Assert.That(fixture.Owner.Pick(fixture.Actor, 0).Selected!.Value.SkillId).IsEqualTo(100u);
    }

    [Test]
    public async Task AckFailureAndUnrelatedTimelinesDoNotSynthesizeSuccessOrStartCooldowns()
    {
        using var fixture = new Fixture(); fixture.SetList("{100}"); using var goal = fixture.Request();
        fixture.Start(101, 3, 100);
        await Assert.That(fixture.Owner.CopyState(fixture.Actor).AcknowledgementState).IsEqualTo(0);
        fixture.Start(100, 4, 100);
        fixture.Fire(100, 5);
        await Assert.That(fixture.Owner.CopyState(fixture.Actor).AcknowledgementState).IsEqualTo(1);
        fixture.End(5);
        await Assert.That(fixture.Owner.CopyState(fixture.Actor).AcknowledgementState).IsEqualTo(1);
        fixture.Fire(100, 4, new(15, 0, 0, true));
        await Assert.That(fixture.Owner.CopyState(fixture.Actor).Result!.Value.Code).IsEqualTo((byte)15);
        await Assert.That(fixture.Actor.Npc.Unit.Cooldowns.GetRemaining(100)).IsEqualTo(0u);
        await Assert.That(goal.Execute()).IsEqualTo(3);
    }

    [Test]
    public async Task OrderedCallbacksAreNotLostWhenWorldStartsFiresAndEndsBeforeTheNextAiFrame()
    {
        using var fixture = new Fixture(); fixture.SetList("{100}"); using var goal = fixture.Request();
        fixture.Start(100, 11, 100); fixture.Fire(100, 11); fixture.End(11);
        await Assert.That(fixture.Owner.CopyState(fixture.Actor).AcknowledgementState).IsEqualTo(3);
        await Assert.That(fixture.Owner.CopyState(fixture.Actor).Timeline).IsEqualTo(11u);
        await Assert.That(goal.Execute()).IsEqualTo(2);
        await Assert.That(fixture.Owner.CopyState(fixture.Actor).ListReceived).IsFalse();
    }

    [Test]
    public async Task LuaListsPreserveDuplicateCandidatesAndKeyedDelayIsConsumedAfterActualAck()
    {
        using var fixture = new Fixture(); fixture.Sample = uint.MaxValue;
        fixture.SetList("{101,100,100,[100]={.25,false},ignored='invalid'}");
        var snapshot = fixture.Owner.CopyState(fixture.Actor);
        await Assert.That(snapshot.Skills.Count).IsEqualTo(5); //native string value contributes unresolved skill0
        await Assert.That(snapshot.Skills.Contains(0u)).IsTrue();
        var selected = fixture.Owner.Pick(fixture.Actor, 0).Selected!.Value;
        await Assert.That(selected.SkillId).IsEqualTo(100u);
        await Assert.That(selected.DelaySeconds).IsEqualTo(.25f);
        await Assert.That(selected.Strafe).IsFalse();
        using var goal = fixture.Request(); fixture.Start(100, 7, 0); fixture.Fire(100, 7);
        await Assert.That(goal.Execute()).IsEqualTo(1);
        fixture.Base.Now = 249999;
        await Assert.That(goal.Execute()).IsEqualTo(1);
        fixture.Base.Now = 250000;
        await Assert.That(goal.Execute()).IsEqualTo(2);
    }

    [Test]
    public async Task AuthoredChildRolesAndSignedPatternStateRemainSeparateFromRequestedList()
    {
        using var fixture = new Fixture(rows:
            [new(11, 100, 1, 123, 456), new(11, 101, 0, 0, 0), new(11, 999, 1, 0, 0), new(11, 102, 2, 0, 0)]);
        fixture.Owner.InstallAuthoredSkillLists(fixture.Actor);
        using var values = fixture.Base.Lua.Execute("return #entity.AI.skills.idle,entity.AI.skills.idle[1],entity.AI.skills.idle[2],#entity.AI.skills.combat,#entity.AI.skills.alert");
        await Assert.That(values[0]).IsEqualTo(2L);
        await Assert.That(values[1]).IsEqualTo(100L);
        await Assert.That(values[2]).IsEqualTo(999L);
        await Assert.That(values[3]).IsEqualTo(1L);
        await Assert.That(values[4]).IsEqualTo(0L);
        await Assert.That(fixture.Owner.CopyState(fixture.Actor).ListReceived).IsFalse();
        fixture.Owner.SetPattern(fixture.Actor, -17);
        await Assert.That(fixture.Owner.CopyState(fixture.Actor).Pattern).IsEqualTo(-17);
        using var idle = fixture.Base.Lua.Execute("return entity.AI.skills.idle");
        fixture.Owner.SetSkillList(fixture.Actor, (ZoneLuaReference)idle[0]!);
        await Assert.That(fixture.Owner.Pick(fixture.Actor, 0).Selected!.Value.SkillId).IsEqualTo(100u); //unresolved999 skipped
    }

    [Test]
    public async Task SkillInfoAndStartTimeoutsUseOriginalBoundariesAndResetDoesNotRetainOldList()
    {
        using var fixture = new Fixture(); using var info = fixture.Owner.CreateUseSkillGoal(fixture.Actor, 0, "idle");
        await Assert.That(info.Execute()).IsEqualTo(1);
        await Assert.That(fixture.Actor.Npc.AiSignals.Pending.Single().Name).IsEqualTo("OnRequestSkillInfo_idle");
        fixture.Base.Now = 1999999; await Assert.That(info.Execute()).IsEqualTo(1);
        fixture.Base.Now = 2000000; await Assert.That(info.Execute()).IsEqualTo(3);
        fixture.SetList("{100}"); using var request = fixture.Request();
        fixture.Base.Now = 3999999; await Assert.That(request.Execute()).IsEqualTo(1);
        fixture.Base.Now = 4000000; await Assert.That(request.Execute()).IsEqualTo(3);
        await Assert.That(fixture.Owner.CopyState(fixture.Actor).Skills.Count).IsEqualTo(0);
        await Assert.That(fixture.Frames.Count).IsEqualTo(1);
    }

    [Test]
    public async Task UnownedTargetAndCostProfilesRefuseBeforeARequestAndNoBaseSkillIsInvented()
    {
        using var fixture = new Fixture(extra: [Definition(102) with { TargetType = 6 }, Definition(103) with { ManaCost = 1 }]);
        fixture.SetList("{102}");
        await Assert.That(() => fixture.Owner.Pick(fixture.Actor, 0)).Throws<NotSupportedException>();
        fixture.SetList("{103}");
        await Assert.That(() => fixture.Owner.Pick(fixture.Actor, 0)).Throws<NotSupportedException>();
        fixture.SetList("{}");
        await Assert.That(() => fixture.Owner.Pick(fixture.Actor, 1)).Throws<NotSupportedException>();
        await Assert.That(fixture.Frames.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ActualAimGeometryAdmitsAnInRangeNpcTargetAndWorldTargetOwnsTheWire()
    {
        using var fixture = new Fixture(extra: [Definition(102) with { TargetType = 4, TargetUnitMask = 119, MaximumRange = 25 }]);
        using var target = fixture.AdmitTarget(new(110, 100, 100));
        fixture.Actor.SetTarget(target.Actor.Handle);
        fixture.Actor.Npc.Unit.SetTarget(target.Actor.Npc.ObjectId);
        fixture.SetList("{102}"); using var goal = fixture.Request();
        await Assert.That(fixture.Owner.CopyState(fixture.Actor).Picked!.Value.BaseSkill).IsFalse();
        await Assert.That(ReadRequest(fixture.Frames.Single()).Target).IsEqualTo(target.Actor.Npc.ObjectId);
        fixture.Start(102, 31, 0); fixture.Fire(102, 31);
        await Assert.That(goal.Execute()).IsEqualTo(2);
    }

    [Test]
    public async Task FlytrapActuallySuppressesTheOriginalClosestOutOfRangeSelection()
    {
        using var fixture = new Fixture(extra: [Definition(102) with { TargetType = 4, TargetUnitMask = 119, MaximumRange = 5 },
            Definition(103) with { TargetType = 4, TargetUnitMask = 119, MaximumRange = 7 }]);
        using var target = fixture.AdmitTarget(new(110, 100, 100));
        fixture.Actor.SetTarget(target.Actor.Handle); fixture.SetList("{102,103}");
        fixture.Owner.SetPattern(fixture.Actor, 0);
        var normal = fixture.Owner.Pick(fixture.Actor, 4);
        await Assert.That(normal.Selected!.Value.SkillId).IsEqualTo(103u);
        await Assert.That(normal.Selected.Value.BaseSkill).IsTrue(); //native movement-selection flag, not a substituted base attack
        fixture.Owner.SetPattern(fixture.Actor, 3);
        var flytrap = fixture.Owner.Pick(fixture.Actor, 4);
        await Assert.That(flytrap.Selected).IsNull();
        await Assert.That(flytrap.Failures.All(failure => failure.Code == 15)).IsTrue();
        await Assert.That(fixture.Frames.Count).IsEqualTo(0);
    }

    [Test]
    public async Task FinalUnitTargetRevalidationDoesNotCastAtAnUnownedOrChangedWorldTarget()
    {
        using var fixture = new Fixture(extra: [Definition(102) with { TargetType = 4, TargetUnitMask = 119, MaximumRange = 25 }]);
        using var target = fixture.AdmitTarget(new(110, 100, 100));
        fixture.Actor.SetTarget(target.Actor.Handle); fixture.Actor.Npc.Unit.SetTarget(target.Actor.Npc.ObjectId);
        fixture.SetList("{102}"); using var goal = fixture.Owner.CreateUseSkillGoal(fixture.Actor, 0);
        await Assert.That(goal.Execute()).IsEqualTo(1); await Assert.That(goal.Execute()).IsEqualTo(1);
        fixture.Actor.Npc.Unit.SetTarget(0); //World target changed after selection; AI attention alone cannot replace it
        await Assert.That(goal.Execute()).IsEqualTo(3);
        await Assert.That(fixture.Frames.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ExactGenerationRetirementInvalidatesSelectionRequestsAndBorrowedBody()
    {
        using var fixture = new Fixture(); fixture.SetList("{100}"); using var goal = fixture.Request();
        fixture.Base.Scene.Remove(fixture.Actor.Handle);
        await Assert.That(() => fixture.Owner.SetPattern(fixture.Actor, 3)).Throws<InvalidOperationException>();
        await Assert.That(() => goal.Execute()).Throws<InvalidOperationException>();
        fixture.Start(100, 55, 100); //World still owns its Unit; retired actor receives no ACK
        await Assert.That(fixture.Lease.IsCurrent).IsFalse();
        await Assert.That(fixture.Body.IsCurrent).IsFalse(); //the body owner performs its own exact-generation retirement
        await Assert.That(fixture.Body.IsDisposed).IsTrue();
        await Assert.That(fixture.Owner.DrainFaults().Count).IsEqualTo(0);
    }

    [Test]
    public async Task UnsupportedAckEffectsAreQueuedAsGenerationFaultsWithoutInterruptingWorldFields()
    {
        using var fixture = new Fixture(); fixture.SetList("{100}"); using var goal = fixture.Request();
        //An actual source-state transition after the request has no modifier solver in this profile.
        fixture.Actor.Npc.Unit.CommitAttributeOverrides(new([new(74, 1)]), null);
        fixture.Start(100, 33, 100);
        await Assert.That(fixture.Actor.Npc.Unit.Casting.CastTimeline).IsEqualTo(33u);
        var fault = fixture.Owner.DrainFaults().Single();
        await Assert.That(fault.Actor).IsEqualTo(fixture.Actor.Handle);
        await Assert.That(fault.Message.Contains("attribute overrides", StringComparison.Ordinal)).IsTrue();
        await Assert.That(() => goal.Execute()).Throws<NotSupportedException>();
    }

    [Test]
    public async Task AcknowledgedGcdUsesTheActualContentAttackSpeedAndDefaultPrecedesCustom()
    {
        var speed = new StatModifier(1, "Npc", 11, 74, 0, 100, 0, 0);
        using (var fixture = new Fixture(extra: [Definition(102) with { CustomGlobalCooldownMilliseconds = 7000 }], modifiers: [speed]))
        {
            fixture.SetList("{102}"); using var goal = fixture.Request(); fixture.Start(102, 7, 0);
            await Assert.That(fixture.Actor.Npc.Unit.Cooldowns.Global.RemainingMilliseconds).IsEqualTo(6363u);
            fixture.Fire(102, 7);
            await Assert.That(fixture.Actor.Npc.Unit.Cooldowns.Global.RemainingMilliseconds).IsEqualTo(6363u);
            await Assert.That(fixture.Actor.Npc.Unit.Cooldowns.GetRemaining(102, false)).IsEqualTo(8000u);
        }
        using (var fixture = new Fixture(extra: [Definition(102) with { DefaultGlobalCooldown = true, CustomGlobalCooldownMilliseconds = 7000 }], modifiers: [speed]))
        {
            fixture.SetList("{102}"); using var goal = fixture.Request(); fixture.Start(102, 8, 0);
            await Assert.That(fixture.Actor.Npc.Unit.Cooldowns.Global.RemainingMilliseconds).IsEqualTo(909u);
        }
    }

    [Test]
    public async Task RetirementDropsOnlyTheExactLeasesUnsentSkillRequests()
    {
        using var fixture = new Fixture(queued: true);
        fixture.Base.Admission.Registry.QueueNotification(new(0xEEEE, new byte[] { 5 }), fixture.Actor.Npc.Unit, new object());
        fixture.SetList("{100}"); using var goal = fixture.Request();
        await Assert.That(fixture.Frames.Count).IsEqualTo(0); //the production-style sender queued the real frame
        fixture.Lease.Dispose();
        await Assert.That(fixture.Base.Admission.Registry.TryDequeueNotification(out var retained)).IsTrue();
        await Assert.That(retained.Opcode).IsEqualTo((ushort)0xEEEE);
        await Assert.That(fixture.Base.Admission.Registry.TryDequeueNotification(out _)).IsFalse();
    }

    [Test]
    public async Task GlobalCooldownWaitsBeforeSkillInfoAndAgainBeforeANonIgnoringRequest()
    {
        using var fixture = new Fixture(extra: [Definition(102) with { IgnoreGlobalCooldown = false }]);
        fixture.SetList("{102}"); using var goal = fixture.Owner.CreateUseSkillGoal(fixture.Actor, 0);
        fixture.Actor.Npc.Unit.Cooldowns.StartGlobal(2000);
        await Assert.That(goal.Execute()).IsEqualTo(1);
        await Assert.That(fixture.Actor.Npc.AiSignals.Pending.Count).IsEqualTo(0);
        fixture.Actor.Npc.Unit.Cooldowns.Advance(2000);
        await Assert.That(goal.Execute()).IsEqualTo(1); await Assert.That(goal.Execute()).IsEqualTo(1);
        fixture.Actor.Npc.Unit.Cooldowns.StartGlobal(50);
        await Assert.That(goal.Execute()).IsEqualTo(1);
        await Assert.That(fixture.Frames.Count).IsEqualTo(0);
        fixture.Actor.Npc.Unit.Cooldowns.Advance(50);
        await Assert.That(goal.Execute()).IsEqualTo(1);
        await Assert.That(fixture.Frames.Single().Opcode).IsEqualTo(ZwOpcodes.StartSkill);
    }

    [Test]
    public async Task PriorCastingIsStoppedOnceInOriginalOrderThenWaitsForWorldToClearTheFields()
    {
        using var fixture = new Fixture(); fixture.SetList("{100}");
        var casting = fixture.Actor.Npc.Unit.Casting; casting.Start(42, 100, 1, 10); casting.StartPlot(99, 1, 0);
        using var goal = fixture.Owner.CreateUseSkillGoal(fixture.Actor, 0);
        await Assert.That(goal.Execute()).IsEqualTo(1); await Assert.That(goal.Execute()).IsEqualTo(1);
        await Assert.That(ReadStop(fixture.Frames[0])).IsEqualTo((0u, 99u));
        await Assert.That(ReadStop(fixture.Frames[1])).IsEqualTo((42u, 0u));
        await Assert.That(goal.Execute()).IsEqualTo(1);
        await Assert.That(fixture.Frames.Count).IsEqualTo(2);
        await Assert.That(casting.CastTimeline).IsEqualTo(42u);
        await Assert.That(casting.CurrentSkill).IsEqualTo(99u);
        fixture.Base.Admission.Registry.ApplyCastingStopped(new(fixture.Actor.Npc.ObjectId, 42, 100, 0));
        await Assert.That(goal.Execute()).IsEqualTo(1); await Assert.That(goal.Execute()).IsEqualTo(1);
        await Assert.That(fixture.Frames.Last().Opcode).IsEqualTo(ZwOpcodes.StartSkill);
    }

    [Test]
    public async Task NonchannelFireWaitsForActualCastIdentifiersAndIgnoresTheIndependentAutoattackByte()
    {
        using var fixture = new Fixture(); fixture.SetList("{100}"); using var goal = fixture.Request();
        fixture.Start(100, 4, 100); fixture.Fire(100, 4);
        var casting = fixture.Actor.Npc.Unit.Casting; casting.StartPlot(777, 1, 0);
        await Assert.That(goal.Execute()).IsEqualTo(1);
        casting.ClearCurrentPlot(777); casting.Start(0, 2, 1, 0);
        await Assert.That(casting.Casting).IsTrue();
        await Assert.That(goal.Execute()).IsEqualTo(2);
    }

    [Test]
    public async Task ATargetLeavingTheOriginalCastingRangeRequestsStopWithoutClearingWorldState()
    {
        using var fixture = new Fixture(extra: [Definition(102) with
            { TargetType = 4, TargetUnitMask = 119, MaximumRange = 25, CastingMilliseconds = 1000 }]);
        using var target = fixture.AdmitTarget(new(110, 100, 100));
        fixture.Actor.SetTarget(target.Actor.Handle); fixture.Actor.Npc.Unit.SetTarget(target.Actor.Npc.ObjectId);
        fixture.SetList("{102}"); using var goal = fixture.Request(); fixture.Start(102, 17, 1000);
        fixture.Bodies.Backend.Teleport(target.Actor.Handle, new(140, 100, 100), Quaternion.Identity);
        await Assert.That(goal.Execute()).IsEqualTo(3);
        await Assert.That(ReadStop(fixture.Frames.Last())).IsEqualTo((17u, 0u));
        await Assert.That(fixture.Actor.Npc.Unit.Casting.CastTimeline).IsEqualTo(17u);
    }

    private static (uint Timeline, uint Skill) ReadStop(ZoneFrame frame)
    {
        if (frame.Opcode != ZwOpcodes.StopCasting) throw new InvalidDataException("Wrong stop request.");
        var reader = new ZoneReader(frame.Body.Span); reader.ReadBc();
        var result = (reader.ReadUInt32(), reader.ReadUInt32()); reader.EnsureFullyConsumed(); return result;
    }

    private static ZoneNpcSkillDefinition Definition(uint id) => new(id)
    { TargetType = 0, MaximumRange = 100, UseConditionBits = 1, TargetAlive = true, IgnoreGlobalCooldown = true };

    private static (uint Caster, uint Skill, uint Target, bool Almighty) ReadRequest(ZoneFrame frame)
    {
        if (frame.Opcode != ZwOpcodes.StartSkill) throw new InvalidDataException("Wrong skill request opcode.");
        var reader = new ZoneReader(frame.Body.Span); var caster = reader.ReadBc(); var skill = reader.ReadUInt32();
        if (reader.ReadByte() != 0) throw new InvalidDataException("Wrong target union.");
        var result = (caster, skill, reader.ReadBc(), reader.ReadBoolean()); reader.EnsureFullyConsumed(); return result;
    }

    private sealed class Fixture : IDisposable
    {
        public ManagedNpcScriptEffectsTests.Fixture Base { get; } = new();
        public ManagedNpcScriptEffectsTests.Admitted Admitted { get; }
        public ZoneManagedNpcActor Actor => Admitted.Actor;
        public ZoneManagedNpcPhysics Bodies { get; }
        public ZoneModelContent Models { get; }
        public ZoneSkillContent Skills { get; }
        public ZoneManagedNpcBodyLease Body { get; }
        public ZoneManagedNpcSkillPatterns Owner { get; }
        public ZoneManagedNpcSkillPatternLease Lease { get; }
        public List<ZoneFrame> Frames { get; } = [];
        public uint Sample;
        public Fixture(IEnumerable<ZoneNpcSkillRow>? rows = null, IEnumerable<ZoneNpcSkillDefinition>? extra = null,
            IEnumerable<StatModifier>? modifiers = null, bool queued = false)
        {
            var definitions = new[] { Definition(100), Definition(101) }.Concat(extra ?? []).ToArray();
            Skills = new ZoneSkillContent(definitions.Select(row => new SkillDefinition(row.Id, 0) { TimingId = 1, CooldownMilliseconds = 8000 }), []);
            var stats = new ZoneStatContent([], [], [new(11, 0, 0, 0, 0)], [], [], [], [], modifiers ?? [], [], skills: Skills);
            Models = new ZoneModelContent([new() { Id = 17, Name = "skill_source", Subtype = "ActorModel", SubtypeId = 8 }],
                actorModels: new([new() { Id = 8, Height = 1, Radius = .3f, PhysicsLivingMass = 80, PhysicsLivingGravity = 25,
                    PhysicsLivingMinSlideAngle = 45, PhysicsLivingMaxClimbAngle = 45, PhysicsLivingMinFallAngle = 60 }],
                [new() { Id = 1, ActorModelId = 8, StanceId = 2, SizeX = .3f, SizeY = .3f, SizeZ = .3f,
                    HeightCollider = 1.2f, UseCapsule = true }]));
            Admitted = Base.Admit(model: Models.Models[17], skillContent: Skills, cleanSkillState: true);
            if (!Base.Host.Initialize(Admitted.Binding).SignalReady) throw new InvalidOperationException("Script fixture failed to initialize.");
            Bodies = new(new ZoneManagedPhysicsBackend(new(new ZoneTerrainHeightField())), Models, _ => 0);
            Body = Bodies.Create(Actor.Handle, Actor.Npc, new(100, 100, 100), Quaternion.Identity, () => Actor.HasActor);
            ZoneManagedNpcSkillPatterns? notificationOwner = null;
            Owner = notificationOwner = new(Base.Lua, Base.Admission.Registry, Base.Admission.Catalog,
                new(rows ?? [], definitions), stats, Frames.Add, () => Sample, () => Base.Now,
                new(Base.Admission.Registry, Models, unit =>
                {
                    var handle = Base.Admission.Catalog.TryGet(unit.Snapshot.ObjectId) ?? throw new InvalidOperationException("Target retired.");
                    var body = Bodies.Find(handle) ?? throw new InvalidOperationException("Target body missing.");
                    body.CopyStatus(out var status);
                    return new(unit.Snapshot.ModelId, status.Position, status.Orientation, body.Admission.Request.EntityScale);
                }), scopedSend: queued ? (actor, frame) =>
                    Base.Admission.Registry.QueueNotification(frame, actor.Npc.Unit, notificationOwner!.GetRequestScope(actor)) : null);
            Lease = Owner.BindEntity(Admitted.Binding, Body);
        }
        public void SetList(string text)
        { using var value = Base.Lua.Execute("return " + text); Owner.SetSkillList(Actor, (ZoneLuaReference)value[0]!); }
        public ManagedNpcScriptEffectsTests.Admitted AdmitTarget(Vector3 position)
        {
            var admitted = Base.Admit(model: Models.Models[17], skillContent: Skills, cleanSkillState: true);
            _ = Bodies.Create(admitted.Actor.Handle, admitted.Actor.Npc, position, Quaternion.Identity, () => admitted.Actor.HasActor);
            return admitted;
        }
        public IZoneGoalOperation Request()
        {
            var goal = Owner.CreateUseSkillGoal(Actor, 0);
            if (goal.Execute() != 1 || goal.Execute() != 1 || goal.Execute() != 1) throw new InvalidOperationException("Request did not reach World.");
            return goal;
        }
        private SkillCasterRef Caster => new(0, Actor.Npc.ObjectId, 0, 0, 0, 0);
        public void Start(uint skill, ushort timeline, uint castTime, SkillCastResult? result = null) =>
            Base.Admission.Registry.ApplySkillStarted(new(skill, timeline, Caster, new(0, Actor.Npc.ObjectId), castTime) { Result = result ?? SkillCastResult.Success });
        public void Fire(uint skill, ushort timeline, SkillCastResult? result = null) =>
            Base.Admission.Registry.ApplySkillFired(new(skill, timeline, Caster, new(0, Actor.Npc.ObjectId), false) { Result = result ?? SkillCastResult.Success });
        public void End(ushort timeline) => Base.Admission.Registry.ApplySkillEnded(new(timeline, Caster));
        public void Dispose() { Owner.Dispose(); Lease.Dispose(); Body.Dispose(); Bodies.Dispose(); Admitted.Dispose(); Base.Dispose(); }
    }
}

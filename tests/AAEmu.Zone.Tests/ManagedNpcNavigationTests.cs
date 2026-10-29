using System.Numerics;
using AAEmu.Zone.Ai;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Combat;
using AAEmu.Zone.Content;
using AAEmu.Zone.Hosting;
using AAEmu.Zone.Navigation;
using AAEmu.Zone.Scene;
using AAEmu.Zone.Physics;
using AAEmu.Zone.Terrain;

namespace AAEmu.Zone.Tests;

public sealed class ManagedNpcNavigationTests
{
    [Test]
    public async Task ApproachPipeDrivesThePredictiveProxyAndSingleLivingStepToItsCompletionSignal()
    {
        using var fixture = new Fixture();
        fixture.EnablePuppetOutput();
        using (var source = fixture.Runtime.Runtime.Lua.Execute("""
            AIBehaviour.spawning.OnCompletedReturn=function(self,e)
              e.completed=true;e:SelectPipe(0,'_first_')
            end
            AI.BeginGoalPipe('controlled_return')
              AI.PushGoal('locate',1,'refpoint')
              AI.PushGoal('run',1,1)
              AI.PushGoal('approach',1,2,AILASTOPRES_USE,-1)
              AI.PushGoal('signal',1,AISIGNAL_INCLUDE_DISABLED,'OnCompletedReturn',SIGNALFILTER_SENDER)
            AI.EndGoalPipe()
            observedEntity:SelectPipe(AIGOALPIPE_DONT_RESET_AG,'controlled_return')
            """)) { }
        var start = fixture.Actor.Npc.Unit.Pose;
        var steps = 0;
        for (; steps < 300 && !fixture.Completed(); steps++) fixture.Step();
        await Assert.That(fixture.Completed()).IsTrue();
        await Assert.That(steps).IsGreaterThan(2);
        var body = fixture.Runtime.Bodies!.Find(fixture.Actor.Handle)!.CopyState();
        await Assert.That(body.Status.Position.Y).IsGreaterThan(1102f);
        await Assert.That(Vector3.Distance(body.Status.Position, fixture.Target.Position)).IsLessThan(2.5f);
        await Assert.That(fixture.Actor.Npc.Unit.Pose == start).IsFalse();
        await Assert.That(fixture.Runtime.PositionChecks!.CorrectionCount).IsEqualTo(0ul);
        await Assert.That(fixture.Lease.ExistingPredictiveFollower is not null).IsTrue();
        await Assert.That(fixture.Lease.MovementPredictions.Count).IsEqualTo(0);
        await Assert.That(fixture.Runtime.MovementRequests!.Find(fixture.Actor.Handle)!.PreparedFrameCount).IsGreaterThan(2ul);
        await Assert.That(fixture.Runtime.Runtime.Scripts.RecentErrors.Count).IsEqualTo(0);
        await Assert.That(fixture.Runtime.Motion!.CopyAiPhysicsState(fixture.Actor).Gravity).IsEqualTo(new Vector3(0, 0, -25));
    }

    [Test]
    public async Task AuthoredRoutePublishesBothPathsAndSelectsTheActualPersistentPredictiveFollower()
    {
        using var fixture = new Fixture();
        var before = fixture.Runtime.Bodies!.Find(fixture.Actor.Handle)!.CopyState();
        fixture.Request();
        await Assert.That(fixture.Lease.PathDecision).IsEqualTo(ZoneApproachPathDecision.Found);
        await Assert.That(fixture.Actor.Navigation.CurrentPath.HasPath).IsTrue();
        await Assert.That(fixture.Actor.Navigation.OriginalPath.HasPath).IsTrue();
        await Assert.That(fixture.Actor.Navigation.CurrentPath.Points.SequenceEqual(fixture.Actor.Navigation.OriginalPath.Points)).IsTrue();
        await Assert.That(fixture.Lease.CurrentPathTarget).IsEqualTo(fixture.Target);
        await Assert.That(fixture.Runtime.Bodies.Find(fixture.Actor.Handle)!.CopyState()).IsEqualTo(before);
        var follower = fixture.Lease.PredictiveFollower;
        using (var goal = fixture.Lease.CreateTrace(new(-1, false, false)))
            await Assert.That(goal is ZonePredictiveTraceGoal).IsTrue();
        await Assert.That(follower.IsDisposed).IsFalse();
        await Assert.That(ReferenceEquals(follower.Path, fixture.Actor.Navigation.CurrentPath)).IsTrue();
        await Assert.That(fixture.Lease.UpdatePriority).IsEqualTo(3);
        await Assert.That(fixture.Lease.ReadPredictiveFrame().GeneratePredictions).IsFalse();
        fixture.Lease.SetTraceTarget(new(1100, 1102, 10.1f));
        await Assert.That(fixture.Navigation.TraceTargetCount).IsEqualTo(1);
        var marker = fixture.Lease.TraceTarget!;
        await Assert.That(marker.EntityId).IsEqualTo(0u);
        fixture.Lease.Dispose();
        await Assert.That(marker.Enabled).IsFalse();
        await Assert.That(fixture.Navigation.Count).IsEqualTo(0);
        await Assert.That(fixture.Navigation.TraceTargetCount).IsEqualTo(0);
        await Assert.That(follower.IsDisposed).IsTrue();
    }

    [Test]
    public async Task UnclassifiedAuthoredEntityCollisionRefusesBeforePublishingAFalseNoPath()
    {
        using var fixture = new Fixture(withUnknownEntity: true);
        var before = fixture.Runtime.Bodies!.Find(fixture.Actor.Handle)!.CopyState();
        await Assert.That(fixture.Request).Throws<NotSupportedException>();
        await Assert.That(fixture.Lease.PathDecision).IsEqualTo(ZoneApproachPathDecision.Pending);
        await Assert.That(fixture.Actor.Navigation.CurrentPath.Count).IsEqualTo(0);
        await Assert.That(fixture.Actor.Navigation.OriginalPath.Count).IsEqualTo(0);
        await Assert.That(fixture.Runtime.Bodies.Find(fixture.Actor.Handle)!.CopyState()).IsEqualTo(before);
    }

    [Test]
    public async Task ARealTerrainUnloadAndGraphRetirementInvalidateTheBoundActorRequests()
    {
        using var fixture = new Fixture();
        fixture.Request();
        using var goal = fixture.Lease.CreateTrace(new(-1, false, false));
        fixture.Graph.RetireTile(fixture.Tile);
        await Assert.That(() => goal.Execute()).Throws<NotSupportedException>();
        await Assert.That(fixture.Runtime.Backend!.Geometry.HeightField.UnloadCell(1, 1)).IsTrue();
        await Assert.That(fixture.Request).Throws<NotSupportedException>();
        await Assert.That(fixture.Lease.PathDecision).IsEqualTo(ZoneApproachPathDecision.Pending);
        await Assert.That(fixture.Actor.Navigation.CurrentPath.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    public async Task RetiredPhysicalCoverageStopsACachedMovingRequestBeforeAnotherStep(int family)
    {
        using var fixture = new Fixture();
        fixture.EnablePuppetOutput();
        fixture.SelectReturnPipe();
        for (var frame = 0; frame < 6; frame++) fixture.Step();
        var before = fixture.Actor.Npc.Unit.Pose;
        fixture.RetirePhysicalPublication(family);
        await Assert.That(fixture.Lease.ValidatePublication).Throws<NotSupportedException>();
        await Assert.That(fixture.Step).Throws<InvalidOperationException>();
        await Assert.That(fixture.Actor.Npc.Unit.Pose).IsEqualTo(before);
        await Assert.That(fixture.Runtime.Bodies!.Count).IsEqualTo(0);
        await Assert.That(fixture.Runtime.Runtime.Count).IsEqualTo(0);
        await Assert.That(fixture.Navigation.Count).IsEqualTo(0);
    }

    [Test]
    public async Task AiFrameTimestampIsConsumedBeforeInitializationAndGlobalCvarGuards()
    {
        using var fixture = new Fixture();
        var actors = fixture.Runtime.Runtime.Actors;
        await Assert.That(actors.BeginAiFrame(true, ZoneEngineCVars.Empty)).IsFalse(); //Native initial static timestamp0.
        fixture.Runtime.Clock.Advance(TimeSpan.FromMilliseconds(20));
        await Assert.That(actors.BeginAiFrame(false, ZoneEngineCVars.Empty)).IsFalse();
        await Assert.That(actors.BeginAiFrame(true, ZoneEngineCVars.Empty)).IsFalse();
        fixture.Runtime.Clock.Advance(TimeSpan.FromMilliseconds(20));
        await Assert.That(actors.BeginAiFrame(true, ZoneEngineCVars.Parse("ai_SystemUpdate=0"))).IsFalse();
        await Assert.That(actors.BeginAiFrame(true, ZoneEngineCVars.Empty)).IsFalse();
        fixture.Runtime.Clock.Advance(TimeSpan.FromMilliseconds(20));
        await Assert.That(actors.BeginAiFrame(true, ZoneEngineCVars.Empty)).IsTrue();
        await Assert.That(actors.BeginAiFrame(true, ZoneEngineCVars.Empty)).IsFalse();
    }

    [Test]
    public async Task AnUnclassifiedPhysicalActorCannotCertifyAnEmptyCrowdOrContinueACachedMovingRequest()
    {
        using var fixture = new Fixture();
        fixture.EnablePuppetOutput();
        fixture.SelectReturnPipe();
        for (var frame = 0; frame < 6; frame++) fixture.Step();
        var before = fixture.Actor.Npc.Unit.Pose;
        var backend = fixture.Runtime.Backend!;
        var unclassified = new ZoneActorHandle(0x00ffff00, 1);
        var stance = fixture.Runtime.Bodies!.Find(fixture.Actor.Handle)!.CopyState().CurrentStance;
        backend.AddActor(unclassified, ZonePhysicsActorDescription.FromLivingStance(stance, 80));
        try
        {
            //This actual living body starts at zero, outside the NPC's30m current
            //radius. No source AI/cache owner exists to certify its membership or
            //cached position, so current-body distance cannot establish absence.
            await Assert.That(() => fixture.Lease.UpdateAvoidance(true, false)).Throws<NotSupportedException>();
            await Assert.That(fixture.Lease.ValidatePublication).Throws<NotSupportedException>();
            await Assert.That(fixture.Step).Throws<InvalidOperationException>();
            await Assert.That(fixture.Actor.Npc.Unit.Pose).IsEqualTo(before);
            await Assert.That(fixture.Runtime.Runtime.Count).IsEqualTo(0);
            await Assert.That(fixture.Runtime.Bodies.Count).IsEqualTo(0);
        }
        finally { backend.RemoveActor(unclassified); }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly ZoneManagedSceneCollision _collision;
        private readonly ZoneManagedWaterQueries _water;
        private readonly ZoneManagedNpcPuppetOutput _output;
        private readonly ZoneManagedActorGrids _grids;
        private ZoneManagedNpcMovementFramePhases? _phases;
        private uint _frame;
        public ManagedNpcRuntimeTests.Fixture Runtime { get; }
        public ZoneManagedNpcActor Actor { get; }
        public ZoneManagedNpcNavigation Navigation { get; }
        public ZoneManagedNpcNavigationLease Lease { get; }
        public ZoneNavigationTiledGraph Graph { get; }
        public ZoneMapCoordinate Tile { get; } = new(40, 60);
        public ZoneAiObjectState Target { get; }
        public Fixture(bool withUnknownEntity = false)
        {
            var scene = new ManagedSceneActivationTests.Fixture((9, 14), (10, 15));
            var placement = scene.Layout.Cells.Single(cell => cell.EngineCell == new ZoneMapCoordinate(1, 1));
            var diagnostics = new[] { "Objects: authored asset is absent.", "BigObjects: authored asset is absent.",
                "Vegetation: authored asset is absent.", "Entities: authored asset is absent." };
            var entities = withUnknownEntity ? ZoneCellEntityContent.Read("<Root><Objects><Entity EntityClass='UnknownCollisionOwner'/></Objects></Root>") : null;
            var cell = new ZoneManagedSceneCellLoadReport(placement, new(ZoneTerrainCellLoadStatus.Loaded),
                new(null, null, entities, null, diagnostics));
            _collision = new(scene.Assets, ZoneTerrainMaterialResolver.Load(scene.Assets), [cell]);
            ZoneManagedNpcNavigation? navigationOwner = null;
            ZoneManagedNpcPuppetOutput? outputOwner = null;
            Runtime = new(withMovementRequests: true, withMovementAbility: true, movementNormalSpeed: 6,
                movementStaticGeometry: _collision.Geometry, navigationFactory: () => navigationOwner!,
                puppetOutputFactory: () => outputOwner!);
            ManagedNpcMovementAbilityTests.InstallTables(Runtime);
            using (var result = Runtime.Runtime.Lua.Execute("npc.ActorTemplate.AIMovementAbility.usePredictiveFollowing=1; npc.ActorTemplate.AIMovementAbility.pathRegenIntervalDuringTrace=4")) { }
            var tiles = new NavigationTileFixture();
            tiles.Add(Tile, [new(1, new(76, 76, 10.1f)), new(2, new(76, 82, 10.1f))], [new(1, 2, 2)],
                [new(70, 70, 10.1f), new(90, 70, 10.1f), new(70, 100, 10.1f)]);
            Graph = new(tiles.Origin, [tiles.Load(Tile)]);
            _water = new(0, [cell], ZoneManagedWaterQueryProfile.StaticServerWithoutOceanVortex);
            var queries = new ZoneManagedNpcNavigationQueries(Runtime.Backend!, Runtime.Backend!.Geometry.HeightField,
                new([cell], true, 1, 0, []), _collision, _water, new(new(128, .25f, 1024, .75f, 2, .7f), 2, 65536));
            _grids = new(Runtime.Runtime.Actors.Objects, ZoneManagedActorGridConfiguration.FromLayout(scene.Layout));
            Runtime.Runtime.Actors.ActorGrids = _grids;
            Navigation = navigationOwner = new(Runtime.Runtime.Actors, Runtime.MovementAbility!, Runtime.Motion!, Runtime.Bodies!, Graph, queries,
                ZoneEngineCVars.Empty, new ZoneStatCalculator(Runtime.MovementStats!), Runtime.Components);
            Runtime.Runtime.Actors.Navigation = Navigation;
            Runtime.Runtime.Actors.MovementAbility = Runtime.MovementAbility;
            _output = outputOwner = new(Runtime.Runtime.Actors, Runtime.Poses!, Runtime.MovementAbility!, Runtime.Motion!,
                new ZoneStatCalculator(Runtime.MovementStats!), actor => Navigation.Find(actor.Handle));
            Runtime.Runtime.Actors.PuppetOutput = _output;
            Runtime.Runtime.Actors.MovementRequests = Runtime.MovementRequests;
            var npc = Runtime.Admission.CreateFromWire(Runtime.Model.Id, new(1100, 1100, 10.1f));
            Actor = Runtime.Runtime.Actors.Find(Runtime.Admission.Catalog.TryGet(npc)!.Value)!;
            Lease = Navigation.Find(Actor.Handle)!;
            Runtime.Runtime.Actors.Points.SetReferencePosition(Actor, new(1100, 1106, 10.1f));
            Target = Runtime.Runtime.Actors.Points.GetReferencePoint(Actor);
        }
        public void Request() => Lease.RequestPath(new(Target, Target.Position, Target.MoveDirection, true, -1, float.MaxValue, 2));
        public void SelectReturnPipe()
        {
            using var source = Runtime.Runtime.Lua.Execute("""
                AI.BeginGoalPipe('moving_coverage_control')
                  AI.PushGoal('locate',1,'refpoint')
                  AI.PushGoal('run',1,1)
                  AI.PushGoal('approach',1,2,AILASTOPRES_USE,-1)
                AI.EndGoalPipe()
                observedEntity:SelectPipe(AIGOALPIPE_DONT_RESET_AG,'moving_coverage_control')
                """);
        }
        public void RetirePhysicalPublication(int family)
        {
            if (family == 0)
            {
                var terrain = Runtime.Backend!.Geometry.HeightField;
                terrain.UnloadCell(1, 1);
                terrain.LoadCell(1, 1, TerrainHeightFieldTests.CreateCell(10, 0)); //Same data still has a new publication.
            }
            else if (family == 3) _water.RetireCell(new(1, 1));
            else
            {
                var geometry = family == 1 ? _collision.Geometry : _collision.RayGeometry;
                var instance = geometry.AddInstance(new ZoneManagedTriangleMesh(
                    [new(1200, 1200, 10), new(1202, 1200, 10), new(1200, 1202, 10)], [0, 1, 2]), Matrix4x4.Identity);
                geometry.RemoveInstance(instance); //Returning to an empty catalog cannot resurrect an old route.
            }
        }
        public void EnablePuppetOutput()
        {
            Runtime.Runtime.Actors.GameObjectActivationProfile = ZoneManagedNpcGameObjectActivationProfile.MultiplayerWorldServer;
            Actor.Npc.Activation.SetActive(true, Runtime.Admission.World.GetActivationServices(Actor.Npc));
            var diagnostics = new ZoneMovementDiagnostics();
            _phases = new(new BackendPhases(Runtime.Backend!), Runtime.Admission.Registry, Runtime.Admission.Catalog,
                Runtime.Runtime.Actors, Runtime.Bodies!, new(Runtime.Admission.Registry, diagnostics, true), diagnostics,
                prePhysics: sample =>
                {
                    Runtime.CurrentFrame = sample;
                    RequireNoFault(Runtime.Runtime.PrePhysics(sample, Runtime.Admission.Provider.RemoveNpc));
                },
                publishPose: (actor, body, sample) => RequireNoFault(Runtime.Runtime.PublishPhysicalPose(actor.Handle, body, sample, Runtime.Admission.Provider.RemoveNpc)),
                managerOwnsReplication: true,
                prepareRequests: sample => RequireNoFault(Runtime.Runtime.PrepareMovementRequests(sample, Runtime.Admission.Provider.RemoveNpc)));
        }
        public void Step()
        {
            var elapsed = TimeSpan.FromMilliseconds(20); Runtime.Clock.Advance(elapsed); _frame++;
            var sample = new ZoneMovementFrame(_frame, elapsed, 400000 + _frame * 20, _frame * .02f)
            { LocalGameMilliseconds = 2000 + _frame * 20, UnpausedMilliseconds = 7000 + _frame * 20 };
            _phases!.PrePhase(sample); _phases.StepPhysics(elapsed); _phases.PostPhase(sample);
            if (Runtime.Runtime.Actors.BeginAiFrame(true, ZoneEngineCVars.Empty)) Actor.Update(2000 + _frame * 20, .02f);
        }
        public bool Completed()
        { using var result = Runtime.Runtime.Lua.Execute("return observedEntity.completed or false"); return result[0] is true; }
        private static void RequireNoFault(IReadOnlyList<AAEmu.Zone.Scripting.ZoneManagedNpcScriptFault> faults)
        { if (faults.Count != 0) throw new InvalidOperationException(string.Join("; ", faults.Select(fault => fault.Message))); }
        public void Dispose()
        {
            try { _phases?.Dispose(); Runtime.Dispose(); }
            finally { try { _output.Dispose(); Navigation.Dispose(); _grids.Dispose(); } finally { _water.Dispose(); Graph.Dispose(); _collision.Dispose(); } }
        }
    }
    private sealed class BackendPhases(ZoneManagedPhysicsBackend backend) : IZoneMovementFramePhases
    {
        public int PrePhase(in ZoneMovementFrame frame) => 0;
        public void StepPhysics(TimeSpan elapsed) => backend.Step(elapsed);
        public int PostPhase(in ZoneMovementFrame frame) => 0;
    }
}

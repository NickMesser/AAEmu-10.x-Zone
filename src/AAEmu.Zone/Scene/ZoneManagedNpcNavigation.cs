using System.Numerics;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Ai;
using AAEmu.Zone.Combat;
using AAEmu.Zone.Hosting;
using AAEmu.Zone.Navigation;
using AAEmu.Zone.Physics;
using AAEmu.Zone.Scripting;

namespace AAEmu.Zone.Scene;

/// <summary>
/// Exact-generation path/trace ownership over the scene's authored graph and single physical
/// owner. Route search remains explicitly bounded; unsupported scene coverage is a refusal,
/// never an empty route or an invented direct movement wish.
/// </summary>
public sealed class ZoneManagedNpcNavigation : IDisposable
{
    internal ZoneManagedNpcActorScene Actors { get; }
    internal ZoneManagedNpcMovementAbility Ability { get; }
    internal ZoneManagedNpcActorMotion Motion { get; }
    internal ZoneManagedNpcPhysics Bodies { get; }
    internal ZoneManagedNpcNavigationQueries Queries { get; }
    internal ZoneNavigationTiledGraph Graph { get; }
    internal ZoneEngineCVars CVars { get; }
    internal ZoneStatCalculator? Stats { get; }
    internal ZoneManagedNpcSceneScriptServices? Components { get; }
    public ZoneNavigationAvoidanceSpheres AvoidanceSpheres { get; } = new();
    private readonly Dictionary<ZoneActorHandle, ZoneManagedNpcNavigationLease> _live = [];
    public bool IsDisposed { get; private set; }
    public int Count => _live.Count;
    public int TraceTargetCount => _live.Values.Count(owner => owner.TraceTarget is not null);

    public ZoneManagedNpcNavigation(ZoneManagedNpcActorScene actors, ZoneManagedNpcMovementAbility ability,
        ZoneManagedNpcActorMotion motion, ZoneManagedNpcPhysics bodies, ZoneNavigationTiledGraph graph,
        ZoneManagedNpcNavigationQueries queries, ZoneEngineCVars cvars, ZoneStatCalculator? stats = null,
        ZoneManagedNpcSceneScriptServices? components = null)
    {
        Actors = actors ?? throw new ArgumentNullException(nameof(actors));
        Ability = ability ?? throw new ArgumentNullException(nameof(ability));
        Motion = motion ?? throw new ArgumentNullException(nameof(motion));
        Bodies = bodies ?? throw new ArgumentNullException(nameof(bodies));
        Graph = graph ?? throw new ArgumentNullException(nameof(graph));
        Queries = queries ?? throw new ArgumentNullException(nameof(queries));
        CVars = cvars ?? throw new ArgumentNullException(nameof(cvars));
        Stats = stats;
        Components = components;
    }

    public ZoneManagedNpcNavigationLease Bind(ZoneManagedNpcActor actor)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this); Actors.Require(actor);
        if (!actor.IsPuppet || actor.Entry.Registration?.ObjectType != 5)
            throw new NotSupportedException("This path owner requires an ordinary registered NPC Puppet.");
        if (Components is null)
            throw new NotSupportedException("NPC navigation requires its actual entity/model transform publication owner.");
        if (Actors.ActorGrids is null)
            throw new NotSupportedException("NPC navigation requires the actual cached actor-grid owner.");
        if (_live.ContainsKey(actor.Handle)) throw new InvalidOperationException("This exact actor already owns its path request and persistent follower.");
        if (Bodies.Find(actor.Handle) is not { } body || !ReferenceEquals(body.Npc, actor.Npc) || Motion.Find(actor.Handle) is null)
            throw new NotSupportedException("NPC navigation requires the actual CActor motion and living-body owners.");
        var owner = new ZoneManagedNpcNavigationLease(this, actor);
        _live.Add(actor.Handle, owner); return owner;
    }
    public ZoneManagedNpcNavigationLease? Find(ZoneActorHandle actor) => !IsDisposed &&
        _live.TryGetValue(actor, out var owner) && owner.IsCurrent ? owner : null;
    internal bool Owns(ZoneManagedNpcNavigationLease owner) => !IsDisposed && !owner.IsDisposed &&
        _live.TryGetValue(owner.Actor.Handle, out var current) && ReferenceEquals(current, owner) &&
        ReferenceEquals(Actors.Find(owner.Actor.Handle), owner.Actor) && Bodies.Find(owner.Actor.Handle) is { } body &&
        ReferenceEquals(body.Npc, owner.Actor.Npc) && Motion.Find(owner.Actor.Handle) is not null;
    internal void Retire(ZoneManagedNpcNavigationLease owner)
    { if (_live.TryGetValue(owner.Actor.Handle, out var current) && ReferenceEquals(current, owner)) _live.Remove(owner.Actor.Handle); }
    internal void RequireCrowdActorCoverage()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        foreach (var physical in Queries.CopyPhysicalInventory())
        {
            //The public AI index is not a census of all physical scene actors. In
            //particular, managed player admission does not yet own the source AI
            //registration/type100 contract. Its absent index entry cannot prove
            //an empty player bucket, nor can current body position replace the
            //missing actor's independently cached grid position.
            if (Actors.Find(physical.Actor) is not { } actor || actor.ActorGrid is not { IsCurrent: true })
                throw new NotSupportedException("NPC crowd census has a registered physical actor without an owned AI/grid classification; player and other actor registration profiles remain unowned.");
        }
    }
    public void Dispose()
    {
        if (IsDisposed) return; IsDisposed = true;
        foreach (var owner in _live.Values.ToArray()) owner.Dispose();
        AvoidanceSpheres.Dispose();
    }
}

public sealed class ZoneManagedNpcNavigationLease : IZoneApproachGoalServices, IZoneApproachPathFindServices,
    IZonePredictiveTraceGoalServices, IDisposable
{
    private readonly ZoneManagedNpcNavigation _owner;
    private ZoneNavigationPathResult? _route;
    private ZoneManagedNpcNavigationCoverage? _coverage;
    private ZoneManagedNpcPointObject? _trace;
    private Vector3 _requestedTargetPosition;
    private IReadOnlyList<ZonePredictivePathSample> _predictions = Array.Empty<ZonePredictivePathSample>();
    private ZonePredictivePathFollower? _predictiveFollower;
    private readonly ZoneEmptyCrowdAvoidance _crowd;
    private uint _cachedNavigationType = 1; //31163974→3115E764 initializes actor3760.
    public ZoneManagedNpcActor Actor { get; }
    public bool IsDisposed { get; private set; }
    public bool IsCurrent => _owner.Owns(this);
    public ZoneActorControlState Controls => Actor.Controls;
    public ZoneActorNavigationState Navigation => Actor.Navigation;
    public ZoneActorLookState LookState => Actor.LookState;
    public object? Argument => Actor.Argument;
    public ZoneApproachPathDecision PathDecision { get; set; }
    public ZoneAiObjectState? CurrentPathTarget { get; private set; }
    public ZoneAiObjectState? TraceTarget => _trace?.State;
    public IReadOnlyList<ZonePredictivePathSample> MovementPredictions => _predictions;
    public int MovementContext { get; private set; } //CPipeUser constructor actor3600=0.
    public int UpdatePriority => 3; //All three writers31171F8C/3116D3CC/3109B198 store3 at actor4728.
    public ZonePredictivePathFollower? ExistingPredictiveFollower => _predictiveFollower;
    public ZonePredictivePathFollower PredictiveFollower
    {
        get
        {
            Require(); var parameters = Parameters;
            if (!parameters.PredictiveFollowing)
                throw new NotSupportedException("This actor's installed movement parameters do not allocate a predictive follower.");
            if (_predictiveFollower is null)
            {
                _predictiveFollower = new(Actor.Navigation.CurrentPath);
                _predictiveFollower.Parameters = _predictiveFollower.Parameters with
                { PathRadius = parameters.GroundTrace.PathRadius, LookAhead = parameters.GroundTrace.LookAhead };
            }
            _predictiveFollower.Parameters = _predictiveFollower.Parameters with { Use2D = !parameters.ThreeDimensionalMovement };
            return _predictiveFollower;
        }
    }
    public int? CurrentPathTargetType => CurrentPathTarget is { } target ? GetTargetType(target) : null;
    private ZoneManagedNpcMovementAbilitySnapshot Parameters => _owner.Ability.CopyState(Actor);
    public bool UsesPathfinder { get { Require(); return Parameters.UsesPathfinder; } }
    public bool UsesThreeDimensionalNavigation
    { get { Require(); return ZoneNavigationPathDecision.UsesThreeDimensionalNavigation(Profile(), Navigation.CurrentPath, out _); } }

    internal ZoneManagedNpcNavigationLease(ZoneManagedNpcNavigation owner, ZoneManagedNpcActor actor)
    { _owner = owner; Actor = actor; _crowd = new(actor.AiObject, 5); }
    private void Require()
    { if (!IsCurrent) throw new ObjectDisposedException(nameof(ZoneManagedNpcNavigationLease), "This exact actor's navigation generation has retired."); }
    public object? GetAttentionTarget() { Require(); return Actor.AttentionTarget; }
    public Vector3 GetPosition() { Require(); return Actor.AiObject.Position; }
    public Vector3 GetEntityPosition() => CopyEntityPosition(Actor);
    private Vector3 CopyEntityPosition(ZoneManagedNpcActor actor)
    {
        Require();
        var position = _owner.Components!.CopyModelTransform(actor.Npc.Unit).Position;
        Require(); return position;
    }
    public Vector3 GetEntityPosition(object target)
    {
        Require(); var state = Target(target);
        if (_owner.Actors.ObjectOwner(state) is { } actor) return CopyEntityPosition(actor);
        if (state.EntityId != 0) throw new NotSupportedException("A navigation target's external entity position has no admitted owner.");
        return state.Position;
    }
    public Vector3 GetTargetDirection(object target) { Require(); return Target(target).MoveDirection; }
    public bool IsTargetEnabled(object target) { Require(); return Target(target).Enabled; }
    public int GetTargetType(object target)
    {
        Require(); var state = Target(target);
        if (_owner.Actors.ObjectOwner(state) is { } actor) return actor.Entry.Registration!.ObjectType;
        foreach (var candidate in _owner.Actors.CopyActors())
            if (ReferenceEquals(candidate.Points?.Reference.State, state) || ReferenceEquals(candidate.Points?.Look.State, state)) return 2;
        if (ReferenceEquals(TraceTarget, state)) return 2;
        throw new NotSupportedException("Navigation requires the target's actual registered AI object type.");
    }
    private static ZoneAiObjectState Target(object value) => value as ZoneAiObjectState ??
        throw new ArgumentException("Navigation targets require actual managed AI object identity.", nameof(value));

    public void PrepareApproach(bool pathFound)
    {
        Require();
        //The owned CPipeUser has no allocated persistent COPStick; its factory refuses
        //that operation. actor2832 ignore-end-direction is the fresh false branch here.
    }
    public bool AdmitPathDestination(Vector3 targetPosition)
    {
        Require();
        //No path-area registration producer is admitted by this scene. The corresponding
        //native named-area lookup therefore has no registered restriction to intersect.
        return true;
    }
    public IZoneGoalOperation CreatePathFind(ZoneApproachPathRequest request)
    { Require(); return new ZoneApproachPathFindGoal(request, this); }
    public IZoneGoalOperation CreateTrace(ZoneTraceGoalParameters parameters)
    {
        Require();
        return Parameters.PredictiveFollowing && _owner.CVars.GetInt("ai_PredictivePathFollowing", 1) != 0
            ? new ZonePredictiveTraceGoal(parameters, this, _owner.Actors.FrameMicroseconds)
            : new ZoneGroundTraceGoal(parameters, this, _owner.Actors.FrameMicroseconds);
    }
    public void RequestPath(ZoneApproachPathSubmission submission)
    {
        Require(); ArgumentNullException.ThrowIfNull(submission);
        if (submission.ForceBuildingId != -1)
            throw new NotSupportedException("NPC forced-building requests require their original region/waypoint producer.");
        ResetPath(); PathDecision = ZoneApproachPathDecision.Pending;
        var parameters = Parameters;
        if (parameters.ThreeDimensionalMovement || parameters.FlyingCreature || parameters.UnderwaterCreature)
            throw new NotSupportedException("NPC three-dimensional path requests require their original graph and physical query profile.");
        var start = GetEntityPosition(); var query = _owner.Queries.ForActor(Actor.Handle);
        var physicalPublication = _owner.Queries.CaptureCoverage();
        var coverage = _owner.Queries.CheckCollisionCoverage(new(Vector3.Min(start, submission.Position), Vector3.Max(start, submission.Position)));
        if (coverage.Outcome != ZoneNavigationSegmentOutcome.Clear) throw new NotSupportedException(coverage.Diagnostic);
        var route = _owner.Graph.FindPath(new(start, submission.Position, parameters.PassRadius)
        {
            CapabilityMask = (uint)parameters.NavigationCapabilities,
            MaximumEndpointDistance = _owner.Queries.Policy.MaximumEndpointDistance,
            MaximumExpandedNodes = _owner.Queries.Policy.MaximumExpandedNodes,
        }, query);
        Require();
        if (!_owner.Graph.IsCurrent(route)) throw new NotSupportedException("The graph publication retired during the NPC's path request.");
        if (route.Outcome != ZoneNavigationSearchOutcome.Found)
        {
            if (route.Outcome != ZoneNavigationSearchOutcome.NoRoute || route.UnsupportedLinks != 0 || route.MissingSegments != 0)
                throw new NotSupportedException($"NPC path admission {route.Outcome}: {route.Diagnostic}");
            var failed = ZoneNavigationPathDecision.PublishNoPath(Navigation, Profile());
            if (failed.ClearArgument) Actor.Services.SetArgument(null);
            PathDecision = failed.Decision; return;
        }
        var candidate = route.CopyPath();
        candidate.CreationParameters = candidate.CreationParameters with
        {
            Start = start, End = submission.Position, EndDirection = submission.Direction,
            ForceBuildingId = submission.ForceBuildingId, AllowDangerousDestination = submission.AllowDangerousDestination,
            EndDistance = submission.EndDistance,
        };
        var body = _owner.Bodies.Find(Actor.Handle)!.CopyState(); Require();
        var measurements = _owner.Queries.MeasureWater(Actor, body, _owner.Motion, candidate); Require();
        var trimmed = candidate.Clone();
        if (MathF.Abs(submission.EndDistance) > .0001f) trimmed.TrimEnd(submission.EndDistance, false);
        var profile = Profile();
        ZoneNavigationObstacleCensus? census = null;
        ZoneManagedActorGridQuery? gridPublication = null;
        if (trimmed.HasPath && profile.AdjustAroundDynamicObstacles && profile.CrowdControlInPathfind && profile.PathRegenerationInterval > .01f)
        {
            census = CopyObstacleCensus(ZoneNavigationPathDecision.CreateObstacleQuery(trimmed, GetPosition(),
                profile.PathRegenerationInterval, profile.CombatRunMaximum), out var publication);
            gridPublication = publication;
        }
        Require();
        if (!_owner.Graph.IsCurrent(route)) throw new NotSupportedException("The graph publication retired before path decision publication.");
        if (!_owner.Queries.IsCurrent(physicalPublication))
            throw new NotSupportedException("The NPC route's admitted physical publication retired during its path request.");
        if (gridPublication is not null && !_owner.Actors.ActorGrids!.IsCurrent(gridPublication))
            throw new NotSupportedException("The NPC path obstacle census retired before publication.");
        var decision = ZoneNavigationPathDecision.PublishOrdinary(Navigation, Controls, candidate, profile,
            Actor.Handle, GetPosition(), measurements, census);
        _route = route; _coverage = physicalPublication; _cachedNavigationType = decision.CachedNavigationType;
        CurrentPathTarget = Target(submission.Target); _requestedTargetPosition = submission.Position;
        PathDecision = decision.Decision;
    }
    private ZoneNavigationObstacleCensus CopyObstacleCensus(ZoneNavigationObstacleQuery query, out ZoneManagedActorGridQuery publication)
    {
        Require();
        var physical = _owner.Queries.CopyPhysicalInventory();
        var grids = _owner.Actors.ActorGrids!;
        publication = grids.QueryBucket(0, query.ActorPosition, query.ActorRadius);
        var actors = new List<ZoneNavigationAiObstacleCandidate>();
        foreach (var entry in publication.Entries)
        {
            var state = entry.Object ?? throw new InvalidDataException("An actor-grid query returned a non-actor entry.");
            var actor = _owner.Actors.ObjectOwner(state) ??
                throw new NotSupportedException("The path census includes a registered AI actor with no admitted World actor identity.");
            actors.Add(new(actor.Handle, entry.Registration.ObjectType, entry.CachedPosition,
                physical.FirstOrDefault(entity => entity.Actor == actor.Handle).Identity));
        }
        Require();
        if (!grids.IsCurrent(publication)) throw new NotSupportedException("The path's actor-grid publication retired during its census.");
        return new(query, actors.AsReadOnly(), physical.Select(entity => new ZoneNavigationPhysicalObstacleCandidate(
            entity.Identity, entity.EntityClass, entity.Bounds)).ToArray(), _owner.AvoidanceSpheres.Copy());
    }
    private ZoneNavigationPathDecisionProfile Profile()
    {
        var parameters = Parameters;
        var motion = _owner.Motion.Find(Actor.Handle) ?? throw new ObjectDisposedException(nameof(ZoneManagedNpcActorMotionLease));
        return new(parameters.NavigationCapabilities, motion.State.Mode == ZoneManagedNpcActorMotionMode.Swimming,
            parameters.FlyingCreature, parameters.UnderwaterCreature, parameters.ThreeDimensionalMovement,
            _cachedNavigationType, false, false, false, 0, _owner.Motion.CopyPuppetAlertness(Actor) ?? 0,
            _owner.CVars.GetInt("ai_AdjustPathsAroundDynamicObstacles", 1) != 0,
            _owner.CVars.GetInt("ai_CrowdControlInPathfind", 1) != 0, parameters.PathRegenInterval,
            parameters.Speeds.Select(1f, 0).Maximum);
    }
    public void ResetPath()
    {
        Require(); Navigation.CurrentPath.Clear(); Navigation.OriginalPath.Clear();
        _route = null; _coverage = null; CurrentPathTarget = null;
    }
    public float GetMovementSpeed() { Require(); return _owner.Ability.GetMovementSpeeds(Actor).Desired; }
    public ZoneGroundTraceSpeeds GetMovementSpeeds()
    { Require(); var speed = _owner.Ability.GetMovementSpeeds(Actor); return new(speed.Desired, speed.Minimum, speed.Maximum); }
    public void SetMovementContext(int context) { Require(); MovementContext = context; }
    public void SubmitNoPathSignal() => SubmitSignal("OnNoPathFound", false);
    public void SubmitSignal(string name, bool senderIsActor, ZoneAiSignalData? data = null)
    { Require(); Actor.Npc.AiSignals.Submit(0, name, senderIsActor ? Actor.Entry.EntityId : 0, data ?? new()); }
    public ZoneApproachStuckInput ReadStuckInput()
    {
        Require();
        var executor = Actor.Entry.Phase.Pipes.Executor;
        for (var index = 0; index < executor.ActiveGoalCount; index++)
        {
            using var active = executor.RetainActiveGoal(index);
            //3115C414 first requires a nonzero operation.v72 before reading v88.
            //All four owned approach/path/trace operations have the actual return0 gate.
            if (active.Operation is not (ZoneApproachGoal or ZoneApproachPathFindGoal or ZoneGroundTraceGoal or ZonePredictiveTraceGoal))
                throw new NotSupportedException("Approach stuck suppression requires this active operation's original v72/v88 contract.");
        }
        var canMove = (_owner.Actors.MovementEligibility ??
            throw new NotSupportedException("Approach stuck evaluation requires the actual Unit/model movement predicate.")).CanMove(Actor);
        Require();
        if (!canMove) return new(true, false, false);
        var input = (_owner.Actors.PuppetOutput ??
            throw new NotSupportedException("Approach stuck evaluation requires its actual current SAIActorState owner.")).CopyStuckMovementInput(Actor);
        var unit = Actor.Npc.Unit;
        var attributes = (_owner.Stats ?? throw new NotSupportedException("Approach stuck evaluation requires its actual Unit attribute10 calculator."))
            .CalculateAttributes(unit.Snapshot, [10], unit.Buffs, unit.Progression, unit.Equipment, unit.AttributeOverrides);
        Require();
        if (!attributes.IsComplete)
            throw new NotSupportedException("Approach stuck attribute10 has unresolved sources: " + string.Join(", ", attributes.UnresolvedSources));
        //39213180 directly reads AIObject28..36.392E0C80 fills that cache from
        //physical status2+72, the living dynamics gravity vector. Preserve this
        //client's actual offset contract; physical velocity is a different cache.
        var cached = _owner.Motion.CopyAiPhysicsState(Actor);
        Require();
        return new(true, false, ZoneApproachStuckMath.IsStuck(input.MoveDirection, input.DesiredSpeed,
            unchecked((int)attributes.Values[10]), cached.Gravity));
    }
    public void ValidateOrdinaryGroundTrace(ZoneTraceGoalParameters parameters)
    {
        ValidateTrace(parameters);
        if (Parameters.PredictiveFollowing) _ = PredictiveFollower; //310EE988 calls31158AC4 before the global CVar branch.
        if (Parameters.PredictiveFollowing && _owner.CVars.GetInt("ai_PredictivePathFollowing", 1) != 0)
            throw new NotSupportedException("This actor selects the original predictive follower, not the ordinary trace branch.");
    }
    public void ValidatePredictiveTrace(ZoneTraceGoalParameters parameters)
    {
        ValidateTrace(parameters);
        if (!Parameters.PredictiveFollowing || _owner.CVars.GetInt("ai_PredictivePathFollowing", 1) == 0)
            throw new NotSupportedException("This actor does not select the original predictive trace branch.");
        _ = PredictiveFollower;
    }
    private void ValidateTrace(ZoneTraceGoalParameters parameters)
    {
        ValidatePublication();
        if (parameters.StopOnAnimationStart || Parameters.ThreeDimensionalMovement || Parameters.FlyingCreature || Parameters.UnderwaterCreature)
            throw new NotSupportedException("NPC trace requires the owned ordinary ground profile without animation/three-dimensional interruption.");
        if (CurrentPathTarget is { } target && GetEntityPosition(target) != _requestedTargetPosition)
            throw new NotSupportedException("A moved path target requires the original trace regeneration/request consumer.");
    }
    /// <summary>Also checked before physical requests, independently of this actor's AI scheduling cadence.</summary>
    public void ValidatePublication()
    {
        Require();
        if (_route is not null && !_owner.Graph.IsCurrent(_route))
            throw new NotSupportedException("The NPC route belongs to a retired graph publication.");
        if (_route is not null && (_coverage is not { } coverage || !_owner.Queries.IsCurrent(coverage)))
            throw new NotSupportedException("The NPC route's admitted terrain/static/water publication has retired.");
        if (_route is not null) _owner.RequireCrowdActorCoverage();
    }
    public ZoneGroundTraceFrame ReadFrame()
    {
        Require(); var body = _owner.Bodies.Find(Actor.Handle)!.CopyState(); Require();
        return new(GetEntityPosition(), body.Status.Velocity, Parameters.GroundTrace, _cachedNavigationType,
            new(body.Status.IsFlying, body.Control.CachedGroundNormal),
            new(Parameters.ManeuverThreshold, false, Actor.AiObject.BodyDirection));
    }
    public ZonePredictiveTraceFrame ReadPredictiveFrame()
    { var frame = ReadFrame(); return new(frame.EntityPosition, frame.ActualVelocity, frame.Ability, !UsesThreeDimensionalNavigation, UpdatePriority < 2); }
    public bool BeginMove() { Require(); return false; } //The owned AI event-listener inventory is empty.
    public void SetTraceTarget(Vector3 position)
    {
        Require(); _trace ??= new("trace_" + Actor.Entry.Name, 0);
        _trace.SetTransform(position, Vector3.Zero);
    }
    public void ReleaseTraceTarget()
    {
        if (_trace is not { } trace) return;
        _trace = null; trace.State.Enabled = false;
        _owner.Actors.RetirePoint(trace.State);
    }
    public void RefreshNavigationType()
    {
        Require();
        _cachedNavigationType = (uint)_owner.Graph.Snapshot.ModifierQuery.GetNavigationType(GetEntityPosition(), (uint)Parameters.NavigationCapabilities);
        Require();
    }
    public void UpdateAvoidance(bool fullUpdate, bool ignoreTarget)
    {
        Require();
        _owner.RequireCrowdActorCoverage();
        var grids = _owner.Actors.ActorGrids!;
        var position = GetEntityPosition();
        var puppets = grids.QueryBucket(0, position, 30);
        var players = grids.QueryBucket(1, position, 30);
        var candidates = puppets.Entries.Concat(players.Entries).Select(entry =>
            new ZoneEmptyCrowdCandidate(entry.Object ?? throw new InvalidDataException("Crowd grid returned a non-actor entry."), entry.CachedPosition)).ToArray();
        var output = _owner.Actors.PuppetOutput ??
            throw new NotSupportedException("Crowd avoidance requires its actual per-actor time-passed producer.");
        var timePassed = output.CopyTimePassed(Actor);
        var now = _owner.Actors.FrameMicroseconds(); Require();
        if (!grids.IsCurrent(puppets) || !grids.IsCurrent(players))
            throw new NotSupportedException("The crowd's cached actor-grid publication retired before its update.");
        var result = _crowd.Update(new(position, Actor.AiObject.ViewDirection,
            Controls.MovementDirection, Controls.DesiredSpeed, Parameters.AvoidanceRadius,
            _owner.Motion.CopyPuppetAlertness(Actor) ?? throw new NotSupportedException("Crowd avoidance requires actual Puppet alertness."),
            (uint)UpdatePriority, UsesThreeDimensionalNavigation, fullUpdate, now, timePassed)
            { Actor1593 = Parameters.CrowdFlag177, Actor1594 = Parameters.CrowdFlag178 },
            candidates);
        Require(); Controls.MovementDirection = result.MovementDirection; Controls.DesiredSpeed = result.DesiredSpeed;
    }
    public void ResetTrace() => ResetPath();
    public void SetMovementPredictions(IReadOnlyList<ZonePredictivePathSample> predictions)
    { Require(); ArgumentNullException.ThrowIfNull(predictions); _predictions = Array.AsReadOnly(predictions.ToArray()); }
    public void Dispose()
    {
        if (IsDisposed) return; IsDisposed = true;
        try { ReleaseTraceTarget(); }
        finally { _predictiveFollower?.Dispose(); _crowd.Dispose(); _predictions = []; _owner.Retire(this); }
    }
}

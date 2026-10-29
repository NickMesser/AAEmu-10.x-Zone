using System.Numerics;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Ai;
using AAEmu.Zone.Content;
using AAEmu.Zone.Scene;

namespace AAEmu.Zone.Scripting;

/// <summary>The registration-time numeric/boolean fields actually consumed by path execution.</summary>
public sealed record ZoneManagedNpcMovementAbilitySnapshot(ZoneActorMovementSpeedTable Speeds,
    ZoneGroundTraceMovementAbility GroundTrace, float DirectionalReferenceMinimum, float DirectionalReferenceMaximum,
    float ManeuverThreshold, float PathRegenInterval, float PassRadius, bool UsesPathfinder,
    bool ThreeDimensionalMovement, bool PredictiveFollowing, bool FlyingCreature, bool UnderwaterCreature)
{
    /// <summary>Null means the model catalog needed to own this registration branch was not supplied.</summary>
    public ZoneManagedNpcLookAbility? Look { get; init; }
    /// <summary>392ADC00: typed avoidanceRadius plus the resolved pathRadius, actor1620.</summary>
    public float AvoidanceRadius { get; init; }
    //3927FEDC..3927FF0D runs after392ADC00 and copies the actual unit model's
    //descriptor144 bits2/8 into AIParams177/178 before RegisterAI.
    public bool CrowdFlag177 { get; init; }
    public bool CrowdFlag178 { get; init; }
    //392ADC00 installs these masks from the actual registered creature flags;
    //the pathType script read is not assigned to this field in this client.
    public ulong NavigationCapabilities => FlyingCreature ? 184UL : UnderwaterCreature ? 172UL : 134UL;
}

/// <summary>392ADC00's installed radians and instant-strafe flag, not live Lua lookups.</summary>
public sealed record ZoneManagedNpcLookAbility(float IdleTurnSpeed, float CombatTurnSpeed, bool FaceTargetInstantlyOnStrafing);

/// <summary>
///392ADC00 registration-time movement tables, after original Server.OnInit. Copies authored
///values, including the model-generated six-decimal speed triples; later Lua mutations do
///not silently replace installed actor parameters. AI.ChangeMovementAbility remains separate.
/// </summary>
public sealed class ZoneManagedNpcMovementAbility : IDisposable
{
    private static readonly string[] Stances = ["Relaxed", "Combat", "Stealth", "Crouch", "Prone", "Swim", "Fly"];
    private static readonly string[] Urgencies = ["Slow", "Walk", "Run", "Sprint"];
    private readonly ZoneManagedNpcActorScene _actors;
    private readonly ZoneManagedNpcActorMotion _motion;
    private readonly ZoneModelContent? _models;
    private readonly Dictionary<ZoneActorHandle, ZoneManagedNpcMovementAbilityLease> _live = [];
    public bool IsDisposed { get; private set; }
    public int Count => _live.Count;

    public ZoneManagedNpcMovementAbility(ZoneManagedNpcActorScene actors, ZoneManagedNpcActorMotion motion,
        ZoneModelContent? models = null)
    {
        _actors = actors ?? throw new ArgumentNullException(nameof(actors));
        _motion = motion ?? throw new ArgumentNullException(nameof(motion)); _models = models;
    }

    public ZoneManagedNpcMovementAbilityLease BindInitialParameters(ZoneManagedNpcScriptBinding binding)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        ArgumentNullException.ThrowIfNull(binding); binding.RequireCurrent(); _actors.Require(binding.Actor);
        if (_live.ContainsKey(binding.Actor.Handle)) throw new InvalidOperationException("Movement ability already belongs to this exact NPC generation.");
        using var ability = binding.Script.Get("AIMovementAbility") as ZoneLuaReference;
        using var properties = binding.Script.Get("Properties") as ZoneLuaReference;
        binding.RequireCurrent();
        if (ability is not { IsTable: true } || properties is not { IsTable: true })
            throw new NotSupportedException("NPC movement registration requires its actual AIMovementAbility and Properties tables.");
        using var sourceSpeeds = ability.Get("AIMovementSpeeds") as ZoneLuaReference;
        if (sourceSpeeds is not { IsTable: true })
            throw new NotSupportedException("NPC movement speed registration requires its original model-specific speed table.");
        var speeds = new List<ZoneActorMovementSpeeds>(28);
        var maximum = 5f; //392ADC00 initializes the look-ahead fallback maximum to5.
        foreach (var stance in Stances)
        {
            using var group = sourceSpeeds.Get(stance) as ZoneLuaReference;
            if (group is not { IsTable: true })
                throw new NotSupportedException($"Movement speed registration requires the actual {stance} table; constructor/fallback table filling is not admitted.");
            foreach (var urgency in Urgencies)
            {
                using var triplet = group.Get(urgency) as ZoneLuaReference;
                if (triplet is not { IsTable: true })
                    throw new NotSupportedException($"Movement speed registration requires the actual {stance}.{urgency} triple.");
                var normal = Number(triplet.Get(1), 0); var minimum = Number(triplet.Get(2), 0); var max = Number(triplet.Get(3), 0);
                speeds.Add(new(normal, minimum, max));
                if (!(normal <= maximum)) maximum = normal;
            }
        }
        var pathRadius = Number(ability.Get("pathRadius"), 1);
        var passRadius = Number(ability.Get("passRadius"), .4f);
        var unit = binding.Actor.Npc.Unit;
        if (unit.Scale.Multiplier != 1)
            throw new NotSupportedException("NPC initial movement registration requires its actual construction scale; live model scale changes need their own owner.");
        var model = _models?.Models.GetValueOrDefault(unit.Snapshot.ModelId);
        var radii = ZoneNpcMovementRadiusMath.Resolve(passRadius, pathRadius, unit.Scale.Scale,
            model, _models?.ActorForModel(unit.Snapshot.ModelId));
        passRadius = radii.PassRadius; pathRadius = radii.PathRadius;
        var lookAhead = Number(ability.Get("pathLookAhead"), 3);
        if (lookAhead < 0) lookAhead = maximum * .1f + pathRadius;
        //3929D410 gives these constructor fields;392ADC00 overwrites only successfully typed inputs.
        var ground = new ZoneGroundTraceMovementAbility(lookAhead, pathRadius,
            Number(ability.Get("pathSpeedLookAheadPerSpeed"), -1), Number(ability.Get("cornerSlowDown"), 0),
            Number(ability.Get("slopeSlowDown"), 1), Number(ability.Get("maxAccel"), float.MaxValue),
            Number(ability.Get("maxDecel"), float.MaxValue), true);
        var snapshot = new ZoneManagedNpcMovementAbilitySnapshot(new(speeds), ground,
            Number(ability.Get("directionalScaleRefSpeedMin"), -1), Number(ability.Get("directionalScaleRefSpeedMax"), -1),
            Number(ability.Get("maneuverTrh"), 0), Number(ability.Get("pathRegenIntervalDuringTrace"), 0), passRadius,
            Boolean(ability.Get("usePathfinder"), true), Boolean(ability.Get("b3DMove"), false),
            Boolean(ability.Get("usePredictiveFollowing"), false), Boolean(properties.Get("bFlyingCreature"), false),
            Boolean(properties.Get("bUnderwaterCreature"), false))
        {
            Look = _models is null ? null : ReadLook(binding, ability),
            AvoidanceRadius = Number(ability.Get("avoidanceRadius"), 0) + pathRadius,
            CrowdFlag177 = model?.Big ?? false,
            CrowdFlag178 = model?.ForceNavigateAroundObject ?? false,
        };
        binding.RequireCurrent(); _actors.Require(binding.Actor);
        var owner = new ZoneManagedNpcMovementAbilityLease(this, binding, snapshot);
        _live.Add(binding.Actor.Handle, owner);
        //3927F610/392ADC00 publish the actual scaled agent radius after parsing parameters.
        binding.Actor.AiObject.Radius = passRadius;
        return owner;
    }

    public ZoneManagedNpcMovementAbilitySnapshot CopyState(ZoneManagedNpcActor actor) => Require(actor).State;

    private ZoneManagedNpcLookAbility ReadLook(ZoneManagedNpcScriptBinding binding, ZoneLuaReference ability)
    {
        //392ADC00 v107 is the actual ActorModel lookup. A resolved descriptor overrides
        //both Lua turn speeds and the flag; the source only reads Lua on lookup failure.
        if (_models!.ActorForModel(binding.Actor.Npc.Unit.Snapshot.ModelId) is { } model)
        {
            var speed = model.Definition.TurnSpeed * .017453292f;
            return new(speed, speed, model.Definition.FaceTargetInstantly);
        }
        //3100891C preserves -1 degrees until a successfully typed script value replaces it.
        return new(Number(ability.Get("lookIdleTurnSpeed"), -1) * .017453292f,
            Number(ability.Get("lookCombatTurnSpeed"), -1) * .017453292f,
            Boolean(ability.Get("faceTargetInstantlyOnStrafing"), false));
    }

    /// <summary>31172DEC samples prior/body turn before executing this actor's goals.</summary>
    public void UpdateBodyTurn(ZoneManagedNpcActor actor)
    {
        var owner = Require(actor); var body = actor.AiObject.BodyDirection;
        var seconds = _actors.FrameTimeSeconds;
        var cross = owner.PreviousBodyDirection.X * body.Y - body.X * owner.PreviousBodyDirection.Y;
        var dot = body.X * owner.PreviousBodyDirection.X + owner.PreviousBodyDirection.Y * body.Y;
        owner.BodyTurnRate = seconds <= 0 ? 0 : MathF.Atan2(cross, dot) / seconds;
        owner.PreviousBodyDirection = body;
    }

    public ZoneActorMovementSpeeds GetMovementSpeeds(ZoneManagedNpcActor actor)
    {
        var owner = Require(actor); var input = _motion.CopyMovementSpeedState(actor);
        Require(actor);
        return owner.State.Speeds.Calculate(actor.Controls.MovementUrgency, input.Stance,
            owner.State.DirectionalReferenceMinimum, owner.State.DirectionalReferenceMaximum,
            owner.BodyTurnRate, actor.AiObject.BodyDirection, actor.AiObject.MoveDirection, input.ControllerAngle);
    }

    public ZoneManagedNpcMovementAbilityLease? Find(ZoneActorHandle actor) =>
        !IsDisposed && _live.TryGetValue(actor, out var current) && current.IsCurrent ? current : null;
    internal bool Owns(ZoneManagedNpcMovementAbilityLease owner) => !IsDisposed && !owner.IsDisposed &&
        _live.TryGetValue(owner.Binding.Actor.Handle, out var current) && ReferenceEquals(current, owner) && owner.Binding.IsCurrent &&
        ReferenceEquals(_actors.Find(owner.Binding.Actor.Handle), owner.Binding.Actor);
    internal void Retire(ZoneManagedNpcMovementAbilityLease owner)
    { if (_live.TryGetValue(owner.Binding.Actor.Handle, out var current) && ReferenceEquals(current, owner)) _live.Remove(owner.Binding.Actor.Handle); }
    private ZoneManagedNpcMovementAbilityLease Require(ZoneManagedNpcActor actor)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this); ArgumentNullException.ThrowIfNull(actor); _actors.Require(actor);
        return Find(actor.Handle) is { } owner && ReferenceEquals(owner.Binding.Actor, actor) ? owner :
            throw new ObjectDisposedException(nameof(ZoneManagedNpcMovementAbilityLease), "This actor has no current installed movement parameters.");
    }
    private static bool Boolean(object? value, bool fallback)
    {
        try { return ZoneLuaScriptAny.TryBoolean(value, out var converted) ? converted : fallback; }
        finally { if (value is ZoneLuaReference reference) reference.Dispose(); }
    }
    private static float Number(object? value, float fallback)
    {
        try { return value switch
            { double number => (float)number, float number => number, long number => number, int number => number, _ => fallback }; }
        finally { if (value is ZoneLuaReference reference) reference.Dispose(); }
    }
    public void Dispose()
    { if (IsDisposed) return; IsDisposed = true; foreach (var owner in _live.Values.ToArray()) owner.Dispose(); }
}

public sealed class ZoneManagedNpcMovementAbilityLease : IDisposable
{
    private readonly ZoneManagedNpcMovementAbility _owner;
    internal ZoneManagedNpcScriptBinding Binding { get; }
    public ZoneManagedNpcMovementAbilitySnapshot State { get; }
    internal Vector3 PreviousBodyDirection;
    internal float BodyTurnRate;
    public bool IsDisposed { get; private set; }
    public bool IsCurrent => _owner.Owns(this);
    internal ZoneManagedNpcMovementAbilityLease(ZoneManagedNpcMovementAbility owner, ZoneManagedNpcScriptBinding binding,
        ZoneManagedNpcMovementAbilitySnapshot state) => (_owner, Binding, State) = (owner, binding, state);
    public void Dispose() { if (IsDisposed) return; IsDisposed = true; _owner.Retire(this); }
}

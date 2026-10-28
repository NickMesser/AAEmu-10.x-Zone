using AAEmu.Zone.Content;
using AAEmu.Zone.Protocol;
using AAEmu.Zone.Protocol.Units;
using AAEmu.Zone.Scene;

namespace AAEmu.Zone.Npcs;

public sealed record ZoneNpcAnimationAction(uint Id, int ActionState);

/// <summary>
/// Copied construction facts. Driver presence must come from the actual actor construction;
/// absence is not inferred from missing physics or a model filename. NativeOverrideTypes is
/// the actual NpcDesc+464 child vector's entry+4 values; unknown inventory must not be empty.
/// </summary>
public sealed record ZoneNpcPostureDriverProfile(ZoneModelDefinition Model, bool AnimationDriverPresent,
    IReadOnlyList<int> NativeOverrideTypes, ActorPosture InitialPosture, bool InitiallyLooted = false);

/// <summary>
/// Original default-posture selection, actor-model posture, animation-action cache and World
/// notification. CurrentAnimationId is the actual consumer used by ZoneNpcPostureState.
/// This does not run the animation graph, state0 signal action, or state1 item/tool action.
/// </summary>
public sealed class ZoneManagedNpcPostureState : IDisposable
{
    private readonly ZoneNpcPostureContent _content;
    private readonly Func<uint, ZoneNpcAnimationAction?> _actions;
    private readonly Func<float> _hour;
    private readonly Action<ZoneFrame> _send;
    private readonly Dictionary<Actors.ZoneActorHandle, ZoneManagedNpcPostureLease> _actors = [];
    public bool IsDisposed { get; private set; }
    public ZoneManagedNpcPostureState(ZoneNpcPostureContent content, Func<uint, ZoneNpcAnimationAction?> resolveAction,
        Func<float> hour, Action<ZoneFrame> send)
    {
        _content = content ?? throw new ArgumentNullException(nameof(content)); _actions = resolveAction ?? throw new ArgumentNullException(nameof(resolveAction));
        _hour = hour ?? throw new ArgumentNullException(nameof(hour)); _send = send ?? throw new ArgumentNullException(nameof(send));
    }

    public ZoneManagedNpcPostureLease Bind(ZoneManagedNpcActor actor, ZoneNpcPostureDriverProfile profile)
    {
        Check(); ArgumentNullException.ThrowIfNull(actor); ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(profile.Model);
        ArgumentNullException.ThrowIfNull(profile.NativeOverrideTypes); ArgumentNullException.ThrowIfNull(profile.InitialPosture);
        if (!actor.HasActor || profile.Model.Id != actor.Npc.Unit.Snapshot.ModelId || profile.Model.Kind != ZoneModelKind.Actor)
            throw new InvalidOperationException("Posture requires the exact adopted actor's resolved wire model, independently of NPC template metadata.");
        if (_actors.TryGetValue(actor.Handle, out var previous))
        {
            if (previous.IsCurrent) throw new InvalidOperationException("This actor generation already owns its model posture.");
            previous.Dispose();
        }
        var owner = new ZoneManagedNpcPostureLease(this, actor, profile.AnimationDriverPresent, profile.NativeOverrideTypes.Contains(1));
        Apply(owner, profile.InitialPosture, profile.InitiallyLooted, false);
        _actors.Add(actor.Handle, owner); return owner;
    }

    /// <summary>390B34D0: strict start-hour selection; enabled equal-current suppresses dispatch, disabled never does.</summary>
    public void SetDefaultPosture(ZoneManagedNpcActor actor, bool enabled)
    {
        var owner = Require(actor);
        var postures = _content.FindPostures(actor.Npc.Template.NpcPostureSetId);
        if (postures is null) return;
        var hour = _hour(); Require(actor);
        var desired = ZoneNpcPostureState.Select(postures, hour);
        if (desired is null || desired.AnimationId == 0) return;
        if (owner.HasNativeOverride) return;
        if (enabled && owner.CurrentAnimationId == desired.AnimationId) return;
        var posture = new ActorPosture(desired.AnimationId, enabled);
        Apply(owner, posture, false, false); Require(actor);
        _send(PostureFrame(actor.Npc.ObjectId, posture, false));
    }

    /// <summary>WZ393631B0 ->3935C470 applies through the same model/cache setter, without an outgoing echo.</summary>
    public void ApplyFromWorld(ZoneManagedNpcActor actor, ActorPosture posture, bool looted, bool force = false) =>
        Apply(Require(actor), posture, looted, force);

    public ZoneManagedNpcPostureLease? Find(ZoneManagedNpcActor actor) => !IsDisposed &&
        _actors.TryGetValue(actor.Handle, out var owner) && owner.IsCurrent && ReferenceEquals(owner.Actor, actor) ? owner : null;

    private void Apply(ZoneManagedNpcPostureLease owner, ActorPosture posture, bool looted, bool force)
    {
        ArgumentNullException.ThrowIfNull(posture);
        if (!force && owner.Posture == posture && owner.Looted == looted) return;
        ZoneNpcAnimationAction? action = null;
        //3945DDB0 checks controller+24 before resolving the descriptor. Its caller39263A90
        //forces a4=0, so states2..8 update only their real cache field; no graph tick is omitted.
        if (owner.AnimationDriverPresent)
        {
            action = _actions(posture.AnimationAction);
            if (!owner.IsCurrent) throw new InvalidOperationException("The posture actor retired while resolving its action.");
            if (action is null || action.Id != posture.AnimationAction)
                throw new NotSupportedException($"Animation action {posture.AnimationAction} is not resolved for the actual posture owner.");
            if (action.ActionState is < 2 or > 8)
                throw new NotSupportedException($"Animation action {action.Id} state {action.ActionState} requires its signal/item/tool consumer.");
        }
        owner.Posture = posture; owner.Looted = looted;
        if (action is not null) owner.Actions[action.ActionState] = posture.Activate ? posture.AnimationAction : 0;
    }

    /// <summary>3935C760/39362730: object bc then type4, looted, uint32 animation action and bool activate.</summary>
    public static ZoneFrame PostureFrame(uint objectId, ActorPosture posture, bool looted)
    {
        var writer = new ZoneWriter(); writer.WriteBc(objectId); writer.WriteByte(4); writer.WriteBoolean(looted);
        writer.WriteUInt32(posture.AnimationAction); writer.WriteBoolean(posture.Activate);
        return new(ZwOpcodes.UnitModelPostureChanged, writer.ToArray());
    }
    private ZoneManagedNpcPostureLease Require(ZoneManagedNpcActor actor)
    { Check(); return Find(actor) ?? throw new InvalidOperationException("Model posture requires its exact live owned actor generation."); }
    private void Check() => ObjectDisposedException.ThrowIf(IsDisposed, this);
    internal void Retire(ZoneManagedNpcPostureLease owner)
    { if (_actors.TryGetValue(owner.Actor.Handle, out var current) && ReferenceEquals(current, owner)) _actors.Remove(owner.Actor.Handle); }
    public void Dispose()
    { if (IsDisposed) return; IsDisposed = true; foreach (var owner in _actors.Values.ToArray()) owner.Dispose(); }
}

/// <summary>Actual actor-model posture and nine original action-state cache slots, owned by one generation.</summary>
public sealed class ZoneManagedNpcPostureLease : IZoneNpcPostureModel, IDisposable
{
    private readonly ZoneManagedNpcPostureState _owner;
    internal ZoneManagedNpcActor Actor { get; }
    internal bool HasNativeOverride { get; }
    internal uint[] Actions { get; } = new uint[9]; //3945F390 initializes all slots to invalid0.
    public bool AnimationDriverPresent { get; }
    public ActorPosture Posture { get; internal set; } = new(0, false);
    public bool Looted { get; internal set; }
    public bool IsDisposed { get; private set; }
    public bool IsCurrent => !IsDisposed && !_owner.IsDisposed && Actor.HasActor;
    public ZoneModelKind Kind => ZoneModelKind.Actor;
    public uint CurrentAnimationId
    {
        get
        {
            if (!IsCurrent) throw new InvalidOperationException("The model posture generation is retired.");
            for (var state = 3; state < 9; state++) if (Actions[state] != 0) return Actions[state];
            //3945BB70 falls back to the actual initialized constant3A5281F0=2, not invalid0.
            return Actions[2] != 0 ? Actions[2] : 2u;
        }
    }
    internal ZoneManagedNpcPostureLease(ZoneManagedNpcPostureState owner, ZoneManagedNpcActor actor, bool present, bool hasOverride)
    { _owner = owner; Actor = actor; AnimationDriverPresent = present; HasNativeOverride = hasOverride; }
    public void Dispose() { if (IsDisposed) return; IsDisposed = true; Array.Clear(Actions); _owner.Retire(this); }
}

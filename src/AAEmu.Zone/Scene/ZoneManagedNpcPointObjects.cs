using System.Numerics;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Ai;

namespace AAEmu.Zone.Scene;

/// <summary>One native dummy allocation; EntityId remains0, independently of its owner and index key.</summary>
public sealed class ZoneManagedNpcPointObject
{
    public string Name { get; }
    public int SubType { get; }
    public ZoneAiObjectState State { get; } = new();
    public Vector3 PreviousPosition { get; private set; }
    public bool PositionChanged { get; private set; }
    internal ZoneManagedNpcPointObject(string name, int subType) { Name = name; SubType = subType; }
    internal void SetTransform(Vector3 position, Vector3 direction)
    {
        //Generic dummy has no physical proxy or entity.3103DA78's direct branch copies all
        //three directions, keeps previous position, and tests component changes against.05.
        PreviousPosition = State.Position; State.Position = position;
        State.BodyDirection = direction; State.MoveDirection = direction;
        var squared = (direction.X * direction.X + direction.Y * direction.Y) + direction.Z * direction.Z;
        var inverse = squared <= 1.1920928955078125e-7f ? 0 : 1f / MathF.Sqrt(squared);
        State.ViewDirection = squared <= 1.1920928955078125e-7f ? Vector3.Zero :
            new(direction.X * inverse, direction.Y * inverse, direction.Z * inverse);
        PositionChanged = MathF.Abs(PreviousPosition.X - position.X) > .05f ||
            MathF.Abs(PreviousPosition.Y - position.Y) > .05f || MathF.Abs(PreviousPosition.Z - position.Z) > .05f;
    }
}

/// <summary>
/// CPipeUser constructor31163974 creates reference(subtype2) and look(subtype3) dummy objects.
/// CreateDummyObject3105AF48 indexes them in its separate dummy container, not the nearest-object
/// type tree. These are actual target/goal inputs; this owner never steps physics or turns a body.
/// </summary>
public sealed class ZoneManagedNpcPointObjects : IDisposable
{
    private readonly ZoneManagedNpcActorScene _scene;
    private readonly Dictionary<ZoneActorHandle, ZoneManagedNpcPointLease> _actors = [];
    private bool _notifyingPosition, _notifyingDirection;
    public bool IsDisposed { get; private set; }
    public int Count => _actors.Count;
    public int DummyCount => _actors.Count * 2;
    public ZoneManagedNpcPointObjects(ZoneManagedNpcActorScene scene) => _scene = scene ?? throw new ArgumentNullException(nameof(scene));

    public ZoneManagedNpcPointLease Bind(ZoneManagedNpcActor actor, string actualAiName)
    {
        Check(); ArgumentNullException.ThrowIfNull(actor); ArgumentNullException.ThrowIfNull(actualAiName);
        _scene.Require(actor);
        if (_actors.ContainsKey(actor.Handle)) throw new InvalidOperationException("The exact pipe user already owns its dummy targets.");
        var owner = new ZoneManagedNpcPointLease(this, actor, ZoneAiSignalName.CString(actualAiName));
        _actors.Add(actor.Handle, owner); return owner;
    }

    public ZoneManagedNpcPointLease Require(ZoneManagedNpcActor actor)
    {
        Check(); _scene.Require(actor);
        if (!_actors.TryGetValue(actor.Handle, out var owner) || !ReferenceEquals(owner.Actor, actor) || owner.IsDisposed)
            throw new InvalidOperationException("Reference/look operations require the exact pipe user's owned dummy inventory.");
        return owner;
    }
    public ZoneAiObjectState GetReferencePoint(ZoneManagedNpcActor actor) => Require(actor).Reference.State;
    public ZoneAiObjectState GetLookPoint(ZoneManagedNpcActor actor) => Require(actor).Look.State;
    public void SetReferenceRadius(ZoneManagedNpcActor actor, float radius)
    {
        if (!float.IsFinite(radius)) throw new ArgumentException("Reference radius must be finite.");
        Require(actor).Reference.State.Radius = radius; //30525520 -> CAIObject.v96/3103CAF8 writes+192.
    }
    public void SetReferencePosition(ZoneManagedNpcActor actor, Vector3 position)
    { RequireFinite(position); MoveReference(Require(actor), position, actor.AiObject.MoveDirection, false); }
    public void SetReferenceDirection(ZoneManagedNpcActor actor, Vector3 direction)
    {
        RequireFinite(direction); var owner = Require(actor);
        //305252F0 leaves an exactly zero vector unchanged but returns success. Normalize
        //with its FLT_MIN term; tiny nonzero vectors must not be replaced by a unit fallback.
        if (!(MathF.Abs(direction.X) > 0 || MathF.Abs(direction.Y) > 0 || MathF.Abs(direction.Z) > 0)) return;
        var squared = (direction.Y * direction.Y + direction.X * direction.X) + direction.Z * direction.Z;
        var length = (float)Math.Sqrt((double)squared + 1.1754943508222875e-38);
        var inverse = (float)(1d / length);
        MoveReference(owner, owner.Reference.State.Position, new(direction.X * inverse, direction.Y * inverse, direction.Z * inverse), true);
    }
    public bool IsSimilarReferenceDirection(ZoneManagedNpcActor actor)
    {
        var owner = Require(actor);
        var reference = Horizontal(owner.Reference.State.MoveDirection, true);
        var body = Horizontal(actor.AiObject.BodyDirection, false);
        return ((body.Y * reference.Y + body.X * reference.X) + body.Z * reference.Z) > .98000002f;
    }
    public void SetLookAtPoint(ZoneManagedNpcActor actor, Vector3 position, bool force = false)
    {
        RequireFinite(position); var owner = Require(actor);
        if (!force && owner.ForcedLook) return;
        owner.ForcedLook = force; owner.Look.SetTransform(position, Vector3.UnitX);
        actor.LookState.UsePointTarget(owner.Look.State);
    }
    public ZoneAiObjectState? ResolveSpecial(ZoneManagedNpcActor actor, string name)
    {
        var owner = Require(actor);
        return ZoneAiSignalName.CString(name) switch
        {
            "self" => actor.AiObject,
            "refpoint" => owner.Reference.State,
            "lookat_target" => actor.Controls.LooseAttention ? actor.LookState.LooseAttentionTarget as ZoneAiObjectState : null,
            _ => throw ZoneManagedNpcActorScene.Missing($"named/special target '{name}'"),
        };
    }
    private void MoveReference(ZoneManagedNpcPointLease owner, Vector3 position, Vector3 direction, bool explicitDirection)
    {
        RequireFinite(direction);
        var actor = owner.Actor;
        var previous = owner.Reference.State.Position;
        var notifying = explicitDirection ? _notifyingDirection : _notifyingPosition;
        var changed = !notifying && actor.Entry.Phase.Pipes.CurrentPipe is not null &&
            (MathF.Abs(previous.X - position.X) > .001f || MathF.Abs(previous.Y - position.Y) > .001f || MathF.Abs(previous.Z - position.Z) > .001f);
        owner.Reference.SetTransform(position, direction);
        //Primary event19 dispatch is31158A54 ->31012508, the AI recorder. Listener event7
        //is separate, innermost-only and uses the existing AI-system-wide notification guard.
        actor.ActorEvent(19, "reference-point"); Require(actor);
        if (!changed) return;
        if (explicitDirection) _notifyingDirection = true; else _notifyingPosition = true;
        try
        {
            if (actor.Entry.Phase.Pipes.CurrentPipe?.Innermost is { } pipe)
                actor.Entry.Phase.Pipes.Notify(pipe.EventId, ZoneGoalPipeEvent.ReferencePointMoved);
        }
        finally { if (explicitDirection) _notifyingDirection = false; else _notifyingPosition = false; }
        Require(actor);
    }
    private static Vector3 Horizontal(Vector3 direction, bool xFirst)
    {
        var squared = xFirst ? direction.X * direction.X + direction.Y * direction.Y : direction.Y * direction.Y + direction.X * direction.X;
        if (squared <= 1.1920928955078125e-7f) return Vector3.Zero;
        var inverse = (float)(1d / MathF.Sqrt(squared));
        return new(direction.X * inverse, direction.Y * inverse, 0f * inverse);
    }
    internal void Retire(ZoneManagedNpcPointLease owner)
    {
        if (!_actors.TryGetValue(owner.Actor.Handle, out var current) || !ReferenceEquals(owner, current)) return;
        _actors.Remove(owner.Actor.Handle);
        owner.Reference.State.Enabled = false; owner.Look.State.Enabled = false;
        _scene.RetirePoint(owner.Reference.State); _scene.RetirePoint(owner.Look.State);
    }
    private void Check() => ObjectDisposedException.ThrowIf(IsDisposed, this);
    private static void RequireFinite(Vector3 v)
    { if (!float.IsFinite(v.X) || !float.IsFinite(v.Y) || !float.IsFinite(v.Z)) throw new ArgumentException("Point targets require finite scene coordinates/directions."); }
    public void Dispose()
    { if (IsDisposed) return; IsDisposed = true; foreach (var owner in _actors.Values.ToArray()) owner.Dispose(); }
}

public sealed class ZoneManagedNpcPointLease : IDisposable
{
    private readonly ZoneManagedNpcPointObjects _scene;
    public ZoneManagedNpcActor Actor { get; }
    public ZoneManagedNpcPointObject Reference { get; }
    public ZoneManagedNpcPointObject Look { get; }
    public bool IsDisposed { get; private set; }
    internal bool ForcedLook;
    internal ZoneManagedNpcPointLease(ZoneManagedNpcPointObjects scene, ZoneManagedNpcActor actor, string name)
    {
        _scene = scene; Actor = actor;
        Reference = new(name + "_RefPoint", 2);
        Look = new(name + "_LookAt", 3);
        //31163974 initially constructs at zero CAIActor vectors, before the separate live
        //actor position/direction producers.31159444 subsequently applies these names.
        Look.SetTransform(Vector3.Zero, Vector3.UnitX);
    }
    public void Dispose() { if (IsDisposed) return; IsDisposed = true; _scene.Retire(this); }
}

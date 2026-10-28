using AAEmu.Zone.Actors;
using AAEmu.Zone.Hosting;
using AAEmu.Zone.Npcs;
using AAEmu.Zone.Protocol;
using AAEmu.Zone.Protocol.Messages;
using AAEmu.Zone.Protocol.Units;
using AAEmu.Zone.Units;

namespace AAEmu.Zone.Scene;

/// <summary>
/// Generation-owned393405B0 sampler and the1000-row3936D860 buffer. The actual CActor motion
/// owner supplies metadata; this owner never derives it from wishes or uses player suppression.
/// </summary>
public sealed class ZoneManagedNpcReplication(ZoneUnitRegistry units, ZoneManagedNpcActorScene actors,
    ZoneManagedNpcActorMotion motion, ZoneEngineCVars cvars, Func<uint> random,
    bool localOnWire, Action<string>? trace = null) : IDisposable
{
    private readonly ZoneUnitRegistry _units = units ?? throw new ArgumentNullException(nameof(units));
    private readonly ZoneManagedNpcActorScene _actors = actors ?? throw new ArgumentNullException(nameof(actors));
    private readonly ZoneManagedNpcActorMotion _motion = motion ?? throw new ArgumentNullException(nameof(motion));
    private readonly ZoneEngineCVars _cvars = cvars ?? throw new ArgumentNullException(nameof(cvars));
    private readonly Func<uint> _random = random ?? throw new ArgumentNullException(nameof(random));
    private readonly Dictionary<ZoneActorHandle, ZoneManagedNpcReplicationLease> _live = [];
    private readonly List<(ZoneManagedNpcReplicationLease Owner, byte[] Bytes)> _pending = [];
    private bool _busy;
    public bool IsDisposed { get; private set; }
    public int Count => _live.Count;
    public int PendingCount => _pending.Count;
    public ulong DroppedMovementCount { get; private set; }
    public ulong SampledMovementCount { get; private set; }

    public ZoneManagedNpcReplicationLease Bind(ZoneManagedNpcActor actor)
    {
        Check(); ArgumentNullException.ThrowIfNull(actor); _actors.Require(actor);
        if (_live.ContainsKey(actor.Handle)) throw new InvalidOperationException("This NPC generation already owns its movement cache.");
        var disabledRandom = _random(); _actors.Require(actor);
        var standingRandom = _random(); _actors.Require(actor);
        var owner = new ZoneManagedNpcReplicationLease(this, actor, new(disabledRandom, standingRandom));
        _live.Add(actor.Handle, owner); return owner;
    }

    public bool IsBound(ZoneManagedNpcActor actor) => !IsDisposed &&
        _live.TryGetValue(actor.Handle, out var owner) && ReferenceEquals(owner.Actor, actor) && owner.IsCurrent;
    public ZoneNpcMovementReplicationCache CopyState(ZoneManagedNpcActor actor) => Require(actor).State.CopyState();
    public void Reset(ZoneManagedNpcActor actor) => Require(actor).State.Reset(ZoneNpcMovementReplicationSettings.From(_cvars));

    public bool Sample(ZoneManagedNpcActor actor, ZoneMovementFrame frame)
    {
        var owner = Require(actor);
        if (_busy) throw new InvalidOperationException("NPC replication cannot reenter its sampled publication boundary.");
        _busy = true;
        try
        {
            var now = frame.LocalGameMilliseconds ??
                throw new NotSupportedException("NPC replication requires this frame's actual local CTimer0 sample.");
            var sample = _motion.CopyReplicationState(actor); Require(actor);
            if ((sample.ActorFlags & 0x8160) != 0)
                throw new NotSupportedException("NPC movement requires its actual ground/hanging/push/max-pushed field producers.");
            //Validate before consuming a send; unsupported encoding cannot partially commit a cache.
            var heading = UnitHeading.FromQuaternion(sample.Body.Status.Orientation);
            var wirePosition = UnitMovementQuantization.Position(localOnWire
                ? ZoneMovementPoseMath.ToUnitWorldPosition(sample.Body.Status.Position) : actor.Npc.Unit.Pose.Position);
            var active = actor.Npc.Activation.IsActive;
            var velocity = UnitMovementQuantization.ActorVelocity(active ? sample.ActorVelocity : default);
            var delta = UnitMovementQuantization.ActorDelta(active ? sample.Delta : default);
            var fall = UnitMovementQuantization.ActorFallVelocity(sample.FallVelocity);
            var count = (ulong)_units.Units.Values.Count(unit => unit.Snapshot.Identity.Kind == ZoneUnitKind.Character);
            var decision = owner.State.Sample(new(sample.Body.Status.Position, sample.Body.Status.Orientation,
                sample.ActorVelocity, sample.Stance, sample.ControllerId, sample.ControllerPhase,
                sample.KeepSyncing, sample.GuardedAllowStrafing, sample.ModelCanReplicate), active, now, count,
                ZoneNpcMovementReplicationSettings.From(_cvars));
            if (!decision.Send) return false;
            //392A48D0 runs after cache commit, including when the following fixed buffer drops.
            _motion.ResetControllerAfterReplication(actor); Require(actor);
            SampledMovementCount++;
            if (_pending.Count == 1000)
            {
                DroppedMovementCount++;
                trace?.Invoke($"dropped movement unit {actor.Npc.ObjectId} - exceeded 1000");
                return false;
            }
            var movement = new UnitMovementData(1, sample.Timestamp,
                (byte)(sample.MovementFlags | decision.AdditionalMovementFlags), sample.ControllerId, sample.ControllerPhase,
                wirePosition, velocity, new(heading.AxisX, heading.AxisY, heading.AxisZ),
                new UnitActorMovement(delta, sample.Stance, sample.Alertness, sample.ActorFlags, fall));
            _pending.Add((owner, new UnitMovement(actor.Npc.ObjectId, movement).Write()));
            return true;
        }
        finally { _busy = false; }
    }

    /// <summary>3936D860's pending rows drain at the existing global movement-buffer phase.</summary>
    public int Flush()
    {
        Check();
        if (_busy) throw new InvalidOperationException("NPC replication cannot flush during a sample.");
        var live = _pending.Where(entry => entry.Owner.IsCurrent).ToArray(); _pending.Clear();
        if (live.Length == 0) return 0;
        var writer = new ZoneWriter(); writer.WriteInt32(live.Length);
        foreach (var entry in live) writer.WriteBytes(entry.Bytes);
        var body = writer.ToArray();
        if (body.Length > ZoneFrame.MaxBodySize) throw new InvalidOperationException("NPC movement exceeded the native fixed-buffer wire budget.");
        return _units.QueueNotification(new(ZwOpcodes.UnitMovements, body), this) ? live.Length : 0;
    }

    private ZoneManagedNpcReplicationLease Require(ZoneManagedNpcActor actor)
    {
        Check();
        return IsBound(actor) ? _live[actor.Handle] :
            throw new ObjectDisposedException(nameof(ZoneManagedNpcReplicationLease), "The exact NPC movement-cache generation retired.");
    }
    internal bool Owns(ZoneManagedNpcReplicationLease owner) => !IsDisposed &&
        _live.TryGetValue(owner.Actor.Handle, out var current) && ReferenceEquals(current, owner) &&
        ReferenceEquals(_actors.Find(owner.Actor.Handle), owner.Actor) && _units.IsMovementCurrent(owner.Actor.Npc.Unit);
    internal void Retire(ZoneManagedNpcReplicationLease owner)
    {
        if (_live.TryGetValue(owner.Actor.Handle, out var current) && ReferenceEquals(current, owner)) _live.Remove(owner.Actor.Handle);
        _pending.RemoveAll(entry => ReferenceEquals(entry.Owner, owner));
    }
    private void Check() => ObjectDisposedException.ThrowIf(IsDisposed, this);
    public void Dispose()
    {
        if (IsDisposed) return; IsDisposed = true;
        foreach (var owner in _live.Values.ToArray()) owner.Dispose();
        _pending.Clear(); _units.DiscardNotifications(this);
    }
}

public sealed class ZoneManagedNpcReplicationLease : IDisposable
{
    private readonly ZoneManagedNpcReplication _owner;
    internal ZoneManagedNpcActor Actor { get; }
    internal ZoneNpcMovementReplicationState State { get; }
    public bool IsDisposed { get; private set; }
    public bool IsCurrent => !IsDisposed && _owner.Owns(this);
    internal ZoneManagedNpcReplicationLease(ZoneManagedNpcReplication owner, ZoneManagedNpcActor actor, ZoneNpcMovementReplicationState state)
    { _owner = owner; Actor = actor; State = state; }
    public void Dispose() { if (IsDisposed) return; IsDisposed = true; _owner.Retire(this); }
}

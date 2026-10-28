using System.Runtime.CompilerServices;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Ai;
using AAEmu.Zone.Scripting;

namespace AAEmu.Zone.Ai
{
    /// <summary>Constructor/signal ownership readiness, independently of full AI gameplay capability.</summary>
    public interface IZoneManagedAiActorReadiness
    {
        bool IsReady { get; }
    }
}

namespace AAEmu.Zone.Hosting
{
    /// <summary>
    /// Scene-thread replacement scheduling: Enqueue allocates only a pending actor. Pump performs
    /// at most the requested number of FIFO work entries that existed at its initial sequence
    /// boundary. Callback-enqueued actors wait for the next Pump; canceled nodes cost no scan.
    /// </summary>
    public sealed class ZoneManagedNpcAdmissionQueue : IDisposable
    {
        private static readonly ConditionalWeakTable<IZoneManagedAiActor, object> KnownActors = new();
        private static readonly object ActorGate = new(), ActorMarker = new();
        private readonly Func<ZoneActorHandle, IZoneManagedAiActor> _create;
        private readonly Func<ZoneActorHandle, bool> _exactCurrent;
        private readonly Action<ZoneActorHandle, Exception> _refuse;
        private readonly Action<ZoneActorHandle>? _admitted;
        private readonly Dictionary<ZoneActorHandle, PendingActor> _actors = [];
        private readonly Dictionary<uint, PendingActor> _byObjectId = [];
        private readonly HashSet<ZoneActorHandle> _used = [];
        private readonly HashSet<uint> _reserving = [];
        private readonly LinkedList<PendingActor> _pending = new();
        private long _sequence;
        private int _ready;
        private bool _pumping;
        public bool IsDisposed { get; private set; }
        public int Count => _actors.Count;
        public int ReadyCount => _ready;
        /// <summary>Queued plus currently constructing entries, excluding ready or canceled owners.</summary>
        public int PendingCount => Count - ReadyCount;
        public ulong RefusedCount { get; private set; }

        /// <param name="exactCurrent">True only for this exact still-admissible generation, not a numeric Unit-ID match.</param>
        /// <param name="refuse">Must retire the exact provider/catalog admission so exactCurrent(handle) becomes false.</param>
        /// <param name="admitted">Optional exact-generation scheduling notification after readiness commits. Cancellation from this callback wins.</param>
        public ZoneManagedNpcAdmissionQueue(Func<ZoneActorHandle, IZoneManagedAiActor> create,
            Func<ZoneActorHandle, bool> exactCurrent, Action<ZoneActorHandle, Exception> refuse,
            Action<ZoneActorHandle>? admitted = null)
        {
            _create = create ?? throw new ArgumentNullException(nameof(create));
            _exactCurrent = exactCurrent ?? throw new ArgumentNullException(nameof(exactCurrent));
            _refuse = refuse ?? throw new ArgumentNullException(nameof(refuse));
            _admitted = admitted;
        }

        public IZoneManagedAiActor Enqueue(ZoneActorHandle actor)
        {
            Check();
            if (actor.UnitObjectId == 0 || actor.Generation == 0) throw new ArgumentException("NPC admission requires a nonzero exact generation.", nameof(actor));
            if (_used.Contains(actor) || !_reserving.Add(actor.UnitObjectId))
                throw new InvalidOperationException("An admission generation cannot be reused or recursively enqueued; re-entry requires a fresh generation.");
            try
            {
                if (!_exactCurrent(actor)) throw new InvalidOperationException("NPC admission requires an exact live generation.");
                Check();
                //A reused numeric identity may supersede only a proven stale generation.
                if (_byObjectId.TryGetValue(actor.UnitObjectId, out var previous))
                {
                    if (Current(previous)) throw new InvalidOperationException("The numeric NPC identity still has another live admission generation.");
                    previous.Dispose();
                }
                Check();
                if (!_exactCurrent(actor)) throw new InvalidOperationException("NPC admission retired during generation validation.");
                Check();
                if (_sequence == long.MaxValue) throw new InvalidOperationException("NPC admission sequence numbers are exhausted.");
                var pending = new PendingActor(this, actor, ++_sequence);
                _actors.Add(actor, pending); _byObjectId.Add(actor.UnitObjectId, pending); _used.Add(actor);
                pending.Node = _pending.AddLast(pending);
                return pending;
            }
            finally { _reserving.Remove(actor.UnitObjectId); }
        }

        /// <summary>
        /// Returns processed work entries, including stale cancellations and expected refusals.
        /// Each consumes one slot, so stale bursts cannot create an unbounded scan in one frame.
        /// Expected content/capability failures retire through the consumer; programming errors
        /// cancel the entry and propagate. No queue policy catches arbitrary exceptions as success.
        /// </summary>
        public int Pump(int maximumAdmissions)
        {
            Check();
            if (maximumAdmissions < 0) throw new ArgumentOutOfRangeException(nameof(maximumAdmissions));
            if (_pumping || _reserving.Count != 0) throw new InvalidOperationException("NPC admissions cannot recursively pump from a queue callback.");
            _pumping = true;
            var boundary = _sequence; var processed = 0;
            try
            {
                while (!IsDisposed && processed < maximumAdmissions && _pending.First is { } node && node.Value.Sequence <= boundary)
                {
                    var entry = node.Value;
                    _pending.Remove(node); entry.Node = null; processed++;
                    try
                    {
                        if (!Current(entry)) { entry.Dispose(); continue; }
                        var actor = _create(entry.Actor) ?? throw new InvalidOperationException("The NPC admission factory returned no actor.");
                        if (actor is PendingActor)
                            throw new InvalidOperationException("An admission factory must return its real actor, not another queue's pending wrapper.");
                        lock (ActorGate)
                        {
                            if (KnownActors.TryGetValue(actor, out _))
                                throw new InvalidOperationException("The NPC admission factory reused an actor already transferred to another owner.");
                            KnownActors.Add(actor, ActorMarker);
                        }
                        //The factory may have canceled this wrapper before returning its new owner.
                        //That return still transfers ownership and must be collected exactly once.
                        if (entry.Disposed) { actor.Dispose(); continue; }
                        entry.Candidate = actor; //Cancellation callbacks now own its destruction.
                        if (!Current(entry)) { entry.Dispose(); continue; }
                        if (actor is IZoneManagedAiActorReadiness readiness && !readiness.IsReady)
                            throw new InvalidOperationException("The NPC factory returned an actor whose constructor owner is still pending.");
                        if (!Current(entry)) { entry.Dispose(); continue; }
                        entry.ReplayControls();
                        if (!Current(entry)) { entry.Dispose(); continue; }
                        entry.Ready = true; _ready++;
                        _admitted?.Invoke(entry.Actor);
                        if (!Current(entry)) entry.Dispose();
                    }
                    catch (Exception error)
                    {
                        //Always release staged ownership before invoking external refusal handlers.
                        var wasCurrent = false;
                        Exception? validation = null;
                        try { wasCurrent = Current(entry); }
                        catch (Exception failure) { validation = failure; }
                        try { entry.Dispose(); }
                        catch (Exception cleanup)
                        { throw new AggregateException(validation is null ? [error, cleanup] : new[] { error, validation, cleanup }); }
                        if (validation is not null) throw new AggregateException(error, validation);
                        if (!wasCurrent && error is ObjectDisposedException) continue;
                        if (!Expected(error)) throw;
                        if (!wasCurrent || IsDisposed) continue;
                        if (RefusedCount != ulong.MaxValue) RefusedCount++;
                        _refuse(entry.Actor, error);
                        if (!IsDisposed && _exactCurrent(entry.Actor))
                            throw new InvalidOperationException("The NPC refusal consumer did not retire its exact provider/catalog admission.", error);
                    }
                }
                return processed;
            }
            finally { _pumping = false; }
        }

        public bool Remove(ZoneActorHandle actor)
        {
            if (!_actors.TryGetValue(actor, out var entry)) return false;
            entry.Dispose(); return true;
        }
        private bool Current(PendingActor entry)
        {
            if (IsDisposed || entry.Disposed || !_actors.TryGetValue(entry.Actor, out var owner) || !ReferenceEquals(owner, entry)) return false;
            if (entry.Checking) throw new InvalidOperationException("The exact-generation predicate cannot recursively inspect the same admission.");
            entry.Checking = true;
            try
            {
                return _exactCurrent(entry.Actor) && !IsDisposed && !entry.Disposed &&
                    _actors.TryGetValue(entry.Actor, out owner) && ReferenceEquals(owner, entry);
            }
            finally { entry.Checking = false; }
        }
        private static bool Expected(Exception error) => error is NotSupportedException or ZoneLuaException or InvalidDataException;
        private void Retire(PendingActor entry)
        {
            if (entry.Node is { } node) { _pending.Remove(node); entry.Node = null; }
            if (!_actors.TryGetValue(entry.Actor, out var owner) || !ReferenceEquals(owner, entry)) return;
            _actors.Remove(entry.Actor);
            if (_byObjectId.TryGetValue(entry.Actor.UnitObjectId, out var byId) && ReferenceEquals(byId, entry))
                _byObjectId.Remove(entry.Actor.UnitObjectId);
            if (entry.Ready) _ready--;
        }
        private void Check() => ObjectDisposedException.ThrowIf(IsDisposed, this);
        public void Dispose()
        {
            if (IsDisposed) return; IsDisposed = true;
            List<Exception>? failures = null;
            foreach (var entry in _actors.Values.ToArray())
                try { entry.Dispose(); } catch (Exception error) { (failures ??= []).Add(error); }
            if (failures is not null) throw new AggregateException("NPC admission owners failed to retire.", failures);
        }

        private sealed class PendingActor(ZoneManagedNpcAdmissionQueue owner, ZoneActorHandle actor, long sequence)
            : IZoneManagedAiActor, IZoneManagedAiActorReadiness
        {
            internal ZoneActorHandle Actor { get; } = actor;
            internal long Sequence { get; } = sequence;
            internal LinkedListNode<PendingActor>? Node;
            internal IZoneManagedAiActor? Candidate;
            internal bool Ready, Disposed, Checking;
            private bool _active, _hasActive, _hasTarget, _replaying;
            private ZoneActorHandle? _target;
            private ulong _activeRevision, _targetRevision, _appliedActive, _appliedTarget;
            public bool IsReady => Ready && owner.Current(this);

            public void SetActive(bool active)
            {
                Require();
                if (!_hasActive || _active != active) { _active = active; _hasActive = true; _activeRevision++; }
                if (Ready && !_replaying) ReplayControls();
            }
            public void SetTarget(ZoneActorHandle? target)
            {
                Require();
                if (!_hasTarget || _target != target) { _target = target; _hasTarget = true; _targetRevision++; }
                if (Ready && !_replaying) ReplayControls();
            }
            public void SendSignal(ZoneAiSignal signal)
            {
                ArgumentNullException.ThrowIfNull(signal); Require();
                if (!Ready) throw new NotSupportedException("NPC signal admission is pending; no real constructor/script owner is ready.");
                Candidate!.SendSignal(signal);
            }
            public void Update(uint timeMilliseconds, float elapsedSeconds)
            {
                Require();
                if (!float.IsFinite(elapsedSeconds) || elapsedSeconds < 0) throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
                if (Ready) Candidate!.Update(timeMilliseconds, elapsedSeconds);
            }
            internal void ReplayControls()
            {
                if (_replaying) throw new InvalidOperationException("NPC control replay cannot recursively execute its consumer.");
                _replaying = true;
                try
                {
                    //Replacement work guard, not a native limit. Callback changes must settle;
                    //a consumer which flips requests forever cannot monopolize a scene frame.
                    for (var pass = 0; pass < 32; pass++)
                    {
                        if (!owner.Current(this)) return;
                        if (_hasActive && _appliedActive != _activeRevision)
                        {
                            var revision = _activeRevision; var value = _active;
                            Candidate!.SetActive(value); _appliedActive = revision;
                            if (!owner.Current(this)) return;
                        }
                        if (_hasTarget && _appliedTarget != _targetRevision)
                        {
                            var revision = _targetRevision; var value = _target;
                            Candidate!.SetTarget(value); _appliedTarget = revision;
                            if (!owner.Current(this)) return;
                        }
                        if ((!_hasActive || _appliedActive == _activeRevision) && (!_hasTarget || _appliedTarget == _targetRevision)) return;
                    }
                    throw new InvalidOperationException("NPC admission controls did not stabilize within the replacement replay work limit.");
                }
                finally { _replaying = false; }
            }
            private void Require()
            { if (!owner.Current(this)) throw new ObjectDisposedException(nameof(PendingActor), "The exact NPC admission generation retired."); }
            public void Dispose()
            {
                if (Disposed) return;
                Disposed = true; owner.Retire(this);
                var candidate = Candidate; Candidate = null; Ready = false;
                candidate?.Dispose();
            }
        }
    }
}

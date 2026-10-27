using System.Collections.ObjectModel;
using AAEmu.Zone.Protocol.Messages;
using AAEmu.Zone.Units;

namespace AAEmu.Zone.Actors;

/// <summary>The physical and scene leaves used by native actor movement admission.</summary>
public interface IZoneActorMovementServices
{
    /// <summary>39270A40 model+140 psychokinesis guard (no controller, or virtual30/40 permits synchronization).</summary>
    bool AllowsSynchronization(ZoneUnit owner);
    /// <summary>392515E0: actor model+488 exists and actor+90C is false.</summary>
    bool UsesDirectPhysicalMovement(ZoneUnit owner);
    float GameSeconds { get; }
    uint ServerMilliseconds { get; }
    int InterpolationMode { get; }
    int BufferMilliseconds { get; }
    /// <summary>Required 39232570 physical attachment/entity operation; it may update the live model.</summary>
    void ApplyDirectMovement(ZoneUnit owner, ZoneActorMovementState state, float elapsedSeconds);
}

public sealed record ZoneActorMovementSample(uint Time, ZoneActorMovementState State);

/// <summary>Receive history and mode 2/3 sampling state. Physical application requires its actual simulation phase.</summary>
public sealed partial class ZoneActorMovementHistory
{
    private readonly List<ZoneActorMovementSample> _entries = [];
    public ReadOnlyCollection<ZoneActorMovementSample> Entries { get; }
    public double OffsetSeconds { get; private set; }
    public double LagSeconds { get; private set; }
    public uint AnchorTime { get; private set; }
    public ZoneActorMovementState? AnchorState { get; private set; }
    internal ZoneActorMovementHistory() => Entries = _entries.AsReadOnly();

    internal bool AdmitTime(uint packetTime, uint serverTime, uint bufferMilliseconds)
    {
        // 39253D00 uses ordinary unsigned clock values, not a wrap-aware subtraction.
        var buffer = bufferMilliseconds / 1000.0;
        var difference = packetTime / 1000.0 - serverTime / 1000.0;
        if (OffsetSeconds == 0)
        {
            LagSeconds = buffer;
            OffsetSeconds = difference - buffer;
            return true;
        }
        var lag = difference - OffsetSeconds;
        LagSeconds = lag;
        if (0 <= lag && lag <= buffer * 20.0) return true;
        OffsetSeconds = lag + OffsetSeconds;
        LagSeconds = 0;
        return false;
    }

    internal void Append(ZoneActorMovementState state, bool continuous, bool linked)
    {
        if (!continuous) _entries.Clear(); // Does not clear mode 3's anchor or timing state.
        _entries.Add(new(state.Time, state));
        // 392578D0 bounds the vector to its newest 1,000 entries. Mode 3's list is not capped here.
        if (!linked && _entries.Count > 1000) _entries.RemoveRange(0, _entries.Count - 1000);
    }

    internal void Anchor(ZoneActorMovementState state, uint serverTime)
    {
        if (AnchorTime != 0) return;
        AnchorTime = OffsetSeconds == 0 ? 0 : unchecked((uint)(long)((serverTime / 1000.0) * 1000.0 + OffsetSeconds * 1000.0));
        AnchorState = state;
    }

    internal void Clear()
    {
        _entries.Clear(); OffsetSeconds = 0; LagSeconds = 0; AnchorTime = 0; AnchorState = null;
        SampledState = null; HasLinkedSample = false;
    }
}

/// <summary>39270A40/392515E0: owned actor inbox and mode 2/3 history admission, without fabricated physics.</summary>
public sealed partial class ZoneActorMovementController : IZoneUnitMovementModel
{
    private readonly ZoneUnitRegistry _registry;
    private readonly IZoneActorMovementServices _services;
    private bool _receiving;
    private bool _sampling;
    public ZoneUnit Owner { get; }
    public ZoneActorMovementState? Latest { get; private set; }
    public bool HasPendingMovement { get; private set; }
    public float LastGameSeconds { get; private set; }
    public bool DirectMovementRecentlyApplied { get; private set; }
    public bool Hanging { get; private set; }
    public bool MovementFlag8 { get; private set; }
    public bool IsRetired { get; private set; }
    public ZoneActorMovementHistory VectorHistory { get; } = new();
    public ZoneActorMovementHistory LinkedHistory { get; } = new();
    /// <summary>
    /// The most recently ADMITTED input state, retained by both receive branches (direct and
    /// buffered). The frame-phase hooks (<see cref="TryRunPrePhase"/>/<see cref="TryRunPostPhase"/>)
    /// take their working copy from this — direct actors never fill <see cref="Latest"/>
    /// (<c>ReceiveCore</c>'s direct branch, :124-131), and the native physical phase still
    /// requires a retained state to produce input from ("the enclosing physical caller supplies
    /// its working copy of Latest", <c>ZoneActorMovementInput.cs:35-37</c>).
    /// </summary>
    public ZoneActorMovementState? LastAdmitted { get; private set; }
    /// <summary>
    /// The retained "Updated" latch of the enclosing physical caller
    /// (FIRST_ENTRY_NOTES.md:1411-1413): set when new input is admitted or a mode-2/3 sample
    /// lands, consumed by <see cref="TryRunPostPhase"/> (read once, cleared) so
    /// <c>TryRunPhysicalPostPhase</c>'s <c>updated</c> argument means "changed since the last
    /// postphase". TODO(updated-latch): the native retention period is unproven; per-pass
    /// consumption is the conservative reading.
    /// </summary>
    public bool Updated { get; private set; }
    private bool _phasing;

    public ZoneActorMovementController(ZoneUnitRegistry registry, ZoneUnit owner, IZoneActorMovementServices services)
    {
        ArgumentNullException.ThrowIfNull(registry); ArgumentNullException.ThrowIfNull(owner); ArgumentNullException.ThrowIfNull(services);
        _registry = registry; Owner = owner; _services = services;
        registry.BindMovementModel(this);
    }

    private bool Current => !IsRetired && _registry.IsMovementCurrent(Owner);

    public void Receive(UnitMovementData movement)
    {
        if (!Current) return;
        if (_receiving || _sampling) throw new InvalidOperationException("Actor movement admission cannot reenter its owning controller.");
        _receiving = true;
        try { ReceiveCore(movement); }
        finally { _receiving = false; }
    }

    private void ReceiveCore(UnitMovementData movement)
    {
        var allowed = _services.AllowsSynchronization(Owner);
        if (!Current || !allowed) return;
        var stance = movement.Kind == 1 && movement.Details is UnitActorMovement actor ? actor.Stance : -1;
        if (DirectMovementRecentlyApplied && (movement.Flags & 4) == 0 && stance != 0)
        {
            var now = _services.GameSeconds;
            if (!Current || now - LastGameSeconds <= .3f) return;
        }
        var state = ZoneActorMovementState.FromMovement(movement);
        var direct = _services.UsesDirectPhysicalMovement(Owner);
        if (!Current) return;
        if (direct)
        {
            var now = _services.GameSeconds;
            if (!Current) return;
            var elapsed = now - LastGameSeconds;
            DirectMovementRecentlyApplied = true;
            LastGameSeconds = now;
            LastAdmitted = state; Updated = true; // turn-2: retain the direct-branch input so the frame phase has a working copy (direct actors never fill Latest)
            _services.ApplyDirectMovement(Owner, state, elapsed);
        }
        else
        {
            Latest = state; HasPendingMovement = true; DirectMovementRecentlyApplied = false;
            LastAdmitted = state; Updated = true; // turn-2: the working-copy source of the prephase/postphase hooks
            var now = _services.GameSeconds;
            if (!Current) return;
            LastGameSeconds = now;
            var leftHanging = Hanging && state.HangingObjectId == 0;
            Hanging = state.HangingObjectId != 0;
            if (state.ResetHistory || leftHanging) ClearSelectedHistory();
            if (!Current) return;
            if (state.BypassHistory) ClearSelectedHistory();
            else
            {
                var buffer = (uint)Math.Clamp(_services.BufferMilliseconds, 0, 10000);
                if (!Current) return;
                var mode = _services.InterpolationMode;
                if (!Current) return;
                var history = mode == 2 ? VectorHistory : mode == 3 ? LinkedHistory : null;
                if (history is not null)
                {
                    var serverTime = _services.ServerMilliseconds;
                    if (!Current) return;
                    var continuous = history.AdmitTime(state.Time, serverTime, buffer);
                    history.Append(state, continuous, mode == 3);
                    if (mode == 3 && history.AnchorTime == 0)
                    {
                        // 39252BD0 returns zero without reading the clock when its offset is zero.
                        serverTime = history.OffsetSeconds == 0 ? 0 : _services.ServerMilliseconds;
                        if (!Current) return;
                        history.Anchor(state, serverTime);
                    }
                }
            }
        }
        if (Current) MovementFlag8 = (movement.Flags & 8) != 0;
    }

    private void ClearSelectedHistory()
    {
        var mode = _services.InterpolationMode;
        if (!Current) return;
        if (mode == 2) VectorHistory.Clear();
        else
        {
            mode = _services.InterpolationMode;
            if (!Current) return;
            if (mode == 3) LinkedHistory.ClearLinked();
        }
        Application.ResetCorrectionHistory();
    }

    /// <summary>
    /// 39256ED0: sample the mode 2 history using the required server clock. The caller supplies
    /// the native unsigned frame milliseconds and already selected buffer/tolerance values.
    /// Model phase guards, physical application and spatial publication belong to the caller.
    /// This leaves the received Latest state and HasPendingMovement unchanged.
    /// </summary>
    public bool TrySampleVectorHistory(uint elapsedMilliseconds, uint bufferMilliseconds, uint toleranceMilliseconds,
        out ZoneActorMovementState? state)
    {
        state = null;
        if (!Current) return false;
        if (_receiving || _sampling) throw new InvalidOperationException("Actor movement sampling cannot reenter its owning controller.");
        _sampling = true;
        try
        {
            var mode = _services.InterpolationMode;
            if (!Current || mode != 2 || VectorHistory.Entries.Count == 0) return false;
            // The native mode 2 path passes integer frame milliseconds without dividing them by 1,000.
            VectorHistory.CorrectSampleTime(elapsedMilliseconds, bufferMilliseconds / 1000.0, toleranceMilliseconds / 1000.0);
            var serverTime = VectorHistory.OffsetSeconds == 0 ? 0 : _services.ServerMilliseconds;
            if (!Current) return false;
            return VectorHistory.TrySampleAt(VectorHistory.SampleTime(serverTime), out state);
        }
        finally { _sampling = false; }
    }

    public void Retire()
    {
        if (IsRetired) return;
        IsRetired = true; Latest = null; HasPendingMovement = false;
        LastAdmitted = null; Updated = false;
        VectorHistory.Clear(); LinkedHistory.Clear();
        Application.RetireCorrection(); Application.RetireInput();
    }

    // ------------------------------------------------------------------
    // Turn 2 — prephase/postphase hooks called per actor by
    // ZoneMovementFramePhases against the IZoneMovementFramePhases contract
    // (PrePhase → StepPhysics → PostPhase; IMPLEMENTATION_OWNERSHIP.md change log
    // 2026-09-23). "input → physical action through IZonePhysicsBackend" lands
    // through the injected leaves: input production writes IZonePhysicsMotion
    // (the adapter beside the backend, Contracts/IZonePhysicsMotion.cs), and the
    // backend itself integrates between these hooks via StepPhysics. Exactly one
    // movement authority per actor remains enforced by the registry bind
    // (ZoneUnitRegistry.BindMovementModel :216-225 — second model refused) and
    // by these hooks' `Current` gate on every entry (IMPLEMENTATION_OWNERSHIP.md
    // section 1 invariant).
    // ------------------------------------------------------------------

    private void EnterPhase()
    {
        // Not-current actors are filtered by the callers BEFORE this guard (a retired actor is
        // a silent skip, never a throw); this entry exists for reentry only.
        if (_receiving || _sampling || _phasing)
            throw new InvalidOperationException("Actor movement phase cannot reenter its owning controller.");
        if (!Current)
            throw new InvalidOperationException("Actor movement phase requires its exact registered, unretired owner.");
        _phasing = true;
    }

    /// <summary>
    /// The per-actor prephase (PLAN section 4 order): eligibility gate → mode-2/3 sampling →
    /// input production, all BEFORE <c>backend.Step</c>. Returns the working copy of the most
    /// recently admitted input through <paramref name="working"/> and true when the actor was
    /// staged; false (never a throw) when the actor is not movement-current or has never
    /// admitted input — a retired actor is never staged (scope item 6).
    /// Sampling runs only under the native eligibility gate (<see cref="IsSamplingEligible"/>);
    /// input production keeps ITS gate (the producer's own doc: the enclosing physical caller
    /// owns phase eligibility, <c>ZoneActorMovementInput.cs:35-37</c>) so direct players —
    /// whose <c>DirectMovementRecentlyApplied</c> stays set natively — still get their
    /// walk/stop/jump applied every frame before the integration.
    /// Sampling clock domain: frame-millisecond integers from <c>frame.Elapsed</c> plus the
    /// injected scene clock inside the sampler; buffer/tolerance pair as in the sampling
    /// fixtures (<c>TrySampleVectorHistory(…, 50, 50, …)</c>,
    /// <c>AAEmu.Zone.Tests/ActorMovementSamplingTests.cs:87</c>) —
    /// TODO(movement-tolerance-config) for a distinct native tolerance source.
    /// </summary>
    internal bool TryRunPrePhase(in ZoneMovementFrame frame,
        in ZoneActorMovementEligibilitySnapshot snapshot,
        IZoneActorMovementInputServices inputServices, out ZoneActorMovementState? working)
    {
        ArgumentNullException.ThrowIfNull(inputServices);
        working = null;
        if (!Current) return false;
        EnterPhase();
        try
        {
            var admitted = LastAdmitted ?? Latest;
            if (admitted is null || !Current) return false;
            working = admitted with { }; // working copy (FIRST_ENTRY_NOTES.md:1411-1413); Latest itself is never mutated by phases

            if (IsSamplingEligible(snapshot))
            {
                var buffer = (uint)Math.Clamp(_services.BufferMilliseconds, 0, 10000); // bound of ReceiveCore :146
                var elapsedMilliseconds = frame.Elapsed <= TimeSpan.Zero
                    ? 0u
                    : (uint)Math.Clamp((long)frame.Elapsed.TotalMilliseconds, 1, uint.MaxValue);
                if (_services.InterpolationMode == 2)
                {
                    if (TrySampleVectorHistory(elapsedMilliseconds, buffer, buffer, out _)) Updated = true;
                }
                else if (_services.InterpolationMode == 3)
                {
                    // Physical-attachment context byte +29 (3924A300 from physical +11C): derived
                    // from the admitted ground ids — the exact predicate the linked blend itself
                    // uses (ZoneActorMovementLinkedSampling.cs:78); TODO(physical-attachment) for
                    // the native physical read once its owner exists.
                    var attached = working.GroundUnitId != 0 || working.GroundGimmickId != 0 ||
                        working.GroundDoodadId != 0;
                    if (TrySampleLinkedHistory((float)frame.Elapsed.TotalSeconds, buffer, buffer, attached, out _))
                        Updated = true;
                }
            }
            if (!Current) return false;

            TryApplyPhysicalInput(working, inputServices); // refusal-shaped failures surface from the leaves, not here
            return Current;
        }
        finally { _phasing = false; }
    }

    /// <summary>
    /// The per-actor postphase (PLAN section 4): physical correction + the retained-Updated
    /// postphase, then the admitted state through <paramref name="admitted"/> for pose commit,
    /// spatial refresh and outgoing encoding (those publication steps live in
    /// <c>ZoneMovementFramePhases</c>). Returns false for actors that stopped being
    /// movement-current (removal during a pending frame: <c>RemovePrepared</c> retires the
    /// movement model, <c>ZoneUnitRegistry.cs:208-215</c> → <c>IsRetired</c> → <c>Current</c>
    /// false, :100) — such actors produce NO further output (scope item 6).
    /// <c>CorrectionOwnershipLostException</c> mid-callback is absorbed inside the existing
    /// correction methods (:121/:158) and reported as a false return; named service refusals
    /// (open <c>TODO(pose-write)</c>, unsupported hanging) propagate to the coordinator, which
    /// catches per actor and continues the batch (PLAN section 5).
    /// <paramref name="hasConstraint"/> comes from the admitted hanging state — the enclosing
    /// physical caller's "actual constraint state" (contract :111-112).
    /// </summary>
    internal bool TryRunPostPhase(in ZoneMovementFrame frame,
        in ZoneActorMovementEligibilitySnapshot snapshot,
        IZoneActorMovementCorrectionServices correctionServices,
        out ZoneActorMovementState? admitted)
    {
        ArgumentNullException.ThrowIfNull(correctionServices);
        admitted = null;
        if (!Current) return false;
        EnterPhase();
        try
        {
            var admittedState = LastAdmitted ?? Latest;
            admitted = admittedState;
            if (admittedState is null || !Current) return false;
            var working = admittedState with { };
            var hasConstraint = working.HangingObjectId != 0;
            var updated = Updated;
            Updated = false; // latch consumed by this postphase pass (TODO(updated-latch))

            TryApplyPhysicalCorrection(working, hasConstraint, correctionServices);
            if (!Current) return false;
            TryRunPhysicalPostPhase(working, updated, hasConstraint, snapshot, correctionServices);
            return Current;
        }
        finally { _phasing = false; }
    }
}

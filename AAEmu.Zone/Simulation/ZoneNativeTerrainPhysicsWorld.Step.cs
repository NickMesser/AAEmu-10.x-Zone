using System.Runtime.InteropServices;

namespace AAEmu.Zone.Simulation;

public sealed unsafe partial class ZoneNativeTerrainPhysicsWorld
{
    // ------------------------------------------------------------------
    // Nonzero simulation step (PLAN section 2 / B1-B2; registered in
    // IMPLEMENTATION_OWNERSHIP.md section 4, change log 2026-09-23).
    // ------------------------------------------------------------------

    /// <summary>
    /// Advances this world by one explicit, POSITIVE scene period — the simulation-time owner.
    /// This is provably distinct from the engine's zero-time loading drain
    /// (<c>ZoneNativeEngine.PhysicsCompletion.cs:46</c>, which calls the exact same native
    /// function — world vslot +0x100 == module+0x8bd10, identity-checked at :38-41 — with
    /// <c>(world, 0, 0)</c>):
    /// <list type="number">
    /// <item>Time argument: this method requires a strictly positive finite period and refuses
    /// <c>elapsed &lt;= 0</c> BY NAME — "a zero-time drain is not a simulation step". The drain
    /// passes zero elapsed time and flags (<c>ZoneNativeEngine.PhysicsCompletion.cs:46</c>).</item>
    /// <item>Admission profile: <see cref="RequireSimulationStepState"/> demands one physics
    /// thread, no workers, valid time normalization and a FINITE, NON-NEGATIVE accumulated
    /// native time (world+0x680) — the counterpart of the drain's
    /// <c>RequireStaticPhysicsCompletionState</c> (<c>ZoneNativeEngine.PhysicsCompletion.cs:62-87</c>),
    /// which conversely demands <c>currentTime == 0</c> plus empty queues and captures pending
    /// static requests.</item>
    /// <item>Ownership: the drain is the engine-loading owner and validates queue/ownership
    /// postconditions around its call (:47-56); this method never inspects or clears the physics
    /// command queues — queued parameter/action requests queued on <c>body+0x40</c> by
    /// <c>35023200</c> are replayed by this very function (World vslot +0x100 →
    /// <c>3508BD10</c> subtracts the count when the command completes), so the nonzero Step is
    /// their update owner and must not "clear pointers or lower the guard"
    /// (FIRST_ENTRY_NOTES.md:1774-1779).</item>
    /// </list>
    ///
    /// The drain therefore SELF-REFUSES once simulation time advances, structurally: its profile
    /// runs before any early return (<c>ZoneNativeEngine.PhysicsCompletion.cs:33</c>) and throws
    /// "Static loading completion requires valid native time normalization before the first
    /// simulation frame" when <c>currentTime(world+0x680) != 0</c> (:73-77). Ordering falls out
    /// naturally — all loading completion must finish before the first Step, which is exactly
    /// what FIRST_ENTRY_NOTES.md:1810-1811 requires ("Positive-time actor frames and broader
    /// maintenance producers still require their own phase/lifetime owner") and
    /// FIRST_ENTRY_NOTES.md:1518-1519 restates ("callback/worker drain must extend this shared
    /// owner's contract before stepping"). The external proof harness (turn 3) demonstrates both
    /// directions: Step(0) refused by name, and the drain refused after the first nonzero Step.
    /// </summary>
    /// <param name="elapsed">The scene frame period; must be strictly positive and finite. Scene creation validates the configured period &gt; 0 (Simulation/ZoneSceneActivation.cs:74-75).</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="elapsed"/> is zero, negative, or does not cast to a positive finite float — "a zero-time drain is not a simulation step".</exception>
    /// <exception cref="NotSupportedException">The Step admission profile refuses: wrong thread/worker counts, invalid time normalization, non-finite accumulated time, busy native guards, or unresolved maintenance/scratch producers (TODO(step-producers)).</exception>
    public void Step(TimeSpan elapsed)
    {
        if (elapsed <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(elapsed),
                "A zero-time drain is not a simulation step; Step requires a strictly positive scene period.");
        var seconds = (float)elapsed.TotalSeconds;
        if (!float.IsFinite(seconds) || seconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(elapsed),
                "A zero-time drain is not a simulation step; Step requires a strictly positive finite scene period.");

        // Owner-thread + busy + fault gates (ZoneNativeTerrainPhysicsWorld.cs:227-250): only the
        // managed operation gate is held across the native call — never native writer guards,
        // which TimeStep acquires itself in its native order (ZoneNativeEngine.PhysicsCompletion.cs:44-45).
        using var operation = Enter();
        RequireSimulationStepState();

        // Vtable identity check, the exact precedent of the drain (ZoneNativeEngine.PhysicsCompletion.cs:38-41):
        // world vslot +0x100 must be module+0x8bd10 — the same function the loading drain calls at dt=0.
        var table = Marshal.ReadIntPtr(_world);
        if (Marshal.ReadIntPtr(table + 0x100) != _module.Handle + 0x8bd10)
            throw new InvalidOperationException("The native physics time-step implementation changed.");
        var timeStep = Marshal.GetDelegateForFunctionPointer<TimeStepCall>(_module.Handle + 0x8bd10);
        try
        {
            timeStep(_world, seconds, TimeStepFlags);
        }
        catch (Exception error) { NativeInvocationFailed(error); throw; }
        CompleteCall();
    }

    /// <summary>
    /// The accumulated native simulation time (world+0x680), in seconds. This value is ZERO
    /// until the first positive <see cref="Step"/> and is exactly the field the loading drain's
    /// profile requires to still be zero (<c>ZoneNativeEngine.PhysicsCompletion.cs:73-77</c>) —
    /// reading it above zero is the observable evidence that the drain now self-refuses.
    /// Exposed for the external stepping proof harness; not a gameplay clock (the scene's
    /// clocks live on the movement frame record).
    /// </summary>
    public float NativeSimulationTimeSeconds
    {
        get
        {
            using var operation = Enter();
            return ReadNativeSimulationTime();
        }
    }

    /// <summary>
    /// Step admission profile — the counterpart of <c>ZoneNativeEngine.</c>
    /// <c>RequireStaticPhysicsCompletionState</c> (ZoneNativeEngine.PhysicsCompletion.cs:62-87),
    /// with the drain's <c>currentTime == 0</c> / queue-capture requirements replaced by a
    /// finite accumulated time, and every refusal raised BY NAME (never a silent skip).
    /// </summary>
    private void RequireSimulationStepState()
    {
        // One configured physics thread, no workers (profile cell of the drain,
        // ZoneNativeEngine.PhysicsCompletion.cs:66-70; borrowed-idle precedent
        // ZoneNativeTerrainPhysicsWorld.Borrowing.cs:25-26).
        if (Marshal.ReadInt32(_world + 0x1f8) != 1 || Marshal.ReadInt32(_world + 0x5798) != 0)
            throw new NotSupportedException(
                "Simulation stepping requires one configured physics thread and no physics workers " +
                "(world+0x1f8==1, world+0x5798==0); see ZoneNativeEngine.PhysicsCompletion.cs:66-70 " +
                "and ZoneNativeTerrainPhysicsWorld.Borrowing.cs:25-26.");

        // Valid time normalization (identical arithmetic to the drain,
        // ZoneNativeEngine.PhysicsCompletion.cs:71-75) ...
        var maximumStep = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(_world + 0xe0));
        var granularity = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(_world + 0xdc));
        if (!float.IsFinite(maximumStep) || maximumStep < 0 || !float.IsFinite(granularity) ||
            granularity < 0 || (granularity > 0 && !float.IsFinite(1f / granularity)))
            throw new NotSupportedException(
                "Simulation stepping requires valid native time normalization " +
                "(ZoneNativeEngine.PhysicsCompletion.cs:71-75).");

        // ... but unlike the drain, the accumulated native time must already be finite and
        // non-negative (it stays 0 until the first Step, then advances monotonically).
        var currentTime = ReadNativeSimulationTime();
        if (!float.IsFinite(currentTime) || currentTime < 0)
            throw new NotSupportedException(
                "Simulation stepping requires a finite non-negative accumulated native time (world+0x680).");

        // Native in-flight guards must be free (same offset set as the drain's busy loop,
        // ZoneNativeEngine.PhysicsCompletion.cs:78-80).
        foreach (var offset in new[] { 0x57dc, 0x57c4, 0x57e0, 0x57e8, 0x581c, 0x5814, 0x5804, 0x3f0, 0x60 })
            if (Marshal.ReadInt32(_world + offset) != 0)
                throw new NotSupportedException(
                    $"Native physics guard +0x{offset:X} is busy; a simulation frame cannot enter an active native operation.");

        // Maintenance and scratch producers still refuse BY NAME until their producer owner
        // exists — never a silent skip (PLAN B2; same cells as the drain,
        // ZoneNativeEngine.PhysicsCompletion.cs:81-86).
        foreach (var offset in new[] { 0xd4, 0x54b8, 0x51bc })
            if (Marshal.ReadInt32(_world + offset) != 0)
                throw new NotSupportedException(
                    $"Native physics maintenance +0x{offset:X} needs an additional producer/retirement owner " +
                    $"before simulation stepping (TODO(step-producers)); never skipped silently.");
        foreach (var offset in new[] { 0x5170, 0x3e8, 0x5470, 0x58 })
            if (Marshal.ReadIntPtr(_world + offset) != 0)
                throw new NotSupportedException(
                    $"Native physics scratch ownership +0x{offset:X} must be resolved before simulation stepping " +
                    $"(TODO(step-producers)); never skipped silently.");
    }

    private float ReadNativeSimulationTime() =>
        BitConverter.Int32BitsToSingle(Marshal.ReadInt32(_world + 0x680));

    /// <summary>
    /// The only observed flag profile in this tree is flags 0 — the loading drain's call
    /// (<c>complete(_physicsWorld, 0, 0)</c>, ZoneNativeEngine.PhysicsCompletion.cs:46) and
    /// FIRST_ENTRY_NOTES.md:1796-1797 ("<c>3508BD10(0.0f, 0)</c>"). No evidence exists here for
    /// any other flag value, so none is fabricated; the time argument is what separates the
    /// drain (0) from this step (&gt;0).
    /// </summary>
    private const uint TimeStepFlags = 0;

    /// <summary>Same signature as <c>CompleteStaticPhysicsCall</c> (ZoneNativeEngine.PhysicsCompletion.cs:171-172): world vslot +0x100 → 3508BD10(world, fTime, flags).</summary>
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void TimeStepCall(nint world, float elapsedTime, uint flags);
}

using System.Runtime.InteropServices;

namespace AAEmu.Zone.Native.Engine;

public sealed partial class ZoneNativeEngine
{
    /// <summary>
    /// Completes queued construction requests for this engine's retained full static bodies.
    /// Calls the real physical-world TimeStep with zero elapsed time and flags zero. This is
    /// explicit loading completion, not an actor simulation frame or a replacement for frame
    /// scheduling. Native bookkeeping still runs. Other command owners and maintenance producers
    /// require their own lifecycle integration before admission.
    /// </summary>
    public int CompletePendingStaticPhysics()
    {
        using var operation = EnterOperation();
        return CompleteLoadingPhysicsCore(() =>
        {
            var statics = _staticObjectBorrower?.Resource
                ?? throw new InvalidOperationException("Static physics completion requires the engine's live static-object service.");
            return statics.CapturePhysicsCompletionBodies();
        });
    }

    // Only engine-owned resource collectors may call this while the shared operation is held.
    // Every collector validates its own native body subclasses and foreign ownership. The queue,
    // reference, worker and zero-time admission below remains common to loading completion.
    private int CompleteLoadingPhysicsCore(Func<HashSet<nint>> captureOwnedBodies, Action? afterNativeCompletion = null)
    {
        if (!_operationActive) throw new InvalidOperationException("Loading completion requires the active engine operation.");
        var module = RequiredModule("cryphysics_dedicate.dll");
        ValidatePhysicsState(module);
        RequireStaticPhysicsCompletionState();
        var bodies = captureOwnedBodies();
        var pending = CaptureStaticPhysicsRequests(bodies);
        if (pending.Count == 0) return 0;

        var table = Marshal.ReadIntPtr(_physicsWorld);
        if (Marshal.ReadIntPtr(table + 0x100) != module + 0x8bd10)
            throw new InvalidOperationException("The native physics loading-completion implementation changed.");
        var complete = Marshal.GetDelegateForFunctionPointer<CompleteStaticPhysicsCall>(module + 0x8bd10);
        try
        {
            // Do not hold native writer guards across TimeStep: the actual function acquires
            // them in its native order while swapping and replaying the queue with immediate flags.
            complete(_physicsWorld, 0, 0);
            VerifyAvailable();
            ValidatePhysicsState(module);
            RequireStaticPhysicsCompletionState();
            // Concrete cell owners retain any newly returned auxiliary allocations before
            // the generic ownership/queue postconditions can reject this completion.
            afterNativeCompletion?.Invoke();
            if (!bodies.SetEquals(captureOwnedBodies()))
                throw new InvalidOperationException("Native static-body ownership changed during loading completion.");
            if (CaptureStaticPhysicsRequests(bodies).Count != 0)
                throw new InvalidOperationException("Native loading completion left pending static-body requests.");
        }
        catch (Exception error) { Poison(error); throw; }
        return pending.Count;
    }

    private void RequireStaticPhysicsCompletionState()
    {
        // The real TimeStep reconciles requested workers even at dt=0. This exact profile
        // preserves the current owner thread and does not introduce a worker producer.
        if (Marshal.ReadInt32(_physicsWorld + 0x1f8) != 1 ||
            Marshal.ReadInt32(_physicsWorld + 0x5798) != 0 ||
            Marshal.ReadInt32(_physicsWorld + 0x1a4) != 0 ||
            Marshal.ReadInt32(_physicsWorld + 0x6d0) != 0)
            throw new NotSupportedException("Static loading completion requires one configured physics thread, no workers and zero elapsed time.");
        var maximumStep = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(_physicsWorld + 0xe0));
        var granularity = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(_physicsWorld + 0xdc));
        var currentTime = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(_physicsWorld + 0x680));
        if (!float.IsFinite(maximumStep) || maximumStep < 0 || !float.IsFinite(granularity) ||
            granularity < 0 || (granularity > 0 && !float.IsFinite(1f / granularity)) ||
            currentTime != 0 || Marshal.ReadInt32(_physicsWorld + 0x698) != 0)
            throw new NotSupportedException("Static loading completion requires valid native time normalization before the first simulation frame.");
        foreach (var offset in new[] { 0x57dc, 0x57c4, 0x57e0, 0x57e8, 0x581c, 0x5814, 0x5804, 0x3f0, 0x60 })
            if (Marshal.ReadInt32(_physicsWorld + offset) != 0)
                throw new InvalidOperationException($"Native physics guard +0x{offset:X} is busy; retain loading resources.");
        foreach (var offset in new[] { 0xd4, 0x54b8, 0x51bc })
            if (Marshal.ReadInt32(_physicsWorld + offset) != 0)
                throw new NotSupportedException($"Native physics maintenance +0x{offset:X} needs an additional producer/retirement owner.");
        foreach (var offset in new[] { 0x5170, 0x3e8, 0x5470, 0x58 })
            if (Marshal.ReadIntPtr(_physicsWorld + offset) != 0)
                throw new NotSupportedException($"Native physics scratch ownership +0x{offset:X} must be resolved before static loading completion.");
    }

    private StaticPhysicsRequests CaptureStaticPhysicsRequests(HashSet<nint> ownedBodies)
    {
        // 35022520 owns the two arrays of queue blocks. TimeStep consumes only the active
        // array after swapping them, so the spare must be logically empty on admission.
        var active = CapturePhysicsQueue(0x5188, 0x5198, 0x519c, 0x51a8);
        var spare = CapturePhysicsQueue(0x5190, 0x51a0, 0x51a4, 0x51ac);
        RequireEmptyPhysicsQueue(spare);
        var requests = new StaticPhysicsRequests();
        for (var blockIndex = 0; blockIndex < active.Count; blockIndex++)
        {
            var block = Marshal.ReadIntPtr(active.Blocks + blockIndex * 8);
            if (block == 0) throw new InvalidOperationException("A native physics queue block is missing.");
            var offset = 0;
            while (true)
            {
                // Managed admission covers native ordinary 8 KiB blocks. Oversized records
                // have a separate native allocation path and require explicit admission later.
                if (offset > 0x1ffc)
                    throw new NotSupportedException("The native physics queue exceeds the ordinary-block admission bound.");
                var kind = Marshal.ReadInt32(block + offset);
                if (kind == -1) break;
                if (offset > 0x1fec)
                    throw new InvalidOperationException("A native physics queue header exceeds its block.");
                var length = Marshal.ReadInt32(block + offset + 4);
                if (length < 20 || length > 0x1ffc - offset)
                    throw new NotSupportedException("A native physics request requires the oversized-block ownership path.");
                if (kind is < 0 or > 2)
                    throw new NotSupportedException($"Native physics command kind {kind} requires an additional lifecycle owner.");
                var body = Marshal.ReadIntPtr(block + offset + 8);
                if (!ownedBodies.Contains(body))
                    throw new NotSupportedException("A pending physics request belongs to an unowned or non-static body.");
                requests.BodyCounts.TryGetValue(body, out var count);
                requests.BodyCounts[body] = checked(count + 1);
                requests.Count = checked(requests.Count + 1);
                offset += length;
            }
            if (blockIndex == active.Count - 1 && offset != active.Cursor)
                throw new InvalidOperationException("The native physics queue's terminal cursor does not match its records.");
        }
        foreach (var body in ownedBodies)
        {
            requests.BodyCounts.TryGetValue(body, out var count);
            var pendingCount = unchecked((uint)Marshal.ReadInt32(body + 0x40)) >> 17;
            if (pendingCount != count)
                throw new InvalidOperationException("The static body's outstanding references do not match its owned pending requests.");
        }
        return requests;
    }

    private PhysicsQueue CapturePhysicsQueue(int blocksOffset, int countOffset, int capacityOffset, int cursorOffset)
    {
        var queue = new PhysicsQueue(Marshal.ReadIntPtr(_physicsWorld + blocksOffset),
            Marshal.ReadInt32(_physicsWorld + countOffset), Marshal.ReadInt32(_physicsWorld + cursorOffset));
        var capacity = Marshal.ReadInt32(_physicsWorld + capacityOffset);
        // This 64 MiB traversal cap is a managed admission bound, not a native allocation limit.
        if (queue.Count < 0 || queue.Count > capacity || capacity < 0 || capacity % 8 != 0 ||
            (capacity == 0) != (queue.Blocks == 0) || (queue.Count == 0 && queue.Cursor != 0x2000) ||
            queue.Count > 8192 || (queue.Count != 0 && queue.Blocks == 0) ||
            queue.Cursor < 0 || queue.Cursor > 0x2000)
            throw new NotSupportedException("The native physics queue is outside the captured loading layout.");
        return queue;
    }

    private static void RequireEmptyPhysicsQueue(PhysicsQueue queue)
    {
        if (queue.Count == 0) return;
        if (queue.Count == 1 && queue.Cursor == 0)
        {
            var block = Marshal.ReadIntPtr(queue.Blocks);
            if (block != 0 && Marshal.ReadInt32(block) == -1) return;
        }
        throw new InvalidOperationException("The spare native physics queue has outstanding work; retain loading resources.");
    }

    private sealed class StaticPhysicsRequests
    {
        internal int Count;
        internal readonly Dictionary<nint, int> BodyCounts = [];
    }

    private readonly record struct PhysicsQueue(nint Blocks, int Count, int Cursor);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void CompleteStaticPhysicsCall(nint world, float elapsedTime, uint flags);
}

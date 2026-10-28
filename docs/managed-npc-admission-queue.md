# Managed NPC admission queue

`ZoneManagedNpcAdmissionQueue` moves constructor work out of the incoming NPC packet handler. `Enqueue(handle)` returns a fresh pending `IZoneManagedAiActor`; it does not create scripts, a physics body, or an actor shell. The scene calls `Pump(maximumAdmissions)` before physics. The host uses one admission per frame.

This is a replacement scheduling policy. It bounds the number of admission work entries per frame, not the duration of an individual constructor. Work follows FIFO order. Entries created by constructor, readiness, refusal, or scheduling callbacks wait until the next pump. Removing an entry unlinks its queue node immediately, so a canceled burst does not require scanning tombstones. An entry discovered stale during pumping consumes one work slot.

The constructor takes a real actor factory, an exact-generation admission predicate, an expected-refusal consumer, and an optional admitted callback. The predicate must check the live admission generation rather than numeric Unit ID alone. The refusal consumer must retire the exact provider/catalog admission. `NotSupportedException`, `ZoneLuaException`, and `InvalidDataException` follow that refusal path; programming errors release staged ownership and propagate.

The pending wrapper retains the latest activation and target requests. It replays only requests actually received, preserving the real constructor's defaults otherwise. Pending signals are refused rather than buffered; pending updates do no actor work. After construction and replay, `IZoneManagedAiActorReadiness.IsReady` becomes true, the optional admitted callback can update the catalog to its scheduled phase, and the queue rechecks the exact generation. Retirement from any callback wins. Readiness describes constructor and signal ownership; it does not assert full AI gameplay support.

The wrapper owns the real actor returned by the factory, including a late return after cancellation. Disposal cancels queued work or retires the transferred owner exactly once. A reused numeric Unit ID requires a fresh generation and proof that its predecessor retired. The same exact generation cannot be re-enqueued. Factories cannot return pending wrappers or transfer an already-owned actor again.

The queue is scene-thread owned. Recursive pumping is rejected. Callback-driven control replay has an explicit 32-pass work guard; consumers that continually change their own requests fail rather than monopolizing a frame. This guard is a replacement safety bound, not an original engine limit.

Production pumps at most one cold admission on a frame with no World packet dispatched since
the previous frame. `ZoneSceneFrameCoordinator.WorldPacketProcessed` is scoped to
that frame and cleared even on failure. Existing physics, scripts, AI and simulation continue
on busy frames. This prevents the fair one-packet/one-tick loop from invoking a cold Lua
constructor for every packet in an incoming batch. It does not bound one constructor's wall
time; sustained ingress can defer new admissions and still needs a measured fairness policy.

`ManagedNpcAdmissionQueueTests` covers burst deferral, FIFO boundaries, cancellation before and during construction, generation replacement, control replay, signal admission, exact refusal retirement, programming failures, reused ownership, scheduling-callback retirement, and cleanup after destructor failure.

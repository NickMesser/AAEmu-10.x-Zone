using AAEmu.Commons.Network;
using AAEmu.Commons.Utils;
using AAEmu.Zone.Physics;
using AAEmu.Zone.Protocol;
using AAEmu.Zone.Units;

namespace AAEmu.Zone.Actors;

// ---------------------------------------------------------------------------
// Turn 2 — ZWUnitMovements new-pose quantization/compression/batching encoder
// (registered IMPLEMENTATION_OWNERSHIP.md section 4, change log 2026-09-23;
// PLAN section 4 design). This encoder SAMPLES THE AUTHORITATIVE POST-STEP POSE —
// re-emitting received quantized input is NOT compression (FIRST_ENTRY_NOTES.md:
// 1420-1422) — and it encodes against World's parse constraints verbatim:
//   count s32            MovementRelay.cs:316 (read Int32; <= 0 returns :317)
//   entry = Bc + type byte + UnitMoveType body
//                        MovementRelay.cs:330-333 (ReadBc → GetType → mt.Read)
//   len<=0 / overrun aborts the whole batch
//                        MovementRelay.cs:334-339 — so entries are written with
//                        the SAME PacketStream primitives the relay parses with,
//                        in the exact field order of UnitMoveType.Read/Write
//                        (AAEmu.Game UnitMoveType.cs:28-65/:67-103), never
//                        hand-packed bytes (the relay's own re-encode requirement,
//                        MovementRelay.cs:377-381)
//   zone buffer ≤ 1000 entries, World SC split stays 400 (World-side only)
//                        MovementRelay.cs:390 / :426-433 (ScMaxUnits)
//   body ≤ ZoneFrame.MaxBodySize (65533)
//                        AAEmu.Zone.Protocol/ZoneFrame.cs:11-12
// The shared movement body layout is documented at MovementRelay.cs:20-25
// ("identical in layout (MOVEMENT.md); count width differs (ZW s32 vs SC u16)").
//
// Quantization (each step cites its decode counterpart so the round trip closes):
//   position  11 bytes  Helpers.ConvertPosition float→11 (Commons Helpers.cs:154-…,
//                       comment :133-136) — the exact inverse of PacketStream
//                       ReadPosition (PacketStream.cs:881-893) the relay's mt.Read uses
//   velocity  i16 ×3    scale 32767/60 — inverse of UnitMovementData.DecodedVelocity's
//                       kind-1 decode (AAEmu.Zone.Protocol Messages/UnitMovement.cs:77)
//   rotation  sbyte ×3  direction units — mirrored from World's OUTGOING authoring
//                       ConvertRadianToDirection (AAEmu.Game Utils/MathUtil.cs:313-322;
//                       PositionAndRotation.cs:110-114) and decoded by the relay's
//                       NPC mirror via ConvertDirectionToRadian (MovementRelay.cs:220-224)
//   delta     sbyte ×3  inverse of MovementVector8.Decode (/127, UnitMovement.cs:48)
//   stance sbyte, alertness byte, ActorFlags u16 — MoveTypeType field bytes
//                       (UnitMoveType.cs:42-44); ActorFlags carries ONLY the admitted
//                       low-5-bit mask of FromMovement (:53), so the conditional
//                       FallVel/Gc/Climb/Push gates (UnitMoveType.cs:45-64) stay clear
//                       and no optional blob is ever emitted
//   Time u32 ms         THIS frame's ZoneMovementFrame.ServerMilliseconds — the single
//                       sampled scene clock (IZoneMovementFramePhases doc surface);
//                       never the client's packet state.Time
//
// Delta suppression + heartbeat (PLAN section 4): emit when the QUANTIZED tuple
// changed vs the last emission, or on any ActorFlags 8/16 jump-bit transition
// (always immediate), plus a 2 s heartbeat restricted to units whose quantized
// position changed within the last 5 s — idle units emit nothing (fixes the
// "streams every unit every tick" census note, MovementRelay.cs:50-53). Deltas
// are unsigned-clock comparisons in the scene-clock domain, matching the
// non-wrap-aware unsigned admission clock (ZoneActorMovementController.cs:34-40).
//
// Coordinate policy: values are emitted in the model-pose frame verbatim. The
// local/world wire policy is World's relay's conversion owner — "Create is
// zone-local → ZW is local → world for SC + mirrors" with the kill-switch no-op
// (MovementRelay.cs:341-343); Agent 5 supplies the flag so the harness can assert
// the policy the scene runs under. TODO(world-seed-check): under the world-on-wire
// kill-switch, correctness additionally requires World to have seeded Create
// world-space (the pre-fix condition of MovementRelay.cs:33-34).
// ---------------------------------------------------------------------------
public sealed class ZoneMovementOutgoing
{
    /// <summary>Zone buffer note: up to 1000 entries per ZW frame (MovementRelay.cs:390).</summary>
    public const int MaxEntriesPerFrame = 1000;

    private readonly ZoneUnitRegistry _registry;
    private readonly ZoneMovementDiagnostics _diagnostics;
    private readonly Dictionary<ZoneUnit, EmitState> _states = [];
    private readonly List<PendingEntry> _pending = [];
    private readonly uint _heartbeatMilliseconds;
    private readonly uint _recentlyMovedWindowMilliseconds;
    private readonly int _maxEntriesPerFrame;
    private readonly int _maxBodyBytes;

    /// <param name="registry">Entry build re-checks <c>IsCurrent</c> per unit (removal between sample and build suppresses the entry — scope item 6).</param>
    /// <param name="localOnWire">
    /// The <c>ZoneCoordBoundary</c> wire policy for THIS scene (Agent 5 supplies it):
    /// true = retail local-on-wire, the relay shifts local→world on parse
    /// (<c>MovementRelay.cs:343</c>); false = world-on-wire kill-switch mode (:342).
    /// The encoder converts nothing either way — conversion is the relay's — the flag exists
    /// so the harness pins which convention the frames were authored under.
    /// </param>
    /// <param name="heartbeatMilliseconds">Heartbeat period for recently-moving units (PLAN: 2 s).</param>
    /// <param name="recentlyMovedWindowMilliseconds">"Moved within" window gating the heartbeat (PLAN: 5 s); older movement goes silent.</param>
    /// <param name="maxBodyBytes">Frame body cap; defaults to <see cref="ZoneFrame.MaxBodySize"/> (65533, ZoneFrame.cs:11-12).</param>
    public ZoneMovementOutgoing(ZoneUnitRegistry registry, ZoneMovementDiagnostics diagnostics,
        bool localOnWire, uint heartbeatMilliseconds = 2_000,
        uint recentlyMovedWindowMilliseconds = 5_000, int maxEntriesPerFrame = MaxEntriesPerFrame,
        int maxBodyBytes = ZoneFrame.MaxBodySize)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(diagnostics);
        if (heartbeatMilliseconds == 0) throw new ArgumentOutOfRangeException(nameof(heartbeatMilliseconds));
        if (recentlyMovedWindowMilliseconds == 0) throw new ArgumentOutOfRangeException(nameof(recentlyMovedWindowMilliseconds));
        if (maxEntriesPerFrame is < 1 or > MaxEntriesPerFrame)
            throw new ArgumentOutOfRangeException(nameof(maxEntriesPerFrame),
                $"The zone movement buffer holds 1..{MaxEntriesPerFrame} entries per frame (MovementRelay.cs:390).");
        if (maxBodyBytes < 64 || maxBodyBytes > ZoneFrame.MaxBodySize)
            throw new ArgumentOutOfRangeException(nameof(maxBodyBytes),
                $"Frame body must fit 64..{ZoneFrame.MaxBodySize} bytes (ZoneFrame.cs:11-12).");
        _registry = registry;
        _diagnostics = diagnostics;
        LocalOnWire = localOnWire;
        _heartbeatMilliseconds = heartbeatMilliseconds;
        _recentlyMovedWindowMilliseconds = recentlyMovedWindowMilliseconds;
        _maxEntriesPerFrame = maxEntriesPerFrame;
        _maxBodyBytes = maxBodyBytes;
    }

    /// <summary>Wire policy the frames are authored under (relay conversion remains World-side, MovementRelay.cs:341-343).</summary>
    public bool LocalOnWire { get; }

    // ------------------------------------------------------------------
    // quantized tuple — the exact comparison key of the delta suppression
    // ------------------------------------------------------------------
    private readonly record struct QuantizedPose(
        byte[] Position, sbyte RotationX, sbyte RotationY, sbyte RotationZ,
        short VelocityX, short VelocityY, short VelocityZ,
        sbyte DeltaX, sbyte DeltaY, sbyte DeltaZ,
        sbyte Stance, sbyte Alertness, ushort ActorFlags, byte MoveFlags,
        bool Grounded, byte JumpBits)
    {
        /// <summary>Jump bits are ActorFlags 8/16 of the admitted state (controller latch, ZoneActorMovementInput.cs:71-73).</summary>
        public static byte JumpBitsOf(byte actorFlags) => (byte)(actorFlags & 0x18);

        public bool SameValues(in QuantizedPose other) =>
            Position.AsSpan().SequenceEqual(other.Position) &&
            RotationX == other.RotationX && RotationY == other.RotationY && RotationZ == other.RotationZ &&
            VelocityX == other.VelocityX && VelocityY == other.VelocityY && VelocityZ == other.VelocityZ &&
            DeltaX == other.DeltaX && DeltaY == other.DeltaY && DeltaZ == other.DeltaZ &&
            Stance == other.Stance && Alertness == other.Alertness &&
            ActorFlags == other.ActorFlags && MoveFlags == other.MoveFlags &&
            Grounded == other.Grounded && JumpBits == other.JumpBits;
    }

    private sealed class EmitState
    {
        public QuantizedPose? LastEmitted;
        public QuantizedPose? LastSampled;
        public uint LastEmitMilliseconds;
        public uint LastPositionChangeMilliseconds;
        public bool WasMoving;
    }

    private sealed class PendingEntry(ZoneUnit unit, uint bcId, byte[] entry)
    {
        public ZoneUnit Unit { get; } = unit;
        public uint BcId { get; } = bcId;
        public byte[] Entry { get; } = entry;
        public int Length => Entry.Length;
    }

    /// <summary>
    /// Samples ONE unit's authoritative post-step pose for this frame's batch. The caller
    /// (ZoneMovementFramePhases) has already re-checked movement-current/liveness and committed
    /// the pose; this method re-checks registry currency and spatial membership itself
    /// (PLAN section 4: "postphase copied status per live, movement-current, spatially-registered
    /// unit") and applies the quantized-tuple delta suppression with the jump-bit and heartbeat
    /// rules. Nothing is queued here — <see cref="Build"/> drains the pending entries.
    /// </summary>
    public void Sample(ZoneUnit unit, ZonePhysicsActorStatus status, ZoneActorMovementState admitted,
        in ZoneMovementFrame frame)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(admitted);
        if (!_registry.IsCurrent(unit))
        {
            _diagnostics.ReportSkippedActor(unit.Snapshot.ObjectId, "encoder sample suppressed: unit no longer registered.");
            return;
        }
        if (_registry.Spatial is null || _registry.Spatial.GetMembership(unit) is null)
        {
            _diagnostics.ReportSkippedActor(unit.Snapshot.ObjectId,
                "encoder sample suppressed: unit is not spatially registered (no index or no membership).");
            return;
        }
        if (unit.Snapshot.ObjectId > 0xFFFFFF)
        {
            _diagnostics.ReportSkippedActor(unit.Snapshot.ObjectId,
                "encoder sample suppressed: object ID exceeds the 24-bit bc domain (PacketStream.WriteBc :1165-1168).");
            return;
        }

        var pose = Quantize(unit, status, admitted);
        if (pose is null) return; // non-finite status was reported inside

        var now = frame.ServerMilliseconds;
        var state = GetState(unit);
        // MoveFlags depend on the emission context (moving→stopped transition uses the last
        // EMITTED movement state) and must be settled BEFORE the tuple comparison, or the
        // comparison could never match a previous emission.
        var candidate = pose.Value with { MoveFlags = MoveFlagsFor(unit, pose.Value, state) };
        if (state.LastSampled is not { } previousSample || !candidate.SameValues(previousSample))
            state.LastPositionChangeMilliseconds = now; // movement evidence in the scene-clock domain
        state.LastSampled = candidate;

        var emit = state.LastEmitted is not { } previousEmit
            || candidate.JumpBits != previousEmit.JumpBits          // jump 8/16 transitions always emit immediately
            || !candidate.SameValues(previousEmit)                  // quantized tuple changed
            || (unchecked(now - state.LastEmitMilliseconds) >= _heartbeatMilliseconds &&
                unchecked(now - state.LastPositionChangeMilliseconds) < _recentlyMovedWindowMilliseconds);
        // The last clause is the 2 s heartbeat, gated to units that moved within 5 s; a unit
        // whose position has been quantized-still for ≥ 5 s emits nothing (MovementRelay.cs:50-53).
        if (!emit) return;

        state.LastEmitted = candidate;
        state.LastEmitMilliseconds = now;
        state.WasMoving = VelocityLooksMoving(candidate);
        _pending.Add(new(unit, unit.Snapshot.ObjectId,
            EncodeEntry(unit.Snapshot.ObjectId, candidate, frame.ServerMilliseconds)));
    }

    /// <summary>
    /// Drains the frame's pending entries into as few <c>ZWUnitMovements</c> frames as the caps
    /// allow — s32 count + entries (MovementRelay.cs:313-337), ≤1000 entries and ≤65533 bytes
    /// per frame — re-checking <c>IsCurrent</c> per entry (removal during a pending frame never
    /// emits), then queues them through the existing notification route
    /// (<c>ZoneUnitRegistry.QueueNotification</c> :36, flushed by ZoneSession :281-285).
    /// Returns the number of ENTRIES actually queued (0 queues no frame — a zero count would
    /// make the relay return at MovementRelay.cs:317-318).
    /// </summary>
    public int Build(in ZoneMovementFrame frame)
    {
        if (_pending.Count == 0) return 0;
        var queued = 0;
        var batch = new List<PendingEntry>(_pending.Count);
        var bodyBytes = sizeof(int);

        Flush();
        for (var index = 0; index < _pending.Count; index++)
        {
            var entry = _pending[index];
            if (!_registry.IsCurrent(entry.Unit))
            {
                _diagnostics.ReportSkippedActor(entry.BcId,
                    "encoder dropped entry: unit removed between sample and build (scope item 6).");
                continue;
            }
            if (entry.Length + sizeof(int) > _maxBodyBytes)
            {
                _diagnostics.ReportSkippedActor(entry.BcId,
                    $"encoder dropped entry: single entry ({entry.Length} B) exceeds the frame body budget.");
                continue;
            }
            if (batch.Count >= _maxEntriesPerFrame || bodyBytes + entry.Length > _maxBodyBytes)
                Flush();
            batch.Add(entry);
            bodyBytes += entry.Length;
        }
        Flush();
        _pending.Clear();
        return queued;

        void Flush()
        {
            if (batch.Count == 0) return;
            var stream = new PacketStream();
            stream.Write(batch.Count); // s32 count — MovementRelay.cs:316
            foreach (var entry in batch)
                stream.Write(entry.Entry, appendSize: false); // same raw append the SC side uses (MovementRelay.cs:429-432)
            var body = stream.GetBytes();
            var frame1 = new ZoneFrame(ZwOpcodes.UnitMovements, body);
            // Frame.Encode enforces the body cap again (ZoneFrame.cs:16-17); a false queue
            // means notifications are closed (scene teardown, ZoneUnitRegistry.cs:75) — count
            // only what was actually accepted.
            if (_registry.QueueNotification(frame1)) queued += batch.Count;
            else _diagnostics.ReportSkippedActor(batch[0].BcId,
                "ZWUnitMovements frame dropped: notification queue is closed (scene teardown).");
            batch.Clear();
            bodyBytes = sizeof(int);
        }
    }

    /// <summary>Forget a unit's suppression state and any pending entry (retirement/scene cleanup).</summary>
    public void Discard(ZoneUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        _states.Remove(unit);
        _pending.RemoveAll(entry => ReferenceEquals(entry.Unit, unit));
    }

    private EmitState GetState(ZoneUnit unit)
    {
        if (!_states.TryGetValue(unit, out var state)) _states[unit] = state = new EmitState();
        return state;
    }

    // ------------------------------------------------------------------
    // quantization
    // ------------------------------------------------------------------

    private QuantizedPose? Quantize(ZoneUnit unit, ZonePhysicsActorStatus status, ZoneActorMovementState admitted)
    {
        if (!Finite(status.Position) || !Finite(status.Velocity) ||
            !Finite(new System.Numerics.Vector3(status.Orientation.X, status.Orientation.Y, status.Orientation.Z)) ||
            !float.IsFinite(status.Orientation.W) || status.Orientation.LengthSquared() <= 0)
        {
            _diagnostics.ReportSkippedActor(unit.Snapshot.ObjectId,
                "encoder sample rejected: authoritative status carries non-finite values.");
            return null;
        }

        // position — 11-byte quantized world position (Helpers.ConvertPosition, the exact
        // float→bytes direction of PacketStream.WritePosition :1318-1341).
        var position = Helpers.ConvertPosition(status.Position.X, status.Position.Y, status.Position.Z);
        if (position is not { Length: 11 })
        {
            _diagnostics.ReportSkippedActor(unit.Snapshot.ObjectId, "encoder sample rejected: position quantization failed.");
            return null;
        }

        // rotation — roll/pitch/yaw decomposition mirrored from MathUtil.GetYawPitchRollFromQuat
        // (AAEmu.Game Utils/MathUtil.cs:136-152), then direction units per axis mirrored from
        // PositionAndRotation.ToRollPitchYawSBytesMovement (:110-114) → ConvertRadianToDirection
        // (:313-322). Outgoing precedent: Npc.cs:1273-1283 authors the same triple for clients.
        var q = status.Orientation;
        var roll = MathF.Atan2(2f * (q.W * q.X + q.Y * q.Z), 1f - 2f * (q.X * q.X + q.Y * q.Y));
        var sinPitch = 2f * (q.W * q.Y - q.Z * q.X);
        var pitch = MathF.Abs(sinPitch) >= 1f
            ? MathF.CopySign(MathF.PI / 2f, sinPitch)
            : MathF.Asin(sinPitch);
        var yaw = MathF.Atan2(2f * (q.W * q.Z + q.X * q.Y), 1f - 2f * (q.Y * q.Y + q.Z * q.Z));

        // velocity — inverse of the kind-1 ingress decode (short / 32767 * 60, UnitMovement.cs:77).
        var velocityScale = 32767f / 60f;
        var velocityX = ClampShort(status.Velocity.X * velocityScale);
        var velocityY = ClampShort(status.Velocity.Y * velocityScale);
        var velocityZ = ClampShort(status.Velocity.Z * velocityScale);

        // delta — inverse of MovementVector8.Decode (/127, UnitMovement.cs:48), from the ADMITTED
        // state (stance/actorFlags/delta come from the selected state, PLAN section 4).
        var deltaX = ClampDelta(admitted.Delta.X);
        var deltaY = ClampDelta(admitted.Delta.Y);
        var deltaZ = ClampDelta(admitted.Delta.Z);

        var actorFlags = admitted.ActorFlags;
        if ((actorFlags & ~0x1Fu) != 0)
        {
            // FromMovement masks to 0x1F (:53); anything wider is not this wave's admitted state.
            _diagnostics.ReportRejectedInput(unit.Snapshot.ObjectId,
                $"encoder masked admitted ActorFlags 0x{actorFlags:X2} to the low 5 bits (FromMovement, ZoneActorMovementState.cs:54).");
            actorFlags = (byte)(actorFlags & 0x1F);
        }

        return new QuantizedPose(
            position,
            ToDirection(roll), ToDirection(pitch), ToDirection(yaw),
            velocityX, velocityY, velocityZ,
            deltaX, deltaY, deltaZ,
            admitted.Stance, admitted.Alertness, actorFlags, MoveFlags: 0,
            Grounded: status.IsGrounded,
            JumpBits: QuantizedPose.JumpBitsOf(actorFlags));
    }

    /// <summary>
    /// MoveTypeFlags byte (AAEmu.Game StaticValues/MoveTypeFlags.cs:3-25): Moving when the
    /// quantized velocity looks alive, Stopping on the moving→still TRANSITION (this emission's
    /// own state — a suppressed frame never reports a transition), Jumping (= Moving|Stopping)
    /// while the admitted jump bits are set, InCombat from the unit's combat flag — the field
    /// combination outgoing World writers author (Npc.cs:1284-1285).
    /// </summary>
    private byte MoveFlagsFor(ZoneUnit unit, in QuantizedPose pose, EmitState state)
    {
        var moving = VelocityLooksMoving(pose);
        byte flags = 0;
        if (moving) flags |= 0x02; // Moving
        else if (state.WasMoving) flags |= 0x04; // Stopping — transition only
        if (pose.JumpBits != 0) flags |= 0x06; // Jumping = Moving | Stopping (MoveTypeFlags.cs:24)
        if (unit.IsInCombat) flags |= 0x08; // InCombat
        return flags;
    }

    /// <summary>Alive threshold for the MoveFlags bit: one quantization step of the 60 m/s scale is ~0.018 m/s — anything ≥ 0.05 m/s is moving.</summary>
    private static bool VelocityLooksMoving(in QuantizedPose pose)
    {
        const float threshold = 0.05f * (32767f / 60f);
        return Math.Abs(pose.VelocityX) >= threshold ||
               Math.Abs(pose.VelocityY) >= threshold ||
               Math.Abs(pose.VelocityZ) >= threshold;
    }

    /// <summary>Mirror of MathUtil.ConvertRadianToDirection (AAEmu.Game Utils/MathUtil.cs:313-322), including its −2π caller shift (PositionAndRotation.cs:110-114) and the res &gt; 85 correction — this file cannot reference AAEmu.Game, so the arithmetic is quoted, not linked.</summary>
    private static sbyte ToDirection(double radians)
    {
        var degree = (radians - 2.0 * Math.PI) * (180.0 / Math.PI);
        if (degree < 0) degree = 360 + degree;
        var result = (sbyte)(degree / (360f / 128));
        if (result > 85) result = (sbyte)((degree - 360) / (360f / 128));
        return result;
    }

    private static short ClampShort(float value) =>
        value >= short.MaxValue ? short.MaxValue : value <= short.MinValue ? short.MinValue : (short)MathF.Round(value);

    private static sbyte ClampDelta(float value)
    {
        var scaled = MathF.Round(value * 127f);
        return scaled >= sbyte.MaxValue ? sbyte.MaxValue : scaled <= sbyte.MinValue ? sbyte.MinValue : (sbyte)scaled;
    }

    private static bool Finite(System.Numerics.Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    // ------------------------------------------------------------------
    // wire entry — field-for-field mirror of UnitMoveType.Read
    // (MovementRelay.cs:330-333 → AAEmu.Game UnitMoveType.cs:28-65) written
    // with the PacketStream primitives the relay parses with
    // ------------------------------------------------------------------
    private static byte[] EncodeEntry(uint bcId, in QuantizedPose pose, uint timeMilliseconds)
    {
        var entry = new PacketStream();
        entry.WriteBc(bcId);            // 24-bit bc id (PacketStream.cs:1165-1168)
        entry.Write((byte)1);           // type byte: MoveTypeEnum.Unit (MoveType.cs:13)
        entry.Write(timeMilliseconds);  // MoveType.Time u32 (MoveType.cs:45) — scene-clock ms
        entry.Write(pose.MoveFlags);    // MoveTypeFlags byte (MoveType.cs:58)
        entry.Write(pose.Position, appendSize: false); // the 11 already-quantized position bytes, raw (the same raw append the relay uses for entry bodies, MovementRelay.cs:429-432) — byte-exact, never re-quantized
        entry.Write(pose.VelocityX);    // i16 ×3 (UnitMoveType.cs:32-34)
        entry.Write(pose.VelocityY);
        entry.Write(pose.VelocityZ);
        entry.Write(pose.RotationX);    // sbyte ×3 direction units (:35-37)
        entry.Write(pose.RotationY);
        entry.Write(pose.RotationZ);
        entry.Write(pose.DeltaX);       // sbyte ×3 (:38-41)
        entry.Write(pose.DeltaY);
        entry.Write(pose.DeltaZ);
        entry.Write((byte)pose.Stance); // (:42, written as byte at :81)
        entry.Write((byte)pose.Alertness); // (:43, read as byte at :43)
        entry.Write(pose.ActorFlags);   // u16 (:44) — conditional gates all clear
        return entry.GetBytes();
    }
}

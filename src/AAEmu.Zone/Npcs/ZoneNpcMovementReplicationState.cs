using System.Numerics;
using AAEmu.Zone.Hosting;

namespace AAEmu.Zone.Npcs;

/// <summary>Live393405B0 CVars; skip counters count this NPC's eligible calls, not milliseconds.</summary>
public sealed record ZoneNpcMovementReplicationSettings(bool MovementSkip = true, int SyncPeriodMilliseconds = 100,
    int SkipDisabledAi = 2000, int SkipStanding = 400, bool FastestSync = false)
{
    public static ZoneNpcMovementReplicationSettings From(ZoneEngineCVars values) => new(
        values.GetBool(ZoneEngineCVars.NpcMovementSkip, true), values.GetInt(ZoneEngineCVars.NpcMoveSyncPeriod, 100),
        values.GetInt(ZoneEngineCVars.NpcMoveSkipDisabledAi, 2000), values.GetInt(ZoneEngineCVars.NpcMoveSkipStanding, 400),
        values.GetBool(ZoneEngineCVars.NpcEnableFastestSync, false));
}

public sealed record ZoneNpcMovementReplicationInput(Vector3 Position, Quaternion Rotation, Vector3 Velocity,
    sbyte Stance, uint ControllerId = 0, byte ControllerPhase = 0, bool KeepSyncing = false,
    bool? GuardedAllowStrafing = null, bool ModelCanReplicate = true);

public sealed record ZoneNpcMovementReplicationCache(uint LastSync, int DisabledCalls, int StandingCalls,
    Vector3 Position, Quaternion Rotation, Vector3 Velocity, sbyte Stance, uint ControllerId, byte ControllerPhase,
    bool CachedAllowStrafing);

public readonly record struct ZoneNpcMovementReplicationDecision(bool Send, int ChangeCount, byte AdditionalMovementFlags);

/// <summary>
/// Actual per-NPC39345000/393405B0 cache and3933B510 reset. Owns no clocks, pose, controller
/// or network. The scene supplies copied model/CActor producers and consumes a Send immediately.
/// </summary>
public sealed class ZoneNpcMovementReplicationState
{
    private uint _lastSync;
    private int _disabledCalls, _standingCalls;
    private Vector3 _position, _velocity;
    private Quaternion _rotation = Quaternion.Identity;
    private sbyte _stance = -1;
    private uint _controllerId;
    private byte _controllerPhase;
    private bool _allowStrafing;

    public ZoneNpcMovementReplicationState(uint disabledRandom, uint standingRandom)
    {
        _disabledCalls = (int)(((ulong)disabledRandom * 100) >> 32);
        _standingCalls = (int)(((ulong)standingRandom * 100) >> 32);
    }

    public ZoneNpcMovementReplicationCache CopyState() => new(_lastSync, _disabledCalls, _standingCalls,
        _position, _rotation, _velocity, _stance, _controllerId, _controllerPhase, _allowStrafing);

    public void Reset(ZoneNpcMovementReplicationSettings settings)
    {
        _disabledCalls = settings.SkipDisabledAi; _standingCalls = settings.SkipStanding; _lastSync = 0;
    }

    public ZoneNpcMovementReplicationDecision Sample(ZoneNpcMovementReplicationInput source, bool active,
        uint gameMilliseconds, ulong characterCount, ZoneNpcMovementReplicationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(settings);
        if (settings.FastestSync) Reset(settings);
        if (!active)
        {
            var old = _disabledCalls; _disabledCalls = unchecked(old + 1);
            if (old < settings.SkipDisabledAi) return default;
        }
        var elapsed = unchecked(gameMilliseconds - _lastSync);
        var period = unchecked((uint)settings.SyncPeriodMilliseconds);
        if (elapsed < period) return default;
        if (settings.MovementSkip)
        {
            //Native converts the unsigned tree size to f32 and truncates count*.2f, capped80.
            var count = (float)unchecked((long)characterCount);
            if ((long)characterCount < 0) count += 18446744073709551616f;
            var scaled = count * .2f;
            //CVTTSS2SI64 precedes the low32-bit unsigned cap, even for very large tree sizes.
            var extra = Math.Min(unchecked((uint)(long)scaled), 80u);
            if (elapsed < unchecked(period + extra)) return default;
        }
        if (!source.ModelCanReplicate) return default;
        var changes = _stance != source.Stance ? 1 : 0;
        if (!Near(source.Position, _position, .01f)) changes++;
        if (!Near(source.Velocity, _velocity, .01f) || source.Velocity == Vector3.Zero && _velocity != Vector3.Zero) changes++;
        if (!Near(_rotation, source.Rotation, .00001f)) changes++;
        if (source.KeepSyncing) changes++;
        if (_controllerId != source.ControllerId || _controllerPhase != source.ControllerPhase) changes += 10;
        //3934B1F0 -> IAIActor16 -> SAIActorState964 is AllowStrafing, guarded by
        //the cached NPC-template ActorModel.DBSlopeAlignment bit. It is not a tilt angle.
        if (source.GuardedAllowStrafing is { } strafe && _allowStrafing != strafe)
        { changes++; _allowStrafing = strafe; }
        if (active && changes == 0)
        {
            var old = _standingCalls; _standingCalls = unchecked(old + 1);
            if (old < settings.SkipStanding) return default;
        }
        _stance = source.Stance; _position = source.Position; _rotation = source.Rotation; _velocity = source.Velocity;
        _controllerId = source.ControllerId; _controllerPhase = source.ControllerPhase;
        _disabledCalls = _standingCalls = 0; _lastSync = gameMilliseconds;
        return new(true, changes, (byte)((changes >= 10 ? 4 : 0) | (_allowStrafing ? 8 : 0)));
    }

    private static bool Near(Vector3 a, Vector3 b, float tolerance) =>
        MathF.Abs(a.X - b.X) <= tolerance && MathF.Abs(a.Y - b.Y) <= tolerance && MathF.Abs(a.Z - b.Z) <= tolerance;
    private static bool Near(Quaternion a, Quaternion b, float tolerance) =>
        Same(a, b, tolerance) || Same(a, new(-b.X, -b.Y, -b.Z, -b.W), tolerance);
    private static bool Same(Quaternion a, Quaternion b, float tolerance) =>
        MathF.Abs(a.X - b.X) <= tolerance && MathF.Abs(a.Y - b.Y) <= tolerance &&
        MathF.Abs(a.Z - b.Z) <= tolerance && MathF.Abs(a.W - b.W) <= tolerance;
}

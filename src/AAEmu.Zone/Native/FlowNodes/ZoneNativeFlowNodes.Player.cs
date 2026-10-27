using System.Numerics;

namespace AAEmu.Zone.Native.FlowNodes;

/// <summary>Actual engine/player services used by the two native singleton player nodes.</summary>
internal interface IZoneNativeFlowPlayerServices
{
    void ValidateForRegistration();
    nint FindEntity(uint entityId);
    uint ReadEntityId(nint entity);
    IZoneNativeFlowPlayerOperations? GetClientPlayer();
    void ReportBooleanPortMismatch(nint node, int port, string portName, int actualType);
    void ReportInvalidStance(int stance);
}

/// <summary>
/// Operations supplied by the actual P1-owned player identified by the native ActorSystem.
/// A missing player may be null; missing implementations for a present player are errors.
/// The operation owner remains retained through the entire current engine callback.
/// </summary>
internal interface IZoneNativeFlowPlayerOperations
{
    /// <summary>The actual player quaternion at original +340, without normalization.</summary>
    Quaternion ReadStagingRotation();

    /// <summary>The original +217 byte. The node compares it with exact 0/1 values.</summary>
    byte DrawHiddenState { get; }

    /// <summary>Write original +217 to 0/1 and +218 to 1, without other visibility changes.</summary>
    void SetDrawHiddenState(bool hidden);

    /// <summary>The actual actor +258 operation, including its attachment/transform lifetime.</summary>
    void LinkToEntity(uint targetEntityId, bool keepTransformOnDetach);

    /// <summary>Apply the complete original 392FDC10 operation, rather than only caching its inputs.</summary>
    void ApplyStaging(bool enabled, ZoneNativeFlowPlayerStagingParameters parameters);
}

/// <summary>
/// Original staging payload: byte +0, lock byte +1, direction +4, yaw +10, pitch +14,
/// stance +18. The node always initializes the first byte false; the real player owns
/// how 392FDC10 consumes these fields and updates movement, view limits and stance.
/// </summary>
internal readonly record struct ZoneNativeFlowPlayerStagingParameters(
    bool NativeFlag0, bool LockPosition, Vector3 Direction, float Yaw, float Pitch, int TryStance);

internal static class ZoneNativeFlowPlayerNodes
{
    internal static IZoneNativeFlowNodeBehavior CreateFreezeEntity() => new FreezeEntity();

    internal static IZoneNativeFlowNodeBehavior CreatePlayerStaging(IZoneNativeFlowPlayerServices services)
    { ArgumentNullException.ThrowIfNull(services); return new PlayerStaging(services); }

    internal static IZoneNativeFlowNodeBehavior CreatePlayerLink(IZoneNativeFlowPlayerServices services)
    { ArgumentNullException.ThrowIfNull(services); return new PlayerLink(services); }

    private abstract class PlayerBehavior(IZoneNativeFlowPlayerServices services)
        : IZoneNativeFlowNodeBehavior, IZoneNativeFlowNodeStorageBehavior
    {
        private nint _node;
        protected IZoneNativeFlowPlayerServices Services { get; } = services;
        public abstract ZoneNativeFlowNodeConfiguration Configuration { get; }
        public abstract void ProcessEvent(int eventId, ZoneNativeFlowActivation activation);
        public bool SerializeXml(ZoneNativeFlowActivation activation, nint xmlNodeReference, bool loading) => true;
        public void Serialize(ZoneNativeFlowActivation activation, nint serializer) { }
        public void Destroy() { }

        public void BindNativeStorage(nint node, int byteLength)
        {
            if (_node != 0 || node == 0 || byteLength != 0x10)
                throw new InvalidOperationException("The singleton player node requires its own actual 0x10-byte interface.");
            _node = node;
        }

        protected bool ReadBoolean(ZoneNativeFlowActivation activation, int port)
        {
            //39318410 uses exact bool tag7. A mismatch logs and returns false; it does not
            // use conversion, read a float/int payload as bool, or terminate the callback.
            if (activation.TryGetBoolean(port, out var value)) return value;
            Services.ReportBooleanPortMismatch(_node, port, Configuration.Inputs[port].Name,
                activation.GetNativePortType(port));
            return false;
        }
    }

    private sealed class PlayerLink(IZoneNativeFlowPlayerServices services) : PlayerBehavior(services)
    {
        private static readonly ZoneNativeFlowNodeConfiguration NodeConfiguration = new(
            Array.AsReadOnly(new ZoneNativeFlowInputPort[]
            {
                new("Link", ZoneNativeFlowValue.Void(), "Link the Player to Target Entity"),
                new("Unlink", ZoneNativeFlowValue.Void(), "Unlink the Player (from any Entity)"),
                new("Target", ZoneNativeFlowValue.EntityId(0), "Target Entity Id"),
                new("DrawPlayer", ZoneNativeFlowValue.Int32(0), "Draw the player", null,
                    "enum_int:NoChange=0,Hide=-1,Show=1"),
                new("KeepTransfromDetach", ZoneNativeFlowValue.Boolean(true), "Keep Transformation on Detach")
            }),
            Array.AsReadOnly(new ZoneNativeFlowOutputPort[]
            {
                new("Linked", 0, "Trigger if Linked"), new("Unlinked", 0, "Trigger if Unlinked")
            }), "Linking the Player to an Entity (with FreeLook)", 0xfffff08f, 0x80);

        public override ZoneNativeFlowNodeConfiguration Configuration => NodeConfiguration;

        public override void ProcessEvent(int eventId, ZoneNativeFlowActivation activation)
        {
            if (eventId != 1) return;
            //3931A240 reads DrawPlayer before either trigger, then handles both branches
            // independently. The second lookup must observe any changes caused by Link.
            var drawPlayer = activation.GetInt32(3);
            if (activation.IsPortActive(0))
            {
                var target = Services.FindEntity(activation.GetEntityId(2));
                if (target != 0 && Services.GetClientPlayer() is { } player)
                {
                    ApplyDrawPlayer(player, drawPlayer);
                    player.LinkToEntity(Services.ReadEntityId(target), true);
                    activation.ActivateBooleanOutput(0, true);
                }
            }
            if (activation.IsPortActive(1) && Services.GetClientPlayer() is { } unlinkPlayer)
            {
                ApplyDrawPlayer(unlinkPlayer, drawPlayer);
                unlinkPlayer.LinkToEntity(0, ReadBoolean(activation, 4));
                activation.ActivateBooleanOutput(1, true);
            }
        }

        private static void ApplyDrawPlayer(IZoneNativeFlowPlayerOperations player, int value)
        {
            if (value == -1)
            {
                if (player.DrawHiddenState != 1) player.SetDrawHiddenState(true);
            }
            else if (value == 1 && player.DrawHiddenState != 0) player.SetDrawHiddenState(false);
        }
    }

    private sealed class PlayerStaging(IZoneNativeFlowPlayerServices services) : PlayerBehavior(services)
    {
        private static readonly ZoneNativeFlowNodeConfiguration NodeConfiguration = new(
            Array.AsReadOnly(new ZoneNativeFlowInputPort[]
            {
                new("Trigger", ZoneNativeFlowValue.Void(), "Trigger"),
                new("ViewLimitDir", ZoneNativeFlowValue.Vector3(default), "ViewLimitDir"),
                new("InLocalSpace", ZoneNativeFlowValue.Boolean(true), "ViewLimit Vector is in Local Space or World Space"),
                new("ViewLimitYaw", ZoneNativeFlowValue.Float(0), "ViewLimitYaw (0.0=FreeLook, 0.001=Lock)"),
                new("ViewLimitPitch", ZoneNativeFlowValue.Float(0), "ViewLimitPitch (0.0=Freelook, 0.001=Lock)"),
                new("LockPlayer", ZoneNativeFlowValue.Boolean(false), "Lock the player's position"),
                new("TryStance", ZoneNativeFlowValue.Int32(-1),
                    "Try to set Stance on Locking [works only if Player was linked beforehand]", null,
                    "enum_int:<ignore>=-1,Stand=0,Crouch=1,Prone=2,Relaxed=3,Stealth=4,Swim=5,ZeroG=6")
            }),
            Array.AsReadOnly(new ZoneNativeFlowOutputPort[] { new("Done", 0, "Trigger for Chaining") }),
            "Staging of the Player - ViewLimits and Position Lock", 0xfffff08f, 0x80);

        public override ZoneNativeFlowNodeConfiguration Configuration => NodeConfiguration;

        public override void ProcessEvent(int eventId, ZoneNativeFlowActivation activation)
        {
            if (eventId != 1 || !activation.IsPortActive(0)) return;
            var requestedDirection = activation.GetVector3(1);
            var inLocalSpace = ReadBoolean(activation, 2);
            var yaw = activation.GetFloat(3);
            var pitch = activation.GetFloat(4);
            if (Services.GetClientPlayer() is { } player)
            {
                var direction = Vector3.Zero;
                var lengthSquared = (requestedDirection.X * requestedDirection.X + requestedDirection.Y * requestedDirection.Y)
                    + requestedDirection.Z * requestedDirection.Z;
                if (lengthSquared > BitConverter.Int32BitsToSingle(0x3c23d70a))
                {
                    direction = inLocalSpace
                        ? Normalize(Rotate(player.ReadStagingRotation(), requestedDirection), local: true)
                        : Normalize(requestedDirection, local: false);
                }
                var radians = BitConverter.Int32BitsToSingle(0x3c8efa35);
                yaw *= radians;
                pitch *= radians;
                var locked = ReadBoolean(activation, 5);
                var stance = activation.GetInt32(6);
                if (stance < -1 || stance >= 9)
                {
                    //3931A11E overwrites the invalid value before logging it. Native emits
                    // -1 in this diagnostic, and stores -1 as the resulting requested stance.
                    stance = -1;
                    Services.ReportInvalidStance(stance);
                }
                var hasDirection = !(MathF.Abs(direction.X) <= 0 && MathF.Abs(direction.Y) <= 0 && MathF.Abs(direction.Z) <= 0);
                var enabled = locked || (hasDirection && NonzeroBits(yaw) && NonzeroBits(pitch));
                player.ApplyStaging(enabled, new(false, locked, direction, yaw, pitch, stance));
            }
            //3931A1DE always chains boolean FALSE after an active trigger, including when
            // the real framework returns no client player. Lock/stance are not read then.
            activation.ActivateBooleanOutput(0, false);
        }

        private static bool NonzeroBits(float value) => (BitConverter.SingleToInt32Bits(value) & 0x7fffffff) != 0;

        private static Vector3 Normalize(Vector3 value, bool local)
        {
            // Local39319FE0 sums Y²+X²; world390B3A20 sums X²+Y². Both use native sqrtf,
            // an ordered strict comparison with 2^-23, and zero fallback (including NaN).
            var x = value.X * value.X; var y = value.Y * value.Y; var z = value.Z * value.Z;
            var squared = (local ? y + x : x + y) + z;
            if (!(squared > BitConverter.Int32BitsToSingle(0x34000000))) return Vector3.Zero;
            var inverse = 1f / MathF.Sqrt(squared);
            return new(value.X * inverse, value.Y * inverse, value.Z * inverse);
        }

        private static Vector3 Rotate(Quaternion q, Vector3 v)
        {
            //390ADC00: scalar float order, without normalizing the actual player quaternion.
            var a = (q.Y * v.Z - q.Z * v.Y) + q.W * v.X;
            var b = (q.Z * v.X - q.X * v.Z) + q.W * v.Y;
            var c = (q.X * v.Y - q.Y * v.X) + q.W * v.Z;
            var x = q.Y * c - q.Z * b;
            var y = q.Z * a - q.X * c;
            var z = q.X * b - q.Y * a;
            return new((x + v.X) + x, (y + v.Y) + y, (z + v.Z) + z);
        }
    }

    /// <summary>
    /// Exact dedicated-image FreezeEntity behavior. The authored ports remain present even
    /// though native table39FD7E68 selects the actual empty392A49F0 event/serialize methods.
    /// This is not a substitute implementation for the active PlayerStaging/PlayerLink nodes.
    /// </summary>
    private sealed class FreezeEntity : IZoneNativeFlowNodeBehavior
    {
        private static readonly ZoneNativeFlowNodeConfiguration NodeConfiguration = new(
            Array.AsReadOnly(new ZoneNativeFlowInputPort[]
            {
                new("Freeze", ZoneNativeFlowValue.Void(), "Trigger to freeze entity"),
                new("UnFreeze", ZoneNativeFlowValue.Void(), "Trigger to un-freeze entity"),
                new("UseVapor", ZoneNativeFlowValue.Boolean(true), "Trigger to un-freeze entity")
            }),
            Array.AsReadOnly(new ZoneNativeFlowOutputPort[]
            {
                new("Frozen", 0, "Triggered on Freeze"),
                new("UnFrozen", 0, "Triggered on UnFreeze")
            }),
            "Freezes/Unfreezes attached entity/actor.", 0xfffff08f, 0x81);

        public ZoneNativeFlowNodeConfiguration Configuration => NodeConfiguration;

        public void ProcessEvent(int eventId, ZoneNativeFlowActivation activation) { }

        public bool SerializeXml(ZoneNativeFlowActivation activation, nint xmlNodeReference, bool loading) => true;

        public void Serialize(ZoneNativeFlowActivation activation, nint serializer) { }

        public void Destroy() { }
    }
}

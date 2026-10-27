using System.Runtime.InteropServices;

namespace AAEmu.Zone.Native.FlowNodes;

/// <summary>
/// Real actor-system services for Game:ActorSensor. Lookups preserve 39316630: full unsigned
/// entity ID, actual framework actor-system lookup, then exact CPlayer type-token identity.
/// A missing actor or a different actor type returns null. Missing framework/actor-system
/// ownership is an admission error, rather than a fabricated missing-player result.
/// </summary>
internal interface IZoneNativeFlowActorSensorServices
{
    void ValidateForRegistration();
    IZoneNativeFlowActorSensorPlayer? FindPlayer(uint entityId);
}

/// <summary>
/// A resolved actual player. Native39302320 adds a listener once by listener identity;
/// 393008C0 removes that exact listener if present. Neither operation emits a callback.
/// The owner retains registered listeners until removal or the actual player's retirement.
/// Callbacks run synchronously under the same engine gate, with exceptions contained before
/// returning to native code. Retired players cannot deliver callbacks into released nodes.
/// </summary>
internal interface IZoneNativeFlowActorSensorPlayer
{
    void AddListener(IZoneNativeFlowActorSensorListener listener);
    void RemoveListener(IZoneNativeFlowActorSensorListener listener);
}

/// <summary>
/// The seven nonempty callbacks in listener table39FD7018. The first three native listener
/// slots are empty in this dedicated image; they do not emit the declared vehicle outputs.
/// These callbacks must be sourced from the real player event owner, never replacement events.
/// </summary>
internal interface IZoneNativeFlowActorSensorListener
{
    void OnItemDropped(uint itemEntityId);
    void OnItemPickedUp(uint itemEntityId);
    void OnItemUsed(uint itemEntityId);
    void OnStanceChanged(int stance);
    void OnSpecialMove(int move);
    void OnDeath(bool isGod);
    void OnGrabChanged(bool grabbed, uint entityId, bool isNpc);
}

/// <summary>Dedicated-image sensor behaviors; native interface and reference ownership belong to the common adapter.</summary>
internal static unsafe class ZoneNativeFlowSensorNodes
{
    internal static IZoneNativeFlowNodeBehavior CreateActorSensor(IZoneNativeFlowActorSensorServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return new ActorSensor(services);
    }

    internal static IZoneNativeFlowNodeBehavior CreateHitInfo() => new Information(HitConfiguration);
    internal static IZoneNativeFlowNodeBehavior CreateExplosionInfo() => new Information(ExplosionConfiguration);

    private static readonly ZoneNativeFlowNodeConfiguration ActorConfiguration = new(
        Array.AsReadOnly(new ZoneNativeFlowInputPort[]
        {
            new("Enable", ZoneNativeFlowValue.Void(), "Trigger to enable"),
            new("Disable", ZoneNativeFlowValue.Void(), "Trigger to enable")
        }),
        Array.AsReadOnly(new ZoneNativeFlowOutputPort[]
        {
            new("EnterVehicle", 4, "Triggered when entering a vehicle"),
            new("ExitVehicle", 4, "Triggered when exiting a vehicle"),
            new("SeatChange", 1, "Triggered when seat has changed"),
            new("ItemPickedUp", 4, "Triggered when an item is picked up"),
            new("ItemDropped", 4, "Triggered when an item is dropped"),
            new("ItemUsed", 4, "Triggered when an item is used"),
            new("NPCGrabbed", 4, "Triggered when an NPC is grabbed"),
            new("NPCThrown", 4, "Triggered when an NPC is thrown"),
            new("ObjectGrabbed", 4, "Triggered when an object is grabbed"),
            new("ObjectThrown", 4, "Triggered when an object is thrown"),
            new("StanceChanged", 1,
                "Triggered when Stance changed. 0=Stand,1=Crouch,2=Prone,3=Relaxed,4=Stealth,5=Swim,6=ZeroG"),
            new("SpecialMove", 1, "Triggered On SpecialMove. 0=Jump,1=SpeedSprint,2=StrengthJump"),
            new("OnDeath", 1, "Triggered when Actor dies. Outputs 0 if not god. 1 if god.")
        }),
        "Tracks the attached Entity and its Vehicle-related actions", 0xfffff08f, 0x81);

    private static readonly ZoneNativeFlowNodeConfiguration HitConfiguration = new(
        Array.AsReadOnly(new ZoneNativeFlowInputPort[]
        {
            new("Enable", ZoneNativeFlowValue.Boolean(false), "Enable/Disable HitInfo"),
            new("ShooterId", ZoneNativeFlowValue.EntityId(0), "When connected, limit HitInfo to this shooter"),
            new("TargetId", ZoneNativeFlowValue.EntityId(0), "When connected, limit HitInfo to this target"),
            new("Weapon", ZoneNativeFlowValue.String(""), "When set, limit HitInfo to this weapon", null, "enum_global:weapon"),
            new("Ammo", ZoneNativeFlowValue.String(""), "When set, limit HitInfo to this ammo", null, "enum_global:ammos")
        }),
        Array.AsReadOnly(new ZoneNativeFlowOutputPort[]
        {
            new("ShooterId", 4, "EntityID of the Shooter"),
            new("TargetId", 4, "EntityID of the Target"),
            new("WeaponId", 4, "EntityID of the Weapon"),
            new("ProjectileId", 4, "EntityID of the Projectile if it was a bullet hit"),
            new("HitPos", 5, "Position of the Hit"),
            new("HitDir", 5, "Direction of the Hit"),
            new("HitNormal", 5, "Normal of the Hit Impact"),
            new("HitType", 6, "Name of the HitType"),
            new("Damage", 3, "Damage amout which was caused"),
            new("Material", 6, "Name of the Material")
        }),
        "Tracks Hits on Actors.\nAll input conditions (ShooterId, TargetId, Weapon, Ammo) must be fulfilled to output.\nIf a condition is left empty/not connected, it is regarded as fulfilled.",
        0xfffff08f, 0x80);

    private static readonly ZoneNativeFlowNodeConfiguration ExplosionConfiguration = new(
        Array.AsReadOnly(new ZoneNativeFlowInputPort[]
        {
            new("Enable", ZoneNativeFlowValue.Boolean(false), "Enable/Disable ExplosionInfo"),
            new("ShooterId", ZoneNativeFlowValue.EntityId(0), "When connected, limit ExplosionInfo to this shooter"),
            new("Weapon", ZoneNativeFlowValue.String(""), "!DONT USE! -> Use Ammo!", "!DONT USE! -> Use Ammo!", "enum_global:ammos"),
            new("Ammo", ZoneNativeFlowValue.String(""), "When set, limit ExplosionInfo to this ammo", null, "enum_global:ammos"),
            new("ImpactTargetId", ZoneNativeFlowValue.EntityId(0),
                "When connected, limit ExplosionInfo to this Impact target (e.g. for Rockets)")
        }),
        Array.AsReadOnly(new ZoneNativeFlowOutputPort[]
        {
            new("ShooterId", 4, "EntityID of the Shooter"),
            new("Ammo", 6, "Ammo Class"),
            new("Pos", 5, "Position of the Explosion"),
            new("Dir", 5, "Direction of the Explosion"),
            new("Radius", 3, "Radius of Explosion"),
            new("Damage", 3, "Damage amout which was caused"),
            new("Pressure", 3, "Pressure amout which was caused"),
            new("HoleSize", 3, "Hole size which was caused"),
            new("Type", 6, "Name of the Explosion type"),
            new("ImpactTargetId", 4, "EntityID of the Impact Target (e.g. for Rockets)"),
            new("ImpactNormal", 5, "Impact Normal (e.g. for Rockets)"),
            new("ImpactVelocity", 3, "Impact Normal (e.g. for Rockets)")
        }),
        "Tracks Explosions.\nAll input conditions (ShooterId, Ammo, ImpactTargetId) must be fulfilled to output.\nIf a condition is left empty/not connected, it is regarded as fulfilled.",
        0xfffff08f, 0x80);

    private sealed class ActorSensor(IZoneNativeFlowActorSensorServices services) :
        IZoneNativeFlowNodeBehavior, IZoneNativeFlowActorSensorListener
    {
        private uint _entityId;
        private uint _vehicleId;
        private ZoneNativeFlowActivation _activation = ZoneNativeFlowActivation.Empty;

        public ZoneNativeFlowNodeConfiguration Configuration => ActorConfiguration;

        public void ProcessEvent(int eventId, ZoneNativeFlowActivation activation)
        {
            if (eventId == 3)
            {
                _activation = activation;
                return;
            }
            if (eventId != 1) return;
            // Native393168C0 deliberately handles Disable before Enable in the same event.
            if (activation.IsPortActive(1)) RemoveCurrentListener();
            if (!activation.IsPortActive(0)) return;
            RemoveCurrentListener();
            _entityId = ReadAttachedEntityId(activation.Entity);
            AddCurrentListener();
        }

        private void RemoveCurrentListener()
        {
            if (_entityId == 0) return;
            services.FindPlayer(_entityId)?.RemoveListener(this);
            _entityId = 0;
            _vehicleId = 0; // The native operation clears the full qword at node+18.
        }

        private void AddCurrentListener()
        {
            if (_entityId == 0) return;
            var player = services.FindPlayer(_entityId);
            if (player is not null) player.AddListener(this);
            else _entityId = 0; // A missing restored player does not clear the vehicle field.
        }

        public bool SerializeXml(ZoneNativeFlowActivation activation, nint xmlNodeReference, bool loading) => true;

        public void Serialize(ZoneNativeFlowActivation activation, nint serializer)
        {
            // Preserve both IsReading calls, field order and the lookup/remove/restore order.
            if (SerializerIsReading(serializer)) RemoveCurrentListener();
            SerializeUInt32(serializer, "m_entityId", ref _entityId);
            SerializeUInt32(serializer, "m_vehicleId", ref _vehicleId);
            if (SerializerIsReading(serializer)) AddCurrentListener();
        }

        public void Destroy()
        {
            RemoveCurrentListener();
            _activation = default;
        }

        public void OnItemDropped(uint itemEntityId) => _activation.ActivateEntityIdOutput(4, itemEntityId);
        public void OnItemPickedUp(uint itemEntityId) => _activation.ActivateEntityIdOutput(3, itemEntityId);
        public void OnItemUsed(uint itemEntityId) => _activation.ActivateEntityIdOutput(5, itemEntityId);
        public void OnStanceChanged(int stance) => _activation.ActivateInt32Output(10, stance);
        public void OnSpecialMove(int move) => _activation.ActivateInt32Output(11, move);
        public void OnDeath(bool isGod) => _activation.ActivateInt32Output(12, isGod ? 1 : 0);
        public void OnGrabChanged(bool grabbed, uint entityId, bool isNpc) =>
            _activation.ActivateEntityIdOutput((byte)(isNpc ? grabbed ? 6 : 7 : grabbed ? 8 : 9), entityId);
    }

    private sealed class Information(ZoneNativeFlowNodeConfiguration configuration) : IZoneNativeFlowNodeBehavior
    {
        private ZoneNativeFlowActivation _activation = ZoneNativeFlowActivation.Empty;
        public ZoneNativeFlowNodeConfiguration Configuration => configuration;

        public void ProcessEvent(int eventId, ZoneNativeFlowActivation activation)
        {
            // Both dedicated tables select393191E0. There is no listener registration,
            // Enable handling, filtering or output dispatch in that actual event body.
            if (eventId == 3) _activation = activation;
        }

        public bool SerializeXml(ZoneNativeFlowActivation activation, nint xmlNodeReference, bool loading) => true;

        // Actual39319220 queries reading mode once and serializes no fields.
        public void Serialize(ZoneNativeFlowActivation activation, nint serializer) => _ = SerializerIsReading(serializer);
        public void Destroy() => _activation = default;
    }

    private static uint ReadAttachedEntityId(nint entity) => entity == 0 ? 0 :
        Marshal.GetDelegateForFunctionPointer<EntityIdCall>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(entity)))(entity);

    private static bool SerializerIsReading(nint serializer) =>
        Marshal.GetDelegateForFunctionPointer<SerializerIsReadingCall>(
            Marshal.ReadIntPtr(Marshal.ReadIntPtr(serializer) + 0x40))(serializer) != 0;

    private static void SerializeUInt32(nint serializer, string name, ref uint value)
    {
        var nativeName = Marshal.StringToCoTaskMemUTF8(name);
        try
        {
            fixed (uint* pointer = &value)
                Marshal.GetDelegateForFunctionPointer<SerializerUInt32Call>(
                    Marshal.ReadIntPtr(Marshal.ReadIntPtr(serializer) + 0x80))(serializer, nativeName, pointer, 0);
        }
        finally { Marshal.FreeCoTaskMem(nativeName); }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint EntityIdCall(nint entity);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate byte SerializerIsReadingCall(nint serializer);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SerializerUInt32Call(
        nint serializer, nint name, uint* value, uint policy);
}

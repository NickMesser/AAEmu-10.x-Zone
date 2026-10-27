namespace AAEmu.Zone.Protocol;

public static class ZwOpcodes
{
    public const ushort Join = 0x0000;
    public const ushort CommandResponse = 0x0001;
    public const ushort SpawnNpc = 0x0002;
    public const ushort RemoveNpc = 0x0003;
    public const ushort SaveInstanceSpawner = 0x0005;
    public const ushort NpcSaid = 0x0006;
    public const ushort RemoveHouse = 0x0007;
    public const ushort UnitMovements = 0x0008;
    /// <summary>Native ctor site 0x392291B0 stores 9.</summary>
    public const ushort SkillControllerState = 0x0009;
    public const ushort CreateBuff = 0x000A;
    public const ushort RemoveBuff = 0x000B;
    /// <summary>Native ctor site 0x393534D0 stores 12.</summary>
    public const ushort ClearCombat = 0x000C;
    public const ushort MakeAggroTargetHostile = 0x000D;
    public const ushort UnitModelPostureChanged = 0x000F;
    public const ushort UnitFell = 0x0010;
    public const ushort UnitCollision = 0x0011;
    public const ushort UnitCollisionResult = 0x0012;
    public const ushort ErrorMsgToPlayer = 0x0013;
    public const ushort NpcInteractionEnded = 0x0014;
    public const ushort ChangeTarget = 0x0017;
    /// <summary>Native ctor site 0x39227440 stores 24.</summary>
    public const ushort StartSkill = 0x0018;
    /// <summary>Native ctor site 0x39325940 stores 25.</summary>
    public const ushort StopSkill = 0x0019;
    /// <summary>Native ctor site 0x393259F0 stores 26.</summary>
    public const ushort StopCasting = 0x001A;
    /// <summary>Native ctor site 0x3935DD20 stores 27.</summary>
    public const ushort LongDistSkillControllerCreated = 0x001B;
    public const ushort ChangeUnitFlyState = 0x001C;
    public const ushort TimeOfDay = 0x001D;
    public const ushort DetailedTimeOfDay = 0x001E;
    public const ushort ZoneLoaded = 0x001F;
    /// <summary>Native ctor site 0x3932D1C0 stores 32.</summary>
    public const ushort SandboxOnlineHeightmapAction = 0x0020;
    /// <summary>Native ctor site 0x3932D170 stores 34.</summary>
    public const ushort SandboxOnlinePlayerPos = 0x0022;
    public const ushort EnterArea = 0x0023;
    public const ushort LeaveArea = 0x0024;
    public const ushort GimmickMovement = 0x0025;
    public const ushort RequestSpawnStaticGimmick = 0x0026;
    public const ushort GimmickJointBroken = 0x0027;
    public const ushort GimmickResetJoints = 0x0028;
    public const ushort GimmickRemoveParts = 0x0029;
    public const ushort GimmickCollided = 0x002A;
    public const ushort NpcInteractionStatusChanged = 0x002C;
    public const ushort ResponseCombatUnits = 0x002D;
    public const ushort AggroTargetChanged = 0x002E;
    /// <summary>Native ctor site 0x39365090 stores 47.</summary>
    public const ushort ReportZoneStatus = 0x002F;
    public const ushort RayCastingResult = 0x0030;
    /// <summary>Native ctor site 0x3933EA90 stores 49.</summary>
    public const ushort KillNpc = 0x0031;
    public const ushort TowerDefReportPlayability = 0x0032;
    /// <summary>Native ctor site 0x3944E590 stores 51.</summary>
    public const ushort AiDebug = 0x0033;
    public const ushort AiAggro = 0x0034;
    public const ushort UnitLocation = 0x0035;
    public const ushort BackpackDropped = 0x0036;
    public const ushort ReportMoleMiner = 0x0037;
    public const ushort ReportMoleTrader = 0x0038;
    public const ushort ReportMoveHack = 0x0039;
    public const ushort ReportDoodadSurfaceHack = 0x003A;
    /// <summary>Native ctor site 0x394547C0 stores 59 (0x3B).</summary>
    public const ushort DoodadCollideUnit = 0x003B;
    public const ushort Heartbeat = 0x003C;
    public const ushort RegisterNpcAbuser = 0x003D;
    public const ushort UnregisterNpcAbusers = 0x003E;
    public const ushort ClearNpcAbusers = 0x003F;
    /// <summary>Native ctor site 0x39454400 stores 64.</summary>
    public const ushort DumpNpc = 0x0040;
    /// <summary>Native ctor site 0x3931F790 stores 65.</summary>
    public const ushort AggroRemove = 0x0041;
}

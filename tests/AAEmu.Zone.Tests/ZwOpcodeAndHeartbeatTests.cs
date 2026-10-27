using System.Reflection;
using AAEmu.Zone.Protocol;
using AAEmu.Zone.Scene;

namespace AAEmu.Zone.Tests;

public sealed class ZwOpcodeAndHeartbeatTests
{
    // Native ZW* constructor-site opcodes (x2game-dev_dedicate.dll, r575).
    private static readonly (string Name, ushort Value)[] Native =
    [
        ("Join", 0), ("CommandResponse", 1), ("SpawnNpc", 2), ("RemoveNpc", 3), ("SaveInstanceSpawner", 5),
        ("NpcSaid", 6), ("RemoveHouse", 7), ("UnitMovements", 8), ("SkillControllerState", 9), ("CreateBuff", 10),
        ("RemoveBuff", 11), ("ClearCombat", 12), ("MakeAggroTargetHostile", 13), ("UnitModelPostureChanged", 15),
        ("UnitFell", 16), ("UnitCollision", 17), ("UnitCollisionResult", 18), ("ErrorMsgToPlayer", 19),
        ("NpcInteractionEnded", 20), ("ChangeTarget", 23), ("StartSkill", 24), ("StopSkill", 25), ("StopCasting", 26),
        ("LongDistSkillControllerCreated", 27), ("ChangeUnitFlyState", 28), ("TimeOfDay", 29), ("DetailedTimeOfDay", 30),
        ("ZoneLoaded", 31), ("SandboxOnlineHeightmapAction", 32), ("SandboxOnlinePlayerPos", 34), ("EnterArea", 35),
        ("LeaveArea", 36), ("GimmickMovement", 37), ("RequestSpawnStaticGimmick", 38), ("GimmickJointBroken", 39),
        ("GimmickResetJoints", 40), ("GimmickRemoveParts", 41), ("GimmickCollided", 42), ("NpcInteractionStatusChanged", 44),
        ("ResponseCombatUnits", 45), ("AggroTargetChanged", 46), ("ReportZoneStatus", 47), ("RayCastingResult", 48),
        ("KillNpc", 49), ("TowerDefReportPlayability", 50), ("AiDebug", 51), ("AiAggro", 52), ("UnitLocation", 53),
        ("BackpackDropped", 54), ("ReportMoleMiner", 55), ("ReportMoleTrader", 56), ("ReportMoveHack", 57),
        ("ReportDoodadSurfaceHack", 58), ("DoodadCollideUnit", 59), ("Heartbeat", 60), ("RegisterNpcAbuser", 61),
        ("UnregisterNpcAbusers", 62), ("ClearNpcAbusers", 63), ("DumpNpc", 64), ("AggroRemove", 65),
    ];

    [Test]
    public async Task ZwOpcodesMatchTheNativeTableExactly()
    {
        var actual = typeof(ZwOpcodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral)
            .Select(f => (f.Name, Value: (ushort)f.GetRawConstantValue()!))
            .ToArray();
        await Assert.That(Native.Select(n => n.Name).Distinct().Count()).IsEqualTo(Native.Length);
        await Assert.That(Native.Select(n => n.Value).Distinct().Count()).IsEqualTo(Native.Length);
        await Assert.That(actual.Length).IsEqualTo(Native.Length);
        await Assert.That(actual.OrderBy(a => a.Value).SequenceEqual(Native.OrderBy(n => n.Value))).IsTrue();
    }

    [Test]
    public async Task HeartbeatIsEmittedOncePerElapsedSecondWithTheFlag()
    {
        foreach (var flag in new[] { false, true })
        {
            var emitter = new ZoneHeartbeatEmitter(flag);
            var frames = new List<ZoneFrame>();
            // 0.25 s steps are exact in float: 3 s elapse -> frames at 1.0, 2.0, 3.0 only.
            for (var i = 1; i <= 12; i++)
            {
                var before = frames.Count;
                if (emitter.TryCreate(i * 0.25f, out var frame)) frames.Add(frame);
                await Assert.That(frames.Count - before).IsEqualTo(i % 4 == 0 ? 1 : 0);
            }
            await Assert.That(frames.Count).IsEqualTo(3);
            foreach (var frame in frames)
            {
                await Assert.That(frame.Opcode).IsEqualTo(ZwOpcodes.Heartbeat);
                await Assert.That(frame.Body.ToArray().SequenceEqual(new byte[] { flag ? (byte)1 : (byte)0 })).IsTrue();
            }
            await Assert.That(emitter.TryCreate(3.99f, out _)).IsFalse();
        }
    }
}

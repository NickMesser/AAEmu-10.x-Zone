using System.Numerics;

namespace AAEmu.Zone.Ai;

/// <summary>
/// AI-object fields shared by spatial searches and pipe-argument binding. The scene supplies entity identity and position.
/// This represents the corresponding CAIObject fields, not its complete scene or physics lifecycle.
/// </summary>
public sealed class ZoneAiObjectState : IZoneAiObjectSearchCandidate
{
    // Defaults from 3103D7F8; field names from CAIObject serialization.
    public Vector3 Position { get; set; }
    public bool Enabled { get; set; } = true;
    public float Radius { get; set; }
    public uint EntityId { get; set; }
    //3103E638 names+132 predicted position,+144 body,+156 move,+168 view; ctor zeros all.
    //Their scene producers must copy actual AI/proxy values, not infer them from desired motion.
    public Vector3 MoveDirection { get; set; }
    public Vector3 ViewDirection { get; set; }
    public Vector3 BodyDirection { get; set; }
    public Vector3 PredictedPosition { get; set; }
    // CAIObject+E0: 3100992C writes, 31009934 reads, 3103D7F8 initializes false.
    // COPUseSkill 310E6B68 uses it to report stalled AI; it is not an update-enable flag.
    public bool DiagnosticsEnabled { get; set; }

    // 31009944 writes +B4. The destructor uses this ID to diagnose an entity which still binds the object.
    public uint BindingEntityId { get; set; }
    // The independent m_pAssociation pointer is at +B8, returned by 31009894. Argument binding does not change it.
    public object? Association { get; set; }

    public Vector3 GetPosition() => Position;
    public bool IsEnabled() => Enabled;
    public float GetRadius() => Radius;
}

using System.Collections.Frozen;
using Microsoft.Data.Sqlite;
using static AAEmu.Zone.Content.NativeContentReader;
using static AAEmu.Zone.Content.ZoneActorModelContent;

namespace AAEmu.Zone.Content;

/// <summary>Every scalar consumed by native LoadActorModelDescs (39AD8AD0).</summary>
public sealed record ZoneActorModelDefinition
{
    public uint Id { get; init; }
    public float ActorHeight { get; init; }
    public bool AddBox { get; init; }
    public string AnimationGraph { get; init; } = "";
    public float AttackStartRange { get; init; }
    public float BeanstalkBack { get; init; }
    public float BoxX { get; init; }
    public float BoxY { get; init; }
    public float BoxZ { get; init; }
    public float CenterX { get; init; }
    public float CenterY { get; init; }
    public float CenterZ { get; init; }
    public bool EnableSideMovingJump { get; init; }
    public bool FaceTargetInstantly { get; init; }
    public bool FlyMode { get; init; }
    public float FxScale { get; init; }
    public float GameBackwardDiagonalMultiplier { get; init; }
    public float GameBackwardMultiplier { get; init; }
    public float GameBowLookIkBlendHead { get; init; }
    public float GameBowLookIkBlendNeck { get; init; }
    public float GameBowLookIkBlendSpine1 { get; init; }
    public float GameBowLookIkBlendSpine2 { get; init; }
    public float GameBowLookIkBlendSpine3 { get; init; }
    public float GameForwardDiagonalMultiplier { get; init; }
    public float GameForwardMultiplier { get; init; }
    public float GameGrabMultiplier { get; init; }
    public float GameInertia { get; init; }
    public float GameInertiaAccel { get; init; }
    public float GameJumpHeight { get; init; }
    public int GameLeanAngle { get; init; }
    public float GameLeanShift { get; init; }
    public float GameLookIkBlendHead { get; init; }
    public float GameLookIkBlendNeck { get; init; }
    public float GameLookIkBlendSpine1 { get; init; }
    public float GameLookIkBlendSpine2 { get; init; }
    public float GameLookIkBlendSpine3 { get; init; }
    public int GameMaxGrabMass { get; init; }
    public float GameMaxGrabVolume { get; init; }
    public float GameSprintMultiplier { get; init; }
    public float GameStrafeMultiplier { get; init; }
    public float GameWalkBackwardDiagonalMultiplier { get; init; }
    public float GameWalkBackwardMultiplier { get; init; }
    public float GameWalkForwardDiagonalMultiplier { get; init; }
    public float GameWalkMultiplier { get; init; }
    public float GameWalkStrafeMultiplier { get; init; }
    public bool GroundTargetable { get; init; }
    public float HandRate { get; init; }
    public float Height { get; init; }
    public int HitPower { get; init; }
    public float HropeDown { get; init; }
    public string ModelFile { get; init; } = "";
    public float ModelViewOffsetZ { get; init; }
    public uint MovementId { get; init; }
    public int PhysicsFlags { get; init; }
    public float PhysicsLivingAirResistance { get; init; }
    public string PhysicsLivingColliderMat { get; init; } = "";
    public float PhysicsLivingGravity { get; init; }
    public float PhysicsLivingKAirControl { get; init; }
    public int PhysicsLivingMass { get; init; }
    public float PhysicsLivingMaxClimbAngle { get; init; }
    public int PhysicsLivingMaxVelGround { get; init; }
    public float PhysicsLivingMinFallAngle { get; init; }
    public float PhysicsLivingMinSlideAngle { get; init; }
    public float PhysicsLivingTimeImpulseRecover { get; init; }
    public int PhysicsMass { get; init; }
    public int PhysicsStiffnessScale { get; init; }
    public string Portrait { get; init; } = "";
    public bool PushRagdoll { get; init; }
    public float Radius { get; init; }
    public bool RestrictBoardingMate { get; init; }
    public bool RestrictBoardingSlave { get; init; }
    public bool RestrictClimb { get; init; }
    public float RopeBack { get; init; }
    public float RopeHangingHandOffsetX { get; init; }
    public float RopeHangingHandOffsetY { get; init; }
    public float RopeHangingHandOffsetZ { get; init; }
    public float RopeWalkingHandOffsetX { get; init; }
    public float RopeWalkingHandOffsetY { get; init; }
    public float RopeWalkingHandOffsetZ { get; init; }
    public bool SharedDummyModel { get; init; }
    public float SightFov { get; init; }
    public float SightRange { get; init; }
    public bool SlopeAlignment { get; init; }
    public float TurnSpeed { get; init; }
    public bool UnderwaterCreature { get; init; }
    public string UpperbodyGraph { get; init; } = "";
    public bool UseRagdoll { get; init; }
    public bool UseRagdollHit { get; init; }
    public bool UseRagdollKnockDown { get; init; }
    public bool UseRandomIdleControl { get; init; }

    internal const string SelectSql = "SELECT id, actor_height, add_box, animation_graph, attack_start_range, beanstalk_back, box_x, box_y, box_z, center_x, center_y, center_z, enable_side_moving_jump, face_target_instantly, fly_mode, fx_scale, game_backward_diagonal_multiplier, game_backward_multiplier, game_bow_look_ik_blend_head, game_bow_look_ik_blend_neck, game_bow_look_ik_blend_spine1, game_bow_look_ik_blend_spine2, game_bow_look_ik_blend_spine3, game_forward_diagonal_multiplier, game_forward_multiplier, game_grab_multiplier, game_inertia, game_inertia_accel, game_jump_height, game_lean_angle, game_lean_shift, game_look_ik_blend_head, game_look_ik_blend_neck, game_look_ik_blend_spine1, game_look_ik_blend_spine2, game_look_ik_blend_spine3, game_max_grab_mass, game_max_grab_volume, game_sprint_multiplier, game_strafe_multiplier, game_walk_backward_diagonal_multiplier, game_walk_backward_multiplier, game_walk_forward_diagonal_multiplier, game_walk_multiplier, game_walk_strafe_multiplier, ground_targetable, hand_rate, height, hit_power, hrope_down, model_file, model_view_offset_z, movement_id, physics_flags, physics_living_air_resistance, physics_living_collider_mat, physics_living_gravity, physics_living_k_air_control, physics_living_mass, physics_living_max_climb_angle, physics_living_max_vel_ground, physics_living_min_fall_angle, physics_living_min_slide_angle, physics_living_time_impulse_recover, physics_mass, physics_stiffness_scale, portrait, push_ragdoll, radius, restrict_boarding_mate, restrict_boarding_slave, restrict_climb, rope_back, rope_hanging_hand_offset_x, rope_hanging_hand_offset_y, rope_hanging_hand_offset_z, rope_walking_hand_offset_x, rope_walking_hand_offset_y, rope_walking_hand_offset_z, shared_dummy_model, sight_fov, sight_range, slope_alignment, turn_speed, underwater_creature, upperbody_graph, use_ragdoll, use_ragdoll_hit, use_ragdoll_knock_down, use_random_idle_control FROM actor_models";
    internal static ZoneActorModelDefinition Read(SqliteDataReader reader) => new()
    {
        Id = UInt32(reader, 0),
        ActorHeight = Single(reader, 1),
        AddBox = Boolean(reader, 2),
        AnimationGraph = CString(String(reader, 3)),
        AttackStartRange = Single(reader, 4),
        BeanstalkBack = Single(reader, 5),
        BoxX = Single(reader, 6),
        BoxY = Single(reader, 7),
        BoxZ = Single(reader, 8),
        CenterX = Single(reader, 9),
        CenterY = Single(reader, 10),
        CenterZ = Single(reader, 11),
        EnableSideMovingJump = Boolean(reader, 12),
        FaceTargetInstantly = Boolean(reader, 13),
        FlyMode = Boolean(reader, 14),
        FxScale = Single(reader, 15),
        GameBackwardDiagonalMultiplier = Single(reader, 16),
        GameBackwardMultiplier = Single(reader, 17),
        GameBowLookIkBlendHead = Single(reader, 18),
        GameBowLookIkBlendNeck = Single(reader, 19),
        GameBowLookIkBlendSpine1 = Single(reader, 20),
        GameBowLookIkBlendSpine2 = Single(reader, 21),
        GameBowLookIkBlendSpine3 = Single(reader, 22),
        GameForwardDiagonalMultiplier = Single(reader, 23),
        GameForwardMultiplier = Single(reader, 24),
        GameGrabMultiplier = Single(reader, 25),
        GameInertia = Single(reader, 26),
        GameInertiaAccel = Single(reader, 27),
        GameJumpHeight = Single(reader, 28),
        GameLeanAngle = Int32(reader, 29),
        GameLeanShift = Single(reader, 30),
        GameLookIkBlendHead = Single(reader, 31),
        GameLookIkBlendNeck = Single(reader, 32),
        GameLookIkBlendSpine1 = Single(reader, 33),
        GameLookIkBlendSpine2 = Single(reader, 34),
        GameLookIkBlendSpine3 = Single(reader, 35),
        GameMaxGrabMass = Int32(reader, 36),
        GameMaxGrabVolume = Single(reader, 37),
        GameSprintMultiplier = Single(reader, 38),
        GameStrafeMultiplier = Single(reader, 39),
        GameWalkBackwardDiagonalMultiplier = Single(reader, 40),
        GameWalkBackwardMultiplier = Single(reader, 41),
        GameWalkForwardDiagonalMultiplier = Single(reader, 42),
        GameWalkMultiplier = Single(reader, 43),
        GameWalkStrafeMultiplier = Single(reader, 44),
        GroundTargetable = Boolean(reader, 45),
        HandRate = Single(reader, 46),
        Height = Single(reader, 47),
        HitPower = Int32(reader, 48),
        HropeDown = Single(reader, 49),
        ModelFile = CString(String(reader, 50)),
        ModelViewOffsetZ = Single(reader, 51),
        MovementId = UInt32(reader, 52),
        PhysicsFlags = Int32(reader, 53),
        PhysicsLivingAirResistance = Single(reader, 54),
        PhysicsLivingColliderMat = CString(String(reader, 55)),
        PhysicsLivingGravity = Single(reader, 56),
        PhysicsLivingKAirControl = Single(reader, 57),
        PhysicsLivingMass = Int32(reader, 58),
        PhysicsLivingMaxClimbAngle = Single(reader, 59),
        PhysicsLivingMaxVelGround = Int32(reader, 60),
        PhysicsLivingMinFallAngle = Single(reader, 61),
        PhysicsLivingMinSlideAngle = Single(reader, 62),
        PhysicsLivingTimeImpulseRecover = Single(reader, 63),
        PhysicsMass = Int32(reader, 64),
        PhysicsStiffnessScale = Int32(reader, 65),
        Portrait = CString(String(reader, 66)),
        PushRagdoll = Boolean(reader, 67),
        Radius = Single(reader, 68),
        RestrictBoardingMate = Boolean(reader, 69),
        RestrictBoardingSlave = Boolean(reader, 70),
        RestrictClimb = Boolean(reader, 71),
        RopeBack = Single(reader, 72),
        RopeHangingHandOffsetX = Single(reader, 73),
        RopeHangingHandOffsetY = Single(reader, 74),
        RopeHangingHandOffsetZ = Single(reader, 75),
        RopeWalkingHandOffsetX = Single(reader, 76),
        RopeWalkingHandOffsetY = Single(reader, 77),
        RopeWalkingHandOffsetZ = Single(reader, 78),
        SharedDummyModel = Boolean(reader, 79),
        SightFov = Single(reader, 80),
        SightRange = Single(reader, 81),
        SlopeAlignment = Boolean(reader, 82),
        TurnSpeed = Single(reader, 83),
        UnderwaterCreature = Boolean(reader, 84),
        UpperbodyGraph = CString(String(reader, 85)),
        UseRagdoll = Boolean(reader, 86),
        UseRagdollHit = Boolean(reader, 87),
        UseRagdollKnockDown = Boolean(reader, 88),
        UseRandomIdleControl = Boolean(reader, 89),
    };
}

/// <summary>Native game_stances row (39C04220), shared by its owning actor model.</summary>
public sealed record ZoneGameStanceDefinition
{
    public uint Id { get; init; }
    public uint ActorModelId { get; init; }
    public float AiMoveSpeedRun { get; init; }
    public float AiMoveSpeedSlow { get; init; }
    public float AiMoveSpeedSprint { get; init; }
    public float AiMoveSpeedWalk { get; init; }
    public float HeightCollider { get; init; }
    public float HeightPivot { get; init; }
    public bool IgnoreCollision { get; init; }
    public float MaxSpeed { get; init; }
    public float ModelOffsetX { get; init; }
    public float ModelOffsetY { get; init; }
    public float ModelOffsetZ { get; init; }
    public string Name { get; init; } = "";
    public float NormalSpeed { get; init; }
    public float SizeX { get; init; }
    public float SizeY { get; init; }
    public float SizeZ { get; init; }
    public int StanceId { get; init; }
    public bool UseCapsule { get; init; }
    public float ViewOffsetX { get; init; }
    public float ViewOffsetY { get; init; }
    public float ViewOffsetZ { get; init; }

    internal const string SelectSql = "SELECT id, actor_model_id, ai_move_speed_run, ai_move_speed_slow, ai_move_speed_sprint, ai_move_speed_walk, height_collider, height_pivot, ignore_collision, max_speed, model_offset_x, model_offset_y, model_offset_z, name, normal_speed, size_x, size_y, size_z, stance_id, use_capsule, view_offset_x, view_offset_y, view_offset_z FROM game_stances";
    internal static ZoneGameStanceDefinition Read(SqliteDataReader reader) => new()
    {
        Id = UInt32(reader, 0),
        ActorModelId = UInt32(reader, 1),
        AiMoveSpeedRun = Single(reader, 2),
        AiMoveSpeedSlow = Single(reader, 3),
        AiMoveSpeedSprint = Single(reader, 4),
        AiMoveSpeedWalk = Single(reader, 5),
        HeightCollider = Single(reader, 6),
        HeightPivot = Single(reader, 7),
        IgnoreCollision = Boolean(reader, 8),
        MaxSpeed = Single(reader, 9),
        ModelOffsetX = Single(reader, 10),
        ModelOffsetY = Single(reader, 11),
        ModelOffsetZ = Single(reader, 12),
        Name = CString(String(reader, 13)),
        NormalSpeed = Single(reader, 14),
        SizeX = Single(reader, 15),
        SizeY = Single(reader, 16),
        SizeZ = Single(reader, 17),
        StanceId = Int32(reader, 18),
        UseCapsule = Boolean(reader, 19),
        ViewOffsetX = Single(reader, 20),
        ViewOffsetY = Single(reader, 21),
        ViewOffsetZ = Single(reader, 22),
    };
}

public sealed class ZoneActorModel
{
    public uint Id => Definition.Id;
    public ZoneActorModelDefinition Definition { get; }
    public IReadOnlyList<ZoneGameStanceDefinition> Stances { get; }

    internal ZoneActorModel(ZoneActorModelDefinition definition, IEnumerable<ZoneGameStanceDefinition> stances)
    { Definition = definition; Stances = Array.AsReadOnly(stances.ToArray()); }
}

/// <summary>Actor scalars and stance rows preserve physical source order and last-ID lookup identity.</summary>
public sealed class ZoneActorModelContent
{
    public static ZoneActorModelContent Empty { get; } = new([], []);
    public IReadOnlyList<ZoneActorModel> Rows { get; }
    public IReadOnlyDictionary<uint, ZoneActorModel> Models { get; }
    public IReadOnlyList<ZoneGameStanceDefinition> StanceRows { get; }
    public IReadOnlyDictionary<uint, ZoneGameStanceDefinition> Stances { get; }

    public ZoneActorModelContent(IEnumerable<ZoneActorModelDefinition> models, IEnumerable<ZoneGameStanceDefinition> stances)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(stances);
        var definitions = models.ToArray();
        var stanceRows = stances.ToArray();
        var lastModel = new Dictionary<uint, int>();
        var lastStance = new Dictionary<uint, ZoneGameStanceDefinition>();
        for (var i = 0; i < definitions.Length; i++)
        {
            ArgumentNullException.ThrowIfNull(definitions[i]);
            lastModel[definitions[i].Id] = i;
        }
        foreach (var stance in stanceRows)
        {
            ArgumentNullException.ThrowIfNull(stance);
            lastStance[stance.Id] = stance;
        }
        // 39AD8AD0 links every physical stance, including repeated IDs, to the final actor ID.
        // Unresolved actor IDs remain available as source rows but have no owning actor model.
        var byActor = stanceRows.ToLookup(row => row.ActorModelId);
        var rows = definitions.Select((definition, i) => new ZoneActorModel(definition,
            lastModel[definition.Id] == i ? byActor[definition.Id] : [])).ToArray();
        Rows = Array.AsReadOnly(rows);
        Models = lastModel.ToFrozenDictionary(pair => pair.Key, pair => rows[pair.Value]);
        StanceRows = Array.AsReadOnly(stanceRows);
        Stances = lastStance.ToFrozenDictionary();
    }

    internal static ZoneActorModelContent Load(SqliteConnection connection)
    {
        var stances = new List<ZoneGameStanceDefinition>();
        Read(connection, ZoneGameStanceDefinition.SelectSql, reader => stances.Add(ZoneGameStanceDefinition.Read(reader)));
        var actors = new List<ZoneActorModelDefinition>();
        Read(connection, ZoneActorModelDefinition.SelectSql, reader => actors.Add(ZoneActorModelDefinition.Read(reader)));
        return new(actors, stances);
    }

    internal static string CString(string value)
    {
        var terminator = value.IndexOf('\0');
        return terminator < 0 ? value : value[..terminator];
    }
}

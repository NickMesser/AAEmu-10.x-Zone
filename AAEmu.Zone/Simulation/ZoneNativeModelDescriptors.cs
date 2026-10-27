using System.Runtime.InteropServices;
using System.Text;
using AAEmu.Zone.Content;

namespace AAEmu.Zone.Simulation;

/// <summary>
/// Retained content records, not CActor/model instances or a replacement IGame object. Native
/// consumers only borrow these pointers. Published descriptors remain owned through framework and
/// CSystem shutdown, unless a complete unpublication/drain is separately proved. Native code must
/// never mutate or destroy these owned records. A direct leaf borrower must return before Dispose.
/// </summary>
internal sealed unsafe partial class ZoneNativeModelDescriptors : IDisposable
{
    private const int ModelSize = 0x98;
    private const int ActorSize = 0x168;
    private const int StanceSize = 0x60;
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private readonly ZoneModelContent _content;
    private readonly List<nint> _allocations = [];
    private readonly Dictionary<string, nint> _strings = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, nint> _models = [];
    private readonly Dictionary<uint, nint> _actors = [];
    private readonly Dictionary<uint, nint> _stances = [];
    private readonly Dictionary<uint, string> _unsupportedModels = [];
    private bool _auxiliaryContentLoaded;
    private bool _disposed;

    // Private construction cannot publish scalar-only model records. The companion auxiliary
    // constructor must populate the actual +50/+70 vectors before returning this owner.
    private ZoneNativeModelDescriptors(ZoneModelContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        _content = content;
        try
        {
            BuildActorsAndStances(content.ActorModels);
            BuildModelScalars(content);
        }
        catch { Dispose(); throw; }
    }

    internal nint LookupModel(uint modelId)
    {
        VerifyAccess();
        if (!_auxiliaryContentLoaded)
            throw new InvalidOperationException("Load complete model binding and slave-link content before borrowing model descriptors.");
        if (_unsupportedModels.TryGetValue(modelId, out var reason))
            throw new NotSupportedException(reason);
        return _models.GetValueOrDefault(modelId);
    }

    // Called only by the exact auxiliary-content constructor after both native tail vectors
    // have been populated for every model. The scalar-only construction is never publishable.
    private void CompleteDescriptorConstruction()
    {
        VerifyAccess();
        if (_auxiliaryContentLoaded) throw new InvalidOperationException("Model auxiliary content is single-use.");
        _auxiliaryContentLoaded = true;
    }

    private void BuildActorsAndStances(ZoneActorModelContent content)
    {
        var stanceStorage = AllocateRows(content.StanceRows.Count, StanceSize);
        for (var index = 0; index < content.StanceRows.Count; index++)
        {
            var row = content.StanceRows[index];
            var pointer = stanceStorage + checked(index * StanceSize);
            WriteStanceScalars(pointer, row);
            _stances[row.Id] = pointer; // Physical duplicate rows remain; lookup uses the last ID.
        }
        var actorStorage = AllocateRows(content.Rows.Count, ActorSize);
        for (var index = 0; index < content.Rows.Count; index++)
        {
            var row = content.Rows[index];
            var pointer = actorStorage + checked(index * ActorSize);
            WriteActorScalars(pointer, row.Definition);
            _actors[row.Id] = pointer;
        }
        //39AD8AD0 links every physical stance to the final indexed actor. Never use a stance-ID
        // dictionary for this vector: repeated IDs still appear at their actual source positions.
        var links = new Dictionary<uint, List<nint>>();
        for (var index = 0; index < content.StanceRows.Count; index++)
        {
            var row = content.StanceRows[index];
            if (!_actors.ContainsKey(row.ActorModelId)) continue;
            if (!links.TryGetValue(row.ActorModelId, out var pointers))
                links.Add(row.ActorModelId, pointers = []);
            pointers.Add(stanceStorage + checked(index * StanceSize));
        }
        foreach (var pair in links) WritePointerVector(_actors[pair.Key], 0x140, pair.Value);
    }

    private void BuildModelScalars(ZoneModelContent content)
    {
        foreach (var row in content.Models.Values)
        {
            var pointer = Allocate(ModelSize);
            _models.Add(row.Id, pointer);
            nint subtype = 0;
            if (row.Kind == ZoneModelKind.Actor && _actors.TryGetValue(row.SubtypeId, out var actor))
                subtype = actor;
            else if (content.SubtypeForModel(row.Id) is not null)
                _unsupportedModels.Add(row.Id,
                    $"Model {row.Id} resolves {row.Kind} subtype {row.SubtypeId}, whose complete native descriptor is not loaded.");
            // A genuinely unresolved subtype remains null, as39ADC040's actual indexed lookup.
            // A resolved unsupported subtype never escapes through LookupModel as a fake null.
            WriteModelScalars(pointer, row, subtype);
        }
    }

    private void WriteModelScalars(nint pointer, ZoneModelDefinition row, nint subtype)
    {
        WriteUInt32(pointer, 0x00, row.Id);
        WriteSingle(pointer, 0x04, row.CameraDistance);
        WriteSingle(pointer, 0x08, row.CameraDistanceForActionMode);
        WriteSingle(pointer, 0x0c, row.CameraDistanceForWideMode);
        WriteSingle(pointer, 0x10, row.DyingTime);
        WriteUInt32(pointer, 0x14, row.HighImpactFxGroupId);
        WriteUInt32(pointer, 0x18, row.LowImpactFxGroupId);
        WriteUInt32(pointer, 0x1c, row.MiddleImpactFxGroupId);
        WriteUInt32(pointer, 0x20, row.MountPoseId);
        WritePointer(pointer, 0x28, StoreString(row.Name));
        WriteSingle(pointer, 0x30, row.NameTagOffset);
        WriteUInt32(pointer, 0x34, row.SoundMaterialId);
        WriteUInt32(pointer, 0x38, row.SoundPackId);
        WriteUInt32(pointer, 0x3c, (uint)row.Kind);
        WritePointer(pointer, 0x40, subtype);
        WriteSingle(pointer, 0x48, row.TargetDecalSize);
        WriteFlag(pointer, 0x90, 0x01, row.AutoAdjustBindOffset);
        WriteFlag(pointer, 0x90, 0x02, row.Big);
        WriteFlag(pointer, 0x90, 0x04, row.DespawnDoodadOnCollision);
        WriteFlag(pointer, 0x90, 0x08, row.ForceNavigateAroundObject);
        WriteFlag(pointer, 0x90, 0x10, row.PlayMountAnimation);
        WriteFlag(pointer, 0x90, 0x20, row.PlayerMountNameTagPos);
        WriteFlag(pointer, 0x90, 0x40, row.Selectable);
        WriteFlag(pointer, 0x90, 0x80, row.ShowNameTag);
        WriteFlag(pointer, 0x91, 0x01, row.UseTargetDecal);
        WriteFlag(pointer, 0x91, 0x02, row.UseTargetHighlight);
        WriteFlag(pointer, 0x91, 0x04, row.UseTargetSilhouette);
    }

    private void WriteActorScalars(nint pointer, ZoneActorModelDefinition row)
    {
        WriteSingle(pointer, 0x0, row.ActorHeight);
        WriteFlag(pointer, 0x160, 0x01, row.AddBox);
        WritePointer(pointer, 0x8, StoreString(row.AnimationGraph));
        WriteSingle(pointer, 0x10, row.AttackStartRange);
        WriteSingle(pointer, 0x14, row.BeanstalkBack);
        WriteSingle(pointer, 0x18, row.BoxX);
        WriteSingle(pointer, 0x1c, row.BoxY);
        WriteSingle(pointer, 0x20, row.BoxZ);
        WriteSingle(pointer, 0x24, row.CenterX);
        WriteSingle(pointer, 0x28, row.CenterY);
        WriteSingle(pointer, 0x2c, row.CenterZ);
        WriteFlag(pointer, 0x160, 0x02, row.EnableSideMovingJump);
        WriteFlag(pointer, 0x160, 0x04, row.FaceTargetInstantly);
        WriteFlag(pointer, 0x160, 0x08, row.FlyMode);
        WriteSingle(pointer, 0x30, row.FxScale);
        WriteSingle(pointer, 0x34, row.GameBackwardDiagonalMultiplier);
        WriteSingle(pointer, 0x38, row.GameBackwardMultiplier);
        WriteSingle(pointer, 0x3c, row.GameBowLookIkBlendHead);
        WriteSingle(pointer, 0x40, row.GameBowLookIkBlendNeck);
        WriteSingle(pointer, 0x44, row.GameBowLookIkBlendSpine1);
        WriteSingle(pointer, 0x48, row.GameBowLookIkBlendSpine2);
        WriteSingle(pointer, 0x4c, row.GameBowLookIkBlendSpine3);
        WriteSingle(pointer, 0x50, row.GameForwardDiagonalMultiplier);
        WriteSingle(pointer, 0x54, row.GameForwardMultiplier);
        WriteSingle(pointer, 0x58, row.GameGrabMultiplier);
        WriteSingle(pointer, 0x5c, row.GameInertia);
        WriteSingle(pointer, 0x60, row.GameInertiaAccel);
        WriteSingle(pointer, 0x64, row.GameJumpHeight);
        WriteInt32(pointer, 0x68, row.GameLeanAngle);
        WriteSingle(pointer, 0x6c, row.GameLeanShift);
        WriteSingle(pointer, 0x70, row.GameLookIkBlendHead);
        WriteSingle(pointer, 0x74, row.GameLookIkBlendNeck);
        WriteSingle(pointer, 0x78, row.GameLookIkBlendSpine1);
        WriteSingle(pointer, 0x7c, row.GameLookIkBlendSpine2);
        WriteSingle(pointer, 0x80, row.GameLookIkBlendSpine3);
        WriteInt32(pointer, 0x84, row.GameMaxGrabMass);
        WriteSingle(pointer, 0x88, row.GameMaxGrabVolume);
        WriteSingle(pointer, 0x8c, row.GameSprintMultiplier);
        WriteSingle(pointer, 0x90, row.GameStrafeMultiplier);
        WriteSingle(pointer, 0x94, row.GameWalkBackwardDiagonalMultiplier);
        WriteSingle(pointer, 0x98, row.GameWalkBackwardMultiplier);
        WriteSingle(pointer, 0x9c, row.GameWalkForwardDiagonalMultiplier);
        WriteSingle(pointer, 0xa0, row.GameWalkMultiplier);
        WriteSingle(pointer, 0xa4, row.GameWalkStrafeMultiplier);
        WriteFlag(pointer, 0x160, 0x10, row.GroundTargetable);
        WriteSingle(pointer, 0xa8, row.HandRate);
        WriteSingle(pointer, 0xac, row.Height);
        WriteInt32(pointer, 0xb0, row.HitPower);
        WriteSingle(pointer, 0xb4, row.HropeDown);
        WritePointer(pointer, 0xb8, StoreString(row.ModelFile));
        WriteSingle(pointer, 0xc0, row.ModelViewOffsetZ);
        WriteUInt32(pointer, 0xc4, row.MovementId);
        WriteInt32(pointer, 0xc8, row.PhysicsFlags);
        WriteSingle(pointer, 0xcc, row.PhysicsLivingAirResistance);
        WritePointer(pointer, 0xd0, StoreString(row.PhysicsLivingColliderMat));
        WriteSingle(pointer, 0xd8, row.PhysicsLivingGravity);
        WriteSingle(pointer, 0xdc, row.PhysicsLivingKAirControl);
        WriteInt32(pointer, 0xe0, row.PhysicsLivingMass);
        WriteSingle(pointer, 0xe4, row.PhysicsLivingMaxClimbAngle);
        WriteInt32(pointer, 0xe8, row.PhysicsLivingMaxVelGround);
        WriteSingle(pointer, 0xec, row.PhysicsLivingMinFallAngle);
        WriteSingle(pointer, 0xf0, row.PhysicsLivingMinSlideAngle);
        WriteSingle(pointer, 0xf4, row.PhysicsLivingTimeImpulseRecover);
        WriteInt32(pointer, 0xf8, row.PhysicsMass);
        WriteInt32(pointer, 0xfc, row.PhysicsStiffnessScale);
        WritePointer(pointer, 0x100, StoreString(row.Portrait));
        WriteFlag(pointer, 0x160, 0x20, row.PushRagdoll);
        WriteSingle(pointer, 0x108, row.Radius);
        WriteFlag(pointer, 0x160, 0x40, row.RestrictBoardingMate);
        WriteFlag(pointer, 0x160, 0x80, row.RestrictBoardingSlave);
        WriteFlag(pointer, 0x161, 0x01, row.RestrictClimb);
        WriteSingle(pointer, 0x10c, row.RopeBack);
        WriteSingle(pointer, 0x110, row.RopeHangingHandOffsetX);
        WriteSingle(pointer, 0x114, row.RopeHangingHandOffsetY);
        WriteSingle(pointer, 0x118, row.RopeHangingHandOffsetZ);
        WriteSingle(pointer, 0x11c, row.RopeWalkingHandOffsetX);
        WriteSingle(pointer, 0x120, row.RopeWalkingHandOffsetY);
        WriteSingle(pointer, 0x124, row.RopeWalkingHandOffsetZ);
        WriteFlag(pointer, 0x161, 0x02, row.SharedDummyModel);
        WriteSingle(pointer, 0x128, row.SightFov);
        WriteSingle(pointer, 0x12c, row.SightRange);
        WriteFlag(pointer, 0x161, 0x04, row.SlopeAlignment);
        WriteSingle(pointer, 0x130, row.TurnSpeed);
        WriteFlag(pointer, 0x161, 0x08, row.UnderwaterCreature);
        WritePointer(pointer, 0x138, StoreString(row.UpperbodyGraph));
        WriteFlag(pointer, 0x161, 0x10, row.UseRagdoll);
        WriteFlag(pointer, 0x161, 0x20, row.UseRagdollHit);
        WriteFlag(pointer, 0x161, 0x40, row.UseRagdollKnockDown);
        WriteFlag(pointer, 0x161, 0x80, row.UseRandomIdleControl);
    }

    private void WriteStanceScalars(nint pointer, ZoneGameStanceDefinition row)
    {
        WriteUInt32(pointer, 0x0, row.ActorModelId);
        WriteSingle(pointer, 0x4, row.AiMoveSpeedRun);
        WriteSingle(pointer, 0x8, row.AiMoveSpeedSlow);
        WriteSingle(pointer, 0xc, row.AiMoveSpeedSprint);
        WriteSingle(pointer, 0x10, row.AiMoveSpeedWalk);
        WriteSingle(pointer, 0x14, row.HeightCollider);
        WriteSingle(pointer, 0x18, row.HeightPivot);
        WriteFlag(pointer, 0x58, 0x01, row.IgnoreCollision);
        WriteSingle(pointer, 0x1c, row.MaxSpeed);
        WriteSingle(pointer, 0x20, row.ModelOffsetX);
        WriteSingle(pointer, 0x24, row.ModelOffsetY);
        WriteSingle(pointer, 0x28, row.ModelOffsetZ);
        WritePointer(pointer, 0x30, StoreString(row.Name));
        WriteSingle(pointer, 0x38, row.NormalSpeed);
        WriteSingle(pointer, 0x3c, row.SizeX);
        WriteSingle(pointer, 0x40, row.SizeY);
        WriteSingle(pointer, 0x44, row.SizeZ);
        WriteInt32(pointer, 0x48, row.StanceId);
        WriteFlag(pointer, 0x58, 0x02, row.UseCapsule);
        WriteSingle(pointer, 0x4c, row.ViewOffsetX);
        WriteSingle(pointer, 0x50, row.ViewOffsetY);
        WriteSingle(pointer, 0x54, row.ViewOffsetZ);
    }

    private nint StoreString(string? value)
    {
        //39D3A0E0: native NULL becomes an empty C string; strlen uses the first NUL. A string
        // larger than the native 1 MiB page (including terminator) produces a null field pointer.
        value ??= "";
        var terminator = value.IndexOf('\0');
        if (terminator >= 0) value = value[..terminator];
        if (_strings.TryGetValue(value, out var existing)) return existing;
        var length = Encoding.UTF8.GetByteCount(value);
        if (length >= 0x100000) return 0;
        var pointer = Allocate(checked(length + 1));
        Encoding.UTF8.GetBytes(value, new Span<byte>((void*)pointer, length));
        _strings.Add(value, pointer);
        return pointer;
    }

    private void WritePointerVector(nint record, int offset, IReadOnlyList<nint> pointers)
    {
        if (pointers.Count == 0) return; // Native empty vectors have null begin/end/capacity.
        var storage = AllocateRows(pointers.Count, 8);
        for (var index = 0; index < pointers.Count; index++) WritePointer(storage, index * 8, pointers[index]);
        var end = storage + checked(pointers.Count * 8);
        WritePointer(record, offset, storage);
        WritePointer(record, offset + 8, end);
        WritePointer(record, offset + 16, end); // Capacity covers the exact retained allocation.
    }

    private nint AllocateRows(int count, int stride) => count == 0 ? 0 : Allocate(checked(count * stride));
    private nint Allocate(int size)
    {
        var pointer = Marshal.AllocHGlobal(size);
        try { _allocations.Add(pointer); }
        catch { Marshal.FreeHGlobal(pointer); throw; }
        // Zero native empty-vector headers and unspecified padding; semantic scalar/string fields
        // are written from content before the complete record owner may be published.
        new Span<byte>((void*)pointer, size).Clear();
        return pointer;
    }
    private static void WritePointer(nint record, int offset, nint value) => Marshal.WriteIntPtr(record + offset, value);
    private static void WriteUInt32(nint record, int offset, uint value) => Marshal.WriteInt32(record + offset, unchecked((int)value));
    private static void WriteInt32(nint record, int offset, int value) => Marshal.WriteInt32(record + offset, value);
    private static void WriteSingle(nint record, int offset, float value) => Marshal.WriteInt32(record + offset, BitConverter.SingleToInt32Bits(value));
    private static void WriteFlag(nint record, int offset, byte mask, bool value)
    {
        var current = Marshal.ReadByte(record + offset);
        Marshal.WriteByte(record + offset, value ? (byte)(current | mask) : (byte)(current & ~mask));
    }
    private void VerifyAccess()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new InvalidOperationException("Native content descriptors belong to their captured engine owner thread.");
    }
    public void Dispose()
    {
        if (_disposed) return;
        VerifyAccess();
        // Published storage survives framework/CSystem shutdown; direct leaf borrowing must have ended.
        // There is deliberately no finalizer which could free still-borrowed native pointers.
        for (var index = _allocations.Count - 1; index >= 0; index--) Marshal.FreeHGlobal(_allocations[index]);
        _allocations.Clear(); _models.Clear(); _actors.Clear(); _stances.Clear();
        _strings.Clear(); _unsupportedModels.Clear();
        _disposed = true;
    }
}

using AAEmu.Zone.Actors;

namespace AAEmu.Zone.Scene;

public enum ZoneAnimatedColliderMode
{
    Undefined = 0, Disabled = 1, GroundedOnly = 2, GroundedOnlyNonPushable = 3,
    Pushable = 4, NonPushable = 5, PushesPlayersOnly = 6, Spectator = 7, DontPushable = 8,
}

/// <summary>Native parameter types are retained without assigning unproved names to their fields.</summary>
public sealed record ZoneAnimatedColliderPhysicsUpdate(int Mode, int StateFlags, bool ResetAction1,
    bool RequiresNonPushableAuxiliary, ZoneActorPhysicsParameterFields Living,
    ZoneActorPhysicsParameterFields Part, ZoneActorPhysicsParameterFields EntityFlags,
    ZoneActorPhysicsParameterFields Type26);

/// <summary>
/// One actual entity's physical owner. Apply must consume the ordered native action/auxiliary/
/// parameter effects or refuse; merely retaining this payload does not implement the body effects.
/// </summary>
public interface IZoneAnimatedColliderBodyEffects
{
    /// <summary>0 means an owned absent lookup, 4 a living body; other native types skip this update.</summary>
    int PhysicalEntityType { get; }
    void Apply(ZoneAnimatedColliderPhysicsUpdate update);
}

public enum ZoneAnimatedColliderUpdateOutcome { UnchangedRequest, UnchangedState, FrameGated, NoBody, NonLivingBody, Applied, UnknownMode }

public sealed record ZoneAnimatedColliderState(IReadOnlyList<int> Requests, IReadOnlyList<string?> Reasons,
    int SelectedMode, int RequestedFlags, int AppliedFlags, bool ForceRebuild,
    int Frame, int PhysicalizedFrame, int ResetFrame);

/// <summary>
/// Original CAnimatedCharacter request layers3065FB80 and arbitration30669A20. Selection/state
/// commit precedes the physical lookup; frame suppression and absent bodies do not erase requests.
/// The scene owns the frame/reset/physicalization event producers and the actual body consumer.
/// </summary>
public sealed class ZoneAnimatedColliderController : IDisposable
{
    private readonly Func<bool> _isCurrent;
    private readonly IZoneAnimatedColliderBodyEffects _body;
    private readonly float _inertia, _inertiaAcceleration;
    private readonly Func<bool> _spectatorCollisions;
    private readonly int[] _requests = new int[7];
    private readonly string?[] _reasons = new string?[7];
    private int _selected, _flags, _appliedFlags, _frame = -1, _physicalizedFrame, _resetFrame;
    private bool _force, _updating;
    private string? _effectRefusal;
    public bool IsDisposed { get; private set; }
    public string? EffectRefusal => _effectRefusal;

    public ZoneAnimatedColliderController(Func<bool> isCurrent, IZoneAnimatedColliderBodyEffects body,
        float inertia, float inertiaAcceleration, Func<bool> spectatorCollisions, int initialResetFrame = 0)
    {
        _isCurrent = isCurrent ?? throw new ArgumentNullException(nameof(isCurrent));
        _body = body ?? throw new ArgumentNullException(nameof(body));
        _spectatorCollisions = spectatorCollisions ?? throw new ArgumentNullException(nameof(spectatorCollisions));
        if (!float.IsFinite(inertia) || !float.IsFinite(inertiaAcceleration))
            throw new ArgumentException("Animated collider dynamics must be actual finite actor values.");
        _inertia = inertia; _inertiaAcceleration = inertiaAcceleration;
        //30660860 samples CSystem's current frame into+468 during construction/reset.
        //The historical PhysicalizedFrame name is retained for existing snapshot consumers.
        _physicalizedFrame = initialResetFrame;
    }

    public ZoneAnimatedColliderState CopyState() => new(Array.AsReadOnly((int[])_requests.Clone()),
        Array.AsReadOnly((string?[])_reasons.Clone()), _selected, _flags, _appliedFlags, _force,
        _frame, _physicalizedFrame, _resetFrame);

    public void SetFrame(int frame) { Require(); _frame = frame; }
    public void Physicalized(int frame) { Require(); _physicalizedFrame = frame; _force = true; }
    public void ResetFrame(int frame) { Require(); _resetFrame = frame; }
    public void ForceRebuild() { Require(); _force = true; }

    public ZoneAnimatedColliderUpdateOutcome Request(int mode, int layer, string? reason = null)
    {
        Require();
        if ((uint)layer >= 7) throw new ArgumentOutOfRangeException(nameof(layer));
        if (_requests[layer] == mode && !_force) return ZoneAnimatedColliderUpdateOutcome.UnchangedRequest;
        _requests[layer] = mode; _reasons[layer] = reason;
        return Update();
    }

    public ZoneAnimatedColliderUpdateOutcome SetFlags(int flags)
    { Require(); _flags = flags; return flags == _appliedFlags ? ZoneAnimatedColliderUpdateOutcome.UnchangedState : Update(); }

    public ZoneAnimatedColliderUpdateOutcome Update()
    {
        Require();
        if (_updating) throw new InvalidOperationException("Animated collider updates cannot run recursively.");
        _updating = true;
        try
        {
            var selected = 0; var disabled = false;
            foreach (var request in _requests)
                if (request != 0) { selected = request; disabled |= request == 1; }
            // Layer6 is also the original +1952 override slot.
            if (disabled && _requests[6] == 0) selected = 1;
            else if (selected == 0) selected = 4;
            if (selected == _selected && _flags == _appliedFlags && unchecked(_resetFrame + 3) <= _frame && !_force)
                return ZoneAnimatedColliderUpdateOutcome.UnchangedState;
            if (!_force && selected is not (1 or 6 or 7) && (_frame <= 0 || unchecked(_physicalizedFrame + 1) > _frame))
                return ZoneAnimatedColliderUpdateOutcome.FrameGated;
            _force = false; _appliedFlags = _flags; _selected = selected;
            var bodyType = _body.PhysicalEntityType;
            Require();
            if (bodyType == 0) return ZoneAnimatedColliderUpdateOutcome.NoBody;
            if (bodyType != 4) return ZoneAnimatedColliderUpdateOutcome.NonLivingBody;
            if (selected is < 1 or > 9) return ZoneAnimatedColliderUpdateOutcome.UnknownMode;
            var spectator = selected == 7 && _spectatorCollisions();
            Require();
            _body.Apply(ZoneAnimatedColliderParameters.Create(selected, _appliedFlags, _inertia, _inertiaAcceleration, spectator));
            Require();
            return ZoneAnimatedColliderUpdateOutcome.Applied;
        }
        catch (NotSupportedException error)
        {
            //Native selection commits before body effects. Preserve that evidence, but seal
            //this unadmitted component: a repeated identical request must not turn its earlier
            //missing body consumer into an apparently successful UnchangedRequest.
            _effectRefusal = error.Message;
            throw;
        }
        finally { _updating = false; }
    }

    private void Require()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (_effectRefusal is { } refusal)
            throw new NotSupportedException($"Animated collider body effects were refused; this component requires a new admitted owner: {refusal}");
        if (!_isCurrent()) throw new ObjectDisposedException(nameof(ZoneAnimatedColliderController), "The exact entity generation retired.");
        ObjectDisposedException.ThrowIf(IsDisposed, this);
    }
    public void Dispose() => IsDisposed = true;
}

/// <summary>Defined bytes from30669A20, including sentinels. Padding/unwritten vector components stay undefined.</summary>
public static class ZoneAnimatedColliderParameters
{
    public static ZoneAnimatedColliderPhysicsUpdate Create(int mode, int flags, float inertia, float inertiaAcceleration,
        bool spectatorCollisions)
    {
        if (mode is < 1 or > 9) throw new ArgumentOutOfRangeException(nameof(mode));
        var living = new ZoneActorPhysicsParameterFields(0x64); living.Word(0, 4);
        living.Single(4, inertia); living.Single(8, inertiaAcceleration);
        foreach (var offset in new[] { 0x0C, 0x10, 0x14, 0x1C, 0x20, 0x28, 0x30, 0x34, 0x38, 0x3C, 0x40, 0x44 })
            living.Word(offset, ZoneActorPhysicalizationRequest.UnusedFloatBits);
        foreach (var offset in new[] { 0x24, 0x2C, 0x48, 0x58, 0x5C, 0x60 })
            living.Word(offset, ZoneActorPhysicalizationRequest.UnusedIntegerBits);
        living.Word(0x50, uint.MaxValue); living.Word(0x54, uint.MaxValue);

        var part = new ZoneActorPhysicsParameterFields(0xA0); part.Word(0, 6);
        part.Word(4, 0x80000000); part.Word(8, 0); part.Word(12, 1);
        foreach (var offset in new[] { 0x10, 0x28, 0x2C, 0x54, 0x58, 0x5C, 0x94 })
            part.Word(offset, ZoneActorPhysicalizationRequest.UnusedFloatBits);
        foreach (var offset in new[] { 0x40, 0x70, 0x80, 0x9C }) part.Word(offset, 0x80000000);
        foreach (var offset in new[] { 0x30, 0x38 }) part.NullPointer(offset);
        foreach (var offset in new[] { 0x60, 0x68, 0x78, 0x88 }) { part.Word(offset, uint.MaxValue); part.Word(offset + 4, uint.MaxValue); }
        part.Word(0x44, 0); part.Word(0x48, uint.MaxValue); part.Word(0x4C, 0); part.Word(0x50, uint.MaxValue);
        part.Word(0x90, 0); part.Word(0x98, 0);
        var entity = new ZoneActorPhysicsParameterFields(16); entity.Word(0, 15);
        entity.Word(4, 0x80000000); entity.Word(8, 0x80000000); entity.Word(12, 0x80000000);
        uint collisionTypes;
        var active = true;
        switch (mode)
        {
            case 1:
                collisionTypes = 271; active = false; part.Word(0x48, unchecked((uint)-3));
                entity.Word(12, unchecked((uint)-8388617)); break;
            case 2:
                collisionTypes = 263; part.Word(0x48, unchecked((uint)-3)); part.Word(0x4C, 2);
                entity.Word(12, unchecked((uint)-8388617)); break;
            case 3:
                collisionTypes = 263; part.Word(0x48, unchecked((uint)-3));
                entity.Word(8, 8); entity.Word(12, unchecked((uint)-8389121)); break;
            case 4:
                collisionTypes = 271; part.Word(0x44, 2); part.Word(0x4C, 2);
                entity.Word(8, 514); entity.Word(12, unchecked((uint)-8388617)); break;
            case 5:
                collisionTypes = 271; part.Word(0x44, 2); part.Word(0x4C, 2);
                entity.Word(12, unchecked((uint)-8389121)); break;
            case 6:
                collisionTypes = 8; part.Word(0x44, 2); part.Word(0x4C, 2);
                entity.Word(8, 8388616); entity.Word(12, unchecked((uint)-513));
                living.Single(4, 0); living.Single(8, 0); ZeroGravity(); break;
            case 7:
                collisionTypes = spectatorCollisions ? 257u : 256u;
                part.Word(0x48, unchecked((uint)-3)); part.Word(0x4C, 2);
                entity.Word(8, 0x800000); entity.Word(12, unchecked((uint)-9)); ZeroGravity(); break;
            case 8:
                collisionTypes = 256; part.Word(0x48, 0); part.Word(0x4C, 2);
                entity.Word(8, 8); entity.Word(12, unchecked((uint)-8388611));
                break;
            default: // mode9 has no public name in the original mode-name table.
                collisionTypes = 265; part.Word(0x44, 2); part.Word(0x4C, 2);
                entity.Word(12, unchecked((uint)-8389121)); break;
        }
        if ((flags & 2) != 0) collisionTypes &= ~6u;
        living.Word(0x48, collisionTypes);
        living.Word(0x5C, active ? 1u : 0u);
        var type26 = new ZoneActorPhysicsParameterFields(8); type26.Word(0, 26); type26.Byte(4, (flags & 4) != 0 ? (byte)1 : (byte)0);
        return new(mode, flags, mode is 1 or 7, mode == 5, living, part, entity, type26);
        void ZeroGravity() { living.Single(0x14, 0); living.Single(0x18, 0); living.Single(0x1C, 0); }
    }
}

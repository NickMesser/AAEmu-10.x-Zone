using System.Numerics;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Ai;

namespace AAEmu.Zone.Scene;

/// <summary>Copied devaluation state; perception/target selection must explicitly consume this state when attached.</summary>
public sealed record ZoneManagedNpcDevaluation(object Target, bool Flag, long ExpiresMicroseconds);

internal sealed class ZoneManagedNpcActorServices : IZoneActorGoalPhaseServices, IZoneActorControlGoalServices,
    IZoneActorNavigationScene, IZoneLocateGoalServices, IZoneDevalueGoalServices, IZoneSignalGoalServices
{
    private readonly ZoneManagedNpcActorScene _scene;
    private readonly ZoneManagedNpcActor _actor;
    private readonly ZoneActorTargetState _targets;
    private readonly Dictionary<object, ZoneManagedNpcDevaluation> _devalued = new(ReferenceEqualityComparer.Instance);
    private object? _attention;
    public ZoneActorControlState Controls { get; }
    public ZoneActorNavigationState Navigation { get; }
    public ZoneActorContinuationState Continuation { get; } = new();
    public bool IsPuppet => _actor.IsPuppet;
    public object? Argument => _targets.Argument;
    public object Actor => _actor.AiObject;
    public object? SelectionTarget { get; private set; }
    public IReadOnlyList<ZoneManagedNpcDevaluation> Devaluations => _devalued.Values.ToArray();
    // CPipeUser's persistent COPStick at+FA0 is not replaced by an invented operation.
    public IZoneGoalOperation? AuxiliaryGoal => null;

    internal ZoneManagedNpcActorServices(ZoneManagedNpcActorScene scene, ZoneManagedNpcActor actor, ZoneActorControlState controls)
    {
        _scene = scene; _actor = actor; Controls = controls;
        Navigation = new(this);
        _targets = new(actor.AiObject, scene.Objects, (_, _) => throw ZoneManagedNpcActorScene.Missing("named/special target resolution"));
    }

    public IZoneGoalOperation CreateOperation(ZoneGoalDefinition definition, ZoneGoalExecutor executor)
    {
        _scene.Require(_actor);
        if (definition.Operation == ZoneGoalOperation.LookAt)
            throw ZoneManagedNpcActorScene.Missing("look-target generation and look-at output");
        return new ZoneGoalOperationFactory(executor, _scene.FrameMicroseconds, _scene.NextRandom,
            this, signals: this, controls: this, location: this, continuation: Continuation).Create(definition);
    }
    public uint NextRandom() => _scene.NextRandom();
    public bool EvaluateBranch(ZoneGoalParameters parameters) =>
        throw ZoneManagedNpcActorScene.Missing($"actor/world branch predicate {parameters.Integer & ~0x100}");
    public Vector3? GetAttentionTargetPosition() => _attention is null ? null : GetTargetPosition(_attention);
    public void SetArgument(object? argument) { _scene.Require(_actor); _targets.SetArgument(argument); }
    public void ClearAttentionTarget() => SetAttentionTarget(null);
    // Original CPipeUser vtable+290 is310C4334/nullsub_14. No engine side effect is omitted here.
    public void ResetActorAfterClear() => _scene.Require(_actor);
    // 310018AC visits optional entity-linked listeners. This owner has no such registrations.
    public void PreparePipeSelection() => _scene.Require(_actor);
    public void ResetForPipeSelection(ZoneGoalPipeFlags flags)
    {
        _scene.Require(_actor);
        if (IsPuppet)
        {
            if ((flags & ZoneGoalPipeFlags.DontResetStrafe) == 0) Controls.SetStrafeDistances(default);
            Controls.SetAdaptiveUrgency(0, 0, 0);
            Controls.SetDelayedStance(-1);
        }
        // There is no animation graph, pending path request or smart-object state allocated by
        // this shell. 3115D2B4 resets those optional owners; it does not clear CNavPath containers
        // or the independent movement-output controls. Path execution requires its own scene owner.
    }
    public void ResetAfterPipeClear() { _scene.Require(_actor); Controls.SignalsEnabled = true; }
    public void EnableSignals() { _scene.Require(_actor); Controls.SignalsEnabled = true; }
    public void ClearSelectionTarget() { _scene.Require(_actor); SelectionTarget = null; }
    public void ClearSelectionControls() => _scene.Require(_actor);
    public void RecordPipeEvent(int kind, string? name) => _scene.Record(_actor, "recorder", kind, name);
    // Verified31158A54→31012508 is only AIRecorder forwarding, not a gameplay-event switch.
    public void ActorPipeEvent(int kind, string? name) => _scene.Record(_actor, "actor", kind, name);
    public void Diagnostic(string message) => _scene.Warning(message);
    public void ReportSubPipeCount(int count, string rootName, string? insertingName) =>
        Diagnostic($"NPC {_actor.Handle} pipe '{rootName}' has {count} subpipes; inserting '{insertingName}'.");
    public void ReportActiveGoalCount(int count, string? rootName, ZoneGoalDefinition lastGoal) =>
        Diagnostic($"NPC {_actor.Handle} pipe '{rootName}' has {count} active goals after {lastGoal.Operation}.");
    public void ReportInsertionChain(IReadOnlyList<ZoneGoalPipeInstance> chain) =>
        Diagnostic($"NPC {_actor.Handle} insertion chain: {string.Join('/', chain.Select(pipe => pipe.Name))}.");

    public object? ResolveTarget(string name) => throw ZoneManagedNpcActorScene.Missing($"named target '{name}'");
    public void SetAttentionTarget(object? target)
    {
        _scene.Require(_actor);
        if (target is not null && target is not ZoneAiObjectState)
            throw new ArgumentException("Attention targets require actual managed AI-object state.", nameof(target));
        _attention = target;
    }
    // No character animation system/proxy was allocated by this actor shell. Native strafe
    // goals take their authored-parameter fallback when these optional components are absent.
    public object? GetCharacterSystem() => null;
    public object? GetActorProxy() => null;
    public uint GetCharacterKey(object proxy) => throw ZoneManagedNpcActorScene.Missing("character animation proxy identity");
    public ZoneCharacterStrafeSettings? GetCharacterStrafeSettings(object characterSystem, uint key) =>
        throw ZoneManagedNpcActorScene.Missing("character strafe settings");
    public bool CanStrafe() => throw ZoneManagedNpcActorScene.Missing("character locomotion strafe query");
    public Vector3 GetPosition() { _scene.Require(_actor); return _scene.Position(_actor); }
    public object? GetAttentionTarget() => _attention;
    public Vector3 GetTargetPosition(object target) => target is ZoneAiObjectState state ? state.Position :
        throw new ArgumentException("Target positions require actual AI-object state.", nameof(target));
    public object? ResolveTarget(string name, float range) => ResolveTarget(name);
    public object? FindNearest(Vector3 position, int objectType, float range)
    {
        _scene.Require(_actor);
        _scene.RefreshPositions();
        return _scene.Objects.FindNearest(position, objectType, range);
    }
    public object? GetArgument() => _targets.Argument;

    public void DevalueTarget(object? target, bool devalueFlag, float durationSeconds)
    {
        _scene.Require(_actor);
        if (target is null) return;
        if (!float.IsFinite(durationSeconds) || durationSeconds < 0) throw new ArgumentOutOfRangeException(nameof(durationSeconds));
        var duration = checked((long)((double)durationSeconds * 1_000_000));
        var expiry = checked(_scene.FrameMicroseconds() + duration);
        _devalued[target] = new(target, devalueFlag, expiry);
    }
    public void ClearDevaluedTargets() { _scene.Require(_actor); _devalued.Clear(); }
    public bool IsActor(object recipient) => _scene.ObjectOwner(recipient) is not null;
    public uint GetEntityId() => _actor.Entry.EntityId;
    public void Submit(object recipient, int kind, string name, uint senderEntityId, ZoneAiSignalData data)
    {
        _scene.Require(_actor);
        var target = _scene.ObjectOwner(recipient) ?? throw ZoneManagedNpcActorScene.Missing("the routed signal recipient actor");
        target.Npc.AiSignals.Submit(kind, name, senderEntityId, data);
    }
    public void Route(int filter, int kind, string name, ZoneAiSignalData data) =>
        throw ZoneManagedNpcActorScene.Missing($"group/perception signal filter {filter}");

    internal void RemoveTarget(ZoneAiObjectState target)
    {
        if (ReferenceEquals(_attention, target)) _attention = null;
        if (ReferenceEquals(_targets.Argument, target)) _targets.SetArgument(null);
        if (ReferenceEquals(SelectionTarget, target)) SelectionTarget = null;
        _devalued.Remove(target);
    }
    internal void ReleaseTargets() { _attention = null; _targets.SetArgument(null); _devalued.Clear(); }
}

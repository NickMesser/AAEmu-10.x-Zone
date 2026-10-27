using System.Numerics;

namespace AAEmu.Zone.Ai;

/// <summary>
/// Connects locate goals to the AI-object index and owns the actor's pipe argument and entity-binding updates.
/// Actor-specific named and special target resolution remains a required scene operation.
/// </summary>
public sealed class ZoneActorTargetState : IZoneLocateGoalServices
{
    private readonly ZoneAiObjectState _actor;
    private readonly ZoneAiObjectRegistry _objects;
    private readonly Func<string, float, ZoneAiObjectState?> _resolveNamed;

    public ZoneActorTargetState(ZoneAiObjectState actor, ZoneAiObjectRegistry objects,
        Func<string, float, ZoneAiObjectState?> resolveNamed)
    {
        _actor = actor ?? throw new ArgumentNullException(nameof(actor));
        _objects = objects ?? throw new ArgumentNullException(nameof(objects));
        _resolveNamed = resolveNamed ?? throw new ArgumentNullException(nameof(resolveNamed));
    }

    public ZoneAiObjectState? Argument { get; private set; }
    public Vector3 GetPosition() => _actor.Position;
    public Vector3 GetTargetPosition(object target) => RequireObject(target).Position;
    public object? ResolveTarget(string name, float range) => _resolveNamed(ZoneAiSignalName.CString(name), range);
    public object? FindNearest(Vector3 position, int objectType, float range) => _objects.FindNearest(position, objectType, range);
    public object? GetArgument() => Argument;

    /// <summary>
    /// 31158734: clear the old binding entity ID, replace the argument, then bind it to the actor's current entity ID.
    /// Reassigning the same target performs both writes; a shared target has one binding ID, not an owner set.
    /// </summary>
    public void SetArgument(object? target)
    {
        // Reject objects from an incompatible scene before changing the old argument.
        var next = target is null ? null : RequireObject(target);
        if (Argument is { } previous) previous.BindingEntityId = 0;
        Argument = next;
        if (next is not null) next.BindingEntityId = _actor.EntityId;
    }

    private static ZoneAiObjectState RequireObject(object target) => target as ZoneAiObjectState ??
        throw new ArgumentException("The target must have AI-object state in this scene.", nameof(target));
}

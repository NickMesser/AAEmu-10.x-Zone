using System.Numerics;

using AAEmu.Zone.Navigation;

namespace AAEmu.Zone.Actors;

/// <summary>Scene queries which remain external to an actor's owned path data.</summary>
public interface IZoneActorNavigationScene
{
    bool CanStrafe();
    Vector3 GetPosition();
    object? GetAttentionTarget();
    Vector3 GetTargetPosition(object target);
}

/// <summary>
/// Owns current/original paths and the separate movement-output snapshot used by actor movement controls.
/// The enclosing actor calls the copy operations at their native phases; this does not run pathfinding or locomotion.
/// </summary>
public sealed class ZoneActorNavigationState(IZoneActorNavigationScene scene) : IZoneActorStrafeServices
{
    private readonly IZoneActorNavigationScene _scene = scene ?? throw new ArgumentNullException(nameof(scene));
    private IReadOnlyList<ZoneNavigationMovementPoint> _movementOutput = Array.Empty<ZoneNavigationMovementPoint>();

    // CPipeUser constructor 31163974 initializes two independent CNavPath instances at +960 and +A30.
    public ZoneNavigationPath CurrentPath { get; } = new();
    public ZoneNavigationPath OriginalPath { get; } = new();
    public IReadOnlyList<ZoneNavigationMovementPoint> MovementOutput => _movementOutput;

    /// <summary>31044208, called at the end of HandlePathDecision (311757BC).</summary>
    public void CopyCurrentToOriginal() => OriginalPath.CopyFrom(CurrentPath);

    /// <summary>31142FF0: explicitly replace the output snapshot at the actor's movement-output phase.</summary>
    public void RefreshMovementOutput() => _movementOutput = CurrentPath.GetMovementOutput();

    public bool CanStrafe() => _scene.CanStrafe();
    // 3116B94C uses original-path data and the previously produced movement output independently.
    public bool HasPath() => OriginalPath.HasPath;
    public float GetPathLength() => OriginalPath.GetLength();
    public Vector3 GetPosition() => _scene.GetPosition();
    public object? GetAttentionTarget() => _scene.GetAttentionTarget();
    public Vector3 GetTargetPosition(object target) => _scene.GetTargetPosition(target);
    public Vector3? GetFirstPathOutputPosition() => _movementOutput.Count == 0 ? null : _movementOutput[0].Position;
}

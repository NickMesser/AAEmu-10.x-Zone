using System.Numerics;

namespace AAEmu.Zone.Ai;

/// <summary>The native actor and AI scene operations used by COPLocate.</summary>
public interface IZoneLocateGoalServices
{
    Vector3 GetPosition();
    Vector3 GetTargetPosition(object target);
    object? ResolveTarget(string name, float range);

    /// <summary>
    /// CAISystem 31055FB8 with minimum distance zero, no excluded object, and flags zero.
    /// The scene selects the nearest enabled AI object of the requested type within its search radius.
    /// </summary>
    object? FindNearest(Vector3 position, int objectType, float range);

    /// <summary>31158734: clear the previous argument's binding entity ID, then bind the new argument to this actor's entity ID.</summary>
    void SetArgument(object? target);
    object? GetArgument();
}

/// <summary>310DBE38: resolve a named or nearby target and store it as the actor's pipe argument.</summary>
public sealed class ZoneLocateGoal : IZoneGoalOperation
{
    private readonly IZoneLocateGoalServices _services;
    private readonly string _name;
    private readonly int _objectType;
    private readonly float _range;
    private bool _disposed;

    // 310F8054 owns the name and preserves the integer object type and single-precision range.
    public ZoneLocateGoal(string name, int objectType, float range, IZoneLocateGoalServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _name = ZoneAiSignalName.CString(name);
        _objectType = objectType;
        _range = range;
    }

    public int Execute()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_name.Length == 0)
        {
            var target = _services.FindNearest(_services.GetPosition(), _objectType, _range == 0 ? 50 : _range);
            _services.SetArgument(target);
            // This branch reads the resulting argument again; SetArgument can invoke actor callbacks.
            return _services.GetArgument() is null ? 3 : 2;
        }

        var namedTarget = _services.ResolveTarget(_name, _range);
        if (_name != "formation_id" && _range != 0 && namedTarget is not null)
        {
            var targetPosition = _services.GetTargetPosition(namedTarget);
            var position = _services.GetPosition();
            var x = position.X - targetPosition.X;
            var y = position.Y - targetPosition.Y;
            var z = position.Z - targetPosition.Z;
            // Preserve native scalar summation and ordered comparison, including negative and NaN ranges.
            if ((x * x + y * y) + z * z > _range * _range) namedTarget = null;
        }
        _services.SetArgument(namedTarget);
        return namedTarget is null ? 3 : 2;
    }

    // Native Reset is the shared empty virtual. Destroying the goal releases only its owned name.
    public void Reset() => ObjectDisposedException.ThrowIf(_disposed, this);
    public void Dispose() => _disposed = true;
}

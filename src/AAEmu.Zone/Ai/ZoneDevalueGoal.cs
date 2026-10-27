namespace AAEmu.Zone.Ai;

public interface IZoneDevalueGoalServices
{
    bool IsPuppet { get; }
    object? GetAttentionTarget();
    void DevalueTarget(object? target, bool devalueFlag, float durationSeconds);
    void ClearDevaluedTargets();
}

/// <summary>310D7308: COPDeValue, also instantiated for branch, random and clear entries.</summary>
public sealed class ZoneDevalueGoal(bool devalueFlag, bool clear, IZoneDevalueGoalServices services) : IZoneGoalOperation
{
    private readonly IZoneDevalueGoalServices _services = services ?? throw new ArgumentNullException(nameof(services));
    private bool _disposed;
    public int Execute()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (clear) _services.ClearDevaluedTargets();
        else if (_services.GetAttentionTarget() is not null && _services.IsPuppet)
            _services.DevalueTarget(_services.GetAttentionTarget(), devalueFlag, 20f);
        return 4;
    }
    public void Reset() => ObjectDisposedException.ThrowIf(_disposed, this);
    public void Dispose() => _disposed = true;
}

namespace AAEmu.Zone.Simulation;

/// <summary>An owned goal reference. Active goals retain their operations when a pipe is removed.</summary>
public sealed class ZoneGoalLease : IDisposable
{
    private readonly ZoneGoalOperationOwner? _owner;
    private bool _disposed;
    public ZoneGoalDefinition Definition { get; }
    public IZoneGoalOperation? Operation
    {
        get { ObjectDisposedException.ThrowIf(_disposed, this); return _owner?.Operation; }
    }

    internal ZoneGoalLease(ZoneGoalDefinition definition, ZoneGoalOperationOwner? owner)
    { Definition = definition; _owner = owner; owner?.Retain(); }

    public ZoneGoalLease Retain()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new(Definition, _owner);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _owner?.Release();
    }
}

// 310F4054 / 310F4058: one pipe reference plus any copied active-goal references.
internal sealed class ZoneGoalOperationOwner(IZoneGoalOperation operation)
{
    public IZoneGoalOperation Operation { get; } = operation;
    private int _references = 1;
    public void Retain()
    {
        ObjectDisposedException.ThrowIf(_references == 0, this);
        _references = checked(_references + 1);
    }
    public void Release()
    {
        if (--_references == 0) Operation.Dispose();
    }
}

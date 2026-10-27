namespace AAEmu.Zone.Simulation;

/// <summary>Actor wait-signal registrations, including the native duplicate registrations at frame time zero.</summary>
public sealed class ZoneSignalWaitRegistry : IDisposable
{
    private readonly List<ZoneWaitSignalGoal> _waiting = [];
    private bool _disposed;
    public int Count => _waiting.Count;

    internal void Register(ZoneWaitSignalGoal goal)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _waiting.Add(goal);
    }

    internal void Unregister(ZoneWaitSignalGoal goal) => _waiting.RemoveAll(entry => ReferenceEquals(entry, goal));

    /// <summary>3100695C. Normal actor admission calls this without data; direct notifications may supply it.</summary>
    public void Notify(string name, ZoneAiSignalData? data = null)
    {
        if (_disposed) return;
        name = ZoneAiSignalName.CString(name);
        for (var index = 0; index < _waiting.Count;)
            if (_waiting[index].Receive(name, data)) _waiting.RemoveAt(index); else index++;
    }

    public void Dispose() { _disposed = true; _waiting.Clear(); }
}

namespace AAEmu.Zone.Simulation;

/// <summary>
/// Cry engine DLLs retain process-global environments. A standalone physics host and a real
/// CSystem may never replace each other's environment, even after the first world is retired.
/// The retained owner also roots callbacks after an uncertain native initialization/cleanup.
/// </summary>
internal static class ZoneNativeProcessOwnership
{
    private static readonly object Gate = new();
    private static object? _owner;
    private static string? _kind;

    internal static void Claim(object owner, string kind)
    {
        lock (Gate)
        {
            if (_owner is not null)
                throw new InvalidOperationException($"This process has already admitted {_kind}; start a fresh Zone process for another native engine owner.");
            _owner = owner;
            _kind = kind;
        }
    }
}

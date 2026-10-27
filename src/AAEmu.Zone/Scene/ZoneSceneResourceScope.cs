using AAEmu.Zone.Hosting;
using AAEmu.Zone.Units;

namespace AAEmu.Zone.Scene;

/// <summary>
/// Ownership of one scene's managed resources. Factories register each allocation immediately,
/// before their next fallible operation. A returned resource transfers to this scope; a repeated
/// reference is retained once. This is replacement rollback policy, not native destruction order.
/// </summary>
public sealed class ZoneSceneResourceScope : IDisposable
{
    private readonly List<IDisposable> _resources = [];
    private readonly HashSet<IDisposable> _known = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<IDisposable> _delegated = new(ReferenceEqualityComparer.Instance);
    public bool IsDisposed { get; private set; }

    public T Own<T>(T resource) where T : class, IDisposable
    {
        ArgumentNullException.ThrowIfNull(resource);
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (_known.Add(resource)) _resources.Add(resource);
        return resource;
    }

    /// <summary>Transfers tick-service disposal to its real tick without also disposing the service directly.</summary>
    public ZoneSimulationTick CreateSimulation(ZoneUnitRegistry registry, IZoneSimulationTickServices services)
    {
        Own(services);
        if (_delegated.Contains(services)) throw new InvalidOperationException("These tick services already have an owner.");
        var tick = new ZoneSimulationTick(registry, services);
        _delegated.Add(services);
        return Own(tick);
    }

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        List<Exception>? errors = null;
        for (var index = _resources.Count - 1; index >= 0; index--)
        {
            if (_delegated.Contains(_resources[index])) continue;
            try { _resources[index].Dispose(); }
            catch (Exception error) { (errors ??= []).Add(error); }
        }
        _resources.Clear();
        if (errors is not null) throw new AggregateException("Scene resource retirement failed.", errors);
    }
}

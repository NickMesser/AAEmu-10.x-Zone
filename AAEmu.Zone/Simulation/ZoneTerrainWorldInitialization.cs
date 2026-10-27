using AAEmu.Zone.Content;

namespace AAEmu.Zone.Simulation;

/// <summary>Pure world inputs applied at the native terrain initialization boundary.</summary>
public static class ZoneTerrainWorldInitialization
{
    /// <summary>
    /// World XML 365676F0 writes oceanLevel to World+274; getter +B0/36540290 supplies the
    /// terrain parameter +14 through 30157300. Initialization 300CFEC0 replaces either signed
    /// zero with its no-ocean sentinel. Nonzero values, including NaN, pass through unchanged.
    /// </summary>
    public static float ResolveOceanLevel(ZoneWorldMapContent world)
    {
        ArgumentNullException.ThrowIfNull(world);
        var oceanLevel = world.Header.OceanLevel;
        return oceanLevel == 0f ? -1_000_000f : oceanLevel;
    }
}

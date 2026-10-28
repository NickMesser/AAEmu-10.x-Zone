namespace AAEmu.Zone.Scene;

/// <summary>
/// ZoneClient+8C ("server type"), set by 0x393654B0: 1 = non-dedicated client, 3 = a dedicated host whose map descriptor
/// string equals the global at 0x3A5192C0 (the continent map; the string itself was not recovered), 2 = every other dedicated host
/// (instances). Type 3 skips the initial detailed time-of-day (0x39332130) and both per-tick time reports
/// (0x393322F0/0x39332400), which is why the native continent zone 129 sent no ZW 0x001D/0x001E in the 6200 s baseline capture.
/// The managed zone cannot read the descriptor string, so an instance id of 0 (the continent host) stands in for it.
/// </summary>
public static class ZoneServerType
{
    public const int Instance = 2;
    public const int Continent = 3;

    public static int For(uint instanceId) => instanceId == 0 ? Continent : Instance;
}

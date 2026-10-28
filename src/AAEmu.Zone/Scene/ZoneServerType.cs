namespace AAEmu.Zone.Scene;

/// <summary>
/// ZoneClient+140 ("server type"), set by 0x393654B0: 1 = not dedicated; on a dedicated host the
/// sv_map zone's world name (descriptor +120 -> +72) is compared with XlCompareString against
/// off_3A5192C0 = "main_world" (pointer 0x39F93C68 in the r575 image): equal = 3, else 2 (instance
/// worlds). Type 3 skips the initial detailed time-of-day (0x39332130) and both per-tick time
/// reports (0x393322F0/0x39332400), which is why the native zone 129 sent no ZW 0x001D/0x001E.
/// </summary>
public static class ZoneServerType
{
    public const int Instance = 2;
    public const int MainWorld = 3;

    /// <summary>The native continent world name at off_3A5192C0.</summary>
    public const string MainWorldName = "main_world";

    public static int For(string? worldName) =>
        string.Equals(worldName, MainWorldName, StringComparison.Ordinal) ? MainWorld : Instance;
}

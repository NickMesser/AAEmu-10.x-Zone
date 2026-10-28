using System.Numerics;
using AAEmu.Zone.Content;

namespace AAEmu.Zone.Scene;

/// <summary>
/// Fixed-origin SeamlessSystem region ownership for one admitted scene. A region is the
/// canonical X2Zone world row, not a database area ID, a loaded terrain cell or a bounds test.
/// This owner borrows immutable world content and never moves a Unit or changes the origin.
/// </summary>
public sealed class ZoneManagedNpcRegions : IDisposable
{
    private readonly ZoneWorldMapLayout _layout;
    public bool IsDisposed { get; private set; }

    public ZoneManagedNpcRegions(ZoneWorldMapLayout layout) =>
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));

    /// <summary>Seamless virtual160/365696A0: the exact selected Zone object, independent of position.</summary>
    public ZoneWorldMapZone CurrentRegion
    {
        get { Require(); return _layout.Zone; }
    }

    /// <summary>
    /// Seamless virtual176/3656A740: engine-local model meters -> origin-relative cell ->
    /// full-world cell -> authored sector owner. Z is not read. Null represents an absent
    /// sector or invalid world/sector index, including nonrepresentable native int conversion.
    /// Original CVTTSS2SI truncates toward zero, including positions just below local zero.
    /// </summary>
    public ZoneWorldMapZone? GetRegion(Vector3 localModelPosition)
    {
        Require();
        if (!TryTruncate(localModelPosition.X * .0009765625f, out var localX) ||
            !TryTruncate(localModelPosition.Y * .0009765625f, out var localY)) return null;
        var cell = _layout.World.GetCell(unchecked(localX + _layout.Origin.X), unchecked(localY + _layout.Origin.Y));
        if (cell is null) return null;
        var x = (localModelPosition.X - (float)unchecked(localX << 10)) * .015625f;
        var y = (localModelPosition.Y - (float)unchecked(localY << 10)) * .015625f;
        return TryTruncate(x, out var sectorX) && TryTruncate(y, out var sectorY)
            ? cell.GetSectorOwner(sectorX, sectorY) : null;
    }

    private static bool TryTruncate(float value, out int result)
    {
        //Out-of-range and unordered CVTTSS2SI yield INT_MIN. A coordinate that large
        //also cannot form a valid0..15 sector after the native wrapped shift. Modern
        //managed casts can saturate instead; reject explicitly rather than depending on that.
        if (!float.IsFinite(value) || value >= 2147483648f || value < -2147483648f)
        { result = 0; return false; }
        result = (int)value;
        return true;
    }
    private void Require() => ObjectDisposedException.ThrowIf(IsDisposed, this);
    public void Dispose() => IsDisposed = true;
}

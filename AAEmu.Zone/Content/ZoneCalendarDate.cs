using System.Runtime.InteropServices;

namespace AAEmu.Zone.Content;

public enum ZoneCalendarPosition { After, During, Before }

/// <summary>
/// A native content calendar value, before conversion to the local C-runtime clock.
/// Zero year/month/day is an open endpoint. Invalid nonzero components follow 39B110A0.
/// </summary>
public readonly record struct ZoneCalendarDate(int Year, int Month, int Day, int Hour, int Minute)
{
    // XLCOMMON imports MSVCR100. UCRT accepts a wider upper range, so retain the
    // original runtime's bound explicitly (verified against both installed CRTs).
    private const long MaximumNativeSeconds = 0x793406FFF;

    // 39B0F3B0: zero endpoints are unbounded; both boundaries are inclusive.
    public static ZoneCalendarPosition PositionInPeriod(ulong seconds, ulong start, ulong end) =>
        start != 0 && seconds < start ? ZoneCalendarPosition.Before :
        end != 0 && seconds > end ? ZoneCalendarPosition.After : ZoneCalendarPosition.During;
    public ulong ToUnixTimeSeconds()
    {
        if (Year == 0 || Month == 0 || Day == 0) return 0;
        RequireWindows();
        var time = new CalendarTime
        {
            Year = Math.Max(Year, 1900) - 1900, Month = Math.Clamp(Month, 1, 12) - 1,
            Day = Math.Clamp(Day, 1, 31), Hour = Math.Clamp(Hour, 0, 23), Minute = Math.Clamp(Minute, 0, 59),
            DaylightSaving = 0
        };
        // XLCOMMON!XlMakeFileTime tail-calls _mktime64. It normalizes calendar dates,
        // forces standard time via tm_isdst=0, and retains -1 as unsigned all-ones on failure.
        var seconds = MakeTime(ref time);
        return seconds > MaximumNativeSeconds ? ulong.MaxValue : unchecked((ulong)seconds);
    }

    public ZoneCalendarDate ShiftMinutes(int minutes)
    {
        if (minutes == 0) return this;
        var seconds = ToUnixTimeSeconds();
        if (seconds == 0) return this;
        var shifted = unchecked((long)seconds + (long)minutes * 60);
        // 39B0F1E0 / XlGetLocalTime ignore the conversion result, including the CRT's
        // all-minus-one output on an invalid timestamp. The descriptor keeps those fields.
        // Also avoid invoking the CRT invalid-parameter handler on an invalid timestamp.
        if (shifted < 0 || shifted > MaximumNativeSeconds) return new(1899, 0, -1, -1, -1);
        LocalTime(out var time, ref shifted);
        return new(time.Year + 1900, time.Month + 1, time.Day, time.Hour, time.Minute);
    }

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Native content calendars currently require the Windows C runtime's local-time rules.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CalendarTime
    {
        public int Second, Minute, Hour, Day, Month, Year, Weekday, DayOfYear, DaylightSaving;
    }

    [DllImport("ucrtbase.dll", EntryPoint = "_mktime64", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern long MakeTime(ref CalendarTime time);

    [DllImport("ucrtbase.dll", EntryPoint = "_localtime64_s", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int LocalTime(out CalendarTime time, ref long seconds);
}

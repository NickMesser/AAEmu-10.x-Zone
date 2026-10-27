using System.Diagnostics;

namespace AAEmu.Zone.WorldState;

/// <summary>
/// World game milliseconds: WZ handler 0x393643E0 supplies the same local timestamp at both
/// ends of TimeKeeper::AddSample (0x3303CEC0), with remote generation zero. All five latency
/// values are consequently zero; the native filtered mean includes every stored sample.
/// </summary>
public sealed class ZoneWorldGameClock : IDisposable
{
    private readonly Func<uint> _localGameMilliseconds;
    private readonly int[] _samples = new int[5];
    private int _nextSample;

    public uint FirstWorldTime { get; private set; }
    public int MeanLocalTime { get; private set; }
    public uint? LastReceivedWorldTime { get; private set; }
    public bool IsDisposed { get; private set; }

    /// <summary>
    /// Supply the scene's game clock when available. Otherwise a connection-local monotonic
    /// Stopwatch provides the replacement host clock; it does not simulate native pause modes.
    /// </summary>
    public ZoneWorldGameClock(Func<uint>? localGameMilliseconds = null)
    {
        var started = Stopwatch.GetTimestamp();
        _localGameMilliseconds = localGameMilliseconds ?? (() =>
            unchecked((uint)((Stopwatch.GetTimestamp() - started) * 1000 / Stopwatch.Frequency)));
    }

    public void Receive(uint worldMilliseconds)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        var local = _localGameMilliseconds();
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        Receive(worldMilliseconds, local);
    }

    public void Receive(uint worldMilliseconds, uint localMilliseconds)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        LastReceivedWorldTime = worldMilliseconds;
        if (FirstWorldTime == 0)
        {
            FirstWorldTime = worldMilliseconds;
            Array.Fill(_samples, Truncate((float)unchecked((int)localMilliseconds)));
        }
        else
        {
            var relative = unchecked((int)(FirstWorldTime - worldMilliseconds + localMilliseconds));
            _samples[_nextSample] = Truncate((float)relative);
            _nextSample = (_nextSample + 1) % 5;
        }
        var total = 0;
        foreach (var sample in _samples) total = unchecked(total + sample);
        MeanLocalTime = total / 5;
    }

    public uint GetTime()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        var local = _localGameMilliseconds();
        return GetTime(local);
    }

    /// <summary>CTimer 0x365AD250 / TimeKeeper::GetRemoteTime; unsigned millisecond wrap is retained.</summary>
    public uint GetTime(uint localMilliseconds)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        return unchecked(FirstWorldTime - (uint)MeanLocalTime + localMilliseconds);
    }

    // CVTTSS2SI returns INT_MIN for positive overflow, rather than the managed saturating conversion.
    private static int Truncate(float value) => value >= 2147483648f || value < -2147483648f
        ? int.MinValue : (int)value;

    public void Dispose() => IsDisposed = true;
}

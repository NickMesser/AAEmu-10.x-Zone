using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AAEmu.Zone.Native.Engine;

/// <summary>
/// The native dedicated console requires real Win32 screen-buffer handles, even with external
/// managed logs. Mirrors the native host's CONIN$/CONOUT$ setup, hiding only a newly owned console.
/// Keep its handles through engine shutdown; restore borrowed standard handles before closing it.
/// </summary>
internal sealed class ZoneNativeEngineConsole : IDisposable
{
    private readonly nint _originalInput = GetStdHandle(-10);
    private readonly nint _originalOutput = GetStdHandle(-11);
    private readonly nint _originalError = GetStdHandle(-12);
    private SafeFileHandle? _input;
    private SafeFileHandle? _output;
    private bool _allocated;
    private bool _disposed;
    private bool _started;

    internal void Initialize()
    {
        if (_started || _disposed) throw new InvalidOperationException("The native console lifetime has already started.");
        _started = true;
        // Materialize the managed writers while their inherited/redirected handles are still
        // current. Native screen output then uses its own handles, without replacing these logs.
        _ = Console.Out;
        _ = Console.Error;
        if (GetConsoleWindow() == 0)
        {
            if (!AllocConsole()) throw Error("Cannot allocate the native engine console.");
            _allocated = true;
            var window = GetConsoleWindow();
            if (window != 0) _ = ShowWindow(window, 0);
        }
        _input = CreateFileW("CONIN$", 0xc0000000, 3, 0, 3, 0, 0);
        if (_input.IsInvalid) throw Error("Cannot open the native console input.");
        _output = CreateFileW("CONOUT$", 0xc0000000, 3, 0, 3, 0, 0);
        if (_output.IsInvalid) throw Error("Cannot open the native console screen buffer.");
        if (!SetStdHandle(-10, _input.DangerousGetHandle()) ||
            !SetStdHandle(-11, _output.DangerousGetHandle()) || !SetStdHandle(-12, _output.DangerousGetHandle()))
            throw Error("Cannot install the native console standard handles.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        // Restore every handle even when one fails. The previous redirected file/pipe handles
        // were borrowed, never closed by this owner. Allocation also changes std handles.
        List<Exception> errors = [];
        if (!SetStdHandle(-10, _originalInput)) errors.Add(Error("Cannot restore standard input."));
        if (!SetStdHandle(-11, _originalOutput)) errors.Add(Error("Cannot restore standard output."));
        if (!SetStdHandle(-12, _originalError)) errors.Add(Error("Cannot restore standard error."));
        if (errors.Count != 0) throw new AggregateException("Cannot restore the process standard handles.", errors);
        _input?.Dispose();
        _output?.Dispose();
        if (_allocated && !FreeConsole()) throw Error("Cannot release the owned engine console.");
        _disposed = true;
    }

    private static Win32Exception Error(string message) => new(Marshal.GetLastWin32Error(), message);
    [DllImport("kernel32")] private static extern nint GetStdHandle(int kind);
    [DllImport("kernel32")] private static extern nint GetConsoleWindow();
    [DllImport("kernel32", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AllocConsole();
    [DllImport("kernel32", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FreeConsole();
    [DllImport("kernel32", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetStdHandle(int kind, nint handle);
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("user32")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(nint window, int command);
}

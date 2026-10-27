using System.Runtime.InteropServices;
using System.Text;

namespace AAEmu.Zone.Native.Engine;

/// <summary>
/// Explicit inputs for the original engine services, independent of native game startup.
/// CommandLine is the complete native command line including the executable token.
/// WorkingDirectory selects the native CryPak/configuration root; managed asset-source order
/// does not configure CryPak. SaveDirectory and log output should be outside the repository.
/// </summary>
public sealed class ZoneNativeEngineOptions
{
    public string BinaryDirectory { get; }
    public string WorkingDirectory { get; }
    public string SaveDirectory { get; }
    public string GamePakPath { get; }
    public string LogName { get; }
    public string CommandLine { get; }
    public bool FullDump { get; }

    public ZoneNativeEngineOptions(string binaryDirectory, string workingDirectory,
        string saveDirectory, string gamePakPath, string logName, string commandLine, bool fullDump = false)
    {
        BinaryDirectory = Absolute(binaryDirectory);
        WorkingDirectory = Absolute(workingDirectory);
        SaveDirectory = Absolute(saveDirectory);
        GamePakPath = Absolute(gamePakPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(logName);
        if (logName is "." or ".." || logName.IndexOfAny(['/', '\\', ':']) >= 0)
            throw new ArgumentException("The native log name must be a file name, not a path.", nameof(logName));
        LogName = logName;
        ArgumentException.ThrowIfNullOrWhiteSpace(commandLine);
        CommandLine = commandLine;
        FullDump = fullDump;
        // These original interfaces have byte strings and fixed native buffers. Until the
        // configured native encoding is proven, reject lossy conversion instead of replacing
        // path characters. This is an explicit admission limit, not an inferred engine limit.
        ValidateAscii(SaveDirectory, 0x104);
        ValidateAscii(LogName, 0x104);
        ValidateAscii(CommandLine, 0x800);
    }

    private static string Absolute(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException("Native engine paths must be absolute.", nameof(path));
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    internal static void ValidateAscii(string value, int capacity)
    {
        if (value.Length >= capacity || value.Any(character => character is '\0' or > '\x7f'))
            throw new ArgumentException($"Native text must be ASCII without embedded NUL and fit within {capacity - 1} bytes.");
    }
}

/// <summary>The retained, exact 0xA00 dedicatedserver startup block, consumed by real CSystem.</summary>
internal sealed unsafe class ZoneNativeEngineParameters : IDisposable
{
    private nint _parameters;
    private nint _saveDirectory;
    private nint _logName;
    internal nint Pointer => _parameters != 0 ? _parameters : throw new ObjectDisposedException(nameof(ZoneNativeEngineParameters));

    internal ZoneNativeEngineParameters(ZoneNativeEngineOptions options, nint applicationModule,
        nint context, string resolvedSaveDirectory, long gamePakStamp)
    {
        if (IntPtr.Size != 8) throw new PlatformNotSupportedException("Engine startup requires 64-bit pointers.");
        if (applicationModule == 0 || context == 0)
            throw new ArgumentException("Engine startup requires an application module and a retained startup context.");
        ZoneNativeEngineOptions.ValidateAscii(resolvedSaveDirectory, 0x104);
        try
        {
            _saveDirectory = Marshal.StringToCoTaskMemUTF8(resolvedSaveDirectory);
            _logName = Marshal.StringToCoTaskMemUTF8(options.LogName);
            _parameters = (nint)NativeMemory.AllocZeroed(0xa00);
            if (_parameters == 0) throw new OutOfMemoryException();
            var bytes = new Span<byte>((void*)_parameters, 0xa00);
            Marshal.WriteIntPtr(_parameters, applicationModule);
            Marshal.WriteIntPtr(_parameters + 0x28, _saveDirectory);
            Marshal.WriteIntPtr(_parameters + 0x30, _logName);
            Encoding.ASCII.GetBytes(options.CommandLine, bytes.Slice(0x40, 0x800));
            Marshal.WriteInt32(_parameters + 0x940, 2);
            Marshal.WriteInt64(_parameters + 0x948, 0x23f);
            ReadOnlySpan<byte> build = [0x82, 0xb4, 0x41, 0x5a, 0xa4, 0x55, 0xd2, 0x2a,
                0x46, 0x59, 0xa3, 0x2c, 0x2e, 0x17, 0x97, 0x0b];
            build.CopyTo(bytes[0x950..]);
            bytes[0x960] = 1;
            Marshal.WriteIntPtr(_parameters + 0x968, context);
            bytes[0x970] = 1;
            bytes.Slice(0x977, 10).Fill(1);
            bytes[0x977] = 0; // The real CSystem network-module gate, not a fake network object.
            Marshal.WriteInt64(_parameters + 0x9a0, gamePakStamp);
            bytes[0x9ac] = 1;
        }
        catch { Dispose(); throw; }
    }

    public void Dispose()
    {
        NativeMemory.Free((void*)_parameters);
        _parameters = 0;
        Marshal.FreeCoTaskMem(_saveDirectory);
        _saveDirectory = 0;
        Marshal.FreeCoTaskMem(_logName);
        _logName = 0;
    }
}

using System.Text;

namespace AAEmu.Zone.Hosting;

/// <summary>Process-entry console logging. Restores borrowed console writers before closing its own file.</summary>
internal sealed class ZoneConsoleLog : IDisposable
{
    private readonly TextWriter _stdout = Console.Out;
    private readonly TextWriter _stderr = Console.Error;
    private readonly TextWriter _file;
    public string Path { get; }
    private bool _disposed;

    /// <param name="name">A launcher-chosen file name (World's AAEMU_ZONE_LOG_NAME), appended to; null = a unique name.</param>
    public ZoneConsoleLog(string directory, string? name = null)
    {
        Directory.CreateDirectory(directory);
        Path = System.IO.Path.Combine(directory, name is null
            ? $"zone-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Environment.ProcessId}-{Guid.NewGuid():N}.log"
            : System.IO.Path.GetFileName(name));
        _file = TextWriter.Synchronized(new StreamWriter(new FileStream(Path, name is null ? FileMode.CreateNew : FileMode.Append,
            FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true });
        Console.SetOut(new Tee(_stdout, _file));
        Console.SetError(new Tee(_stderr, _file));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Console.SetOut(_stdout);
        Console.SetError(_stderr);
        _file.Dispose();
    }

    private sealed class Tee(TextWriter console, TextWriter file) : TextWriter
    {
        public override Encoding Encoding => console.Encoding;
        public override void Write(char value) { console.Write(value); file.Write(value); }
        public override void Write(string? value) { console.Write(value); file.Write(value); }
        public override void WriteLine(string? value) { console.WriteLine(value); file.WriteLine(value); }
        public override void Flush() { console.Flush(); file.Flush(); }
    }
}

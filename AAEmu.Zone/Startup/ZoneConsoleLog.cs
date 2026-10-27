using System.Text;

namespace AAEmu.Zone.Startup;

/// <summary>Process-entry console logging. Restores borrowed console writers before closing its own file.</summary>
internal sealed class ZoneConsoleLog : IDisposable
{
    private readonly TextWriter _stdout = Console.Out;
    private readonly TextWriter _stderr = Console.Error;
    private readonly TextWriter _file;
    public string Path { get; }
    private bool _disposed;

    public ZoneConsoleLog(string directory)
    {
        Directory.CreateDirectory(directory);
        Path = System.IO.Path.Combine(directory, $"zone-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Environment.ProcessId}-{Guid.NewGuid():N}.log");
        _file = TextWriter.Synchronized(new StreamWriter(new FileStream(Path, FileMode.CreateNew,
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

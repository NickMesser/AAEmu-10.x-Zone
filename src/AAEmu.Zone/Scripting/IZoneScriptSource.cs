namespace AAEmu.Zone.Scripting;

/// <summary>Read-only script bytes and the resolved filename used in Lua diagnostics.</summary>
public sealed record ZoneScriptFile(ReadOnlyMemory<byte> Bytes, string Filename);

public interface IZoneScriptSource
{
    /// <summary>Read a slash-normalized game path; return null when the file cannot be opened.</summary>
    ZoneScriptFile? Read(string filename);
}

/// <summary>Loads loose game scripts from an explicitly configured content directory.</summary>
public sealed class ZoneDirectoryScriptSource : IZoneScriptSource
{
    public string Root { get; }

    public ZoneDirectoryScriptSource(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (!Path.IsPathFullyQualified(root)) throw new ArgumentException("Supply an absolute game content directory.", nameof(root));
        Root = Path.GetFullPath(root);
        if (!Directory.Exists(Root)) throw new DirectoryNotFoundException(Root);
    }

    public ZoneScriptFile? Read(string filename)
    {
        ArgumentNullException.ThrowIfNull(filename);
        var path = Path.GetFullPath(filename.Replace('/', Path.DirectorySeparatorChar), Root);
        var relative = Path.GetRelativePath(Root, path);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new IOException("The script filename is outside the configured game content directory.");
        try { return new(File.ReadAllBytes(path), path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
}

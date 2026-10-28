using System.Text;

namespace AAEmu.Zone.Scripting;

/// <summary>CScriptSystem file execution and the corresponding Lua Script bindings.</summary>
public sealed class ZoneScriptLoader
{
    private readonly IZoneLuaRuntime _lua;
    private readonly IZoneScriptSource _source;
    private readonly Action<string> _error;
    private readonly Func<int> _compiledMode;
    private readonly HashSet<string> _loaded = new(StringComparer.Ordinal);
    private int _forceDepth;
    private bool _bindingsInstalled;
    /// <summary>All failed file executions, including nested loads whose caller ignores the return.</summary>
    public long FailureCount { get; private set; }
    public string? LastFailure { get; private set; }

    public ZoneScriptLoader(IZoneLuaRuntime lua, IZoneScriptSource source, Action<string> error, Func<int> compiledMode)
    {
        _lua = lua ?? throw new ArgumentNullException(nameof(lua));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _error = error ?? throw new ArgumentNullException(nameof(error));
        _compiledMode = compiledMode ?? throw new ArgumentNullException(nameof(compiledMode));
    }

    public IReadOnlyList<string> LoadedScripts => _lua.WithStack(() =>
        (IReadOnlyList<string>)_loaded.Order(ZoneUtf8StringComparer.Instance).ToArray());

    // 3550F140: slash normalization, case-sensitive cache, compiled-path substitution and nested force depth.
    public bool ExecuteFile(string filename, bool raiseError = true, bool forceReload = false) => _lua.WithStack(() =>
    {
        var path = Normalize(filename);
        if (path.Length == 0) return false;
        if (_compiledMode() == 1 && path.StartsWith("scripts/", StringComparison.OrdinalIgnoreCase))
        {
            path = path.Replace("scripts/", "scriptsbin64/", StringComparison.OrdinalIgnoreCase)
                .Replace(".lua", ".alb", StringComparison.OrdinalIgnoreCase);
            CheckLength(path);
        }
        var existed = _loaded.Contains(path);
        if (existed && !forceReload && _forceDepth == 0) return true;
        if (forceReload) _forceDepth++;
        try
        {
            if (!ExecuteBytes(path, raiseError))
            {
                if (existed) _loaded.Remove(path);
                return false;
            }
            if (!existed) _loaded.Add(path);
            return true;
        }
        finally { if (forceReload) _forceDepth--; }
    });

    // 3550EE70: this deliberately does not apply the compiled-script substitution or undo globals.
    public void UnloadScript(string filename) => _lua.WithStack(() => _loaded.Remove(Normalize(filename)));

    public void InstallBindings() => _lua.WithStack(() =>
    {
        if (_bindingsInstalled) return 0;
        var existing = _lua.GetGlobal("Script");
        using var script = existing is null ? _lua.CreateTable() : existing as ZoneLuaReference;
        if (script is not { IsTable: true }) throw new InvalidOperationException("The Script global must be a table.");
        Bind("LoadScript", arguments =>
        {
            if (arguments.Count == 0 || arguments[0] is not string filename) throw new ArgumentException("Script.LoadScript requires a filename string.");
            return ExecuteFile(filename, Flag(arguments, 2, true), Flag(arguments, 1, false)) ? [1.0] : [];
        });
        // 3552DE20 passes (raiseError=1, forceReload=0), despite the Lua binding's name.
        Bind("ReloadScript", arguments =>
        {
            if (arguments.Count != 0 && arguments[0] is string filename) ExecuteFile(filename);
            return [];
        });
        Bind("UnloadScript", arguments =>
        {
            if (arguments.Count != 0 && arguments[0] is string filename) UnloadScript(filename);
            return [];
        });
        _lua.SetGlobal("Script", script);
        _bindingsInstalled = true;
        return 0;

        void Bind(string name, Func<IReadOnlyList<object?>, object?[]> callback)
        {
            using var function = _lua.CreateFunction(callback);
            script.Set(name, function);
        }
    });

    private bool ExecuteBytes(string path, bool raiseError)
    {
        ZoneScriptFile? file;
        try { file = _source.Read(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Failure($"[Lua Error] Failed to load script file {path}: {error.Message}", raiseError);
            return false;
        }
        if (file is null)
        {
            Failure($"[Lua Error] Failed to load script file {path}", raiseError);
            return false;
        }
        var bytes = file.Bytes;
        if (bytes.Length == 0) { Failure($"[Lua Error] Empty script file {path}", false); return false; }
        if (bytes.Span.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) bytes = bytes[3..];
        try
        {
            using var function = _lua.Compile(bytes, "@" + file.Filename.Replace('\\', '/'));
            _lua.Invoke(function);
            return true;
        }
        catch (ZoneLuaException error)
        {
            // raiseError only controls missing-file diagnostics, not compilation/execution errors.
            Failure($"[Lua Error] Failed to execute file {file.Filename}: {error.Message}", true);
            return false;
        }
    }

    private void Failure(string message, bool report)
    {
        FailureCount = checked(FailureCount + 1);
        LastFailure = message;
        if (report) _error(message);
    }

    private static bool Flag(IReadOnlyList<object?> arguments, int index, bool fallback) =>
        index >= arguments.Count ? fallback : arguments[index] switch
        {
            bool value => value,
            long value => value != 0,
            double value => value != 0,
            _ => fallback
        };

    private static string Normalize(string filename)
    {
        ArgumentNullException.ThrowIfNull(filename);
        var end = filename.IndexOf('\0');
        var result = (end < 0 ? filename : filename[..end]).Replace('\\', '/');
        CheckLength(result);
        return result;
    }

    private static void CheckLength(string filename)
    {
        if (Encoding.UTF8.GetByteCount(filename) >= 260)
            throw new ArgumentException("Native script filenames must fit in 259 UTF-8 bytes.", nameof(filename));
    }
}

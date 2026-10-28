using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace AAEmu.Zone.Scripting;

/// <summary>A serialized Lua 5.1 double+int64 state with protected script/managed calls and owned references.</summary>
public sealed class ZoneLuaRuntime : IZoneLuaRuntime
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly object _gate = new();
    private readonly Lua51Native _api;
    private readonly List<Lua51Native.CFunction> _callbacks = [];
    private nint _library, _state, _callbackState;
    private int _callDepth;
    private bool _closing;
    private ZoneLuaReference _get = null!, _set = null!, _wrap = null!, _globals = null!, _vector = null!;
    public bool IsDisposed => _state == 0 || _closing;
    public string LibraryPath { get; }

    public ZoneLuaRuntime(string libraryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryPath);
        if (!Path.IsPathFullyQualified(libraryPath)) throw new ArgumentException("Supply an absolute Lua 5.1 library path.", nameof(libraryPath));
        if (IntPtr.Size != 8) throw new PlatformNotSupportedException("The Zone script ABI currently requires a 64-bit process.");
        LibraryPath = Path.GetFullPath(libraryPath);
        if (!File.Exists(LibraryPath)) throw new FileNotFoundException("The configured Lua 5.1 library does not exist.", LibraryPath);
        _library = NativeLibrary.Load(LibraryPath);
        try
        {
            _api = new(_library);
            _state = _api.NewState();
            if (_state == 0) throw new ZoneLuaException("Lua could not allocate a script state.");
            _api.OpenLibraries(_state);
            using var version = Execute("return _VERSION, _LNUM, 9007199254740993-9007199254740992", "=zone-abi");
            // Stock LNUM (lua514-lnum-20090417) reports "double int64"; some builds join the parts with '+'.
            if (version.Count != 3 || version[0] is not "Lua 5.1" || version[1] is not ("double+int64" or "double int64") || version[2] is not 1L)
                throw new NotSupportedException("Zone scripts require Lua 5.1 with the double+int64 patch.");
            // File access belongs to the configured content loader. Engine bindings are installed explicitly.
            // Capture primitives so scripts cannot replace the host's protected-call helpers.
            using var helpers = Execute("""
                io=nil; os=nil; package=nil; require=nil; dofile=nil; loadfile=nil; debug=nil; newproxy=nil
                local unpack, select, error, tonumber, rawget = unpack, select, error, tonumber, rawget
                local function pack(...) return {n=select('#',...), ...} end
                return function(t,k) return t[k] end,
                       function(t,k,v) t[k]=v end,
                       function(f) return function(...)
                           local r=pack(f(...))
                           if not r[1] then error(r[2],2) end
                           return unpack(r,2,r.n)
                       end end,
                       _G,
                       function(t)
                           local x=tonumber(rawget(t,'x'))
                           local y,z
                           if x==nil then
                               x=tonumber(rawget(t,1))
                               if x==nil then return end
                               y=tonumber(rawget(t,2)); z=tonumber(rawget(t,3))
                           else
                               y=tonumber(rawget(t,'y')); z=tonumber(rawget(t,'z'))
                           end
                           if y~=nil and z~=nil then return x,y,z end
                       end
                """, "=zone-host");
            _get = ((ZoneLuaReference)helpers[0]!).Retain(); _set = ((ZoneLuaReference)helpers[1]!).Retain();
            _wrap = ((ZoneLuaReference)helpers[2]!).Retain(); _globals = ((ZoneLuaReference)helpers[3]!).Retain();
            _vector = ((ZoneLuaReference)helpers[4]!).Retain();
        }
        catch { Close(); throw; }
    }

    private nint State => _callbackState != 0 ? _callbackState : _state;

    public ZoneLuaValues Execute(string source, string chunkName = "=zone")
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(chunkName);
        return WithStack(() =>
        {
            var start = _api.GetTop(State); var bytes = Encoding.UTF8.GetBytes(source);
            if (_api.LoadBuffer(State, bytes, (nuint)bytes.Length, Encoding.UTF8.GetBytes(chunkName + '\0')) != 0) throw Error();
            Invoke(0); return ReadResults(start);
        });
    }

    /// <summary>Compile source or native Lua 5.1 bytecode without converting its bytes to text.</summary>
    public ZoneLuaReference Compile(ReadOnlyMemory<byte> source, string chunkName)
    {
        ArgumentNullException.ThrowIfNull(chunkName);
        return WithStack(() =>
        {
            var bytes = source.ToArray();
            if (_api.LoadBuffer(State, bytes, (nuint)bytes.Length, Encoding.UTF8.GetBytes(chunkName + '\0')) != 0) throw Error();
            return (ZoneLuaReference)Read(-1)!;
        });
    }

    public ZoneLuaReference CreateTable() => WithStack(() =>
    {
        _api.CreateTable(State, 0, 0); return (ZoneLuaReference)Read(-1)!;
    });

    public object? GetGlobal(string name) => Get(_globals, name);
    public void SetGlobal(string name, object? value) => Set(_globals, name, value);

    internal object? Get(ZoneLuaReference table, object key, int? expectedType = null) => WithStack(() =>
    {
        RequireTable(table); Push(_get); Push(table); Push(key); Invoke(2);
        return expectedType is { } type && _api.Type(State, -1) != type ? null : Read(-1);
    });

    internal void Set(ZoneLuaReference table, object key, object? value) => WithStack(() =>
    {
        RequireTable(table); Push(_set); Push(table); Push(key); Push(value); Invoke(3); return 0;
    });

    // 3550C4C0: raw named fields, or raw array entries when x is not convertible to a number.
    internal Vector3? GetVector(ZoneLuaReference table) => WithStack<Vector3?>(() =>
    {
        RequireTable(table); var start = _api.GetTop(State); Push(_vector); Push(table); Invoke(1);
        return _api.GetTop(State) - start == 3
            ? new Vector3((float)_api.ToNumber(State, -3), (float)_api.ToNumber(State, -2), (float)_api.ToNumber(State, -1))
            : null;
    });

    public ZoneLuaValues Call(ZoneLuaReference function, params object?[] arguments)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(arguments);
        return WithStack(() =>
        {
            if (!function.IsFunction) throw new ArgumentException("A Lua function is required.", nameof(function));
            var start = _api.GetTop(State); Reserve(checked(arguments.Length + 1)); Push(function);
            foreach (var argument in arguments) Push(argument);
            Invoke(arguments.Length); return ReadResults(start);
        });
    }

    /// <summary>Invoke a function while discarding all return values, including userdata and threads.</summary>
    public void Invoke(ZoneLuaReference function, params object?[] arguments)
    {
        ArgumentNullException.ThrowIfNull(function); ArgumentNullException.ThrowIfNull(arguments);
        WithStack(() =>
        {
            if (!function.IsFunction) throw new ArgumentException("A Lua function is required.", nameof(function));
            Reserve(checked(arguments.Length + 1)); Push(function);
            foreach (var argument in arguments) Push(argument);
            if (_api.ProtectedCall(State, arguments.Length, 0, 0) != 0) throw Error();
            return 0;
        });
    }

    /// <summary>Callback arguments are borrowed references; Retain any table/function needed after the call.</summary>
    public ZoneLuaReference CreateFunction(Func<IReadOnlyList<object?>, object?[]> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return WithStack(() =>
        {
            Lua51Native.CFunction bridge = state => Callback(state, callback);
            _callbacks.Add(bridge); // native closures retain this delegate until the state closes
            Push(_wrap); _api.PushClosure(State, bridge, 0); Invoke(1);
            return (ZoneLuaReference)Read(-1)!;
        });
    }

    private int Callback(nint state, Func<IReadOnlyList<object?>, object?[]> callback)
    {
        // Closing a VM must not invoke application code on a finalizer thread or re-enter lua_close.
        if (_closing) { _api.PushBoolean(state, 1); return 1; }
        var previous = _callbackState; _callbackState = state;
        var count = _api.GetTop(state);
        try
        {
            using var arguments = ReadResults(0);
            var results = callback(arguments) ?? throw new InvalidOperationException("A Lua callback returned no result array.");
            Reserve(checked(results.Length + 1)); _api.PushBoolean(state, 1);
            foreach (var result in results) Push(result);
            return results.Length + 1;
        }
        catch (Exception error)
        {
            // Never call lua_error/longjmp across a CLR callback frame. The Lua wrapper raises it after return.
            _api.SetTop(state, count); _api.PushBoolean(state, 0); Push(error.Message); return 2;
        }
        finally { _callbackState = previous; }
    }

    internal ZoneLuaReference Retain(ZoneLuaReference reference) => WithStack(() =>
    { Push(reference); return (ZoneLuaReference)Read(-1)!; });

    internal void Release(ZoneLuaReference reference)
    {
        lock (_gate)
        {
            if (reference.RegistryId < 0) return;
            if (!IsDisposed) _api.Unreference(State, Lua51Native.Registry, reference.RegistryId);
            reference.Released();
        }
    }

    internal T WithStack<T>(Func<T> operation)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            var state = State; var top = _api.GetTop(state); _callDepth++;
            try { Reserve(16); return operation(); }
            finally { _api.SetTop(state, top); _callDepth--; }
        }
    }

    private void Reserve(int count)
    { if (_api.CheckStack(State, count) == 0) throw new InvalidOperationException("Lua stack capacity exceeded."); }

    private void RequireTable(ZoneLuaReference table)
    { if (!table.IsTable) throw new ArgumentException("A Lua table is required.", nameof(table)); }

    private void Invoke(int arguments)
    { if (_api.ProtectedCall(State, arguments, -1, 0) != 0) throw Error(); }

    private ZoneLuaException Error() => new(_api.Type(State, -1) == 4 ? ReadString(-1) : "Lua raised a non-string error.");

    private byte[] ReadBytes(int index)
    {
        var address = _api.ToByteString(State, index, out var length);
        if (length > int.MaxValue) throw new InvalidDataException("Lua string exceeds managed capacity.");
        var bytes = new byte[(int)length]; if (bytes.Length != 0) Marshal.Copy(address, bytes, 0, bytes.Length);
        return bytes;
    }

    private string ReadString(int index) => Encoding.UTF8.GetString(ReadBytes(index));

    private object ReadStringValue(int index)
    {
        var bytes = ReadBytes(index);
        try { return StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { return bytes; }
    }

    private object? Read(int index)
    {
        switch (_api.Type(State, index))
        {
            case 0: return null;
            case 1: return _api.ToBoolean(State, index) != 0;
            case 2: return new ZoneLuaHandle(unchecked((ulong)_api.ToUserData(State, index)));
            case 3: return _api.IsInteger(State, index) != 0 ? (object)_api.ToInteger(State, index) : _api.ToNumber(State, index);
            case 4: return ReadStringValue(index);
            case 5: case 6:
                var table = _api.Type(State, index) == 5;
                _api.PushValue(State, index); return new ZoneLuaReference(this, _api.Reference(State, Lua51Native.Registry), table);
            default: throw new NotSupportedException("Lua threads and full userdata cannot cross the Zone managed boundary.");
        }
    }

    private ZoneLuaValues ReadResults(int start)
    {
        var values = new object?[_api.GetTop(State) - start];
        try { for (var i = 0; i < values.Length; i++) values[i] = Read(start + i + 1); return new(values); }
        catch { foreach (var value in values) if (value is ZoneLuaReference reference) reference.Dispose(); throw; }
    }

    private void Push(object? value)
    {
        switch (value)
        {
            case null: _api.PushNil(State); break;
            case bool boolean: _api.PushBoolean(State, boolean ? 1 : 0); break;
            case string text:
                var bytes = Encoding.UTF8.GetBytes(text); _api.PushString(State, bytes, (nuint)bytes.Length); break;
            case byte[] binary: _api.PushString(State, binary, (nuint)binary.Length); break;
            case ZoneLuaHandle handle: _api.PushLightUserData(State, unchecked((nint)handle.Value)); break;
            case ZoneLuaReference reference:
                if (!ReferenceEquals(reference.Runtime, this)) throw new ArgumentException("Lua values cannot cross runtime instances.");
                ObjectDisposedException.ThrowIf(reference.IsDisposed, reference);
                _api.RawGetInteger(State, Lua51Native.Registry, reference.RegistryId); break;
            case sbyte or byte or short or ushort or int or uint or long:
                _api.PushInteger(State, Convert.ToInt64(value)); break;
            case ulong integer when integer <= long.MaxValue: _api.PushInteger(State, (long)integer); break;
            case float single: _api.PushNumber(State, single); break;
            case double number: _api.PushNumber(State, number); break;
            default: throw new ArgumentException($"Unsupported Lua value type: {value.GetType().Name}.", nameof(value));
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_callDepth != 0) throw new InvalidOperationException("A Lua runtime cannot close during an active script call.");
            Close(); GC.SuppressFinalize(this);
        }
    }

    private void Close()
    {
        if (_closing) return;
        _closing = true;
        if (_state != 0) { _api.Close(_state); _state = 0; }
        if (_library != 0) { NativeLibrary.Free(_library); _library = 0; }
        GC.KeepAlive(_callbacks); _callbacks.Clear();
    }

    object? IZoneLuaRuntime.Get(ZoneLuaReference table, object key, int? expectedType) => Get(table, key, expectedType);
    void IZoneLuaRuntime.Set(ZoneLuaReference table, object key, object? value) => Set(table, key, value);
    Vector3? IZoneLuaRuntime.GetVector(ZoneLuaReference table) => GetVector(table);
    ZoneLuaReference IZoneLuaRuntime.Retain(ZoneLuaReference reference) => Retain(reference);
    void IZoneLuaRuntime.Release(ZoneLuaReference reference) => Release(reference);
    T IZoneLuaRuntime.WithStack<T>(Func<T> operation) => WithStack(operation);

    ~ZoneLuaRuntime() => Close();
}

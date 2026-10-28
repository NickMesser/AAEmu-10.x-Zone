using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Text;
using Neo.IronLua;

namespace AAEmu.Zone.Scripting;

/// <summary>
/// Source-script execution without a native Lua library. Values retain the existing host
/// contract; Lua strings use a reversible byte representation inside the managed VM.
/// Native bytecode and remaining language differences are explicit compatibility work.
/// </summary>
public sealed partial class ZoneManagedLuaRuntime : IZoneLuaRuntime
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly object _gate = new();
    private readonly Lua _compiler = new(LuaIntegerType.Int64, LuaFloatType.Double);
    private readonly LuaTable _globals;
    private readonly LuaTable _invoke;
    private readonly Dictionary<int, object> _references = [];
    //A loadstring continuation can suspend inside Protect on its worker. Its stack must
    //not be the resumer's stack: the resumer completes a different protected call.
    private readonly ThreadLocal<Stack<ProtectedCall>> _protectedCalls = new(() => []);
    private sealed class ProtectedCall { public ZoneLuaException? HostFailure; }
    private readonly bool _refuseCaughtHostFailures;
    internal int ActiveProtectedCallCount => IsDisposed ? 0 : _protectedCalls.Value!.Count;
    private int _nextReference, _callDepth;
    [ThreadStatic] private static Stack<ZoneManagedLuaRuntime>? _callbackScopes;
    private bool IsCallbackThread => _callbackScopes is { Count: > 0 } scopes && ReferenceEquals(scopes.Peek(), this);
    public bool IsDisposed { get; private set; }
    public int RetainedReferenceCount { get { if (IsCallbackThread) return _references.Count; lock (_gate) return _references.Count; } }

    public ZoneManagedLuaRuntime(bool refuseCaughtHostFailures = false)
    {
        _refuseCaughtHostFailures = refuseCaughtHostFailures;
        try
        {
            // Copy the Lua libraries into a plain table: host file loading and CLR globals
            // are supplied only by explicit scene bindings. LuaGlobal is never the script env.
            var standard = _compiler.CreateEnvironment();
            _invoke = _compiler.CreateEnvironment();
            var libraries = standard.DoChunk("""
                return {
                  error=error, pcall=pcall, xpcall=xpcall, select=select,
                  next=next, pairs=pairs, ipairs=ipairs, rawget=rawget, rawset=rawset,
                  rawequal=rawequal, getmetatable=getmetatable, setmetatable=setmetatable,
                  type=type, tonumber=tonumber, tostring=tostring,
                  math={abs=math.abs,acos=math.acos,asin=math.asin,atan=math.atan,
                    ceil=math.ceil,cos=math.cos,cosh=math.cosh,deg=math.deg,exp=math.exp,
                    floor=math.floor,fmod=math.fmod,frexp=math.frexp,huge=math.huge,
                    ldexp=math.ldexp,log=math.log,log10=math.log10,max=math.max,min=math.min,
                    modf=math.modf,pi=math.pi,pow=math.pow,rad=math.rad,random=math.random,
                    randomseed=math.randomseed,sin=math.sin,sinh=math.sinh,sqrt=math.sqrt,
                    tan=math.tan,tanh=math.tanh},
                  string={byte=string.byte,char=string.char,dump=nil,find=string.find,
                    format=string.format,gmatch=string.gmatch,gsub=string.gsub,len=string.len,
                    lower=string.lower,match=string.match,rep=string.rep,reverse=string.reverse,
                    sub=string.sub,upper=string.upper},
                  table={concat=table.concat,insert=table.insert,maxn=table.maxn,
                    remove=table.remove,sort=table.sort,unpack=table.unpack,pack=table.pack},
                  coroutine={create=coroutine.create,resume=coroutine.resume,running=coroutine.running,
                    status=coroutine.status,wrap=coroutine.wrap,yield=coroutine.yield}
                }
                """, "=zone-managed-libraries");
            _globals = (LuaTable)libraries[0];
            _globals.SetValue("_G", _globals, true);
            _globals.SetValue("_VERSION", "Lua 5.1", true);
            _globals.SetValue("_LNUM", "double int64", true);
            _globals.SetValue("loadstring", new Func<object?[], LuaResult>(LoadString), true);
            _globals.SetValue("tostring", new Func<object?, string>(StringValue), true);
            _globals.SetValue("__zone_ascii_case", new Func<string, bool, string>(AsciiCase), true);
            var helpers = Run(CompileCore(Encoding.ASCII.GetBytes("""
                local error,select,rawget,type=error,select,rawget,type
                local function pack(...) return {n=select('#',...), ...} end
                function assert(value,...) if not value then error((...) or 'assertion failed!',2) end return value,... end
                function unpack(t,i,j) return table.unpack(t,i or 1,j or #t) end
                function table.getn(t) return #t end
                function table.maxn(t) local m=0; for k in pairs(t) do if type(k)=='number' and k>m then m=k end end return m end
                math.mod=math.fmod
                math.atan2=function(y,x) return math.atan(y,x) end
                local ascii=__zone_ascii_case; __zone_ascii_case=nil
                string.lower=function(s) return ascii(s,false) end
                string.upper=function(s) return ascii(s,true) end
                return function(f,...) return f(...) end,
                  function(t,k) return t[k] end,
                  function(t,k,v) t[k]=v end,
                  function(t)
                    local x=tonumber(rawget(t,'x')); local y,z
                    if x==nil then x=tonumber(rawget(t,1)); if x==nil then return end
                      y=tonumber(rawget(t,2));z=tonumber(rawget(t,3))
                    else y=tonumber(rawget(t,'y'));z=tonumber(rawget(t,'z')) end
                    if y~=nil and z~=nil then return x,y,z end
                  end
                """), "=zone-managed-host"));
            _invoke.SetValue("call", helpers[0], true);
            _invoke.SetValue("get", helpers[1], true);
            _invoke.SetValue("set", helpers[2], true);
            _invoke.SetValue("vector", helpers[3], true);
            InstallCoroutines();
        }
        catch { _protectedCalls.Dispose(); _compiler.Dispose(); throw; }
    }

    public ZoneLuaValues Execute(string source, string chunkName = "=zone")
    {
        ArgumentNullException.ThrowIfNull(source);
        using var function = Compile(Encoding.UTF8.GetBytes(source), chunkName);
        return Call(function);
    }

    public ZoneLuaReference Compile(ReadOnlyMemory<byte> source, string chunkName)
    {
        ArgumentNullException.ThrowIfNull(chunkName);
        return WithStack(() =>
        {
            var chunk = Protect(() => CompileCore(source.Span, chunkName));
            Func<object?[], LuaResult> function = arguments => Run(chunk, arguments);
            return Reference(function, false);
        });
    }

    private LuaChunk CompileCore(ReadOnlySpan<byte> source, string name)
    {
        if (source.StartsWith(new byte[] { 0x1b, (byte)'L', (byte)'u', (byte)'a' }))
            throw new ZoneLuaException("Managed Lua requires source; original Lua5.1 compiled chunks are not yet supported.");
        // Each VM char is one original byte, including invalid UTF8 in legacy comments
        // and binary string escapes. Host strings decode only when crossing the boundary.
        using var lexer = _compileOptions.CreateLexer(name, new StringReader(Encoding.Latin1.GetString(source)));
        return _compiler.CompileChunk(lexer, _compileOptions);
    }

    private readonly LuaCompileOptions _compileOptions = new ZoneLuaCompileOptions();

    private LuaResult Run(LuaChunk chunk, params object?[] arguments) => Protect(() => chunk.Run(_globals, arguments!));

    private LuaResult LoadString(object?[] arguments)
    {
        if (arguments.Length == 0 || arguments[0] is not string source)
            throw new LuaRuntimeException("loadstring requires a source string.", 1, true);
        try
        {
            var name = arguments.Length > 1 && arguments[1] is string label ? label : source;
            var chunk = CompileCore(Encoding.Latin1.GetBytes(source), name);
            return new LuaResult(new Func<object?[], LuaResult>(values => Run(chunk, values)));
        }
        catch (Exception error) when (error is LuaException or ZoneLuaException)
        { return new LuaResult(null, VmString(error.Message)); }
    }

    public ZoneLuaReference CreateTable() => WithStack(() => Reference(new LuaTable(), true));
    public object? GetGlobal(string name) => WithStack(() => Read(_globals.GetValue(VmString(name), true)));
    public void SetGlobal(string name, object? value) => WithStack(() => _globals.SetValue(VmString(name), Push(value), true));

    public ZoneLuaValues Call(ZoneLuaReference function, params object?[] arguments)
    {
        ArgumentNullException.ThrowIfNull(function); ArgumentNullException.ThrowIfNull(arguments);
        return WithStack(() => Results(InvokeCore(function, arguments)));
    }

    public void Invoke(ZoneLuaReference function, params object?[] arguments)
    {
        ArgumentNullException.ThrowIfNull(function); ArgumentNullException.ThrowIfNull(arguments);
        WithStack(() => { InvokeCore(function, arguments); return 0; });
    }

    private LuaResult InvokeCore(ZoneLuaReference function, object?[] arguments)
    {
        if (!function.IsFunction) throw new ArgumentException("A Lua function is required.", nameof(function));
        var target = Value(function);
        var values = new object?[arguments.Length];
        for (var i = 0; i < arguments.Length; i++) values[i] = Push(arguments[i]);
        // Invoke the retained function itself. A Lua vararg trampoline shifts method self
        // in this compiler profile. Invoke discards results without exporting their types.
        return Protect(() => new LuaResult(Lua.RtInvoke(target, values!)));
    }

    public ZoneLuaReference CreateFunction(Func<IReadOnlyList<object?>, object?[]> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return WithStack(() => Reference(new Func<object?[], LuaResult>(arguments =>
        {
            (_callbackScopes ??= new()).Push(this);
            try
            {
                return WithStack(() =>
                {
                    using var borrowed = Results(new LuaResult(arguments!));
                    var returned = callback(borrowed) ?? throw new InvalidOperationException("A Lua callback returned no result array.");
                    return new LuaResult(returned.Select(Push).ToArray()!);
                });
            }
            catch (Exception error)
            {
                while (error is TargetInvocationException { InnerException: { } inner }) error = inner;
                var failure = FindHostFailure(error) ?? error as ZoneLuaException ??
                    new ZoneLuaException(error.Message, error is not LuaException, error);
                if (failure.IsHostFailure) RecordHostFailure(failure);
                throw new LuaRuntimeException(failure.Message, failure);
            }
            finally { _callbackScopes.Pop(); }
        }), false));
    }

    private object? Get(ZoneLuaReference table, object key, int? expectedType) => WithStack(() =>
    {
        RequireTable(table);
        var value = Protect(() => _invoke.CallMemberDirect("get", [Value(table), Push(key)!], rawGet: true))[0];
        return expectedType is { } tag && Tag(value) != tag ? null : Read(value);
    });

    private void Set(ZoneLuaReference table, object key, object? value) => WithStack(() =>
    {
        RequireTable(table);
        return Protect(() => _invoke.CallMemberDirect("set", [Value(table), Push(key)!, Push(value)!], rawGet: true));
    });

    private Vector3? GetVector(ZoneLuaReference table) => WithStack<Vector3?>(() =>
    {
        RequireTable(table);
        var result = Protect(() => _invoke.CallMemberDirect("vector", [Value(table)], rawGet: true));
        return result.Count == 3 ? new(Convert.ToSingle(result[0], CultureInfo.InvariantCulture),
            Convert.ToSingle(result[1], CultureInfo.InvariantCulture), Convert.ToSingle(result[2], CultureInfo.InvariantCulture)) : null;
    });

    private ZoneLuaReference Reference(object value, bool table)
    {
        if (_nextReference == int.MaxValue) throw new InvalidOperationException("Managed Lua reference IDs are exhausted.");
        var id = ++_nextReference; _references.Add(id, value); return new(this, id, table);
    }
    private object Value(ZoneLuaReference reference)
    {
        if (!ReferenceEquals(reference.Runtime, this)) throw new ArgumentException("Lua values cannot cross runtime instances.");
        ObjectDisposedException.ThrowIf(reference.IsDisposed, reference);
        return _references.TryGetValue(reference.RegistryId, out var value) ? value : throw new ObjectDisposedException(nameof(ZoneLuaReference));
    }
    private void RequireTable(ZoneLuaReference table)
    { Value(table); if (!table.IsTable) throw new ArgumentException("A Lua table is required.", nameof(table)); }

    private ZoneLuaValues Results(LuaResult result)
    {
        var values = new object?[result.Count];
        try { for (var i = 0; i < values.Length; i++) values[i] = Read(result[i]); return new(values); }
        catch { foreach (var value in values) if (value is ZoneLuaReference reference) Release(reference); throw; }
    }
    private object? Read(object? value) => value switch
    {
        null or bool or ZoneLuaHandle => value,
        sbyte or byte or short or ushort or int or uint or long => Convert.ToInt64(value, CultureInfo.InvariantCulture),
        float number => ReadNumber(number), double number => ReadNumber(number),
        string text => HostString(text), LuaTable table => Reference(table, true),
        Delegate function => Reference(function, false),
        _ => throw new NotSupportedException("Lua threads and full userdata cannot cross the Zone managed boundary."),
    };
    private static object ReadNumber(double number)
    {
        if (double.IsFinite(number) && number >= long.MinValue &&
            number < 9223372036854775808d && number == Math.Truncate(number))
            return (long)number;
        return number;
    }

    private object? Push(object? value) => value switch
    {
        null or bool or ZoneLuaHandle => value,
        string text => VmString(text), byte[] bytes => Encoding.Latin1.GetString(bytes),
        ZoneLuaReference reference => Value(reference),
        sbyte or byte or short or ushort or int or uint or long => Convert.ToInt64(value, CultureInfo.InvariantCulture),
        ulong number when number <= long.MaxValue => (long)number,
        float number => (double)number, double => value,
        _ => throw new ArgumentException($"Unsupported Lua value type: {value.GetType().Name}.", nameof(value)),
    };
    private static int Tag(object? value) => value switch
    { null => 0, bool => 1, ZoneLuaHandle => 2, sbyte or byte or short or ushort or int or uint or long or float or double => 3,
        string => 4, LuaTable => 5, Delegate => 6, _ => -1 };
    private static string VmString(string value) => Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(value));
    private static object HostString(string value)
    {
        var bytes = Encoding.Latin1.GetBytes(value);
        try { return StrictUtf8.GetString(bytes); } catch (DecoderFallbackException) { return bytes; }
    }
    private static string StringValue(object? value) => value switch
    {
        null => "nil", bool flag => flag ? "true" : "false", string text => text,
        sbyte or byte or short or ushort or int or uint or long => Convert.ToString(value, CultureInfo.InvariantCulture)!,
        double number => number.ToString("G14", CultureInfo.InvariantCulture).Replace('E', 'e'),
        _ => value.ToString() ?? "nil",
    };
    private static string AsciiCase(string value, bool upper) => string.Create(value.Length, (value, upper),
        static (output, state) =>
        { for (var i = 0; i < output.Length; i++) { var c = state.value[i]; output[i] = state.upper && c is >= 'a' and <= 'z'
                ? (char)(c - 32) : !state.upper && c is >= 'A' and <= 'Z' ? (char)(c + 32) : c; } });

    private T WithStack<T>(Func<T> operation)
    {
        // NeoLua's cooperative coroutines use worker threads while their resumer waits.
        // A VM-invoked callback is part of the already serialized call; reacquiring its
        // resumer's monitor would deadlock. External callers still acquire the monitor.
        if (IsCallbackThread) return Enter();
        lock (_gate)
        {
            return Enter();
        }
        T Enter()
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this); _callDepth++;
            try { return operation(); } finally { _callDepth--; }
        }
    }
    private T Protect<T>(Func<T> operation)
    {
        var calls = _protectedCalls.Value!;
        var scope = new ProtectedCall(); calls.Push(scope);
        try
        {
            var result = operation();
            //An authored pcall may handle a script error. An unimplemented host effect
            //still refuses admission even when Lua catches its textual exception.
            if (_refuseCaughtHostFailures && scope.HostFailure is { } failure) throw new ZoneLuaException(failure.Message, true, failure);
            return result;
        }
        catch (LuaException error)
        {
            var failure = scope.HostFailure ?? FindHostFailure(error);
            throw new ZoneLuaException(failure?.Message ?? error.Message, failure is not null, failure ?? (Exception)error);
        }
        catch (TargetInvocationException error) when (error.InnerException is { } inner)
        {
            var failure = scope.HostFailure ?? FindHostFailure(inner);
            throw new ZoneLuaException(failure?.Message ?? InvocationMessage(inner), failure is not null, failure ?? inner);
        }
        finally
        {
            calls.Pop();
            if (scope.HostFailure is { } failure && calls.TryPeek(out var outer)) outer.HostFailure ??= failure;
        }
    }
    private void RecordHostFailure(ZoneLuaException failure)
    {
        if (!_refuseCaughtHostFailures) return;
        if (_protectedCalls.Value!.TryPeek(out var scope)) scope.HostFailure ??= failure;
        if (_runningCoroutine is { } coroutine && ReferenceEquals(coroutine.Owner, this)) coroutine.RecordHostFailure(failure);
    }
    private static ZoneLuaException? FindHostFailure(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
            if (current is ZoneLuaException { IsHostFailure: true } failure) return failure;
        return null;
    }
    private static string InvocationMessage(Exception error)
    {
        //Nested compiled Lua callbacks can introduce several reflection wrappers. Retain
        //the real service refusal before crossing the VM boundary instead of flattening
        //an outer TargetInvocationException into an unrecoverable generic message.
        while (error is TargetInvocationException { InnerException: { } inner }) error = inner;
        return error.Message;
    }
    private void Release(ZoneLuaReference reference)
    {
        if (IsCallbackThread) { ReleaseCore(); return; }
        lock (_gate) ReleaseCore();
        void ReleaseCore()
        {
            if (!ReferenceEquals(reference.Runtime, this)) throw new ArgumentException("Lua reference belongs to another runtime.");
            if (reference.RegistryId < 0) return;
            _references.Remove(reference.RegistryId); reference.Released();
        }
    }
    public void Dispose()
    {
        if (IsCallbackThread && _callDepth != 0)
            throw new InvalidOperationException("A Lua runtime cannot close during an active script call.");
        lock (_gate)
        {
            if (IsDisposed) return;
            if (_callDepth != 0) throw new InvalidOperationException("A Lua runtime cannot close during an active script call.");
            IsDisposed = true; RetireCoroutines(); _references.Clear(); _protectedCalls.Dispose(); _compiler.Dispose();
        }
    }
    object? IZoneLuaRuntime.Get(ZoneLuaReference table, object key, int? expectedType) => Get(table, key, expectedType);
    void IZoneLuaRuntime.Set(ZoneLuaReference table, object key, object? value) => Set(table, key, value);
    Vector3? IZoneLuaRuntime.GetVector(ZoneLuaReference table) => GetVector(table);
    ZoneLuaReference IZoneLuaRuntime.Retain(ZoneLuaReference reference) => WithStack(() => Reference(Value(reference), reference.IsTable));
    void IZoneLuaRuntime.Release(ZoneLuaReference reference) => Release(reference);
    T IZoneLuaRuntime.WithStack<T>(Func<T> operation) => WithStack(operation);
}

using System.Collections;

namespace AAEmu.Zone.Scripting;

/// <summary>Native script handle, distinct from a Lua number even when the bits are identical.</summary>
public readonly record struct ZoneLuaHandle(ulong Value);

/// <summary>Owned registry reference. Dispose it or the owning runtime to release the native value.</summary>
public sealed class ZoneLuaReference : IDisposable
{
    internal ZoneLuaRuntime Runtime { get; }
    internal int RegistryId { get; private set; }
    public bool IsTable { get; }
    public bool IsFunction => !IsTable;
    public bool IsDisposed => RegistryId < 0 || Runtime.IsDisposed;
    internal ZoneLuaReference(ZoneLuaRuntime runtime, int id, bool table)
    { Runtime = runtime; RegistryId = id; IsTable = table; }
    public ZoneLuaReference Retain() => Runtime.Retain(this);
    public object? Get(object key) => Runtime.Get(this, key);
    public void Set(object key, object? value) => Runtime.Set(this, key, value);
    public ZoneLuaValues Call(params object?[] arguments) => Runtime.Call(this, arguments);
    public void Dispose() => Runtime.Release(this);
    internal void Released() => RegistryId = -1;
}

/// <summary>
/// Call results, including owned table/function references. Disposing releases those references.
/// Lua strings decode as UTF-8 strings or, for invalid UTF-8, lossless byte arrays.
/// </summary>
public sealed class ZoneLuaValues : IReadOnlyList<object?>, IDisposable
{
    private readonly object?[] _values;
    internal ZoneLuaValues(object?[] values) => _values = values;
    public int Count => _values.Length;
    public object? this[int index] => _values[index];
    public IEnumerator<object?> GetEnumerator() => ((IEnumerable<object?>)_values).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public void Dispose() { foreach (var value in _values) if (value is ZoneLuaReference reference) reference.Dispose(); }
}

public sealed class ZoneLuaException(string message) : Exception(message);

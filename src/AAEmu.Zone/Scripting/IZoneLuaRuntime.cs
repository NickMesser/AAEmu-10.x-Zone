using System.Numerics;

namespace AAEmu.Zone.Scripting;

/// <summary>
/// Owned script values and protected calls, independent of a native VM library. References
/// belong to one exact runtime and callback arguments are borrowed until the call returns.
/// </summary>
public interface IZoneLuaRuntime : IDisposable
{
    bool IsDisposed { get; }
    ZoneLuaValues Execute(string source, string chunkName = "=zone");
    ZoneLuaReference Compile(ReadOnlyMemory<byte> source, string chunkName);
    ZoneLuaReference CreateTable();
    object? GetGlobal(string name);
    void SetGlobal(string name, object? value);
    ZoneLuaValues Call(ZoneLuaReference function, params object?[] arguments);
    void Invoke(ZoneLuaReference function, params object?[] arguments);
    ZoneLuaReference CreateFunction(Func<IReadOnlyList<object?>, object?[]> callback);

    // Typed Lua tags are the existing host contract: nil0,boolean1,handle2,number3,
    // string4,table5,function6. VM internals never escape through this boundary.
    internal object? Get(ZoneLuaReference table, object key, int? expectedType = null);
    internal void Set(ZoneLuaReference table, object key, object? value);
    internal Vector3? GetVector(ZoneLuaReference table);
    internal ZoneLuaReference Retain(ZoneLuaReference reference);
    internal void Release(ZoneLuaReference reference);
    internal T WithStack<T>(Func<T> operation);
}

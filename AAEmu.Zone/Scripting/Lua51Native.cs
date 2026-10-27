using System.Runtime.InteropServices;

namespace AAEmu.Zone.Scripting;

// The engine uses Lua 5.1 with the double+int64 patch. This ABI is not Lua 5.4/NLua.
internal sealed class Lua51Native
{
    internal const int Registry = -10000;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate nint NewStateFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void StateFn(nint state);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int StateIntFn(nint state);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int IndexIntFn(nint state, int index);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate long IndexLongFn(nint state, int index);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate double IndexDoubleFn(nint state, int index);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate nint IndexPointerFn(nint state, int index);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate nint StringFn(nint state, int index, out nuint size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void IndexFn(nint state, int index);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void IntegerFn(nint state, long value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void NumberFn(nint state, double value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void PointerFn(nint state, nint value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void BytesFn(nint state, byte[] bytes, nuint size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int LoadFn(nint state, byte[] bytes, nuint size, byte[] name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int CallFn(nint state, int arguments, int results, int errorHandler);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void PairFn(nint state, int first, int second);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int CFunction(nint state);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void ClosureFn(nint state, CFunction callback, int upvalues);

    internal readonly NewStateFn NewState;
    internal readonly StateFn Close, OpenLibraries, PushNil;
    internal readonly StateIntFn GetTop;
    internal readonly IndexIntFn Type, IsInteger, ToBoolean, Reference, CheckStack;
    internal readonly IndexLongFn ToInteger;
    internal readonly IndexDoubleFn ToNumber;
    internal readonly IndexPointerFn ToUserData;
    internal readonly StringFn ToByteString;
    internal readonly IndexFn SetTop, PushValue, PushBoolean;
    internal readonly IntegerFn PushInteger;
    internal readonly NumberFn PushNumber;
    internal readonly PointerFn PushLightUserData;
    internal readonly BytesFn PushString;
    internal readonly LoadFn LoadBuffer;
    internal readonly CallFn ProtectedCall;
    internal readonly PairFn CreateTable, RawGetInteger, Unreference;
    internal readonly ClosureFn PushClosure;

    internal Lua51Native(nint library)
    {
        T Bind<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
        NewState = Bind<NewStateFn>("luaL_newstate"); Close = Bind<StateFn>("lua_close");
        OpenLibraries = Bind<StateFn>("luaL_openlibs"); PushNil = Bind<StateFn>("lua_pushnil");
        GetTop = Bind<StateIntFn>("lua_gettop"); Type = Bind<IndexIntFn>("lua_type");
        IsInteger = Bind<IndexIntFn>("lua_isinteger"); ToBoolean = Bind<IndexIntFn>("lua_toboolean");
        Reference = Bind<IndexIntFn>("luaL_ref"); CheckStack = Bind<IndexIntFn>("lua_checkstack");
        ToInteger = Bind<IndexLongFn>("lua_tointeger"); ToNumber = Bind<IndexDoubleFn>("lua_tonumber");
        ToUserData = Bind<IndexPointerFn>("lua_touserdata");
        ToByteString = Bind<StringFn>("lua_tolstring"); SetTop = Bind<IndexFn>("lua_settop");
        PushValue = Bind<IndexFn>("lua_pushvalue"); PushBoolean = Bind<IndexFn>("lua_pushboolean");
        PushInteger = Bind<IntegerFn>("lua_pushinteger"); PushNumber = Bind<NumberFn>("lua_pushnumber");
        PushLightUserData = Bind<PointerFn>("lua_pushlightuserdata"); PushString = Bind<BytesFn>("lua_pushlstring");
        LoadBuffer = Bind<LoadFn>("luaL_loadbuffer"); ProtectedCall = Bind<CallFn>("lua_pcall");
        CreateTable = Bind<PairFn>("lua_createtable"); RawGetInteger = Bind<PairFn>("lua_rawgeti");
        Unreference = Bind<PairFn>("luaL_unref"); PushClosure = Bind<ClosureFn>("lua_pushcclosure");
    }
}

using System.Text;
using AAEmu.Zone.Scripting;

namespace AAEmu.Zone.Tests;

public sealed class ManagedLuaRuntimeTests
{
    [Test]
    public async Task DecimalLiteralsRoundOnceIncludingSubnormalsWithoutChangingStrings()
    {
        using var lua = new ZoneManagedLuaRuntime();
        using var values = lua.Execute("return .3, .1, 1.2345678901234567, 1e-100, 2.2250738585072014e-308, 5e-324, '.3 -- literal', 9007199254740993");
        double[] expected = [.3, .1, 1.2345678901234567, 1e-100, 2.2250738585072014e-308, 5e-324];
        for (var i = 0; i < expected.Length; i++)
            await Assert.That(BitConverter.DoubleToUInt64Bits((double)values[i]!)).IsEqualTo(BitConverter.DoubleToUInt64Bits(expected[i]));
        await Assert.That(values[6]).IsEqualTo(".3 -- literal");
        await Assert.That(values[7]).IsEqualTo(9007199254740993L);
    }

    [Test]
    public async Task NumbersRetainIntegerPrecisionFloorModuloAndOverflowPromotion()
    {
        using var lua = new ZoneManagedLuaRuntime();
        using var values = lua.Execute("""
            local m=9223372036854775807; local promoted=m+1
            local a=9007199254740993; local b=9007199254740992
            return a-b, 1/2, 1/1, -3%2, 3%-2,
              promoted>0, tostring(promoted), promoted-m
            """);
        await Assert.That(values[0]).IsEqualTo(1L);
        await Assert.That(values[1]).IsEqualTo(.5);
        await Assert.That(values[2]).IsEqualTo(1L);
        await Assert.That(values[3]).IsEqualTo(1L);
        await Assert.That(values[4]).IsEqualTo(-1L);
        await Assert.That((bool)values[5]!).IsTrue();
        await Assert.That(values[6]).IsEqualTo("9.2233720368548e+18");
        await Assert.That(values[7]).IsEqualTo(0L);
    }

    [Test]
    public async Task LnumLargeDecimalHexadecimalAndMinimumNegationKeepTheirDistinctConversions()
    {
        using var lua = new ZoneManagedLuaRuntime();
        using var values = lua.Execute("""
            local m=-9223372036854775807-1
            local h=0x8000000000000000
            return tostring(9223372036854775808),9223372036854775808>0,
              0xffffffffffffffff,-m>0,tostring(-m),h-m,h+1-m
            """);
        await Assert.That(values[0]).IsEqualTo("9.2233720368548e+18");
        await Assert.That((bool)values[1]!).IsTrue();
        await Assert.That(values[2]).IsEqualTo(-1L);
        await Assert.That((bool)values[3]!).IsTrue();
        await Assert.That(values[4]).IsEqualTo("9.2233720368548e+18");
        await Assert.That(values[5]).IsEqualTo(0L);
        await Assert.That(values[6]).IsEqualTo(1L);
    }

    [Test]
    public async Task NestedLiteralArithmeticPromotesBeforeOuterOperationsAndLargeDoublesRemainDoubles()
    {
        using var lua = new ZoneManagedLuaRuntime();
        using var values = lua.Execute("""
            return (9223372036854775807+1)-9223372036854775807,
              (9223372036854775807*2)/2, (9223372036854775807+1)>0,
              9223372036854775808, math.pow(2,63), 9223372036854775807
            """);
        await Assert.That(values[0]).IsEqualTo(0L);
        await Assert.That(values[1]).IsEqualTo(9223372036854775808d);
        await Assert.That((bool)values[2]!).IsTrue();
        await Assert.That(values[3]).IsEqualTo(9223372036854775808d);
        await Assert.That(values[4]).IsEqualTo(9223372036854775808d);
        await Assert.That(values[5]).IsEqualTo(long.MaxValue);
    }

    [Test]
    public async Task InvokeDiscardsResultsWithoutExportingUnsupportedThreadValues()
    {
        using var lua = new ZoneManagedLuaRuntime();
        using var returned = lua.Execute("return function() return coroutine.create(function() end) end");
        lua.Invoke((ZoneLuaReference)returned[0]!);
        await Assert.That(lua.LiveContinuationCount).IsEqualTo(0);
    }

    [Test]
    public async Task StringsPreserveUtf8AndBinaryBytesInsteadOfUtf16CharacterCounts()
    {
        using var lua = new ZoneManagedLuaRuntime();
        using var values = lua.Execute("return #'한글', '한글', string.char(0,255,128,65), string.lower('ABCé')");
        await Assert.That(values[0]).IsEqualTo(6L);
        await Assert.That(values[1]).IsEqualTo("한글");
        await Assert.That((byte[])values[2]!).IsEquivalentTo(new byte[] { 0, 255, 128, 65 });
        await Assert.That(values[3]).IsEqualTo("abcé");
        byte[] legacySource = [.. "-- old byte comment: "u8, 0xff, .. "\nreturn 19"u8];
        using var function = lua.Compile(legacySource, "=legacy-comment");
        using var executed = function.Call();
        await Assert.That(executed[0]).IsEqualTo(19L);
    }

    [Test]
    public async Task ReferencesRespectRuntimeIdentityMetatablesAndBorrowedCallbackLifetime()
    {
        using var lua = new ZoneManagedLuaRuntime();
        using var other = new ZoneManagedLuaRuntime();
        ZoneLuaReference? borrowed = null, retained = null;
        using var callback = lua.CreateFunction(arguments =>
        {
            borrowed = (ZoneLuaReference)arguments[0]!;
            retained = borrowed.Retain();
            return [borrowed.Get("answer"), null, 3L];
        });
        lua.SetGlobal("callback", callback);
        using var result = lua.Execute("return callback(setmetatable({},{__index=function(t,k) return 42 end}))");
        await Assert.That(result.Count).IsEqualTo(3);
        await Assert.That(result[0]).IsEqualTo(42L);
        await Assert.That(result[1]).IsNull();
        await Assert.That(borrowed!.IsDisposed).IsTrue();
        await Assert.That(retained!.Get("answer")).IsEqualTo(42L);
        await Assert.That(() => other.SetGlobal("foreign", retained)).Throws<ArgumentException>();
        retained.Dispose();
    }

    [Test]
    public async Task ManagedErrorsStayInsideProtectedLuaCallsAndLoadstringUsesTheSameEnvironment()
    {
        using var lua = new ZoneManagedLuaRuntime();
        using var callback = lua.CreateFunction(_ => throw new InvalidOperationException("owned callback refused"));
        lua.SetGlobal("callback", callback);
        using var values = lua.Execute("local ok,err=pcall(callback); local f=assert(loadstring('return globalValue')); globalValue=71; return ok,err,f(),assert(0)");
        await Assert.That((bool)values[0]!).IsFalse();
        await Assert.That(((string)values[1]!).Contains("owned callback refused", StringComparison.Ordinal)).IsTrue();
        await Assert.That(values[2]).IsEqualTo(71L);
        await Assert.That(values[3]).IsEqualTo(0L);
    }

    [Test]
    public async Task HostInvocationPreservesMethodSelfArgumentsAndItsGlobalEnvironment()
    {
        using var lua = new ZoneManagedLuaRuntime();
        using var loaded = lua.Execute("""
            npc={ActorTemplate={answer=41}}
            function npc:Create(name,source)
              _G[name]={answer=source.answer+1}
              return type(self),type(name),type(source),name,self==npc
            end
            """);
        using var npc = (ZoneLuaReference)lua.GetGlobal("npc")!;
        using var create = (ZoneLuaReference)npc.Get("Create")!;
        using var source = (ZoneLuaReference)npc.Get("ActorTemplate")!;
        using var result = lua.Call(create, npc, "created", source);
        await Assert.That(result[0]).IsEqualTo("table");
        await Assert.That(result[1]).IsEqualTo("string");
        await Assert.That(result[2]).IsEqualTo("table");
        await Assert.That(result[3]).IsEqualTo("created");
        await Assert.That((bool)result[4]!).IsTrue();
        using var created = (ZoneLuaReference)lua.GetGlobal("created")!;
        await Assert.That(created.Get("answer")).IsEqualTo(42L);
    }

    [Test]
    public async Task CoroutineCallbacksCanReadOwnedTablesAndRuntimeCannotCloseDuringACall()
    {
        using var lua = new ZoneManagedLuaRuntime();
        using var callback = lua.CreateFunction(arguments =>
        {
            using var table = lua.CreateTable(); table.Set("value", arguments[0]);
            try { lua.Dispose(); throw new InvalidOperationException("The VM closed inside its callback."); }
            catch (InvalidOperationException error) when (error.Message.Contains("active script call", StringComparison.Ordinal)) { }
            return [table.Get("value")];
        });
        lua.SetGlobal("callback", callback);
        using var values = lua.Execute("local c=coroutine.create(function() coroutine.yield(callback(17)); return callback(19) end); local a,b=coroutine.resume(c);local x,y=coroutine.resume(c);return a,b,x,y,coroutine.status(c)");
        await Assert.That((bool)values[0]!).IsTrue();
        await Assert.That(values[1]).IsEqualTo(17L);
        await Assert.That((bool)values[2]!).IsTrue();
        await Assert.That(values[3]).IsEqualTo(19L);
        await Assert.That(values[4]).IsEqualTo("dead");
        await Assert.That(lua.LiveContinuationCount).IsEqualTo(0);
    }

    [Test]
    public async Task CompletedContinuationsReleaseWorkersAndSuspendedContinuationsUnwindOnRetirement()
    {
        using var lua = new ZoneManagedLuaRuntime();
        var callbacks = 0;
        using var callback = lua.CreateFunction(_ => { callbacks++; return []; });
        lua.SetGlobal("callback", callback);
        using var finished = lua.Execute("for i=1,100 do local c=coroutine.create(function() return i end); assert(coroutine.resume(c)); assert(coroutine.status(c)=='dead') end");
        await Assert.That(lua.LiveContinuationCount).IsEqualTo(0);
        using var suspended = lua.Execute("c=coroutine.create(function() coroutine.yield(1); callback() end); return coroutine.resume(c)");
        await Assert.That((bool)suspended[0]!).IsTrue();
        await Assert.That(lua.LiveContinuationCount).IsEqualTo(1);
        lua.Dispose();
        await Assert.That(lua.LiveContinuationCount).IsEqualTo(0);
        await Assert.That(callbacks).IsEqualTo(0);
    }
}

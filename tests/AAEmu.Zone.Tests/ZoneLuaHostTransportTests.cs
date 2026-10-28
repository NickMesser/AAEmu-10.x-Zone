using AAEmu.Zone.Scripting;

namespace AAEmu.Zone.Tests;

public sealed class ZoneLuaHostTransportTests
{
    [Test]
    public async Task BorrowedMethodKeepsSelfItsClosureAndTheExactArguments()
    {
        using var lua = new ZoneManagedLuaRuntime();
        using var loaded = lua.Execute("""
            local captured=17
            actor={value=23}
            function actor:Call(name,source)
                _G[name]={sum=self.value+source.extra+captured}
                return self==actor,name,source.extra,captured
            end
            """);
        using var actor = (ZoneLuaReference)lua.GetGlobal("actor")!;
        using var method = (ZoneLuaReference)actor.Get("Call")!;
        using var source = lua.CreateTable(); source.Set("extra", 2);
        using var result = method.Call(actor, "createdByMethod", source);
        await Assert.That(result.Count).IsEqualTo(4);
        await Assert.That(result[0] is true).IsTrue();
        await Assert.That(result[1]).IsEqualTo("createdByMethod");
        await Assert.That(result[2]).IsEqualTo(2L);
        await Assert.That(result[3]).IsEqualTo(17L);
        using var created = (ZoneLuaReference)lua.GetGlobal("createdByMethod")!;
        await Assert.That(created.Get("sum")).IsEqualTo(42L);
    }

    [Test]
    public async Task VarargsKeepLeadingMiddleAndTrailingNilValues()
    {
        using var lua = new ZoneManagedLuaRuntime();
        using var defined = lua.Execute("return function(...) return select('#',...),... end");
        using var function = ((ZoneLuaReference)defined[0]!).Retain();
        using var result = lua.Call(function, new object?[] { null, "middle", null });
        await Assert.That(result.Count).IsEqualTo(4);
        await Assert.That(result[0]).IsEqualTo(3L);
        await Assert.That(result[1]).IsNull();
        await Assert.That(result[2]).IsEqualTo("middle");
        await Assert.That(result[3]).IsNull();
    }

    [Test]
    public async Task HostCallbackCanMakeReentrantProtectedCallsAndReturnBorrowedValues()
    {
        using var lua = new ZoneManagedLuaRuntime();
        using var defined = lua.Execute("return function(t,x) return t.value+x,nil end");
        using var function = ((ZoneLuaReference)defined[0]!).Retain();
        using var callback = lua.CreateFunction(arguments =>
        {
            using var result = lua.Call(function, arguments[0], arguments[1]);
            return [result[0], result[1], arguments[0]];
        });
        lua.SetGlobal("callback", callback);
        using var values = lua.Execute("local t={value=40};local answer,empty,original=callback(t,2);return answer,empty,original==t");
        await Assert.That(values.Count).IsEqualTo(3);
        await Assert.That(values[0]).IsEqualTo(42L);
        await Assert.That(values[1]).IsNull();
        await Assert.That(values[2] is true).IsTrue();
    }
}

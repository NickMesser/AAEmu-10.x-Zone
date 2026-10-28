using System.Numerics;
using AAEmu.Zone.Scene;
using AAEmu.Zone.Scripting;

namespace AAEmu.Zone.Tests;

public sealed class ManagedNpcProtectedCallbackTests
{
    [Test]
    public async Task OriginalProtectedLuaErrorKeepsTheEntityCallbackAndTimersAlive()
    {
        using var world = new ManagedNpcRuntimeTests.Fixture(withMovement: true);
        var npc = world.Admission.CreateFromWire(world.Model.Id, new Vector3(1100, 1100, 10.1f));
        var actor = world.Runtime.Actors.Find(world.Admission.Catalog.TryGet(npc)!.Value)!;
        actor.SetEntityActive(true); actor.SetEntityHidden(false);
        world.Runtime.Lua.SetGlobal("failUpdate", true);
        using var entity = actor.Entry.RetainScript()!; entity.Set("failUpdate", true);
        using (world.Runtime.Lua.Execute("System.Error=function() local actual='4'; return actual<=0 end")) { }
        world.Components.Effects.SetEntityTimer(actor, 99, 10_000);
        var errors = world.Runtime.TickScripts(TimeSpan.FromSeconds(.4), world.Admission.Provider.RemoveNpc);
        await Assert.That(errors.Count).IsEqualTo(1);
        await Assert.That(errors[0].RetireActor).IsFalse();
        await Assert.That(actor.HasActor).IsTrue();
        await Assert.That(world.Runtime.Count).IsEqualTo(1);
        await Assert.That(world.Components.Effects.TimerCount).IsEqualTo(1);
        await Assert.That(world.Components.Effects.ProtectedErrorCount).IsEqualTo(1ul);
        var repeated = world.Runtime.TickScripts(TimeSpan.FromSeconds(.4), world.Admission.Provider.RemoveNpc);
        await Assert.That(repeated.Count).IsEqualTo(0); //Repeat diagnostics bounded; the real callback still ran.
        await Assert.That(world.Components.Effects.ProtectedErrorCount).IsEqualTo(2ul);
        await Assert.That(world.Components.Effects.TimerCount).IsEqualTo(1);
        await Assert.That(actor.HasActor).IsTrue();
    }

    [Test]
    public async Task AnUnsupportedHostEffectStillRetiresItsExactActorEvenWhenLuaPcallCatchesTheText()
    {
        using var world = new ManagedNpcRuntimeTests.Fixture(withMovement: true);
        var npc = world.Admission.CreateFromWire(world.Model.Id, new Vector3(1100, 1100, 10.1f));
        var actor = world.Runtime.Actors.Find(world.Admission.Catalog.TryGet(npc)!.Value)!;
        actor.SetEntityActive(true); actor.SetEntityHidden(false);
        world.Runtime.Lua.SetGlobal("failUpdate", true);
        using var entity = actor.Entry.RetainScript()!; entity.Set("failUpdate", true);
        using var missing = world.Runtime.Lua.CreateFunction(_ => throw new NotSupportedException("Unowned physical effect control"));
        world.Runtime.Lua.SetGlobal("missingHostEffect", missing);
        using (world.Runtime.Lua.Execute("System.Error=function() local ok=pcall(missingHostEffect); observedCaught=not ok end")) { }
        var errors = world.Runtime.TickScripts(TimeSpan.FromSeconds(.4), world.Admission.Provider.RemoveNpc);
        await Assert.That(world.Runtime.Lua.GetGlobal("observedCaught")).IsEqualTo(true);
        await Assert.That(errors.Count).IsEqualTo(1);
        await Assert.That(errors[0].RetireActor).IsTrue();
        await Assert.That(errors[0].Message.Contains("Unowned physical effect control", StringComparison.Ordinal)).IsTrue();
        await Assert.That(world.Runtime.Count).IsEqualTo(0);
        await Assert.That(world.Bodies!.Count).IsEqualTo(0);
        await Assert.That(world.Components.Effects.ProtectedErrorCount).IsEqualTo(0ul);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task AYieldingCoroutineCannotHideAnUnsupportedHostEffectOrLeakItsProtectedScope(bool loaded)
    {
        using var lua = new ZoneManagedLuaRuntime(refuseCaughtHostFailures: true);
        using var host = lua.CreateFunction(_ => throw new NotSupportedException("Coroutine host effect unavailable"));
        lua.SetGlobal("hostEffect", host);
        var body = loaded ? "assert(loadstring('local ok=pcall(hostEffect); coroutine.yield(ok); return 7'))"
            : "function() local ok=pcall(hostEffect); coroutine.yield(ok); return 7 end";
        ZoneLuaException? failure = null;
        try { using var ignored = lua.Execute($"co=coroutine.create({body}); return coroutine.resume(co)"); }
        catch (ZoneLuaException error) { failure = error; }
        await Assert.That(failure is { IsHostFailure: true }).IsTrue();
        await Assert.That(failure!.Message).IsEqualTo("Coroutine host effect unavailable");
        await Assert.That(lua.ActiveProtectedCallCount).IsEqualTo(0);
        await Assert.That(lua.LiveContinuationCount).IsEqualTo(0);
        using var clean = lua.Execute("return coroutine.status(co),17");
        await Assert.That(clean[0]).IsEqualTo("dead"); await Assert.That(clean[1]).IsEqualTo(17L);
        await Assert.That(lua.ActiveProtectedCallCount).IsEqualTo(0);
    }

    [Test]
    public async Task NestedLoadstringCoroutinesTransferAHostFailureAndCancelBothContinuations()
    {
        using var lua = new ZoneManagedLuaRuntime(refuseCaughtHostFailures: true);
        using var host = lua.CreateFunction(_ => throw new NotSupportedException("Nested coroutine owner unavailable"));
        lua.SetGlobal("hostEffect", host);
        ZoneLuaException? failure = null;
        try
        {
            using var ignored = lua.Execute("""
                inner=coroutine.create(assert(loadstring('local ok=pcall(hostEffect);coroutine.yield(ok);return 7')))
                outer=coroutine.create(assert(loadstring('local ok,value=coroutine.resume(inner);coroutine.yield(ok,value);return 9')))
                return coroutine.resume(outer)
                """);
        }
        catch (ZoneLuaException error) { failure = error; }
        await Assert.That(failure is { IsHostFailure: true }).IsTrue();
        await Assert.That(lua.ActiveProtectedCallCount).IsEqualTo(0);
        await Assert.That(lua.LiveContinuationCount).IsEqualTo(0);
        using var clean = lua.Execute("return coroutine.status(inner),coroutine.status(outer),19");
        await Assert.That(clean[0]).IsEqualTo("dead"); await Assert.That(clean[1]).IsEqualTo("dead");
        await Assert.That(clean[2]).IsEqualTo(19L);
    }

    [Test]
    public async Task TypedHostFailureAndNativeScriptFailureKeepDistinctProvenanceAcrossNestedCalls()
    {
        using var lua = new ZoneManagedLuaRuntime(refuseCaughtHostFailures: true);
        using var host = lua.CreateFunction(_ => throw new NotSupportedException("Concrete owner unavailable"));
        lua.SetGlobal("hostEffect", host);
        ZoneLuaException? captured = null;
        try { using var ignored = lua.Execute("local function nested()return pcall(hostEffect)end; return nested()"); }
        catch (ZoneLuaException error) { captured = error; }
        await Assert.That(captured is { IsHostFailure: true }).IsTrue();
        await Assert.That(captured!.Message).IsEqualTo("Concrete owner unavailable");
        using var handled = lua.Execute("local ok=pcall(function() error('authored failure') end); return ok");
        await Assert.That(handled[0]).IsEqualTo(false);
        try { using var ignored = lua.Execute("local actual='4'; return actual<=0"); }
        catch (ZoneLuaException error) { captured = error; }
        await Assert.That(captured is { IsHostFailure: false }).IsTrue();
        using var next = lua.Execute("return 17");
        await Assert.That(next[0]).IsEqualTo(17L);
    }
}

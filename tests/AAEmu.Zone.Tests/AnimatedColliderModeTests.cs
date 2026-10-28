using System.Globalization;
using System.Reflection;
using System.Text.Json;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Scene;

namespace AAEmu.Zone.Tests;

public sealed class AnimatedColliderModeTests
{
    [Test]
    public async Task ArbitrationAndDefinedParameterBytesMatchExecutedOriginalControls()
    {
        using var input = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("AAEmu.Zone.Tests.Fixtures.npc-collider-native-controls.json")!;
        using var corpus = await JsonDocument.ParseAsync(input);
        var count = 0;
        foreach (var control in corpus.RootElement.GetProperty("controls").EnumerateArray())
        {
            var source = control.GetProperty("inputs");
            var body = new Body(source.GetProperty("body").GetBoolean() ? source.GetProperty("bodyType").GetInt32() : 0);
            using var actor = new ZoneAnimatedColliderController(() => true, body, 30, 8,
                () => source.GetProperty("spectatorCollisions").GetBoolean());
            // The corpus starts at copied native fields, then calls the arbiter directly.
            // Populate the same fields without issuing source Request's immediate updates.
            var requests = (int[])typeof(ZoneAnimatedColliderController).GetField("_requests", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(actor)!;
            var index = 0;
            foreach (var mode in source.GetProperty("layers").EnumerateArray()) requests[index++] = mode.GetInt32();
            Set("_selected", source.GetProperty("current").GetInt32());
            Set("_flags", source.GetProperty("state").GetInt32());
            Set("_appliedFlags", source.GetProperty("appliedState").GetInt32());
            Set("_force", source.GetProperty("force").GetBoolean());
            actor.SetFrame(source.GetProperty("frame").GetInt32());
            Set("_physicalizedFrame", source.GetProperty("physicalizedFrame").GetInt32());
            actor.ResetFrame(source.GetProperty("resetFrame").GetInt32());
            actor.Update();
            var state = actor.CopyState();
            await Assert.That(state.SelectedMode).IsEqualTo(control.GetProperty("selected").GetInt32());
            await Assert.That(state.AppliedFlags).IsEqualTo(control.GetProperty("appliedState").GetInt32());
            await Assert.That(state.ForceRebuild).IsEqualTo(control.GetProperty("force").GetBoolean());
            var calls = control.GetProperty("calls").EnumerateArray().ToArray();
            if (calls.Length == 0) await Assert.That(body.Updates.Count).IsEqualTo(0);
            else
            {
                await Assert.That(body.Updates.Count).IsEqualTo(1);
                var update = body.Updates[0];
                await Assert.That(update.ResetAction1).IsEqualTo(calls.Any(call => call.TryGetProperty("action", out _)));
                await Assert.That(update.RequiresNonPushableAuxiliary).IsEqualTo(calls.Any(call => call.TryGetProperty("createAuxiliary", out _)));
                foreach (var call in calls.Where(call => call.TryGetProperty("type", out _)))
                {
                    var type = call.GetProperty("type").GetInt32();
                    var block = type switch { 4 => update.Living, 6 => update.Part, 15 => update.EntityFlags, _ => update.Type26 };
                    foreach (var field in call.GetProperty("fields").EnumerateObject())
                    {
                        var offset = int.Parse(field.Name, CultureInfo.InvariantCulture);
                        var expected = field.Value.GetUInt32();
                        var actual = type == 26 && offset == 4 ? block.ReadByte(4) : block.ReadUInt32(offset);
                        if (actual != expected) throw new InvalidDataException($"{control.GetProperty("name").GetString()}: parameter {type}+0x{offset:X} expected {expected:X8}, received {actual:X8}.");
                        await Assert.That(actual).IsEqualTo((uint?)expected);
                    }
                }
            }
            count++;
            void Set(string field, object value) => typeof(ZoneAnimatedColliderController).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(actor, value);
        }
        await Assert.That(count).IsEqualTo(21);
    }

    [Test]
    public async Task RequestsRetainLayerPriorityReasonsAndBodylessSelection()
    {
        var body = new Body(0);
        using var actor = new ZoneAnimatedColliderController(() => true, body, 30, 8, () => false);
        await Assert.That(actor.Request(0, 3)).IsEqualTo(ZoneAnimatedColliderUpdateOutcome.UnchangedRequest);
        actor.SetFrame(10);
        await Assert.That(actor.Request(4, 3, "script")).IsEqualTo(ZoneAnimatedColliderUpdateOutcome.NoBody);
        await Assert.That(actor.CopyState().Reasons[3]).IsEqualTo("script");
        actor.Request(1, 1, "disable");
        await Assert.That(actor.CopyState().SelectedMode).IsEqualTo(1);
        actor.Request(7, 5);
        await Assert.That(actor.CopyState().SelectedMode).IsEqualTo(1);
        actor.Request(2, 6);
        await Assert.That(actor.CopyState().SelectedMode).IsEqualTo(2);
        await Assert.That(body.Updates.Count).IsEqualTo(0);
    }

    [Test]
    public async Task RefusedBodyConsumerCannotTurnAnIdenticalRetryIntoSuccessfulCachedSelection()
    {
        var lookups = 0;
        var body = new Body(4)
        {
            Lookup = () => lookups++,
            BeforeApply = () => throw new NotSupportedException("unported body effect"),
        };
        using var actor = new ZoneAnimatedColliderController(() => true, body, 30, 8, () => false);
        actor.SetFrame(10);
        await Assert.That(() => actor.Request(4, 3)).Throws<NotSupportedException>();
        await Assert.That(actor.CopyState().SelectedMode).IsEqualTo(4); //Original commit-before-body order survives.
        await Assert.That(actor.EffectRefusal).IsEqualTo("unported body effect");
        await Assert.That(() => actor.Request(4, 3)).Throws<NotSupportedException>();
        await Assert.That(() => actor.Request(1, 3)).Throws<NotSupportedException>();
        await Assert.That(lookups).IsEqualTo(1);
        await Assert.That(body.Updates.Count).IsEqualTo(0);
    }

    [Test]
    public async Task RetiredOwnerAndBodyLookupCallbacksCannotWriteAReplacement()
    {
        var live = true;
        var body = new Body(4) { Lookup = () => live = false };
        using var actor = new ZoneAnimatedColliderController(() => live, body, 30, 8, () => false);
        await Assert.That(() => actor.Request(1, 3)).Throws<ObjectDisposedException>();
        await Assert.That(body.Updates.Count).IsEqualTo(0);
        await Assert.That(() => actor.SetFlags(4)).Throws<ObjectDisposedException>();
    }

    private sealed class Body(int type) : IZoneAnimatedColliderBodyEffects
    {
        public Action? Lookup;
        public Action? BeforeApply;
        public int PhysicalEntityType { get { Lookup?.Invoke(); return type; } }
        public List<ZoneAnimatedColliderPhysicsUpdate> Updates { get; } = [];
        public void Apply(ZoneAnimatedColliderPhysicsUpdate update) { BeforeApply?.Invoke(); Updates.Add(update); }
    }
}

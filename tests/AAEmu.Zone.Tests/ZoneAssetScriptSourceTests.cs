using AAEmu.Zone.Content;
using AAEmu.Zone.Scripting;

namespace AAEmu.Zone.Tests;

public sealed class ZoneAssetScriptSourceTests
{
    [Test]
    public async Task AssetScriptsUseGamePathsAndPreserveOriginalBytesAndStreamOwnership()
    {
        var assets = new Assets();
        var scripts = new ZoneAssetScriptSource(assets);
        var first = scripts.Read("SCRIPTS\\AI\\Original.lua")!;
        var second = scripts.Read("game/scripts/ai/original.lua")!;
        await Assert.That(first.Filename).IsEqualTo("game/scripts/ai/original.lua");
        await Assert.That(first.Bytes.ToArray()).IsEquivalentTo(new byte[] { 0xff, 0, 65 });
        await Assert.That(second.Bytes.ToArray()).IsEquivalentTo(first.Bytes.ToArray());
        await Assert.That(assets.Closed).IsEqualTo(2);
        await Assert.That(scripts.Read("scripts/absent.lua")).IsNull();
        await Assert.That(() => scripts.Read("scripts/../outside.lua")).Throws<ArgumentException>();
        await Assert.That(() => scripts.Read("game/worlds/main_world/world.xml")).Throws<IOException>();
    }

    private sealed class Assets : IZoneAssetSource
    {
        public int Closed;
        public Stream? OpenRead(string virtualPath) => virtualPath == "game/scripts/ai/original.lua"
            ? new OwnedStream(this) : null;
        private sealed class OwnedStream(Assets owner) : MemoryStream(new byte[] { 0xff, 0, 65 }, false)
        {
            protected override void Dispose(bool disposing) { if (disposing) owner.Closed++; base.Dispose(disposing); }
        }
    }
}

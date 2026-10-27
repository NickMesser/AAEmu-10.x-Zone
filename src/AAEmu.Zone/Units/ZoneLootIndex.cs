using System.Collections.Frozen;

using AAEmu.Zone.Content;

namespace AAEmu.Zone.Units;

/// <summary>Native 39C55750 NPC-to-pack lookup after World content exclusions.</summary>
public sealed class ZoneLootIndex
{
    public ZoneLootContent Source { get; }
    public IReadOnlyDictionary<uint, IReadOnlyList<ZoneLootPack>> NpcPacks { get; }

    public ZoneLootIndex(ZoneLootContent source, IReadOnlySet<uint>? excludedPackIds = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        Source = source;
        var index = new Dictionary<uint, IReadOnlyList<ZoneLootPack>>();
        foreach (var (npcId, packIds) in source.NpcPackIds)
        {
            var packs = packIds.Where(id => excludedPackIds?.Contains(unchecked((uint)id)) != true)
                .Select(id => source.Packs.GetValueOrDefault(id)).OfType<ZoneLootPack>().ToArray();
            if (packs.Length != 0) index.Add(npcId, Array.AsReadOnly(packs));
        }
        NpcPacks = index.ToFrozenDictionary();
    }

    public IReadOnlyList<ZoneLootPack> PacksForNpc(uint npcId) => NpcPacks.GetValueOrDefault(npcId) ?? [];
}

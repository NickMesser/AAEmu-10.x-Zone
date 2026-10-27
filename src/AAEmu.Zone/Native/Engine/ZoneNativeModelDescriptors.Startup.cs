using System.Runtime.InteropServices;

using AAEmu.Zone.Content;

namespace AAEmu.Zone.Native.Engine;

internal sealed partial class ZoneNativeModelDescriptors
{
    internal void ValidateAiStartupModels()
    {
        // Exact3100A8C4 search keys, proved against the configured models/actors/stances.
        //31050598 does not null-check actor or stance pointers. Validate only records the
        // actual search can reach; missing/rejected rows and its first-success stop remain native.
        // LookupModel still rejects resolved unsupported subtypes: their complete descriptor
        // implementation is required before this provider may expose them, even as candidates.
        foreach (uint modelId in new uint[] { 10, 16, 20, 18 })
        {
            var model = LookupModel(modelId);
            if (model == 0 || (ZoneModelKind)Marshal.ReadInt32(model + 0x3c) != ZoneModelKind.Actor) continue;
            var actor = Marshal.ReadIntPtr(model + 0x40);
            if (actor == 0) throw new InvalidDataException($"AI startup model {modelId} has no resolved actor.");
            var begin = Marshal.ReadIntPtr(actor + 0x140);
            var end = Marshal.ReadIntPtr(actor + 0x148);
            if (begin == end) continue; // Native rejects an empty vector and tries the next model.
            if (begin == 0 || end < begin || (end - begin) % 8 != 0)
                throw new InvalidDataException($"AI startup model {modelId} has an invalid stance vector.");
            nint selected = 0;
            for (var cursor = begin; cursor < end; cursor += 8)
            {
                selected = Marshal.ReadIntPtr(cursor);
                if (selected == 0) throw new InvalidDataException($"AI startup model {modelId} has an unresolved stance.");
                if (Marshal.ReadInt32(selected + 0x48) == 2) break;
            }
            // First stance_id2 wins; without one, the real leaf uses the last physical row.
            if ((Marshal.ReadByte(selected + 0x58) & 0x02) != 0) return;
        }
    }
}

using AAEmu.Zone.Content;

namespace AAEmu.Zone.Native.Engine;

internal sealed unsafe partial class ZoneNativeModelDescriptors
{
    internal ZoneNativeModelDescriptors(ZoneModelContent content, ZoneModelAuxiliaryContent auxiliary) : this(content)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(auxiliary);
            BuildModelBindings(auxiliary);
            BuildSlaveExtendModelLinks(auxiliary);
            CompleteDescriptorConstruction();
        }
        catch { Dispose(); throw; }
    }

    private void BuildModelBindings(ZoneModelAuxiliaryContent content)
    {
        // Native model+50 points to the0x28 payload after the multimap's owner key at node+1C.
        // It does not point to an attribute modifier or require a native tree-node allocation.
        var storage = AllocateRows(content.BindingRows.Count, 0x28);
        var linked = new Dictionary<uint, List<nint>>();
        for (var index = 0; index < content.BindingRows.Count; index++)
        {
            var row = content.BindingRows[index];
            var pointer = storage + checked(index * 0x28);
            WriteUInt32(pointer, 0x00, row.PassengerCheckingAttachPointId);
            WriteUInt32(pointer, 0x04, row.AttachPointId);
            WriteSingle(pointer, 0x08, row.BreathZ);
            WriteSingle(pointer, 0x0c, row.OffsetX);
            WriteSingle(pointer, 0x10, row.OffsetY);
            WriteSingle(pointer, 0x14, row.OffsetZ);
            WriteSingle(pointer, 0x18, row.PassengerOffsetX);
            WriteSingle(pointer, 0x1c, row.PassengerOffsetY);
            WriteSingle(pointer, 0x20, row.PassengerOffsetZ);
            WriteFlag(pointer, 0x24, 0x01, row.CameraOffset);
            WriteFlag(pointer, 0x24, 0x02, row.HeelHeightEnable);
            WriteFlag(pointer, 0x24, 0x04, row.HorseRein);
            WriteFlag(pointer, 0x24, 0x08, row.NotCollideWithCamera);
            if (!ZoneModelAuxiliaryContent.IsModelOwner(row.OwnerType) || !_models.ContainsKey(row.OwnerId)) continue;
            if (!linked.TryGetValue(row.OwnerId, out var pointers)) linked.Add(row.OwnerId, pointers = []);
            pointers.Add(pointer); // Equal owner keys and all physical duplicates retain insertion order.
        }
        foreach (var pair in linked) WritePointerVector(_models[pair.Key], 0x50, pair.Value);
    }

    private void BuildSlaveExtendModelLinks(ZoneModelAuxiliaryContent content)
    {
        //39AD8620 creates actual4-byte ID-only parent descriptors; its SQL never reads name.
        var parents = new Dictionary<uint, nint>();
        var parentStorage = AllocateRows(content.SlaveExtendModelRows.Count, 4);
        for (var index = 0; index < content.SlaveExtendModelRows.Count; index++)
        {
            var row = content.SlaveExtendModelRows[index];
            var pointer = parentStorage + checked(index * 4);
            WriteUInt32(pointer, 0, row.Id);
            parents[row.Id] = pointer;
        }
        var storage = AllocateRows(content.SlaveExtendModelLinkRows.Count, 0x48);
        var linked = new Dictionary<uint, List<nint>>();
        for (var index = 0; index < content.SlaveExtendModelLinkRows.Count; index++)
        {
            var row = content.SlaveExtendModelLinkRows[index];
            var pointer = storage + checked(index * 0x48);
            WriteUInt32(pointer, 0x00, row.Id);
            WritePointer(pointer, 0x08, StoreString(row.Damaged25));
            WritePointer(pointer, 0x10, StoreString(row.Damaged50));
            WritePointer(pointer, 0x18, StoreString(row.Damaged75));
            WritePointer(pointer, 0x20, StoreString(row.Dead));
            WritePointer(pointer, 0x28, StoreString(row.Dying));
            WriteUInt32(pointer, 0x30, row.ModelId);
            WritePointer(pointer, 0x38, StoreString(row.Normal));
            WritePointer(pointer, 0x40, parents.GetValueOrDefault(row.SlaveExtendModelId));
            //39ADC040 appends every physical link whose model resolves, irrespective of link-ID
            // duplicates or a missing extension parent. The latter remains an actual null pointer.
            if (!_models.ContainsKey(row.ModelId)) continue;
            if (!linked.TryGetValue(row.ModelId, out var pointers)) linked.Add(row.ModelId, pointers = []);
            pointers.Add(pointer);
        }
        foreach (var pair in linked) WritePointerVector(_models[pair.Key], 0x70, pair.Value);
    }
}

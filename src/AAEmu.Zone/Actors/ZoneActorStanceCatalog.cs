using System.Numerics;
using AAEmu.Zone.Content;

namespace AAEmu.Zone.Actors;

/// <summary>One installed actor stance. A missing authored row retains the native constructor fields.</summary>
public sealed record ZoneActorStanceDefinition(
    ZoneActorPhysicalizationStance Physicalization, ZoneGameStanceDefinition? AuthoredRow)
{
    public float NormalSpeed => AuthoredRow?.NormalSpeed ?? 0;
}

/// <summary>
/// Copied stance slots installed by CActor 392DF300. Content stance IDs 1 through 9 fill slots
/// 0 through 8 in source order; the last duplicate wins. The default is then copied from slot 1.
/// </summary>
public sealed class ZoneActorStanceCatalog
{
    public const int SlotCount = 9;
    public const int InitialSlot = 1;
    public const int UnchangedRequest = -1;
    private readonly ZoneActorStanceDefinition[] _slots;

    public ZoneActorStanceCatalog(ZoneActorModel model, float entityScale)
    {
        ArgumentNullException.ThrowIfNull(model);
        // 392E6BC0 initializes nine slots. Missing rows do not scale this constructor shape.
        var constructor = new ZoneActorStanceDefinition(
            new(new(.5f), default, default, 0, 0, 0, false, false), null);
        _slots = Enumerable.Repeat(constructor, SlotCount).ToArray();
        foreach (var row in model.Stances)
        {
            // ID zero writes the native default before slot 1 overwrites it. Other IDs outside
            // this range never populate a slot; retaining those rows in content is independent.
            if (row.StanceId is < 1 or > SlotCount) continue;
            _slots[row.StanceId - 1] = new(
                new(new(row.SizeX * entityScale, row.SizeY * entityScale, row.SizeZ * entityScale),
                    new(row.ViewOffsetX * entityScale, row.ViewOffsetY * entityScale, row.ViewOffsetZ * entityScale),
                    new(row.ModelOffsetX * entityScale, row.ModelOffsetY * entityScale, row.ModelOffsetZ * entityScale),
                    row.HeightCollider * entityScale, row.HeightPivot * entityScale, row.MaxSpeed,
                    row.UseCapsule, row.IgnoreCollision), row);
        }
        Default = _slots[InitialSlot];
    }

    public ZoneActorStanceDefinition Default { get; }

    /// <summary>
    /// Resolves an actor slot, not a content ID. Out-of-range slots use the copied default in
    /// 392D8890. Slot 9 is a named refusal: the original bound admits memory beyond the nine
    /// initialized slots, and there is no authored stance to decode there. Request -1 means
    /// unchanged at the movement caller and must be handled before invoking this selector.
    /// </summary>
    public ZoneActorStanceDefinition ForSlot(int slot)
    {
        if (slot == SlotCount)
            throw new NotSupportedException("Actor stance slot 9 has no initialized native stance storage.");
        return (uint)slot < SlotCount ? _slots[slot] : Default;
    }
}

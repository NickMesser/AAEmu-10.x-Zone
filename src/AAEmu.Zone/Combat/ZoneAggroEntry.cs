using System.Numerics;

namespace AAEmu.Zone.Combat;

public readonly record struct ZoneAggroAmounts(uint Damage, uint Healing, uint Direct)
{
    public uint Total => unchecked(Damage + Healing + Direct);
    public uint this[int component] => component switch
    {
        0 => Damage, 1 => Healing, 2 => Direct, _ => throw new ArgumentOutOfRangeException(nameof(component))
    };

    public ZoneAggroAmounts Add(ZoneAggroAmounts other) =>
        new(unchecked(Damage + other.Damage), unchecked(Healing + other.Healing), unchecked(Direct + other.Direct));

    // 39321340/39323F40: unsigned input -> f32 multiply -> truncate to i64 -> retain low 32 bits.
    public ZoneAggroAmounts Scale(float multiplier) =>
        new(ScaleValue(Damage, multiplier), ScaleValue(Healing, multiplier), ScaleValue(Direct, multiplier));
    private static uint ScaleValue(uint value, float multiplier) => unchecked((uint)(long)((float)value * multiplier));
}

/// <summary>The defined fields of the native 64-byte NpcAggro entry; padding is not state.</summary>
public sealed class ZoneAggroEntry
{
    public uint TargetId { get; internal set; }
    public ZoneAggroAmounts Amounts { get; internal set; }
    public byte Flags { get; internal set; }
    public float IncomingMultiplier { get; internal set; }
    public float DistanceRatio { get; internal set; }
    public uint TauntedAt { get; internal set; }
    public Vector3 OriginAtCreation { get; internal set; }
    public Vector3 TargetPositionAtCreation { get; internal set; }
    public uint CreatedAt { get; internal set; }
    public uint UpdatedAt { get; internal set; }
    public uint WeightedTotal => Amounts.Scale(IncomingMultiplier).Total;

    public float ScoreAt(uint now)
    {
        // 39322950: sum BEFORE scaling, and decay from creation rather than the last update.
        var score = (float)Amounts.Total * IncomingMultiplier;
        var elapsed = unchecked(now - CreatedAt);
        return elapsed > 1000 ? score / ((float)elapsed / 1000f) : score;
    }

    internal ZoneAggroEntry Copy() => (ZoneAggroEntry)MemberwiseClone();
}

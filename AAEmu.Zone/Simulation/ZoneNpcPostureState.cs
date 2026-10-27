using AAEmu.Zone.Content;

namespace AAEmu.Zone.Simulation;

public interface IZoneNpcPostureModel
{
    ZoneModelKind Kind { get; }
    uint CurrentAnimationId { get; } // ActorModel virtual +2D0, first posture word.
}

/// <summary>Current actor models, configured posture rows and engine time required by the manager.</summary>
public interface IZoneNpcPostureServices
{
    float Hour { get; }
    // This is the NPC owner's stable allocation key, distinct from unit IDs and its update phase.
    ulong GetOwnerAllocationKey(ZoneNpc npc);
    IZoneNpcPostureModel? GetCurrentModel(ZoneNpc npc);
    // Null means the posture set is absent. An existing empty set still causes a clock read.
    IReadOnlyList<ZoneNpcPosture>? FindPostures(uint postureSetId);
}

/// <summary>
/// NpcManager's retained hour and 16-pass posture fan-out, 39347390/3933E6E0/3933BF30.
/// Run after the ordinary NPC owner pass. The actor receives OnPostureChanged through its real
/// signal queue; the AI behavior owns the resulting posture change.
/// </summary>
public sealed class ZoneNpcPostureState : IDisposable
{
    private readonly ZoneNpcSimulation _npcs;
    private readonly IZoneNpcPostureServices _services;
    private bool _updating;
    public int LastHour { get; private set; } = -2;
    public uint PassCount { get; private set; }
    public bool IsDisposed { get; private set; }

    public ZoneNpcPostureState(ZoneNpcSimulation npcs, IZoneNpcPostureServices services)
    {
        _npcs = npcs ?? throw new ArgumentNullException(nameof(npcs));
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    public void Update(uint frame)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (_npcs.IsStopped) return;
        if (_updating) throw new InvalidOperationException("NPC posture updates cannot run recursively.");
        _updating = true;
        try
        {
            var sampled = _services.Hour;
            if (!IsActive) return;
            var hour = TruncateHour(sampled);
            if (LastHour != hour)
            {
                if (PassCount < 16 && LastHour != -2)
                {
                    if (LastHour <= hour)
                    {
                        PassCount++;
                        UpdateOwners(frame);
                        return;
                    }
                    hour = unchecked(hour - 1);
                }
                PassCount = 0;
                LastHour = hour;
            }
            if (PassCount != 0) UpdateOwners(frame);
        }
        finally { _updating = false; }
    }

    private bool IsActive => !IsDisposed && !_npcs.IsStopped;
    private bool IsCurrent(ZoneNpc npc) => IsActive && ReferenceEquals(_npcs.FindByObjectId(npc.ObjectId), npc);

    private void UpdateOwners(uint frame)
    {
        foreach (var npc in _npcs.Npcs.OrderBy(pair => pair.Key).Select(pair => pair.Value).ToArray())
        {
            if (!IsActive) return;
            if (!IsCurrent(npc)) continue;
            var allocation = _services.GetOwnerAllocationKey(npc);
            if (!IsCurrent(npc) || (allocation & 15) != (frame & 15)) continue;
            var model = _services.GetCurrentModel(npc);
            if (!IsCurrent(npc) || model is null) continue;
            var kind = model.Kind;
            if (!IsCurrent(npc) || kind != ZoneModelKind.Actor) continue;
            var postures = _services.FindPostures(npc.Template.NpcPostureSetId);
            if (!IsCurrent(npc) || postures is null) continue;
            var hour = _services.Hour;
            if (!IsCurrent(npc)) continue;
            var desired = Select(postures, hour);
            if (desired is null || desired.AnimationId == 0) continue; // Native invalid animation 3A638B60.
            var current = model.CurrentAnimationId;
            if (IsCurrent(npc) && current != desired.AnimationId) npc.AiSignals.Send("OnPostureChanged");
        }
    }

    /// <summary>390B3440 selects the greatest integer start strictly below the hour and above -1.</summary>
    public static ZoneNpcPosture? Select(IReadOnlyList<ZoneNpcPosture> postures, float hour)
    {
        ArgumentNullException.ThrowIfNull(postures);
        ZoneNpcPosture? selected = null;
        var greatest = -1;
        foreach (var posture in postures)
            if (posture.StartHour < hour && greatest < posture.StartHour)
            {
                selected = posture;
                greatest = posture.StartHour;
            }
        return selected;
    }

    private static int TruncateHour(float hour) => !float.IsFinite(hour) || hour >= 2147483648f || hour < -2147483648f
        ? int.MinValue : (int)hour;

    // Managed retirement policy; the caller owns the services and the NPC simulation.
    public void Dispose() => IsDisposed = true;
}

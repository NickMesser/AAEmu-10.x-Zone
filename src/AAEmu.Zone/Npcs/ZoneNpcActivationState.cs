using System.Numerics;

using AAEmu.Zone.Ai;
using AAEmu.Zone.Units;

namespace AAEmu.Zone.Npcs;

/// <summary>
/// Required services bound to one NPC owner generation. Entity and AI handles must retain their
/// identity across callbacks; a missing entity or non-actor AI is distinct from a missing service.
/// </summary>
public interface IZoneNpcActivationServices
{
    bool HasUnit { get; }
    float PlayerSightRange { get; } // player_unit_sight_range, native default 120.
    float PlayerSightRangeBig { get; } // player_unit_sight_range_big, native default 250.
    Vector3 GetPosition(); // Live UnitModel position, 392589F0.
    IZoneNpcActivationEntity? GetEntity();
    bool IsDedicated { get; }
    bool GlobalAiUpdateAlways { get; }
}

public interface IZoneNpcActivationEntity
{
    void Hide(bool hidden);
    void Activate(bool active);
    IZoneNpcActivationAi? GetAiObject();
}

public interface IZoneNpcActivationAi
{
    // Null means this AI object does not implement IAIActor. An actual actor must own a proxy.
    ZoneActorActivationState? GetActorActivation();
    // Native IAIObject::Event receives event 6 (inactive) or 7 (active), with null event data.
    void SendActivationEvent(int eventId);
}

/// <summary>
/// NPC proximity and activation fields from 39345000, updated by 3933BEA0/3933C020.
/// The scene owns this state for one NPC generation and disposes it when that owner is removed.
/// Disposal retiring callback work is managed ownership policy; native destruction frees the owner.
/// Scheduling remains in ZoneNpcUpdateSchedule: proximity precedes activation every eight frames,
/// including dead NPCs, after the entity/AI actor gate. Neither operation samples a clock.
/// </summary>
public sealed class ZoneNpcActivationState(bool modelUsesLargeSightRange) : IDisposable
{
    public const float ProximityStopDistance = 29.9f; // 39FDFC54, bits 0x41EF3333.
    // Native caches models.big (model descriptor +90 bit1) at owner construction, not NPC scale.
    public bool ModelUsesLargeSightRange { get; } = modelUsesLargeSightRange;
    public bool IsActive { get; private set; } = true;
    public bool IsNearPlayer { get; private set; }
    public float PlayerDistance { get; private set; } = float.MaxValue;
    public bool IsDisposed { get; private set; }

    public void UpdateProximity(IZoneNpcActivationServices scene, ZoneUnitSpatialIndex spatialIndex)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(spatialIndex);
        // Both cvars are read, in this order, even though only one value is selected.
        var normal = scene.PlayerSightRange;
        if (IsDisposed) return;
        var big = scene.PlayerSightRangeBig;
        if (IsDisposed) return;
        var radius = ModelUsesLargeSightRange ? big : normal;
        var position = scene.GetPosition();
        if (IsDisposed) return;
        var distance = spatialIndex.QueryDistance(ZoneSpatialCategory.Character, position, ProximityStopDistance, radius);
        SetProximity(distance, radius, scene);
    }

    /// <summary>3933B780 stores both fields before notifying the current actor proxy, even for repeated distances.</summary>
    public void SetProximity(float distance, float radius, IZoneNpcActivationServices scene)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        ArgumentNullException.ThrowIfNull(scene);
        PlayerDistance = distance;
        IsNearPlayer = distance < radius;
        var hasUnit = scene.HasUnit;
        if (IsDisposed || !hasUnit) return;
        var entity = scene.GetEntity();
        if (IsDisposed || entity is null) return;
        var ai = entity.GetAiObject();
        if (IsDisposed || ai is null) return;
        var actor = ai.GetActorActivation();
        if (!IsDisposed) actor?.SetPlayerDistance(distance);
    }

    public void UpdateActivation(IZoneNpcActivationServices scene)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        ArgumentNullException.ThrowIfNull(scene);
        var desired = true;
        if (!IsNearPlayer)
        {
            var hasUnit = scene.HasUnit;
            if (IsDisposed) return;
            if (hasUnit)
            {
                var entity = scene.GetEntity();
                if (IsDisposed) return;
                if (entity is not null)
                {
                    var ai = entity.GetAiObject();
                    if (IsDisposed) return;
                    if (ai is not null)
                    {
                        var actor = ai.GetActorActivation();
                        if (IsDisposed) return;
                        if (actor is not null && !actor.AlwaysUpdate)
                        {
                            desired = scene.GlobalAiUpdateAlways;
                            if (IsDisposed) return;
                        }
                    }
                }
            }
        }
        SetActive(desired, scene);
    }

    /// <summary>
    /// 3933B580: unchanged values do nothing. Hide, Activate, a fresh AI lookup and its event occur
    /// before the cached flag is written. A missing unit retains the flag; a missing entity does not.
    /// </summary>
    public void SetActive(bool active, IZoneNpcActivationServices scene)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        ArgumentNullException.ThrowIfNull(scene);
        if (IsActive == active) return;
        var hasUnit = scene.HasUnit;
        if (IsDisposed || !hasUnit) return;
        var entity = scene.GetEntity();
        if (IsDisposed) return;
        if (entity is not null)
        {
            var dedicated = scene.IsDedicated;
            if (IsDisposed) return;
            if (!dedicated)
            {
                entity.Hide(!active);
                if (IsDisposed) return;
            }
            entity.Activate(active);
            if (IsDisposed) return;
            // Activate can replace the AI object; native deliberately queries it afterward.
            var ai = entity.GetAiObject();
            if (IsDisposed) return;
            ai?.SendActivationEvent(active ? 7 : 6);
            if (IsDisposed) return;
        }
        IsActive = active;
    }

    public void Dispose() => IsDisposed = true;
}

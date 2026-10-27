namespace AAEmu.Zone.Ai;

/// <summary>
/// The single time authority for AI update scheduling: the scene's frame owner pumps one
/// non-wrapping engine clock into the scene's <see cref="IZoneAiProvider"/> so per-generation
/// periods registered through <see cref="IZoneAiProvider.SetUpdateSchedule"/> become due work.
///
/// Discipline follows <c>ZoneUnitUpdateScheduler</c> (Units/ZoneUnitUpdateScheduler.cs):
/// the caller supplies absolute milliseconds sampled by the scene's single clock
/// (<c>IZoneUnitUpdateServices.TimeMilliseconds</c>, Units/ZoneUnitUpdateScheduler.cs:10),
/// all comparisons use unchecked wrapping subtraction, and the provider itself never samples a
/// time source — restated on <see cref="IZoneAiProvider.SetUpdateSchedule"/>.
///
/// This is a new contract file added under the contract-folder extension allowance; Agent 5
/// owns the scene-frame wiring that calls <see cref="Pump"/> (recorded in
/// AAEmu.Zone/IMPLEMENTATION_OWNERSHIP.md, change log 2026-09-22).
/// </summary>
public interface IZoneAiFramePump
{
    /// <summary>
    /// Advances AI update scheduling to <paramref name="timeMilliseconds"/>: every bound
    /// generation whose period is armed, active and due receives its update callback exactly
    /// once per pump, and its next deadline advances from this pump's timestamp. Cancelled or
    /// retired generations are never visited; a generation removed during the pump skips its
    /// remaining cadence instead of touching a dead binding.
    /// </summary>
    void Pump(uint timeMilliseconds);
}

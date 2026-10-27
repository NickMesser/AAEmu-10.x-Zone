namespace AAEmu.Zone.Simulation.Contracts;

/// <summary>
/// Physics backends selectable at scene creation.
/// <list type="bullet">
/// <item><see cref="NativeTerrain"/> — the current adapter over <c>ZoneNativeTerrainPhysicsWorld</c>
/// (Simulation/ZoneNativeTerrainPhysicsWorld.cs), reached only through that adapter type.</item>
/// <item><see cref="Managed"/> — the OWNED backend (<c>ZoneOwnedPhysicsBackend</c>, Agent 8): the
/// host becomes the movement authority, integrating the player and resolving the ground through
/// the scene's real native terrain raycast (<c>ZoneNativeGroundQuery</c>). It exists because the
/// admitted player's native body cannot be moved from here (its <c>body+0x47</c> is 0x03, so the
/// recovered action defers), and refusing forever is not a product. It still refuses what it cannot
/// honestly answer - shape sweeps and overlaps stay named refusals.</item>
/// </list>
/// </summary>
public enum ZonePhysicsBackendKind
{
    NativeTerrain = 0,
    Managed = 1,
}

/// <summary>
/// AI backends selectable at scene creation.
/// <list type="bullet">
/// <item><see cref="Native"/> — the temporary native AI provider (the embedded engine's AI behind
/// its adapter); the production path today.</item>
/// <item><see cref="Managed"/> — the unfinished managed provider, preserved for later replacement
/// (TODO(managed-ai)). It must not run for native-driven NPCs; see <see cref="IZoneAiProvider"/>
/// for the exclusivity rules.</item>
/// </list>
/// </summary>
public enum ZoneAiBackendKind
{
    Native = 0,
    Managed = 1,
}

/// <summary>
/// Explicit backend selection at scene creation: a small immutable record consumed by scene
/// creation code, following the selection-parameter precedent of <c>ZoneSceneActivation</c>'s
/// <c>ZoneNativeWorldLayout selection</c> (Simulation/ZoneSceneActivation.cs).
///
/// The selection happens once, before the scene owner is constructed, and is fixed for the
/// scene's lifetime — the record enforces the scene invariants stated on
/// <see cref="IZoneActorLifecycle"/>: one scene owner, hence exactly one physics backend and one
/// AI provider instance per scene generation, chosen here and never swapped mid-scene.
///
/// TODO(scene-creation): Program.cs, ZoneSceneCoordinator and ZoneSceneRunner are the consumers
/// that must pass this record; they are owned by a separate change and are untouched by this
/// contract layer. Until that wiring lands, nothing in this record is yet read.
/// </summary>
public sealed record ZoneSceneBackendSelection
{
    public ZonePhysicsBackendKind Physics { get; init; } = ZonePhysicsBackendKind.NativeTerrain;
    public ZoneAiBackendKind Ai { get; init; } = ZoneAiBackendKind.Native;

    /// <summary>Today's configuration: native terrain physics adapter plus the temporary native AI provider.</summary>
    public static ZoneSceneBackendSelection Default { get; } = new();
}

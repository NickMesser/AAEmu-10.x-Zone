using AAEmu.Zone.Scene;
using AAEmu.Zone.WorldLink;

namespace AAEmu.Zone.Hosting;

/// <summary>
/// Runs one real loaded scene and its accepted World session on a dedicated simulation owner.
/// The factories supply actual level/actor services; this runner supplies no readiness shortcuts.
/// The executable can compose this path once its required production providers are available.
/// </summary>
public static class ZoneSceneRunner
{
    /// <param name="loadScene">
    /// Synchronize and completely load a new scene on the supplied owner. A failure before return
    /// retains factory cleanup responsibility. Returning a scene owned elsewhere transfers nothing.
    /// </param>
    /// <param name="adoptSession">Adopt the exact returned scene with the configured relationship/content services.</param>
    /// <param name="framePeriod">Explicit replacement frame policy, not a claimed recovered native rate.</param>
    public static Task RunAsync(
        Func<ZoneSimulationThread, CancellationToken, Task<ZoneSceneCoordinator>> loadScene,
        Func<ZoneSceneCoordinator, ZoneSession> adoptSession,
        TimeSpan framePeriod, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(loadScene);
        ArgumentNullException.ThrowIfNull(adoptSession);
        return ZoneSimulationThread.RunAsync(async (owner, stop) =>
        {
            using var frames = new PeriodicZoneFrameSource(framePeriod);
            ZoneSceneCoordinator? scene = null;
            Exception? failure = null;
            try
            {
                var loaded = await loadScene(owner, stop) ??
                    throw new InvalidOperationException("The Zone loading factory returned no scene.");
                owner.VerifyAccess();
                if (!ReferenceEquals(loaded.Owner, owner))
                    throw new InvalidOperationException("The loaded scene belongs to a different simulation owner.");
                scene = loaded;
                scene.EnsureCompleted();
                stop.ThrowIfCancellationRequested();
                var session = adoptSession(scene) ??
                    throw new InvalidOperationException("The Zone session factory returned no session.");
                if (!session.BelongsToScene(scene))
                    throw new InvalidOperationException("The session must adopt the exact loaded scene.");
                // The scene's simulation tick is what selects the frame-aware branch in
                // ZoneSession.ReceiveScheduledAsync (:264-274): without it the session falls back to
                // the bare Registry.Npcs.Update and the ONE global frame (PrePhase -> StepPhysics ->
                // PostPhase -> Pump) never runs - which is why the physics backend was constructed
                // but never stepped. The coordinator is passed to AdoptScene already; the tick is
                // what makes it reachable.
                // NO ConfigureAwait(false) here: the finally below retires the scene on the
                // simulation owner thread, and letting the continuation hop threads makes
                // ZoneSceneCoordinator.Dispose throw "This operation belongs to the Zone simulation
                // thread" (the SceneRunner tests pin exactly that).
                await session.ReceiveAsync(stop, frames, scene.Simulation);
            }
            catch (Exception error) { failure = error; throw; }
            finally
            {
                // Receive drains both producers before returning, including on cancellation.
                // The owner keeps this continuation alive until all native retirement finishes.
                try { scene?.Dispose(); }
                catch (Exception cleanup) when (failure is not null)
                { throw new AggregateException("Zone scene execution and cleanup failed.", failure, cleanup); }
            }
        }, cancellationToken);
    }
}

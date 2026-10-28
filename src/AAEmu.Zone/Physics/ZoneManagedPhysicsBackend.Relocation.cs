using System.Numerics;
using AAEmu.Zone.Actors;

namespace AAEmu.Zone.Physics;

public sealed partial class ZoneManagedPhysicsBackend
{
    private bool _npcRelocationActive;
    internal IDisposable EnterNpcRelocation()
    {
        if (_npcRelocationActive || _sceneStepActive || _drainingLivingCommands || OwnedPhysics.IsStepping)
            throw new InvalidOperationException("NPC relocation cannot reenter a relocation, scene step or living command drain.");
        _npcRelocationActive = true;
        return new RelocationBoundary(this);
    }
    private sealed class RelocationBoundary(ZoneManagedPhysicsBackend owner) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        { if (_disposed) return; _disposed = true; owner._npcRelocationActive = false; }
    }

    /// <summary>
    /// NPC39269040: consume earlier body requests, action_reset4(1), action10(zero
    /// velocity), then the flags0 entity translation. This is not a motion request,
    /// correction sweep, native-world thread scheduler, or a simulation/time advance.
    /// </summary>
    internal (int FlushedCommands, bool TranslationChanged) RelocateLivingBody(
        ZoneActorHandle actor, Vector3 previousEntityPosition, Vector3 destination)
    {
        Require(actor);
        if (_sceneStepActive || _drainingLivingCommands || OwnedPhysics.IsStepping)
            throw new InvalidOperationException("NPC relocation cannot reenter a scene step or living command drain.");
        var description = _actors[actor];
        if (description.Profile != ZonePhysicsActorProfile.Living || description.LivingMovement is null ||
            !_livingControls.ContainsKey(actor))
            throw new NotSupportedException("NPC relocation actions4/10 require an owned authored living physical entity.");
        ZoneManagedTerrainGeometry.ValidateFinite(previousEntityPosition, nameof(previousEntityPosition));
        ValidateDestination(description);
        //A queued accepted stance precedes the reset. Check every possible applied collider
        //center before flushing, so a finite destination cannot overflow after that update.
        foreach (var pending in _pendingLivingStances)
            if (pending.Actor == actor) ValidateDestination(DescriptionForStance(description, pending.Stance));

        //32012940 updates all translation components only when at least one exceeds this
        //strict per-component threshold. Earlier queued physical poses do not change that
        //entity comparison; the scene supplied its pre-operation entity/model position.
        var changed = MathF.Abs(previousEntityPosition.X - destination.X) > .0001f ||
            MathF.Abs(previousEntityPosition.Y - destination.Y) > .0001f ||
            MathF.Abs(previousEntityPosition.Z - destination.Z) > .0001f;
        var flushed = DrainLivingCommands(actor);
        Require(actor);
        if (_pendingLivingCommands.Exists(command => command.Actor == actor))
            throw new InvalidOperationException("A living query callback added body commands during relocation's ordered flush.");
        var control = _livingControls[actor];
        OwnedPhysics.TryReadPosition(actor, out var current);
        OwnedPhysics.TryReadOrientation(actor, out var orientation);
        //All source-preserved fields deliberately survive:564 suspension acceleration,
        //608 impulse timer,572 floor/484 normal cache,952 other bits and1120 one-shot override.
        //Action10 sets bit2 but is not an action_move mode3 request.
        OwnedPhysics.Teleport(actor, changed ? destination : current, orientation);
        control.Requested = Vector3.Zero;
        control.SuspensionOffset = 0;
        control.SuspensionVelocity = 0;
        control.SuspensionTime = 1;
        control.Flag2 = true;
        _livingPrior.Remove(actor);
        _livingNextControls.Remove(actor);
        _lastRecovery.Remove(actor);
        return (flushed, changed);

        void ValidateDestination(ZonePhysicsActorDescription candidate)
        {
            ZoneManagedTerrainGeometry.ValidateFinite(destination, nameof(destination));
            ZoneManagedTerrainGeometry.ValidateFinite(destination + new Vector3(0, 0, candidate.CenterOffsetZ), nameof(destination));
        }
    }
}

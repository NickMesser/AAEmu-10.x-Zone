using AAEmu.Zone.Actors;
using System.Numerics;

namespace AAEmu.Zone.Physics;

public sealed partial class ZoneManagedPhysicsBackend
{
    //35023200/35023650 put parameter and action requests in the same native queue.
    //Keep the copied requests in call order, including the pose portion of a C0 correction.
    //The existing per-kind collections describe pending state, not a separate drain order.
    private enum LivingCommandKind { Velocity, Stance, Correction, Orientation }
    private sealed class PendingLivingCommand(ZoneActorHandle actor, LivingCommandKind kind, Action apply,
        Quaternion? orientation = null)
    {
        internal ZoneActorHandle Actor { get; } = actor;
        internal LivingCommandKind Kind { get; } = kind;
        internal Action Apply { get; } = apply;
        internal Quaternion? Orientation { get; } = orientation;
    }

    private readonly List<PendingLivingCommand> _pendingLivingCommands = [];
    private bool _drainingLivingCommands;
    private bool _sceneStepActive;

    private void QueueLivingCommand(ZoneActorHandle actor, LivingCommandKind kind, Action apply,
        Quaternion? orientation = null) => _pendingLivingCommands.Add(new(actor, kind, apply, orientation));

    private int DrainLivingCommands(ZoneActorHandle? onlyActor = null)
    {
        if (_drainingLivingCommands)
            throw new InvalidOperationException("Living body commands cannot reenter their ordered drain.");
        _drainingLivingCommands = true;
        var applied = 0;
        try
        {
            //Requests created by a query callback belong to the next drain. A retirement can
            //remove commands from this snapshot; it cannot redirect them to a fresh generation.
            var pending = _pendingLivingCommands.Where(command => onlyActor is null || command.Actor == onlyActor).ToArray();
            foreach (var command in pending)
            {
                if (!_pendingLivingCommands.Contains(command)) continue;
                if (_actors.ContainsKey(command.Actor))
                {
                    command.Apply();
                    applied++;
                }
                _pendingLivingCommands.Remove(command);
            }
        }
        finally { _drainingLivingCommands = false; }
        return applied;
    }

    private void RetireLivingCommands(ZoneActorHandle actor) =>
        _pendingLivingCommands.RemoveAll(command => command.Actor == actor);
}

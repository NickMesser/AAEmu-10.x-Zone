using AAEmu.Zone.Actors;

namespace AAEmu.Zone.Ai;

/// <summary>One actor's pipe services and the stable shared states used by its executable goals and output phase.</summary>
public interface IZoneActorGoalPhaseServices : IZoneGoalPipeActorServices
{
    ZoneActorControlState Controls { get; }
    ZoneActorNavigationState Navigation { get; }
    ZoneActorContinuationState Continuation { get; }
}

/// <summary>
/// Owns an actor's pipe controller and runs its goal/output/continuation phase using the actor's shared state.
/// The enclosing scene schedules this phase separately from Lua proxy draining, path decisions and locomotion.
/// That scene must dispose this owner when removing its NPC actor; releasing Lua script state alone does not do so.
/// </summary>
public sealed class ZoneActorGoalPhase : IDisposable
{
    private bool _updating;
    public bool IsDisposed { get; private set; }
    /// <summary>Borrowed controller; the phase owns its selected pipes, active goals and signal waits.</summary>
    public ZoneGoalPipeController Pipes { get; }
    public ZoneActorControlState Controls { get; }
    public ZoneActorNavigationState Navigation { get; }
    public ZoneActorContinuationState Continuation { get; }

    public ZoneActorGoalPhase(ZoneGoalPipeCatalog catalog, IZoneActorGoalPhaseServices actor,
        ZoneGoalPipeEventDispatcher events)
    {
        ArgumentNullException.ThrowIfNull(actor);
        Controls = actor.Controls ?? throw new ArgumentException("The actor must supply goal controls.", nameof(actor));
        Navigation = actor.Navigation ?? throw new ArgumentException("The actor must supply navigation state.", nameof(actor));
        Continuation = actor.Continuation ?? throw new ArgumentException("The actor must supply continuation state.", nameof(actor));
        if ((actor is IZoneActorControlGoalServices controlGoals && !ReferenceEquals(controlGoals.Controls, Controls)) ||
            (actor is IZoneLookAtGoalServices lookGoals && !ReferenceEquals(lookGoals.LookState.Controls, Controls)))
            throw new ArgumentException("Goal execution and output must share the actor's control state.", nameof(actor));
        Pipes = new(catalog, actor, events);
    }

    /// <summary>311614B4: finish goal execution before replacing movement output and applying KeepMoving.</summary>
    public void Update()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (_updating) throw new InvalidOperationException("An actor goal phase cannot update recursively.");
        _updating = true;
        try
        {
            Pipes.Update();
            // Goal callbacks may remove their actor. Its active operations remain leased until execution returns.
            if (IsDisposed) return;
            Navigation.RefreshMovementOutput(); // 31161AE9 -> 31142FF0, after active-goal retirement.
            Continuation.UpdateMovementDirection(Controls); // 31161AFE..31161C33.
        }
        finally { _updating = false; }
    }

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        Pipes.Dispose();
    }
}

using System.Numerics;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Physics;

namespace AAEmu.Zone.Scene;

/// <summary>Copied physical operation; entity, Unit, spatial and AI publication belongs to the scene callback.</summary>
public sealed record ZoneManagedNpcRelocationResult(ZoneActorHandle Actor, uint ModelId, float EntityScale,
    Vector3 RequestedPosition, ZoneManagedNpcBodySnapshot Previous, ZoneManagedNpcBodySnapshot Current,
    int FlushedCommands, bool TranslationChanged);

/// <summary>
/// Generation-owned NPC living relocation. Borrows the scene's existing physics owner and
/// never advances it, writes an AI pose, or invents a replacement physical class.
/// </summary>
public sealed class ZoneManagedNpcRelocation(ZoneManagedNpcPhysics bodies)
{
    private readonly ZoneManagedNpcPhysics _bodies = bodies ?? throw new ArgumentNullException(nameof(bodies));
    private bool _relocating;

    /// <summary>
    /// The caller first preflights its Unit/spatial/model/AI and replication owners. Earlier
    /// exact-body commands flush in order, then actions4/10 and the entity translation commit.
    /// The callback runs once after those physical effects, inside the nonreentrant operation.
    /// It must publish the copied Current pose through the scene's sole prepared publisher.
    /// Callback failure or retirement reports failure; committed native-ordered effects are
    /// not rolled back, and no later physical/publication write follows a retired generation.
    /// </summary>
    public ZoneManagedNpcRelocationResult Relocate(ZoneManagedNpcActor actor, Vector3 localDestination,
        Action<ZoneManagedNpcRelocationResult> publishModelAndUnit)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(publishModelAndUnit);
        if (_relocating) throw new InvalidOperationException("NPC relocation cannot reenter its publication boundary.");
        _relocating = true;
        try
        {
            using var boundary = _bodies.Backend.EnterNpcRelocation();
            RequireActor();
            var body = _bodies.Find(actor.Handle) ??
                throw new NotSupportedException("NPC relocation requires this exact generation's actual living body owner.");
            if (!ReferenceEquals(body.Npc, actor.Npc))
                throw new InvalidOperationException("NPC relocation body belongs to a different Unit owner.");
            var previous = body.CopyState();
            var unit = actor.Npc.Unit;
            if (unit.Snapshot.ModelId != previous.ModelId || unit.Scale.BaseScale != previous.EntityScale ||
                unit.Scale.Multiplier != 1 || unit.Snapshot.Attachment.Point != -1 || unit.Snapshot.Bonding.Point != -1)
                throw new NotSupportedException("NPC relocation requires its admitted unparented model and unchanged applied scale.");
            RequireActor();
            var operation = body.Relocate(previous.Status.Position, localDestination);
            var current = body.CopyState();
            RequireActor();
            var result = new ZoneManagedNpcRelocationResult(actor.Handle, current.ModelId, current.EntityScale,
                localDestination, previous, current, operation.FlushedCommands, operation.TranslationChanged);
            publishModelAndUnit(result);
            RequireActor();
            if (!body.IsCurrent)
                throw new ObjectDisposedException(nameof(ZoneManagedNpcBodyLease), "The NPC body retired during relocation publication.");
            return result;
        }
        finally { _relocating = false; }

        void RequireActor() => actor.Scene.Require(actor);
    }
}

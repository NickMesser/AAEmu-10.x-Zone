using System.Runtime.InteropServices;

namespace AAEmu.Zone.Simulation;

public sealed partial class ZoneNativeEngine
{
    internal void RequireCellEntityPhysicsCaptureState(ZoneNativeEntityServices owner)
    {
        if (!_operationActive || !ReferenceEquals(_entityBorrower?.Resource, owner))
            throw new InvalidOperationException("Cell physics requires the current entity borrower and shared engine operation.");
        ValidatePhysicsState(RequiredModule("cryphysics_dedicate.dll"));
        RequireStaticPhysicsCompletionState();
        // Native iterator is intentionally lock-free; the serialized loading profile excludes
        // every writer. Purge also owns +57F0 itself, so never hold it across native entry.
        foreach (var offset in new[] { 0x57c0, 0x57c8, 0x57e4, 0x57f0 })
            if (Marshal.ReadInt32(_physicsWorld + offset) != 0)
                throw new InvalidOperationException($"Native entity-physics guard +0x{offset:X} is busy.");
    }

    internal int CompleteCellEntityLoadingPhysics(ZoneNativeEntityServices owner, ZoneNativeEntityServices.CellEntityBatch batch)
    {
        RequireCellEntityPhysicsCaptureState(owner);
        var before = owner.BeginCellPhysicsCompletionInterval(batch);
        return CompleteLoadingPhysicsCore(() => CaptureConcreteCellPhysicsOwners(owner),
            () => owner.EndCellPhysicsCompletionInterval(batch, before));
    }

    internal void RequireCellEntityPhysicsRequestsEmpty(ZoneNativeEntityServices owner)
    {
        RequireCellEntityPhysicsCaptureState(owner);
        if (CaptureStaticPhysicsRequests(CaptureConcreteCellPhysicsOwners(owner)).Count != 0)
            throw new InvalidOperationException("Native entity shutdown left physical requests which retain cell resources.");
    }

    private HashSet<nint> CaptureConcreteCellPhysicsOwners(ZoneNativeEntityServices owner)
    {
        RequireCellEntityPhysicsCaptureState(owner);
        var bodies = owner.CaptureCellPhysicsCompletionBodies();
        if (_staticObjectBorrower?.Resource is { } statics)
            foreach (var pointer in statics.CapturePhysicsCompletionBodies())
                if (!bodies.Add(pointer))
                    throw new InvalidOperationException("A physics allocation has both a cell entity and static-resource owner.");
        return bodies;
    }
}

using System.Runtime.InteropServices;

namespace AAEmu.Zone.Simulation;

public sealed partial class ZoneNativeStaticObjectServices
{
    // Called only by the owning engine while its shared operation gate is held. These are
    // live full static bodies whose foreign nodes and model/group dependencies remain rooted
    // here while the engine completes their queued native construction requests.
    internal HashSet<nint> CapturePhysicsCompletionBodies()
    {
        _lease.RequireActiveOperation();
        RequireIdleVegetationWorld();
        var bodies = new HashSet<nint>();
        foreach (var brush in _brushes)
        {
            if (brush.Retired || brush.PhysicalPointer == 0) continue;
            ValidateBrush(brush);
            ValidateBrushBody(brush);
            RequireStaticCompletionClass(brush.PhysicalPointer);
            Require(bodies.Add(brush.PhysicalPointer), "Two static resources claim the same native physical body.");
        }
        foreach (var vegetation in _vegetation)
        {
            if (vegetation.Retired || vegetation.PhysicalPointer == 0) continue;
            ValidateVegetation(vegetation);
            ValidateVegetationBody(vegetation);
            RequireStaticCompletionClass(vegetation.PhysicalPointer);
            Require(bodies.Add(vegetation.PhysicalPointer), "Two static resources claim the same native physical body.");
        }
        AppendImportedVisibilityPhysicsBodies(bodies);
        return bodies;
    }

    private static void RequireStaticCompletionClass(nint body)
    {
        if (Marshal.ReadByte(body + 0x47) != 0)
            throw new NotSupportedException("Loading completion currently admits only class-zero native static bodies.");
    }
}

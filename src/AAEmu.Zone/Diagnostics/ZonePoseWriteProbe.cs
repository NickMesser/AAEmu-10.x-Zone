using System.Numerics;

using AAEmu.Zone.Actors;
using AAEmu.Zone.Content;
using AAEmu.Zone.Native.Entities;
using AAEmu.Zone.Native.Physics;

namespace AAEmu.Zone.Diagnostics;

/// <summary>
/// Retained CLI compatibility for an obsolete pose probe. Original3502A300 action type1 writes
/// velocity and reads24 bytes; the former position-labelled20-byte action is never submitted.
/// The separately scoped scene living oracle now owns verified diagnostic pose/velocity controls.
/// </summary>
internal static class ZonePoseWriteProbe
{
    internal readonly record struct Hit(int Type, int PayloadSize, int MarkerOffset, string Detail);
    internal static string EvidencePath { get; set; } = string.Empty;

    internal static IReadOnlyList<Hit> Run(ZoneNativeTerrainPhysicsWorld world,
        ZoneNativeEntityServices? entities, ZoneActorModel? actorModel,
        ZoneActorPhysicalizationRequest request, Action<string> trace, Vector3 spawn, float groundZ)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(trace);
        const string refusal = "blocked-native-pose-write-probe: original3502A300 action type1 controls velocity; " +
            "the former20-byte position-labelled action is invalid and has been removed";
        trace(refusal);
        if (EvidencePath.Length != 0) File.AppendAllText(EvidencePath, refusal + Environment.NewLine);
        throw new NotSupportedException(refusal);
    }
}

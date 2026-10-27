using System.Numerics;

namespace AAEmu.Zone.Simulation;

/// <summary>
/// CNavPath creation parameters at +64. Native constructor 31142EA0 initializes the building ID to -1 and all other fields to zero.
/// </summary>
public readonly record struct ZoneNavigationPathCreationParameters
{
    public ZoneNavigationPathCreationParameters() { }
    public Vector3 Start { get; init; }
    public Vector3 End { get; init; }
    public Vector3 StartDirection { get; init; }
    public Vector3 EndDirection { get; init; }
    public int ForceBuildingId { get; init; } = -1;
    public bool AllowDangerousDestination { get; init; }
    public bool PrecalculatedPath { get; init; }
    public bool InhibitPathRegeneration { get; init; }
    public bool ContinueMovingAtEnd { get; init; }
    public bool IsDirectional { get; init; }

    // The native path serializer 3113CE80 omits these two fields, although constructor and copy include them.
    // HandlePathDecision 311757BC passes EndDistance to path trimming 31141B30.
    public float EndDistance { get; init; }
    // UsePathToFollow 31162B44 closes the input route when this is set; 310EC118 then disables stopping at its end.
    public bool ClosedLoop { get; init; }
}

// 311407F0 / 311408F0 store float RGBA colors; 3113DFB0 converts them only when rendering.
public readonly record struct ZoneNavigationDebugLine(Vector3 Start, Vector3 End, Vector4 Color);
public readonly record struct ZoneNavigationDebugSphere(Vector3 Position, float Radius, Vector4 Color);

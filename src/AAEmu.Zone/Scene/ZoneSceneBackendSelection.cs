namespace AAEmu.Zone.Scene;

/// <summary>Scene composition. Managed never constructs or loads a CryEngine module.</summary>
public enum ZoneEngineBackendKind
{
    Native = 0,
    Managed = 1,
}

public enum ZonePhysicsBackendKind
{
    NativeTerrain = 0,
    /// <summary>Legacy owned movement with CryEngine terrain and static collision queries.</summary>
    Managed = 1,
    /// <summary>Owned movement and authored terrain queries without CryEngine.</summary>
    ManagedTerrain = 2,
}

public enum ZoneAiBackendKind
{
    Native = 0,
    Managed = 1,
}

/// <summary>
/// One immutable selection per scene generation. Legacy native/owned configurations retain
/// their meaning; engineBackend=managed selects the independent composition explicitly.
/// </summary>
public sealed record ZoneSceneBackendSelection
{
    public ZoneEngineBackendKind Engine { get; init; } = ZoneEngineBackendKind.Native;
    public ZonePhysicsBackendKind Physics { get; init; } = ZonePhysicsBackendKind.NativeTerrain;
    public ZoneAiBackendKind Ai { get; init; } = ZoneAiBackendKind.Native;

    public static ZoneSceneBackendSelection Default { get; } = new();
    public static ZoneSceneBackendSelection NativeFree { get; } = new()
    {
        Engine = ZoneEngineBackendKind.Managed,
        Physics = ZonePhysicsBackendKind.ManagedTerrain,
        Ai = ZoneAiBackendKind.Managed,
    };
}

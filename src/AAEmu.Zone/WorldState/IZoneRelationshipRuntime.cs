namespace AAEmu.Zone.WorldState;

/// <summary>
/// Runtime configuration required by the content-backed relationship scene.
/// Missing configuration must fail explicitly.
/// </summary>
public interface IZoneRelationshipRuntime
{
    // World configuration's group exclusion set, native config+468 (39C53B50).
    bool IsFestivalGuardExcluded(ushort groupId);
}

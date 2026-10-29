namespace AAEmu.Zone.Physics;

public sealed partial class ZoneManagedStaticGeometry
{
    private ulong _publicationRevision;

    /// <summary>
    /// Managed lifetime token for this exact scene's immutable instance publication.
    /// Add, present removal and nonempty clear advance it under the scene gate; queries,
    /// absent removal, empty clear and rejected inputs do not. Meshes, materials, transforms
    /// and admission bounds are immutable after instance creation in this owner.
    /// </summary>
    public ulong PublicationRevision { get { lock (_gate) return _publicationRevision; } }

    private void AdvancePublicationCore()
    {
        if (_publicationRevision == ulong.MaxValue)
            throw new InvalidOperationException("Static collision publication revisions are exhausted; instances cannot change without a new scene.");
        _publicationRevision++;
    }
}

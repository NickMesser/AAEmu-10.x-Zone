namespace AAEmu.Zone.Native.FlowNodes;

/// <summary>
/// Exclusive ownership of a real X2-compatible flow-node factory and its callback roots.
/// Implementations supply the actual node creation, configuration and behavior; this contract
/// does not authorize a stub factory. All methods run on the engine's owning thread.
/// </summary>
internal interface IZoneNativeFlowNodeFactoryOwner
{
    string Name { get; }

    /// <summary>
    /// Stable native interface storage with AddRef, Release, Create and GetMemoryUsage at
    /// slots 0, 8, 0x10 and 0x18. Storage and callback addresses remain valid until the final
    /// release phase, including after the registry releases its last factory reference.
    /// </summary>
    nint FactoryPointer { get; }

    /// <summary>
    /// Read-only admission of the complete implementation, its stable native interface and
    /// dependencies. This must not execute native code, acquire references or create nodes.
    /// It is also called between registration callbacks to detect changed ownership.
    /// </summary>
    void ValidateForRegistration();

    /// <summary>
    /// Checks managed callback fault state only. Reverse callbacks must contain exceptions
    /// and retain their ownership on uncertainty. This check remains valid after all cleanup
    /// phases and must never dereference retired native storage.
    /// </summary>
    void ThrowIfFaulted();

    /// <summary>
    /// Releases an exclusively owned, unpublished factory whose registration never began.
    /// No native graph or registry reference may exist. This is construction rollback, not
    /// a substitute for native framework shutdown.
    /// </summary>
    void ReleaseUnpublished();

    /// <summary>
    /// Called after actual framework/FlowSystem shutdown, while native modules remain mapped.
    /// Verify that graph/registry references are gone and release the factory's own cached
    /// node references through their real lifetime. Preserve factory interface/callback storage
    /// until ReleaseAfterNativeModules. Do not assume the former CSystem is still alive.
    /// </summary>
    void RetireAfterFrameworkShutdown();

    /// <summary>
    /// Releases retained interface/callback storage after native module teardown. No native
    /// code may run here, and no native reference may remain. A failure must retain any
    /// resources whose release has not completed; the registry will not replay this phase.
    /// </summary>
    void ReleaseAfterNativeModules();
}

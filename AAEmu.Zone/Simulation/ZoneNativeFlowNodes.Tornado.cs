namespace AAEmu.Zone.Simulation;

/// <summary>
/// Real game-framework and entity services consumed by Crysis:TornadoWander. The provider
/// belongs to the same engine and graph generation as the common node adapter. It must not
/// implement a wandering simulation or turn an unavailable framework into a missing entity.
/// </summary>
internal interface IZoneNativeFlowTornadoServices
{
    void ValidateForRegistration();

    /// <summary>
    /// Preserve 3920C4E0's order: obtain the current framework through game+410, obtain the
    /// full unsigned ID from this real IEntity+0, then query framework+198 with "Tornado".
    /// Null means that exact entity has no such extension; unavailable ownership is an error.
    /// The returned extension and entity lookups stay valid through the held engine callback.
    /// </summary>
    IZoneNativeFlowTornado? FindTornado(nint entity);

    /// <summary>The actual EntitySystem+70 lookup; preserve all 32 ID bits and null results.</summary>
    nint FindEntity(uint entityId);

    /// <summary>
    /// Read-only lifetime admission before node retirement. Reject while any live extension
    /// can still invoke this receiver. The original node destructor does not detach or cancel
    /// a target: this check must not clear target state, synthesize completion, or use dead
    /// native pointers. The provider tracks replacement, completed callbacks and actual
    /// extension retirement under the engine gate; a failed check retains the node owner.
    /// </summary>
    void VerifyCompletionRetired(IZoneNativeFlowTornadoCompletion completion);
}

/// <summary>
/// An actual resolved Tornado extension. 3920DBE0 replaces its target IEntity and completion
/// receiver together, without AddRef, output, random sampling, or immediate movement. A
/// failed target lookup never reaches this operation and leaves both earlier fields intact.
/// </summary>
internal interface IZoneNativeFlowTornado
{
    /// <summary>
    /// Bind the real target and retain the receiver until replacement, the complete native
    /// target-reached interval, or actual extension retirement. The caller's engine owner
    /// supplies the required target lifetime. Do not pass the common managed node's native
    /// interface pointer to 3920DBE0: native completion392108F0 directly reads the original
    /// node layout at +10/+18, rather than invoking a virtual completion callback.
    /// </summary>
    void SetTarget(nint targetEntity, IZoneNativeFlowTornadoCompletion completion);
}

/// <summary>
/// Completion from the actual extension owner. 3920FE10 emits the entity TargetReached event
/// first, then invokes the current receiver if present, then clears BOTH extension fields
/// even if the event or receiver reentrantly assigned a new target. Keep the receiver rooted
/// through that whole interval. The provider runs this synchronously under the engine's
/// callback gate and contains exceptions before returning to native code. Movement, distance
/// evaluation and randomness remain the real extension's responsibility.
/// </summary>
internal interface IZoneNativeFlowTornadoCompletion
{
    void OnTargetReached();
}

/// <summary>
/// Complete flow-node behavior from table39FB8588. The ordinary factory and clone both create
/// fresh state; the common adapter owns native interfaces, configuration and graph references.
/// This component requires an actual Tornado extension provider and does not implement that
/// entity's movement or claim that such an extension exists in the current engine host.
/// </summary>
internal static class ZoneNativeFlowTornadoNodes
{
    internal static IZoneNativeFlowNodeBehavior CreateTornadoWander(IZoneNativeFlowTornadoServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return new TornadoWander(services);
    }

    private sealed class TornadoWander(IZoneNativeFlowTornadoServices services)
        : IZoneNativeFlowNodeBehavior, IZoneNativeFlowTornadoCompletion
    {
        private static readonly ZoneNativeFlowNodeConfiguration NodeConfiguration = new(
            Array.AsReadOnly(new ZoneNativeFlowInputPort[]
            {
                new("Activate", ZoneNativeFlowValue.Void(),
                    "Tell the tornado to actually wander to the new [Target]"),
                new("Target", ZoneNativeFlowValue.EntityId(0),
                    "Set a new wandering target for the tornado")
            }),
            Array.AsReadOnly(new ZoneNativeFlowOutputPort[]
            {
                new("Done", 0, "Triggered when target has been reached")
            }),
            "Tells a tornado entity to wander in the direction of the [Target] entity",
            0xfffff02f, 0x21);

        private ZoneNativeFlowActivation _activation;
        private bool _destroyed;

        public ZoneNativeFlowNodeConfiguration Configuration => NodeConfiguration;

        public void ProcessEvent(int eventId, ZoneNativeFlowActivation activation)
        {
            ObjectDisposedException.ThrowIf(_destroyed, this);
            // 3920C4E0 copies all 0x30 context bytes only on event 3. In particular, a later
            // event 1 does not replace the context used by completion392108F0.
            if (eventId == 3)
            {
                _activation = activation;
                return;
            }
            if (eventId != 1 || !activation.IsPortActive(0) || activation.Entity == 0) return;

            // Resolve the extension BEFORE reading Target. The native target accessor is
            // exact type4 and cannot be replaced with a float/int conversion or guessed ID.
            var tornado = services.FindTornado(activation.Entity);
            if (tornado is null) return;
            var target = services.FindEntity(activation.GetEntityId(1));
            if (target != 0) tornado.SetTarget(target, this);
        }

        public void OnTargetReached()
        {
            ObjectDisposedException.ThrowIf(_destroyed, this);
            // The output is declared void but the native trigger carries boolean true.
            // A zero stored graph is explicitly admitted by 392108F0 and emits nothing.
            if (_activation.Graph != 0) _activation.ActivateBooleanOutput(0, true);
        }

        // Actual node slots+20/+28 are 39710D00(true) and 392A49F0(empty). No target,
        // transition, update registration or completion state is serialized by this node.
        public bool SerializeXml(ZoneNativeFlowActivation activation, nint xmlNodeReference, bool loading) => true;
        public void Serialize(ZoneNativeFlowActivation activation, nint serializer) { }

        public void Destroy()
        {
            if (_destroyed) return;
            // 3931C4E0 performs no native cancellation. Prevent unsafe adapter reclamation
            // until the real provider proves the otherwise-unretained receiver unreachable.
            services.VerifyCompletionRetired(this);
            _activation = default;
            _destroyed = true;
        }
    }
}

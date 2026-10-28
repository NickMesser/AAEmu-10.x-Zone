using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Physics;

namespace AAEmu.Zone.Diagnostics;

/// <summary>
/// Replays the original living corpus against the managed controller. Native creation is not
/// reproduced: an explicitly matched capture supplies only each body's copied frame-zero pose.
/// </summary>
internal static class ZoneManagedLivingMotionProbes
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };

    private sealed record Seed(string Name, float[] RequestedPosition, float[] NativeCreatedPosition,
        float[] NativeFrameZeroPosition, bool NativeFrameZeroFlying, float NativeFrameZeroGroundHeight,
        float[] NativeFrameZeroGroundNormal);
    private sealed record PreparedProbe(ZoneLivingRecoveryProbes.Input Input, Seed[] Seeds);
    private sealed record PreparedCorpus(PreparedProbe[] Probes, string InputSha256, string ReferenceSha256);
    private sealed record ControlState(int LastVelocityMode, bool HasPendingVelocityRequest,
        bool IdleStateKnown, bool LastStepUsedIdleShortcut, int ConstraintCount,
        bool LivingFlag2, bool ForceRequestedVelocityOnce, float ImpulseRecoveryTimeRemaining,
        float SuspensionOffset, float SuspensionVelocity, float SuspensionAcceleration, float SuspensionTime);
    private sealed record MovementState(float MinSlideAngle, float MaxClimbAngle, float MaxJumpAngle,
        float MinFallAngle, float MinSlideCosine, float MaxClimbCosine, float MaxJumpCosine,
        float MinFallCosine, float MaximumSupportVelocity, float GeometryRadius,
        float GeometryAxialHalfHeight, float[] PrimaryPartOffset, float Inertia,
        float InertiaAcceleration, float AirControl, float AirResistance);
    private sealed record StanceUpdate(ulong Sequence, bool PreflightAccepted, string Phase,
        float[] Position, float UpwardShift, int Queries, bool UsedNormalFallback,
        bool HadMultipleContacts, string Refusal);
    private sealed record BodyState(string Name, uint PhysicalId, uint Generation,
        float[] Position, float[] RawPosition, float[] Velocity, float[] RawVelocity,
        float[] RequestedVelocity, float[] Gravity, bool IsFlying, bool IsGrounded,
        float Radius, float AxialHalfHeight, float HeightCollider, float HeightPivot,
        string Shape, bool CollisionActive, float GroundHeight, float[] GroundNormal, ControlState Control,
        MovementState Movement, StanceUpdate? LastQueuedDimensionUpdate);
    private sealed record Frame(int Number, float ManagedSimulationTime, BodyState[] Bodies);
    private sealed record DimensionResult(int BeforeFrame, string Body,
        ZoneLivingRecoveryProbes.DimensionInput Requested, int ThreadSafe, int ManagedReturn,
        string Refusal, float ImmediateUpwardShift, StanceUpdate? QueuedUpdate,
        BodyState Before, BodyState After);
    private sealed record CorrectionResult(int BeforeFrame, string Body,
        ZoneLivingRecoveryProbes.CorrectionInput Requested, int ManagedReturn, string Refusal,
        BodyState Before, BodyState After);
    private sealed record Result(string Name, string Outcome, ZoneLivingRecoveryProbes.Input Input,
        Seed[] Seeds, int[] ActionReturns, Frame[] Frames, bool? MinimumFirstBodyTravelMet,
        string? Diagnostic, DimensionResult[] DimensionUpdates, CorrectionResult[] Corrections);

    public static bool Run(string? inputPath, string? outputPath, string? nativeReferencePath,
        ZoneManagedTerrainGeometry terrain, ZoneManagedStaticGeometry staticGeometry,
        IZoneCollisionSurfaces? surfaces, CancellationToken cancellation)
    {
        if (inputPath is null) return true;
        if (nativeReferencePath is null)
            throw new ArgumentException("Managed living probes require --scene-living-native-reference for an explicit matched frame-zero pose seed.");
        var path = Path.GetFullPath(outputPath ?? Path.ChangeExtension(inputPath, ".managed-living-results.json"));
        if (string.Equals(path, Path.GetFullPath(inputPath), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(path, Path.GetFullPath(nativeReferencePath), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Managed living output must have a different path from its input and native reference.", nameof(outputPath));
        ArgumentNullException.ThrowIfNull(terrain);
        ArgumentNullException.ThrowIfNull(staticGeometry);
        var corpus = Prepare(inputPath, nativeReferencePath);
        var results = new List<Result>(corpus.Probes.Length);
        uint nextId = 40000;
        try
        {
            foreach (var prepared in corpus.Probes)
            {
                cancellation.ThrowIfCancellationRequested();
                var probe = prepared.Input;
                // A JSON .01 decoded as float is slightly below .01. FromSeconds would
                // truncate that to 99,999 ticks, while this corpus' copied native step is
                // .01. Round to the nearest representable tick instead of changing physics.
                var period = TimeSpan.FromTicks(checked((long)Math.Round((double)probe.StepSeconds * TimeSpan.TicksPerSecond)));
                if (period <= TimeSpan.Zero)
                    throw new InvalidDataException("Managed living probes need a positive period representable in100ns ticks.");
                Console.WriteLine($"managed living probe {probe.Name}: {probe.Bodies.Length} bodies, {probe.Frames} steps at {probe.StepSeconds}s.");
                var backend = new ZoneManagedPhysicsBackend(terrain, surfaces, staticGeometry);
                var handles = new List<ZoneActorHandle>(probe.Bodies.Length);
                var frames = new List<Frame>(probe.Frames + 1);
                var actionReturns = new List<int>();
                var dimensions = new List<DimensionResult>();
                var corrections = new List<CorrectionResult>();
                var errors = new List<Exception>();
                bool? minimumMet = null;
                try
                {
                    for (var i = 0; i < probe.Bodies.Length; i++)
                    {
                        var input = probe.Bodies[i];
                        var handle = new ZoneActorHandle(nextId++, 1);
                        backend.AddActor(handle, ZonePhysicsActorDescription.FromLivingRequest(ZoneLivingRecoveryProbes.Request(input)));
                        handles.Add(handle);
                        // Reproduce stepping from copied physical truth. Native construction
                        // shifts and native hidden support state remain separately reported.
                        backend.Teleport(handle, Vector(prepared.Seeds[i].NativeFrameZeroPosition), Quaternion.Identity);
                        if (!input.CollisionActive) backend.SetLivingCollisionActive(handle, false);
                    }
                    frames.Add(Capture(0));
                    for (var frame = 1; frame <= probe.Frames; frame++)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        foreach (var update in probe.DimensionUpdates ?? [])
                            if (update.BeforeFrame == frame)
                            {
                                var index = Array.FindIndex(probe.Bodies, input => input.Name == update.Body);
                                var handle = handles[index];
                                var before = CaptureBody(update.Body, handle, backend);
                                var requested = update.Dimensions;
                                var stance = ZoneLivingRecoveryProbes.Request(probe.Bodies[index] with
                                {
                                    Shape = requested.Shape, Radius = requested.Radius,
                                    HeightCollider = requested.HeightCollider, HeightPivot = requested.HeightPivot,
                                    SegmentHalfHeight = requested.SegmentHalfHeight, HalfHeight = requested.HalfHeight,
                                }).Stance;
                                var accepted = update.ThreadSafe == -1
                                    ? backend.TryChangeLivingStance(handle, stance, out var refusal)
                                    : backend.TryApplyLivingStanceImmediatelyForDiagnostic(handle, stance, out refusal);
                                var after = CaptureBody(update.Body, handle, backend);
                                dimensions.Add(new(frame, update.Body, requested, update.ThreadSafe, accepted ? 1 : 0,
                                    refusal, after.Position[2] - before.Position[2],
                                    update.ThreadSafe == -1 ? after.LastQueuedDimensionUpdate : null, before, after));
                            }
                        foreach (var correction in probe.Corrections ?? [])
                            if (correction.BeforeFrame == frame)
                            {
                                var index = Array.FindIndex(probe.Bodies, input => input.Name == correction.Body);
                                var handle = handles[index];
                                var before = CaptureBody(correction.Body, handle, backend);
                                var accepted = backend.TryApplyLivingCorrection(handle,
                                    ZoneLivingRecoveryProbes.Correction(correction), out var refusal);
                                corrections.Add(new(frame, correction.Body, correction, accepted ? 1 : 0,
                                    refusal, before, CaptureBody(correction.Body, handle, backend)));
                            }
                        for (var i = 0; i < probe.Bodies.Length; i++)
                        {
                            var input = probe.Bodies[i];
                            if ((frame == input.VelocityStartFrame || probe.SubmitVelocityEachStep && frame >= input.VelocityStartFrame) &&
                                input.Velocity is { } velocity)
                            {
                                backend.ApplyLivingVelocityRequest(handles[i], Vector(velocity),
                                    (ZonePhysicsLivingVelocityMode)input.VelocityMode);
                                actionReturns.Add(1);
                            }
                        }
                        backend.Step(period);
                        frames.Add(Capture(frame));
                    }
                    if (probe.MinimumFirstBodyTravel is { } minimum)
                        minimumMet = Vector3.Distance(Vector(frames[0].Bodies[0].RawPosition),
                            Vector(frames[^1].Bodies[0].RawPosition)) >= minimum;
                }
                catch (Exception error) { errors.Add(error); }
                finally
                {
                    foreach (var handle in handles.AsEnumerable().Reverse())
                        try { backend.RemoveActor(handle); } catch (Exception error) { errors.Add(error); }
                }
                results.Add(new(probe.Name, errors.Count != 0 ? "error" : minimumMet == false ? "control-failed" : "completed",
                    probe, prepared.Seeds, actionReturns.ToArray(), frames.ToArray(), minimumMet,
                    errors.Count == 0 ? null : string.Join(Environment.NewLine, errors.Select(error => error.ToString())),
                    dimensions.ToArray(), corrections.ToArray()));
                Write(false);
                cancellation.ThrowIfCancellationRequested();

                Frame Capture(int frame) => new(frame, frame * probe.StepSeconds,
                    handles.Select((handle, i) => CaptureBody(probe.Bodies[i].Name, handle, backend)).ToArray());
            }
        }
        catch { Write(false); throw; }
        Write(true);
        var successful = results.Count(result => result.Outcome == "completed");
        Console.WriteLine($"managed living probes: {successful}/{corpus.Probes.Length} completed their controls; matched native frame-zero pose, managed physics only.");
        return successful == corpus.Probes.Length;

        void Write(bool complete)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                backend = "managed-living", complete, requested = corpus.Probes.Length,
                corpus.InputSha256, nativeReference = Path.GetFullPath(nativeReferencePath),
                nativeReferenceSha256 = corpus.ReferenceSha256,
                creationProfile = "Native frame0 RawPosition seeds each managed body; creation admission is not compared. Native creation/frame0 pose and unseeded cache metadata are preserved in seeds.",
                bodyProfile = "Same resolved living request dimensions/dynamics; upright primitive, authored entity origin and collider offset; explicit collision-active state.",
                stepProfile = "Full managed backend Step on decoded authored terrain/static proxies plus registered bodies; one fresh backend per case. Declared float period rounds to nearest100ns tick; native single-step elapsed must match the declared period.",
                velocityProfile = "Queued managed living wishes, explicit normal0/immediate3; actionReturns1 means accepted request, not copied native return.",
                dimensionProfile = "Actor queued(-1) or diagnostic immediate(0); return, immediate applied readback and subsequent queued drain phase remain separate.",
                correctionProfile = "Copied network tuple capability, distinct from reset Action4; correction before optional wish submission and Step. Same-ground ownership remains explicitly unsupported; return/immediate state are independent from queued pose drain.",
                centerConvention = "input position is entity origin; collider center=origin+(heightCollider-heightPivot)*up",
                limitations = "No native hidden support pointer, suspension/impulse state or contact cache is imported. This report compares the exercised scene/pose/controller subset; it is not full creation or gameplay parity.",
                results,
            }, Json));
        }
    }

    private static BodyState CaptureBody(string name, ZoneActorHandle handle, ZoneManagedPhysicsBackend backend)
    {
        backend.CopyStatus(handle, out var status);
        backend.CopyDescription(handle, out var description);
        backend.CopyLivingControlState(handle, out var control);
        var settings = description.LivingMovement!;
        var update = backend.TryReadLivingStanceUpdate(handle, out var queued) ? Copy(queued) : null;
        return new(name, handle.UnitObjectId, handle.Generation, Values(status.Position), Values(status.Position),
            Values(status.Velocity), Values(status.Velocity), Values(control.RequestedVelocity), [0, 0, description.Gravity],
            status.IsFlying, status.IsGrounded, settings.Radius, settings.SupportHalfHeight,
            settings.HeightCollider, settings.HeightPivot, description.Shape.Kind.ToString().ToLowerInvariant(), description.CollisionActive,
            control.CachedGroundHeight, Values(control.CachedGroundNormal),
            new((int)control.LastVelocityMode, control.HasPendingVelocityRequest, control.IdleStateKnown,
                control.LastStepUsedIdleShortcut, control.ConstraintCount, control.LivingFlag2,
                control.ForceRequestedVelocityOnce, control.ImpulseRecoveryTimeRemaining,
                control.SuspensionOffset, control.SuspensionVelocity, control.SuspensionAcceleration, control.SuspensionTime),
            new(settings.MinimumSlideAngleDegrees, settings.MaximumClimbAngleDegrees, settings.MaximumJumpAngleDegrees,
                settings.MinimumFallAngleDegrees, settings.MinimumSlideCosine, settings.MaximumClimbCosine,
                settings.MaximumJumpCosine, settings.MinimumFallCosine, settings.MaximumSupportVelocity,
                settings.Radius, settings.SegmentHalfHeight, [0, 0, description.CenterOffsetZ],
                settings.Inertia, settings.InertiaAcceleration, settings.AirControl, settings.AirResistance), update);
    }

    private static StanceUpdate Copy(ZoneManagedLivingStanceUpdate update) => new(update.Sequence,
        update.PreflightAccepted, update.Phase.ToString(), Values(update.Position), update.UpwardShift,
        update.Queries, update.UsedNormalFallback, update.HadMultipleContacts, update.Refusal);

    private static PreparedCorpus Prepare(string inputPath, string referencePath)
    {
        var probes = ZoneLivingRecoveryProbes.Read(inputPath);
        using var reference = JsonDocument.Parse(File.ReadAllBytes(referencePath));
        var root = reference.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            Required(root, "backend").GetString() != "native-living" ||
            !Required(root, "complete").GetBoolean() ||
            Required(root, "requested").GetInt32() != probes.Length)
            throw new InvalidDataException("The living reference must be a completed native-living capture of this exact corpus.");
        var results = Required(root, "results");
        if (results.ValueKind != JsonValueKind.Array || results.GetArrayLength() != probes.Length)
            throw new InvalidDataException("The native living reference must contain exactly one result per requested case.");
        var byName = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var result in results.EnumerateArray())
        {
            var name = Required(result, "name").GetString();
            if (string.IsNullOrWhiteSpace(name) || !byName.TryAdd(name, result) ||
                Required(result, "outcome").GetString() != "completed" ||
                result.TryGetProperty("diagnostic", out var diagnostic) && diagnostic.ValueKind != JsonValueKind.Null)
                throw new InvalidDataException("The native living reference contains a repeated, failed or unnamed result.");
        }
        var prepared = new List<PreparedProbe>(probes.Length);
        foreach (var probe in probes)
        {
            if (!byName.TryGetValue(probe.Name, out var result))
                throw new InvalidDataException($"The native living reference is missing {probe.Name}.");
            var referenceInput = Required(result, "input").Deserialize<ZoneLivingRecoveryProbes.Input>(Json);
            // Deserialization applies the same historical defaults to captures made before a
            // later optional diagnostic field was introduced. Every actual request must agree.
            if (referenceInput is null || JsonSerializer.Serialize(referenceInput, Json) != JsonSerializer.Serialize(probe, Json))
                throw new InvalidDataException($"The native living reference has different input for {probe.Name}.");
            var frames = ReadReferenceFrames(probe, result);
            var seeds = new Seed[probe.Bodies.Length];
            for (var i = 0; i < probe.Bodies.Length; i++)
            {
                var input = probe.Bodies[i];
                var initial = frames[0][input.Name];
                var created = frames[-1][input.Name];
                var expected = ZonePhysicsActorDescription.FromLivingRequest(ZoneLivingRecoveryProbes.Request(input));
                ValidateInitialDimensions(probe.Name, input, initial, expected);
                var velocity = ReadVector(initial, "rawVelocity");
                var wish = ReadVector(initial, "requestedVelocity");
                if (velocity != Vector3.Zero || wish != Vector3.Zero)
                    throw new InvalidDataException($"Native frame zero for {probe.Name}/{input.Name} already has motion; this pose-only seed profile cannot reproduce it.");
                seeds[i] = new(input.Name, [.. input.Position], Values(ReadVector(created, "rawPosition")),
                    Values(ReadVector(initial, "rawPosition")), Required(initial, "isFlying").GetBoolean(),
                    ReadFinite(initial, "groundHeight"), Values(ReadVector(initial, "groundNormal")));
            }
            prepared.Add(new(probe, seeds));
        }
        return new(prepared.ToArray(), Hash(inputPath), Hash(referencePath));
    }

    private static Dictionary<int, Dictionary<string, JsonElement>> ReadReferenceFrames(
        ZoneLivingRecoveryProbes.Input probe, JsonElement result)
    {
        var frames = Required(result, "frames");
        if (frames.ValueKind != JsonValueKind.Array || frames.GetArrayLength() != probe.Frames + 2)
            throw new InvalidDataException($"Native reference {probe.Name} must contain creation, frame zero and every requested step.");
        var mapped = new Dictionary<int, Dictionary<string, JsonElement>>();
        foreach (var frame in frames.EnumerateArray())
        {
            var number = Required(frame, "number").GetInt32();
            if (number < -1 || number > probe.Frames || mapped.ContainsKey(number))
                throw new InvalidDataException($"Native reference {probe.Name} has a repeated or out-of-range frame.");
            var bodies = Required(frame, "bodies");
            if (bodies.ValueKind != JsonValueKind.Array || bodies.GetArrayLength() != probe.Bodies.Length)
                throw new InvalidDataException($"Native reference {probe.Name}, frame {number}, has different body membership.");
            var byName = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var body in bodies.EnumerateArray())
            {
                var name = Required(body, "name").GetString();
                if (name is null || !probe.Bodies.Any(input => input.Name == name) || !byName.TryAdd(name, body))
                    throw new InvalidDataException($"Native reference {probe.Name}, frame {number}, has an unknown or repeated body.");
                ReadVector(body, "rawPosition");
                ReadVector(body, "rawVelocity");
                ReadVector(body, "requestedVelocity");
                ReadVector(body, "groundNormal");
                ReadFinite(body, "groundHeight");
                if (number > 0)
                {
                    var elapsed = ReadFinite(body, "stepElapsed");
                    if (elapsed <= 0 || MathF.Abs(elapsed - probe.StepSeconds) > .0000001f)
                        throw new InvalidDataException($"Native reference {probe.Name} has a quantized/substep elapsed period outside this single-step replay profile.");
                }
            }
            mapped.Add(number, byName);
        }
        return mapped;
    }

    private static void ValidateInitialDimensions(string caseName, ZoneLivingRecoveryProbes.BodyInput input,
        JsonElement initial, ZonePhysicsActorDescription description)
    {
        var settings = description.LivingMovement!;
        Near("radius", settings.Radius);
        Near("axialHalfHeight", settings.SupportHalfHeight);
        Near("heightCollider", settings.HeightCollider);
        Near("heightPivot", settings.HeightPivot);
        if (Vector3.Distance(ReadVector(initial, "gravity"), new(0, 0, description.Gravity)) > .00001f ||
            ((Required(initial, "livingFlags").GetUInt32() & 8) != 0) != settings.UseCapsule)
            throw Mismatch();
        var movement = Required(initial, "movement");
        foreach (var (name, expected) in new (string, float)[]
        {
            ("minSlideAngle", settings.MinimumSlideAngleDegrees),
            ("maxClimbAngle", settings.MaximumClimbAngleDegrees),
            ("maxJumpAngle", settings.MaximumJumpAngleDegrees),
            ("minFallAngle", settings.MinimumFallAngleDegrees),
            ("minSlideCosine", settings.MinimumSlideCosine),
            ("maxClimbCosine", settings.MaximumClimbCosine),
            ("maxJumpCosine", settings.MaximumJumpCosine),
            ("minFallCosine", settings.MinimumFallCosine),
            ("maximumSupportVelocity", settings.MaximumSupportVelocity),
            ("geometryRadius", settings.Radius),
            ("geometryAxialHalfHeight", settings.SegmentHalfHeight),
            ("inertia", settings.Inertia), ("inertiaAcceleration", settings.InertiaAcceleration),
            ("airControl", settings.AirControl), ("airResistance", settings.AirResistance),
        })
            if (MathF.Abs(ReadFinite(movement, name) - expected) > .00001f) throw Mismatch();
        if (Vector3.Distance(ReadVector(movement, "primaryPartOffset"), new(0, 0, description.CenterOffsetZ)) > .00001f)
            throw Mismatch();
        return;

        void Near(string name, float expected)
        { if (MathF.Abs(ReadFinite(initial, name) - expected) > .00001f) throw Mismatch(); }
        InvalidDataException Mismatch() =>
            new($"Native frame-zero dimensions/dynamics differ from the resolved managed request for {caseName}/{input.Name}.");
    }

    private static JsonElement Required(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var field) ? field :
        throw new InvalidDataException($"The native living reference is missing {property}.");
    private static float ReadFinite(JsonElement value, string property)
    {
        var field = Required(value, property);
        if (field.ValueKind != JsonValueKind.Number || !field.TryGetSingle(out var number) || !float.IsFinite(number))
            throw new InvalidDataException($"The native living reference needs a finite {property}.");
        return number;
    }
    private static Vector3 ReadVector(JsonElement value, string property)
    {
        var field = Required(value, property);
        if (field.ValueKind != JsonValueKind.Array || field.GetArrayLength() != 3)
            throw new InvalidDataException($"The native living reference needs a three-component {property}.");
        var components = field.EnumerateArray().Select(component => component.GetSingle()).ToArray();
        if (components.Any(component => !float.IsFinite(component)))
            throw new InvalidDataException($"The native living reference needs a finite {property}.");
        return Vector(components);
    }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static Vector3 Vector(float[] value) => new(value[0], value[1], value[2]);
    private static float[] Values(Vector3 value) => [value.X, value.Y, value.Z];
}

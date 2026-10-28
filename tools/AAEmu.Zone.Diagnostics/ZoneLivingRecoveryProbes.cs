using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using AAEmu.Zone.Actors;
using AAEmu.Zone.Content;
using AAEmu.Zone.Native.Physics;

namespace AAEmu.Zone.Diagnostics;

/// <summary>Controlled full living bodies stepped by the original world, independent of raw shape sweeps.</summary>
internal static class ZoneLivingRecoveryProbes
{
    internal sealed record BodyInput(string Name, string Shape, float[] Position, float Radius,
        float HeightCollider, float HeightPivot, float Gravity = 0, float? SegmentHalfHeight = null,
        float? HalfHeight = null, float[]? Velocity = null, int Mass = 80, float[]? SetupPosition = null,
        bool CollisionActive = true, float? MinSlideAngle = null, float? MaxClimbAngle = null,
        float? MinFallAngle = null, int? MaxVelGround = null, bool UseNativeMovementDefaults = false,
        int VelocityStartFrame = 1, int VelocityMode = 3, float AirControl = 1, float AirResistance = 0,
        float? Inertia = null, float? InertiaAcceleration = null);
    internal sealed record DimensionInput(string Shape, float Radius, float HeightCollider,
        float HeightPivot, float? SegmentHalfHeight = null, float? HalfHeight = null);
    internal sealed record DimensionUpdate(int BeforeFrame, string Body, DimensionInput Dimensions, int ThreadSafe = -1);
    internal sealed record CorrectionInput(int BeforeFrame, string Body, float[] Position, float[] Velocity,
        float[]? OriginalVelocity = null, bool SameGround = false, bool GroundTransition = false,
        bool ForcePosition = false, uint GroundPart = 0, uint GroundPartId = 0);
    internal sealed record Input(string Name, BodyInput[] Bodies, float StepSeconds = .01f,
        int Frames = 5, bool SubmitVelocityEachStep = false, float? MinimumFirstBodyTravel = null,
        DimensionUpdate[]? DimensionUpdates = null, CorrectionInput[]? Corrections = null);
    private sealed record MovementState(float MinSlideAngle, float MaxClimbAngle, float MaxJumpAngle,
        float MinFallAngle, float MinSlideCosine, float MaxClimbCosine, float MaxJumpCosine,
        float MinFallCosine, float MaximumSupportVelocity, float SuspensionOffset,
        float SuspensionVelocity, float SuspensionAcceleration, float SuspensionTime,
        bool HasRetainedSupport, float[] SupportVelocity, float GeometryRadius,
        float GeometryAxialHalfHeight, float[] PrimaryPartOffset, float Inertia,
        float InertiaAcceleration, float AirControl, float AirResistance);
    private sealed record BodyState(string Name, ushort PhysicalId, float[] Position, float[] RawPosition,
        float[] Velocity, float[] RawVelocity, float[] RequestedVelocity, float[] Gravity,
        bool IsFlying, float FlyingTime, float Radius, float AxialHalfHeight, float HeightCollider,
        float HeightPivot, uint EntityFlags, uint LivingFlags, uint PartColliderFlags,
        float GroundHeight, float[] GroundNormal, uint CommandState, float ContactClearance,
        byte SimulationClass, bool InLivingStepList, float StepPeriod, float StepElapsed, MovementState Movement,
        bool VelocityBypassPending);
    private sealed record DimensionResult(int BeforeFrame, string Body, DimensionInput Requested,
        int ThreadSafe, int NativeReturn, BodyState Before, BodyState After);
    private sealed record CorrectionResult(int BeforeFrame, string Body, CorrectionInput Requested,
        int NativeReturn, BodyState Before, BodyState After);
    private sealed record Frame(int Number, float NativeSimulationTime, BodyState[] Bodies);
    private sealed record Result(string Name, string Outcome, Input Input, int[] ActionReturns,
        Frame[] Frames, string? Diagnostic = null, DimensionResult[]? DimensionUpdates = null,
        CorrectionResult[]? Corrections = null);
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, WriteIndented = true
    };

    public static void Run(string inputPath, string? outputPath, ZoneNativeTerrainPhysicsWorld world,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(world);
        var probes = Read(inputPath);
        var results = new List<Result>(probes.Length);
        var nextId = 40000;
        try
        {
            foreach (var probe in probes)
            {
                cancellation.ThrowIfCancellationRequested();
                Console.WriteLine($"native living probe {probe.Name}: {probe.Bodies.Length} bodies, {probe.Frames} steps at{probe.StepSeconds}s.");
                var bodies = new List<ZoneNativeTerrainPhysicsWorld.LivingEntity>(probe.Bodies.Length);
                var frames = new List<Frame>(probe.Frames + 1);
                var actionReturns = new List<int>();
                var dimensionResults = new List<DimensionResult>();
                var correctionResults = new List<CorrectionResult>();
                var errors = new List<Exception>();
                try
                {
                    foreach (var input in probe.Bodies)
                    {
                        Console.WriteLine($"  create {input.Name} id={nextId} at({string.Join(',', input.Position)}).");
                        bodies.Add(world.CreateLivingEntity(Request(input), Vector(input.SetupPosition ?? input.Position),
                            Quaternion.Identity, checked((uint)nextId++)));
                    }
                    frames.Add(Capture(-1));
                    for (var i = 0; i < bodies.Count; i++)
                    {
                        var input = probe.Bodies[i];
                        var body = bodies[i];
                        var configured = world.CaptureLivingDiagnosticState(body);
                        if (!body.DimensionsAccepted || !body.DynamicsAccepted ||
                            MathF.Abs(configured.Radius - input.Radius) > .00001f ||
                            MathF.Abs(configured.AxialHalfHeight - MathF.Max(.001f, input.SegmentHalfHeight ?? input.HalfHeight!.Value)) > .00001f ||
                            MathF.Abs(configured.HeightCollider - input.HeightCollider) > .00001f ||
                            MathF.Abs(configured.HeightPivot - input.HeightPivot) > .00001f ||
                            Vector3.Distance(configured.Gravity, new(0, 0, -input.Gravity)) > .00001f ||
                            ((configured.LivingFlags & 8) != 0) != input.Shape.Equals("capsule", StringComparison.OrdinalIgnoreCase) ||
                            configured.SimulationClass != 3 || !configured.InLivingStepList)
                            throw new InvalidOperationException($"Native living body {input.Name} did not accept its dimensions/dynamics or enter the original living simulation list.");
                        if (input.SetupPosition is not null)
                        {
                            var returned = world.SetDiagnosticLivingPose(body, Vector(input.Position), Quaternion.Identity);
                            var positioned = world.CaptureLivingDiagnosticState(body);
                            if (returned == 0 || Vector3.Distance(positioned.RawPosition, Vector(input.Position)) > .001f)
                                throw new InvalidOperationException($"The original position parameters did not place body {input.Name} at its requested initial pose.");
                        }
                        if (!input.CollisionActive)
                        {
                            var returned = world.SetDiagnosticLivingCollisionActive(body, Request(input), false);
                            if (returned == 0 || (world.CaptureLivingDiagnosticState(body).LivingFlags & 0x40) != 0)
                                throw new InvalidOperationException("The original dynamics bActive flag did not disable living collision.");
                        }
                    }
                    frames.Add(Capture(0));
                    for (var frame = 1; frame <= probe.Frames; frame++)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        foreach (var update in probe.DimensionUpdates ?? [])
                            if (update.BeforeFrame == frame)
                            {
                                var index = Array.FindIndex(probe.Bodies, input => input.Name == update.Body);
                                var before = CaptureBody(update.Body, bodies[index], world);
                                var dimensions = update.Dimensions;
                                var request = Request(probe.Bodies[index] with
                                {
                                    Shape = dimensions.Shape, Radius = dimensions.Radius,
                                    HeightCollider = dimensions.HeightCollider, HeightPivot = dimensions.HeightPivot,
                                    SegmentHalfHeight = dimensions.SegmentHalfHeight, HalfHeight = dimensions.HalfHeight
                                });
                                var returned = world.SetDiagnosticLivingDimensions(bodies[index], request, update.ThreadSafe);
                                dimensionResults.Add(new(frame, update.Body, dimensions, update.ThreadSafe, returned, before,
                                    CaptureBody(update.Body, bodies[index], world)));
                                Console.WriteLine($"  dimensions {update.Body} before frame {frame}: native return {returned}.");
                            }
                        foreach (var correction in probe.Corrections ?? [])
                            if (correction.BeforeFrame == frame)
                            {
                                var index = Array.FindIndex(probe.Bodies, input => input.Name == correction.Body);
                                var before = CaptureBody(correction.Body, bodies[index], world);
                                var returned = world.SubmitDiagnosticLivingCorrection(bodies[index], Correction(correction));
                                correctionResults.Add(new(frame, correction.Body, correction, returned, before,
                                    CaptureBody(correction.Body, bodies[index], world)));
                                Console.WriteLine($"  network tuple {correction.Body} before frame {frame}: native return {returned}.");
                            }
                        for (var i = 0; i < bodies.Count; i++)
                            if ((frame == probe.Bodies[i].VelocityStartFrame || probe.SubmitVelocityEachStep && frame >= probe.Bodies[i].VelocityStartFrame) &&
                                probe.Bodies[i].Velocity is { } velocity)
                            {
                                var returned = world.SubmitDiagnosticLivingVelocity(bodies[i], Vector(velocity),
                                    mode: probe.Bodies[i].VelocityMode);
                                actionReturns.Add(returned);
                                if (returned == 0) throw new InvalidOperationException("The original living velocity action was refused.");
                            }
                        Console.WriteLine($"  step {frame}/{probe.Frames}.");
                        world.StepDiagnosticLiving(TimeSpan.FromSeconds(probe.StepSeconds));
                        frames.Add(Capture(frame));
                    }
                    if (probe.MinimumFirstBodyTravel is { } minimum &&
                        Vector3.Distance(Vector(frames.First(frame => frame.Number == 0).Bodies[0].RawPosition),
                            Vector(frames[^1].Bodies[0].RawPosition)) < minimum)
                        throw new InvalidOperationException("The original living step failed the explicit minimum-travel positive control.");
                }
                catch (Exception error) { errors.Add(error); }
                // Every owned body is attempted even if stepping or another retirement fails.
                try { world.SettleForRetirement(); } catch (Exception error) { errors.Add(error); }
                foreach (var body in bodies.AsEnumerable().Reverse())
                    try { body.Dispose(); } catch (Exception error) { errors.Add(error); }
                var diagnostic = errors.Count == 0 ? null : string.Join(Environment.NewLine, errors.Select(error => error.ToString()));
                results.Add(new(probe.Name, errors.Count == 0 ? "completed" : "error", probe,
                    actionReturns.ToArray(), frames.ToArray(), diagnostic, dimensionResults.ToArray(), correctionResults.ToArray()));
                if (errors.Count != 0) throw new AggregateException($"Native living probe {probe.Name} failed.", errors);
                Write(false);

                Frame Capture(int frame) => new(frame, world.NativeSimulationTimeSeconds,
                    bodies.Select((body, index) => CaptureBody(probe.Bodies[index].Name, body, world)).ToArray());
            }
        }
        catch { Write(false); throw; }
        Write(true);
        Console.WriteLine($"native living probes: {results.Count}/{probes.Length} completed; full original living bodies, no pose shortcut.");

        void Write(bool complete)
        {
            var path = outputPath ?? Path.ChangeExtension(inputPath, ".native-living-results.json");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                backend = "native-living", complete, requested = probes.Length,
                bodyProfile = "unbound full PE_LIVING; authored-compatible dimensions; exact world Step and status",
                stepProfile = "original world TimeStep flags8 selects living class3 list; flags0 is not simulation acceptance",
                velocityProfile = "optional original24-byte action type1, explicit mode0 or mode3(default), dt0; no position action or forced correction",
                dimensionProfile = "optional original64-byte type1; actor queued(-1) or explicit immediate(0); +Z unprojection, max(heightPivot,1), zero postpone flag; return and immediate read-back retained independently",
                correctionProfile = "optional exact84-byte network-state tuple type4 at CLiving virtual0xC0/3502AA60; distinct from reset Action4; correction before optional velocity submission and Step; return and immediate read-back retained",
                centerConvention = "input position is entity origin; collider center=origin+(heightCollider-heightPivot)*up",
                results
            }, Json));
        }
    }

    private static BodyState CaptureBody(string name, ZoneNativeTerrainPhysicsWorld.LivingEntity body,
        ZoneNativeTerrainPhysicsWorld world)
    {
        var value = world.CaptureLivingDiagnosticState(body);
        return new(name, body.PhysicalEntityId, Values(value.Status.Position), Values(value.RawPosition),
            Values(value.Status.Velocity), Values(value.RawVelocity), Values(value.RequestedVelocity), Values(value.Gravity),
            value.Status.IsFlying, value.Status.FlyingTime, value.Radius, value.AxialHalfHeight,
            value.HeightCollider, value.HeightPivot, value.EntityFlags, value.LivingFlags, value.PartColliderFlags,
            value.GroundHeight, Values(value.GroundNormal), value.CommandState, value.ContactClearance,
            value.SimulationClass, value.InLivingStepList, value.StepPeriod, value.StepElapsed,
            new(value.Movement.MinSlideAngle, value.Movement.MaxClimbAngle, value.Movement.MaxJumpAngle,
                value.Movement.MinFallAngle, value.Movement.MinSlideCosine, value.Movement.MaxClimbCosine,
                value.Movement.MaxJumpCosine, value.Movement.MinFallCosine, value.Movement.MaximumSupportVelocity,
                value.Movement.SuspensionOffset, value.Movement.SuspensionVelocity,
                value.Movement.SuspensionAcceleration, value.Movement.SuspensionTime,
                value.Movement.HasRetainedSupport, Values(value.Movement.SupportVelocity),
                value.Movement.GeometryRadius, value.Movement.GeometryAxialHalfHeight,
                Values(value.Movement.PrimaryPartOffset), value.Movement.Inertia,
                value.Movement.InertiaAcceleration, value.Movement.AirControl, value.Movement.AirResistance),
            value.VelocityBypassPending);
    }

    internal static ZoneActorMovementCorrectionAction Correction(CorrectionInput input) =>
        new(Vector(input.Position), Vector(input.Velocity), input.OriginalVelocity is { } original ? Vector(original) : null,
            input.SameGround, input.GroundTransition, input.ForcePosition, input.GroundPart, input.GroundPartId);

    internal static ZoneActorPhysicalizationRequest Request(BodyInput input)
    {
        var unused = BitConverter.UInt32BitsToSingle(ZoneActorPhysicalizationRequest.UnusedFloatBits);
        var model = new ZoneActorModelContent(
            [new() { Id = 1, PhysicsLivingMass = input.Mass, PhysicsLivingGravity = input.Gravity,
                PhysicsLivingKAirControl = input.AirControl, PhysicsLivingAirResistance = input.AirResistance,
                PhysicsLivingMinSlideAngle = input.MinSlideAngle ?? (input.UseNativeMovementDefaults ? unused : 45),
                PhysicsLivingMaxClimbAngle = input.MaxClimbAngle ?? (input.UseNativeMovementDefaults ? unused : 60),
                PhysicsLivingMinFallAngle = input.MinFallAngle ?? (input.UseNativeMovementDefaults ? unused : 70),
                PhysicsLivingMaxVelGround = input.MaxVelGround ?? 100,
                PhysicsLivingTimeImpulseRecover = 0 }],
            [new() { Id = 1, ActorModelId = 1, StanceId = 2, SizeX = input.Radius, SizeY = input.Radius,
                SizeZ = input.SegmentHalfHeight ?? input.HalfHeight!.Value,
                HeightCollider = input.HeightCollider, HeightPivot = input.HeightPivot,
                UseCapsule = input.Shape.Equals("capsule", StringComparison.OrdinalIgnoreCase) }]).Models[1];
        var request = ZoneActorPhysicalizationRequest.BuildInitial(model, 1, false, _ => null);
        // Authored MaxVelGround is an integer model column, while the native type4 field is a
        // float. Constructor-default controls must send the actual unused float sentinel.
        if (input.UseNativeMovementDefaults && input.MaxVelGround is null)
            request.Dynamics.Word(0x40, ZoneActorPhysicalizationRequest.UnusedFloatBits);
        if (input.Inertia is { } inertia) request.Dynamics.Single(4, inertia);
        if (input.InertiaAcceleration is { } acceleration) request.Dynamics.Single(8, acceleration);
        return request;
    }

    internal static Input[] Read(string inputPath)
    {
        var probes = JsonSerializer.Deserialize<Input[]>(File.ReadAllText(inputPath), Json) ??
            throw new InvalidDataException("Living probes must be a JSON array.");
        if (probes.Length is < 1 or > 256 || probes.Sum(probe => probe.Frames) > 4096 ||
            probes.Sum(probe => probe.DimensionUpdates?.Length ?? 0) > 256 ||
            probes.Sum(probe => probe.Corrections?.Length ?? 0) > 256)
            throw new InvalidDataException("Living probes require1-256 cases and at most4096 steps.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var probe in probes)
        {
            if (string.IsNullOrWhiteSpace(probe.Name) || !names.Add(probe.Name) ||
                probe.Bodies is not { Length: >= 1 and <= 4 } || probe.Frames is < 1 or > 120 ||
                !float.IsFinite(probe.StepSeconds) || probe.StepSeconds <= 0 || probe.StepSeconds > .1f)
                throw new InvalidDataException("Living cases need unique names,1-4 bodies,1-120 frames and a finite period in(0,0.1].");
            if (probe.MinimumFirstBodyTravel is { } minimum && (!float.IsFinite(minimum) || minimum < 0 || minimum > 100))
                throw new InvalidDataException("A living minimum-travel control must be finite and in[0,100].");
            var bodyNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var body in probe.Bodies)
            {
                if (string.IsNullOrWhiteSpace(body.Name) || !bodyNames.Add(body.Name) ||
                    body.Shape is null || !new[] { "capsule", "cylinder" }.Contains(body.Shape.ToLowerInvariant()) ||
                    !ValidVector(body.Position) || body.Velocity is not null && !ValidVector(body.Velocity) ||
                    body.SetupPosition is not null && !ValidVector(body.SetupPosition) ||
                    !float.IsFinite(body.Radius) || body.Radius <= 0 || body.Radius > 10 ||
                    !float.IsFinite(body.HeightCollider) || MathF.Abs(body.HeightCollider) > 100 ||
                    !float.IsFinite(body.HeightPivot) || MathF.Abs(body.HeightPivot) > 100 ||
                    !float.IsFinite(body.Gravity) || body.Gravity < 0 || body.Gravity > 100 || body.Mass is < 1 or > 10000 ||
                    !float.IsFinite(body.AirControl) || body.AirControl < 0 || body.AirControl > 10 ||
                    !float.IsFinite(body.AirResistance) || body.AirResistance < 0 || body.AirResistance > 100 ||
                    body.VelocityStartFrame < 1 || body.VelocityStartFrame > probe.Frames || body.VelocityMode is not (0 or 3))
                    throw new InvalidDataException($"Living case {probe.Name} has invalid body dimensions, pose or velocity.");
                var capsule = body.Shape.Equals("capsule", StringComparison.OrdinalIgnoreCase);
                var height = capsule ? body.SegmentHalfHeight : body.HalfHeight;
                if (capsule && body.HalfHeight is not null || !capsule && body.SegmentHalfHeight is not null ||
                    height is not { } h || !float.IsFinite(h) || h < 0 || h > 10 || !capsule && h == 0)
                    throw new InvalidDataException($"Living body {body.Name} requires explicit capsule segmentHalfHeight or cylinder halfHeight.");
                foreach (var angle in new[] { body.MinSlideAngle, body.MaxClimbAngle, body.MinFallAngle })
                    if (angle is { } degrees && (!float.IsFinite(degrees) || MathF.Abs(degrees) > 3600))
                        throw new InvalidDataException("Diagnostic movement angles must be finite and within ten rotations; this is a diagnostic bound, not a native clamp.");
                if (body.MaxVelGround is { } supportVelocity && (supportVelocity < 0 || supportVelocity > 1000))
                    throw new InvalidDataException("Diagnostic support velocity must be finite and in[0,1000].");
                foreach (var coefficient in new[] { body.Inertia, body.InertiaAcceleration })
                    if (coefficient is { } number && (!float.IsFinite(number) || number < 0 || number > 1000))
                        throw new InvalidDataException("Diagnostic inertia coefficients must be finite and in[0,1000].");
            }
            foreach (var update in probe.DimensionUpdates ?? [])
            {
                if (update.BeforeFrame < 1 || update.BeforeFrame > probe.Frames || update.ThreadSafe is not (-1 or 0) ||
                    !bodyNames.Contains(update.Body) || update.Dimensions is not { } dimensions ||
                    dimensions.Shape is null || !new[] { "capsule", "cylinder" }.Contains(dimensions.Shape.ToLowerInvariant()) ||
                    !float.IsFinite(dimensions.Radius) || dimensions.Radius <= 0 || dimensions.Radius > 10 ||
                    !float.IsFinite(dimensions.HeightCollider) || MathF.Abs(dimensions.HeightCollider) > 100 ||
                    !float.IsFinite(dimensions.HeightPivot) || MathF.Abs(dimensions.HeightPivot) > 100)
                    throw new InvalidDataException("A dimension update needs an existing body, bounded dimensions and a valid beforeFrame.");
                var capsule = dimensions.Shape.Equals("capsule", StringComparison.OrdinalIgnoreCase);
                var height = capsule ? dimensions.SegmentHalfHeight : dimensions.HalfHeight;
                if (capsule && dimensions.HalfHeight is not null || !capsule && dimensions.SegmentHalfHeight is not null ||
                    height is not { } h || !float.IsFinite(h) || h < 0 || h > 10 || !capsule && h == 0)
                    throw new InvalidDataException("Dimension updates require explicit capsule segmentHalfHeight or cylinder halfHeight.");
            }
            foreach (var correction in probe.Corrections ?? [])
                if (correction.BeforeFrame < 1 || correction.BeforeFrame > probe.Frames ||
                    !bodyNames.Contains(correction.Body) || !ValidVector(correction.Position) ||
                    !ValidVector(correction.Velocity) ||
                    correction.OriginalVelocity is not null && !ValidVector(correction.OriginalVelocity) ||
                    Vector(correction.Velocity).LengthSquared() > 1_000_000 ||
                    correction.OriginalVelocity is { } original && Vector(original).LengthSquared() > 1_000_000)
                    throw new InvalidDataException("A network correction needs an existing body, finite vectors and a valid beforeFrame; velocity is limited to1000m/s.");
        }
        return probes;
    }

    private static bool ValidVector(float[]? value) => value is { Length: 3 } && value.All(float.IsFinite);
    private static Vector3 Vector(float[] value) => new(value[0], value[1], value[2]);
    private static float[] Values(Vector3 value) => [value.X, value.Y, value.Z];
}

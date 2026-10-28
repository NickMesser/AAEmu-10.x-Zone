using System.Numerics;
using AAEmu.Zone.Content;
using AAEmu.Zone.Units;

namespace AAEmu.Zone.Scene;

/// <summary>Copied live model transform, in the engine scene's local coordinates. No Unit pose is inferred.</summary>
public readonly record struct ZoneNpcSkillModelTransform(uint ModelId, Vector3 Position, Quaternion Orientation, float UniformScale);

/// <summary>The authored ActorModel aim sphere after its actual model transform.</summary>
public readonly record struct ZoneNpcSkillAimSphere(Vector3 Center, float Radius);

/// <summary>
/// Source-backed ordinary ActorUnitModel range geometry.39277D00 authors an AimSphere at
/// (0,0,ActorModel.Height), with ActorModel.Radius;39264FE0 updates it from the live transform.
/// It is independent of living collision dimensions and character/render bounds.
/// </summary>
/// <remarks>
/// The admitted profile has one authored sphere, no AddBox and no Big target. Prefab aim helpers,
/// compound aim volumes, partial angular cones, and obstacle rays remain explicit dependencies.
/// </remarks>
public sealed class ZoneManagedNpcSkillGeometry
{
    private readonly ZoneUnitRegistry _units;
    private readonly ZoneModelContent _models;
    private readonly Func<ZoneUnit, ZoneNpcSkillModelTransform> _copyActualModelTransform;

    public ZoneManagedNpcSkillGeometry(ZoneUnitRegistry units, ZoneModelContent models,
        Func<ZoneUnit, ZoneNpcSkillModelTransform> copyActualModelTransform)
    {
        _units = units ?? throw new ArgumentNullException(nameof(units));
        _models = models ?? throw new ArgumentNullException(nameof(models));
        _copyActualModelTransform = copyActualModelTransform ?? throw new ArgumentNullException(nameof(copyActualModelTransform));
    }

    /// <summary>39CE6C20:0 success,14 too close,15 too far,13 invalid height. Unsupported branches throw by name.</summary>
    /// <param name="minimumRange">Already resolved native skill/equipment/modifier minimum.</param>
    /// <param name="maximumRange">Already resolved native skill/equipment/modifier maximum.</param>
    public byte ValidateLocation(ZoneUnit caster, ZoneUnit target, ZoneNpcSkillDefinition skill,
        float minimumRange, float maximumRange)
    {
        ArgumentNullException.ThrowIfNull(skill);
        Finite(minimumRange, nameof(minimumRange)); Finite(maximumRange, nameof(maximumRange));
        Finite(skill.ValidHeight, nameof(skill.ValidHeight));
        var source = Copy(caster); var destination = Copy(target);
        RequireCurrent(caster); RequireCurrent(target);
        if (destination.Model.Big)
            throw new NotSupportedException("Big-target skill location requires original39CE81A0 compound aim-volume validation.");
        var distance = SphereDistance(source.Sphere, destination.Sphere);
        //The positive minimum comparison is strict; the maximum comparison is inclusive.
        if (minimumRange > 0 && distance <= minimumRange) return 14;
        if (distance > maximumRange) return 15;
        if (skill.ValidHeight > 0)
        {
            var height = MathF.Abs((float)((double)source.Sphere.Center.Z - destination.Sphere.Center.Z));
            if (skill.ValidHeightEdgeToEdge)
            {
                height -= destination.Sphere.Radius + source.Sphere.Radius;
                if (height <= 0) height = 0;
            }
            if (height > skill.ValidHeight) return 13;
        }
        if (skill.WeaponAngleSlot != -1)
            throw new NotSupportedException("Skill location requires its resolved equipment/modified target angle.");
        //39259B00 supplies the raw degree value.39CEE2F0 handles the360-degree early return;
        //nonpositive angles bypass that predicate in39CE6C20.
        if (skill.TargetAngle is > 0 and < 360)
            throw new NotSupportedException("Skill location requires original finite-radius partial-angle cone geometry.");
        if (skill.CheckObstacle)
            throw new NotSupportedException("Skill location requires original ClientRaycaster obstacle flags and ignore-owner semantics.");
        return 0;
    }

    /// <summary>3925AF80: minimum of float model-origin distance and signed query-sphere distance to target aim parts.</summary>
    public float Distance(ZoneUnit caster, ZoneUnit target)
    {
        var source = Copy(caster); var destination = Copy(target);
        RequireCurrent(caster); RequireCurrent(target);
        var x = destination.Transform.Position.X - source.Transform.Position.X;
        var y = destination.Transform.Position.Y - source.Transform.Position.Y;
        var z = destination.Transform.Position.Z - source.Transform.Position.Z;
        var centers = MathF.Sqrt((y * y + x * x) + z * z);
        var shapes = SphereDistance(source.Sphere, destination.Sphere);
        if (!float.IsFinite(centers))
            throw new NotSupportedException("Skill origin distance exceeds the native finite float profile.");
        return centers <= shapes ? centers : shapes;
    }

    public ZoneNpcSkillAimSphere CopyAimSphere(ZoneUnit unit) => Copy(unit).Sphere;

    private (ZoneModelDefinition Model, ZoneNpcSkillModelTransform Transform, ZoneNpcSkillAimSphere Sphere) Copy(ZoneUnit unit)
    {
        RequireCurrent(unit);
        var transform = _copyActualModelTransform(unit);
        RequireCurrent(unit);
        if (transform.ModelId != unit.Snapshot.ModelId)
            throw new NotSupportedException("Skill geometry requires the actual admitted model; model transitions are not owned by this profile.");
        if (!_models.Models.TryGetValue(transform.ModelId, out var model) || model.Kind != ZoneModelKind.Actor ||
            _models.ActorForModel(transform.ModelId) is not { } actor)
            throw new NotSupportedException($"Skill geometry model {transform.ModelId} has no resolved ActorUnitModel aim-volume producer.");
        if (actor.Definition.AddBox)
            throw new NotSupportedException("Skill geometry requires its authored ActorModel.AddBox target and enclosing query-sphere construction.");
        return (model, transform, TransformActorSphere(actor.Definition, transform));
    }

    /// <summary>39CE6650/3929D760 operation order, including raw quaternion and float world-center narrowing.</summary>
    public static ZoneNpcSkillAimSphere TransformActorSphere(ZoneActorModelDefinition actor, ZoneNpcSkillModelTransform transform)
    {
        ArgumentNullException.ThrowIfNull(actor);
        Finite(actor.Height, nameof(actor.Height)); Finite(actor.Radius, nameof(actor.Radius));
        Finite(transform.UniformScale, nameof(transform.UniformScale));
        if (actor.Radius < 0 || transform.UniformScale <= 0)
            throw new NotSupportedException("Actor aim geometry requires a nonnegative authored radius and positive actual uniform scale.");
        var p = transform.Position; var q = transform.Orientation;
        Finite(p.X, nameof(transform.Position)); Finite(p.Y, nameof(transform.Position)); Finite(p.Z, nameof(transform.Position));
        Finite(q.X, nameof(transform.Orientation)); Finite(q.Y, nameof(transform.Orientation));
        Finite(q.Z, nameof(transform.Orientation)); Finite(q.W, nameof(transform.Orientation));
        var z = (double)actor.Height * transform.UniformScale;
        var tx = (float)((double)q.Y * z);
        var ty = (float)(-(double)q.X * z);
        var tz = (float)((double)q.W * z);
        var cx = q.Y * tz - q.Z * ty;
        var cy = q.Z * tx - q.X * tz;
        var cz = q.X * ty - q.Y * tx;
        var local = new Vector3((float)((double)cx + cx), (float)((double)cy + cy), (float)((double)cz + z + cz));
        var center = new Vector3((float)((double)local.X + p.X), (float)((double)local.Y + p.Y), (float)((double)local.Z + p.Z));
        var radius = transform.UniformScale * actor.Radius;
        Finite(center.X, nameof(center)); Finite(center.Y, nameof(center)); Finite(center.Z, nameof(center)); Finite(radius, nameof(radius));
        return new(center, radius);
    }

    /// <summary>Signed distance39D464F0. Overlapping spheres produce negative distances.</summary>
    public static float SphereDistance(ZoneNpcSkillAimSphere source, ZoneNpcSkillAimSphere target)
    {
        var x = (double)source.Center.X - target.Center.X;
        var y = (double)source.Center.Y - target.Center.Y;
        var z = (double)source.Center.Z - target.Center.Z;
        var distance = (float)(Math.Sqrt(x * x + y * y + z * z) - (source.Radius + target.Radius));
        Finite(distance, nameof(distance)); return distance;
    }

    private void RequireCurrent(ZoneUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (!_units.IsCurrent(unit)) throw new InvalidOperationException("The skill geometry Unit allocation is retired or belongs to another registry.");
    }

    private static void Finite(float value, string name)
    {
        if (!float.IsFinite(value)) throw new NotSupportedException($"Skill geometry {name} must be finite and representable.");
    }
}

using AAEmu.Zone.Content;

namespace AAEmu.Zone.Scripting;

internal readonly record struct ZoneNpcRegisteredRadii(float PassRadius, float PathRadius);

/// <summary>392AEA74..392AEC3D's ordinary registered-entity/ActorModel branch.</summary>
internal static class ZoneNpcMovementRadiusMath
{
    internal static ZoneNpcRegisteredRadii Resolve(float passRadius, float pathRadius, float scale,
        ZoneModelDefinition? model, ZoneActorModel? actor)
    {
        if (!float.IsFinite(passRadius) || !float.IsFinite(pathRadius) || !float.IsFinite(scale) || scale <= 0)
            throw new NotSupportedException("NPC radius registration requires finite authored radii and its positive actual uniform model scale.");
        if (passRadius < 0)
        {
            if (model?.Kind != ZoneModelKind.Actor || actor is null || model.SubtypeId != actor.Id)
                throw new NotSupportedException("Negative NPC passRadius requires its actual resolved ActorModel; AimBox fallback is a separate profile.");
            passRadius = scale * actor.Definition.Radius;
            //392ADBC0 stops at the FIRST ID2, otherwise returns the LAST actual row.
            //Do not substitute the CActor stance-slot catalog's last-duplicate-wins map.
            var stance = actor.Stances.FirstOrDefault(value => value.StanceId == 2) ?? actor.Stances.LastOrDefault();
            if (stance?.UseCapsule == true)
            {
                if (!float.IsFinite(stance.SizeX) || !float.IsFinite(stance.SizeY))
                    throw new NotSupportedException("NPC capsule pass-radius fallback requires finite authored stance dimensions.");
                var maximum = stance.SizeY > stance.SizeX ? stance.SizeY : stance.SizeX;
                passRadius = scale * maximum;
            }
        }
        else passRadius = scale * passRadius; //The admitted scene owns an actual entity (v992 nonnull).
        if (pathRadius < 0) pathRadius = passRadius;
        //392655C0 copies ModelDesc.Big(bit2) to UnitModel697.39ADC040 loads
        //ForceNavigateAroundObject into descriptor144 bit8. Both source gates are required.
        if (model is { Big: true, ForceNavigateAroundObject: true }) pathRadius *= 1.1f;
        if (!float.IsFinite(passRadius) || passRadius < 0 || !float.IsFinite(pathRadius) || pathRadius < 0)
            throw new NotSupportedException("NPC radius registration produced an unsupported negative or nonfinite physical/navigation radius.");
        return new(passRadius, pathRadius);
    }
}

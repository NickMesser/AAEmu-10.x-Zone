using AAEmu.Zone.Content;

namespace AAEmu.Zone.Scene;

public sealed partial class ZoneManagedNpcActorMotion
{
    /// <summary>The admitted initial-character CActor164 value, scaled by its actual entity scale.</summary>
    internal float CopyNavigationSwimThreshold(ZoneManagedNpcActor actor)
    {
        var owner = Require(actor);
        if (_characters is null || _assets is null)
            throw new NotSupportedException("Navigation water clearance requires the actual admitted character swim-threshold producer.");
        var character = _characters.Find(actor.Handle) ??
            throw new NotSupportedException("Navigation water clearance requires this actor's actual character slot0.");
        if (character.ActorModelId != owner.Body.Admission.Request.ActorModel.Id || !character.HasSlot(0))
            throw new InvalidOperationException("Navigation swim threshold belongs to a different actor/model generation.");
        var definition = character.Definition;
        if (!_thresholds.TryGetValue(definition.SkeletonAssetName, out var threshold))
        {
            threshold = ZoneCharacterInitialSwimThreshold.Load(_assets, definition);
            RequireDependencies(owner);
            _thresholds.Add(definition.SkeletonAssetName, threshold);
        }
        return threshold.Height * owner.Construction.EntityScale;
    }
}

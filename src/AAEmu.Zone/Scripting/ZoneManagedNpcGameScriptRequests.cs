using AAEmu.Zone.Protocol;
using AAEmu.Zone.Protocol.Messages;
using AAEmu.Zone.Scene;
using AAEmu.Zone.Units;

namespace AAEmu.Zone.Scripting;

/// <summary>
/// Actual copied ServerUnit cast+32280, current skill+32284 and channel+32288 fields.
/// The source is required: the existing ZoneSkillState cast dictionary alone does not retain
/// the independent native channel timeline after SkillEnded and cannot substitute for this tuple.
/// </summary>
public readonly record struct ZoneNpcCastingRequestState(uint CastTimeline, uint CurrentSkill, uint ChannelTimeline);

/// <summary>
/// Source-backed Lua requests to World. These never optimistically remove local buffs/casts;
/// their existing WZ acknowledgment consumers remain authoritative. Cast fields must come
/// from an actual owner, not inferred solely from a skill's Casting boolean.
/// </summary>
public sealed class ZoneManagedNpcGameScriptRequests
{
    private readonly ZoneUnitRegistry _units;
    private readonly ZoneSceneNpcCatalog _catalog;
    private readonly Action<ZoneFrame> _send;
    private readonly Func<ZoneManagedNpcActor, ZoneNpcCastingRequestState> _castFields;
    public ZoneManagedNpcGameScriptRequests(ZoneUnitRegistry units, ZoneSceneNpcCatalog catalog,
        Action<ZoneFrame> send, Func<ZoneManagedNpcActor, ZoneNpcCastingRequestState> castFields)
    {
        _units = units ?? throw new ArgumentNullException(nameof(units)); _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _send = send ?? throw new ArgumentNullException(nameof(send)); _castFields = castFields ?? throw new ArgumentNullException(nameof(castFields));
    }

    /// <summary>39353020: request template removal with invalid instance0/reason10 and return Lua true.</summary>
    public bool RemoveBuff(ZoneManagedNpcActor actor, uint buffType)
    {
        Require(actor);
        _send(new BuffRemovalRequest(actor.Npc.ObjectId, 0, buffType, 10).ToFrame());
        return true;
    }

    /// <summary>
    ///39351050 -> ServerAIUnitSystem+112/393259F0. Send current skill first (timeline0),
    ///then the cast timeline, using the channel timeline only if the cast timeline is0.
    ///No flags, timelines or local controllers are cleared by this request leaf.
    /// </summary>
    public void StopCasting(ZoneManagedNpcActor actor)
    {
        Require(actor); var fields = _castFields(actor); Require(actor);
        if (fields.CurrentSkill != 0) _send(StopCastingFrame(actor.Npc.ObjectId, 0, fields.CurrentSkill));
        Require(actor);
        var timeline = fields.CastTimeline != 0 ? fields.CastTimeline : fields.ChannelTimeline;
        if (timeline != 0) _send(StopCastingFrame(actor.Npc.ObjectId, timeline, 0));
    }

    /// <summary>ZWStopCasting serializer39368F40: World Unit bc, uint32 timeline, uint32 skill.</summary>
    public static ZoneFrame StopCastingFrame(uint objectId, uint timeline, uint skillId)
    {
        var writer = new ZoneWriter(); writer.WriteBc(objectId); writer.WriteUInt32(timeline); writer.WriteUInt32(skillId);
        return new(ZwOpcodes.StopCasting, writer.ToArray());
    }

    private void Require(ZoneManagedNpcActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (!actor.HasActor || !ReferenceEquals(_catalog.Find(actor.Handle), actor.Npc) ||
            !_units.Units.TryGetValue(actor.Npc.ObjectId, out var unit) || !ReferenceEquals(unit, actor.Npc.Unit))
            throw new InvalidOperationException("NPC script requests require the exact current actor, NPC and World Unit generation.");
    }
}

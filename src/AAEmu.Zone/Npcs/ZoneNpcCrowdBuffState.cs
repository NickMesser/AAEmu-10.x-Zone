using System.Numerics;
using AAEmu.Zone.Content;
using AAEmu.Zone.Protocol;
using AAEmu.Zone.Protocol.Messages;
using AAEmu.Zone.Units;

namespace AAEmu.Zone.Npcs;

/// <summary>Required live inputs around the native NPC crowd request state.</summary>
public interface IZoneNpcCrowdBuffScene
{
    /// <summary>
    /// Read-only ownership lookup: return npc.Unit only while this exact NPC and unit remain registered.
    /// Return null for a retired owner, including one whose numeric IDs have been reused.
    /// </summary>
    ZoneUnit? GetBoundUnit(ZoneNpc npc);
    NpcTemplateDefinition? FindTemplate(uint templateId);
    /// <summary>
    /// 393417C0: resolve the native NPC ID, its unit, entity, AI object and Puppet interface.
    /// Null represents any missing link. A generic PipeUser has no Puppet interface.
    /// The value is Puppet m_Alertness (+1040), not a guessed combat or target flag.
    /// </summary>
    int? GetPuppetAlertness(uint npcId);
    Vector3 GetPosition(ZoneUnit unit);
    /// <summary>
    /// 39325250, spatial category 1: return the native traversal order, including the owner if present.
    /// The query uses strict three-dimensional distance squared &lt; radius squared. Do not prefilter
    /// faction, dead units, missing actors or duplicates; the crowd routine evaluates those afterwards.
    /// </summary>
    IReadOnlyList<ZoneUnit> QueryNpcs(Vector3 position, float radius);
    // Live GameExpr scalar IDs 5/6 (+21C/+220), read in this order:
    // monster_crowd_effect_num and monster_crowd_effect_range in enum_constant_kinds.
    float RequiredCount { get; }
    float Radius { get; }
}

/// <summary>
/// NPC::UpdateCrowdBuff (39347060). Owns the request latch at NPC+71, initialized false by 39345000.
/// This neither applies nor removes buffs: WZ replies own those transitions. Keep one instance per
/// NPC lifetime and discard it when that owner is removed; construction does not infer a latch from buffs.
/// </summary>
public sealed class ZoneNpcCrowdBuffState
{
    // Native constants 3A518F08/0C, independently verified against the configured decrypted content:
    // const_system_faction_types.monster and const_buff_types.monster_crowd_effect.
    public const uint MonsterFactionId = 115;
    public const uint CrowdBuffTemplateId = 4835;
    private readonly ZoneNpc _npc;
    private readonly IZoneNpcCrowdBuffScene _scene;
    private readonly Action<ZoneFrame> _send;

    public bool Requested { get; private set; }

    public ZoneNpcCrowdBuffState(ZoneNpc npc, IZoneNpcCrowdBuffScene scene, Action<ZoneFrame> send)
    {
        _npc = npc ?? throw new ArgumentNullException(nameof(npc));
        _scene = scene ?? throw new ArgumentNullException(nameof(scene));
        _send = send ?? throw new ArgumentNullException(nameof(send));
    }

    /// <summary>
    /// Called on the scene thread in the living NPC's every-64-frame crowd phase. The sender enqueues
    /// the request; native writes the latch after sending and does not wait for World acknowledgement.
    /// </summary>
    public bool Update()
    {
        var unit = _scene.GetBoundUnit(_npc);
        var template = _scene.FindTemplate(_npc.TemplateId);
        if (unit is null || !IsCurrent(unit) || template is null ||
            unit.FactionId != MonsterFactionId || !template.CrowdEffect)
        {
            Requested = false;
            return false;
        }

        var alertness = _scene.GetPuppetAlertness(_npc.Id);
        if (!IsCurrent(unit)) return Retire();
        if (alertness != 2)
        {
            if (unit.Buffs.Good.Concat(unit.Buffs.Bad).Concat(unit.Buffs.Hidden)
                .Any(buff => buff.TemplateId == CrowdBuffTemplateId))
                Remove(unit);
            Requested = false;
            return false;
        }

        var required = ToNativeCount(_scene.RequiredCount);
        var radius = _scene.Radius;
        if (!IsCurrent(unit)) return Retire();
        var position = _scene.GetPosition(unit);
        if (!IsCurrent(unit)) return Retire();
        var candidates = _scene.QueryNpcs(position, radius);
        if (!IsCurrent(unit)) return Retire();
        var limit = unchecked(required - 1);
        var count = 0;
        // Native skips all actor lookups when the unfiltered spatial result is already too short.
        if (candidates.Count > limit)
        {
            foreach (var candidate in candidates)
            {
                if (candidate.FactionId != MonsterFactionId) continue;
                var candidateAlertness = _scene.GetPuppetAlertness(candidate.Snapshot.Identity.NpcObjectId);
                if (!IsCurrent(unit)) return Retire();
                if (candidateAlertness == 2 && !candidate.IsDead)
                {
                    count++;
                    if (count > limit) break;
                }
            }
        }
        if (!Requested && count > limit)
        {
            _send(new BuffCreationRequest(unit.Snapshot.ObjectId, CrowdBuffTemplateId).ToFrame());
            Requested = IsCurrent(unit);
        }
        else if (Requested && count < required)
        {
            Remove(unit);
            Requested = false;
        }
        return Requested;
    }

    // Managed callback policy: retire pending work when a scene callback replaces/removes this owner.
    // Native synchronous callback removal is not established; ordinary native transitions are unchanged.
    private bool IsCurrent(ZoneUnit unit) => ReferenceEquals(unit, _npc.Unit) &&
        ReferenceEquals(_scene.GetBoundUnit(_npc), unit);

    private bool Retire() { Requested = false; return false; }

    private void Remove(ZoneUnit unit) =>
        _send(new BuffRemovalRequest(unit.Snapshot.ObjectId, 0, CrowdBuffTemplateId, 10).ToFrame());

    private static int ToNativeCount(float value) =>
        // CVTTSS2SI32: invalid conversions produce INT_MIN; the subsequent subtraction wraps.
        !float.IsFinite(value) || value >= 2147483648f || value < -2147483648f ? int.MinValue : (int)value;
}

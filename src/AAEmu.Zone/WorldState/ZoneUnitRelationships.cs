using AAEmu.Zone.Content;
using AAEmu.Zone.Protocol.Units;
using AAEmu.Zone.Units;

namespace AAEmu.Zone.WorldState;

/// <summary>
/// 3932C4B0 / 39CC17B0, specialized to the native ServerUnit vtable at 39FE54D8.
/// Reads current state on every query. Scene and siege services are required, not defaulted.
/// </summary>
public sealed class ZoneUnitRelationships
{
    private const uint BattlefieldParticipantFactionId = 186; // native 3A5A30A8
    private const ushort ExpeditionWarExcludedGroupId = 49; // native 3A5A30AC, arche_mall
    private const uint RetributionBuffId = 2167; // native 3A5A38DC; not buffs.crime
    private readonly ZoneUnitRegistry _units;
    private readonly ZoneFactionState _factions;
    private readonly ZoneCombatRelations _overrides;
    private readonly ZoneNpcContent _npcs;
    private readonly ZoneBuffContent _buffs;
    private readonly bool _useBaseFaction;
    private readonly IZoneRelationshipWorld _world;

    public ZoneUnitRelationships(ZoneUnitRegistry units, ZoneFactionState factions, ZoneCombatRelations overrides,
        ZoneNpcContent npcs, ZoneBuffContent buffs, bool useBaseFaction, IZoneRelationshipWorld world)
    {
        _units = units ?? throw new ArgumentNullException(nameof(units));
        _factions = factions ?? throw new ArgumentNullException(nameof(factions));
        _overrides = overrides ?? throw new ArgumentNullException(nameof(overrides));
        _npcs = npcs ?? throw new ArgumentNullException(nameof(npcs));
        _buffs = buffs ?? throw new ArgumentNullException(nameof(buffs));
        _useBaseFaction = useBaseFaction; // WZJoinResponse.Battlefield selects 39CC0770.
        _world = world ?? throw new ArgumentNullException(nameof(world));
    }

    public ZoneRelation GetRelation(uint sourceId, uint targetId) =>
        GetRelation(_units.Units.GetValueOrDefault(sourceId), _units.Units.GetValueOrDefault(targetId));

    public ZoneRelation GetRelation(ZoneUnit? source, ZoneUnit? target)
    {
        if (!IsValid(source) || !IsValid(target)) return default;
        if (IsGmFriendly(source!) || IsGmFriendly(target!)) return new(ZoneRelationState.Friendly, false);
        var first = source!;
        var second = target!;
        var firstFaction = EffectiveFaction(first);
        var secondFaction = EffectiveFaction(second);
        var firstOwner = OwnerCharacterId(first);
        var secondOwner = OwnerCharacterId(second);
        var result = _overrides.GetOverride(firstOwner, firstFaction, secondOwner, secondFaction);
        if (result.State == ZoneRelationState.None) result = ConflictRelation(first, second, firstOwner, secondOwner);
        if (result.State == ZoneRelationState.None) result = _factions.GetRelation(firstFaction, secondFaction);

        var area = _world.Area;
        // 39CC0A80 precedes war and duels, but remains subject to subsequent attack restrictions.
        if (firstFaction == BattlefieldParticipantFactionId && secondFaction == BattlefieldParticipantFactionId)
            result = new(firstOwner == secondOwner ? ZoneRelationState.Friendly : ZoneRelationState.Hostile,
                firstOwner != secondOwner, 13);
        else if (IsExpeditionWar(first, second, firstOwner, secondOwner, area) && area?.Integration != true)
            result = new(ZoneRelationState.Hostile, true, 11);
        else if (result.State != ZoneRelationState.Hostile && IsOpposingDuel(first, second, firstOwner, secondOwner))
            result = new(ZoneRelationState.Hostile, true, 7);

        var siege = _world.GetSiegeRelation(first, second);
        if (siege.State != ZoneRelationState.None) result = siege;

        var firstKind = first.Snapshot.Identity.Kind;
        var secondKind = second.Snapshot.Identity.Kind;
        if (firstKind == ZoneUnitKind.Npc)
        {
            if (secondKind == ZoneUnitKind.Npc) return result;
            // Any stored aggro entry is sufficient, including suppressed/abuser entries.
            if (_units.Npcs?.Npcs.GetValueOrDefault(first.Snapshot.Identity.NpcObjectId)?.Aggro.Find(second.Snapshot.ObjectId) is not null)
                return result with { CanAttack = true, Reason = 0 };
            return IsProtectedGuard(first, area) ? result with { CanAttack = false, Reason = 9 } : result;
        }
        if (secondKind == ZoneUnitKind.Npc)
        {
            // 39CC0B60 uses the NPC's base faction, not its effective expedition faction.
            if (area is { FactionId: not 0 } && second.FactionId != 0 &&
                _factions.GetRelation(second.FactionId, area.FactionId).State == ZoneRelationState.Friendly)
                return result with { CanAttack = false, Reason = 1 };
            return IsProtectedGuard(second, area) ? result with { CanAttack = false, Reason = 10 } : result;
        }
        if (firstOwner == 0 || secondOwner == 0 || result.Reason is 7 or 11) return result;
        if (_world.BlockOwnedUnitCombat) return result with { CanAttack = false, Reason = 2 };

        // ServerUnit slots 10/58/60/A0 are constant zero; Zone's peace-faction callback
        // 3932C2C0 is also zero. Generic invulnerability, team and peace-level branches
        // therefore do not apply to this native server specialization.
        var forceAttack = first.ForceAttack && secondKind is ZoneUnitKind.Character or ZoneUnitKind.Slave
            or ZoneUnitKind.Housing or ZoneUnitKind.Mate or ZoneUnitKind.Shipyard;
        if (result.State == ZoneRelationState.Hostile)
            return result with { CanAttack = result.Reason == 8 || forceAttack, Reason = 0 };
        if (result.State is not (ZoneRelationState.Friendly or ZoneRelationState.Neutral)) return result;
        if (firstOwner == secondOwner) return result with { CanAttack = false, Reason = 3 };
        return result with { CanAttack = HasRetribution(second) || forceAttack, Reason = 0 };
    }

    public static long OwnerCharacterId(ZoneUnit unit) => unit.Snapshot.Identity.Kind switch
    {
        ZoneUnitKind.Character => unit.Snapshot.Identity.PersistentId,
        ZoneUnitKind.Npc when unit.Snapshot.Identity.OwnerId > 999 => unit.Snapshot.Identity.OwnerId,
        ZoneUnitKind.Slave or ZoneUnitKind.Mate => unit.Snapshot.Identity.OwnerId,
        _ => 0
    };

    private static bool IsValid(ZoneUnit? unit) => unit is not null &&
        ((unit.Snapshot.Identity.Kind == ZoneUnitKind.Character && unit.Snapshot.Identity.PersistentId != 0) ||
         unit.FactionId != 0 || unit.ExpeditionId != 0);

    private static bool IsGmFriendly(ZoneUnit unit) => unit.Snapshot.Identity.Kind == ZoneUnitKind.Character &&
        unit.GmModes[6] != 0;

    private uint EffectiveFaction(ZoneUnit unit) => !_useBaseFaction && !unit.TemporaryFaction && unit.ExpeditionId != 0
        ? unit.ExpeditionId : unit.FactionId;

    private ZoneRelation ConflictRelation(ZoneUnit first, ZoneUnit second, long firstOwner, long secondOwner)
    {
        // Both ServerUnits report the same current scene key. The conflict group's chaos
        // definition is checked independently; it need not equal the units' area group here.
        if (firstOwner == 0 || secondOwner == 0 || _world.ZoneKey == -1 || _world.Area is not { GroupId: not 0 } ||
            _world.ConflictZoneGroupId == 0 || !_world.IsWarChaos(_world.ConflictZoneGroupId) || _world.ConflictState != 6)
            return default;
        var firstRoot = _factions.RootFactionId(first.FactionId);
        var secondRoot = _factions.RootFactionId(second.FactionId);
        if (firstRoot == 0 || secondRoot == 0) return default;
        return new(firstRoot == secondRoot ? ZoneRelationState.Friendly : ZoneRelationState.Hostile, firstRoot != secondRoot, 12);
    }

    private bool IsExpeditionWar(ZoneUnit first, ZoneUnit second, long firstOwner, long secondOwner, ZoneRelationshipArea? area) =>
        (area?.GroupId ?? 0) != ExpeditionWarExcludedGroupId && !_world.IsBattlefield &&
        !_world.IsWarChaos(area?.GroupId ?? 0) && firstOwner != 0 && secondOwner != 0 &&
        first.ExpeditionId != 0 && second.ExpeditionId != 0 &&
        first.ExpeditionWarFactionId == second.ExpeditionId && second.ExpeditionWarFactionId == first.ExpeditionId;

    private bool IsOpposingDuel(ZoneUnit first, ZoneUnit second, long firstOwner, long secondOwner)
    {
        var firstDuel = DuelOwner(first);
        var secondDuel = DuelOwner(second);
        return firstDuel is { DuelObjectId: not (0 or uint.MaxValue) } && secondDuel is not null &&
            firstDuel.DuelObjectId == secondDuel.DuelObjectId && firstOwner != secondOwner && firstDuel.DuelTeam != secondDuel.DuelTeam;
    }

    private ZoneUnit? DuelOwner(ZoneUnit unit) => unit.Snapshot.Identity.Kind switch
    {
        ZoneUnitKind.Character => unit,
        ZoneUnitKind.Mate => FindCharacter(OwnerCharacterId(unit)),
        _ => null
    };

    private ZoneUnit? FindCharacter(long id) => id == 0 ? null : _units.Units.Values.FirstOrDefault(unit =>
        unit.Snapshot.Identity.Kind == ZoneUnitKind.Character && unit.Snapshot.Identity.PersistentId == id);

    private bool HasRetribution(ZoneUnit unit)
    {
        var owner = unit.Snapshot.Identity.Kind switch
        {
            ZoneUnitKind.Character => unit,
            ZoneUnitKind.Mate or ZoneUnitKind.Slave => FindCharacter(OwnerCharacterId(unit)),
            _ => null
        };
        return owner is not null && _buffs.Templates.ContainsKey(RetributionBuffId) &&
            owner.Buffs.Good.Concat(owner.Buffs.Bad).Concat(owner.Buffs.Hidden).Any(buff => buff.TemplateId == RetributionBuffId);
    }

    private bool IsProtectedGuard(ZoneUnit unit, ZoneRelationshipArea? area) => area is not null &&
        _npcs.Templates.TryGetValue(unit.Snapshot.Identity.TemplateId, out var npc) &&
        npc.AcceptAggroLink && npc.AggroLinkSpecialGuard && _world.IsGuardProtectionActive(area.GroupId);
}

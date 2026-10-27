using AAEmu.Zone.Protocol;
using AAEmu.Zone.Protocol.Messages;
using AAEmu.Zone.Protocol.Units;

namespace AAEmu.Zone.Simulation;

/// <summary>Connection-owned Dominion/siege state and the native ServerUnit siege relationship callback.</summary>
public sealed class ZoneSiegeState(ushort sceneGroupId)
{
    public DominionData Dominion { get; private set; } = DominionData.Empty(sceneGroupId);
    public ulong NonPvpEnd => unchecked(Dominion.NonPvpStart + Dominion.NonPvpDuration);

    public void Apply(ZoneFrame frame)
    {
        // Complete decoding (including nested counts and the final tail) precedes any state change.
        switch (frame.Opcode)
        {
            case WzOpcodes.DominionData:
                Dominion = DominionData.Read(frame.Body.Span);
                break;
            case WzOpcodes.DominionDeleted:
                DominionDeleted.Read(frame.Body.Span);
                // 3932C840 retains the current group, including one received in DominionData.
                Dominion = DominionData.Empty(Dominion.ZoneGroupId);
                break;
            case WzOpcodes.SiegeState:
                var state = SiegeState.Read(frame.Body.Span);
                // 3932C940: a zero incoming owner retains the current owner.
                Dominion = Dominion with { Rules = state.Rules, Siege = state.Status,
                    OwnerFactionId = state.OwnerFactionId == 0 ? Dominion.OwnerFactionId : state.OwnerFactionId };
                break;
            case WzOpcodes.SiegeMember:
                ChangeMember(SiegeMember.Read(frame.Body.Span));
                break;
            case WzOpcodes.SiegeSecondHalf:
                var secondHalf = SiegeSecondHalf.Read(frame.Body.Span);
                Dominion = Dominion with { Siege = Dominion.Siege with { SecondHalf = secondHalf.SecondHalf } };
                break;
            default:
                throw new InvalidDataException($"Opcode {frame.Opcode:X4} is not a Dominion/siege update.");
        }
    }

    private void ChangeMember(SiegeMember message)
    {
        var siege = Dominion.Siege;
        // The defender wins duplicate faction IDs; offense lookup keeps input order.
        if (siege.Defender.FactionId == message.FactionId)
        {
            Dominion = Dominion with { Siege = siege with { Defender = UpdateRoster(siege.Defender, message) } };
            return;
        }
        for (var i = 0; i < siege.Offense.Count; i++)
        {
            if (siege.Offense[i].FactionId != message.FactionId) continue;
            var offense = siege.Offense.ToArray();
            offense[i] = UpdateRoster(offense[i], message);
            Dominion = Dominion with { Siege = siege with { Offense = Array.AsReadOnly(offense) } };
            return;
        }
    }

    private static SiegeFaction UpdateRoster(SiegeFaction faction, SiegeMember message)
    {
        var members = faction.Members.ToList();
        if (message.Added)
        {
            if (members.Count >= faction.MemberLimit) return faction;
            members.Add(message.CharacterId); // Native addition accepts duplicates and zero IDs.
        }
        else
        {
            var index = members.IndexOf(message.CharacterId);
            if (index < 0) return faction;
            members[index] = members[^1];
            members.RemoveAt(members.Count - 1);
        }
        return faction with { Members = members.AsReadOnly() };
    }

    public ZoneRelation GetRelation(ZoneUnit source, ZoneUnit target)
    {
        if (Dominion.OwnerFactionId == 0 || Dominion.Siege.Period != 3 ||
            !Contains(source.Pose.Position) || !Contains(target.Pose.Position)) return default;
        var first = Participant(source); var second = Participant(target);
        if (first is null || second is null) return default;
        if (first.Value.Index == second.Value.Index) return new(ZoneRelationState.Friendly, false);
        return !Dominion.Siege.SecondHalf && first.Value.Attacker && second.Value.Attacker
            ? new(ZoneRelationState.Neutral, false) : new(ZoneRelationState.Hostile, true);
    }

    private (int Index, bool Attacker)? Participant(ZoneUnit unit)
    {
        var siege = Dominion.Siege;
        if (unit.Snapshot.Identity.Kind == ZoneUnitKind.Housing)
        {
            if (unit.FactionId == siege.Defender.FactionId) return (0, false);
            for (var i = 0; i < siege.Offense.Count; i++)
                if (unit.FactionId == siege.Offense[i].FactionId) return (i + 1, false);
            // The native housing branch leaves the attacker flag false, even for an offense faction.
            return null;
        }
        var characterId = unit.Snapshot.Identity.Kind switch
        {
            ZoneUnitKind.Character => unit.Snapshot.Identity.PersistentId,
            ZoneUnitKind.Slave or ZoneUnitKind.Mate => unit.Snapshot.Identity.OwnerId,
            _ => 0
        };
        if (characterId == 0) return null;
        if (siege.Defender.Members.Contains(characterId)) return (0, false);
        for (var i = 0; i < siege.Offense.Count; i++)
            if (siege.Offense[i].Members.Contains(characterId)) return (i + 1, true);
        return null;
    }

    /// <summary>39695460: horizontal circles use strict bounds, signed radii and native float arithmetic.</summary>
    public bool Contains(UnitWorldPosition position)
    {
        if (Inside(position, Dominion.Position, Dominion.Rules.RadiusSiege)) return true;
        var siege = Dominion.Siege;
        if (siege.Period == 3)
            foreach (var faction in siege.Offense)
                if (Inside(position, faction.Position, Dominion.Rules.RadiusOffenseHq)) return true;
        foreach (var faction in siege.Offense)
            foreach (var building in faction.Buildings)
                if (Inside(position, building.Position, Dominion.Rules.RadiusOffenseHq)) return true;
        return siege.Period == 3 && siege.SecondHalf && Inside(position, siege.Defender.Position, Dominion.Rules.RadiusOffenseHq);
    }

    private static bool Inside(UnitWorldPosition position, UnitWorldPosition center, short radius)
    {
        const float fixedPointScale = 1f / 17592186044416f;
        var x = (float)unchecked(position.FixedX - center.FixedX) * fixedPointScale;
        var y = (float)unchecked(position.FixedY - center.FixedY) * fixedPointScale;
        return x * x + y * y < (float)(radius * radius);
    }
}

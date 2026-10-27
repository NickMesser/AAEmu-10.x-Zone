using System.Collections.ObjectModel;
using AAEmu.Zone.Protocol.Messages;
using AAEmu.Zone.Protocol.Units;

namespace AAEmu.Zone.Simulation;

/// <summary>Connection-owned faction descriptors and dynamic relations, updated in packet order.</summary>
public sealed class ZoneFactionState
{
    private readonly ZoneUnitRegistry _units;
    private readonly bool? _battlefieldZone;
    private readonly Dictionary<uint, ZoneFaction> _factions = [];
    private readonly Dictionary<(uint, uint), ZoneFactionRelation> _relations = [];
    public IReadOnlyDictionary<uint, ZoneFaction> Factions { get; }
    public IReadOnlyDictionary<(uint, uint), ZoneFactionRelation> Relations { get; }

    /// <param name="battlefieldZone">Resolve from game content for the join's ZoneKey. An unknown value prevents independence updates.</param>
    public ZoneFactionState(ZoneUnitRegistry units, IEnumerable<ZoneFaction> factions,
        IEnumerable<ZoneFactionRelation> relations, bool? battlefieldZone = null)
    {
        _units = units;
        _battlefieldZone = battlefieldZone;
        foreach (var faction in factions) _factions[faction.Id] = faction;
        foreach (var relation in relations) StoreRelation(relation);
        Factions = new ReadOnlyDictionary<uint, ZoneFaction>(_factions);
        Relations = new ReadOnlyDictionary<(uint, uint), ZoneFactionRelation>(_relations);
    }

    // 39C94270 / 3932D2B0: exactly one mother hop; the mother need not exist in this map.
    public uint HelpFactionId(uint factionId) => _factions.TryGetValue(factionId, out var faction) && faction.MotherId != 0
        ? faction.MotherId : factionId;

    public bool CanFactionHelp(uint firstId, uint secondId)
    {
        var first = HelpFactionId(firstId);
        return first != 0 && first == HelpFactionId(secondId);
    }

    /// <summary>39C942F0 follows every nonzero mother ID, including a final missing descriptor.</summary>
    public uint RootFactionId(uint factionId)
    {
        var hops = 0;
        while (_factions.TryGetValue(factionId, out var faction) && faction.MotherId != 0)
        {
            // Native code never terminates for a cycle. Reject it instead of hanging the simulation.
            if (++hops > _factions.Count)
                throw new InvalidDataException($"Faction ancestry contains a cycle at ID {factionId}.");
            factionId = faction.MotherId;
        }
        return factionId;
    }

    /// <summary>39C969C0 / 39C95930: base faction relation before unit-specific combat overrides.</summary>
    public ZoneRelation GetRelation(uint firstId, uint secondId)
    {
        if (firstId == 0 || secondId == 0) return default;
        // The equality shortcut precedes ancestry resolution, including for missing descriptors.
        if (firstId == secondId) return new(ZoneRelationState.Friendly, false);
        var first = RootFactionId(firstId);
        var second = RootFactionId(secondId);
        ZoneRelationState state;
        if (first == second) state = ZoneRelationState.Friendly;
        // Native built-in faction constants 3A5A2D84/88/8C are IDs 1/2/3.
        else if (first == 3 || second == 3) state = ZoneRelationState.Hostile;
        else if (first == 2 || second == 2) state = ZoneRelationState.Neutral;
        else if (first == 1 || second == 1) state = ZoneRelationState.Friendly;
        else if (_relations.TryGetValue((Math.Min(first, second), Math.Max(first, second)), out var relation))
            state = (ZoneRelationState)relation.State;
        else state = ZoneRelationState.Neutral;
        return new(state, state == ZoneRelationState.Hostile);
    }

    public void Create(ReadOnlySpan<byte> body)
    {
        var faction = FactionCreated.Read(body).Faction;
        _factions[faction.Id] = faction; // 39C97E90 replaces an existing ID.
        UpdateOwner(faction);
    }

    public bool ChangeSponsor(ReadOnlySpan<byte> body)
    {
        var faction = FactionSponsorChanged.Read(body).Faction;
        var found = _factions.ContainsKey(faction.Id);
        if (found) _factions[faction.Id] = faction; // 39C97BC0 never inserts a missing ID.
        UpdateOwner(faction); // 3932D4F0 updates the owner even when the descriptor was missing.
        return found;
    }

    private void UpdateOwner(ZoneFaction faction)
    {
        if (faction.UnitOwnerType != (byte)ZoneUnitKind.Character) return;
        foreach (var unit in _units.Units.Values)
            if (unit.Snapshot.Identity.Kind == ZoneUnitKind.Character && unit.Snapshot.Identity.PersistentId == faction.OwnerId)
            {
                unit.SetExpedition(faction.Id);
                return;
            }
    }

    public bool Dismiss(ReadOnlySpan<byte> body)
    {
        var id = FactionDismissed.Read(body).FactionId;
        // 39C9DD00 refuses to dismiss a root faction. Pair relations are retained.
        if (!_factions.TryGetValue(id, out var faction) || faction.MotherId == 0) return false;
        _factions.Remove(id);
        foreach (var unit in _units.Units.Values)
            if (unit.ExpeditionId == id) unit.SetExpedition(0);
        return true;
    }

    public bool ChangeRelation(ReadOnlySpan<byte> body)
    {
        var message = FactionRelationChanged.Read(body);
        if (message.State == 0) return false;
        // 3932D5A0 creates a fresh relation; audit identities/names are explicitly reset.
        return StoreRelation(new(message.FirstId, message.SecondId, message.State, message.NextState,
            message.UpdateTime, message.ChangeTime, 0, "", 0, ""));
    }

    private bool StoreRelation(ZoneFactionRelation relation)
    {
        // 39C97890 / constant 3A5A2D90: only dynamic IDs >= 100 enter this map.
        if (relation.FirstId < 100 || relation.SecondId < 100) return false;
        var first = Math.Min(relation.FirstId, relation.SecondId);
        var second = Math.Max(relation.FirstId, relation.SecondId);
        _relations[(first, second)] = relation with { FirstId = first, SecondId = second };
        return true;
    }

    public void ChangeWar(ReadOnlySpan<byte> body)
    {
        var message = ExpeditionWarState.Read(body);
        // 3932D930 visits each expedition in this order, including when their IDs are equal.
        ApplyWar(message.FirstId, message.Started ? message.SecondId : 0, message.Started ? 1u : 0);
        ApplyWar(message.SecondId, message.Started ? message.FirstId : 0, message.Started ? 2u : 0);
    }

    private void ApplyWar(uint expeditionId, uint opponent, uint side)
    {
        foreach (var unit in _units.Units.Values)
            if (unit.ExpeditionId == expeditionId) unit.SetExpeditionWar(opponent, side);
        // No persistent war map: units created later retain their own snapshot state.
    }

    public bool DeclareIndependence(ReadOnlySpan<byte> body)
    {
        var message = FactionIndependence.Read(body);
        if (!_factions.TryGetValue(message.OldFactionId, out var old)) return false;
        if (_battlefieldZone is not { } battlefield)
            throw new NotSupportedException("Faction independence requires the content-derived battlefield classification for this Zone key.");
        _factions[message.NewFactionId] = old with
        {
            Id = message.NewFactionId, MotherId = 0, Name = message.Name,
            CreatedTime = message.CreatedTime, DiplomacyTarget = true
        };
        _factions[old.Id] = old with { MotherId = message.NewFactionId };
        if (!battlefield)
            foreach (var unit in _units.Units.Values.Where(unit => unit.ExpeditionId == old.Id).ToArray())
                _units.SetFaction(unit, message.NewFactionId, false, clearAggro: false);
        return true;
    }
}

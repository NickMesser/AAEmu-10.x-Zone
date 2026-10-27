using System.Collections.ObjectModel;
using AAEmu.Zone.Protocol.Messages;

namespace AAEmu.Zone.WorldState;

/// <summary>Connection-owned character and faction-pair overrides used before base faction evaluation.</summary>
public sealed class ZoneCombatRelations
{
    private readonly Dictionary<long, CharacterCombatRelation> _characters = [];
    private readonly Dictionary<(uint, uint), FactionCombatRelation> _factions = [];
    public IReadOnlyDictionary<long, CharacterCombatRelation> Characters { get; }
    public IReadOnlyDictionary<(uint, uint), FactionCombatRelation> Factions { get; }

    public ZoneCombatRelations()
    {
        Characters = new ReadOnlyDictionary<long, CharacterCombatRelation>(_characters);
        Factions = new ReadOnlyDictionary<(uint, uint), FactionCombatRelation>(_factions);
    }

    public void ApplyCharacterChanges(ReadOnlySpan<byte> body)
    {
        var message = CharacterCombatRelations.Read(body);
        foreach (var entry in message.Entries)
            // 39CC2020 / 39CC12E0: first insertion wins until an explicit state-zero removal.
            if (entry.State == 0) _characters.Remove(entry.CharacterId);
            else _characters.TryAdd(entry.CharacterId, entry);
    }

    public void ApplyFactionChanges(ReadOnlySpan<byte> body)
    {
        var message = FactionCombatRelations.Read(body);
        foreach (var entry in message.Entries)
        {
            // 3932C510 / 39CC20A0 preserve input order; insertion/removal do not sort the pair.
            var key = (entry.FirstId, entry.SecondId);
            if (entry.State == 0) _factions.Remove(key);
            else _factions.TryAdd(key, entry);
        }
    }

    public ZoneRelation GetCharacterRelation(long characterId, uint opposingFactionId)
    {
        // 39CC15A0 rejects zero IDs but compares only the signed character key in its tree.
        if (characterId == 0 || opposingFactionId == 0) return default;
        return _characters.TryGetValue(characterId, out var entry) ? Result(entry.State) : default;
    }

    public ZoneRelation GetFactionRelation(uint firstId, uint secondId)
    {
        if (firstId == 0 || secondId == 0) return default;
        // 39CC17B0 sorts the query before 39CC1710 / 39CC1670; reversed stored rows stay distinct.
        var key = (Math.Min(firstId, secondId), Math.Max(firstId, secondId));
        return _factions.TryGetValue(key, out var entry) ? Result(entry.State) : default;
    }

    /// <summary>First character, reverse character, then canonical faction pair; no base or zone rules.</summary>
    public ZoneRelation GetOverride(long firstCharacterId, uint firstFactionId, long secondCharacterId, uint secondFactionId)
    {
        var result = GetCharacterRelation(firstCharacterId, secondFactionId);
        if (result.State != ZoneRelationState.None) return result;
        result = GetCharacterRelation(secondCharacterId, firstFactionId);
        return result.State != ZoneRelationState.None ? result : GetFactionRelation(firstFactionId, secondFactionId);
    }

    // Both native lookup functions preserve state, permit attack only for state 1 and reset reason to zero.
    private static ZoneRelation Result(byte state) => new((ZoneRelationState)state, state == 1);
}

using System.Collections.ObjectModel;

namespace AAEmu.Zone.Simulation;

/// <summary>
/// The exact NPC scene's diagnostic selection map (native NpcManager+E0).
/// 39343660 inserts these NPC content IDs unconditionally after authored spawner loading;
/// it neither reads the content database nor clears existing entries. Presence enables
/// the logs in 393200D0, 393264D0 and 39340370. Only value 2 enables AI diagnostics in
/// 39346960. These values do not change NPC combat, movement or spawn eligibility.
/// </summary>
public sealed class ZoneNpcDiagnostics
{
    private readonly ZoneNpcSimulation _owner;
    private readonly Dictionary<uint, uint> _modes = [];
    public IReadOnlyDictionary<uint, uint> Modes { get; }
    public bool IsRetired { get; private set; }

    internal ZoneNpcDiagnostics(ZoneNpcSimulation owner)
    {
        _owner = owner;
        Modes = new ReadOnlyDictionary<uint, uint>(_modes);
    }

    public void InitializePostLoad()
    {
        ObjectDisposedException.ThrowIf(IsRetired || _owner.IsStopped, this);
        // Instruction constants at 3934366A..3934368D, independently verified against
        // configured npcs.id: Kraken, Red Dragon and Leviathan, respectively.
        _modes.TryAdd(7607, 1);
        _modes.TryAdd(12411, 2);
        _modes.TryAdd(14915, 2);
    }

    public bool IsEnabled(ZoneNpc npc)
    {
        ArgumentNullException.ThrowIfNull(npc);
        return IsCurrent(npc) && _modes.ContainsKey(npc.TemplateId);
    }

    /// <summary>
    /// Apply the creation-time 39346960 branch to an already adopted exact actor. Postload
    /// itself never revisits existing actors, and an unselected actor's flag is not cleared.
    /// </summary>
    public bool InitializeActor(ZoneNpcActorEntry actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (!actor.IsActive || !IsCurrent(actor.Npc) ||
            !_modes.TryGetValue(actor.Npc.TemplateId, out var mode) || mode != 2) return false;
        actor.AiObject.DiagnosticsEnabled = true;
        return true;
    }

    private bool IsCurrent(ZoneNpc npc) => !IsRetired && !_owner.IsStopped &&
        ReferenceEquals(_owner.Npcs.GetValueOrDefault(npc.Id), npc);

    internal void Retire()
    {
        IsRetired = true;
        _modes.Clear();
    }
}

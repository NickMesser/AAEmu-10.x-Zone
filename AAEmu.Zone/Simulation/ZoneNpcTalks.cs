using AAEmu.Zone.Protocol;
using AAEmu.Zone.Protocol.Messages;

namespace AAEmu.Zone.Simulation;

/// <summary>Native NPC conversation participants and interaction availability (NPC + C0).</summary>
public sealed class ZoneNpcTalks
{
    // 3A518F14 is 9.0f; 392264D0 takes its square root and compares physical distance strictly.
    public const float MaximumDistance = 3f;
    private readonly ZoneNpc _npc;
    private readonly IZoneNpcWorld _world;
    private readonly Func<uint, ZoneUnit?> _findUnit;
    private readonly Action<ZoneFrame> _notify;
    private readonly SortedSet<uint> _participants = [];
    private bool _disposed;
    public bool Interactable { get; private set; } = true;
    public IReadOnlyList<uint> Participants => _participants.ToArray();

    internal ZoneNpcTalks(ZoneNpc npc, IZoneNpcWorld world, Func<uint, ZoneUnit?> findUnit, Action<ZoneFrame> notify)
    { _npc = npc; _world = world; _findUnit = findUnit; _notify = notify; }

    internal void Begin(uint playerObjectId)
    {
        // 39341130 -> 393401B0 does not require a living character or an AI actor here.
        if (!Interactable || _findUnit(playerObjectId) is not { } player ||
            _findUnit(_npc.ObjectId) is not { } owner || !InRange(owner, player)) return;
        var first = _participants.Count == 0;
        _participants.Add(playerObjectId);
        // Registration precedes actor/script work. Missing actor state does not undo registration.
        if (!first) return;
        _world.AiRuntime.CaptureTalkPosition(_npc);
        if (!_disposed && _world.AiRuntime.GetEntityId(player) is { } entityId)
            _npc.AiSignals.Send("OnTalk", data: new(EntityId: entityId));
    }

    internal void End(uint playerObjectId)
    {
        // Explicit World completion and distance cleanup do not emit ZWNPCInteractionEnded.
        if (_participants.Remove(playerObjectId) && _participants.Count == 0) Finish();
    }

    internal void SetInteractable(bool interactable)
    {
        if (Interactable == interactable) return;
        if (!interactable && _participants.Count != 0)
        {
            // 39344F80 -> 39343BC0: notify all participants, clear, finish AI talk, then change status.
            foreach (var id in _participants)
                _notify(new NpcInteractionEnded(id, _npc.ObjectId).ToFrame());
            _participants.Clear();
            Finish();
        }
        // Managed actor callbacks may remove the owner. Do not publish a status for a destroyed actor.
        if (_disposed) return;
        Interactable = interactable;
        _notify(new NpcInteractionStatusChanged(_npc.ObjectId, interactable).ToFrame());
    }

    internal void Update()
    {
        if (_participants.Count == 0 || _findUnit(_npc.ObjectId) is not { } owner) return;
        // 39343CF0 first collects every stale participant, then removes them in unsigned ID order.
        var expired = _participants.Where(id => _findUnit(id) is not { } target || !InRange(owner, target)).ToArray();
        foreach (var id in expired) End(id);
    }

    private bool InRange(ZoneUnit owner, ZoneUnit target) =>
        _world.GetInteractionDistance(owner, target, MaximumDistance) < MaximumDistance;

    private void Finish()
    {
        // 39342EF0 scans the pipe user's queue. Send OnTalkEnd only if that scan cancelled no OnTalk.
        var cancelled = _npc.AiSignals.CancelPendingTalk();
        if (!_disposed && !cancelled) _npc.AiSignals.Send("OnTalkEnd");
    }

    // 393441B0 destroys the participant set without sending notifications or AI signals.
    internal void Dispose() { _disposed = true; _participants.Clear(); }
}

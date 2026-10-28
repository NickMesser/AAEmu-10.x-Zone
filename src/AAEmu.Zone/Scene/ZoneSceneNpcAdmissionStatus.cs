using AAEmu.Zone.Actors;

namespace AAEmu.Zone.Scene;

/// <summary>Actor lifecycle only; a scheduled actor does not imply authored behavior or gameplay readiness.</summary>
public enum ZoneSceneNpcAdmissionPhase { Registered, ActorBound, Scheduled, Refused, Retired, Queued }

/// <summary>Immutable admission observation for one exact NPC generation, with no retained actor or script state.</summary>
public sealed record ZoneSceneNpcAdmissionStatus(ZoneActorHandle Actor, uint NpcId, uint TemplateId,
    ZoneSceneNpcAdmissionPhase Phase, string? Refusal);

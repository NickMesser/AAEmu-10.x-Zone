using AAEmu.Zone.Combat;
using AAEmu.Zone.Content;
using AAEmu.Zone.Protocol;
using AAEmu.Zone.Protocol.Messages;
using AAEmu.Zone.Units;

namespace AAEmu.Zone.Npcs;

public enum NpcCreationResult { Created, Duplicate, UnknownSpawner, UnknownTemplate }

/// <summary>NPC owner identity and spawn context, separate from its ServerUnit object ID.</summary>
public sealed class ZoneNpc
{
    public uint Id => Unit.Snapshot.Identity.NpcObjectId;
    public uint ObjectId => Unit.Snapshot.ObjectId;
    public uint TemplateId => Unit.Snapshot.Identity.TemplateId;
    public ZoneUnit Unit { get; }
    public NpcTemplateDefinition Template { get; }
    public ZoneModelDefinition Model { get; }
    public ZoneNpcActivationState Activation { get; }
    public NpcStateMetadata Spawn { get; }
    public uint SpawnerId { get; internal set; }
    public uint CreatorObjectId { get; }
    public ZoneAggroTable Aggro { get; }
    public bool SharesAggro { get; internal set; } = true;
    public bool AggroEnabled { get; set; } = true;
    public byte UpdatePhase { get; }
    public ZoneNpcTimers Timers { get; }
    public ZoneNpcTalks Talks { get; }
    public ZoneNpcSignals Signals { get; }
    public ZoneNpcAiSignals AiSignals { get; }
    internal ZoneNpcDiagnostics Diagnostics { get; }
    public bool DiagnosticsEnabled => Diagnostics.IsEnabled(this);

    internal ZoneNpc(ZoneUnit unit, NpcStateMetadata spawn, uint creatorObjectId, IZoneAggroWorld world,
        NpcTemplateDefinition template, ZoneModelDefinition model, byte updatePhase, IZoneNpcWorld npcWorld,
        Func<uint, ZoneUnit?> findUnit, Action<ZoneFrame> notify, ZoneNpcSignalContent signalContent, ZoneNpcDiagnostics diagnostics)
    {
        Unit = unit;
        Template = template;
        Model = model;
        Diagnostics = diagnostics;
        // 39345000 caches the resolved model's big flag for this NPC owner generation.
        Activation = new(model.Big);
        Spawn = spawn;
        SpawnerId = spawn.SpawnerId;
        CreatorObjectId = creatorObjectId;
        UpdatePhase = updatePhase;
        Timers = new(() => world.TimeMilliseconds);
        Talks = new(this, npcWorld, findUnit, notify);
        Signals = new(this, signalContent, npcWorld.SignalRuntime, findUnit, notify);
        AiSignals = new(this, npcWorld);
        Aggro = new(ObjectId, Id, world, template.ReturnDistance, template.AbsoluteReturnDistance);
    }
}

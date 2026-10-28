using System.Numerics;
using AAEmu.Zone.Protocol.Messages;

namespace AAEmu.Zone.WorldState;

/// <summary>Zone-side copy of one World doodad. State only: physics and collision belong to OBJ-01.</summary>
public sealed record ZoneDoodad(uint ObjectId, uint TemplateId, uint ModelId, uint BackpackItemId, uint OwnerObjectId,
    uint ParentObjectId, byte AttachPoint, Vector3 Position, short Roll, short Pitch, short Yaw, float Scale,
    uint FuncGroupId, uint GrowingTime, byte OwnerType, uint OwnerDbId, int Data);

/// <summary>
/// Connection-owned doodad map, updated in packet order (native: doodad manager qword_3A607A80,
/// sub_39453AD0 create / sub_39452E00 remove / sub_39453010 phase / sub_39453080 vegetation).
/// Create replaces an existing id; unknown remove/phase ids are counted, never thrown.
/// </summary>
public sealed class ZoneDoodadRegistry
{
    private readonly Dictionary<uint, ZoneDoodad> _doodads = [];
    private readonly HashSet<uint> _cutVegetation = [];

    public IReadOnlyDictionary<uint, ZoneDoodad> Doodads => _doodads;
    public IReadOnlySet<uint> CutVegetation => _cutVegetation;
    public int UnknownRemoves { get; private set; }
    public int UnknownPhaseChanges { get; private set; }
    public int Replaced { get; private set; }

    public void Create(ReadOnlySpan<byte> body)
    {
        var message = DoodadCreate.Read(body);
        if (_doodads.ContainsKey(message.ObjectId)) Replaced++;
        _doodads[message.ObjectId] = new(message.ObjectId, message.TemplateId, message.ModelId, message.BackpackItemId,
            message.OwnerObjectId, message.ParentObjectId, message.AttachPoint, message.Position, message.Roll,
            message.Pitch, message.Yaw, message.Scale, message.FuncGroupId, message.GrowingTime, message.OwnerType,
            message.OwnerDbId, message.Data);
    }

    public void Remove(ReadOnlySpan<byte> body)
    {
        if (!_doodads.Remove(DoodadRemove.Read(body).ObjectId)) UnknownRemoves++;
    }

    public void ChangePhase(ReadOnlySpan<byte> body)
    {
        var message = DoodadChangePhase.Read(body);
        if (_doodads.TryGetValue(message.ObjectId, out var doodad))
            _doodads[message.ObjectId] = doodad with { FuncGroupId = message.FuncGroupId };
        else
            UnknownPhaseChanges++;
    }

    public void CutDownVegetation(ReadOnlySpan<byte> body) => _cutVegetation.Add(VegetationCutdown.Read(body).Id);
}

using System.Numerics;
using AAEmu.Zone.Scripting;

namespace AAEmu.Zone.Simulation;

public interface IZoneNpcSpawnCharacterProximityServices
{
    Vector3 GetModelPosition(ZoneUnit unit);
}

public interface IZoneNpcSpawnProximityServices : IZoneNpcSpawnCharacterProximityServices
{
    /// <summary>Return an owned reference to the NPC's actual entity script table.</summary>
    ZoneLuaReference? GetSpawnScript(ZoneNpc npc);
}

/// <summary>39347470 character proximity and39340E40 admitted-NPC separation.</summary>
public sealed class ZoneNpcSpawnProximity
{
    private readonly ZoneUnitRegistry _units;
    private readonly ZoneNpcSpawnerRegistry _spawners;
    private readonly IZoneNpcSpawnCharacterProximityServices _services;
    private bool _querying;
    private readonly object _generation;
    public ZoneNpcSpawnerRegistration Owner { get; }

    public ZoneNpcSpawnProximity(ZoneUnitRegistry units, ZoneNpcSpawnerRegistration owner,
        IZoneNpcSpawnCharacterProximityServices services)
    {
        _units = units ?? throw new ArgumentNullException(nameof(units));
        Owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _generation = owner.ContentGeneration;
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _spawners = units.Npcs?.Spawners ?? throw new NotSupportedException("Spawn proximity requires the scene's actual spawner registry.");
        if (!_spawners.IsAccepting(owner)) throw new ArgumentException("The proximity owner must belong to this accepting scene.", nameof(owner));
    }

    public bool HasNearbyCharacter(Vector3 position, Func<bool> current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (!IsCurrent(current)) return false;
        Enter();
        try
        {
            var radius = Owner.Content.Definition.TestRadiusPlayer;
            if (!(radius >= .01f)) return false;
            // 39347470 reads the scene index only after the native radius gate. Physical
            // spawner owners are constructed before 39332730's spatial allocation, and
            // forced point attempts bypass this whole query. Never invent an empty index.
            var spatial = _units.Spatial ?? throw new NotSupportedException("Spawn proximity requires the scene's actual unit spatial index.");
            foreach (var unit in spatial.Query(ZoneSpatialCategory.Character, position, radius))
            {
                if (!IsCurrent(current)) return false;
                if (unit.IsDead || !IsCurrentUnit(unit)) continue;
                var actual = _services.GetModelPosition(unit);
                if (!IsCurrent(current)) return false;
                if (IsCurrentUnit(unit) && DistanceSquared(position, actual) < radius * radius) return true;
            }
            return false;
        }
        finally { _querying = false; }
    }

    public bool HasNearbySpawnedNpc(Vector3 position, ref int work, Func<bool> current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (!IsCurrent(current)) return false;
        if (_services is not IZoneNpcSpawnProximityServices scripts)
            throw new NotSupportedException("NPC separation requires actual entity script tables containing spawnPos.");
        Enter();
        try
        {
            var radius = Owner.Content.Definition.TestRadiusNpc;
            //39340E89: NaN traverses all admitted NPCs; unlike the character gate it is not disabled.
            if (radius < .01f) return false;
            // Snapshot identities before calling Lua. New admissions belong to the next query.
            var members = Owner.Members.Select(member => member.ToArray()).ToArray();
            for (var index = 0; index < members.Length; index++)
                foreach (var npc in members[index])
                {
                    if (!IsCurrent(current)) return false;
                    if (!IsCurrentNpc(npc, index)) continue;
                    work = unchecked(work + 1);
                    using var script = scripts.GetSpawnScript(npc);
                    if (!IsCurrent(current)) return false;
                    if (!IsCurrentNpc(npc, index)) continue;
                    if (script is null || !script.IsTable)
                        throw new InvalidOperationException($"NPC {npc.Id} requires its entity script table for spawn separation.");
                    using var spawn = script.Runtime.Get(script, "spawnPos", 5) as ZoneLuaReference;
                    if (!IsCurrent(current)) return false;
                    if (!IsCurrentNpc(npc, index)) continue;
                    var actual = spawn is null ? null : spawn.Runtime.GetVector(spawn);
                    if (!IsCurrent(current)) return false;
                    if (!IsCurrentNpc(npc, index)) continue;
                    // Native leaves its output uninitialized on failure. The managed boundary rejects
                    // the unsupported input instead of manufacturing a position from unrelated state.
                    if (actual is null) throw new InvalidOperationException($"NPC {npc.Id} requires a convertible Lua spawnPos vector.");
                    if (DistanceSquared(position, actual.Value) < radius * radius) return true;
                }
            return false;
        }
        finally { _querying = false; }
    }

    private bool IsCurrent(Func<bool> current) => IsOwned() && current() && IsOwned();
    private bool IsOwned() => _spawners.IsAccepting(Owner) && ReferenceEquals(_generation, Owner.ContentGeneration);
    private bool IsCurrentUnit(ZoneUnit unit) => ReferenceEquals(_units.Units.GetValueOrDefault(unit.Snapshot.ObjectId), unit);
    private bool IsCurrentNpc(ZoneNpc npc, int member) => IsCurrentUnit(npc.Unit) &&
        ReferenceEquals(_spawners.Npcs.Npcs.GetValueOrDefault(npc.Id), npc) && Owner.Members[member].Contains(npc);
    private void Enter()
    {
        if (_querying) throw new InvalidOperationException("The same spawner cannot query proximity recursively.");
        _querying = true;
    }
    private static float DistanceSquared(Vector3 first, Vector3 second)
    {
        var x = first.X - second.X; var y = first.Y - second.Y; var z = first.Z - second.Z;
        return (x * x + y * y) + z * z;
    }
}

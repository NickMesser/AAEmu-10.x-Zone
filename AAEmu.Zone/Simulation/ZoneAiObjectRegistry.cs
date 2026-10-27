using System.Numerics;

namespace AAEmu.Zone.Simulation;

/// <summary>Live object properties read by the native CAISystem nearest-object search.</summary>
public interface IZoneAiObjectSearchCandidate
{
    Vector3 GetPosition();
    bool IsEnabled();
    float GetRadius();
}

/// <summary>
/// The CAISystem object-type index and 31055FB8 nearest-object search. Registrations own index membership;
/// the scene remains responsible for the indexed objects and their actor-specific destruction callbacks.
/// </summary>
public sealed class ZoneAiObjectRegistry
{
    private readonly Dictionary<short, LinkedList<Registration>> _types = [];

    public int Count { get; private set; }

    /// <summary>3108C89C inserts equal keys after existing entries, preserving registration order for distance ties.</summary>
    public Registration Register(short objectType, object target, IZoneAiObjectSearchCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(candidate);
        if (!_types.TryGetValue(objectType, out var entries)) _types.Add(objectType, entries = new LinkedList<Registration>());
        var registration = new Registration(this, objectType, target, candidate);
        registration.Node = entries.AddLast(registration);
        Count++;
        return registration;
    }

    /// <summary>Remove one registration, corresponding to the iterator erase at 311BB750.</summary>
    public bool Unregister(Registration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        if (!ReferenceEquals(registration.Owner, this)) return false;
        var entries = _types[registration.ObjectType];
        entries.Remove(registration.Node!);
        registration.Node = null;
        registration.Owner = null;
        Count--;
        if (entries.Count == 0) _types.Remove(registration.ObjectType);
        return true;
    }

    public void Clear()
    {
        foreach (var entries in _types.Values)
            foreach (var registration in entries)
            {
                registration.Owner = null;
                registration.Node = null;
            }
        _types.Clear();
        Count = 0;
    }

    /// <summary>
    /// Find the nearest matching object strictly between the squared minimum and expanded maximum distances.
    /// Flag 8 includes disabled objects; other flag bits have no effect in this native search.
    /// Object properties are read live. Index membership must remain valid while its callbacks execute.
    /// </summary>
    public object? FindNearest(Vector3 position, int objectType, float maximumDistance,
        float minimumDistance = 0, object? excluded = null, byte flags = 0)
    {
        // The tree lookup truncates to a signed short, but its iteration compares the key with the full integer.
        var key = unchecked((short)objectType);
        if (key != objectType || !_types.TryGetValue(key, out var entries)) return null;
        var minimumSquared = minimumDistance * minimumDistance;
        var maximumSquared = maximumDistance * maximumDistance;
        var bestSquared = 100_000_000f; // DAT_311D8F84: the native initial upper bound also limits the search.
        object? nearest = null;
        for (var node = entries.First; node is not null; node = node.Next)
        {
            var registration = node.Value;
            if (ReferenceEquals(registration.Target, excluded)) continue;
            var candidate = registration.Candidate;
            if ((flags & 8) == 0 && !candidate.IsEnabled()) continue;
            var targetPosition = candidate.GetPosition();
            var x = targetPosition.X - position.X;
            var y = targetPosition.Y - position.Y;
            var z = targetPosition.Z - position.Z;
            var squared = (x * x + y * y) + z * z;
            var radius = candidate.GetRadius();
            var limit = maximumSquared;
            // DAT_311CF47C and COMISS/JBE: NaN also enters the branch which reads the radius twice more.
            if (!(radius < 0.01f))
            {
                var secondRadius = candidate.GetRadius() + maximumDistance;
                var thirdRadius = candidate.GetRadius() + maximumDistance;
                limit = thirdRadius * secondRadius;
            }
            if (squared < bestSquared && minimumSquared < squared && squared < limit)
            {
                nearest = registration.Target;
                bestSquared = squared;
            }
        }
        return nearest;
    }

    /// <summary>An individual index entry. Disposing it removes membership without destroying its scene object.</summary>
    public sealed class Registration : IDisposable
    {
        internal Registration(ZoneAiObjectRegistry owner, short objectType, object target, IZoneAiObjectSearchCandidate candidate)
        { Owner = owner; ObjectType = objectType; Target = target; Candidate = candidate; }

        internal ZoneAiObjectRegistry? Owner { get; set; }
        internal LinkedListNode<Registration>? Node { get; set; }
        internal IZoneAiObjectSearchCandidate Candidate { get; }
        public short ObjectType { get; }
        public object Target { get; }
        public bool IsRegistered => Owner is not null;

        public void Dispose() => Owner?.Unregister(this);
    }
}

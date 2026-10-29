using System.Numerics;
using AAEmu.Zone.Actors;

namespace AAEmu.Zone.Ai;

/// <summary>Copied CPipeUser3115A7F4 path request, including the actual target identity and direction.</summary>
public sealed record ZoneApproachPathSubmission(object Target, Vector3 Position, Vector3 Direction,
    bool AllowDangerousDestination, int ForceBuildingId, float EndTolerance, float EndDistance);

public interface IZoneApproachPathFindServices
{
    bool IsCurrent { get; }
    ZoneApproachPathDecision PathDecision { get; }
    ZoneActorNavigationState Navigation { get; }
    Vector3 GetEntityPosition(object target);
    Vector3 GetTargetDirection(object target);
    int GetTargetType(object target);
    /// <summary>
    /// The actor's real request owner clears CurrentPath/OriginalPath and sets decision Pending,
    /// performs authored graph/scene admission, then publishes its generation/revision-bound decision.
    /// </summary>
    void RequestPath(ZoneApproachPathSubmission submission);
    /// <summary>Queue the actor's actual OnNoPathFound signal, preserving its ordinary proxy scheduling.</summary>
    void SubmitNoPathSignal();
}

/// <summary>
/// 310DBA08 COPPathFind's explicit-target branch used by COPApproach. Named/direction-only/offset modes are separate.
/// The scene owns real search, route adjustments, pending cancellation and publication; this goal never invents a route.
/// </summary>
public sealed class ZoneApproachPathFindGoal(ZoneApproachPathRequest request, IZoneApproachPathFindServices services) : IZoneGoalOperation
{
    private readonly ZoneApproachPathRequest _request = request ?? throw new ArgumentNullException(nameof(request));
    private readonly IZoneApproachPathFindServices _services = services ?? throw new ArgumentNullException(nameof(services));
    private bool _waiting, _disposed;
    private ulong _epoch;

    private bool Active(ulong epoch) => !_disposed && epoch == _epoch && _services.IsCurrent;
    public int Execute()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_services.IsCurrent) return 1;
        var epoch = _epoch;
        if (!_waiting)
        {
            var position = _services.GetEntityPosition(_request.Target);
            if (!Active(epoch)) return 1;
            var direction = _services.GetTargetDirection(_request.Target);
            if (!Active(epoch)) return 1;
            var type = _services.GetTargetType(_request.Target);
            if (!Active(epoch)) return 1;
            _services.RequestPath(new(_request.Target, position, direction, type != 1, -1, _request.EndTolerance, _request.EndDistance));
            if (!Active(epoch)) return 1;
            _waiting = true;
        }
        if (_services.PathDecision == ZoneApproachPathDecision.Pending) return 1;
        var noPath = _services.PathDecision == ZoneApproachPathDecision.NoPath;
        if (noPath || _services.Navigation.CurrentPath.Count == 0) _services.SubmitNoPathSignal();
        if (!Active(epoch)) return 1;
        Reset();
        return noPath ? 3 : 2;
    }
    //310D5E08 resets only waiting/forced-building state. The actor owns request cancellation.
    public void Reset() { ObjectDisposedException.ThrowIf(_disposed, this); _epoch++; _waiting = false; }
    public void Dispose() { _disposed = true; _epoch++; }
}

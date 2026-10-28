using System.Numerics;

namespace AAEmu.Zone.Navigation;

/// <summary>Bound to one exact actor generation. Requests go to its existing movement owner.</summary>
public interface IZoneNavigationLocomotion
{
    bool TryReadPosition(out Vector3 position);
    bool TryRequestVelocity(Vector3 requestedVelocity);
}

public enum ZoneNavigationFollowOutcome { Moving, Complete, Blocked, MissingData, Unsupported, Retired }

/// <summary>
/// Ground-route follower using copied waypoints and fresh scene admission. It only submits
/// horizontal wishes; the existing living controller owns gravity, support, pose and stepping.
/// Speed, waypoint distance and stop policy are explicit replacement inputs, not a claim to
/// native steering/animation parity. Dynamic blockage can be retried or trigger caller replanning.
/// </summary>
public sealed class ZoneNavigationPathFollower : IDisposable
{
    private readonly ZoneNavigationTiledGraph _navigation;
    private readonly ZoneNavigationPathResult _path;
    private readonly IZoneNavigationSceneQueries _queries;
    private readonly IZoneNavigationLocomotion _motion;
    private readonly float _speed, _waypointDistance;
    private int _next;
    private bool _hasVelocity, _updating;
    public bool IsDisposed { get; private set; }
    public ZoneNavigationFollowOutcome Outcome { get; private set; } = ZoneNavigationFollowOutcome.Moving;
    public int NextPointIndex => _next;
    public string? Diagnostic { get; private set; }

    public ZoneNavigationPathFollower(ZoneNavigationTiledGraph navigation, ZoneNavigationPathResult path,
        IZoneNavigationSceneQueries queries, IZoneNavigationLocomotion motion, float speed, float waypointDistance = .1f)
    {
        ArgumentNullException.ThrowIfNull(navigation); ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(queries); ArgumentNullException.ThrowIfNull(motion);
        if (!navigation.IsCurrent(path) || path.Outcome != ZoneNavigationSearchOutcome.Found)
            throw new ArgumentException("Locomotion requires a found route from the current navigation publication.", nameof(path));
        if (!float.IsFinite(speed) || speed <= 0 || !float.IsFinite(waypointDistance) || waypointDistance < 0)
            throw new ArgumentOutOfRangeException(nameof(speed), "Following requires positive finite speed and nonnegative waypoint distance.");
        _navigation = navigation; _path = path; _queries = queries; _motion = motion; _speed = speed; _waypointDistance = waypointDistance;
        navigation.RegisterFollower(this);
    }

    public ZoneNavigationFollowOutcome Update(float seconds)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (!float.IsFinite(seconds) || seconds <= 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        if (_updating) throw new InvalidOperationException("A path follower cannot update recursively.");
        _updating = true;
        try
        {
            if (!_navigation.IsCurrent(_path)) return Stop(ZoneNavigationFollowOutcome.Retired);
            if (!_motion.TryReadPosition(out var position)) { _hasVelocity = false; return Outcome = ZoneNavigationFollowOutcome.Retired; }
            if (IsDisposed || !_navigation.IsCurrent(_path)) return Stop(ZoneNavigationFollowOutcome.Retired);
            if (!Finite(position)) throw new InvalidOperationException("The locomotion owner returned a non-finite actor position.");
            var next = _next;
            while (next < _path.Points.Count && HorizontalDistance(position, _path.Points[next].Position) <= _waypointDistance) next++;
            if (next == _path.Points.Count) { _next = next; return Stop(ZoneNavigationFollowOutcome.Complete); }
            var target = _path.Points[next].Position;
            var admission = _navigation.CheckWalkSegment(position, target, _path.PassRadius, _queries);
            if (IsDisposed || !_navigation.IsCurrent(_path)) return Stop(ZoneNavigationFollowOutcome.Retired);
            if (admission.Outcome != ZoneNavigationSegmentOutcome.Clear)
            {
                Diagnostic = admission.Diagnostic;
                return Stop(admission.Outcome == ZoneNavigationSegmentOutcome.Blocked ? ZoneNavigationFollowOutcome.Blocked :
                    admission.Outcome == ZoneNavigationSegmentOutcome.MissingData ? ZoneNavigationFollowOutcome.MissingData : ZoneNavigationFollowOutcome.Unsupported);
            }
            var x = (double)target.X - position.X; var y = (double)target.Y - position.Y;
            var distance = Math.Sqrt(x * x + y * y); var speed = Math.Min(_speed, distance / seconds);
            var wish = new Vector3((float)(x / distance * speed), (float)(y / distance * speed), 0);
            if (!Finite(wish)) throw new InvalidOperationException("The route cannot produce a finite horizontal movement wish.");
            if (!_motion.TryRequestVelocity(wish)) { _hasVelocity = false; return Outcome = ZoneNavigationFollowOutcome.Retired; }
            if (IsDisposed || !_navigation.IsCurrent(_path)) { _hasVelocity = wish != Vector3.Zero; return Stop(ZoneNavigationFollowOutcome.Retired); }
            _next = next; _hasVelocity = wish != Vector3.Zero; Diagnostic = null;
            return Outcome = ZoneNavigationFollowOutcome.Moving;
        }
        catch
        {
            Stop(ZoneNavigationFollowOutcome.Unsupported);
            throw;
        }
        finally { _updating = false; }
    }

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true; _navigation.UnregisterFollower(this); Stop(ZoneNavigationFollowOutcome.Retired);
    }
    internal void RetirePublication()
    { _navigation.UnregisterFollower(this); Stop(ZoneNavigationFollowOutcome.Retired); }
    private ZoneNavigationFollowOutcome Stop(ZoneNavigationFollowOutcome outcome)
    {
        if (_hasVelocity) { _hasVelocity = false; if (!_motion.TryRequestVelocity(Vector3.Zero)) outcome = ZoneNavigationFollowOutcome.Retired; }
        return Outcome = outcome;
    }
    private static double HorizontalDistance(Vector3 first, Vector3 second)
    { var x = (double)first.X - second.X; var y = (double)first.Y - second.Y; return Math.Sqrt(x * x + y * y); }
    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}

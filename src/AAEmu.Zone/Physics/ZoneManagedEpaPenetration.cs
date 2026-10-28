using System.Numerics;
using D = AAEmu.Zone.Physics.ZoneManagedDouble3;

namespace AAEmu.Zone.Physics;

internal readonly record struct ZoneManagedPenetrationResult(double Depth, Vector3 Normal, Vector3 Point, bool Ambiguous,
    ushort? MaterialOverride = null);

/// <summary>
/// Support-polytope expansion. Hull planes give lower depth bounds; exact shape supports give
/// upper bounds. The returned upper bound is a separating translation within the declared
/// support-gap tolerance. Exhaustion/degeneracy is explicit, never a guessed correction.
/// </summary>
internal static class ZoneManagedEpaPenetration
{
    private const int MaximumIterations = 384;
    private const int MaximumVertices = 512;

    public static bool Try(ZoneManagedConvexTarget target, ZonePhysicsShape shape, Vector3 center,
        out ZoneManagedPenetrationResult result)
    {
        result = default;
        var seed = new List<ZoneManagedPenetrationSupport>();
        foreach (var axis in new[] { Vector3.UnitZ, -Vector3.UnitZ, Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY })
        {
            var support = ZoneManagedPenetrationMath.Support(target, shape, center, D.From(axis));
            if (!seed.Any(point => (point.Difference - support.Difference).Squared <= 1e-24)) seed.Add(support);
        }
        if (seed.Count < 4) throw ZoneManagedPenetrationMath.Failure("support seed is not volumetric");
        var first = 0;
        var second = Enumerable.Range(1, seed.Count - 1).MaxBy(i => (seed[i].Difference - seed[first].Difference).Squared);
        var third = Enumerable.Range(0, seed.Count).MaxBy(i => D.Cross(seed[second].Difference - seed[first].Difference,
            seed[i].Difference - seed[first].Difference).Squared);
        var plane = D.Cross(seed[second].Difference - seed[first].Difference, seed[third].Difference - seed[first].Difference);
        var fourth = Enumerable.Range(0, seed.Count).MaxBy(i => Math.Abs(D.Dot(plane, seed[i].Difference - seed[first].Difference)));
        if (plane.Squared == 0 || Math.Abs(D.Dot(plane, seed[fourth].Difference - seed[first].Difference)) < 1e-24)
            throw ZoneManagedPenetrationMath.Failure("support seed has no stable tetrahedron");
        var vertices = new List<ZoneManagedPenetrationSupport> { seed[first], seed[second], seed[third], seed[fourth] };
        var interior = (vertices[0].Difference + vertices[1].Difference + vertices[2].Difference + vertices[3].Difference) * .25;
        var scale = Math.Max(1, Math.Sqrt(seed.Max(point => point.Difference.Squared)));
        var epsilon = scale * 1e-11;
        if (epsilon > ZoneManagedPenetrationQueries.Tolerance * .01)
            throw ZoneManagedPenetrationMath.Failure("shape extent exceeds supported depth precision");
        var faces = new List<Face>();
        AddFace(0, 1, 2); AddFace(0, 3, 1); AddFace(0, 2, 3); AddFace(1, 3, 2);
        foreach (var support in seed)
            if (!vertices.Any(point => (point.Difference - support.Difference).Squared <= 1e-24)) AddVertex(support);

        for (var iteration = 0; iteration < MaximumIterations; iteration++)
        {
            if (faces.Count == 0) throw ZoneManagedPenetrationMath.Failure("convex hull lost its boundary");
            var bestIndex = 0;
            for (var i = 1; i < faces.Count; i++)
                if (faces[i].Distance < faces[bestIndex].Distance - epsilon ||
                    Math.Abs(faces[i].Distance - faces[bestIndex].Distance) <= epsilon &&
                    ZoneManagedPenetrationMath.Prefer(-faces[i].Normal.Float, -faces[bestIndex].Normal.Float)) bestIndex = i;
            var face = faces[bestIndex];
            var support = ZoneManagedPenetrationMath.Support(target, shape, center, face.Normal);
            var upper = D.Dot(face.Normal, support.Difference);
            if (!double.IsFinite(upper)) throw ZoneManagedPenetrationMath.Failure("support plane is non-finite");
            if (upper <= 0) return false; // A certified separating/supporting plane, including touch.
            var gap = upper - face.Distance;
            if (gap < -epsilon) throw ZoneManagedPenetrationMath.Failure("support point lies inside its hull face");
            if (face.Distance >= -epsilon && gap <= ZoneManagedPenetrationQueries.Tolerance)
            {
                var ambiguous = upper <= ZoneManagedPenetrationQueries.Tolerance;
                foreach (var other in faces)
                {
                    if (Math.Abs(other.Distance - face.Distance) > ZoneManagedPenetrationQueries.Tolerance ||
                        D.Dot(other.Normal, face.Normal) > .9999) continue;
                    var otherSupport = ZoneManagedPenetrationMath.Support(target, shape, center, other.Normal);
                    if (D.Dot(other.Normal, otherSupport.Difference) <= upper + ZoneManagedPenetrationQueries.Tolerance) ambiguous = true;
                }
                var a = vertices[face.A]; var b = vertices[face.B]; var c = vertices[face.C];
                ZoneManagedPenetrationMath.ClosestTriangle(face.Normal * face.Distance, a.Difference, b.Difference, c.Difference,
                    out var wa, out var wb, out var wc);
                result = new(upper, -face.Normal.Float, (a.Target * wa + b.Target * wb + c.Target * wc).Float, ambiguous);
                return true;
            }
            if (vertices.Count >= MaximumVertices) throw ZoneManagedPenetrationMath.Failure("EPA vertex bound exceeded");
            if (!AddVertex(support)) throw ZoneManagedPenetrationMath.Failure("support expansion stalled before its depth bound converged");
        }
        throw ZoneManagedPenetrationMath.Failure("EPA iteration bound exceeded");

        void AddFace(int a, int b, int c)
        {
            var normal = D.Cross(vertices[b].Difference - vertices[a].Difference, vertices[c].Difference - vertices[a].Difference);
            if (normal.Squared <= 1e-28) return;
            normal = normal.Unit;
            if (D.Dot(normal, interior - vertices[a].Difference) > 0) { (b, c) = (c, b); normal = -normal; }
            faces.Add(new(a, b, c, normal, D.Dot(normal, vertices[a].Difference)));
        }
        bool AddVertex(ZoneManagedPenetrationSupport support)
        {
            var visible = new List<int>();
            var horizon = new List<(int A, int B)>();
            for (var i = 0; i < faces.Count; i++)
            {
                var face = faces[i];
                if (D.Dot(face.Normal, support.Difference) - face.Distance <= epsilon) continue;
                visible.Add(i); AddEdge(face.A, face.B); AddEdge(face.B, face.C); AddEdge(face.C, face.A);
            }
            if (visible.Count == 0) return false;
            if (horizon.Count < 3) throw ZoneManagedPenetrationMath.Failure("EPA horizon is not closed");
            for (var i = visible.Count - 1; i >= 0; i--) faces.RemoveAt(visible[i]);
            var added = vertices.Count; vertices.Add(support);
            foreach (var edge in horizon) AddFace(edge.A, edge.B, added);
            return true;

            void AddEdge(int a, int b)
            {
                var opposite = horizon.FindIndex(edge => edge.A == b && edge.B == a);
                if (opposite >= 0) horizon.RemoveAt(opposite); else horizon.Add((a, b));
            }
        }
    }

    private readonly record struct Face(int A, int B, int C, D Normal, double Distance);
}

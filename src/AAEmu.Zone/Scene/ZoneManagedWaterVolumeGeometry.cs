using System.Numerics;

namespace AAEmu.Zone.Scene;

/// <summary>35047C80/3504C770's planar area frame and finite polygon query; no render-AABB substitute.</summary>
internal sealed class ZoneManagedWaterVolumeGeometry
{
    private readonly Vector2[] _polygon;
    private readonly int[] _edgeOrder;
    internal Vector3 Center { get; }
    internal Vector3 AxisX { get; }
    internal Vector3 AxisY { get; }
    internal Vector3 Normal { get; }
    internal Vector3 PlaneNormal { get; }
    internal Vector3 BoundsMin { get; }
    internal Vector3 BoundsMax { get; }
    internal Vector3 PlaneOrigin { get; }
    internal IReadOnlyList<Vector3> Contour { get; }
    internal float SurfaceHeight => PlaneOrigin.Z;
    internal float MaximumPotentialLevel { get; }
    private readonly float _lower, _upper;
    private readonly bool _river;

    internal ZoneManagedWaterVolumeGeometry(Vector3[] projectedContour, float depth, bool river)
    {
        if (projectedContour.Length < 4 || river && (projectedContour.Length & 1) != 0)
            throw new NotSupportedException("The authored water contour does not have the native lake/river physical vertex count.");
        if (!float.IsFinite(depth)) throw new InvalidDataException("Water depth must be finite.");
        var vertices = (Vector3[])projectedContour.Clone();
        var signedArea = 0f;
        for (var i = 0; i < vertices.Length; i++)
        {
            var a = vertices[(i + vertices.Length - 1) % vertices.Length]; var b = vertices[i];
            Validate(a); signedArea += a.X * b.Y - a.Y * b.X;
        }
        if (!float.IsFinite(signedArea) || signedArea == 0)
            throw new NotSupportedException("Degenerate water contour orientation has no admitted physical area.");
        if (signedArea <= 0) Array.Reverse(vertices); //300F9130/300F9A80
        Contour = Array.AsReadOnly(vertices);
        PlaneOrigin = vertices[0];
        if (vertices.Any(vertex => vertex.Z != PlaneOrigin.Z))
            throw new NotSupportedException("Nonhorizontal or warped water contours require their area mesh/plane query processor.");
        if (river) RequireSimpleUpwardRiverStrip(vertices);

        //35047C80: perimeter-weighted center includes the closing edge. Its covariance
        //accumulation then visits only successive open edges, not the closing edge.
        var previous = vertices[^1]; var total = 0f; var sum = Vector3.Zero;
        foreach (var vertex in vertices)
        {
            var edge = vertex - previous; var length = Length(edge);
            total += length;
            sum = new(sum.X + (previous.X + vertex.X) * length,
                sum.Y + (previous.Y + vertex.Y) * length, sum.Z + (previous.Z + vertex.Z) * length);
            previous = vertex;
        }
        var inverse = (float)(1.0 / (total * 2f));
        Center = sum * inverse; Validate(Center);
        var covariance = new float[9]; var winding = Vector3.Cross(vertices[^1] - Center, vertices[0] - Center);
        for (var i = 0; i + 1 < vertices.Length; i++)
        {
            var a = vertices[i] - Center; var b = vertices[i + 1] - Center;
            var length = Length(b - a); winding += Vector3.Cross(a, b);
            for (var row = 0; row < 3; row++)
                for (var column = 0; column < 3; column++)
                    covariance[row * 3 + column] += (((b[row] * b[column] + a[row] * a[column]) * 2f +
                        b[column] * a[row]) + a[column] * b[row]) * length;
        }
        var (values, vectors) = Eigenvectors(covariance);
        var minimum = values[1] < values[0] ? 1 : 0;
        if (values[2] < values[minimum]) minimum = 2;
        var maximum = values[0] < values[1] ? 1 : 0;
        if (values[maximum] < values[2]) maximum = 2;
        var normal = Row(minimum); var axis = Row(maximum);
        var dot = (normal.X * winding.X + normal.Y * winding.Y) + normal.Z * winding.Z;
        var sign = BitConverter.SingleToInt32Bits(dot) < 0 ? -1f : 1f;
        normal *= sign; Normal = normal; AxisX = axis * sign; AxisY = Vector3.Cross(normal, AxisX);
        if (normal.Z <= 0 || river && normal != Vector3.UnitZ)
            throw new NotSupportedException("Water area frame requires its unsupported triangle-normal/flow refinement processor.");
        PlaneNormal = BuoyancyNormal(AxisX, AxisY, Normal);
        _polygon = vertices.Select(vertex =>
        {
            var local = vertex - Center;
            return new Vector2((local.X * AxisX.X + local.Y * AxisX.Y) + local.Z * AxisX.Z,
                (local.X * AxisY.X + local.Y * AxisY.Y) + local.Z * AxisY.Z);
        }).ToArray();
        _edgeOrder = Enumerable.Range(0, _polygon.Length)
            .OrderBy(index => MathF.Max(_polygon[index].X, _polygon[(index + 1) % _polygon.Length].X)).ThenBy(index => index).ToArray();
        _lower = MathF.Min(-depth, 0); _upper = 10;
        _river = river;
        var minimumX = _polygon.Min(point => point.X); var maximumX = _polygon.Max(point => point.X);
        var minimumY = _polygon.Min(point => point.Y); var maximumY = _polygon.Max(point => point.Y);
        var halfX = (maximumX - minimumX) * .5f; var halfY = (maximumY - minimumY) * .5f;
        var halfZ = (_upper - _lower) * .5f;
        var extent = new Vector3((MathF.Abs(AxisX.X) * halfX + halfY * MathF.Abs(AxisY.X)) + halfZ * MathF.Abs(Normal.X),
            (MathF.Abs(AxisX.Y) * halfX + halfY * MathF.Abs(AxisY.Y)) + halfZ * MathF.Abs(Normal.Y),
            (MathF.Abs(AxisX.Z) * halfX + halfY * MathF.Abs(AxisY.Z)) + halfZ * MathF.Abs(Normal.Z));
        var center = new Vector3((((maximumX + minimumX) * AxisX.X + AxisY.X * (maximumY + minimumY)) + Normal.X * (_lower + _upper)) * .5f + Center.X,
            (((maximumX + minimumX) * AxisX.Y + AxisY.Y * (maximumY + minimumY)) + Normal.Y * (_lower + _upper)) * .5f + Center.Y,
            (((maximumX + minimumX) * AxisX.Z + AxisY.Z * (maximumY + minimumY)) + Normal.Z * (_lower + _upper)) * .5f + Center.Z);
        BoundsMin = center - extent; BoundsMax = center + extent; Validate(BoundsMin); Validate(BoundsMax);
        //The planar area upper interval is widened after (not before) its world AABB.
        var deviation = vertices.Max(vertex => MathF.Abs(Vector3.Dot(vertex - Center, Normal)));
        if (deviation > .05f)
            throw new NotSupportedException("Water frame deviation requires its native nonplanar area mesh processor.");
        if (!river) _upper = (deviation + .01f) + _upper;
        MaximumPotentialLevel = ProjectionUpperBound(PlaneOrigin, PlaneNormal, BoundsMin, BoundsMax);

        Vector3 Row(int index) => new((float)vectors[index * 3], (float)vectors[index * 3 + 1], (float)vectors[index * 3 + 2]);
    }

    internal bool Contains(Vector3 point, float queryRange)
    {
        //3504E000's strict AABB admission occurs before the expanded area query.
        if (!CouldContributeAtAnyRange(point)) return false;
        var delta = point - Center;
        var low = _lower - queryRange; var high = queryRange + _upper;
        var z = (delta.Y * Normal.Y + delta.X * Normal.X) + delta.Z * Normal.Z;
        var vertical = MathF.Abs((low + high) - z * 2f) - MathF.Abs(low - high);
        if (BitConverter.SingleToInt32Bits(vertical) >= 0) return false;
        if (_river && point.Z - SurfaceHeight > queryRange) return false;
        return true;
    }

    internal bool CouldContributeAtAnyRange(Vector3 point)
    {
        if (!OverlapsWorldBounds(point)) return false;
        var delta = point - Center;
        var x = (delta.Y * AxisX.Y + delta.X * AxisX.X) + delta.Z * AxisX.Z;
        var y = (delta.Y * AxisY.Y + delta.X * AxisY.X) + delta.Z * AxisY.Z;
        var bestNumerator = 1e10f; var bestSquared = 1f; var inside = false;
        foreach (var index in _edgeOrder)
        {
            var a = _polygon[index]; var b = _polygon[(index + 1) % _polygon.Length];
            //3504C770's two sorted-edge searches reconstruct their boundary from
            //the endpoint sum and absolute difference. Comparing directly with
            //Min/Max(endpoint) changes authored-vertex admission after rounding.
            var span = MathF.Abs(a.X - b.X); var sum = a.X + b.X;
            var minimum = (span * -1f + sum) * .5f;
            var maximum = (span + sum) * .5f;
            if (BitConverter.SingleToInt32Bits(x - minimum) < 0 ||
                BitConverter.SingleToInt32Bits(x - maximum) >= 0) continue;
            var dx = b.X - a.X; var squared = dx * dx;
            var numerator = ((x - a.X) * (b.Y - a.Y) + dx * a.Y) * dx;
            if (numerator - squared * y > 0 &&
                ((bestSquared * numerator - squared * bestNumerator) + (numerator - bestNumerator) * 1e-20f) < 0)
            { bestNumerator = numerator; bestSquared = squared; inside = BitConverter.SingleToInt32Bits(dx) < 0; }
        }
        return inside;
    }

    internal float LevelAt(Vector3 point)
    {
        var delta = point - PlaneOrigin;
        return point.Z - (((delta.Y * PlaneNormal.Y + delta.X * PlaneNormal.X) + delta.Z * PlaneNormal.Z) * PlaneNormal.Z);
    }
    internal bool NeedsUnownedZeroRangeRiverMesh(Vector3 point) => _river && OverlapsWorldBounds(point);
    internal bool OverlapsWorldBounds(Vector3 point) =>
        AxisOverlap(BoundsMin.X, BoundsMax.X, point.X) && AxisOverlap(BoundsMin.Y, BoundsMax.Y, point.Y) &&
        AxisOverlap(BoundsMin.Z, BoundsMax.Z, point.Z);
    private static bool AxisOverlap(float minimum, float maximum, float point) =>
        MathF.Abs(((minimum + maximum) - point) - point) - (maximum - minimum) < 0;
    private static float Length(Vector3 value) => MathF.Sqrt((value.X * value.X + value.Y * value.Y) + value.Z * value.Z);
    private static void Validate(Vector3 value)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z))
            throw new InvalidDataException("Water geometry must be finite and representable.");
    }

    //300F9A80's canonical two-bank strip must cover the simple contour once with
    //upward faces before the planar positive-range reduction is valid. This is
    //an admission proof, not a replacement triangulation or a proximity tolerance.
    private static void RequireSimpleUpwardRiverStrip(Vector3[] contour)
    {
        for (var k = 0; k < contour.Length / 2 - 1; k++)
            if (Cross(contour[k], contour[k + 1], contour[contour.Length - 2 - k]) <= 0 ||
                Cross(contour[contour.Length - 2 - k], contour[contour.Length - 1 - k], contour[k]) <= 0)
                Refuse();
        for (var i = 0; i < contour.Length; i++)
            for (var j = i + 2; j < contour.Length; j++)
            {
                if (i == 0 && j == contour.Length - 1) continue;
                var a = contour[i]; var b = contour[(i + 1) % contour.Length];
                var c = contour[j]; var d = contour[(j + 1) % contour.Length];
                var ac = Cross(a, b, c); var ad = Cross(a, b, d);
                var ca = Cross(c, d, a); var cb = Cross(c, d, b);
                if (Opposite(ac, ad) && Opposite(ca, cb) ||
                    ac == 0 && OnSegment(a, b, c) || ad == 0 && OnSegment(a, b, d) ||
                    ca == 0 && OnSegment(c, d, a) || cb == 0 && OnSegment(c, d, b)) Refuse();
            }
        static double Cross(Vector3 a, Vector3 b, Vector3 c) =>
            ((double)b.X - a.X) * ((double)c.Y - a.Y) - ((double)b.Y - a.Y) * ((double)c.X - a.X);
        static bool Opposite(double a, double b) => a < 0 && b > 0 || a > 0 && b < 0;
        static bool OnSegment(Vector3 a, Vector3 b, Vector3 point) =>
            point.X >= MathF.Min(a.X, b.X) && point.X <= MathF.Max(a.X, b.X) &&
            point.Y >= MathF.Min(a.Y, b.Y) && point.Y <= MathF.Max(a.Y, b.Y);
        static void Refuse() => throw new NotSupportedException(
            "Folded, overlapping or degenerate river strips require their actual triangle-mesh area processor.");
    }

    //35122FA0 specialized to the constructor's three-by-three symmetric matrix.
    private static (double[] Values, double[] Vectors) Eigenvectors(float[] input)
    {
        var matrix = Array.ConvertAll(input, value => (double)value);
        double[] vectors = [1, 0, 0, 0, 1, 0, 0, 0, 1]; var tolerance = 0d;
        for (var iteration = 0; iteration < 90; iteration++)
        {
            var largest = tolerance; var p = -1; var q = -1;
            for (var row = 0; row < 2; row++)
                for (var column = row + 1; column < 3; column++)
                    if (matrix[row * 3 + column] * matrix[row * 3 + column] > largest)
                    { largest = matrix[row * 3 + column] * matrix[row * 3 + column]; p = row; q = column; }
            if (p < 0)
            {
                for (var i = 0; i < vectors.Length; i++)
                    if (Math.Abs(vectors[i]) < .000001) vectors[i] = 0;
                    else if (Math.Abs(Math.Abs(vectors[i]) - 1) < 1e-20) vectors[i] = Math.CopySign(1, vectors[i]);
                return ([matrix[0], matrix[4], matrix[8]], vectors);
            }
            var theta = (matrix[q * 3 + q] - matrix[p * 3 + p]) * .5 / matrix[p * 3 + q];
            if (Math.Abs(theta) < 1e20)
            {
                var length = Math.Sqrt(theta * theta + 1);
                var tangent = theta <= 0 ? length - theta : -theta - length;
                var cosine = 1 / Math.Sqrt(tangent * tangent + 1); var sine = cosine * tangent;
                for (var row = 0; row < 3; row++) Rotate(matrix, row * 3 + p, row * 3 + q);
                for (var column = 0; column < 3; column++) Rotate(matrix, p * 3 + column, q * 3 + column);
                for (var column = 0; column < 3; column++) Rotate(vectors, p * 3 + column, q * 3 + column);
                void Rotate(double[] values, int first, int second)
                {
                    var a = values[first]; var b = values[second];
                    values[first] = a * cosine - b * sine; values[second] = a * sine + b * cosine;
                }
            }
            matrix[p * 3 + q] = 0;
            if (iteration == 9) tolerance += 1e-20;
        }
        throw new NotSupportedException("Water area principal-axis construction exhausted its original bounded iteration count.");
    }

    //35023EB0 matrix->quaternion followed by30049C60's rotation of UnitZ. The
    //auxiliary frame of an authored flat contour can have a small float tilt.
    private static Vector3 BuoyancyNormal(Vector3 x, Vector3 y, Vector3 z)
    {
        var trace = (x.X + y.Y) + z.Z; Quaternion q;
        if (trace > 0)
        {
            var root = MathF.Sqrt(trace + 1); var inverse = (float)(.5 / root);
            q = new((y.Z - z.Y) * inverse, (z.X - x.Z) * inverse, (x.Y - y.X) * inverse, root * .5f);
        }
        else if (x.X >= y.Y && x.X >= z.Z)
        {
            var root = MathF.Sqrt(((x.X - y.Y) - z.Z) + 1); var inverse = (float)(.5 / root);
            q = new(root * .5f, (x.Y + y.X) * inverse, (x.Z + z.X) * inverse, (y.Z - z.Y) * inverse);
        }
        else if (y.Y >= x.X && y.Y >= z.Z)
        {
            var root = MathF.Sqrt(((y.Y - z.Z) - x.X) + 1); var inverse = (float)(.5 / root);
            q = new((x.Y + y.X) * inverse, root * .5f, (y.Z + z.Y) * inverse, (z.X - x.Z) * inverse);
        }
        else
        {
            var root = MathF.Sqrt(((z.Z - x.X) - y.Y) + 1); var inverse = (float)(.5 / root);
            q = new((x.Z + z.X) * inverse, (y.Z + z.Y) * inverse, root * .5f, (x.Y - y.X) * inverse);
        }
        var first = q.Y * q.W - q.Z * -q.X;
        var second = q.Z * q.Y - q.X * q.W;
        var third = q.X * -q.X - q.Y * q.Y;
        return new(first + first, second + second, (third + 1) + third);
    }

    /// <summary>Linear real projection plus outward bounds on every float rounding operation.</summary>
    private static float ProjectionUpperBound(Vector3 origin, Vector3 normal, Vector3 minimum, Vector3 maximum)
    {
        var delta = new double[3]; var errors = new double[3]; var products = new double[3];
        for (var i = 0; i < 3; i++)
        {
            delta[i] = Math.Max(Math.Abs((double)minimum[i] - origin[i]), Math.Abs((double)maximum[i] - origin[i]));
            errors[i] = Spacing(delta[i]) * Math.Abs(normal[i]);
            products[i] = delta[i] * Math.Abs(normal[i]);
            errors[i] += Spacing(products[i] + errors[i]);
        }
        var sumMagnitude = products[1] + products[0];
        var error = errors[1] + errors[0] + Spacing(sumMagnitude + errors[1] + errors[0]);
        sumMagnitude += products[2]; error += errors[2] + Spacing(sumMagnitude + error + errors[2]);
        error *= Math.Abs(normal.Z);
        error += Spacing(sumMagnitude * Math.Abs(normal.Z) + error);
        var highest = double.NegativeInfinity; var magnitude = 0d;
        for (var corner = 0; corner < 8; corner++)
        {
            var x = (corner & 1) == 0 ? minimum.X : maximum.X;
            var y = (corner & 2) == 0 ? minimum.Y : maximum.Y;
            var z = (corner & 4) == 0 ? minimum.Z : maximum.Z;
            var value = (double)z - (((double)y - origin.Y) * normal.Y + ((double)x - origin.X) * normal.X +
                ((double)z - origin.Z) * normal.Z) * normal.Z;
            highest = Math.Max(highest, value); magnitude = Math.Max(magnitude, Math.Abs(value));
        }
        error += Spacing(magnitude + error);
        return Up(highest + error);
        static double Spacing(double value)
        { var rounded = Up(value); return (double)MathF.BitIncrement(rounded) - rounded; }
        static float Up(double value)
        {
            var result = (float)value;
            if (result < value) result = MathF.BitIncrement(result);
            if (!float.IsFinite(result)) throw new NotSupportedException("Water query level bound is not finite.");
            return result;
        }
    }
}

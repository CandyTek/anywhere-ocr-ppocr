namespace AnywhereOcr;

public readonly record struct Pt(double X, double Y);

/// <summary>
/// The handful of OpenCV / pyclipper routines PaddleOCR's DB post-processing relies on,
/// re-implemented so the worker needs no native image library.
/// </summary>
public static class Geometry
{
    public static double Dist(Pt a, Pt b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double Cross(Pt o, Pt a, Pt b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

    /// <summary>Andrew's monotone chain convex hull (no duplicate or collinear vertices).</summary>
    public static List<Pt> ConvexHull(IReadOnlyList<Pt> pts)
    {
        var p = pts.Distinct().OrderBy(a => a.X).ThenBy(a => a.Y).ToList();
        if (p.Count <= 2) return p;
        var lower = new List<Pt>();
        foreach (var q in p)
        {
            while (lower.Count >= 2 && Cross(lower[^2], lower[^1], q) <= 0) lower.RemoveAt(lower.Count - 1);
            lower.Add(q);
        }
        var upper = new List<Pt>();
        for (int i = p.Count - 1; i >= 0; i--)
        {
            var q = p[i];
            while (upper.Count >= 2 && Cross(upper[^2], upper[^1], q) <= 0) upper.RemoveAt(upper.Count - 1);
            upper.Add(q);
        }
        lower.RemoveAt(lower.Count - 1);
        upper.RemoveAt(upper.Count - 1);
        lower.AddRange(upper);
        return lower;
    }

    /// <summary>cv2.minAreaRect equivalent: the four corners (unordered) and the side lengths.</summary>
    public static (Pt[] Corners, double W, double H) MinAreaRect(IReadOnlyList<Pt> pts)
    {
        var hull = ConvexHull(pts);
        if (hull.Count == 0) return (new[] { new Pt(0, 0), new Pt(0, 0), new Pt(0, 0), new Pt(0, 0) }, 0, 0);
        if (hull.Count == 1) { var p = hull[0]; return (new[] { p, p, p, p }, 0, 0); }
        if (hull.Count == 2) { var a = hull[0]; var b = hull[1]; return (new[] { a, b, b, a }, Dist(a, b), 0); }

        double best = double.MaxValue, bw = 0, bh = 0;
        Pt[] bestCorners = Array.Empty<Pt>();
        int n = hull.Count;
        for (int i = 0; i < n; i++)
        {
            var a = hull[i];
            var b = hull[(i + 1) % n];
            double ex = b.X - a.X, ey = b.Y - a.Y;
            double len = Math.Sqrt(ex * ex + ey * ey);
            if (len < 1e-12) continue;
            double ux = ex / len, uy = ey / len;   // edge direction
            double vx = -uy, vy = ux;              // edge normal
            double minU = double.MaxValue, maxU = double.MinValue, minV = double.MaxValue, maxV = double.MinValue;
            foreach (var p in hull)
            {
                double u = p.X * ux + p.Y * uy, v = p.X * vx + p.Y * vy;
                if (u < minU) minU = u;
                if (u > maxU) maxU = u;
                if (v < minV) minV = v;
                if (v > maxV) maxV = v;
            }
            double area = (maxU - minU) * (maxV - minV);
            if (area < best)
            {
                best = area; bw = maxU - minU; bh = maxV - minV;
                Pt P(double u, double v) => new(u * ux + v * vx, u * uy + v * vy);
                bestCorners = new[] { P(minU, minV), P(maxU, minV), P(maxU, maxV), P(minU, maxV) };
            }
        }
        return (bestCorners, bw, bh);
    }

    /// <summary>
    /// DBPostProcess.get_mini_boxes: minAreaRect corners ordered top-left, top-right,
    /// bottom-right, bottom-left, plus the shorter side length.
    /// </summary>
    public static (Pt[] Box, double SSide) GetMiniBoxes(IReadOnlyList<Pt> contour)
    {
        var (corners, w, h) = MinAreaRect(contour);
        var pts = corners.OrderBy(p => p.X).ToArray();   // stable, like Python's sorted
        int i1, i2, i3, i4;
        if (pts[1].Y > pts[0].Y) { i1 = 0; i4 = 1; } else { i1 = 1; i4 = 0; }
        if (pts[3].Y > pts[2].Y) { i2 = 2; i3 = 3; } else { i2 = 3; i3 = 2; }
        return (new[] { pts[i1], pts[i2], pts[i3], pts[i4] }, Math.Min(w, h));
    }

    /// <summary>
    /// DBPostProcess.unclip for a rectangle: pyclipper offsets the quad outward by
    /// area * ratio / perimeter with round joins, and the minimum-area rectangle of that
    /// shape is simply the original rectangle grown by that distance on every side.
    /// </summary>
    public static Pt[] UnclipRect(Pt[] box, double ratio)
    {
        double w = Dist(box[0], box[1]), h = Dist(box[0], box[3]);
        double perimeter = 2 * (w + h);
        if (perimeter <= 0) return box;
        double d = w * h * ratio / perimeter;
        double ux = (box[1].X - box[0].X) / w, uy = (box[1].Y - box[0].Y) / w;   // along the top edge
        double vx = (box[3].X - box[0].X) / h, vy = (box[3].Y - box[0].Y) / h;   // along the left edge
        if (w <= 0) { ux = 1; uy = 0; }
        if (h <= 0) { vx = 0; vy = 1; }
        return new[]
        {
            new Pt(box[0].X - d * ux - d * vx, box[0].Y - d * uy - d * vy),
            new Pt(box[1].X + d * ux - d * vx, box[1].Y + d * uy - d * vy),
            new Pt(box[2].X + d * ux + d * vx, box[2].Y + d * uy + d * vy),
            new Pt(box[3].X - d * ux + d * vx, box[3].Y - d * uy + d * vy),
        };
    }

    /// <summary>DBPostProcess.box_score_fast: mean probability inside the quad (cv2.fillPoly semantics).</summary>
    public static double BoxScoreFast(float[] pred, int width, int height, Pt[] box)
    {
        double minX = box.Min(p => p.X), maxX = box.Max(p => p.X);
        double minY = box.Min(p => p.Y), maxY = box.Max(p => p.Y);
        int xmin = Math.Clamp((int)Math.Floor(minX), 0, width - 1);
        int xmax = Math.Clamp((int)Math.Ceiling(maxX), 0, width - 1);
        int ymin = Math.Clamp((int)Math.Floor(minY), 0, height - 1);
        int ymax = Math.Clamp((int)Math.Ceiling(maxY), 0, height - 1);

        var px = new int[4]; var py = new int[4];
        for (int i = 0; i < 4; i++)
        {
            px[i] = (int)(box[i].X - xmin);   // astype(np.int32) truncates toward zero
            py[i] = (int)(box[i].Y - ymin);
        }

        double sum = 0; int count = 0;
        for (int y = 0; y <= ymax - ymin; y++)
        {
            int row = (ymin + y) * width;
            for (int x = 0; x <= xmax - xmin; x++)
            {
                if (!InsideOrOnEdge(x, y, px, py)) continue;
                sum += pred[row + xmin + x];
                count++;
            }
        }
        return count == 0 ? 0 : sum / count;
    }

    private static bool InsideOrOnEdge(int x, int y, int[] px, int[] py)
    {
        int n = px.Length;
        bool inside = false;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            long x1 = px[j], y1 = py[j], x2 = px[i], y2 = py[i];
            long cross = (x2 - x1) * (y - y1) - (y2 - y1) * (x - x1);
            if (cross == 0 && x >= Math.Min(x1, x2) && x <= Math.Max(x1, x2)
                           && y >= Math.Min(y1, y2) && y <= Math.Max(y1, y2))
                return true;
            if ((y1 > y) != (y2 > y))
            {
                double xi = x1 + (double)(y - y1) * (x2 - x1) / (y2 - y1);
                if (x < xi) inside = !inside;
            }
        }
        return inside;
    }

    /// <summary>cv2.getPerspectiveTransform: 3x3 row-major matrix mapping src quad to dst quad.</summary>
    public static double[] GetPerspectiveTransform(Pt[] src, Pt[] dst)
    {
        var a = new double[8, 9];
        for (int i = 0; i < 4; i++)
        {
            double x = src[i].X, y = src[i].Y, X = dst[i].X, Y = dst[i].Y;
            a[i, 0] = x; a[i, 1] = y; a[i, 2] = 1; a[i, 6] = -x * X; a[i, 7] = -y * X; a[i, 8] = X;
            a[i + 4, 3] = x; a[i + 4, 4] = y; a[i + 4, 5] = 1; a[i + 4, 6] = -x * Y; a[i + 4, 7] = -y * Y; a[i + 4, 8] = Y;
        }
        // Gaussian elimination with partial pivoting.
        for (int col = 0; col < 8; col++)
        {
            int pivot = col;
            for (int r = col + 1; r < 8; r++)
                if (Math.Abs(a[r, col]) > Math.Abs(a[pivot, col])) pivot = r;
            if (pivot != col)
                for (int c = 0; c < 9; c++) (a[col, c], a[pivot, c]) = (a[pivot, c], a[col, c]);
            double p = a[col, col];
            if (Math.Abs(p) < 1e-12) continue;
            for (int c = col; c < 9; c++) a[col, c] /= p;
            for (int r = 0; r < 8; r++)
            {
                if (r == col) continue;
                double f = a[r, col];
                if (f == 0) continue;
                for (int c = col; c < 9; c++) a[r, c] -= f * a[col, c];
            }
        }
        return new[] { a[0, 8], a[1, 8], a[2, 8], a[3, 8], a[4, 8], a[5, 8], a[6, 8], a[7, 8], 1.0 };
    }

    public static double[] Invert3x3(double[] m)
    {
        double det = m[0] * (m[4] * m[8] - m[5] * m[7]) - m[1] * (m[3] * m[8] - m[5] * m[6]) + m[2] * (m[3] * m[7] - m[4] * m[6]);
        if (Math.Abs(det) < 1e-18) det = 1e-18;
        double id = 1.0 / det;
        return new[]
        {
            (m[4] * m[8] - m[5] * m[7]) * id, (m[2] * m[7] - m[1] * m[8]) * id, (m[1] * m[5] - m[2] * m[4]) * id,
            (m[5] * m[6] - m[3] * m[8]) * id, (m[0] * m[8] - m[2] * m[6]) * id, (m[2] * m[3] - m[0] * m[5]) * id,
            (m[3] * m[7] - m[4] * m[6]) * id, (m[1] * m[6] - m[0] * m[7]) * id, (m[0] * m[4] - m[1] * m[3]) * id,
        };
    }
}

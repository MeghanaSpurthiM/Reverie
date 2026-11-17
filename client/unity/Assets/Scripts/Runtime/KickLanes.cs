using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// Geometry shared by the kick hit test and the lane solver.
public static class KickGeometry
{
    /// The paddle: Unity's 1 x 1 quad, in its local space.
    public static readonly Bounds PaddleBox = new Bounds(Vector3.zero, new Vector3(1f, 1f, 2e-3f));

    static readonly Vector3[] WorldAxes = { Vector3.right, Vector3.up, Vector3.forward };
    static readonly Vector3[] _qa = new Vector3[3];
    static readonly float[] _qh = new float[3];

    /// Separating-axis test: an oriented box (localBox under localToWorld) against a world AABB.
    public static bool ObbOverlapsBox(Matrix4x4 localToWorld, Bounds localBox, Bounds box)
    {
        Vector3 bh = box.extents, lh = localBox.extents;
        for (int i = 0; i < 3; i++)
        {
            Vector3 col = localToWorld.GetColumn(i);
            float len = col.magnitude;
            _qh[i] = lh[i] * len;
            _qa[i] = len > 0f ? col / len : col;
        }
        Vector3 d = localToWorld.MultiplyPoint3x4(localBox.center) - box.center;

        bool Separated(Vector3 L)
        {
            float len = L.magnitude;
            if (len < 1e-6f) return false;
            float rA = bh.x * Mathf.Abs(L.x) + bh.y * Mathf.Abs(L.y) + bh.z * Mathf.Abs(L.z);
            float rB = _qh[0] * Mathf.Abs(Vector3.Dot(_qa[0], L))
                     + _qh[1] * Mathf.Abs(Vector3.Dot(_qa[1], L))
                     + _qh[2] * Mathf.Abs(Vector3.Dot(_qa[2], L));
            return Mathf.Abs(Vector3.Dot(d, L)) > rA + rB + 1e-9f * len;
        }

        foreach (var a in WorldAxes) if (Separated(a)) return false;
        for (int i = 0; i < 3; i++) if (Separated(_qa[i])) return false;
        foreach (var a in WorldAxes)
            for (int i = 0; i < 3; i++)
                if (Separated(Vector3.Cross(a, _qa[i]))) return false;
        return true;
    }
}

/// Places the falling targets so every lane plays fairly, as in the browser preview
/// (the browser preview's computeLanes). For each pair of spawn points, the lanes are moved as
/// little as possible from the scene's spot until the target's footprint (seen from above, as it
/// falls straight down) overlaps the sweep of its own kick, stays clear of the paddles (and tools)
/// at rest, and touches the other kicks' sweeps as little as its size allows. The two lanes of a
/// pair mirror each other about the midline between the resting paddles, and the pair with the
/// longest shared timing window wins, so one well-timed kick pops both.
///
/// The solver works the way the preview's does, step for step, so both pick the same lanes: in
/// double precision, in the preview's frame (Unity's mirrored on X, as glTF and three.js see it),
/// with loops that step by adding (x += cell), and with a stable sort. Lanes often sit a couple of
/// centimetres from another kick's sweep, so a different rounding of the 1 cm grid picks other lanes.
public static class KickLanes
{
    const double Cell = 0.01;   // 1 cm grid
    const int    Pad  = 2;      // forbidden areas grow by 2 cells

    const int Rest = 3;         // sample sets: 0..2 = PaddleKick, 3 = rest
    static readonly int[] KickOrder = { (int)PaddleKick.Front, (int)PaddleKick.Side, (int)PaddleKick.Wide };

    struct LegPose { public Matrix4x4 paddle; public Matrix4x4 tool; public bool hasTool; public Vector3 foot; }
    struct Pose { public double t; public LegPose[] legs; }
    struct Cand { public double x, xb, z, d; public int wrong; }

    /// <param name="box">The target's bounds around the bubble origin.</param>
    /// <param name="homes">The scene's spawn points, paired (0,1), (2,3), ...</param>
    /// <param name="pairKicks">The kick that should pop each pair.</param>
    /// <param name="fallSpeed">How fast targets fall, in m/s.</param>
    /// <param name="includeTools">Count the themed tools' sweep (layer 4).</param>
    /// <returns>One position per spawn point, or null if no lane fits.</returns>
    public static Vector3[] Compute(PaddleKicker kicker, Bounds box, IReadOnlyList<Vector3> homes,
                                    IReadOnlyList<PaddleKick> pairKicks, float fallSpeed, bool includeTools)
    {
        if (kicker == null || !kicker.HasLegs || homes == null || homes.Count < 2) return null;
        try { return Solve(kicker, box, homes, pairKicks, fallSpeed, includeTools); }
        finally { kicker.RestorePose(); }
    }

    static Vector3[] Solve(PaddleKicker kicker, Bounds box, IReadOnlyList<Vector3> homes,
                           IReadOnlyList<PaddleKick> pairKicks, float fallSpeed, bool includeTools)
    {
        int legCount = PaddleKicker.LegCount;
        double duration = PaddleKickPose.Duration;

        // The target's box in the preview's frame (x mirrored).
        double bMinX = -box.max.x, bMaxX = -box.min.x, bMinZ = box.min.z, bMaxZ = box.max.z;

        // Footprints (x, z) of paddles, feet and tools at rest and through each kick.
        var pts = new List<double>[4];
        for (int i = 0; i < 4; i++) pts[i] = new List<double>();
        void Add(int key, Vector3 v) { pts[key].Add(-(double)v.x); pts[key].Add(v.z); }
        void Sample(int key)
        {
            for (int l = 0; l < legCount; l++)
            {
                Transform paddle = kicker.Paddle(l);
                for (int i = 0; i <= 20; i++)
                    for (int j = 0; j <= 20; j++)
                        Add(key, paddle.TransformPoint(i / 20f - 0.5f, j / 20f - 0.5f, 0f));
                Add(key, kicker.Foot(l).position);
                if (includeTools && kicker.TryGetTool(l, out Matrix4x4 tm, out Bounds tb))
                {
                    Vector3 lo = tb.min, hi = tb.max;
                    for (int i = 0; i <= 8; i++)
                        for (int j = 0; j <= 8; j++)
                            for (int k = 0; k <= 8; k++)
                                Add(key, tm.MultiplyPoint3x4(new Vector3(
                                    lo.x + (hi.x - lo.x) * i / 8f,
                                    lo.y + (hi.y - lo.y) * j / 8f,
                                    lo.z + (hi.z - lo.z) * k / 8f)));
                }
            }
        }
        kicker.SetPose(false, PaddleKick.Front, 0f);
        Sample(Rest);
        foreach (int kick in KickOrder)
            for (double t = 0; t < duration; t += 0.01)
            {
                kicker.SetPose(true, (PaddleKick)kick, (float)t);
                Sample(kick);
            }

        double minX = double.PositiveInfinity, maxX = double.NegativeInfinity;
        double minZ = double.PositiveInfinity, maxZ = double.NegativeInfinity;
        foreach (var a in pts)
            for (int i = 0; i < a.Count; i += 2)
            {
                minX = Math.Min(minX, a[i]); maxX = Math.Max(maxX, a[i]);
                minZ = Math.Min(minZ, a[i + 1]); maxZ = Math.Max(maxZ, a[i + 1]);
            }
        double margin = 0.6 + Math.Max(bMaxX - bMinX, bMaxZ - bMinZ);   // room for the target beside the sweeps
        minX -= margin; maxX += margin; minZ -= margin; maxZ += margin;
        int W = (int)Math.Ceiling((maxX - minX) / Cell) + 1, H = (int)Math.Ceiling((maxZ - minZ) / Cell) + 1;

        SummedArea Mask(int[] keys, int grow)
        {
            var g = new byte[W * H];
            foreach (int key in keys)
            {
                var a = pts[key];
                for (int i = 0; i < a.Count; i += 2)
                {
                    // Math.floor(v + 0.5) is JavaScript's Math.round.
                    int cx = (int)Math.Floor((a[i] - minX) / Cell + 0.5), cz = (int)Math.Floor((a[i + 1] - minZ) / Cell + 0.5);
                    for (int dz = -grow; dz <= grow; dz++)
                        for (int dx = -grow; dx <= grow; dx++)
                        {
                            int x = cx + dx, z = cz + dz;
                            if (x >= 0 && z >= 0 && x < W && z < H) g[z * W + x] = 1;
                        }
                }
            }
            return new SummedArea(g, W, H);
        }

        // Pose samples of each kick, to time a kick against falling targets.
        var poses = new List<Pose>[3];
        foreach (int kick in KickOrder)
        {
            poses[kick] = new List<Pose>();
            for (double t = 0; t < duration; t += 0.025)
            {
                kicker.SetPose(true, (PaddleKick)kick, (float)t);
                var legs = new LegPose[legCount];
                for (int l = 0; l < legCount; l++)
                {
                    legs[l].paddle = kicker.Paddle(l).localToWorldMatrix;
                    legs[l].foot = kicker.Foot(l).position;
                    legs[l].hasTool = includeTools && kicker.TryGetTool(l, out legs[l].tool, out _);
                }
                poses[kick].Add(new Pose { t = t, legs = legs });
            }
        }
        Bounds toolBox = default;
        if (includeTools) for (int l = 0; l < legCount; l++) if (kicker.TryGetTool(l, out _, out toolBox)) break;

        // Which kick start heights pop a target at lane (x, z): the target falls while the kick plays.
        var y0s = new List<double>();
        for (double y = 1.0; y >= -4.0; y -= 0.04) y0s.Add(y);
        float footR2 = kicker.footRadius * kicker.footRadius;
        double animSpeed = kicker.animationSpeed;
        bool[] PopsAt(int kick, double x, double z)   // x in the preview's frame
        {
            var result = new bool[y0s.Count];
            for (int yi = 0; yi < y0s.Count; yi++)
                foreach (var p in poses[kick])
                {
                    double y = y0s[yi] - fallSpeed * p.t / animSpeed;
                    var wb = new Bounds(box.center + new Vector3((float)-x, (float)y, (float)z), box.size);
                    bool hit = false;
                    foreach (var l in p.legs)
                        if (wb.SqrDistance(l.foot) <= footR2
                            || KickGeometry.ObbOverlapsBox(l.paddle, KickGeometry.PaddleBox, wb)
                            || (l.hasTool && KickGeometry.ObbOverlapsBox(l.tool, toolBox, wb))) { hit = true; break; }
                    if (hit) { result[yi] = true; break; }
                }
            return result;
        }

        // Symmetric kicks, symmetric lanes: each pair mirrors about the midline between the resting paddles.
        kicker.SetPose(false, PaddleKick.Front, 0f);
        double xm = 0;
        for (int l = 0; l < legCount; l++) xm += -(double)kicker.Paddle(l).position.x;
        xm /= legCount;

        bool CellsOf(double x, double z, out int x0, out int z0, out int x1, out int z1)
        {
            x0 = (int)Math.Floor((x + bMinX - minX) / Cell); x1 = (int)Math.Ceiling((x + bMaxX - minX) / Cell);
            z0 = (int)Math.Floor((z + bMinZ - minZ) / Cell); z1 = (int)Math.Ceiling((z + bMaxZ - minZ) / Cell);
            return !(x0 < 0 || z0 < 0 || x1 > W || z1 > H);
        }

        var lanes = new Vector3[homes.Count];
        int pairs = homes.Count / 2;
        double w = bMaxX - bMinX;
        const double Gap = 0.02;
        for (int pair = 0; pair < pairs; pair++)
        {
            int k = (int)(pairKicks != null && pair < pairKicks.Count ? pairKicks[pair] : (PaddleKick)(2 - pair % 3));
            // Hard rules for both lanes: never touch a paddle (or tool) at rest; the lane's own kick
            // must reach it; no other kick may reach it where the target's size allows.
            var own   = Mask(new[] { k }, 0);
            var rest  = Mask(new[] { Rest }, Pad);
            var other = Mask(KickOrder.Where(o => o != k).ToArray(), Pad);
            double hAx = -(double)homes[pair * 2].x,     hAz = homes[pair * 2].z;
            double hBx = -(double)homes[pair * 2 + 1].x, hBz = homes[pair * 2 + 1].z;

            var cands = new List<Cand>();
            int homeSide = Math.Sign(hAx - xm);
            for (double z = minZ; z <= maxZ; z += Cell)
                for (double x = minX; x <= maxX; x += Cell)
                {
                    if (Math.Sign(x - xm) != homeSide) continue;
                    double xb = 2 * xm - x;
                    if (Math.Abs(x - xb) < w + Gap) continue;   // the pair must not overlap
                    if (!CellsOf(x, z, out int ax0, out int az0, out int ax1, out int az1)) continue;
                    if (!CellsOf(xb, z, out int bx0, out int bz0, out int bx1, out int bz1)) continue;
                    if (rest.Sum(ax0, az0, ax1, az1) > 0 || rest.Sum(bx0, bz0, bx1, bz1) > 0) continue;
                    if (own.Sum(ax0, az0, ax1, az1) == 0 || own.Sum(bx0, bz0, bx1, bz1) == 0) continue;
                    int wrong = other.Sum(ax0, az0, ax1, az1) + other.Sum(bx0, bz0, bx1, bz1);
                    double d = Hypot(x - hAx, z - hAz) + Hypot(xb - hBx, z - hBz);
                    cands.Add(new Cand { x = x, xb = xb, z = z, wrong = wrong, d = d });
                }
            if (cands.Count == 0) return null;

            int leastWrong = cands.Min(c => c.wrong);
            var pool = cands.Where(c => c.wrong == leastWrong).OrderBy(c => c.d).ToList();   // stable, like JS sort

            // A spread of candidates, nearest first: a long shared timing window, few timings that
            // pop only one of the pair, then closeness.
            var tried = new List<Cand>();
            bool found = false;
            double bestScore = 0;
            Cand best = default;
            foreach (var c in pool)
            {
                if (tried.Any(o => Hypot(o.x - c.x, o.z - c.z) < 0.04)) continue;
                tried.Add(c);
                bool[] pa = PopsAt(k, c.x, c.z), pb = PopsAt(k, c.xb, c.z);
                int run = LongestRun(pa, pb);
                int oneOnly = 0;
                for (int i = 0; i < pa.Length; i++) if (pa[i] != pb[i]) oneOnly++;
                double score = run * 10 - oneOnly * 6 - c.d;
                if (run > 0 && (!found || score > bestScore)) { found = true; bestScore = score; best = c; }
                if (tried.Count >= 150) break;
            }
            if (!found) return null;
            lanes[pair * 2]     = new Vector3((float)-best.x,  homes[pair * 2].y,     (float)best.z);
            lanes[pair * 2 + 1] = new Vector3((float)-best.xb, homes[pair * 2 + 1].y, (float)best.z);
        }
        return lanes;
    }

    static int LongestRun(bool[] a, bool[] b)
    {
        int best = 0, run = 0;
        for (int i = 0; i < a.Length; i++) { run = a[i] && b[i] ? run + 1 : 0; best = Math.Max(best, run); }
        return best;
    }

    // Math.hypot, scaled the same way, so distances round like the preview's.
    static double Hypot(double a, double b)
    {
        a = Math.Abs(a); b = Math.Abs(b);
        double m = Math.Max(a, b);
        if (m == 0) return 0;
        double r = Math.Min(a, b) / m;
        return m * Math.Sqrt(1 + r * r);
    }

    sealed class SummedArea
    {
        readonly int[] _s;
        readonly int _stride;

        public SummedArea(byte[] g, int w, int h)
        {
            _stride = w + 1;
            _s = new int[(w + 1) * (h + 1)];
            for (int z = 0; z < h; z++)
                for (int x = 0; x < w; x++)
                    _s[(z + 1) * _stride + x + 1] = g[z * w + x] + _s[z * _stride + x + 1]
                                                  + _s[(z + 1) * _stride + x] - _s[z * _stride + x];
        }

        /// Number of set cells in [x0, x1) x [z0, z1).
        public int Sum(int x0, int z0, int x1, int z1) =>
            _s[z1 * _stride + x1] - _s[z0 * _stride + x1] - _s[z1 * _stride + x0] + _s[z0 * _stride + x0];
    }
}

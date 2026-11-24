using System.Collections.Generic;
using UnityEngine;

// Shared by the Road Builder and the Road Mesh Builder, so both shape roads the same way:
//  - roads get narrower where buildings are close (old-town streets),
//  - the width varies a little along the road, so roads are not perfectly uniform,
//  - dead ends narrow down to a rounded tip instead of stopping abruptly.
// Everything depends only on the position, so both tools get the same answer.
public static class RoadShaper
{
    public const float MinHalfWidth = 1.9f;     // narrowest street: 3.8 m wide
    public const float BuildingMargin = 1.5f;   // gap kept between the road edge and a wall (the pavement)
    public const float WidthNoise = 0.12f;      // widths vary by +-12 %
    public const float DeadEndTaper = 8f;       // a dead end narrows over this many meters

    const float Cell = 16f;
    const float MinBuildingArea = 12f;          // same idea as the Building Builder: tiny sheds are ignored
    const float EdgeMargin = 20f;               // road ends this close to the map edge are not dead ends

    struct Segment { public Vector2 a, b; }
    struct Vertex { public int road; public Vector2 pos; }

    static readonly Dictionary<long, List<Segment>> buildingGrid = new Dictionary<long, List<Segment>>();
    static readonly Dictionary<int, List<Vector2>> deadEnds = new Dictionary<int, List<Vector2>>();

    // Call once after loading the OSM data
    public static void Prepare(OsmWorldData data)
    {
        buildingGrid.Clear();
        deadEnds.Clear();

        foreach (var b in data.buildings)
        {
            Vector2[] f = b.footprint;
            if (f == null || f.Length < 3) continue;
            if (Mathf.Abs(Area(f)) < MinBuildingArea) continue;
            for (int i = 0; i < f.Length; i++) AddSegment(f[i], f[(i + 1) % f.Length]);
        }
        FindDeadEnds(data);
    }

    public static bool IsDrivable(OsmRoad r)
    {
        float w, s; int l;
        return r.points != null && r.points.Length >= 2 && RoadBuilder.RoadSpec(r, out w, out s, out l) && !r.bridge && !r.tunnel;
    }

    // Half width of the road at a position, given the width the road type asks for
    public static float HalfWidth(int roadIndex, Vector2 p, float baseHalf)
    {
        float noise = (Mathf.PerlinNoise(p.x * 0.07f + 31.7f, p.y * 0.07f + 12.3f) - 0.5f) * 2f;   // -1 to 1
        float half = baseHalf * (1f + WidthNoise * noise);

        float d = DistanceToBuilding(p);
        if (d < float.MaxValue) half = Mathf.Min(half, Mathf.Max(MinHalfWidth, d - BuildingMargin));

        List<Vector2> ends;
        if (deadEnds.TryGetValue(roadIndex, out ends))
            foreach (var e in ends)
            {
                float t = Vector2.Distance(p, e) / DeadEndTaper;
                if (t < 1f) half = Mathf.Max(0.9f, half * Mathf.Lerp(0.4f, 1f, Mathf.SmoothStep(0f, 1f, t)));
            }
        return half;
    }

    public static bool NearDeadEnd(int roadIndex, Vector2 p, float dist)
    {
        List<Vector2> ends;
        if (!deadEnds.TryGetValue(roadIndex, out ends)) return false;
        foreach (var e in ends)
            if ((p - e).sqrMagnitude < dist * dist) return true;
        return false;
    }

    // Smooths values along a road (moving average over 5 points)
    public static void Smooth(float[] a, int passes)
    {
        if (a.Length < 3) return;
        var tmp = new float[a.Length];
        for (int pass = 0; pass < passes; pass++)
        {
            for (int i = 0; i < a.Length; i++)
            {
                float sum = 0f; int cnt = 0;
                for (int k = -2; k <= 2; k++)
                {
                    int j = i + k;
                    if (j < 0 || j >= a.Length) continue;
                    sum += a[j]; cnt++;
                }
                tmp[i] = sum / cnt;
            }
            System.Array.Copy(tmp, a, a.Length);
        }
    }

    // Distance to the nearest building wall within about 16 m, or float.MaxValue if none
    public static float DistanceToBuilding(Vector2 p)
    {
        float best = float.MaxValue;
        int gx = Mathf.FloorToInt(p.x / Cell), gz = Mathf.FloorToInt(p.y / Cell);
        for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
            {
                List<Segment> list;
                if (!buildingGrid.TryGetValue(Key(gx + dx, gz + dz), out list)) continue;
                foreach (var s in list)
                {
                    float d = PointSegment(p, s.a, s.b);
                    if (d < best) best = d;
                }
            }
        return best;
    }

    // ---------------------------------------------------------------- internals

    static long Key(int gx, int gz) { return ((long)gx << 32) | (uint)gz; }

    static float Area(Vector2[] p)
    {
        float sum = 0f;
        for (int i = 0; i < p.Length; i++)
        {
            Vector2 a = p[i], b = p[(i + 1) % p.Length];
            sum += a.x * b.y - b.x * a.y;
        }
        return sum * 0.5f;
    }

    static void AddSegment(Vector2 a, Vector2 b)
    {
        int x0 = Mathf.FloorToInt(Mathf.Min(a.x, b.x) / Cell), x1 = Mathf.FloorToInt(Mathf.Max(a.x, b.x) / Cell);
        int z0 = Mathf.FloorToInt(Mathf.Min(a.y, b.y) / Cell), z1 = Mathf.FloorToInt(Mathf.Max(a.y, b.y) / Cell);
        var seg = new Segment { a = a, b = b };
        for (int z = z0; z <= z1; z++)
            for (int x = x0; x <= x1; x++)
            {
                long key = Key(x, z);
                List<Segment> list;
                if (!buildingGrid.TryGetValue(key, out list)) { list = new List<Segment>(); buildingGrid[key] = list; }
                list.Add(seg);
            }
    }

    static float PointSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float len2 = ab.sqrMagnitude;
        if (len2 < 1e-8f) return Vector2.Distance(p, a);
        float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2);
        return Vector2.Distance(p, a + ab * t);
    }

    // A road end is a dead end if no other drivable road has a node there.
    static void FindDeadEnds(OsmWorldData data)
    {
        const float VCell = 4f;
        var grid = new Dictionary<long, List<Vertex>>();
        float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;

        for (int r = 0; r < data.roads.Length; r++)
        {
            OsmRoad road = data.roads[r];
            if (!IsDrivable(road)) continue;
            foreach (var p in road.points)
            {
                minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x);
                minZ = Mathf.Min(minZ, p.y); maxZ = Mathf.Max(maxZ, p.y);
                long key = Key(Mathf.FloorToInt(p.x / VCell), Mathf.FloorToInt(p.y / VCell));
                List<Vertex> list;
                if (!grid.TryGetValue(key, out list)) { list = new List<Vertex>(); grid[key] = list; }
                list.Add(new Vertex { road = r, pos = p });
            }
        }

        for (int r = 0; r < data.roads.Length; r++)
        {
            OsmRoad road = data.roads[r];
            if (!IsDrivable(road)) continue;
            Vector2 first = road.points[0], last = road.points[road.points.Length - 1];
            if ((first - last).sqrMagnitude < 0.36f) continue;   // closed loop, no ends

            foreach (Vector2 end in new[] { first, last })
            {
                if (end.x < minX + EdgeMargin || end.x > maxX - EdgeMargin ||
                    end.y < minZ + EdgeMargin || end.y > maxZ - EdgeMargin) continue;   // cut by the map edge
                if (TouchesOtherRoad(grid, end, r, VCell)) continue;

                List<Vector2> ends;
                if (!deadEnds.TryGetValue(r, out ends)) { ends = new List<Vector2>(); deadEnds[r] = ends; }
                ends.Add(end);
            }
        }
    }

    static bool TouchesOtherRoad(Dictionary<long, List<Vertex>> grid, Vector2 p, int road, float cell)
    {
        int gx = Mathf.FloorToInt(p.x / cell), gz = Mathf.FloorToInt(p.y / cell);
        for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
            {
                List<Vertex> list;
                if (!grid.TryGetValue(Key(gx + dx, gz + dz), out list)) continue;
                foreach (var v in list)
                    if (v.road != road && (v.pos - p).sqrMagnitude < 0.36f) return true;   // within 0.6 m
            }
        return false;
    }
}

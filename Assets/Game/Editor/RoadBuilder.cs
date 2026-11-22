using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Editor tool: cuts the OSM roads into the built terrain chunks (carve + paint).
// Order: 1) Terrain Builder   2) Road Builder.
// To get a clean result, rebuild the terrain first, then build the roads.
public class RoadBuilder : EditorWindow
{
    class ChunkInfo
    {
        public Terrain terrain;
        public TerrainData td;
        public float ox, oz, baseY;
    }

    struct Sample
    {
        public Vector2 pos;
        public float halfWidth, shoulder;
        public int layer;      // terrain layer used for the road surface
        public float elev;     // later replaced by the smoothed value
    }

    WorldConfig config;
    string osmJsonPath = "Assets/Game/RawData/SidiBouSaid/osm_world.json";
    float spacing = 1.5f;        // meters between road samples
    float profileRadius = 12f;   // road height = average terrain height within this distance
    int profilePasses = 3;
    float widthScale = 1.15f;    // slightly wider than real life: easier to drive

    Dictionary<Vector2Int, ChunkInfo> chunks;
    OsmWorldData data;
    Vector2 scroll;

    [MenuItem("RacerTN/Road Builder")]
    static void Open() { GetWindow<RoadBuilder>("Road Builder"); }

    void OnGUI()
    {
        scroll = EditorGUILayout.BeginScrollView(scroll);
        config = (WorldConfig)EditorGUILayout.ObjectField("World Config", config, typeof(WorldConfig), false);
        osmJsonPath = EditorGUILayout.TextField("OSM JSON", osmJsonPath);
        spacing = EditorGUILayout.FloatField("Sample spacing (m)", spacing);
        profileRadius = EditorGUILayout.FloatField("Smoothing radius (m)", profileRadius);
        profilePasses = EditorGUILayout.IntField("Smoothing passes", profilePasses);
        widthScale = EditorGUILayout.FloatField("Road width scale", widthScale);

        EditorGUILayout.Space();
        if (GUILayout.Button("Build roads", GUILayout.Height(30))) Run(Build);
        if (GUILayout.Button("Drop the Car on a main road", GUILayout.Height(24))) Run(DropCarOnRoad);
        EditorGUILayout.HelpBox("Run the Terrain Builder first. To redo the roads cleanly, rebuild the terrain, then build the roads again.", MessageType.Info);
        EditorGUILayout.EndScrollView();
    }

    void Run(Action action)
    {
        try { action(); }
        catch (Exception e) { Debug.LogError("RoadBuilder failed: " + e); }
        finally { EditorUtility.ClearProgressBar(); }
    }

    // ---------------------------------------------------------------- loading

    bool LoadChunks()
    {
        chunks = new Dictionary<Vector2Int, ChunkInfo>();
        GameObject root = GameObject.Find("World");
        if (root == null) { Debug.LogError("No 'World' object in the scene. Run the Terrain Builder first."); return false; }

        float cs = config.chunkSize;
        foreach (var t in root.GetComponentsInChildren<Terrain>())
        {
            Vector3 p = t.transform.position;
            var key = new Vector2Int(Mathf.RoundToInt(p.x / cs), Mathf.RoundToInt(p.z / cs));
            chunks[key] = new ChunkInfo { terrain = t, td = t.terrainData, ox = p.x, oz = p.z, baseY = p.y };
        }
        if (chunks.Count == 0) { Debug.LogError("No terrain chunks found under 'World'."); return false; }
        return true;
    }

    bool Load(bool needOsm)
    {
        if (config == null) { Debug.LogError("Assign the WorldConfig first."); return false; }
        if (needOsm)
        {
            string full = Path.GetFullPath(osmJsonPath);
            if (!File.Exists(full)) { Debug.LogError("OSM JSON not found: " + full); return false; }
            data = JsonUtility.FromJson<OsmWorldData>(File.ReadAllText(full));
            if (data == null || data.roads == null) { Debug.LogError("The OSM JSON is empty or unreadable."); return false; }
        }
        return LoadChunks();
    }

    // Terrain height (world meters) at a world position, or false if no chunk covers it
    bool TryHeight(float x, float z, out float h)
    {
        float cs = config.chunkSize;
        ChunkInfo c;
        if (!chunks.TryGetValue(new Vector2Int(Mathf.FloorToInt(x / cs), Mathf.FloorToInt(z / cs)), out c))
        {
            h = 0f;
            return false;
        }
        float u = Mathf.Clamp01((x - c.ox) / cs), v = Mathf.Clamp01((z - c.oz) / cs);
        h = c.td.GetInterpolatedHeight(u, v) + c.baseY;
        return true;
    }

    // ---------------------------------------------------------------- main build

    void Build()
    {
        if (!Load(true)) return;
        float cs = config.chunkSize;

        // 1) Resample the drivable roads into points
        var samples = new List<Sample>();
        var tmp = new List<Vector2>();
        int usedRoads = 0, skippedRoads = 0;
        foreach (var road in data.roads)
        {
            float width, shoulder; int layer;
            if (!RoadSpec(road, out width, out shoulder, out layer) || road.bridge || road.tunnel)
            {
                skippedRoads++;
                continue;
            }
            width *= widthScale;
            tmp.Clear();
            Resample(road.points, spacing, tmp);
            bool any = false;
            foreach (var pt in tmp)
            {
                float hh;
                if (!TryHeight(pt.x, pt.y, out hh)) continue;   // outside the built chunks
                samples.Add(new Sample { pos = pt, halfWidth = width * 0.5f, shoulder = shoulder, layer = layer, elev = hh });
                any = true;
            }
            if (any) usedRoads++;
        }
        int n = samples.Count;
        if (n == 0) { Debug.LogError("No road points fall inside the built chunks."); return; }

        // 2) Smooth the road height: average of the terrain heights of all road points nearby.
        //    Because it is a spatial average, roads that meet at a junction get the same height.
        var elev = new float[n];
        for (int i = 0; i < n; i++) elev[i] = samples[i].elev;

        float cell = profileRadius;
        var grid = new Dictionary<long, List<int>>();
        for (int i = 0; i < n; i++)
        {
            long key = Key(Mathf.FloorToInt(samples[i].pos.x / cell), Mathf.FloorToInt(samples[i].pos.y / cell));
            List<int> list;
            if (!grid.TryGetValue(key, out list)) { list = new List<int>(); grid[key] = list; }
            list.Add(i);
        }

        float radius2 = profileRadius * profileRadius;
        for (int pass = 0; pass < profilePasses; pass++)
        {
            EditorUtility.DisplayProgressBar("Road Builder", "Smoothing road heights " + (pass + 1) + "/" + profilePasses, 0.2f + pass * 0.1f);
            var next = new float[n];
            for (int i = 0; i < n; i++)
            {
                Vector2 p = samples[i].pos;
                int gx = Mathf.FloorToInt(p.x / cell), gz = Mathf.FloorToInt(p.y / cell);
                float sum = 0f; int cnt = 0;
                for (int dz = -1; dz <= 1; dz++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        List<int> list;
                        if (!grid.TryGetValue(Key(gx + dx, gz + dz), out list)) continue;
                        foreach (int j in list)
                            if ((samples[j].pos - p).sqrMagnitude <= radius2) { sum += elev[j]; cnt++; }
                    }
                next[i] = cnt > 0 ? sum / cnt : elev[i];
            }
            elev = next;
        }

        // 3) Carve and paint every chunk
        int chunkNo = 0;
        long carved = 0;
        foreach (var kv in chunks)
        {
            EditorUtility.DisplayProgressBar("Road Builder", "Carving chunk " + kv.Key.x + ", " + kv.Key.y, 0.5f + 0.5f * chunkNo / chunks.Count);
            chunkNo++;

            ChunkInfo c = kv.Value;
            TerrainData td = c.td;
            int R = td.heightmapResolution;
            float step = cs / (R - 1);
            float sizeY = td.size.y;

            var heights = td.GetHeights(0, 0, R, R);
            var weight = new float[R, R];
            var target = new float[R, R];
            var layerMap = new byte[R, R];
            var bestScore = new float[R, R];

            for (int s = 0; s < n; s++)
            {
                Sample sm = samples[s];
                float outer = sm.halfWidth + sm.shoulder;
                if (sm.pos.x + outer < c.ox || sm.pos.x - outer > c.ox + cs ||
                    sm.pos.y + outer < c.oz || sm.pos.y - outer > c.oz + cs) continue;

                int ci = Mathf.RoundToInt((sm.pos.x - c.ox) / step);
                int cj = Mathf.RoundToInt((sm.pos.y - c.oz) / step);
                int rad = Mathf.CeilToInt(outer / step) + 1;
                float t = Mathf.Clamp01((elev[s] - c.baseY) / sizeY);

                for (int dj = -rad; dj <= rad; dj++)
                {
                    int j = cj + dj;
                    if (j < 0 || j >= R) continue;
                    for (int di = -rad; di <= rad; di++)
                    {
                        int i = ci + di;
                        if (i < 0 || i >= R) continue;
                        float px = c.ox + i * step - sm.pos.x;
                        float pz = c.oz + j * step - sm.pos.y;
                        float d = Mathf.Sqrt(px * px + pz * pz);
                        if (d > outer) continue;
                        float w = d <= sm.halfWidth ? 1f : 1f - Mathf.SmoothStep(0f, 1f, (d - sm.halfWidth) / sm.shoulder);
                        float score = w - d * 0.001f;   // highest weight wins, nearest road point breaks ties
                        if (score > bestScore[j, i])
                        {
                            bestScore[j, i] = score;
                            weight[j, i] = w;
                            target[j, i] = t;
                            layerMap[j, i] = (byte)sm.layer;
                        }
                    }
                }
            }

            for (int j = 0; j < R; j++)
                for (int i = 0; i < R; i++)
                {
                    float w = weight[j, i];
                    if (w <= 0f) continue;
                    heights[j, i] = Mathf.Lerp(heights[j, i], target[j, i], w);
                    carved++;
                }
            td.SetHeights(0, 0, heights);

            // Paint the road surface into the alphamap
            int A = td.alphamapResolution;
            var maps = td.GetAlphamaps(0, 0, A, A);
            int layers = maps.GetLength(2);
            if (layers >= 4)
            {
                for (int j = 0; j < A; j++)
                    for (int i = 0; i < A; i++)
                    {
                        float w = weight[j, i];
                        if (w <= 0f) continue;
                        float roadW = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.45f, 0.9f, w));
                        if (roadW <= 0f) continue;
                        for (int k = 0; k < layers; k++) maps[j, i, k] *= 1f - roadW;
                        maps[j, i, layerMap[j, i]] += roadW;
                    }
                td.SetAlphamaps(0, 0, maps);
            }
            else
            {
                Debug.LogWarning("Chunk " + kv.Key + " has fewer than 4 terrain layers. Run the Terrain Builder with all 4 layers assigned.");
            }

            EditorUtility.SetDirty(td);
            var col = c.terrain.GetComponent<TerrainCollider>();
            if (col != null) col.terrainData = td;
            c.terrain.Flush();
        }

        AssetDatabase.SaveAssets();
        EditorSceneManager.MarkAllScenesDirty();
        Debug.Log("Roads built: " + usedRoads + " roads used, " + skippedRoads + " skipped (footways, paths, bridges, tunnels), "
                  + n + " road points, " + carved + " terrain samples carved.");
    }

    // ---------------------------------------------------------------- helpers

    static long Key(int gx, int gz) { return ((long)gx << 32) | (uint)gz; }

    // Width, shoulder and surface layer by road type. Returns false for roads cars cannot use.
    public static bool RoadSpec(OsmRoad r, out float width, out float shoulder, out int layer)
    {
        layer = 3;      // 3 = asphalt, 0 = sand/dirt
        shoulder = 4f;
        switch (r.type)
        {
            case "motorway":
            case "trunk":          width = 14f; shoulder = 8f; break;
            case "motorway_link":
            case "trunk_link":     width = 8f;  shoulder = 6f; break;
            case "primary":        width = 10f; shoulder = 6f; break;
            case "primary_link":
            case "secondary_link":
            case "tertiary_link":  width = 7f;  shoulder = 5f; break;
            case "secondary":      width = 9f;  shoulder = 5f; break;
            case "tertiary":       width = 8f;  shoulder = 5f; break;
            case "unclassified":
            case "residential":    width = 6.5f; shoulder = 4f; break;
            case "living_street":  width = 5f;  shoulder = 3f; break;
            case "service":        width = 4.5f; shoulder = 3f; break;
            case "track":          width = 3.5f; shoulder = 3f; layer = 0; break;
            default:               width = 0f;  shoulder = 0f; return false;
        }
        if (r.lanes > 0) width = Mathf.Max(width, r.lanes * 3.3f);
        return true;
    }

    static void Resample(Vector2[] pts, float spacing, List<Vector2> output)
    {
        float carry = 0f;
        for (int i = 0; i < pts.Length - 1; i++)
        {
            Vector2 a = pts[i], b = pts[i + 1];
            float len = Vector2.Distance(a, b);
            if (len < 0.001f) continue;
            Vector2 dir = (b - a) / len;
            float d = carry;
            while (d < len)
            {
                output.Add(a + dir * d);
                d += spacing;
            }
            carry = d - len;
        }
        output.Add(pts[pts.Length - 1]);
    }

    static bool IsMainRoad(string type)
    {
        return type == "secondary" || type == "tertiary" || type == "primary" || type == "trunk" ||
               type == "residential" || type == "unclassified";
    }

    // Puts the car on the main road closest to the anchor, facing along the road
    void DropCarOnRoad()
    {
        if (!Load(true)) return;
        var car = FindAnyObjectByType<CarController>();
        if (car == null) { Debug.LogError("No CarController found in the scene."); return; }

        Vector2 anchor = new Vector2(config.anchorGameX, config.anchorGameZ);
        float best = float.MaxValue;
        Vector2 bestPos = anchor, bestDir = Vector2.up;
        bool found = false;

        foreach (var road in data.roads)
        {
            if (!IsMainRoad(road.type) || road.bridge || road.tunnel) continue;
            for (int i = 0; i < road.points.Length - 1; i++)
            {
                Vector2 a = road.points[i], b = road.points[i + 1];
                float len = Vector2.Distance(a, b);
                if (len < 12f) continue;
                Vector2 mid = (a + b) * 0.5f;
                float tmpH;
                if (!TryHeight(mid.x, mid.y, out tmpH)) continue;
                float d = (mid - anchor).sqrMagnitude;
                if (d < best) { best = d; bestPos = mid; bestDir = (b - a) / len; found = true; }
            }
        }
        if (!found) { Debug.LogError("No main road found inside the built chunks."); return; }

        float h;
        TryHeight(bestPos.x, bestPos.y, out h);
        car.transform.SetPositionAndRotation(new Vector3(bestPos.x, h + 1.5f, bestPos.y),
                                             Quaternion.LookRotation(new Vector3(bestDir.x, 0f, bestDir.y)));
        Debug.Log("Car placed on a main road at " + bestPos.x.ToString("F0") + ", " + bestPos.y.ToString("F0"));
    }
}
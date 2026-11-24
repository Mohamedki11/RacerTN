using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

// Editor tool: builds one Unity Terrain per chunk from the real elevation file.
// All heights come from ONE world-space function, so chunks agree at their edges.
// NEW: paints ground cover (town paving, forest floor, farmland, parks, beaches) from the OSM areas.
// Order: 1) Terrain Builder  2) Road Builder  3) Road Mesh Builder  4) Building Builder
public class TerrainBuilder : EditorWindow
{
    const int Res = 1025;   // height samples per chunk side (1024 cells)
    const string ChunkFolder = "Assets/Game/Chunks";

    [SerializeField] WorldConfig config;
    [SerializeField] string elevationPath = "Assets/Game/RawData/SidiBouSaid/elevation.bytes";
    [SerializeField] string osmJsonPath = "Assets/Game/RawData/SidiBouSaid/osm_world.json";
    [SerializeField] int minChunkX = 4, minChunkZ = 4, maxChunkX = 5, maxChunkZ = 5;

    [SerializeField] float baseY = -30f;            // world height of the lowest terrain point
    [SerializeField] float maxWorldHeight = 300f;   // world height of the highest terrain point
    [SerializeField] float detailAmplitude = 0.6f;  // meters of fine noise on top of the 10 m data
    [SerializeField] float seaDip = 2f;             // pushes flat sea data below sea level so the water plane doesn't flicker
    [SerializeField] float sandHeight = 1.5f;
    [SerializeField] int seed = 1;
    [SerializeField] bool makeSea = true;

    // The first 4 layers keep their positions (0 sand, 1 grass, 2 rock, 3 asphalt) so the Road Builder still works.
    [SerializeField] TerrainLayer sand, grass, rock, asphalt;

    // NEW: optional extra layers. Leave empty to skip that kind of ground.
    [SerializeField] bool paintGroundCover = true;
    [SerializeField] bool paintTown = true;          // pave whole residential areas
    [SerializeField] int edgeSoftness = 3;           // blur radius in cells (about 1 m each) for soft borders
    [SerializeField] TerrainLayer paved, forestFloor, farmland;

    class PreparedArea
    {
        public Vector2[] pts;
        public float minX, maxX, minZ, maxZ;
        public int layer, priority;
    }

    ElevationGrid grid;
    float noiseOffX, noiseOffZ;
    Vector2 scroll;

    List<TerrainLayer> activeLayers;
    int pavedIdx = -1, forestIdx = -1, farmIdx = -1;
    List<PreparedArea> prepared;

    [MenuItem("RacerTN/Terrain Builder")]
    static void Open() { GetWindow<TerrainBuilder>("Terrain Builder"); }

    void OnGUI()
    {
        scroll = EditorGUILayout.BeginScrollView(scroll);
        config = (WorldConfig)EditorGUILayout.ObjectField("World Config", config, typeof(WorldConfig), false);
        elevationPath = EditorGUILayout.TextField("Elevation file", elevationPath);

        EditorGUILayout.LabelField("Chunk range (inclusive)", EditorStyles.boldLabel);
        minChunkX = EditorGUILayout.IntField("Min chunk X", minChunkX);
        minChunkZ = EditorGUILayout.IntField("Min chunk Z", minChunkZ);
        maxChunkX = EditorGUILayout.IntField("Max chunk X", maxChunkX);
        maxChunkZ = EditorGUILayout.IntField("Max chunk Z", maxChunkZ);

        EditorGUILayout.LabelField("Shape", EditorStyles.boldLabel);
        baseY = EditorGUILayout.FloatField("Lowest world height", baseY);
        maxWorldHeight = EditorGUILayout.FloatField("Highest world height", maxWorldHeight);
        detailAmplitude = EditorGUILayout.FloatField("Detail noise (m)", detailAmplitude);
        seaDip = EditorGUILayout.FloatField("Sea dip (m)", seaDip);
        seed = EditorGUILayout.IntField("Seed", seed);
        makeSea = EditorGUILayout.Toggle("Add sea plane", makeSea);

        EditorGUILayout.LabelField("Painting (base layers)", EditorStyles.boldLabel);
        sandHeight = EditorGUILayout.FloatField("Sand up to height (m)", sandHeight);
        sand = (TerrainLayer)EditorGUILayout.ObjectField("Sand", sand, typeof(TerrainLayer), false);
        grass = (TerrainLayer)EditorGUILayout.ObjectField("Grass", grass, typeof(TerrainLayer), false);
        rock = (TerrainLayer)EditorGUILayout.ObjectField("Rock", rock, typeof(TerrainLayer), false);
        asphalt = (TerrainLayer)EditorGUILayout.ObjectField("Asphalt (used by Road Builder)", asphalt, typeof(TerrainLayer), false);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Ground cover from OSM", EditorStyles.boldLabel);
        paintGroundCover = EditorGUILayout.Toggle("Paint ground cover", paintGroundCover);
        osmJsonPath = EditorGUILayout.TextField("OSM JSON", osmJsonPath);
        paintTown = EditorGUILayout.Toggle("Pave residential areas", paintTown);
        edgeSoftness = EditorGUILayout.IntSlider("Edge softness (cells)", edgeSoftness, 0, 8);
        paved = (TerrainLayer)EditorGUILayout.ObjectField("Paved / town ground", paved, typeof(TerrainLayer), false);
        forestFloor = (TerrainLayer)EditorGUILayout.ObjectField("Forest floor / scrub", forestFloor, typeof(TerrainLayer), false);
        farmland = (TerrainLayer)EditorGUILayout.ObjectField("Farmland / dirt", farmland, typeof(TerrainLayer), false);
        EditorGUILayout.HelpBox("Empty slots are skipped: that kind of area keeps the normal ground. Parks use Grass and beaches use Sand.", MessageType.None);

        EditorGUILayout.Space();
        if (GUILayout.Button("Build terrain chunks", GUILayout.Height(30))) Run(Build);
        if (GUILayout.Button("Drop the Car at the anchor point", GUILayout.Height(24))) Run(DropCar);
        EditorGUILayout.HelpBox("After this, run the Road Builder, then the Road Mesh Builder, then the Building Builder again.", MessageType.Info);
        EditorGUILayout.EndScrollView();
    }

    void Run(Action action)
    {
        try { action(); }
        catch (Exception e) { Debug.LogError("TerrainBuilder failed: " + e); }
        finally { EditorUtility.ClearProgressBar(); }
    }

    bool Prepare()
    {
        if (config == null) { Debug.LogError("Assign the WorldConfig first."); return false; }
        string full = Path.GetFullPath(elevationPath);
        if (!File.Exists(full)) { Debug.LogError("Elevation file not found: " + full); return false; }
        grid = ElevationGrid.Load(full);
        var rng = new System.Random(seed);
        noiseOffX = rng.Next(0, 1000);
        noiseOffZ = rng.Next(0, 1000);
        return true;
    }

    void Build()
    {
        if (!Prepare()) return;
        if (sand == null || grass == null || rock == null || asphalt == null)
        {
            Debug.LogError("Assign the 4 base terrain layers first (the TestLayers will do).");
            return;
        }
        if (!AssetDatabase.IsValidFolder(ChunkFolder)) AssetDatabase.CreateFolder("Assets/Game", "Chunks");

        // Layer list: the 4 base layers first, then the optional ones that are assigned
        activeLayers = new List<TerrainLayer> { sand, grass, rock, asphalt };
        pavedIdx = AddOptionalLayer(paved);
        forestIdx = AddOptionalLayer(forestFloor);
        farmIdx = AddOptionalLayer(farmland);
        LoadAreas();

        GameObject root = GameObject.Find("World");
        if (root == null) root = new GameObject("World");

        var built = new Dictionary<Vector2Int, Terrain>();
        int total = (maxChunkX - minChunkX + 1) * (maxChunkZ - minChunkZ + 1), done = 0;
        float lo = float.MaxValue, hi = float.MinValue;

        for (int cz = minChunkZ; cz <= maxChunkZ; cz++)
            for (int cx = minChunkX; cx <= maxChunkX; cx++)
            {
                EditorUtility.DisplayProgressBar("Terrain Builder", "Chunk " + cx + ", " + cz, done / (float)total);
                float chunkLo, chunkHi;
                built[new Vector2Int(cx, cz)] = BuildChunk(cx, cz, root.transform, out chunkLo, out chunkHi);
                lo = Mathf.Min(lo, chunkLo); hi = Mathf.Max(hi, chunkHi);
                done++;
            }

        foreach (var kv in built)
        {
            Vector2Int c = kv.Key;
            Terrain left, right, top, bottom;
            built.TryGetValue(new Vector2Int(c.x - 1, c.y), out left);
            built.TryGetValue(new Vector2Int(c.x + 1, c.y), out right);
            built.TryGetValue(new Vector2Int(c.x, c.y + 1), out top);
            built.TryGetValue(new Vector2Int(c.x, c.y - 1), out bottom);
            kv.Value.SetNeighbors(left, top, right, bottom);
        }

        if (makeSea) BuildSea(root.transform);

        AssetDatabase.SaveAssets();
        EditorSceneManager.MarkAllScenesDirty();
        Debug.Log("Terrain built: " + built.Count + " chunks. World height range " + lo.ToString("F1") + " to " +
                  hi.ToString("F1") + " m. Layers used: " + activeLayers.Count + ".");
    }

    int AddOptionalLayer(TerrainLayer layer)
    {
        if (layer == null) return -1;
        activeLayers.Add(layer);
        return activeLayers.Count - 1;
    }

    // ---------------------------------------------------------------- ground-cover areas

    // Which terrain layer an OSM area kind is painted with (-1 = leave the normal ground)
    int LayerFor(string kind)
    {
        switch (kind)
        {
            case "town": return paintTown ? pavedIdx : -1;
            case "paved": return pavedIdx;
            case "forest": case "scrub": return forestIdx;
            case "farmland": case "dirt": return farmIdx;
            case "park": case "cemetery": return 1;   // grass
            case "beach": return 0;                   // sand
        }
        return -1;
    }

    // Higher number = painted later = wins where areas overlap
    static int Priority(string kind)
    {
        switch (kind)
        {
            case "town": return 0;
            case "farmland": return 1;
            case "scrub": return 2;
            case "forest": return 3;
            case "dirt": return 4;
            case "park": case "cemetery": return 5;
            case "paved": return 6;
            case "beach": return 7;
        }
        return -1;
    }

    void LoadAreas()
    {
        prepared = new List<PreparedArea>();
        if (!paintGroundCover) return;

        string full = Path.GetFullPath(osmJsonPath);
        if (!File.Exists(full)) { Debug.LogWarning("OSM JSON not found, no ground cover painted: " + full); return; }
        var data = JsonUtility.FromJson<OsmWorldData>(File.ReadAllText(full));
        if (data == null || data.areas == null || data.areas.Length == 0)
        {
            Debug.LogWarning("The OSM JSON has no ground areas. Run RacerTN > OSM Importer again first.");
            return;
        }

        var painted = new Dictionary<string, int>();
        int skipped = 0;
        foreach (var a in data.areas)
        {
            int layer = LayerFor(a.kind);
            if (layer < 0 || a.points == null || a.points.Length < 3) { skipped++; continue; }

            var pa = new PreparedArea { pts = a.points, layer = layer, priority = Priority(a.kind) };
            pa.minX = pa.minZ = float.MaxValue;
            pa.maxX = pa.maxZ = float.MinValue;
            foreach (var p in a.points)
            {
                pa.minX = Mathf.Min(pa.minX, p.x); pa.maxX = Mathf.Max(pa.maxX, p.x);
                pa.minZ = Mathf.Min(pa.minZ, p.y); pa.maxZ = Mathf.Max(pa.maxZ, p.y);
            }
            prepared.Add(pa);
            int count;
            painted.TryGetValue(a.kind, out count);
            painted[a.kind] = count + 1;
        }
        prepared.Sort((x, y) => x.priority.CompareTo(y.priority));

        var parts = new List<string>();
        foreach (var kv in painted) parts.Add(kv.Key + " " + kv.Value);
        Debug.Log("Ground cover: " + prepared.Count + " areas will be painted (" + string.Join(", ", parts.ToArray()) + "). "
                  + skipped + " skipped (no texture assigned for them).");
    }

    // For one chunk: the layer index each cell should get from an OSM area, or -1. Null if no area touches the chunk.
    int[,] BuildAreaMask(float ox, float oz, float step, int A)
    {
        if (prepared == null || prepared.Count == 0) return null;
        float cs = config.chunkSize;
        int[,] mask = null;

        foreach (var a in prepared)
        {
            if (a.maxX < ox || a.minX > ox + cs || a.maxZ < oz || a.minZ > oz + cs) continue;
            int i0 = Mathf.Max(0, Mathf.FloorToInt((a.minX - ox) / step));
            int i1 = Mathf.Min(A - 1, Mathf.CeilToInt((a.maxX - ox) / step));
            int j0 = Mathf.Max(0, Mathf.FloorToInt((a.minZ - oz) / step));
            int j1 = Mathf.Min(A - 1, Mathf.CeilToInt((a.maxZ - oz) / step));
            if (i0 > i1 || j0 > j1) continue;

            if (mask == null)
            {
                mask = new int[A, A];
                for (int j = 0; j < A; j++)
                    for (int i = 0; i < A; i++) mask[j, i] = -1;
            }
            for (int j = j0; j <= j1; j++)
            {
                float z = oz + j * step;
                for (int i = i0; i <= i1; i++)
                    if (Inside(ox + i * step, z, a.pts)) mask[j, i] = a.layer;
            }
        }
        return mask;
    }

    // Is the point inside the outline? (ray casting)
    static bool Inside(float x, float z, Vector2[] p)
    {
        bool inside = false;
        for (int i = 0, j = p.Length - 1; i < p.Length; j = i++)
        {
            if ((p[i].y > z) != (p[j].y > z) &&
                x < (p[j].x - p[i].x) * (z - p[i].y) / (p[j].y - p[i].y) + p[i].x)
                inside = !inside;
        }
        return inside;
    }

    // Softens the borders between ground types (box blur, rows then columns).
    // Every cell still adds up to 1 afterwards.
    static void Blur(float[,,] m, int A, int L, int radius)
    {
        if (radius <= 0) return;
        var tmp = new float[A, A, L];
        float inv = 1f / (2 * radius + 1);

        for (int j = 0; j < A; j++)
            for (int i = 0; i < A; i++)
                for (int k = 0; k < L; k++)
                {
                    float sum = 0f;
                    for (int d = -radius; d <= radius; d++) sum += m[j, Mathf.Clamp(i + d, 0, A - 1), k];
                    tmp[j, i, k] = sum * inv;
                }

        for (int j = 0; j < A; j++)
            for (int i = 0; i < A; i++)
                for (int k = 0; k < L; k++)
                {
                    float sum = 0f;
                    for (int d = -radius; d <= radius; d++) sum += tmp[Mathf.Clamp(j + d, 0, A - 1), i, k];
                    m[j, i, k] = sum * inv;
                }
    }

    // ---------------------------------------------------------------- one chunk

    Terrain BuildChunk(int cx, int cz, Transform parent, out float minH, out float maxH)
    {
        float cs = config.chunkSize;
        float ox = cx * cs, oz = cz * cs;
        float step = cs / (Res - 1);
        float sizeY = maxWorldHeight - baseY;

        // World heights with a 1-sample border (the border is only used for slopes)
        var hh = new float[Res + 2, Res + 2];
        minH = float.MaxValue; maxH = float.MinValue;
        for (int j = 0; j < Res + 2; j++)
            for (int i = 0; i < Res + 2; i++)
            {
                float h = WorldHeight(ox + (i - 1) * step, oz + (j - 1) * step);
                hh[j, i] = h;
                if (h < minH) minH = h;
                if (h > maxH) maxH = h;
            }

        var heights = new float[Res, Res];
        for (int j = 0; j < Res; j++)
            for (int i = 0; i < Res; i++)
                heights[j, i] = Mathf.Clamp01((hh[j + 1, i + 1] - baseY) / sizeY);

        string path = ChunkFolder + "/Terrain_" + cx + "_" + cz + ".asset";
        var td = AssetDatabase.LoadAssetAtPath<TerrainData>(path);
        if (td == null)
        {
            td = new TerrainData();
            AssetDatabase.CreateAsset(td, path);
        }
        td.heightmapResolution = Res;
        td.size = new Vector3(cs, sizeY, cs);
        td.SetHeights(0, 0, heights);

        // Paint: sand near the sea, rock on steep slopes, grass elsewhere.
        // Then the OSM areas (town, forest, farmland, parks, beaches) go on top, except on steep rock.
        td.terrainLayers = activeLayers.ToArray();
        int L = activeLayers.Count;
        int A = Res - 1;
        td.alphamapResolution = A;
        var maps = new float[A, A, L];
        int[,] areaMask = BuildAreaMask(ox, oz, step, A);

        for (int j = 0; j < A; j++)
            for (int i = 0; i < A; i++)
            {
                float x = ox + i * step, z = oz + j * step;
                float h = hh[j + 1, i + 1];
                float dx = (hh[j + 1, i + 2] - hh[j + 1, i]) / (2f * step);
                float dz = (hh[j + 2, i + 1] - hh[j, i + 1]) / (2f * step);
                float slope = Mathf.Atan(Mathf.Sqrt(dx * dx + dz * dz)) * Mathf.Rad2Deg;
                float n = Mathf.PerlinNoise(x * 0.02f + noiseOffX, z * 0.02f + noiseOffZ) - 0.5f;

                float sandW = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(sandHeight + n * 3f, sandHeight + 2.5f + n * 3f, h));
                float rockW = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(28f + n * 8f, 42f + n * 8f, slope));
                float grassW = Mathf.Max(0f, 1f - sandW - rockW);
                float sum = sandW + grassW + rockW;
                if (sum < 0.0001f) { grassW = 1f; sum = 1f; }
                sandW /= sum; grassW /= sum; rockW /= sum;

                int target = areaMask != null ? areaMask[j, i] : -1;
                if (target >= 0)
                {
                    maps[j, i, 2] = rockW;                 // steep slopes stay rocky
                    maps[j, i, target] += 1f - rockW;      // the rest becomes the area's ground
                }
                else
                {
                    maps[j, i, 0] = sandW;
                    maps[j, i, 1] = grassW;
                    maps[j, i, 2] = rockW;
                }
            }
        if (areaMask != null) Blur(maps, A, L, edgeSoftness);
        td.SetAlphamaps(0, 0, maps);
        EditorUtility.SetDirty(td);

        string goName = "Chunk_" + cx + "_" + cz;
        Transform existing = parent.Find(goName);
        GameObject go;
        if (existing != null) go = existing.gameObject;
        else
        {
            go = Terrain.CreateTerrainGameObject(td);
            go.name = goName;
            go.transform.SetParent(parent, false);
        }
        go.transform.position = new Vector3(ox, baseY, oz);

        var terrain = go.GetComponent<Terrain>();
        terrain.terrainData = td;
        var col = go.GetComponent<TerrainCollider>();
        if (col != null) col.terrainData = td;

        var rp = GraphicsSettings.currentRenderPipeline;
        if (rp != null && rp.defaultTerrainMaterial != null) terrain.materialTemplate = rp.defaultTerrainMaterial;
        return terrain;
    }

    // ---------------------------------------------------------------- the world height function

    float WorldHeight(float x, float z)
    {
        float h = grid.Sample(x, z);
        h -= seaDip * (1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0f, 2f, h)));  // flat sea data goes below 0
        h += DetailNoise(x, z);
        return Mathf.Max(h, baseY + 0.5f);
    }

    float DetailNoise(float x, float z)
    {
        float n = Mathf.PerlinNoise(x * 0.05f + noiseOffX, z * 0.05f + noiseOffZ) - 0.5f;
        n += (Mathf.PerlinNoise(x * 0.22f + noiseOffX, z * 0.22f + noiseOffZ) - 0.5f) * 0.4f;
        return n * detailAmplitude * 2f;
    }

    // ---------------------------------------------------------------- extras

    void BuildSea(Transform parent)
    {
        Transform old = parent.Find("Sea");
        if (old != null) DestroyImmediate(old.gameObject);

        float cs = config.chunkSize;
        float minX = minChunkX * cs, maxX = (maxChunkX + 1) * cs;
        float minZ = minChunkZ * cs, maxZ = (maxChunkZ + 1) * cs;

        var sea = GameObject.CreatePrimitive(PrimitiveType.Plane);
        sea.name = "Sea";
        DestroyImmediate(sea.GetComponent<Collider>());
        sea.transform.SetParent(parent, false);
        sea.transform.position = new Vector3((minX + maxX) * 0.5f, 0f, (minZ + maxZ) * 0.5f);
        sea.transform.localScale = new Vector3((maxX - minX + 4000f) / 10f, 1f, (maxZ - minZ + 4000f) / 10f);

        var mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        mat.color = new Color(0.06f, 0.36f, 0.55f);
        mat.SetFloat("_Smoothness", 0.95f);
        sea.GetComponent<Renderer>().sharedMaterial = mat;
    }

    void DropCar()
    {
        if (!Prepare()) return;
        var car = FindFirstObjectByType<CarController>();
        if (car == null) { Debug.LogError("No CarController found in the scene."); return; }
        float x = config.anchorGameX, z = config.anchorGameZ;
        car.transform.SetPositionAndRotation(new Vector3(x, WorldHeight(x, z) + 3f, z), Quaternion.identity);
        Debug.Log("Car placed at the anchor: " + x + ", " + z);
    }

    // ---------------------------------------------------------------- elevation file reader

    class ElevationGrid
    {
        const int Magic = 0x314E5452; // "RTN1"
        float minX, minZ, spacing;
        int cols, rows;
        float[] h;

        public static ElevationGrid Load(string path)
        {
            using (var r = new BinaryReader(File.OpenRead(path)))
            {
                if (r.ReadInt32() != Magic) throw new Exception("Not an RTN1 elevation file: " + path);
                var g = new ElevationGrid();
                g.minX = r.ReadSingle();
                g.minZ = r.ReadSingle();
                g.spacing = r.ReadSingle();
                g.cols = r.ReadInt32();
                g.rows = r.ReadInt32();
                g.h = new float[g.cols * g.rows];
                for (int i = 0; i < g.h.Length; i++) g.h[i] = r.ReadSingle();
                g.Despike();
                g.ShapeSeabed(0.3f, 0.10f, 28f);   // sea threshold (m), slope, max depth (m)
                return g;
            }
        }

        float At(int c, int r)
        {
            c = Mathf.Clamp(c, 0, cols - 1);
            r = Mathf.Clamp(r, 0, rows - 1);
            return h[r * cols + c];
        }

        // Replaces isolated needles: any sample far from the median of its neighbours gets the median.
        void Despike()
        {
            const float maxJump = 8f;
            const int passes = 2;
            var tmp = new float[h.Length];
            var win = new float[9];
            for (int pass = 0; pass < passes; pass++)
            {
                for (int r = 0; r < rows; r++)
                    for (int c = 0; c < cols; c++)
                    {
                        int k = 0;
                        for (int dr = -1; dr <= 1; dr++)
                            for (int dc = -1; dc <= 1; dc++)
                                win[k++] = At(c + dc, r + dr);
                        Array.Sort(win);
                        float median = win[4];
                        float v = h[r * cols + c];
                        tmp[r * cols + c] = Mathf.Abs(v - median) > maxJump ? median : v;
                    }
                Array.Copy(tmp, h, h.Length);
            }
        }

        // Makes the sea floor deepen with distance from the shore, so the water shows
        // turquoise near the beach and deep blue offshore.
        void ShapeSeabed(float seaThreshold, float slope, float maxDepth)
        {
            const float Far = 1e9f;
            int count = h.Length;
            var dist = new float[count];
            for (int i = 0; i < count; i++) dist[i] = h[i] > seaThreshold ? 0f : Far;

            float straight = spacing, diagonal = spacing * 1.4142f;

            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    int i = r * cols + c;
                    float d = dist[i];
                    if (d == 0f) continue;
                    if (c > 0) d = Mathf.Min(d, dist[i - 1] + straight);
                    if (r > 0)
                    {
                        d = Mathf.Min(d, dist[i - cols] + straight);
                        if (c > 0) d = Mathf.Min(d, dist[i - cols - 1] + diagonal);
                        if (c < cols - 1) d = Mathf.Min(d, dist[i - cols + 1] + diagonal);
                    }
                    dist[i] = d;
                }

            for (int r = rows - 1; r >= 0; r--)
                for (int c = cols - 1; c >= 0; c--)
                {
                    int i = r * cols + c;
                    float d = dist[i];
                    if (d == 0f) continue;
                    if (c < cols - 1) d = Mathf.Min(d, dist[i + 1] + straight);
                    if (r < rows - 1)
                    {
                        d = Mathf.Min(d, dist[i + cols] + straight);
                        if (c < cols - 1) d = Mathf.Min(d, dist[i + cols + 1] + diagonal);
                        if (c > 0) d = Mathf.Min(d, dist[i + cols - 1] + diagonal);
                    }
                    dist[i] = d;
                }

            for (int i = 0; i < count; i++)
            {
                if (dist[i] == 0f) continue;   // land
                float depth = dist[i] >= Far * 0.5f ? maxDepth : Mathf.Min(maxDepth, 1.5f + dist[i] * slope);
                h[i] = Mathf.Max(Mathf.Min(h[i], -depth), -maxDepth);
            }
        }

        static float CatmullRom(float p0, float p1, float p2, float p3, float t)
        {
            float t2 = t * t, t3 = t2 * t;
            return 0.5f * ((2f * p1) + (-p0 + p2) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
        }

        float Row(int ix, int iz, float tx)
        {
            return CatmullRom(At(ix - 1, iz), At(ix, iz), At(ix + 1, iz), At(ix + 2, iz), tx);
        }

        // Smooth (bicubic) height at any world position, in meters
        public float Sample(float x, float z)
        {
            float fx = (x - minX) / spacing, fz = (z - minZ) / spacing;
            int ix = Mathf.FloorToInt(fx), iz = Mathf.FloorToInt(fz);
            float tx = fx - ix, tz = fz - iz;
            float value = CatmullRom(Row(ix, iz - 1, tx), Row(ix, iz, tx), Row(ix, iz + 1, tx), Row(ix, iz + 2, tx), tz);

            float a = At(ix, iz), b = At(ix + 1, iz), c = At(ix, iz + 1), d = At(ix + 1, iz + 1);
            float lo = Mathf.Min(Mathf.Min(a, b), Mathf.Min(c, d));
            float hi = Mathf.Max(Mathf.Max(a, b), Mathf.Max(c, d));
            return Mathf.Clamp(value, lo, hi);
        }
    }
}
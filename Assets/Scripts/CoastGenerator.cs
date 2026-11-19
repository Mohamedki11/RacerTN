using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(Terrain))]
public class CoastGenerator : MonoBehaviour
{
    [Header("Terrain")]
    [SerializeField] Vector3 terrainSize = new Vector3(1500, 120, 1500);
    [SerializeField] int seed = 7;
    [SerializeField] float seaLevel = 10f;
    [SerializeField] float seaFloor = 2f;
    [SerializeField] float landBase = 14f;
    [SerializeField] float hillStrength = 22f;
    [SerializeField] float mainHillHeight = 45f;

    [Header("Road")]
    [SerializeField] float roadWidth = 12f;
    [SerializeField] float shoulderWidth = 14f;

    [Header("Terrain layers")]
    [SerializeField] TerrainLayer sand;
    [SerializeField] TerrainLayer grass;
    [SerializeField] TerrainLayer rock;
    [SerializeField] TerrainLayer asphalt;

    [Header("Village")]
    [SerializeField] int houseCount = 45;
    [SerializeField] int treeCount = 80;

    [SerializeField, HideInInspector] GameObject generatedRoot;

    const int Res = 1025;

    [ContextMenu("Generate Everything")]
    public void Generate()
    {
        if (sand == null || grass == null || rock == null || asphalt == null)
        {
            Debug.LogError("CoastGenerator: assign all 4 terrain layers first.");
            return;
        }

        Clear();
        var terrain = GetComponent<Terrain>();
        var td = terrain.terrainData;
        var rng = new System.Random(seed);
        float ox = rng.Next(0, 1000), oz = rng.Next(0, 1000);
        Vector3 tpos = transform.position;

        td.heightmapResolution = Res;
        td.size = terrainSize;
        td.alphamapResolution = Res - 1;

        // ---------- 1. Base heightmap: sea on the west, land and hills on the east ----------
        var heights = new float[Res, Res];
        for (int z = 0; z < Res; z++)
            for (int x = 0; x < Res; x++)
            {
                float h = ComputeHeight(x / (float)(Res - 1), z / (float)(Res - 1), ox, oz);
                heights[z, x] = Mathf.Clamp01(h / terrainSize.y);
            }

        // ---------- 2. Road loop (Catmull-Rom through random control points) ----------
        var pathXZ = BuildLoop(rng);
        int n = pathXZ.Count;
        float cellX = terrainSize.x / (Res - 1), cellZ = terrainSize.z / (Res - 1);

        var elev = new float[n];
        for (int i = 0; i < n; i++)
        {
            int cx = Mathf.Clamp(Mathf.RoundToInt(pathXZ[i].x / cellX), 0, Res - 1);
            int cz = Mathf.Clamp(Mathf.RoundToInt(pathXZ[i].y / cellZ), 0, Res - 1);
            elev[i] = heights[cz, cx] * terrainSize.y;
        }
        for (int pass = 0; pass < 4; pass++)   // smooth the road profile so slopes stay drivable
        {
            var copy = (float[])elev.Clone();
            for (int i = 0; i < n; i++)
            {
                float sum = 0f;
                for (int k = -12; k <= 12; k++) sum += copy[(i + k + n) % n];
                elev[i] = sum / 25f;
            }
        }
        for (int i = 0; i < n; i++) elev[i] = Mathf.Max(elev[i], seaLevel + 1.5f);

        // ---------- 3. Carve the road into the terrain ----------
        var roadMask = new float[Res, Res];
        var targetMap = new float[Res, Res];
        float half = roadWidth * 0.5f, outer = half + shoulderWidth;
        int rx = Mathf.CeilToInt(outer / cellX), rz = Mathf.CeilToInt(outer / cellZ);
        for (int i = 0; i < n; i++)
        {
            int cx = Mathf.RoundToInt(pathXZ[i].x / cellX), cz = Mathf.RoundToInt(pathXZ[i].y / cellZ);
            float target = elev[i] / terrainSize.y;
            for (int dz = -rz; dz <= rz; dz++)
                for (int dx = -rx; dx <= rx; dx++)
                {
                    int x = cx + dx, z = cz + dz;
                    if (x < 0 || z < 0 || x >= Res || z >= Res) continue;
                    float d = new Vector2(dx * cellX, dz * cellZ).magnitude;
                    if (d > outer) continue;
                    float w = d <= half ? 1f : 1f - Mathf.SmoothStep(0f, 1f, (d - half) / shoulderWidth);
                    if (w > roadMask[z, x]) { roadMask[z, x] = w; targetMap[z, x] = target; }
                }
        }
        for (int z = 0; z < Res; z++)
            for (int x = 0; x < Res; x++)
                if (roadMask[z, x] > 0f)
                    heights[z, x] = Mathf.Lerp(heights[z, x], targetMap[z, x], roadMask[z, x]);

        td.SetHeights(0, 0, heights);

        // ---------- 4. Paint the terrain by height, slope and road ----------
        td.terrainLayers = new[] { sand, grass, rock, asphalt };
        int A = td.alphamapResolution;
        var maps = new float[A, A, 4];
        for (int z = 0; z < A; z++)
            for (int x = 0; x < A; x++)
            {
                float h = heights[z, x] * terrainSize.y;
                int xl = Mathf.Max(x - 1, 0), xr = Mathf.Min(x + 1, Res - 1);
                int zl = Mathf.Max(z - 1, 0), zr = Mathf.Min(z + 1, Res - 1);
                float dhx = (heights[z, xr] - heights[z, xl]) * terrainSize.y / ((xr - xl) * cellX);
                float dhz = (heights[zr, x] - heights[zl, x]) * terrainSize.y / ((zr - zl) * cellZ);
                float slope = Mathf.Atan(Mathf.Sqrt(dhx * dhx + dhz * dhz)) * Mathf.Rad2Deg;

                float sandW = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(seaLevel + 1f, seaLevel + 4f, h));
                float rockW = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(24f, 38f, slope));
                float grassW = Mathf.Max(0f, 1f - sandW - rockW);
                float roadW = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.6f, 1f, roadMask[z, x]));
                float keep = 1f - roadW;
                sandW *= keep; rockW *= keep; grassW *= keep;
                float total = sandW + rockW + grassW + roadW;
                if (total < 0.0001f) { grassW = 1f; total = 1f; }
                maps[z, x, 0] = sandW / total;
                maps[z, x, 1] = grassW / total;
                maps[z, x, 2] = rockW / total;
                maps[z, x, 3] = roadW / total;
            }
        td.SetAlphamaps(0, 0, maps);

        var col = GetComponent<TerrainCollider>();
        if (col != null) col.terrainData = td;

        // ---------- 5. Generated objects: sea, houses, trees, road points, start ----------
        generatedRoot = new GameObject("GeneratedWorld");

        var sea = GameObject.CreatePrimitive(PrimitiveType.Plane);
        sea.name = "Sea";
        DestroyImmediate(sea.GetComponent<Collider>());
        sea.transform.SetParent(generatedRoot.transform, false);
        sea.transform.position = new Vector3(tpos.x + terrainSize.x * 0.5f, tpos.y + seaLevel, tpos.z + terrainSize.z * 0.5f);
        sea.transform.localScale = new Vector3(terrainSize.x * 0.3f, 1f, terrainSize.z * 0.3f);
        sea.GetComponent<Renderer>().sharedMaterial = MakeMat(new Color(0.06f, 0.36f, 0.55f), 0.95f);

        var wallMat = MakeMat(new Color(0.96f, 0.95f, 0.92f), 0.1f);
        var blueMat = MakeMat(new Color(0.12f, 0.37f, 0.66f), 0.3f);
        var trunkMat = MakeMat(new Color(0.35f, 0.24f, 0.14f), 0.1f);
        var leafMat = MakeMat(new Color(0.28f, 0.45f, 0.2f), 0.1f);
        var oliveMat = MakeMat(new Color(0.42f, 0.5f, 0.32f), 0.1f);

        var used = new List<Vector3>();

        // Houses along the road, doors facing the road
        var houses = new GameObject("Houses").transform;
        houses.SetParent(generatedRoot.transform, false);
        int placed = 0, attempts = 0;
        while (placed < houseCount && attempts < houseCount * 40)
        {
            attempts++;
            int i = rng.Next(0, n);
            Vector2 tangent = (pathXZ[(i + 1) % n] - pathXZ[(i - 1 + n) % n]).normalized;
            Vector2 normal = new Vector2(-tangent.y, tangent.x);
            float side = rng.Next(0, 2) == 0 ? -1f : 1f;
            float dist = half + 6f + (float)rng.NextDouble() * 20f;
            Vector2 p2 = pathXZ[i] + normal * side * dist;
            float u = p2.x / terrainSize.x, v = p2.y / terrainSize.z;
            if (u < 0.02f || u > 0.98f || v < 0.02f || v > 0.98f) continue;
            float y = td.GetInterpolatedHeight(u, v);
            if (y < seaLevel + 1.5f || td.GetSteepness(u, v) > 22f) continue;
            Vector3 world = tpos + new Vector3(p2.x, y - 0.6f, p2.y);
            if (TooClose(used, world, 11f)) continue;

            Vector3 toRoad = new Vector3(-normal.x * side, 0f, -normal.y * side);
            float w = Mathf.Lerp(6f, 9f, (float)rng.NextDouble());
            float d = Mathf.Lerp(6f, 8f, (float)rng.NextDouble());
            float ht = rng.Next(0, 2) == 0 ? 4.5f : 7.5f;
            BuildHouse(houses, world, Quaternion.LookRotation(-toRoad), w, d, ht, wallMat, blueMat);
            used.Add(world);
            placed++;
        }

        // Trees (placeholders: palms and olive trees)
        var trees = new GameObject("Trees").transform;
        trees.SetParent(generatedRoot.transform, false);
        int treesPlaced = 0; attempts = 0;
        while (treesPlaced < treeCount && attempts < treeCount * 30)
        {
            attempts++;
            float u = 0.3f + (float)rng.NextDouble() * 0.68f, v = 0.02f + (float)rng.NextDouble() * 0.96f;
            float y = td.GetInterpolatedHeight(u, v);
            if (y < seaLevel + 1.2f || td.GetSteepness(u, v) > 28f) continue;
            int mx = Mathf.Clamp(Mathf.RoundToInt(u * (Res - 1)), 0, Res - 1);
            int mz = Mathf.Clamp(Mathf.RoundToInt(v * (Res - 1)), 0, Res - 1);
            if (roadMask[mz, mx] > 0f) continue;
            Vector3 world = tpos + new Vector3(u * terrainSize.x, y, v * terrainSize.z);
            if (TooClose(used, world, 8f)) continue;
            BuildTree(trees, world, rng.Next(0, 2) == 0, (float)rng.NextDouble(), trunkMat, leafMat, oliveMat);
            used.Add(world);
            treesPlaced++;
        }

        // Road points every ~25 m (used later for checkpoints and AI) + start point
        var road = new GameObject("RoadPoints").transform;
        road.SetParent(generatedRoot.transform, false);
        for (int i = 0; i < n; i += 10)
        {
            var pt = new GameObject("P_" + (i / 10)).transform;
            pt.SetParent(road, false);
            pt.position = tpos + new Vector3(pathXZ[i].x, elev[i] + 0.1f, pathXZ[i].y);
            Vector2 tg = (pathXZ[(i + 1) % n] - pathXZ[i]).normalized;
            pt.rotation = Quaternion.LookRotation(new Vector3(tg.x, 0f, tg.y));
        }
        var start = new GameObject("StartPoint").transform;
        start.SetParent(generatedRoot.transform, false);
        Vector2 t0 = (pathXZ[1] - pathXZ[0]).normalized;
        start.SetPositionAndRotation(tpos + new Vector3(pathXZ[0].x, elev[0] + 1.5f, pathXZ[0].y),
                                     Quaternion.LookRotation(new Vector3(t0.x, 0f, t0.y)));

        var car = GameObject.Find("Car");
        if (car != null) car.transform.SetPositionAndRotation(start.position, start.rotation);

#if UNITY_EDITOR
        UnityEditor.EditorUtility.SetDirty(td);
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(gameObject.scene);
#endif
        Debug.Log($"CoastGenerator: done. {placed} houses, {treesPlaced} trees, road length ~{n * 2.5f:0} m.");
    }

    [ContextMenu("Clear Generated Objects")]
    public void Clear()
    {
        if (generatedRoot != null) DestroyImmediate(generatedRoot);
        generatedRoot = null;
    }























    #if UNITY_EDITOR
    [ContextMenu("Create Test Layers (no download)")]
    void CreateTestLayers()
    {
        sand    = MakeFlatLayer("Test_Sand",    new Color(0.86f, 0.78f, 0.58f));
        grass   = MakeFlatLayer("Test_Grass",   new Color(0.36f, 0.50f, 0.22f));
        rock    = MakeFlatLayer("Test_Rock",    new Color(0.45f, 0.42f, 0.40f));
        asphalt = MakeFlatLayer("Test_Asphalt", new Color(0.16f, 0.16f, 0.17f));
        UnityEditor.EditorUtility.SetDirty(this);
    }

    static TerrainLayer MakeFlatLayer(string name, Color c)
    {
        const string dir = "Assets/TestLayers";
        if (!UnityEditor.AssetDatabase.IsValidFolder(dir))
            UnityEditor.AssetDatabase.CreateFolder("Assets", "TestLayers");

        // small noisy texture so the surface isn't one flat color
        var tex = new Texture2D(64, 64, TextureFormat.RGBA32, false);
        var rnd = new System.Random(name.GetHashCode());
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
            {
                float k = 0.9f + (float)rnd.NextDouble() * 0.2f;
                tex.SetPixel(x, y, new Color(c.r * k, c.g * k, c.b * k, 1f));
            }
        tex.Apply();

        string texPath = $"{dir}/{name}.png";
        System.IO.File.WriteAllBytes(texPath, tex.EncodeToPNG());
        DestroyImmediate(tex);
        UnityEditor.AssetDatabase.ImportAsset(texPath);

        var layer = new TerrainLayer
        {
            diffuseTexture = UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>(texPath),
            tileSize = new Vector2(6, 6)
        };
        UnityEditor.AssetDatabase.CreateAsset(layer, $"{dir}/{name}.terrainlayer");
        return layer;
    }
#endif









    // ---------------- helpers ----------------

    float ComputeHeight(float u, float v, float ox, float oz)
    {
        float shore = 0.30f + 0.06f * (Fbm(v * 3f + ox, 0.5f + oz, 3) - 0.5f) * 2f;
        float land = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(shore - 0.05f, shore + 0.10f, u));
        float baseH = Mathf.Lerp(seaFloor, landBase, land);

        float hills = (Fbm(u * 4f + ox, v * 4f + oz, 5) - 0.5f) * 2f;
        hills = Mathf.Max(0f, hills + 0.2f) * hillStrength * land;

        float du = (u - 0.58f) / 0.16f, dv = (v - 0.62f) / 0.18f;     // the big "Sidi Bou Said" hill
        float bump = Mathf.Exp(-(du * du + dv * dv)) * mainHillHeight * land;

        return baseH + hills + bump;
    }

    static float Fbm(float x, float y, int octaves)
    {
        float sum = 0f, amp = 0.5f, freq = 1f, norm = 0f;
        for (int i = 0; i < octaves; i++)
        {
            sum += Mathf.PerlinNoise(x * freq, y * freq) * amp;
            norm += amp; amp *= 0.5f; freq *= 2f;
        }
        return sum / norm;
    }

    List<Vector2> BuildLoop(System.Random rng)
    {
        const int controlCount = 12, stepsPerSegment = 80;
        var ctrl = new List<Vector2>();
        for (int i = 0; i < controlCount; i++)
        {
            float a = i / (float)controlCount * Mathf.PI * 2f;
            float r = 0.85f + (float)rng.NextDouble() * 0.30f;
            ctrl.Add(new Vector2(0.68f + Mathf.Cos(a) * 0.19f * r, 0.50f + Mathf.Sin(a) * 0.30f * r));
        }
        var pts = new List<Vector2>();
        for (int i = 0; i < controlCount; i++)
        {
            Vector2 p0 = ctrl[(i - 1 + controlCount) % controlCount], p1 = ctrl[i];
            Vector2 p2 = ctrl[(i + 1) % controlCount], p3 = ctrl[(i + 2) % controlCount];
            for (int s = 0; s < stepsPerSegment; s++)
            {
                Vector2 q = CatmullRom(p0, p1, p2, p3, s / (float)stepsPerSegment);
                pts.Add(new Vector2(q.x * terrainSize.x, q.y * terrainSize.z));
            }
        }
        return pts;
    }

    static Vector2 CatmullRom(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
    {
        float t2 = t * t, t3 = t2 * t;
        return 0.5f * ((2f * p1) + (-p0 + p2) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
    }

    static bool TooClose(List<Vector3> list, Vector3 p, float minDist)
    {
        foreach (var q in list)
        {
            float dx = q.x - p.x, dz = q.z - p.z;
            if (dx * dx + dz * dz < minDist * minDist) return true;
        }
        return false;
    }

    static Material MakeMat(Color c, float smoothness)
    {
        var m = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        m.color = c;
        m.SetFloat("_Smoothness", smoothness);
        return m;
    }

    static GameObject Part(Transform parent, PrimitiveType type, string name, Vector3 pos, Vector3 scale, Material mat, bool collider)
    {
        var go = GameObject.CreatePrimitive(type);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = pos;
        go.transform.localScale = scale;
        go.GetComponent<Renderer>().sharedMaterial = mat;
        if (!collider) DestroyImmediate(go.GetComponent<Collider>());
        return go;
    }

    static void BuildHouse(Transform parent, Vector3 pos, Quaternion rot, float w, float d, float h, Material wall, Material blue)
    {
        var root = new GameObject("House").transform;
        root.SetParent(parent, false);
        root.SetPositionAndRotation(pos, rot);

        Part(root, PrimitiveType.Cube, "Walls", new Vector3(0, h / 2f, 0), new Vector3(w, h, d), wall, true);
        Part(root, PrimitiveType.Cube, "Parapet", new Vector3(0, h + 0.1f, 0), new Vector3(w + 0.4f, 0.2f, d + 0.4f), wall, false);
        Part(root, PrimitiveType.Cube, "Door", new Vector3(0, 1.1f, -d / 2f - 0.05f), new Vector3(1.3f, 2.2f, 0.12f), blue, false);
        float[] rows = h > 6f ? new[] { 3.2f, 6.0f } : new[] { 3.0f };
        foreach (float wy in rows)
        {
            Part(root, PrimitiveType.Cube, "Window", new Vector3(-w * 0.3f, wy, -d / 2f - 0.05f), new Vector3(0.9f, 1.2f, 0.12f), blue, false);
            Part(root, PrimitiveType.Cube, "Window", new Vector3(w * 0.3f, wy, -d / 2f - 0.05f), new Vector3(0.9f, 1.2f, 0.12f), blue, false);
        }
    }

    static void BuildTree(Transform parent, Vector3 pos, bool palm, float rnd, Material trunk, Material leaf, Material olive)
    {
        var root = new GameObject(palm ? "Palm" : "Olive").transform;
        root.SetParent(parent, false);
        root.SetPositionAndRotation(pos, Quaternion.Euler(0f, rnd * 360f, 0f));
        float s = 0.8f + rnd * 0.5f;
        root.localScale = Vector3.one * s;
        if (palm)
        {
            Part(root, PrimitiveType.Cylinder, "Trunk", new Vector3(0, 2.5f, 0), new Vector3(0.45f, 2.5f, 0.45f), trunk, true);
            Part(root, PrimitiveType.Sphere, "Crown", new Vector3(0, 5.2f, 0), new Vector3(4f, 0.8f, 4f), leaf, false);
        }
        else
        {
            Part(root, PrimitiveType.Cylinder, "Trunk", new Vector3(0, 1f, 0), new Vector3(0.4f, 1f, 0.4f), trunk, true);
            Part(root, PrimitiveType.Sphere, "Crown", new Vector3(0, 3f, 0), new Vector3(3.2f, 2.4f, 3.2f), olive, false);
        }
    }
}
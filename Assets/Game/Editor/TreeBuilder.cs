using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Editor tool: scatters trees on the Sidi Bou Said map.
// Where trees go comes from the OSM ground areas: forests, scrub, parks, farmland, plus palms along wide roads.
// Trees stay off roads, buildings, steep slopes, the beach and the sea.
// Order: 1) Terrain Builder  2) Road Builder  3) Road Mesh Builder  4) Building Builder  5) Tree Builder
//
// FIX for "trees lying down": Quaternius FBX files are exported from Blender (Z is up), so Unity
// gives their root a -90 degree rotation. Terrain Trees ignore that rotation. This tool now turns on
// "Bake Axis Conversion" in each FBX import settings, which puts the rotation inside the mesh itself.
//
// FIX for "trees disappear": two placement modes.
//   TerrainTrees = cheap, drawn by the terrain (fast).
//   RealObjects  = normal GameObjects under World/Trees. Always visible, culled by a LODGroup. Use this if
//                  Terrain Trees give you any problem.
public class TreeBuilder : EditorWindow
{
    enum Mode { TerrainTrees, RealObjects }

    const float RoadCell = 16f, BuildCell = 32f, AreaCell = 64f;
    const string WrapFolder = "Assets/Game/TreePrefabs";
    const string TreesRootName = "Trees";

    class ChunkInfo
    {
        public Terrain terrain;
        public TerrainData td;
        public float ox, oz, baseY;
    }

    struct RoadPoint
    {
        public Vector2 pos;
        public float halfWidth;
    }

    struct Placed
    {
        public Vector3 world;     // world position (x, ground height, z)
        public Vector2 uv;        // position inside the chunk, 0..1
        public int proto;
        public float width, height, rotation;   // rotation in radians
    }

    class AreaInfo
    {
        public string kind;
        public int priority;
        public Vector2[] pts;
        public float minX, maxX, minZ, maxZ;
    }

    class BuildInfo
    {
        public Vector2[] pts;
        public float minX, maxX, minZ, maxZ;   // already grown by the clearance
    }

    [SerializeField] WorldConfig config;
    [SerializeField] string osmJsonPath = "Assets/Game/RawData/SidiBouSaid/osm_world.json";

    // Drag the tree models (FBX or prefabs) into these lists
    [SerializeField] GameObject[] forestPrefabs;
    [SerializeField] GameObject[] scrubPrefabs;
    [SerializeField] GameObject[] parkPrefabs;
    [SerializeField] GameObject[] farmPrefabs;
    [SerializeField] GameObject[] roadsidePrefabs;

    [SerializeField] Mode mode = Mode.TerrainTrees;

    [SerializeField] float forestSpacing = 7f;     // meters between trees
    [SerializeField] float scrubSpacing = 10f;
    [SerializeField] float parkSpacing = 14f;
    [SerializeField] float farmSpacing = 9f;       // farmland is planted in neat rows
    [SerializeField] float roadsideSpacing = 24f;
    [SerializeField] float roadsideChance = 0.5f;  // chance per spot and per side

    [SerializeField] float scaleMin = 1f, scaleMax = 1.4f;
    [SerializeField] float minHeight = 2f;         // no trees lower than this (beach and sea)
    [SerializeField] float maxSlope = 35f;         // degrees
    [SerializeField] float roadClearance = 1.5f;
    [SerializeField] float buildingClearance = 2.5f;
    [SerializeField] float roadWidthScale = 1.15f; // keep equal to the Road Builder value
    [SerializeField] float treeDistance = 250f;    // Terrain Trees: farther than this are not drawn
    [SerializeField] float cullScreenSize = 0.02f; // Real Objects: hidden when smaller than this fraction of the screen height
    [SerializeField] bool applySpeedSettings = true;
    [SerializeField] int seed = 1;

    [SerializeField] float fullDetailSize = 0.12f;   // full model only while the tree is bigger than this fraction of the screen height
    [SerializeField] bool treeColliders = true;      // the car can hit the trunks
    // Model fix
    [SerializeField] bool fixFbxAxis = true;                 // turns on Bake Axis Conversion in the FBX import settings
    [SerializeField] Vector3 modelRotation = Vector3.zero;   // only if trees STILL lean after the fix: try (-90,0,0) or (90,0,0)

    [SerializeField] bool treeShadows = false;   // shadows from thousands of trees are very expensive
    List<float> protoScale;                      // makes every model the right size in meters

    Dictionary<Vector2Int, ChunkInfo> chunks;
    OsmWorldData data;
    List<AreaInfo> areas;
    List<BuildInfo> buildings;
    Dictionary<long, List<int>> areaGrid, buildGrid;
    Dictionary<long, List<RoadPoint>> roadGrid;
    List<GameObject> protos;                 // the wrapped prefabs that are really used
    Dictionary<GameObject, int> protoMap;    // original model -> index in protos
    Dictionary<Vector2Int, List<Placed>> placed;
    float noiseOff;
    Vector2 scroll;

    [MenuItem("RacerTN/Tree Builder")]
    static void Open() { GetWindow<TreeBuilder>("Tree Builder"); }

    void OnGUI()
    {
        scroll = EditorGUILayout.BeginScrollView(scroll);
        config = (WorldConfig)EditorGUILayout.ObjectField("World Config", config, typeof(WorldConfig), false);
        osmJsonPath = EditorGUILayout.TextField("OSM JSON", osmJsonPath);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Tree models (drag several at once onto a list name)", EditorStyles.boldLabel);
        var so = new SerializedObject(this);
        so.Update();
        EditorGUILayout.PropertyField(so.FindProperty("forestPrefabs"), new GUIContent("Forest (pines, trees)"), true);
        EditorGUILayout.PropertyField(so.FindProperty("scrubPrefabs"), new GUIContent("Scrub (bushes)"), true);
        EditorGUILayout.PropertyField(so.FindProperty("parkPrefabs"), new GUIContent("Parks and gardens"), true);
        EditorGUILayout.PropertyField(so.FindProperty("farmPrefabs"), new GUIContent("Farmland rows"), true);
        EditorGUILayout.PropertyField(so.FindProperty("roadsidePrefabs"), new GUIContent("Roadside (palms)"), true);
        so.ApplyModifiedProperties();

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("How trees are placed", EditorStyles.boldLabel);
        mode = (Mode)EditorGUILayout.EnumPopup("Mode", mode);
        if (mode == Mode.RealObjects)
            EditorGUILayout.HelpBox("Real Objects: normal GameObjects under World/Trees. Always visible. Small far-away trees are hidden by a LODGroup.", MessageType.None);
        else
            EditorGUILayout.HelpBox("Terrain Trees: faster, but fussier with imported models. If they vanish, use Real Objects.", MessageType.None);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Density (meters between trees, bigger = fewer)", EditorStyles.boldLabel);
        forestSpacing = EditorGUILayout.FloatField("Forest", forestSpacing);
        scrubSpacing = EditorGUILayout.FloatField("Scrub", scrubSpacing);
        parkSpacing = EditorGUILayout.FloatField("Parks", parkSpacing);
        farmSpacing = EditorGUILayout.FloatField("Farmland", farmSpacing);
        roadsideSpacing = EditorGUILayout.FloatField("Roadside", roadsideSpacing);
        roadsideChance = EditorGUILayout.Slider("Roadside chance", roadsideChance, 0f, 1f);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Rules", EditorStyles.boldLabel);
        scaleMin = EditorGUILayout.FloatField("Tree scale min", scaleMin);
        scaleMax = EditorGUILayout.FloatField("Tree scale max", scaleMax);
        minHeight = EditorGUILayout.FloatField("No trees below height (m)", minHeight);
        maxSlope = EditorGUILayout.FloatField("Max slope (degrees)", maxSlope);
        roadClearance = EditorGUILayout.FloatField("Road clearance (m)", roadClearance);
        buildingClearance = EditorGUILayout.FloatField("Building clearance (m)", buildingClearance);
        roadWidthScale = EditorGUILayout.FloatField("Road width scale", roadWidthScale);
        seed = EditorGUILayout.IntField("Seed", seed);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Speed", EditorStyles.boldLabel);
        fullDetailSize = EditorGUILayout.Slider("Full detail until (screen size)", fullDetailSize, 0.03f, 0.4f);
        treeColliders = EditorGUILayout.Toggle("Trunk colliders", treeColliders);
        treeDistance = EditorGUILayout.FloatField("Terrain Trees draw distance (m)", treeDistance);
        cullScreenSize = EditorGUILayout.Slider("Real Objects cull size", cullScreenSize, 0.005f, 0.1f);
        applySpeedSettings = EditorGUILayout.Toggle("Also set terrain speed options", applySpeedSettings);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Trees lying down? (model fix)", EditorStyles.boldLabel);
        fixFbxAxis = EditorGUILayout.Toggle("Fix FBX axis (Bake Axis Conversion)", fixFbxAxis);
        modelRotation = EditorGUILayout.Vector3Field("Extra model rotation", modelRotation);

        EditorGUILayout.Space();
        if (GUILayout.Button("Build trees", GUILayout.Height(30))) Run(Build);
        if (GUILayout.Button("Remove all trees", GUILayout.Height(22))) Run(RemoveAll);
        EditorGUILayout.HelpBox("Run this last, after the Road Builder and Building Builder. Running it again replaces the old trees.", MessageType.Info);
        EditorGUILayout.EndScrollView();
    }

    void Run(Action action)
    {
        try { action(); }
        catch (Exception e) { Debug.LogError("TreeBuilder failed: " + e); }
        finally { EditorUtility.ClearProgressBar(); }
    }

    // ---------------------------------------------------------------- loading

    bool Load()
    {
        if (config == null) { Debug.LogError("Assign the WorldConfig first."); return false; }

        string full = Path.GetFullPath(osmJsonPath);
        if (!File.Exists(full)) { Debug.LogError("OSM JSON not found: " + full); return false; }
        data = JsonUtility.FromJson<OsmWorldData>(File.ReadAllText(full));
        if (data == null || data.roads == null || data.buildings == null) { Debug.LogError("The OSM JSON is empty or unreadable."); return false; }
        if (data.areas == null || data.areas.Length == 0) { Debug.LogError("The OSM JSON has no ground areas. Run RacerTN > OSM Importer first."); return false; }

        GameObject root = GameObject.Find("World");
        if (root == null) { Debug.LogError("No 'World' object in the scene. Run the Terrain Builder first."); return false; }

        chunks = new Dictionary<Vector2Int, ChunkInfo>();
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

    // ---------------------------------------------------------------- lookup tables

    static long Key(int gx, int gz) { return ((long)gx << 32) | (uint)gz; }

    static int Priority(string kind)   // same order as the Terrain Builder: higher wins where areas overlap
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

    static void AddToGrid(Dictionary<long, List<int>> grid, float cell, int index, float minX, float maxX, float minZ, float maxZ)
    {
        int x0 = Mathf.FloorToInt(minX / cell), x1 = Mathf.FloorToInt(maxX / cell);
        int z0 = Mathf.FloorToInt(minZ / cell), z1 = Mathf.FloorToInt(maxZ / cell);
        for (int gz = z0; gz <= z1; gz++)
            for (int gx = x0; gx <= x1; gx++)
            {
                List<int> list;
                long k = Key(gx, gz);
                if (!grid.TryGetValue(k, out list)) { list = new List<int>(); grid[k] = list; }
                list.Add(index);
            }
    }

    void BuildTables()
    {
        areas = new List<AreaInfo>();
        areaGrid = new Dictionary<long, List<int>>();
        foreach (var a in data.areas)
        {
            int pr = Priority(a.kind);
            if (pr < 0 || a.points == null || a.points.Length < 3) continue;
            var info = new AreaInfo { kind = a.kind, priority = pr, pts = a.points };
            Bounds(a.points, 0f, out info.minX, out info.maxX, out info.minZ, out info.maxZ);
            areas.Add(info);
            AddToGrid(areaGrid, AreaCell, areas.Count - 1, info.minX, info.maxX, info.minZ, info.maxZ);
        }

        buildings = new List<BuildInfo>();
        buildGrid = new Dictionary<long, List<int>>();
        foreach (var b in data.buildings)
        {
            if (b.footprint == null || b.footprint.Length < 3) continue;
            var info = new BuildInfo { pts = b.footprint };
            Bounds(b.footprint, buildingClearance, out info.minX, out info.maxX, out info.minZ, out info.maxZ);
            buildings.Add(info);
            AddToGrid(buildGrid, BuildCell, buildings.Count - 1, info.minX, info.maxX, info.minZ, info.maxZ);
        }

        roadGrid = new Dictionary<long, List<RoadPoint>>();
        var pts = new List<Vector2>();
        foreach (var road in data.roads)
        {
            float width, shoulder; int layer;
            if (!RoadBuilder.RoadSpec(road, out width, out shoulder, out layer) || road.bridge || road.tunnel) continue;
            float half = width * roadWidthScale * 0.5f;
            pts.Clear();
            Resample(road.points, 2f, pts);
            foreach (var p in pts)
            {
                long k = Key(Mathf.FloorToInt(p.x / RoadCell), Mathf.FloorToInt(p.y / RoadCell));
                List<RoadPoint> list;
                if (!roadGrid.TryGetValue(k, out list)) { list = new List<RoadPoint>(); roadGrid[k] = list; }
                list.Add(new RoadPoint { pos = p, halfWidth = half });
            }
        }
    }

    static void Bounds(Vector2[] pts, float grow, out float minX, out float maxX, out float minZ, out float maxZ)
    {
        minX = minZ = float.MaxValue;
        maxX = maxZ = float.MinValue;
        foreach (var p in pts)
        {
            if (p.x < minX) minX = p.x;
            if (p.x > maxX) maxX = p.x;
            if (p.y < minZ) minZ = p.y;
            if (p.y > maxZ) maxZ = p.y;
        }
        minX -= grow; maxX += grow; minZ -= grow; maxZ += grow;
    }

    // ---------------------------------------------------------------- tests

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

    static float DistToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float len2 = ab.sqrMagnitude;
        float t = len2 < 1e-8f ? 0f : Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2);
        return Vector2.Distance(p, a + ab * t);
    }

    // Kind of the highest-priority area that covers the point (null = no area)
    string TopmostKind(float x, float z)
    {
        List<int> list;
        if (!areaGrid.TryGetValue(Key(Mathf.FloorToInt(x / AreaCell), Mathf.FloorToInt(z / AreaCell)), out list)) return null;
        int best = -1;
        string kind = null;
        foreach (int i in list)
        {
            AreaInfo a = areas[i];
            if (a.priority <= best) continue;
            if (x < a.minX || x > a.maxX || z < a.minZ || z > a.maxZ) continue;
            if (Inside(x, z, a.pts)) { best = a.priority; kind = a.kind; }
        }
        return kind;
    }

    bool NearRoad(float x, float z)
    {
        Vector2 p = new Vector2(x, z);
        int gx = Mathf.FloorToInt(x / RoadCell), gz = Mathf.FloorToInt(z / RoadCell);
        for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
            {
                List<RoadPoint> list;
                if (!roadGrid.TryGetValue(Key(gx + dx, gz + dz), out list)) continue;
                foreach (var rp in list)
                    if (Vector2.Distance(rp.pos, p) < rp.halfWidth + roadClearance) return true;
            }
        return false;
    }

    bool NearBuilding(float x, float z)
    {
        List<int> list;
        if (!buildGrid.TryGetValue(Key(Mathf.FloorToInt(x / BuildCell), Mathf.FloorToInt(z / BuildCell)), out list)) return false;
        Vector2 p = new Vector2(x, z);
        foreach (int i in list)
        {
            BuildInfo b = buildings[i];
            if (x < b.minX || x > b.maxX || z < b.minZ || z > b.maxZ) continue;
            if (Inside(x, z, b.pts)) return true;
            for (int k = 0; k < b.pts.Length; k++)
                if (DistToSegment(p, b.pts[k], b.pts[(k + 1) % b.pts.Length]) < buildingClearance) return true;
        }
        return false;
    }

    // ---------------------------------------------------------------- placing

    bool TryAddTree(float x, float z, int proto, System.Random rng)
    {
        float cs = config.chunkSize;
        Vector2Int key = new Vector2Int(Mathf.FloorToInt(x / cs), Mathf.FloorToInt(z / cs));
        ChunkInfo c;
        if (!chunks.TryGetValue(key, out c)) return false;

        float u = Mathf.Clamp01((x - c.ox) / cs), v = Mathf.Clamp01((z - c.oz) / cs);
        float groundY = c.td.GetInterpolatedHeight(u, v) + c.baseY;
        if (groundY < minHeight) return false;
        if (c.td.GetSteepness(u, v) > maxSlope) return false;
        if (NearRoad(x, z)) return false;
        if (NearBuilding(x, z)) return false;

        float s = scaleMin + (float)rng.NextDouble() * Mathf.Max(0f, scaleMax - scaleMin);
        var p = new Placed
        {
            world = new Vector3(x, groundY, z),
            uv = new Vector2(u, v),
            proto = proto,
            width = s,
            height = s * (0.9f + 0.25f * (float)rng.NextDouble()),
            rotation = (float)rng.NextDouble() * Mathf.PI * 2f
        };
        List<Placed> list;
        if (!placed.TryGetValue(key, out list)) { list = new List<Placed>(); placed[key] = list; }
        list.Add(p);
        return true;
    }

    // How much to scale a model so it ends up targetHeight meters tall
    static float MeasureScale(GameObject src, float targetHeight)
    {
        var tmp = (GameObject)PrefabUtility.InstantiatePrefab(src);
        tmp.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        Renderer[] rs = tmp.GetComponentsInChildren<Renderer>();
        float h = 0f;
        if (rs.Length > 0)
        {
            Bounds b = rs[0].bounds;
            for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);
            h = b.size.y;
        }
        DestroyImmediate(tmp);
        if (h < 0.0001f) { Debug.LogWarning("Could not measure model: " + src.name); return 1f; }
        return targetHeight / h;
    }

        int[] ProtoIndices(GameObject[] prefabs, float targetHeight, bool collide)
    {
        var result = new List<int>();
        if (prefabs != null)
            foreach (var go in prefabs)
            {
                if (go == null) continue;
                int idx;
                if (!protoMap.TryGetValue(go, out idx))
                {
                    if (fixFbxAxis) FixImport(go);
                    GameObject wrapped = Wrap(go, collide && treeColliders, targetHeight); // upright wrapper (uses the Extra model rotation)
                    protos.Add(wrapped);
                    protoScale.Add(MeasureScale(wrapped, targetHeight));
                    idx = protos.Count - 1;
                    protoMap[go] = idx;
                }
                result.Add(idx);
            }
        return result.ToArray();
    }

    // Turns on "Bake Axis Conversion" for an FBX, so the model stands up without a rotated root object.
    static void FixImport(GameObject go)
    {
        string path = AssetDatabase.GetAssetPath(go);
        if (string.IsNullOrEmpty(path)) return;
        var imp = AssetImporter.GetAtPath(path) as ModelImporter;
        if (imp == null || imp.bakeAxisConversion) return;
        imp.bakeAxisConversion = true;
        imp.SaveAndReimport();
    }

    // Puts the model inside an empty prefab with a LODGroup (used for culling in both modes).
    // Existing wrapper prefabs are rebuilt every time, so old broken ones are replaced.
        // Puts the model in an empty prefab with two detail levels (the real model up close, a cheap
    // blob far away), a trunk collider, and cheaper render settings.
    GameObject Wrap(GameObject src, bool collide, float targetHeight)
    {
        if (!AssetDatabase.IsValidFolder(WrapFolder)) AssetDatabase.CreateFolder("Assets/Game", "TreePrefabs");

        var root = new GameObject("Tree_" + src.name);
        var child = (GameObject)PrefabUtility.InstantiatePrefab(src);
        child.transform.SetParent(root.transform, false);
        child.transform.localPosition = Vector3.zero;
        child.transform.localRotation = Quaternion.Euler(modelRotation);
        child.transform.localScale = Vector3.one;

        Renderer[] renderers = child.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0)
        {
            Debug.LogWarning("Model has no renderers, it will be invisible: " + src.name);
        }
        else
        {
            Bounds b = renderers[0].bounds;
            Renderer biggest = renderers[0];
            float bigVolume = -1f;
            foreach (var r in renderers)
            {
                b.Encapsulate(r.bounds);
                float vol = r.bounds.size.x * r.bounds.size.y * r.bounds.size.z;
                if (vol > bigVolume) { bigVolume = vol; biggest = r; }

                // cheaper rendering for every tree
                r.receiveShadows = false;
                r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
                r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
                if (!treeShadows) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                foreach (var m in r.sharedMaterials)
                    if (m != null && !m.enableInstancing) { m.enableInstancing = true; EditorUtility.SetDirty(m); }
            }

            // Far version: a trunk and a low-poly blob in the tree's own leaf colour
            Color leaf = new Color(0.25f, 0.45f, 0.2f);
            Material bm = biggest.sharedMaterial;
            if (bm != null)
            {
                if (bm.HasProperty("_BaseColor")) leaf = bm.GetColor("_BaseColor");
                else if (bm.HasProperty("_Color")) leaf = bm.GetColor("_Color");
            }

            var far = new GameObject("Far");
            far.transform.SetParent(root.transform, false);
            float trunkR = b.size.y * 0.03f;
            Renderer trunk = MakeProxy(far.transform, "Trunk", GetProxyMesh("ProxyTrunk", false),
                ProxyMaterial("Proxy_Trunk", new Color(0.3f, 0.2f, 0.12f)),
                new Vector3(0f, b.min.y, 0f), new Vector3(trunkR, b.size.y * 0.55f, trunkR));
            Renderer canopy = MakeProxy(far.transform, "Canopy", GetProxyMesh("ProxyCanopy", true),
                ProxyMaterial("Proxy_" + src.name, leaf),
                new Vector3(b.center.x, b.min.y + b.size.y * 0.68f, b.center.z),
                new Vector3(Mathf.Max(0.3f, b.size.x * 0.5f), b.size.y * 0.3f, Mathf.Max(0.3f, b.size.z * 0.5f)));

            var lodGroup = root.AddComponent<LODGroup>();
            lodGroup.fadeMode = LODFadeMode.None;
            float full = Mathf.Max(fullDetailSize, cullScreenSize + 0.01f);
            lodGroup.SetLODs(new[]
            {
                new LOD(full, renderers),
                new LOD(cullScreenSize, new[] { trunk, canopy })
            });
            lodGroup.RecalculateBounds();

           if (collide)   // thin trunk, sized in real meters
            {
                float k = targetHeight / Mathf.Max(0.01f, b.size.y);   // model units -> meters
                var cap = root.AddComponent<CapsuleCollider>();
                cap.direction = 1;
                cap.radius = 0.25f / k;
                cap.height = 3f / k;
                cap.center = new Vector3(0f, b.min.y + cap.height * 0.5f, 0f);
            }
        }

        GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, WrapFolder + "/Tree_" + src.name + ".prefab");
        DestroyImmediate(root);
        return prefab;
    }

    static Renderer MakeProxy(Transform parent, string name, Mesh mesh, Material mat, Vector3 pos, Vector3 scale)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = pos;
        go.transform.localScale = scale;
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = mat;
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows = false;
        mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
        mr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        return mr;
    }

    static Material ProxyMaterial(string name, Color c)
    {
        string path = WrapFolder + "/" + name + ".mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            AssetDatabase.CreateAsset(mat, path);
        }
        mat.SetColor("_BaseColor", c);
        mat.SetFloat("_Smoothness", 0.05f);
        mat.SetFloat("_Cull", 0f);
        mat.enableInstancing = true;
        EditorUtility.SetDirty(mat);
        return mat;
    }

    static Mesh GetProxyMesh(string name, bool canopy)
    {
        string path = WrapFolder + "/" + name + ".asset";
        var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
        if (existing != null) return existing;
        Mesh m = canopy ? BuildIcosahedron() : BuildTrunkMesh();
        m.name = name;
        AssetDatabase.CreateAsset(m, path);
        return m;
    }

    static Mesh BuildIcosahedron()   // 20 triangles, flat shaded
    {
        float t = (1f + Mathf.Sqrt(5f)) / 2f;
        var v = new[]
        {
            new Vector3(-1, t, 0), new Vector3(1, t, 0), new Vector3(-1, -t, 0), new Vector3(1, -t, 0),
            new Vector3(0, -1, t), new Vector3(0, 1, t), new Vector3(0, -1, -t), new Vector3(0, 1, -t),
            new Vector3(t, 0, -1), new Vector3(t, 0, 1), new Vector3(-t, 0, -1), new Vector3(-t, 0, 1)
        };
        for (int i = 0; i < v.Length; i++) v[i] = v[i].normalized;
        int[] f =
        {
            0, 11, 5, 0, 5, 1, 0, 1, 7, 0, 7, 10, 0, 10, 11,
            1, 5, 9, 5, 11, 4, 11, 10, 2, 10, 7, 6, 7, 1, 8,
            3, 9, 4, 3, 4, 2, 3, 2, 6, 3, 6, 8, 3, 8, 9,
            4, 9, 5, 2, 4, 11, 6, 2, 10, 8, 6, 7, 9, 8, 1
        };
        var verts = new Vector3[f.Length];
        var tris = new int[f.Length];
        for (int i = 0; i < f.Length; i++) { verts[i] = v[f[i]]; tris[i] = i; }
        var m = new Mesh();
        m.vertices = verts;
        m.triangles = tris;
        m.RecalculateNormals();
        m.RecalculateBounds();
        return m;
    }

    static Mesh BuildTrunkMesh()   // hexagonal prism, 1 unit radius and height
    {
        const int sides = 6;
        var verts = new List<Vector3>();
        var tris = new List<int>();
        for (int k = 0; k < sides; k++)
        {
            float a0 = k * Mathf.PI * 2f / sides, a1 = (k + 1) * Mathf.PI * 2f / sides;
            Vector3 p0 = new Vector3(Mathf.Cos(a0), 0f, Mathf.Sin(a0));
            Vector3 p1 = new Vector3(Mathf.Cos(a1), 0f, Mathf.Sin(a1));
            int i = verts.Count;
            verts.Add(p0); verts.Add(p0 + Vector3.up); verts.Add(p1 + Vector3.up); verts.Add(p1);
            tris.Add(i); tris.Add(i + 1); tris.Add(i + 2);
            tris.Add(i); tris.Add(i + 2); tris.Add(i + 3);
        }
        var m = new Mesh();
        m.SetVertices(verts);
        m.SetTriangles(tris, 0);
        m.RecalculateNormals();
        m.RecalculateBounds();
        return m;
    }

    void Build()
    {
        if (!Load()) return;

        protos = new List<GameObject>();
        protoScale = new List<float>();
        protoMap = new Dictionary<GameObject, int>();
        EditorUtility.DisplayProgressBar("Tree Builder", "Preparing models...", 0.02f);
        // second number = how tall that kind of tree should be, in meters
        int[] forest = ProtoIndices(forestPrefabs, 9f, true);
        int[] scrub = ProtoIndices(scrubPrefabs, 1.5f, false);
        int[] park = ProtoIndices(parkPrefabs, 6f, true);
        int[] farm = ProtoIndices(farmPrefabs, 4f, true);
        int[] roadside = ProtoIndices(roadsidePrefabs, 10f, true);
        if (protos.Count == 0) { Debug.LogError("Drag some tree models into the lists first."); return; }

        EditorUtility.DisplayProgressBar("Tree Builder", "Preparing...", 0.05f);
        BuildTables();
        placed = new Dictionary<Vector2Int, List<Placed>>();
        var rng = new System.Random(seed);
        noiseOff = rng.Next(0, 1000);

        int forestN = 0, scrubN = 0, parkN = 0, farmN = 0, roadN = 0;

        // Area trees
        for (int ai = 0; ai < areas.Count; ai++)
        {
            AreaInfo a = areas[ai];
            int[] pool; float spacing, jitter; bool clumpy;
            switch (a.kind)
            {
                case "forest": pool = forest; spacing = forestSpacing; jitter = 0.9f; clumpy = true; break;
                case "scrub": pool = scrub; spacing = scrubSpacing; jitter = 0.9f; clumpy = true; break;
                case "park": case "cemetery": pool = park; spacing = parkSpacing; jitter = 0.9f; clumpy = false; break;
                case "farmland": pool = farm; spacing = farmSpacing; jitter = 0.15f; clumpy = false; break;
                default: continue;
            }
            if (pool.Length == 0 || spacing < 1f) continue;

            if (ai % 20 == 0)
                EditorUtility.DisplayProgressBar("Tree Builder", "Area " + ai + " of " + areas.Count, 0.1f + 0.7f * ai / areas.Count);

            float startX = Mathf.Floor(a.minX / spacing) * spacing, startZ = Mathf.Floor(a.minZ / spacing) * spacing;
            for (float gz = startZ; gz <= a.maxZ; gz += spacing)
                for (float gx = startX; gx <= a.maxX; gx += spacing)
                {
                    float x = gx + ((float)rng.NextDouble() - 0.5f) * spacing * jitter;
                    float z = gz + ((float)rng.NextDouble() - 0.5f) * spacing * jitter;
                    if (!Inside(x, z, a.pts)) continue;
                    if (TopmostKind(x, z) != a.kind) continue;   // a higher-priority area (garden, parking...) is on top here

                    if (clumpy)   // patches and clearings instead of an even carpet
                    {
                        float d = Mathf.PerlinNoise(x * 0.035f + noiseOff, z * 0.035f + noiseOff);
                        if (rng.NextDouble() > Mathf.InverseLerp(0.15f, 0.55f, d)) continue;
                    }

                    if (TryAddTree(x, z, pool[rng.Next(pool.Length)], rng))
                    {
                        switch (a.kind)
                        {
                            case "forest": forestN++; break;
                            case "scrub": scrubN++; break;
                            case "farmland": farmN++; break;
                            default: parkN++; break;
                        }
                    }
                }
        }

        // Roadside palms along wide asphalt roads
        if (roadside.Length > 0 && roadsideSpacing >= 3f)
        {
            var pts = new List<Vector2>();
            for (int r = 0; r < data.roads.Length; r++)
            {
                if (r % 50 == 0)
                    EditorUtility.DisplayProgressBar("Tree Builder", "Roadside " + r + " of " + data.roads.Length, 0.8f + 0.1f * r / data.roads.Length);
                OsmRoad road = data.roads[r];
                float width, shoulder; int layer;
                if (!RoadBuilder.RoadSpec(road, out width, out shoulder, out layer) || road.bridge || road.tunnel) continue;
                if (layer != 3 || width < 6f) continue;           // only real asphalt roads
                float offset = width * roadWidthScale * 0.5f + roadClearance + 1f;

                pts.Clear();
                Resample(road.points, roadsideSpacing, pts);
                for (int i = 1; i < pts.Count - 1; i++)
                {
                    Vector2 dir = (pts[i + 1] - pts[i - 1]).normalized;
                    Vector2 nrm = new Vector2(-dir.y, dir.x);
                    for (int side = -1; side <= 1; side += 2)
                    {
                        if (rng.NextDouble() > roadsideChance) continue;
                        Vector2 p = pts[i] + nrm * side * offset;
                        if (TryAddTree(p.x, p.y, roadside[rng.Next(roadside.Length)], rng)) roadN++;
                    }
                }
            }
        }

        int total = mode == Mode.TerrainTrees ? SaveAsTerrainTrees() : SaveAsRealObjects();

        AssetDatabase.SaveAssets();
        EditorSceneManager.MarkAllScenesDirty();
        Debug.Log("Trees built (" + mode + "): " + total + " trees (" + forestN + " forest, " + scrubN + " scrub, " + parkN + " park, "
                  + farmN + " farmland, " + roadN + " roadside) using " + protos.Count + " different models.");
    }

    // ---------------------------------------------------------------- saving: two modes

    int SaveAsTerrainTrees()
    {
        EditorUtility.DisplayProgressBar("Tree Builder", "Saving Terrain Trees...", 0.95f);
        RemoveRealObjects();

        var protoArr = new TreePrototype[protos.Count];
        for (int i = 0; i < protos.Count; i++) protoArr[i] = new TreePrototype { prefab = protos[i] };

        int total = 0;
        foreach (var kv in chunks)
        {
            ChunkInfo c = kv.Value;
            List<Placed> list;
            placed.TryGetValue(kv.Key, out list);
            int n = list != null ? list.Count : 0;
            var arr = new TreeInstance[n];
            for (int i = 0; i < n; i++)
            {
                Placed p = list[i];
                arr[i] = new TreeInstance
                {
                    position = new Vector3(p.uv.x, 0f, p.uv.y),   // height is snapped to the terrain below
                    prototypeIndex = p.proto,
                    widthScale = p.width * protoScale[p.proto],
                    heightScale = p.height * protoScale[p.proto],
                    rotation = p.rotation,
                    color = Color.white,
                    lightmapColor = Color.white
                };
            }
            c.td.treePrototypes = protoArr;
            c.td.SetTreeInstances(arr, true);      // true = sit on the terrain surface
            EditorUtility.SetDirty(c.td);
            c.terrain.drawTreesAndFoliage = true;
            c.terrain.treeDistance = treeDistance;
            c.terrain.treeMaximumFullLODCount = 60;
            var tc = c.terrain.GetComponent<TerrainCollider>();
        
            {
                c.terrain.basemapDistance = 250f;
                c.terrain.heightmapPixelError = 8f;
                c.terrain.drawInstanced = true;
            }
            total += n;
        }
        return total;
    }

    int SaveAsRealObjects()
    {
        // Clear Terrain Trees so nothing is drawn twice, then rebuild World/Trees
        foreach (var kv in chunks)
        {
            kv.Value.td.SetTreeInstances(new TreeInstance[0], true);
            EditorUtility.SetDirty(kv.Value.td);
        }
        RemoveRealObjects();

        GameObject world = GameObject.Find("World");
        var treesRoot = new GameObject(TreesRootName);
        treesRoot.transform.SetParent(world.transform, false);

        int total = 0, done = 0, all = 0;
        foreach (var kv in placed) all += kv.Value.Count;

        foreach (var kv in placed)
        {
            var chunkGo = new GameObject("Trees_" + kv.Key.x + "_" + kv.Key.y);
            chunkGo.transform.SetParent(treesRoot.transform, false);
            foreach (Placed p in kv.Value)
            {
                if ((done++ & 255) == 0)
                    EditorUtility.DisplayProgressBar("Tree Builder", "Placing trees " + done + " of " + all, 0.9f + 0.09f * done / Mathf.Max(1, all));
                var go = (GameObject)PrefabUtility.InstantiatePrefab(protos[p.proto], chunkGo.transform);
                go.transform.position = p.world;
                go.transform.rotation = Quaternion.Euler(0f, p.rotation * Mathf.Rad2Deg, 0f);
                float k = protoScale[p.proto];
                go.transform.localScale = new Vector3(p.width * k, p.height * k, p.width * k);
                total++;
            }
        }
        return total;
    }

    static void RemoveRealObjects()
    {
        GameObject world = GameObject.Find("World");
        if (world == null) return;
        Transform old = world.transform.Find(TreesRootName);
        if (old != null) DestroyImmediate(old.gameObject);
    }

    void RemoveAll()
    {
        GameObject root = GameObject.Find("World");
        if (root == null) { Debug.LogWarning("No 'World' object found."); return; }
        foreach (var t in root.GetComponentsInChildren<Terrain>())
        {
            t.terrainData.SetTreeInstances(new TreeInstance[0], true);
            EditorUtility.SetDirty(t.terrainData);
        }
        RemoveRealObjects();
        AssetDatabase.SaveAssets();
        EditorSceneManager.MarkAllScenesDirty();
        Debug.Log("All trees removed.");
    }

    // ---------------------------------------------------------------- helpers

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
}

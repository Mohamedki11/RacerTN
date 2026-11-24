using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

// Editor tool: turns the OSM building outlines into 3D buildings.
// Each outline is extruded into walls, closed with a flat roof and a small parapet, and
// gets windows and one door (the door faces the nearest road). Everything is merged into
// one mesh per chunk (plus a simple collider mesh), saved in Assets/Game/Chunks.
// Order: 1) Terrain Builder  2) Road Builder  3) Road Mesh Builder  4) Building Builder
public class BuildingBuilder : EditorWindow
{
    const string ChunkFolder = "Assets/Game/Chunks";
    const string MaterialFolder = "Assets/Game/Materials/Buildings";
    const float RoadCell = 16f;

    class ChunkInfo
    {
        public TerrainData td;
        public float ox, oz, baseY;
    }

    class MeshData
    {
        public List<Vector3> verts = new List<Vector3>();
        public List<Vector3> normals = new List<Vector3>();
        public List<Vector2> uvs = new List<Vector2>();
        public List<int>[] tris;

        public MeshData(int submeshes)
        {
            tris = new List<int>[submeshes];
            for (int i = 0; i < submeshes; i++) tris[i] = new List<int>();
        }
    }

    struct RoadPoint
    {
        public Vector2 pos;
        public float halfWidth;
    }

    [SerializeField] WorldConfig config;
    [SerializeField] string osmJsonPath = "Assets/Game/RawData/SidiBouSaid/osm_world.json";

    [SerializeField] Material wall1, wall2, wall3;   // wall 2 and 3 are optional variants
    [SerializeField] Material roofMat, windowMat, doorMat;

    [SerializeField] float minArea = 12f;            // smaller outlines are skipped
    [SerializeField] float maxArea = 6000f;          // larger outlines are skipped
    [SerializeField] float floorHeight = 3f;
    [SerializeField] int minFloors = 1, maxFloors = 2; // when OSM has no height info
    [SerializeField] float parapet = 0.45f;
    [SerializeField] float wallTile = 3f;            // meters covered by one wall texture tile
    [SerializeField] float roofTile = 4f;
    [SerializeField] float windowSpacing = 3.5f;
    [SerializeField] float windowWidth = 1f, windowHeight = 1.4f, windowSill = 0.9f;
    [SerializeField] float doorWidth = 1.2f, doorHeight = 2.2f;
    [SerializeField] float minGroundHeight = 0.3f;   // buildings lower than this (in the sea) are skipped
    [SerializeField] bool skipOnRoads = true;
    [SerializeField] float roadClearance = 1.5f;   // keeps room for a pavement; equals RoadShaper.BuildingMargin
    [SerializeField] int seed = 1;

    Dictionary<Vector2Int, ChunkInfo> chunks;
    Dictionary<long, List<RoadPoint>> roadGrid;
    OsmWorldData data;
    Vector2 scroll;

    int sBuilt, sSmall, sBig, sOutside, sWater, sRoad, sWindows, sDoors;

    [MenuItem("RacerTN/Building Builder")]
    static void Open() { GetWindow<BuildingBuilder>("Building Builder"); }

    void OnGUI()
    {
        scroll = EditorGUILayout.BeginScrollView(scroll);
        config = (WorldConfig)EditorGUILayout.ObjectField("World Config", config, typeof(WorldConfig), false);
        osmJsonPath = EditorGUILayout.TextField("OSM JSON", osmJsonPath);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Materials", EditorStyles.boldLabel);
        wall1 = (Material)EditorGUILayout.ObjectField("Wall 1", wall1, typeof(Material), false);
        wall2 = (Material)EditorGUILayout.ObjectField("Wall 2 (optional)", wall2, typeof(Material), false);
        wall3 = (Material)EditorGUILayout.ObjectField("Wall 3 (optional)", wall3, typeof(Material), false);
        roofMat = (Material)EditorGUILayout.ObjectField("Roof", roofMat, typeof(Material), false);
        windowMat = (Material)EditorGUILayout.ObjectField("Windows", windowMat, typeof(Material), false);
        doorMat = (Material)EditorGUILayout.ObjectField("Doors", doorMat, typeof(Material), false);
        if (GUILayout.Button("Create starter materials", GUILayout.Height(24))) Run(CreateStarterMaterials);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Shape", EditorStyles.boldLabel);
        minArea = EditorGUILayout.FloatField("Min footprint area (m2)", minArea);
        maxArea = EditorGUILayout.FloatField("Max footprint area (m2)", maxArea);
        floorHeight = EditorGUILayout.FloatField("Floor height (m)", floorHeight);
        minFloors = EditorGUILayout.IntField("Default min floors", minFloors);
        maxFloors = EditorGUILayout.IntField("Default max floors", maxFloors);
        parapet = EditorGUILayout.FloatField("Parapet height (m)", parapet);
        minGroundHeight = EditorGUILayout.FloatField("Skip below height (m)", minGroundHeight);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Textures and details", EditorStyles.boldLabel);
        wallTile = EditorGUILayout.FloatField("Wall tile size (m)", wallTile);
        roofTile = EditorGUILayout.FloatField("Roof tile size (m)", roofTile);
        windowSpacing = EditorGUILayout.FloatField("Window spacing (m)", windowSpacing);
        windowWidth = EditorGUILayout.FloatField("Window width (m)", windowWidth);
        windowHeight = EditorGUILayout.FloatField("Window height (m)", windowHeight);
        windowSill = EditorGUILayout.FloatField("Window sill height (m)", windowSill);
        doorWidth = EditorGUILayout.FloatField("Door width (m)", doorWidth);
        doorHeight = EditorGUILayout.FloatField("Door height (m)", doorHeight);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Roads", EditorStyles.boldLabel);
        skipOnRoads = EditorGUILayout.Toggle("Skip buildings on roads", skipOnRoads);
        roadClearance = EditorGUILayout.FloatField("Road clearance (m)", roadClearance);
        seed = EditorGUILayout.IntField("Seed", seed);

        EditorGUILayout.Space();
        if (GUILayout.Button("Build buildings", GUILayout.Height(30))) Run(Build);
        if (GUILayout.Button("Remove buildings", GUILayout.Height(22))) Run(RemoveAll);
        EditorGUILayout.HelpBox("Run the Terrain Builder and Road Builder first. After rebuilding the terrain, run this again.", MessageType.Info);
        EditorGUILayout.EndScrollView();
    }

    void Run(Action action)
    {
        try { action(); }
        catch (Exception e) { Debug.LogError("BuildingBuilder failed: " + e); }
        finally { EditorUtility.ClearProgressBar(); }
    }

    // ---------------------------------------------------------------- loading

    bool Load()
    {
        if (config == null) { Debug.LogError("Assign the WorldConfig first."); return false; }

        string full = Path.GetFullPath(osmJsonPath);
        if (!File.Exists(full)) { Debug.LogError("OSM JSON not found: " + full); return false; }
        data = JsonUtility.FromJson<OsmWorldData>(File.ReadAllText(full));
        if (data == null || data.buildings == null || data.roads == null)
        {
            Debug.LogError("The OSM JSON is empty or unreadable.");
            return false;
        }

        GameObject root = GameObject.Find("World");
        if (root == null) { Debug.LogError("No 'World' object in the scene. Run the Terrain Builder first."); return false; }

        chunks = new Dictionary<Vector2Int, ChunkInfo>();
        float cs = config.chunkSize;
        foreach (var t in root.GetComponentsInChildren<Terrain>())
        {
            Vector3 p = t.transform.position;
            var key = new Vector2Int(Mathf.RoundToInt(p.x / cs), Mathf.RoundToInt(p.z / cs));
            chunks[key] = new ChunkInfo { td = t.terrainData, ox = p.x, oz = p.z, baseY = p.y };
        }
        if (chunks.Count == 0) { Debug.LogError("No terrain chunks found under 'World'."); return false; }
        return true;
    }

    // Terrain height (world meters) at a world position, or false if no chunk covers it
    bool Sample(float x, float z, out float h)
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
        if (!Load()) return;

        var walls = new List<Material>();
        if (wall1 != null) walls.Add(wall1);
        if (wall2 != null) walls.Add(wall2);
        if (wall3 != null) walls.Add(wall3);
        if (walls.Count == 0 || roofMat == null || windowMat == null || doorMat == null)
        {
            Debug.LogError("Assign Wall 1, Roof, Windows and Doors first (the 'Create starter materials' button makes them).");
            return;
        }

        float cs = config.chunkSize;
        GameObject root = GameObject.Find("World");
        RemoveOld(root);

        int W = walls.Count;
        int subRoof = W, subWin = W + 1, subDoor = W + 2, subCount = W + 3;

        sBuilt = sSmall = sBig = sOutside = sWater = sRoad = sWindows = sDoors = 0;
        BuildRoadGrid();

        var meshes = new Dictionary<Vector2Int, MeshData>();
        for (int b = 0; b < data.buildings.Length; b++)
        {
            if (b % 100 == 0)
                EditorUtility.DisplayProgressBar("Building Builder", "Building " + b + " of " + data.buildings.Length, b / (float)data.buildings.Length);
            AddBuilding(data.buildings[b], meshes, W, subRoof, subWin, subDoor, subCount, cs);
        }

        if (!AssetDatabase.IsValidFolder(ChunkFolder)) AssetDatabase.CreateFolder("Assets/Game", "Chunks");

        var mats = new Material[subCount];
        for (int i = 0; i < W; i++) mats[i] = walls[i];
        mats[subRoof] = roofMat;
        mats[subWin] = windowMat;
        mats[subDoor] = doorMat;

        long triangles = 0;
        foreach (var kv in meshes)
        {
            MeshData md = kv.Value;
            string id = kv.Key.x + "_" + kv.Key.y;

            var mesh = new Mesh();
            mesh.name = "Buildings_" + id;
            mesh.indexFormat = IndexFormat.UInt32;
            mesh.SetVertices(md.verts);
            mesh.SetNormals(md.normals);
            mesh.SetUVs(0, md.uvs);
            mesh.subMeshCount = subCount;
            for (int s = 0; s < subCount; s++)
            {
                mesh.SetTriangles(md.tris[s], s);
                triangles += md.tris[s].Count / 3;
            }
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();

            // Collider: walls and roofs only (no windows or doors)
            var colTris = new List<int>();
            for (int s = 0; s <= W; s++) colTris.AddRange(md.tris[s]);
            var colMesh = new Mesh();
            colMesh.name = "BuildingsCol_" + id;
            colMesh.indexFormat = IndexFormat.UInt32;
            colMesh.SetVertices(md.verts);
            colMesh.SetTriangles(colTris, 0);
            colMesh.RecalculateBounds();

            string path = ChunkFolder + "/" + mesh.name + ".asset";
            string colPath = ChunkFolder + "/" + colMesh.name + ".asset";
            AssetDatabase.DeleteAsset(path);
            AssetDatabase.DeleteAsset(colPath);
            AssetDatabase.CreateAsset(mesh, path);
            AssetDatabase.CreateAsset(colMesh, colPath);

            var go = new GameObject(mesh.name);
            go.transform.SetParent(root.transform, false);
            go.transform.position = new Vector3(kv.Key.x * cs, 0f, kv.Key.y * cs);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterials = mats;
            go.AddComponent<MeshCollider>().sharedMesh = colMesh;
        }

        AssetDatabase.SaveAssets();
        EditorSceneManager.MarkAllScenesDirty();
        Debug.Log("Buildings built: " + sBuilt + " (" + sWindows + " windows, " + sDoors + " doors, " + triangles + " triangles, " + meshes.Count + " chunk meshes).\n"
                  + "Skipped: " + sSmall + " too small, " + sBig + " too large, " + sOutside + " outside the built chunks, " + sWater + " in the sea.\n"
                  + "Buildings overlapping roads: " + sRoad + (skipOnRoads ? " (skipped)" : " (kept: tick 'Skip buildings on roads' to remove them)"));
    }

    void AddBuilding(OsmBuilding bd, Dictionary<Vector2Int, MeshData> meshes,
                     int W, int subRoof, int subWin, int subDoor, int subCount, float cs)
    {
        List<Vector2> fp = CleanFootprint(bd.footprint);
        if (fp == null) { sSmall++; return; }

        float area = SignedArea(fp);
        if (area < 0f) { fp.Reverse(); area = -area; }   // make it counter-clockwise
        if (area < minArea) { sSmall++; return; }
        if (area > maxArea) { sBig++; return; }

        int n = fp.Count;
        float minG = float.MaxValue, maxG = float.MinValue;
        Vector2 centroid = Vector2.zero;
        for (int i = 0; i < n; i++)
        {
            float g;
            if (!Sample(fp[i].x, fp[i].y, out g)) { sOutside++; return; }
            minG = Mathf.Min(minG, g);
            maxG = Mathf.Max(maxG, g);
            centroid += fp[i];
        }
        centroid /= n;
        if (minG < minGroundHeight) { sWater++; return; }

        if (OnRoad(fp)) { sRoad++; if (skipOnRoads) return; }

        var rng = new System.Random(unchecked(seed * 7919 + Mathf.RoundToInt(fp[0].x * 10f) * 73856093 ^ Mathf.RoundToInt(fp[0].y * 10f) * 19349663));
        float height = bd.height > 0.5f ? bd.height
                     : bd.levels > 0 ? bd.levels * floorHeight + 0.4f
                     : DefaultHeight(bd.type, rng);
        height = Mathf.Clamp(height, 2.5f, 60f);

        float roofY = maxG + height;         // full height on the uphill side
        float bottomY = minG - 0.6f;         // walls reach below the downhill ground
        float topY = roofY + parapet;
        int wallSub = rng.Next(0, W);

        var key = new Vector2Int(Mathf.FloorToInt(centroid.x / cs), Mathf.FloorToInt(centroid.y / cs));
        MeshData md;
        if (!meshes.TryGetValue(key, out md)) { md = new MeshData(subCount); meshes[key] = md; }
        Vector3 origin = new Vector3(key.x * cs, 0f, key.y * cs);

        // The door goes on the edge closest to a road
        int doorEdge = -1;
        float bestScore = float.MaxValue;
        for (int i = 0; i < n; i++)
        {
            Vector2 a = fp[i], c = fp[(i + 1) % n];
            float len = Vector2.Distance(a, c);
            if (len < doorWidth + 1.2f) continue;
            float d = NearestRoadDistance((a + c) * 0.5f);
            float score = d < float.MaxValue ? d : 1000f - len;
            if (score < bestScore) { bestScore = score; doorEdge = i; }
        }

        for (int i = 0; i < n; i++)
        {
            Vector2 a = fp[i], c = fp[(i + 1) % n];
            Vector2 e = c - a;
            float len = e.magnitude;
            if (len < 0.05f) continue;
            Vector2 dir = e / len;
            Vector3 nrm = new Vector3(dir.y, 0f, -dir.x);   // outward, because the outline is counter-clockwise

            // Outer wall, from below the ground up to the top of the parapet
            Quad(md, wallSub,
                 new Vector3(a.x, bottomY, a.y), new Vector3(c.x, bottomY, c.y),
                 new Vector3(a.x, topY, a.y), new Vector3(c.x, topY, c.y),
                 nrm, origin,
                 new Vector2(0f, bottomY / wallTile), new Vector2(len / wallTile, bottomY / wallTile),
                 new Vector2(0f, topY / wallTile), new Vector2(len / wallTile, topY / wallTile), false);

            // Inner face of the parapet (so it is not see-through from above)
            if (parapet > 0.01f)
                Quad(md, wallSub,
                     new Vector3(a.x, roofY, a.y), new Vector3(c.x, roofY, c.y),
                     new Vector3(a.x, topY, a.y), new Vector3(c.x, topY, c.y),
                     -nrm, origin,
                     new Vector2(0f, roofY / wallTile), new Vector2(len / wallTile, roofY / wallTile),
                     new Vector2(0f, topY / wallTile), new Vector2(len / wallTile, topY / wallTile), true);

            // Door
            float doorS = len * 0.5f;
            if (i == doorEdge)
            {
                Vector2 p = a + dir * doorS;
                float g;
                if (!Sample(p.x, p.y, out g)) g = minG;
                AddOpening(md, subDoor, a, dir, nrm, doorS, doorWidth, g + 0.02f, doorHeight, origin);
                sDoors++;
            }

            // Windows, one row per floor, following the ground
            if (len >= 2.6f)
            {
                int count = Mathf.Max(1, Mathf.FloorToInt(len / windowSpacing));
                for (int k = 0; k < count; k++)
                {
                    float s = (k + 0.5f) * len / count;
                    Vector2 p = a + dir * s;
                    float g;
                    if (!Sample(p.x, p.y, out g)) g = minG;
                    for (int fl = 0; ; fl++)
                    {
                        float bottom = g + windowSill + fl * floorHeight;
                        if (bottom + windowHeight > roofY - 0.3f) break;
                        if (i == doorEdge && fl == 0 && Mathf.Abs(s - doorS) < (doorWidth + windowWidth) * 0.5f + 0.3f) continue;
                        AddOpening(md, subWin, a, dir, nrm, s, windowWidth, bottom, windowHeight, origin);
                        sWindows++;
                    }
                }
            }
        }

        // Flat roof
        List<int> tri = Triangulate(fp);
        int baseIndex = md.verts.Count;
        for (int i = 0; i < n; i++)
        {
            md.verts.Add(new Vector3(fp[i].x, roofY, fp[i].y) - origin);
            md.normals.Add(Vector3.up);
            md.uvs.Add(fp[i] / roofTile);
        }
        for (int t = 0; t + 2 < tri.Count; t += 3)
        {
            md.tris[subRoof].Add(baseIndex + tri[t]);       // reversed: clockwise seen from above
            md.tris[subRoof].Add(baseIndex + tri[t + 2]);
            md.tris[subRoof].Add(baseIndex + tri[t + 1]);
        }
        sBuilt++;
    }

    float DefaultHeight(string type, System.Random rng)
    {
        int floors;
        switch (type)
        {
            case "garage": case "garages": case "shed": case "roof": case "carport":
            case "kiosk": case "hut": case "service": case "cabin": case "greenhouse": case "barn":
                return 2.8f;
            case "apartments": case "dormitory":
                floors = rng.Next(3, 6); break;
            case "commercial": case "retail": case "office": case "hotel":
            case "hospital": case "school": case "university":
                floors = rng.Next(2, 5); break;
            default:
                floors = rng.Next(Mathf.Max(1, minFloors), Mathf.Max(minFloors, maxFloors) + 1); break;
        }
        return floors * floorHeight + 0.4f;
    }

    // ---------------------------------------------------------------- mesh helpers

    // A rectangle on a wall. A0 and B0 are the bottom corners (A on the left seen from outside),
    // A1 and B1 the top corners. "flip" makes it face the other way.
    static void Quad(MeshData md, int sub, Vector3 a0, Vector3 b0, Vector3 a1, Vector3 b1,
                     Vector3 n, Vector3 origin, Vector2 uvA0, Vector2 uvB0, Vector2 uvA1, Vector2 uvB1, bool flip)
    {
        int i = md.verts.Count;
        md.verts.Add(a0 - origin);
        md.verts.Add(b0 - origin);
        md.verts.Add(a1 - origin);
        md.verts.Add(b1 - origin);
        for (int k = 0; k < 4; k++) md.normals.Add(n);
        md.uvs.Add(uvA0);
        md.uvs.Add(uvB0);
        md.uvs.Add(uvA1);
        md.uvs.Add(uvB1);

        List<int> t = md.tris[sub];
        if (!flip)
        {
            t.Add(i); t.Add(i + 2); t.Add(i + 1);
            t.Add(i + 1); t.Add(i + 2); t.Add(i + 3);
        }
        else
        {
            t.Add(i); t.Add(i + 1); t.Add(i + 2);
            t.Add(i + 1); t.Add(i + 3); t.Add(i + 2);
        }
    }

    // A window or door: a small rectangle 3 cm in front of the wall, with texture 0..1
    static void AddOpening(MeshData md, int sub, Vector2 a, Vector2 dir, Vector3 nrm,
                           float s, float width, float bottom, float height, Vector3 origin)
    {
        Vector2 p0 = a + dir * (s - width * 0.5f);
        Vector2 p1 = a + dir * (s + width * 0.5f);
        Vector3 off = nrm * 0.03f;
        Quad(md, sub,
             new Vector3(p0.x, bottom, p0.y) + off, new Vector3(p1.x, bottom, p1.y) + off,
             new Vector3(p0.x, bottom + height, p0.y) + off, new Vector3(p1.x, bottom + height, p1.y) + off,
             nrm, origin, Vector2.zero, Vector2.right, Vector2.up, Vector2.one, false);
    }

    // Splits a flat outline into triangles by "ear clipping". Works for concave outlines.
    // The outline must be counter-clockwise. Returns triples of indices (counter-clockwise).
    static List<int> Triangulate(List<Vector2> p)
    {
        var tris = new List<int>();
        var idx = new List<int>();
        for (int i = 0; i < p.Count; i++) idx.Add(i);

        int guard = p.Count * p.Count + 10;
        while (idx.Count > 3 && guard-- > 0)
        {
            bool clipped = false;
            for (int i = 0; i < idx.Count; i++)
            {
                int ia = idx[(i + idx.Count - 1) % idx.Count], ib = idx[i], ic = idx[(i + 1) % idx.Count];
                Vector2 a = p[ia], b = p[ib], c = p[ic];
                if (Cross(b - a, c - b) <= 1e-5f) continue;       // reflex or flat corner: not an ear

                bool blocked = false;
                for (int k = 0; k < idx.Count; k++)
                {
                    int ik = idx[k];
                    if (ik == ia || ik == ib || ik == ic) continue;
                    if (InTriangle(p[ik], a, b, c)) { blocked = true; break; }
                }
                if (blocked) continue;

                tris.Add(ia); tris.Add(ib); tris.Add(ic);
                idx.RemoveAt(i);
                clipped = true;
                break;
            }
            if (!clipped) break;                                   // odd outline: fan out the rest
        }
        if (idx.Count >= 3)
            for (int k = 1; k < idx.Count - 1; k++)
            {
                tris.Add(idx[0]); tris.Add(idx[k]); tris.Add(idx[k + 1]);
            }
        return tris;
    }

    static float Cross(Vector2 a, Vector2 b) { return a.x * b.y - a.y * b.x; }

    static bool InTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        return Cross(b - a, p - a) >= -1e-6f && Cross(c - b, p - b) >= -1e-6f && Cross(a - c, p - c) >= -1e-6f;
    }

    static float SignedArea(List<Vector2> p)
    {
        float sum = 0f;
        for (int i = 0; i < p.Count; i++)
        {
            Vector2 a = p[i], b = p[(i + 1) % p.Count];
            sum += a.x * b.y - b.x * a.y;
        }
        return sum * 0.5f;
    }

    // Removes the repeated closing point and points closer than 5 cm
    static List<Vector2> CleanFootprint(Vector2[] src)
    {
        if (src == null || src.Length < 3) return null;
        var list = new List<Vector2>(src.Length);
        foreach (var p in src)
            if (list.Count == 0 || (p - list[list.Count - 1]).sqrMagnitude > 0.0025f) list.Add(p);
        while (list.Count > 1 && (list[0] - list[list.Count - 1]).sqrMagnitude <= 0.0025f) list.RemoveAt(list.Count - 1);
        return list.Count >= 3 ? list : null;
    }

    // ---------------------------------------------------------------- roads (for doors and overlap checks)

    static long Key(int gx, int gz) { return ((long)gx << 32) | (uint)gz; }

    void BuildRoadGrid()
    {
        roadGrid = new Dictionary<long, List<RoadPoint>>();
        var pts = new List<Vector2>();
        foreach (var road in data.roads)
        {
            float width, shoulder; int layer;
            if (!RoadBuilder.RoadSpec(road, out width, out shoulder, out layer) || road.bridge || road.tunnel) continue;
            float half = RoadShaper.MinHalfWidth;   // the narrowest street the Road Builder leaves
            pts.Clear();
            Resample(road.points, 2f, pts);
            foreach (var p in pts)
            {
                long key = Key(Mathf.FloorToInt(p.x / RoadCell), Mathf.FloorToInt(p.y / RoadCell));
                List<RoadPoint> list;
                if (!roadGrid.TryGetValue(key, out list)) { list = new List<RoadPoint>(); roadGrid[key] = list; }
                list.Add(new RoadPoint { pos = p, halfWidth = half });
            }
        }
    }

    // Distance to the nearest road point within about 16 m, or float.MaxValue if none
    float NearestRoadDistance(Vector2 p)
    {
        float best = float.MaxValue;
        int gx = Mathf.FloorToInt(p.x / RoadCell), gz = Mathf.FloorToInt(p.y / RoadCell);
        for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
            {
                List<RoadPoint> list;
                if (!roadGrid.TryGetValue(Key(gx + dx, gz + dz), out list)) continue;
                foreach (var rp in list)
                {
                    float d = Vector2.Distance(rp.pos, p);
                    if (d < best) best = d;
                }
            }
        return best;
    }

    // True if any point along the outline lies on a road (inside its half width plus clearance)
    bool OnRoad(List<Vector2> fp)
    {
        int n = fp.Count;
        for (int i = 0; i < n; i++)
        {
            Vector2 a = fp[i], c = fp[(i + 1) % n];
            int steps = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(a, c) / 2f));
            for (int s = 0; s < steps; s++)
            {
                Vector2 q = Vector2.Lerp(a, c, s / (float)steps);
                int gx = Mathf.FloorToInt(q.x / RoadCell), gz = Mathf.FloorToInt(q.y / RoadCell);
                for (int dz = -1; dz <= 1; dz++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        List<RoadPoint> list;
                        if (!roadGrid.TryGetValue(Key(gx + dx, gz + dz), out list)) continue;
                        foreach (var rp in list)
                            if (Vector2.Distance(rp.pos, q) < rp.halfWidth + roadClearance) return true;
                    }
            }
        }
        return false;
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

    // ---------------------------------------------------------------- cleanup

    void RemoveAll()
    {
        GameObject root = GameObject.Find("World");
        if (root == null) { Debug.LogWarning("No 'World' object found."); return; }
        RemoveOld(root);
        EditorSceneManager.MarkAllScenesDirty();
    }

    static void RemoveOld(GameObject root)
    {
        for (int i = root.transform.childCount - 1; i >= 0; i--)
        {
            Transform t = root.transform.GetChild(i);
            if (t.name.StartsWith("Buildings_")) DestroyImmediate(t.gameObject);
        }
    }

    // ---------------------------------------------------------------- starter materials

    void CreateStarterMaterials()
    {
        EnsureFolder("Assets/Game", "Materials");
        EnsureFolder("Assets/Game/Materials", "Buildings");

        Color blue = new Color(0.12f, 0.37f, 0.66f);
        wall1 = MakeMaterial("M_Wall_Whitewash", null, new Color(0.93f, 0.92f, 0.88f), 0.1f);
        roofMat = MakeMaterial("M_Roof_Flat", null, new Color(0.72f, 0.70f, 0.66f), 0.05f);
        windowMat = MakeMaterial("M_Window_Blue", MakeWindowTexture("T_Window_Blue", blue), Color.white, 0.5f);
        doorMat = MakeMaterial("M_Door_Blue", MakeDoorTexture("T_Door_Blue", blue), Color.white, 0.2f);
        AssetDatabase.SaveAssets();
        Debug.Log("Starter materials created in " + MaterialFolder + ". Swap in your ambientCG textures on M_Wall_Whitewash and M_Roof_Flat.");
    }

    static void EnsureFolder(string parent, string name)
    {
        if (!AssetDatabase.IsValidFolder(parent + "/" + name)) AssetDatabase.CreateFolder(parent, name);
    }

    static Material MakeMaterial(string name, Texture2D baseMap, Color color, float smoothness)
    {
        string path = MaterialFolder + "/" + name + ".mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        bool isNew = mat == null;
        if (isNew) mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        mat.SetColor("_BaseColor", color);
        mat.SetFloat("_Smoothness", smoothness);
        if (baseMap != null) mat.SetTexture("_BaseMap", baseMap);
        if (isNew) AssetDatabase.CreateAsset(mat, path);
        else EditorUtility.SetDirty(mat);
        return mat;
    }

    static Texture2D MakeWindowTexture(string name, Color blue)
    {
        const int N = 128;
        var tex = new Texture2D(N, N, TextureFormat.RGBA32, false);
        var glass = new Color(0.07f, 0.11f, 0.15f);
        var sill = new Color(0.92f, 0.91f, 0.87f);
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                float u = x / (N - 1f), v = y / (N - 1f);
                Color c = blue;
                bool inGlass = u > 0.10f && u < 0.90f && v > 0.12f && v < 0.92f;
                bool mullion = Mathf.Abs(u - 0.5f) < 0.03f || Mathf.Abs(v - 0.58f) < 0.025f;
                if (inGlass && !mullion) c = glass;
                if (v < 0.06f) c = sill;
                tex.SetPixel(x, y, c);
            }
        return SaveTexture(tex, name);
    }

    static Texture2D MakeDoorTexture(string name, Color blue)
    {
        const int N = 128;
        var tex = new Texture2D(N, N, TextureFormat.RGBA32, false);
        Color dark = blue * 0.65f; dark.a = 1f;
        Color stud = new Color(0.85f, 0.8f, 0.55f);
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                float u = x / (N - 1f), v = y / (N - 1f);
                Color c = blue;
                if (Mathf.Abs(Mathf.Repeat(u * 5f, 1f) - 0.5f) > 0.46f) c = dark;          // plank gaps
                if (u < 0.05f || u > 0.95f || v > 0.96f) c = dark;                           // frame
                float su = Mathf.Repeat(u * 5f, 1f) - 0.5f, sv = Mathf.Repeat(v * 8f, 1f) - 0.5f;
                if (su * su + sv * sv < 0.012f && u > 0.1f && u < 0.9f && v > 0.08f && v < 0.9f) c = stud;
                tex.SetPixel(x, y, c);
            }
        return SaveTexture(tex, name);
    }

    static Texture2D SaveTexture(Texture2D tex, string name)
    {
        string path = MaterialFolder + "/" + name + ".png";
        File.WriteAllBytes(Path.GetFullPath(path), tex.EncodeToPNG());
        DestroyImmediate(tex);
        AssetDatabase.ImportAsset(path);
        var importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer != null)
        {
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.SaveAndReimport();
        }
        return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }
}

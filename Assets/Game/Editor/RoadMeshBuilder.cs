using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

// Editor tool: lays a flat road mesh with lane markings along every wide OSM road.
// A terrain layer can only paint plain asphalt, because terrain textures tile along the
// world axes. A mesh strip has UVs that follow the road, so the markings follow it too.
// Order: 1) Terrain Builder   2) Road Builder   3) Road Mesh Builder
public class RoadMeshBuilder : EditorWindow
{
    const string ChunkFolder = "Assets/Game/Chunks";

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
        public List<int> tris = new List<int>();
    }

    WorldConfig config;
    string osmJsonPath = "Assets/Game/RawData/SidiBouSaid/osm_world.json";
    Material roadMaterial;
    float spacing = 2f;                // meters between points along the road
    float widthScale = 1.15f;          // keep equal to the Road Builder value
    float meshWidthFactor = 0.96f;     // strip is a little narrower than the carved road
    float minWidthForMarkings = 7.5f;  // narrower roads stay plain asphalt
    float lift = 0.06f;                // meters above the terrain, avoids flickering
    float tileLengthPerWidth = 1f;     // one texture tile is this many road-widths long
    bool swapAxes;                     // tick if the markings run across the road

    Dictionary<Vector2Int, ChunkInfo> chunks;
    OsmWorldData data;
    Vector2 scroll;

    [MenuItem("RacerTN/Road Mesh Builder")]
    static void Open() { GetWindow<RoadMeshBuilder>("Road Mesh Builder"); }

    void OnGUI()
    {
        scroll = EditorGUILayout.BeginScrollView(scroll);
        config = (WorldConfig)EditorGUILayout.ObjectField("World Config", config, typeof(WorldConfig), false);
        osmJsonPath = EditorGUILayout.TextField("OSM JSON", osmJsonPath);
        roadMaterial = (Material)EditorGUILayout.ObjectField("Road material", roadMaterial, typeof(Material), false);

        EditorGUILayout.Space();
        spacing = EditorGUILayout.FloatField("Point spacing (m)", spacing);
        widthScale = EditorGUILayout.FloatField("Road width scale", widthScale);
        meshWidthFactor = EditorGUILayout.FloatField("Mesh width factor", meshWidthFactor);
        minWidthForMarkings = EditorGUILayout.FloatField("Min width for markings (m)", minWidthForMarkings);
        lift = EditorGUILayout.FloatField("Lift above terrain (m)", lift);
        tileLengthPerWidth = EditorGUILayout.FloatField("Tile length per road width", tileLengthPerWidth);
        swapAxes = EditorGUILayout.Toggle("Swap texture axes", swapAxes);

        EditorGUILayout.Space();
        if (GUILayout.Button("Build road meshes", GUILayout.Height(30))) Run(Build);
        if (GUILayout.Button("Remove road meshes", GUILayout.Height(22))) Run(RemoveAll);
        EditorGUILayout.HelpBox("Run the Terrain Builder and the Road Builder first. After rebuilding either of them, run this again.", MessageType.Info);
        EditorGUILayout.EndScrollView();
    }

    void Run(Action action)
    {
        try { action(); }
        catch (Exception e) { Debug.LogError("RoadMeshBuilder failed: " + e); }
        finally { EditorUtility.ClearProgressBar(); }
    }

    // ---------------------------------------------------------------- loading

    bool Load()
    {
        if (config == null) { Debug.LogError("Assign the WorldConfig first."); return false; }
        if (roadMaterial == null) { Debug.LogError("Assign the road material first."); return false; }

        string full = Path.GetFullPath(osmJsonPath);
        if (!File.Exists(full)) { Debug.LogError("OSM JSON not found: " + full); return false; }
        data = JsonUtility.FromJson<OsmWorldData>(File.ReadAllText(full));
        if (data == null || data.roads == null) { Debug.LogError("The OSM JSON is empty or unreadable."); return false; }

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

    // Terrain height (world meters) and surface normal at a world position
    bool Sample(float x, float z, out float h, out Vector3 normal)
    {
        float cs = config.chunkSize;
        ChunkInfo c;
        if (!chunks.TryGetValue(new Vector2Int(Mathf.FloorToInt(x / cs), Mathf.FloorToInt(z / cs)), out c))
        {
            h = 0f;
            normal = Vector3.up;
            return false;
        }
        float u = Mathf.Clamp01((x - c.ox) / cs), v = Mathf.Clamp01((z - c.oz) / cs);
        h = c.td.GetInterpolatedHeight(u, v) + c.baseY;
        normal = c.td.GetInterpolatedNormal(u, v);
        return true;
    }

    // ---------------------------------------------------------------- build

    void Build()
    {
        if (!Load()) return;
        float cs = config.chunkSize;
        GameObject root = GameObject.Find("World");
        RemoveOld(root);

        var meshes = new Dictionary<Vector2Int, MeshData>();
        var pts = new List<Vector2>();
        int usedRoads = 0, segments = 0;

        for (int r = 0; r < data.roads.Length; r++)
        {
            if (r % 50 == 0)
                EditorUtility.DisplayProgressBar("Road Mesh Builder", "Road " + r + " of " + data.roads.Length, r / (float)data.roads.Length);

            OsmRoad road = data.roads[r];
            float width, shoulder; int layer;
            if (!RoadBuilder.RoadSpec(road, out width, out shoulder, out layer) || road.bridge || road.tunnel) continue;
            if (layer != 3) continue;                 // dirt tracks stay painted
            width *= widthScale;
            if (width < minWidthForMarkings) continue;

            pts.Clear();
            Resample(road.points, spacing, pts);
            float roadLift = lift + (r % 8) * 0.004f; // tiny difference so crossing roads do not flicker
            int added = AddRoad(pts, width * 0.5f * meshWidthFactor, width * tileLengthPerWidth, roadLift, meshes, cs);
            if (added > 0) { usedRoads++; segments += added; }
        }

        if (!AssetDatabase.IsValidFolder(ChunkFolder)) AssetDatabase.CreateFolder("Assets/Game", "Chunks");

        foreach (var kv in meshes)
        {
            MeshData md = kv.Value;
            var mesh = new Mesh();
            mesh.name = "Roads_" + kv.Key.x + "_" + kv.Key.y;
            mesh.indexFormat = IndexFormat.UInt32;
            mesh.SetVertices(md.verts);
            mesh.SetNormals(md.normals);
            mesh.SetUVs(0, md.uvs);
            mesh.SetTriangles(md.tris, 0);
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();

            string path = ChunkFolder + "/" + mesh.name + ".asset";
            AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(mesh, path);

            var go = new GameObject(mesh.name);
            go.transform.SetParent(root.transform, false);
            go.transform.position = new Vector3(kv.Key.x * cs, 0f, kv.Key.y * cs);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = roadMaterial;
            mr.shadowCastingMode = ShadowCastingMode.Off;
        }

        AssetDatabase.SaveAssets();
        EditorSceneManager.MarkAllScenesDirty();
        Debug.Log("Road meshes built: " + usedRoads + " roads, " + segments + " segments, " + meshes.Count + " chunk meshes.");
    }

    // Adds one road as a strip of quads. Returns the number of quads.
    int AddRoad(List<Vector2> p, float half, float tileLength, float roadLift,
                Dictionary<Vector2Int, MeshData> meshes, float cs)
    {
        int n = p.Count;
        if (n < 2) return 0;

        var L = new Vector3[n]; var R = new Vector3[n];
        var NL = new Vector3[n]; var NR = new Vector3[n];
        var ok = new bool[n];
        var vv = new float[n];
        float dist = 0f;

        for (int i = 0; i < n; i++)
        {
            if (i > 0) dist += Vector2.Distance(p[i], p[i - 1]);
            vv[i] = dist / tileLength;

            // Direction of the road before and after this point; the strip follows the average
            Vector2 t0 = Dir(i > 0 ? p[i - 1] : p[i], i > 0 ? p[i] : p[i + 1]);
            Vector2 t1 = Dir(i < n - 1 ? p[i] : p[i - 1], i < n - 1 ? p[i + 1] : p[i]);
            Vector2 n0 = new Vector2(-t0.y, t0.x), n1 = new Vector2(-t1.y, t1.x);
            Vector2 nn = n0 + n1;
            nn = nn.sqrMagnitude < 1e-6f ? n0 : nn.normalized;
            float miter = 1f / Mathf.Max(0.62f, Vector2.Dot(nn, n0));
            Vector2 off = nn * half * miter;

            Vector2 a = p[i] + off, b = p[i] - off;
            float ha, hb; Vector3 na, nb;
            bool oka = Sample(a.x, a.y, out ha, out na);
            bool okb = Sample(b.x, b.y, out hb, out nb);
            ok[i] = oka && okb;
            L[i] = new Vector3(a.x, ha + roadLift, a.y);
            R[i] = new Vector3(b.x, hb + roadLift, b.y);
            NL[i] = na;
            NR[i] = nb;
        }

        int count = 0;
        for (int i = 0; i < n - 1; i++)
        {
            if (!ok[i] || !ok[i + 1]) continue;
            if (vv[i + 1] - vv[i] < 0.0001f) continue;

            Vector3 mid = (L[i] + R[i] + L[i + 1] + R[i + 1]) * 0.25f;
            var key = new Vector2Int(Mathf.FloorToInt(mid.x / cs), Mathf.FloorToInt(mid.z / cs));
            MeshData md;
            if (!meshes.TryGetValue(key, out md)) { md = new MeshData(); meshes[key] = md; }

            Vector3 origin = new Vector3(key.x * cs, 0f, key.y * cs);
            int baseIndex = md.verts.Count;
            md.verts.Add(L[i] - origin);
            md.verts.Add(R[i] - origin);
            md.verts.Add(L[i + 1] - origin);
            md.verts.Add(R[i + 1] - origin);
            md.normals.Add(NL[i]);
            md.normals.Add(NR[i]);
            md.normals.Add(NL[i + 1]);
            md.normals.Add(NR[i + 1]);
            md.uvs.Add(Uv(0f, vv[i]));
            md.uvs.Add(Uv(1f, vv[i]));
            md.uvs.Add(Uv(0f, vv[i + 1]));
            md.uvs.Add(Uv(1f, vv[i + 1]));

            // Clockwise seen from above = front face in Unity
            md.tris.Add(baseIndex); md.tris.Add(baseIndex + 2); md.tris.Add(baseIndex + 1);
            md.tris.Add(baseIndex + 1); md.tris.Add(baseIndex + 2); md.tris.Add(baseIndex + 3);
            count++;
        }
        return count;
    }

    Vector2 Uv(float across, float along)
    {
        return swapAxes ? new Vector2(along, across) : new Vector2(across, along);
    }

    // ---------------------------------------------------------------- helpers

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
            if (t.name.StartsWith("Roads_")) DestroyImmediate(t.gameObject);
        }
    }

    static Vector2 Dir(Vector2 a, Vector2 b)
    {
        Vector2 d = b - a;
        return d.sqrMagnitude < 1e-8f ? Vector2.up : d.normalized;
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
}

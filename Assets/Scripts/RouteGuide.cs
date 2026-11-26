using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Draws a route line along the real roads from the car to the mission drop-off,
/// in the world and on the minimap. Put on the "Systems" object and drag osm_world.json into "Osm Json".
/// How it works: at start the OSM roads become a graph (junctions = shared points), then A* finds the
/// shortest way over the roads. It is recalculated every second or so, so a wrong turn gives a new route.
/// T = show / hide the line. One-way streets and turn rules are ignored for now.
/// </summary>
public class RouteGuide : MonoBehaviour
{
    public static RouteGuide Instance { get; private set; }

    [Tooltip("Drag Assets/Game/RawData/SidiBouSaid/osm_world.json here.")]
    [SerializeField] TextAsset osmJson;
    [Tooltip("Leave empty: the car is found automatically.")]
    [SerializeField] Transform player;
    [SerializeField] float refreshSeconds = 1.5f;

    [Header("Line in the world")]
    [SerializeField] Color color = new Color(0.2f, 0.9f, 1f, 0.9f);
    [SerializeField] float width = 2.2f;
    [SerializeField] float lift = 0.35f;            // metres above the ground

    [Header("Line on the minimap")]
    [SerializeField] float mapWidth = 7f;
    [SerializeField] float mapHeight = 250f;        // must be below the minimap camera and above the ground

    /// <summary>Length of the current route in metres (0 = no route).</summary>
    public float RouteLength { get; private set; }

    struct Edge { public int to; public float cost; }

    const float Cell = 64f;
    const float MaxSegment = 15f;                   // long road pieces are cut so the car can snap anywhere
    const float HeuristicScale = 0.85f;             // lowest road cost factor, keeps A* correct

    List<Vector2> nodePos;
    List<List<Edge>> adj;
    Dictionary<long, int> keyToNode;
    Dictionary<long, List<int>> grid;
    bool[] inMain;

    float[] g;
    int[] came, stamp, done;
    int searchId;
    readonly MinHeap heap = new MinHeap();

    LineRenderer worldLine, mapLine;
    Material mat;
    Transform lastDest;
    float nextRefresh;
    bool graphReady, show = true;
    readonly List<Vector2> pts = new List<Vector2>();

    void Awake() => Instance = this;

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
        if (mat != null) Destroy(mat);
    }

    void Start()
    {
        if (player == null)
        {
            var c = PlayerCar.Find();
            if (c != null) player = c.transform;
        }
        if (osmJson == null || player == null)
        {
            Debug.LogWarning("RouteGuide: drag osm_world.json into 'Osm Json' (and make sure the Car exists).");
            enabled = false;
            return;
        }

        var data = JsonUtility.FromJson<OsmWorldData>(osmJson.text);
        if (data == null || data.roads == null) { Debug.LogWarning("RouteGuide: the JSON has no roads."); enabled = false; return; }
        BuildGraph(data);

        Shader sh = Shader.Find("Sprites/Default");
        if (sh == null) sh = Shader.Find("Universal Render Pipeline/Unlit");
        mat = new Material(sh);

        int mapLayer = Mathf.Max(0, LayerMask.NameToLayer("Minimap"));
        worldLine = MakeLine("RouteLine_World", 0, width);
        mapLine = MakeLine("RouteLine_Minimap", mapLayer, mapWidth);
        graphReady = true;
    }

    LineRenderer MakeLine(string name, int layer, float lineWidth)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        go.transform.rotation = Quaternion.Euler(90f, 0f, 0f);   // the ribbon lies flat, facing up
        go.layer = layer;
        var lr = go.AddComponent<LineRenderer>();
        lr.useWorldSpace = true;
        lr.alignment = LineAlignment.TransformZ;
        lr.widthMultiplier = lineWidth;
        lr.numCornerVertices = 3;
        lr.numCapVertices = 3;
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.receiveShadows = false;
        lr.sharedMaterial = mat;
        lr.startColor = color;
        lr.endColor = color;
        lr.positionCount = 0;
        go.SetActive(false);
        return lr;
    }

    // ---------------------------------------------------------------- update

    void Update()
    {
        var k = Keyboard.current;
        if (k != null && k.tKey.wasPressedThisFrame) show = !show;
        if (!graphReady) return;

        var m = MissionManager.Instance;
        bool active = show && m != null && m.State == MissionState.Active && m.Destination != null;
        worldLine.gameObject.SetActive(active);
        mapLine.gameObject.SetActive(active);
        if (!active) { lastDest = null; RouteLength = 0f; return; }
        if (GameState.Paused) return;

        if (m.Destination != lastDest || Time.time >= nextRefresh)
        {
            lastDest = m.Destination;
            nextRefresh = Time.time + refreshSeconds;
            Recalculate(m.Destination.position);
        }
    }

    void Recalculate(Vector3 dest)
    {
        Vector2 from = new Vector2(player.position.x, player.position.z);
        Vector2 to = new Vector2(dest.x, dest.z);

        var path = new List<Vector2> { from };
        int a = Nearest(from), b = Nearest(to);
        if (a >= 0 && b >= 0) FindPath(a, b, path);       // adds the road points between
        path.Add(to);
        Draw(path);
    }

    void Draw(List<Vector2> path)
    {
        // Cut into pieces of about 4 m so the line follows the shape of the terrain
        pts.Clear();
        float total = 0f;
        for (int i = 0; i < path.Count - 1; i++)
        {
            Vector2 a = path[i], b = path[i + 1];
            float len = Vector2.Distance(a, b);
            total += len;
            int steps = Mathf.Max(1, Mathf.CeilToInt(len / 4f));
            for (int s = 0; s < steps; s++) pts.Add(Vector2.Lerp(a, b, s / (float)steps));
        }
        pts.Add(path[path.Count - 1]);
        RouteLength = total;

        var terrains = Terrain.activeTerrains;
        var w = new Vector3[pts.Count];
        var mp = new Vector3[pts.Count];
        for (int i = 0; i < pts.Count; i++)
        {
            float y = GroundY(terrains, pts[i].x, pts[i].y);
            w[i] = new Vector3(pts[i].x, y + lift, pts[i].y);
            mp[i] = new Vector3(pts[i].x, mapHeight, pts[i].y);
        }
        worldLine.positionCount = w.Length;
        worldLine.SetPositions(w);
        mapLine.positionCount = mp.Length;
        mapLine.SetPositions(mp);
    }

    float GroundY(Terrain[] terrains, float x, float z)
    {
        foreach (var t in terrains)
        {
            Vector3 p = t.transform.position;
            Vector3 s = t.terrainData.size;
            if (x >= p.x && x <= p.x + s.x && z >= p.z && z <= p.z + s.z)
                return t.SampleHeight(new Vector3(x, 0f, z)) + p.y;
        }
        return player.position.y;
    }

    // ---------------------------------------------------------------- road API for AI cars

    /// <summary>Road route for AI cars. 'path' becomes: from, road points..., to. False = no road route found.</summary>
    public bool GetRoute(Vector3 from, Vector3 to, List<Vector2> path)
    {
        path.Clear();
        if (!graphReady) return false;
        Vector2 a2 = new Vector2(from.x, from.z), b2 = new Vector2(to.x, to.z);
        path.Add(a2);
        int a = Nearest(a2), b = Nearest(b2);
        bool ok = a >= 0 && b >= 0 && FindPath(a, b, path);
        path.Add(b2);
        return ok;
    }

    /// <summary>The road point closest to a position (keeps the given height).</summary>
    public bool NearestRoadPoint(Vector3 position, out Vector3 point)
    {
        point = position;
        if (!graphReady) return false;
        int i = Nearest(new Vector2(position.x, position.z));
        if (i < 0) return false;
        point = new Vector3(nodePos[i].x, position.y, nodePos[i].y);
        return true;
    }

    // ---------------------------------------------------------------- road graph

    // 0 = not a road for cars. Lower = preferred (big roads are a little cheaper).
    static float Factor(string type)
    {
        switch (type)
        {
            case "motorway": case "motorway_link": case "trunk": case "trunk_link":
            case "primary": case "primary_link": return 0.85f;
            case "secondary": case "secondary_link": case "tertiary": case "tertiary_link": return 0.9f;
            case "unclassified": case "residential": return 1f;
            case "living_street": case "service": return 1.3f;
            case "track": return 1.6f;
        }
        return 0f;
    }

    static long Key(int a, int b) { return ((long)a << 32) | (uint)b; }

    void BuildGraph(OsmWorldData data)
    {
        nodePos = new List<Vector2>();
        adj = new List<List<Edge>>();
        keyToNode = new Dictionary<long, int>();
        grid = new Dictionary<long, List<int>>();

        foreach (var road in data.roads)
        {
            float f = Factor(road.type);
            if (f <= 0f || road.points == null || road.points.Length < 2) continue;

            int prev = Node(road.points[0]);
            for (int i = 1; i < road.points.Length; i++)
            {
                Vector2 a = road.points[i - 1], b = road.points[i];
                int steps = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(a, b) / MaxSegment));
                for (int s = 1; s <= steps; s++)
                {
                    int cur = Node(Vector2.Lerp(a, b, s / (float)steps));
                    if (cur != prev) Link(prev, cur, f);
                    prev = cur;
                }
            }
        }

        // Keep only the biggest connected network, so we never snap onto an isolated bit of road
        int n = nodePos.Count;
        var comp = new int[n];
        for (int i = 0; i < n; i++) comp[i] = -1;
        int bestComp = -1, bestSize = 0, id = 0;
        var stack = new Stack<int>();
        for (int s = 0; s < n; s++)
        {
            if (comp[s] >= 0) continue;
            int size = 0;
            comp[s] = id;
            stack.Push(s);
            while (stack.Count > 0)
            {
                int u = stack.Pop();
                size++;
                foreach (var e in adj[u])
                    if (comp[e.to] < 0) { comp[e.to] = id; stack.Push(e.to); }
            }
            if (size > bestSize) { bestSize = size; bestComp = id; }
            id++;
        }
        inMain = new bool[n];
        for (int i = 0; i < n; i++) inMain[i] = comp[i] == bestComp;

        g = new float[n];
        came = new int[n];
        stamp = new int[n];
        done = new int[n];
        Debug.Log("RouteGuide: road graph with " + n + " points, main network " + bestSize + ".");
    }

    int Node(Vector2 p)
    {
        long key = Key(Mathf.RoundToInt(p.x * 20f), Mathf.RoundToInt(p.y * 20f));   // junctions share the same point
        int id;
        if (keyToNode.TryGetValue(key, out id)) return id;

        id = nodePos.Count;
        nodePos.Add(p);
        adj.Add(new List<Edge>());
        keyToNode[key] = id;

        long gk = Key(Mathf.FloorToInt(p.x / Cell), Mathf.FloorToInt(p.y / Cell));
        List<int> list;
        if (!grid.TryGetValue(gk, out list)) { list = new List<int>(); grid[gk] = list; }
        list.Add(id);
        return id;
    }

    void Link(int a, int b, float factor)
    {
        float c = Vector2.Distance(nodePos[a], nodePos[b]) * factor;
        adj[a].Add(new Edge { to = b, cost = c });
        adj[b].Add(new Edge { to = a, cost = c });
    }

    // Nearest road point (in the main network) to a world position, or -1
    int Nearest(Vector2 p)
    {
        int cx = Mathf.FloorToInt(p.x / Cell), cz = Mathf.FloorToInt(p.y / Cell);
        int best = -1;
        float bestD = float.MaxValue;
        for (int r = 0; r <= 40; r++)
        {
            for (int dz = -r; dz <= r; dz++)
                for (int dx = -r; dx <= r; dx++)
                {
                    if (Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dz)) != r) continue;   // only the new outer ring
                    List<int> list;
                    if (!grid.TryGetValue(Key(cx + dx, cz + dz), out list)) continue;
                    foreach (int i in list)
                    {
                        if (!inMain[i]) continue;
                        float d = Vector2.Distance(nodePos[i], p);
                        if (d < bestD) { bestD = d; best = i; }
                    }
                }
            if (best >= 0 && bestD <= r * Cell) break;     // nothing in the next ring can be closer
        }
        return best;
    }

    // ---------------------------------------------------------------- A*

    // Appends the road points from start to goal to 'result'. Returns false if there is no way.
    bool FindPath(int start, int goal, List<Vector2> result)
    {
        searchId++;
        heap.Clear();
        Vector2 goalPos = nodePos[goal];

        g[start] = 0f;
        came[start] = -1;
        stamp[start] = searchId;
        heap.Push(start, Vector2.Distance(nodePos[start], goalPos) * HeuristicScale);

        bool found = false;
        while (heap.Count > 0)
        {
            int u = heap.Pop();
            if (done[u] == searchId) continue;
            done[u] = searchId;
            if (u == goal) { found = true; break; }

            foreach (var e in adj[u])
            {
                if (done[e.to] == searchId) continue;
                float ng = g[u] + e.cost;
                if (stamp[e.to] != searchId || ng < g[e.to])
                {
                    stamp[e.to] = searchId;
                    g[e.to] = ng;
                    came[e.to] = u;
                    heap.Push(e.to, ng + Vector2.Distance(nodePos[e.to], goalPos) * HeuristicScale);
                }
            }
        }
        if (!found) return false;

        int insertAt = result.Count;
        for (int n = goal; n >= 0; n = came[n]) result.Insert(insertAt, nodePos[n]);   // walk back, keep the order start..goal
        return true;
    }

    // Small binary heap: the node with the lowest key comes out first
    class MinHeap
    {
        int[] nodes = new int[256];
        float[] keys = new float[256];
        public int Count;

        public void Clear() { Count = 0; }

        public void Push(int node, float key)
        {
            if (Count == nodes.Length)
            {
                System.Array.Resize(ref nodes, Count * 2);
                System.Array.Resize(ref keys, Count * 2);
            }
            int i = Count++;
            while (i > 0)
            {
                int p = (i - 1) / 2;
                if (keys[p] <= key) break;
                nodes[i] = nodes[p]; keys[i] = keys[p];
                i = p;
            }
            nodes[i] = node; keys[i] = key;
        }

        public int Pop()
        {
            int top = nodes[0];
            Count--;
            if (Count > 0)
            {
                int node = nodes[Count];
                float key = keys[Count];
                int i = 0;
                while (true)
                {
                    int c = 2 * i + 1;
                    if (c >= Count) break;
                    if (c + 1 < Count && keys[c + 1] < keys[c]) c++;
                    if (keys[c] >= key) break;
                    nodes[i] = nodes[c]; keys[i] = keys[c];
                    i = c;
                }
                nodes[i] = node; keys[i] = key;
            }
            return top;
        }
    }
}

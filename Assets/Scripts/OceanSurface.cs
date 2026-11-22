using UnityEngine;
using UnityEngine.Rendering;

// A big water mesh that follows the camera. Its UVs are calculated from WORLD position,
// so waves, foam and colour keep their designed scale and speed no matter how large the sea is.
[ExecuteAlways]
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class OceanSurface : MonoBehaviour
{
    [Header("Shape")]
    [SerializeField] float size = 8000f;        // meters, edge to edge
    [SerializeField] int resolution = 129;      // vertices per side
    [SerializeField] float centerSpacing = 4f;  // meters between vertices right under the camera
    [SerializeField] float seaLevel = 0f;
    [SerializeField] float snap = 4f;           // the mesh moves in steps of this many meters

    [Header("Water texture scale")]
    [SerializeField] float uvMeters = 40f;      // meters covered by one 0..1 UV tile (smaller = smaller, busier waves)
    [SerializeField] Mesh sourceMesh;           // optional: drag SM_WaterGrid here, then use "Set UV meters from source mesh"

    [Header("Follow")]
    [SerializeField] Transform target;          // empty = main camera

    Mesh mesh;
    Vector3[] vertices;
    Vector2[] uvs;
    Vector2 lastCenter = new Vector2(float.NaN, float.NaN);

    void OnEnable() { Build(); }

    void OnDisable()
    {
        if (mesh != null)
        {
            if (Application.isPlaying) Destroy(mesh); else DestroyImmediate(mesh);
            mesh = null;
        }
    }

    void OnValidate() { lastCenter = new Vector2(float.NaN, float.NaN); }

    [ContextMenu("Rebuild mesh")]
    void Rebuild() { OnDisable(); Build(); }

    [ContextMenu("Set UV meters from source mesh")]
    void UseSourceMeshSize()
    {
        if (sourceMesh == null) { Debug.LogWarning("Drag SM_WaterGrid into 'Source Mesh' first."); return; }
        uvMeters = sourceMesh.bounds.size.x;
        lastCenter = new Vector2(float.NaN, float.NaN);
        Debug.Log("UV meters set to " + uvMeters);
    }

    void Build()
    {
        transform.rotation = Quaternion.identity;
        transform.localScale = Vector3.one;

        int n = resolution | 1;                                   // odd number: a vertex sits at the center
        float half = size * 0.5f;
        float a = Mathf.Clamp(centerSpacing * (n - 1) / (2f * half), 0.001f, 1f);

        var axis = new float[n];                                  // fine spacing near the center, coarse far away
        for (int k = 0; k < n; k++)
        {
            float t = k / (float)(n - 1) * 2f - 1f;
            axis[k] = half * (a * t + (1f - a) * t * t * t);
        }

        vertices = new Vector3[n * n];
        uvs = new Vector2[n * n];
        var normals = new Vector3[n * n];
        var tangents = new Vector4[n * n];
        for (int z = 0; z < n; z++)
            for (int x = 0; x < n; x++)
            {
                int i = z * n + x;
                vertices[i] = new Vector3(axis[x], 0f, axis[z]);
                normals[i] = Vector3.up;
                tangents[i] = new Vector4(1f, 0f, 0f, -1f);
            }

        var tris = new int[(n - 1) * (n - 1) * 6];
        int w = 0;
        for (int z = 0; z < n - 1; z++)
            for (int x = 0; x < n - 1; x++)
            {
                int i00 = z * n + x, i10 = i00 + 1, i01 = i00 + n, i11 = i01 + 1;
                tris[w++] = i00; tris[w++] = i01; tris[w++] = i11;
                tris[w++] = i00; tris[w++] = i11; tris[w++] = i10;
            }

        mesh = new Mesh { name = "OceanSurface", indexFormat = IndexFormat.UInt32, hideFlags = HideFlags.HideAndDontSave };
        mesh.vertices = vertices;
        mesh.normals = normals;
        mesh.tangents = tangents;
        mesh.triangles = tris;
        mesh.bounds = new Bounds(Vector3.zero, new Vector3(size, 200f, size));
        mesh.MarkDynamic();
        GetComponent<MeshFilter>().sharedMesh = mesh;

        lastCenter = new Vector2(float.NaN, float.NaN);
    }

    void LateUpdate()
    {
        if (mesh == null) Build();

        Transform cam = target != null ? target : (Camera.main != null ? Camera.main.transform : null);
        if (cam == null) return;

        float s = Mathf.Max(0.1f, snap);
        var center = new Vector2(Mathf.Round(cam.position.x / s) * s, Mathf.Round(cam.position.z / s) * s);
        if (center == lastCenter) return;
        lastCenter = center;

        transform.position = new Vector3(center.x, seaLevel, center.y);

        float inv = 1f / Mathf.Max(0.01f, uvMeters);
        for (int i = 0; i < vertices.Length; i++)
            uvs[i] = new Vector2((center.x + vertices[i].x) * inv, (center.y + vertices[i].z) * inv);
        mesh.uv = uvs;
    }
}
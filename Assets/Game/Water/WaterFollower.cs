using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// Keeps a sea around the car made of nested rings: fine triangles near the car, coarser
// ones farther away, and one huge flat ring out to the horizon. Wave displacement needs
// small triangles, so one stretched mesh flickers. The rings snap to a fixed world grid,
// so their vertices never swim while you drive.
//
// Setup: create an empty GameObject at position 0,0,0 with scale 1, add this component,
// and drag your water material (M_Water_Realistic_Plane) into "Water Material".
// Leave "Target" empty to follow the object that has CarController.
[ExecuteAlways]
public class WaterFollower : MonoBehaviour
{
    public Material waterMaterial;
    public Transform target;          // empty = find the Car automatically
    public float waterHeight = 0f;    // sea level in world meters

    // Ring size and vertex spacing in meters. Each size must be a multiple of 2 x spacing,
    // and each spacing must divide the next one, so everything stays on one world grid.
    static readonly float[] Sizes = { 256f, 1024f, 4096f };
    static readonly float[] Spacings = { 1f, 4f, 16f };
    const float FarSize = 61440f;
    const float FarSpacing = 2048f;
    const float Snap = 16f;           // the largest spacing
    const string LevelName = "WaterLevel";
    const string FarName = "WaterFar";

    readonly List<Transform> parts = new List<Transform>();
    Transform autoTarget;

    void OnEnable() { Build(); }
    void OnDisable() { Clear(); }
    void LateUpdate() { Follow(); }

    void Build()
    {
        Clear();
        for (int i = 0; i < Sizes.Length; i++)
            parts.Add(Create(LevelName + i, BuildGrid(Sizes[i], Spacings[i], i > 0 ? Sizes[i - 1] : 0f)));
        parts.Add(Create(FarName, BuildGrid(FarSize, FarSpacing, Sizes[Sizes.Length - 1])));
        Follow();
    }

    void Follow()
    {
        Vector3 center = transform.position;
        Transform t = target;
        if (t == null)
        {
            if (autoTarget == null)
            {
                var car = FindFirstObjectByType<CarController>();
                if (car != null) autoTarget = car.transform;
            }
            t = autoTarget;
        }
        if (t != null) center = t.position;

        var pos = new Vector3(Mathf.Round(center.x / Snap) * Snap, waterHeight, Mathf.Round(center.z / Snap) * Snap);
        foreach (Transform p in parts)
        {
            if (p == null) continue;
            p.position = pos;
            var mr = p.GetComponent<MeshRenderer>();
            if (mr != null && mr.sharedMaterial != waterMaterial) mr.sharedMaterial = waterMaterial;
        }
    }

    Transform Create(string objectName, Mesh mesh)
    {
        var go = new GameObject(objectName);
        go.hideFlags = HideFlags.DontSave;   // rebuilt on load, never saved into the scene
        go.transform.SetParent(transform, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = waterMaterial;
        mr.shadowCastingMode = ShadowCastingMode.Off;
        mr.receiveShadows = false;
        return go.transform;
    }

    void Clear()
    {
        parts.Clear();
        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            Transform c = transform.GetChild(i);
            if (!c.name.StartsWith(LevelName) && c.name != FarName) continue;
            var mf = c.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null) SafeDestroy(mf.sharedMesh);
            SafeDestroy(c.gameObject);
        }
    }

    static void SafeDestroy(Object o)
    {
        if (Application.isPlaying) Destroy(o);
        else DestroyImmediate(o);
    }

    // A flat square grid centered on 0,0. Cells inside the central "hole" are left out,
    // because the next finer ring covers that area.
    static Mesh BuildGrid(float size, float spacing, float hole)
    {
        int cells = Mathf.RoundToInt(size / spacing);
        int side = cells + 1;
        float half = size * 0.5f;

        var verts = new Vector3[side * side];
        var uvs = new Vector2[side * side];
        var normals = new Vector3[side * side];
        var tangents = new Vector4[side * side];
        for (int z = 0; z < side; z++)
            for (int x = 0; x < side; x++)
            {
                int i = z * side + x;
                verts[i] = new Vector3(-half + x * spacing, 0f, -half + z * spacing);
                uvs[i] = new Vector2(x / (float)cells, z / (float)cells);
                normals[i] = Vector3.up;
                tangents[i] = new Vector4(1f, 0f, 0f, 1f);
            }

        var tris = new List<int>(cells * cells * 6);
        float halfHole = hole * 0.5f;
        for (int z = 0; z < cells; z++)
            for (int x = 0; x < cells; x++)
            {
                float cx = -half + (x + 0.5f) * spacing;
                float cz = -half + (z + 0.5f) * spacing;
                if (hole > 0f && Mathf.Abs(cx) < halfHole && Mathf.Abs(cz) < halfHole) continue;

                int i00 = z * side + x, i10 = i00 + 1, i01 = i00 + side, i11 = i01 + 1;
                tris.Add(i00); tris.Add(i01); tris.Add(i10);   // clockwise seen from above
                tris.Add(i10); tris.Add(i01); tris.Add(i11);
            }

        var mesh = new Mesh();
        mesh.name = "WaterGrid_" + size;
        mesh.hideFlags = HideFlags.DontSave;
        mesh.indexFormat = IndexFormat.UInt32;
        mesh.vertices = verts;
        mesh.normals = normals;
        mesh.tangents = tangents;
        mesh.uv = uvs;
        mesh.SetTriangles(tris, 0);
        mesh.bounds = new Bounds(Vector3.zero, new Vector3(size, 50f, size)); // room for the waves
        return mesh;
    }
}

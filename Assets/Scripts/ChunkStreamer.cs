using System.Collections.Generic;
using UnityEngine;

// Hides every direct child of the "roots" that is farther than hideDistance from the player,
// and shows it again when the player comes back within showDistance.
public class ChunkStreamer : MonoBehaviour
{
    public Transform player;          // drag the Car here
    public Transform[] roots;         // parents whose children are streamed (see steps)
    public float showDistance = 700f;
    public float hideDistance = 800f; // keep bigger than showDistance so nothing flickers
    public float checkInterval = 0.5f;

    class Item { public GameObject go; public Bounds bounds; }
    readonly List<Item> items = new List<Item>();
    float timer;

    void Start()
    {
        foreach (Transform root in roots)
        {
            if (root == null) continue;
            foreach (Transform child in root)
            {
                Bounds b;
                if (!GetBounds(child, out b)) continue;
                items.Add(new Item { go = child.gameObject, bounds = b });
            }
        }
        Debug.Log("ChunkStreamer is managing " + items.Count + " objects.");
    }

    static bool GetBounds(Transform t, out Bounds b)
    {
        b = new Bounds();
        bool has = false;

        var terrain = t.GetComponent<Terrain>();
        if (terrain != null)
        {
            Vector3 size = terrain.terrainData.size;
            b = new Bounds(t.position + size * 0.5f, size);
            return true;
        }

        foreach (var r in t.GetComponentsInChildren<Renderer>(true))
        {
            if (!has) { b = r.bounds; has = true; }
            else b.Encapsulate(r.bounds);
        }
        return has;
    }

    void Update()
    {
        timer -= Time.deltaTime;
        if (timer > 0f || player == null) return;
        timer = checkInterval;

        Vector3 p = player.position;
        foreach (var it in items)
        {
            float d = Mathf.Sqrt(it.bounds.SqrDistance(p));
            bool active = it.go.activeSelf;
            if (active && d > hideDistance) it.go.SetActive(false);
            else if (!active && d < showDistance) it.go.SetActive(true);
        }
    }
}
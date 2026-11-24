using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

// Press F1-F7 in Play mode to switch features off and on, and watch the frame time.
public class PerfToggles : MonoBehaviour
{
    bool trees = true, water = true, post = true, shadows = true, buildings = true, roads = true, terrainOn = true;
    float smooth;
    ChunkStreamer streamer;
    Light sun;
    LightShadows sunShadows;
    GUIStyle style;

    void Start()
    {
        streamer = FindAnyObjectByType<ChunkStreamer>();
        sun = RenderSettings.sun;
        if (sun == null)
            foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None))
                if (l.type == LightType.Directional) { sun = l; break; }
        if (sun != null) sunShadows = sun.shadows;
    }

    void Update()
    {
        smooth = Mathf.Lerp(smooth, Time.unscaledDeltaTime, 0.05f);
        var k = Keyboard.current;
        if (k == null) return;

        if (k.f1Key.wasPressedThisFrame)
        {
            trees = !trees;
            foreach (var t in Terrain.activeTerrains) t.drawTreesAndFoliage = trees;
            SetWorldChild("Trees", trees);
        }
        if (k.f2Key.wasPressedThisFrame)
        {
            water = !water;
            foreach (var o in FindObjectsByType<OceanSurface>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                o.gameObject.SetActive(water);
        }
        if (k.f3Key.wasPressedThisFrame)
        {
            post = !post;
            foreach (var v in FindObjectsByType<Volume>(FindObjectsSortMode.None)) v.enabled = post;
        }
        if (k.f4Key.wasPressedThisFrame)
        {
            shadows = !shadows;
            if (sun != null) sun.shadows = shadows ? sunShadows : LightShadows.None;
        }
        if (k.f5Key.wasPressedThisFrame) { buildings = !buildings; SetByPrefix("Buildings_", buildings); }
        if (k.f6Key.wasPressedThisFrame) { roads = !roads; SetByPrefix("Roads_", roads); }
        if (k.f7Key.wasPressedThisFrame)
        {
            terrainOn = !terrainOn;
            foreach (var t in Terrain.activeTerrains) t.drawHeightmap = terrainOn;
        }

        // The streamer would switch the objects back on, so pause it while something is off
        if (streamer != null) streamer.enabled = trees && buildings && roads;
    }

    static void SetByPrefix(string prefix, bool on)
    {
        GameObject world = GameObject.Find("World");
        if (world == null) return;
        foreach (Transform t in world.transform)
            if (t.name.StartsWith(prefix)) t.gameObject.SetActive(on);
    }

    static void SetWorldChild(string name, bool on)
    {
        GameObject world = GameObject.Find("World");
        if (world == null) return;
        Transform t = world.transform.Find(name);
        if (t != null) t.gameObject.SetActive(on);
    }

    void OnGUI()
    {
        if (style == null)
        {
            style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperRight, fontSize = 14 };
            style.normal.textColor = Color.white;
        }
        string text = (smooth * 1000f).ToString("F1") + " ms  (" + (1f / Mathf.Max(0.0001f, smooth)).ToString("F0") + " fps)\n"
                    + "F1 trees " + trees + "   F2 water " + water + "   F3 post " + post + "   F4 shadows " + shadows + "\n"
                    + "F5 buildings " + buildings + "   F6 roads " + roads + "   F7 terrain " + terrainOn;
        GUI.Label(new Rect(Screen.width - 560, 8, 550, 80), text, style);
    }
}
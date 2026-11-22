using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using UnityEditor;
using UnityEngine;

// Editor tool: reads an OSM XML export (map.osm), converts lat/lon to game meters
// and saves roads, buildings and the coastline as one JSON file.
public class OsmImporter : EditorWindow
{
    WorldConfig config;
    string osmPath = "Assets/Game/RawData/SidiBouSaid/map.osm";
    string outPath = "Assets/Game/RawData/SidiBouSaid/osm_world.json";

    [MenuItem("RacerTN/OSM Importer")]
    static void Open() { GetWindow<OsmImporter>("OSM Importer"); }

    void OnGUI()
    {
        EditorGUILayout.Space();
        config = (WorldConfig)EditorGUILayout.ObjectField("World Config", config, typeof(WorldConfig), false);
        osmPath = EditorGUILayout.TextField("OSM file", osmPath);
        outPath = EditorGUILayout.TextField("Output JSON", outPath);

        if (config == null)
        {
            EditorGUILayout.HelpBox("Drag your WorldConfig asset into the field first.", MessageType.Info);
            return;
        }
        if (GUILayout.Button("Import OSM", GUILayout.Height(28))) Run();
    }

    void Run()
    {
        try { Import(); }
        catch (Exception e) { Debug.LogError("OSM import failed: " + e); }
        finally { EditorUtility.ClearProgressBar(); }
    }

    void Import()
    {
        string full = Path.GetFullPath(osmPath);
        if (!File.Exists(full)) { Debug.LogError("File not found: " + full); return; }

        EditorUtility.DisplayProgressBar("OSM Importer", "Reading file...", 0.2f);
        XElement root = XDocument.Load(full).Root;

        var nodes = new Dictionary<long, Vector2>();
        foreach (var n in root.Elements("node"))
        {
            double lat = double.Parse((string)n.Attribute("lat"), CultureInfo.InvariantCulture);
            double lon = double.Parse((string)n.Attribute("lon"), CultureInfo.InvariantCulture);
            nodes[long.Parse((string)n.Attribute("id"))] = GeoConverter.LatLonToGame(lat, lon, config);
        }

        var roads = new List<OsmRoad>();
        var buildings = new List<OsmBuilding>();
        var coast = new List<OsmLine>();

        EditorUtility.DisplayProgressBar("OSM Importer", "Reading ways...", 0.6f);
        foreach (var w in root.Elements("way"))
        {
            var tags = new Dictionary<string, string>();
            foreach (var t in w.Elements("tag"))
                tags[(string)t.Attribute("k")] = (string)t.Attribute("v");

            var pts = new List<Vector2>();
            foreach (var nd in w.Elements("nd"))
            {
                long id = long.Parse((string)nd.Attribute("ref"));
                Vector2 p;
                if (nodes.TryGetValue(id, out p)) pts.Add(p);
            }
            if (pts.Count < 2) continue;

            string value;
            if (tags.TryGetValue("highway", out value))
            {
                string roadName;
                tags.TryGetValue("name", out roadName);
                roads.Add(new OsmRoad
                {
                    type = value,
                    name = roadName ?? "",
                    lanes = ParseInt(tags, "lanes"),
                    bridge = IsSet(tags, "bridge"),
                    tunnel = IsSet(tags, "tunnel"),
                    points = pts.ToArray()
                });
            }
            else if (tags.TryGetValue("building", out value))
            {
                if (pts.Count > 3 && pts[0] == pts[pts.Count - 1]) pts.RemoveAt(pts.Count - 1);
                if (pts.Count < 3) continue;
                buildings.Add(new OsmBuilding
                {
                    type = value,
                    levels = ParseInt(tags, "building:levels"),
                    height = ParseFloat(tags, "height"),
                    footprint = pts.ToArray()
                });
            }
            else if (tags.TryGetValue("natural", out value) && value == "coastline")
            {
                coast.Add(new OsmLine { points = pts.ToArray() });
            }
        }

        var data = new OsmWorldData
        {
            roads = roads.ToArray(),
            buildings = buildings.ToArray(),
            coastline = coast.ToArray()
        };

        string outFull = Path.GetFullPath(outPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outFull));
        File.WriteAllText(outFull, JsonUtility.ToJson(data, false));
        AssetDatabase.Refresh();

        // Report, so you can check that everything landed where it should
        float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
        float roadMeters = 0f;
        foreach (var r in roads)
            for (int i = 0; i < r.points.Length; i++)
            {
                Vector2 p = r.points[i];
                minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x);
                minZ = Mathf.Min(minZ, p.y); maxZ = Mathf.Max(maxZ, p.y);
                if (i > 0) roadMeters += Vector2.Distance(p, r.points[i - 1]);
            }

        string types = string.Join(", ", roads.GroupBy(r => r.type)
            .OrderByDescending(g => g.Count()).Take(8).Select(g => g.Key + " " + g.Count()));

        Debug.Log("OSM import done: " + roads.Count + " roads (" + (roadMeters / 1000f).ToString("F1") + " km), "
                  + buildings.Count + " buildings, " + coast.Count + " coastline pieces.\n"
                  + "Road types: " + types + "\n"
                  + "Road extent in game meters: X " + minX.ToString("F0") + " to " + maxX.ToString("F0")
                  + ", Z " + minZ.ToString("F0") + " to " + maxZ.ToString("F0") + "\nSaved: " + outFull);
    }

    static bool IsSet(Dictionary<string, string> tags, string key)
    {
        string v;
        return tags.TryGetValue(key, out v) && v != "no";
    }

    static int ParseInt(Dictionary<string, string> tags, string key)
    {
        string v; int result;
        return tags.TryGetValue(key, out v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out result) ? result : 0;
    }

    static float ParseFloat(Dictionary<string, string> tags, string key)
    {
        string v; float result;
        if (!tags.TryGetValue(key, out v)) return 0f;
        v = v.Replace("m", "").Trim();
        return float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out result) ? result : 0f;
    }
}
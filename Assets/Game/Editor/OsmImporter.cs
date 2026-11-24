using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using UnityEditor;
using UnityEngine;

// Editor tool: reads an OSM XML export (map.osm), converts lat/lon to game meters
// and saves roads, buildings, the coastline and ground-cover areas as one JSON file.
public class OsmImporter : EditorWindow
{
    [SerializeField] WorldConfig config;
    [SerializeField] string osmPath = "Assets/Game/RawData/SidiBouSaid/map.osm";
    [SerializeField] string outPath = "Assets/Game/RawData/SidiBouSaid/osm_world.json";

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
        var areas = new List<OsmArea>();
        var wayNodeIds = new Dictionary<long, List<long>>();   // remembered for multipolygon relations

        EditorUtility.DisplayProgressBar("OSM Importer", "Reading ways...", 0.5f);
        foreach (var w in root.Elements("way"))
        {
            var tags = ReadTags(w);

            var pts = new List<Vector2>();
            var ids = new List<long>();
            foreach (var nd in w.Elements("nd"))
            {
                long id = long.Parse((string)nd.Attribute("ref"));
                Vector2 p;
                if (nodes.TryGetValue(id, out p)) { pts.Add(p); ids.Add(id); }
            }
            wayNodeIds[long.Parse((string)w.Attribute("id"))] = ids;
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
            else
            {
                // Ground-cover area: only closed outlines count
                string kind = AreaKind(tags);
                if (kind != null && pts.Count >= 4 && pts[0] == pts[pts.Count - 1])
                {
                    pts.RemoveAt(pts.Count - 1);
                    areas.Add(new OsmArea { kind = kind, points = pts.ToArray() });
                }
            }
        }

        // Multipolygon relations: big forests, parks, beaches are often stored this way.
        // We join the "outer" ways into closed rings. Inner holes are ignored for now.
        EditorUtility.DisplayProgressBar("OSM Importer", "Reading relations...", 0.8f);
        foreach (var rel in root.Elements("relation"))
        {
            var tags = ReadTags(rel);
            string type;
            if (!tags.TryGetValue("type", out type) || type != "multipolygon") continue;
            string kind = AreaKind(tags);
            if (kind == null) continue;

            var segments = new List<List<long>>();
            foreach (var m in rel.Elements("member"))
            {
                if ((string)m.Attribute("type") != "way") continue;
                if ((string)m.Attribute("role") == "inner") continue;
                List<long> ids;
                if (wayNodeIds.TryGetValue(long.Parse((string)m.Attribute("ref")), out ids) && ids.Count >= 2)
                    segments.Add(new List<long>(ids));
            }

            foreach (var ring in JoinRings(segments))
            {
                var pts = new List<Vector2>();
                for (int i = 0; i < ring.Count - 1; i++) pts.Add(nodes[ring[i]]);   // last id repeats the first
                if (pts.Count >= 3) areas.Add(new OsmArea { kind = kind, points = pts.ToArray() });
            }
        }

        var data = new OsmWorldData
        {
            roads = roads.ToArray(),
            buildings = buildings.ToArray(),
            coastline = coast.ToArray(),
            areas = areas.ToArray()
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
        string areaKinds = string.Join(", ", areas.GroupBy(a => a.kind)
            .OrderByDescending(g => g.Count()).Select(g => g.Key + " " + g.Count()));

        Debug.Log("OSM import done: " + roads.Count + " roads (" + (roadMeters / 1000f).ToString("F1") + " km), "
                  + buildings.Count + " buildings, " + coast.Count + " coastline pieces, " + areas.Count + " ground areas.\n"
                  + "Road types: " + types + "\n"
                  + "Ground areas: " + areaKinds + "\n"
                  + "Road extent in game meters: X " + minX.ToString("F0") + " to " + maxX.ToString("F0")
                  + ", Z " + minZ.ToString("F0") + " to " + maxZ.ToString("F0") + "\nSaved: " + outFull);
    }

    // ---------------------------------------------------------------- ground-cover kinds

    // Turns OSM tags into one of our simple ground kinds, or null if it is not a ground area.
    static string AreaKind(Dictionary<string, string> tags)
    {
        string v;
        if (tags.TryGetValue("natural", out v))
        {
            if (v == "wood") return "forest";
            if (v == "scrub" || v == "heath") return "scrub";
            if (v == "beach" || v == "sand") return "beach";
        }
        if (tags.TryGetValue("landuse", out v))
        {
            switch (v)
            {
                case "forest": return "forest";
                case "residential": case "retail": case "commercial": return "town";
                case "industrial": return "paved";
                case "farmland": case "farmyard": case "orchard": case "vineyard": case "meadow": return "farmland";
                case "brownfield": case "construction": case "quarry": return "dirt";
                case "grass": case "village_green": case "greenfield": case "recreation_ground": return "park";
                case "cemetery": return "cemetery";
            }
        }
        if (tags.TryGetValue("leisure", out v))
        {
            switch (v)
            {
                case "park": case "garden": case "common": case "playground": return "park";
                case "pitch": return "paved";
            }
        }
        if (tags.TryGetValue("amenity", out v) && v == "parking") return "paved";
        return null;
    }

    // Chains way pieces (lists of node ids) end to end into closed rings.
    static List<List<long>> JoinRings(List<List<long>> segments)
    {
        var rings = new List<List<long>>();
        while (segments.Count > 0)
        {
            var cur = segments[0];
            segments.RemoveAt(0);
            bool grew = true;
            while (grew && cur[0] != cur[cur.Count - 1])
            {
                grew = false;
                long end = cur[cur.Count - 1];
                for (int i = 0; i < segments.Count; i++)
                {
                    var s = segments[i];
                    if (s[0] == end) { }
                    else if (s[s.Count - 1] == end) s.Reverse();
                    else continue;
                    cur.AddRange(s.GetRange(1, s.Count - 1));
                    segments.RemoveAt(i);
                    grew = true;
                    break;
                }
            }
            if (cur.Count >= 4 && cur[0] == cur[cur.Count - 1]) rings.Add(cur);   // not closed = cut by the map edge, dropped
        }
        return rings;
    }

    // ---------------------------------------------------------------- small helpers

    static Dictionary<string, string> ReadTags(XElement e)
    {
        var tags = new Dictionary<string, string>();
        foreach (var t in e.Elements("tag"))
            tags[(string)t.Attribute("k")] = (string)t.Attribute("v");
        return tags;
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
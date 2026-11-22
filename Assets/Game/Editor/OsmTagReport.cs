using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Xml.Linq;
using UnityEditor;
using UnityEngine;

// Prints which land-cover and nature tags exist in map.osm.
public static class OsmTagReport
{
    static readonly string[] AreaKeys = { "landuse", "natural", "leisure", "amenity", "waterway", "man_made", "tourism", "historic", "type" };
    static readonly string[] NodeKeys = { "natural", "highway", "amenity", "tourism", "historic", "man_made", "leisure" };

    [MenuItem("RacerTN/OSM Tag Report")]
    static void Run()
    {
        string path = Path.GetFullPath("Assets/Game/RawData/SidiBouSaid/map.osm");
        if (!File.Exists(path)) { Debug.LogError("File not found: " + path); return; }

        XElement root = XDocument.Load(path).Root;
        var counts = new Dictionary<string, int>();

        foreach (var el in root.Elements())
        {
            string kind = el.Name.LocalName;
            string[] keys;
            if (kind == "way" || kind == "relation") keys = AreaKeys;
            else if (kind == "node") keys = NodeKeys;
            else continue;

            foreach (var t in el.Elements("tag"))
            {
                string k = (string)t.Attribute("k");
                if (System.Array.IndexOf(keys, k) < 0) continue;
                string id = kind + ": " + k + "=" + (string)t.Attribute("v");
                int c;
                counts.TryGetValue(id, out c);
                counts[id] = c + 1;
            }
        }

        var lines = counts.OrderByDescending(kv => kv.Value).Take(70).Select(kv => kv.Value + "  " + kv.Key);
        Debug.Log("OSM tag report (" + counts.Count + " different tags). Top ones:\n" + string.Join("\n", lines));
    }
}
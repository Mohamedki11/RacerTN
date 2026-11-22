using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using UnityEditor;
using UnityEngine;

// Editor tool (must live in an Editor folder).
// Downloads real elevation and OpenStreetMap data for a box of chunks and saves
// plain files into the output folder. Run it once per area, never at runtime.
//   Elevation: Terrarium tiles from the AWS open data bucket
//   Roads, buildings, coastline: OpenStreetMap through the Overpass API
// Credit both in CREDITS.md.
public class WorldDataFetcher : EditorWindow
{
    const int TileSize = 256;
    const int Magic = 0x314E5452; // "RTN1"

    static readonly string[] OverpassServers =
    {
        "https://overpass-api.de/api/interpreter",
        "https://overpass.kumi.systems/api/interpreter"
    };

    static readonly HttpClient http = MakeClient();

    WorldConfig config;
    int minChunkX = 4, minChunkZ = 4, maxChunkX = 5, maxChunkZ = 5;
    float spacing = 10f;
    int zoom = 14;
    string outFolder = "Assets/Game/RawData/SidiBouSaid";

    readonly Dictionary<long, Color32[]> tiles = new Dictionary<long, Color32[]>();
    int downloadedTiles;

    static HttpClient MakeClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("RacerTN-DataFetcher/1.0 (personal game project)");
        return c;
    }

    [MenuItem("RacerTN/World Data Fetcher")]
    static void Open()
    {
        GetWindow<WorldDataFetcher>("World Data Fetcher");
    }

    static string F5(double d) { return d.ToString("F5", CultureInfo.InvariantCulture); }
    static string F6(double d) { return d.ToString("F6", CultureInfo.InvariantCulture); }

    void OnGUI()
    {
        EditorGUILayout.Space();
        config = (WorldConfig)EditorGUILayout.ObjectField("World Config", config, typeof(WorldConfig), false);

        EditorGUILayout.LabelField("Chunk range (inclusive)", EditorStyles.boldLabel);
        minChunkX = EditorGUILayout.IntField("Min chunk X", minChunkX);
        minChunkZ = EditorGUILayout.IntField("Min chunk Z", minChunkZ);
        maxChunkX = EditorGUILayout.IntField("Max chunk X", maxChunkX);
        maxChunkZ = EditorGUILayout.IntField("Max chunk Z", maxChunkZ);

        EditorGUILayout.Space();
        spacing = EditorGUILayout.FloatField("Sample spacing (m)", spacing);
        zoom = EditorGUILayout.IntSlider("Elevation tile zoom", zoom, 10, 15);
        outFolder = EditorGUILayout.TextField("Output folder", outFolder);

        if (config == null)
        {
            EditorGUILayout.HelpBox("Drag your WorldConfig asset into the field above first.", MessageType.Info);
            return;
        }

        float cs = config.chunkSize;
        double sLat, wLon, nLat, eLon;
        GeoConverter.GameToLatLon(minChunkX * cs, minChunkZ * cs, config, out sLat, out wLon);
        GeoConverter.GameToLatLon((maxChunkX + 1) * cs, (maxChunkZ + 1) * cs, config, out nLat, out eLon);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Box corners (paste into Google Maps to check)", EditorStyles.boldLabel);
        EditorGUILayout.SelectableLabel("SW: " + F5(sLat) + ", " + F5(wLon), EditorStyles.textField, GUILayout.Height(18));
        EditorGUILayout.SelectableLabel("NE: " + F5(nLat) + ", " + F5(eLon), EditorStyles.textField, GUILayout.Height(18));
        EditorGUILayout.LabelField("Box size",
            ((maxChunkX - minChunkX + 1) * cs / 1000f) + " x " + ((maxChunkZ - minChunkZ + 1) * cs / 1000f) + " km");

        EditorGUILayout.Space();
        if (GUILayout.Button("1. Fetch elevation", GUILayout.Height(28))) Run(FetchElevation);
        if (GUILayout.Button("2. Fetch OSM roads, buildings, coast", GUILayout.Height(28))) Run(FetchOsm);
    }

    void Run(Action action)
    {
        try { action(); }
        catch (Exception e) { Debug.LogError("WorldDataFetcher failed: " + e); }
        finally { EditorUtility.ClearProgressBar(); }
    }

    bool Validate()
    {
        if (config == null)
        {
            EditorUtility.DisplayDialog("World Data Fetcher", "Assign a WorldConfig first.", "OK");
            return false;
        }
        if (maxChunkX < minChunkX || maxChunkZ < minChunkZ || minChunkX < 0 || minChunkZ < 0 ||
            maxChunkX >= config.gridChunks || maxChunkZ >= config.gridChunks)
        {
            EditorUtility.DisplayDialog("World Data Fetcher", "The chunk range is invalid.", "OK");
            return false;
        }
        float ratio = config.chunkSize / spacing;
        if (spacing <= 0f || Mathf.Abs(ratio - Mathf.Round(ratio)) > 0.001f)
        {
            EditorUtility.DisplayDialog("World Data Fetcher",
                "Spacing must divide the chunk size evenly (for example 5, 10 or 20).", "OK");
            return false;
        }
        return true;
    }

    // ---------------------------------------------------------------- elevation

    void FetchElevation()
    {
        if (!Validate()) return;

        tiles.Clear();
        downloadedTiles = 0;

        float cs = config.chunkSize;
        float minX = minChunkX * cs, minZ = minChunkZ * cs;
        float sizeX = (maxChunkX - minChunkX + 1) * cs, sizeZ = (maxChunkZ - minChunkZ + 1) * cs;
        int cols = Mathf.RoundToInt(sizeX / spacing) + 1;
        int rows = Mathf.RoundToInt(sizeZ / spacing) + 1;
        var heights = new float[cols * rows];

        for (int row = 0; row < rows; row++)
        {
            if (row % 5 == 0 &&
                EditorUtility.DisplayCancelableProgressBar("Fetching elevation",
                    "Row " + row + " of " + rows + " (" + downloadedTiles + " tiles downloaded)", row / (float)rows))
            {
                Debug.Log("Elevation fetch cancelled.");
                return;
            }
            for (int col = 0; col < cols; col++)
            {
                double lat, lon;
                GeoConverter.GameToLatLon(minX + col * spacing, minZ + row * spacing, config, out lat, out lon);
                heights[row * cols + col] = SampleHeight(lat, lon);
            }
        }

        string folder = Path.GetFullPath(outFolder);
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "elevation.bytes");

        float min = float.MaxValue, max = float.MinValue;
        using (var w = new BinaryWriter(File.Create(path)))
        {
            w.Write(Magic);
            w.Write(minX);
            w.Write(minZ);
            w.Write(spacing);
            w.Write(cols);
            w.Write(rows);
            for (int i = 0; i < heights.Length; i++)
            {
                if (heights[i] < min) min = heights[i];
                if (heights[i] > max) max = heights[i];
                w.Write(heights[i]);
            }
        }

        AssetDatabase.Refresh();
        Debug.Log("Elevation saved: " + cols + " x " + rows + " samples, " + downloadedTiles +
                  " tiles downloaded, height range " + min.ToString("F1") + " to " + max.ToString("F1") +
                  " m. File: " + path);
    }

    float SampleHeight(double lat, double lon)
    {
        double n = (1 << zoom) * (double)TileSize;
        double px = (lon + 180.0) / 360.0 * n;
        double latRad = lat * Math.PI / 180.0;
        double py = (1.0 - Math.Log(Math.Tan(latRad) + 1.0 / Math.Cos(latRad)) / Math.PI) / 2.0 * n;

        double fx = px - 0.5, fy = py - 0.5;
        int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
        float tx = (float)(fx - x0), ty = (float)(fy - y0);

        float h00 = PixelHeight(x0, y0);
        float h10 = PixelHeight(x0 + 1, y0);
        float h01 = PixelHeight(x0, y0 + 1);
        float h11 = PixelHeight(x0 + 1, y0 + 1);
        return Mathf.Lerp(Mathf.Lerp(h00, h10, tx), Mathf.Lerp(h01, h11, tx), ty);
    }

    float PixelHeight(int gx, int gy)
    {
        Color32[] tile = GetTile(gx >> 8, gy >> 8);
        // Unity stores texture rows bottom to top, the image is top to bottom.
        Color32 p = tile[(TileSize - 1 - (gy & 255)) * TileSize + (gx & 255)];
        return p.r * 256f + p.g + p.b / 256f - 32768f;
    }

    Color32[] GetTile(int tx, int ty)
    {
        long key = ((long)tx << 32) | (uint)ty;
        Color32[] pixels;
        if (tiles.TryGetValue(key, out pixels)) return pixels;

        string url = "https://s3.amazonaws.com/elevation-tiles-prod/terrarium/" + zoom + "/" + tx + "/" + ty + ".png";
        byte[] bytes = http.GetByteArrayAsync(url).GetAwaiter().GetResult();

        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
        if (!tex.LoadImage(bytes, false))
        {
            DestroyImmediate(tex);
            throw new Exception("Could not decode elevation tile " + url);
        }
        if (tex.width != TileSize || tex.height != TileSize)
        {
            int w = tex.width, h = tex.height;
            DestroyImmediate(tex);
            throw new Exception("Unexpected tile size " + w + "x" + h + " from " + url);
        }
        pixels = tex.GetPixels32();
        DestroyImmediate(tex);

        tiles[key] = pixels;
        downloadedTiles++;
        return pixels;
    }

    // ---------------------------------------------------------------- OpenStreetMap

    void FetchOsm()
    {
        if (!Validate()) return;

        float cs = config.chunkSize, margin = 50f;
        double s, w, n, e;
        GeoConverter.GameToLatLon(minChunkX * cs - margin, minChunkZ * cs - margin, config, out s, out w);
        GeoConverter.GameToLatLon((maxChunkX + 1) * cs + margin, (maxChunkZ + 1) * cs + margin, config, out n, out e);
        string bbox = F6(s) + "," + F6(w) + "," + F6(n) + "," + F6(e);

        string[] files = { "roads.json", "buildings.json", "coastline.json" };
        string[] filters =
        {
            "way[\"highway\"](" + bbox + ");",
            "way[\"building\"](" + bbox + ");",
            "way[\"natural\"=\"coastline\"](" + bbox + ");"
        };

        string folder = Path.GetFullPath(outFolder);
        Directory.CreateDirectory(folder);

        for (int i = 0; i < files.Length; i++)
        {
            EditorUtility.DisplayProgressBar("Fetching OpenStreetMap", files[i], i / (float)files.Length);
            string query = "[out:json][timeout:90];" + filters[i] + "out geom;";
            string json = Overpass(query);
            string path = Path.Combine(folder, files[i]);
            File.WriteAllText(path, json, new UTF8Encoding(false));
            Debug.Log("Saved " + files[i] + " (" + (json.Length / 1024) + " KB)");
            if (i < files.Length - 1) System.Threading.Thread.Sleep(2000);
        }

        AssetDatabase.Refresh();
        Debug.Log("OSM data saved to " + folder);
    }

    string Overpass(string query)
    {
        Exception last = null;
        foreach (string url in OverpassServers)
        {
            try
            {
                var content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("data", query) });
                var resp = http.PostAsync(url, content).GetAwaiter().GetResult();
                string body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                if (!resp.IsSuccessStatusCode) throw new Exception("HTTP " + (int)resp.StatusCode);
                if (!body.TrimStart().StartsWith("{")) throw new Exception("Unexpected reply");
                if (body.Contains("\"remark\"")) Debug.LogWarning("Overpass added a remark (maybe a timeout): check the file.");
                return body;
            }
            catch (Exception ex)
            {
                last = ex;
                Debug.LogWarning("Overpass failed on " + url + ": " + ex.Message);
            }
        }
        throw new Exception("All Overpass servers failed. Wait a minute and try again.", last);
    }
}

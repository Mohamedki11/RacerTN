using System;
using UnityEngine;

[Serializable]
public class OsmRoad
{
    public string type;        // motorway, primary, residential, footway...
    public string name;
    public int lanes;          // 0 = unknown
    public bool bridge;
    public bool tunnel;
    public Vector2[] points;   // game meters: x = east, y = north (game Z)
}

[Serializable]
public class OsmBuilding
{
    public string type;        // yes, house, apartments...
    public int levels;         // 0 = unknown
    public float height;       // meters, 0 = unknown
    public Vector2[] footprint;
}

[Serializable]
public class OsmLine
{
    public Vector2[] points;   // order kept: in OSM the land is on the LEFT of a coastline
}

[Serializable]
public class OsmWorldData
{
    public OsmRoad[] roads;
    public OsmBuilding[] buildings;
    public OsmLine[] coastline;
}
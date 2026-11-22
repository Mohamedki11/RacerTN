using System;
using UnityEngine;

// Converts between real-world latitude/longitude and game meters.
// Game X = east, game Z = north. Accurate to well under a meter per km
// around the anchor, which is plenty for a region a few kilometers wide.
public static class GeoConverter
{
    static double MetersPerDegLat(double latDeg)
    {
        double p = latDeg * Math.PI / 180.0;
        return 111132.92 - 559.82 * Math.Cos(2 * p) + 1.175 * Math.Cos(4 * p) - 0.0023 * Math.Cos(6 * p);
    }

    static double MetersPerDegLon(double latDeg)
    {
        double p = latDeg * Math.PI / 180.0;
        return 111412.84 * Math.Cos(p) - 93.5 * Math.Cos(3 * p) + 0.118 * Math.Cos(5 * p);
    }

    public static Vector2 LatLonToGame(double lat, double lon, WorldConfig c)
    {
        double dx = (lon - c.anchorLon) * MetersPerDegLon(c.anchorLat);
        double dz = (lat - c.anchorLat) * MetersPerDegLat(c.anchorLat);
        return new Vector2((float)(c.anchorGameX + dx), (float)(c.anchorGameZ + dz));
    }

    public static void GameToLatLon(float gameX, float gameZ, WorldConfig c, out double lat, out double lon)
    {
        double dx = gameX - c.anchorGameX;
        double dz = gameZ - c.anchorGameZ;
        lat = c.anchorLat + dz / MetersPerDegLat(c.anchorLat);
        lon = c.anchorLon + dx / MetersPerDegLon(c.anchorLat);
    }
}

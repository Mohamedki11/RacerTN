using UnityEngine;

// One asset that holds the world's basic settings.
// Create it with: right-click in Project > Create > RacerTN > World Config
[CreateAssetMenu(menuName = "RacerTN/World Config", fileName = "WorldConfig")]
public class WorldConfig : ScriptableObject
{
    [Header("Grid")]
    public float chunkSize = 1000f;   // meters per chunk side
    public int gridChunks = 60;       // 60 x 60 chunks = 60 x 60 km

    [Header("Real-world anchor of the Sidi Bou Said region")]
    public double anchorLat = 36.8715;
    public double anchorLon = 10.3430;

    [Header("Where that anchor sits in game meters")]
    // 5000, 5000 puts the anchor in the middle of chunks 4-5, with room to grow
    // west and south later without negative coordinates.
    public float anchorGameX = 5000f;
    public float anchorGameZ = 5000f;

    public Vector2 ChunkOrigin(int cx, int cz)
    {
        return new Vector2(cx * chunkSize, cz * chunkSize);
    }
}

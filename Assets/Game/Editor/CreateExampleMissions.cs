using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Menu: RacerTN > Missions > Create Two Example Missions
/// Builds two complete missions (markers, light beams, texts, cargo settings) near the car,
/// and adds CargoHealth to the car and MissionDialogue to the Systems object if they are missing.
/// Afterwards: MOVE each pickup and drop-off onto real roads. Delete the old test mission if you want.
/// Put this file in an Editor folder (Assets/Game/Editor).
/// </summary>
public static class CreateExampleMissions
{
    const string MatFolder = "Assets/Game/Materials/Missions";

    [MenuItem("RacerTN/Missions/Create Two Example Missions")]
    static void Create()
    {
        var car = Object.FindFirstObjectByType<CarController>();
        Vector3 start = Vector3.zero;
        if (car != null) start = car.transform.position;
        else if (SceneView.lastActiveSceneView != null) start = SceneView.lastActiveSceneView.pivot;

        EnsureFolders();
        Material green = MakeMat("M_Mission_Pickup", new Color(0.2f, 1f, 0.4f));
        Material gold = MakeMat("M_Mission_Dropoff", new Color(1f, 0.8f, 0.15f));

        var root = new GameObject("Missions");
        Undo.RegisterCreatedObjectUndo(root, "Create example missions");

        // ---------------- Mission 1: easy, sturdy cargo ----------------
        MissionPickup m1 = MakeMission(root.transform, "Mission_1_CafePackage",
                                       start + new Vector3(25f, 0f, 25f), start + new Vector3(300f, 0f, 25f), green, gold);
        m1.title = "The Cafe Package";
        m1.description = "Cigarette crates for the cafe. Keep them in one piece.";
        m1.cargoName = "Crates";
        m1.timeLimit = 110f;
        m1.reward = 150;
        m1.fragility = 0.6f;
        m1.dropoffRadius = 10f;
        m1.briefing = new[]
        {
            L("Kamel", "Aslema. You are the new driver, behi? The crates are loaded."),
            L("Kamel", "Follow the yellow light. Don't stop, don't talk to anyone."),
            L("Kamel", "And watch the crates. If they break, I don't pay full.")
        };
        m1.cargoWarning = L("Kamel", "Careful! I can hear the crates cracking!");
        m1.onSuccess = new[]
        {
            L("Kamel", "Behi, behi! Clean work."),
            L("Kamel", "Here is your money. Come back soon.")
        };
        m1.onFail = new[] { L("Kamel", "Yezzi! That was a waste of my time.") };

        // ---------------- Mission 2: longer, fragile cargo ----------------
        MissionPickup m2 = MakeMission(root.transform, "Mission_2_FragileGoods",
                                       start + new Vector3(-25f, 0f, 25f), start + new Vector3(-300f, 0f, 300f), green, gold);
        m2.title = "Fragile Goods";
        m2.description = "Antique glass for a rich client. Every crack costs you.";
        m2.cargoName = "Glassware";
        m2.timeLimit = 150f;
        m2.reward = 400;
        m2.fragility = 1.8f;
        m2.dropoffRadius = 10f;
        m2.briefing = new[]
        {
            L("Kamel", "This one is fragile. Antique glass from the old souk."),
            L("Kamel", "Slow in the corners, fast on the straights."),
            L("Kamel", "Fissa3, but gently. You hear me?")
        };
        m2.cargoWarning = L("Kamel", "I hear glass breaking! Slow down!");
        m2.onSuccess = new[]
        {
            L("Rafik", "Mrigel! Not one crack."),
            L("Rafik", "Yaatik essa7a. Kamel was right about you.")
        };
        m2.onFail = new[] { L("Rafik", "Where is my glass? Don't call me again.") };

        // ---------------- make sure the scene has what the missions need ----------------
        if (car != null && car.GetComponent<CargoHealth>() == null)
            Undo.AddComponent<CargoHealth>(car.gameObject);
        else if (car == null)
            Debug.LogWarning("No CarController found: add the CargoHealth script to the Car yourself.");

        var mgr = Object.FindFirstObjectByType<MissionManager>();
        if (mgr != null)
        {
            if (mgr.GetComponent<MissionDialogue>() == null) Undo.AddComponent<MissionDialogue>(mgr.gameObject);
        }
        else Debug.LogWarning("No MissionManager found: add MissionManager and MissionDialogue to your Systems object.");

        EditorSceneManager.MarkAllScenesDirty();
        Selection.activeGameObject = root;
        Debug.Log("Two example missions created under 'Missions'. Move each pickup and each Dropoff onto real roads.");
    }

    static MissionPickup MakeMission(Transform parent, string name, Vector3 pickupPos, Vector3 dropPos,
                                     Material pickMat, Material dropMat)
    {
        // Pickup: trigger sphere + flat glowing disc + light beam
        var pickup = new GameObject(name);
        pickup.transform.SetParent(parent, false);
        pickup.transform.position = Snap(pickupPos);
        var col = pickup.AddComponent<SphereCollider>();        // must come before MissionPickup
        col.isTrigger = true;
        col.radius = 6f;
        var mp = pickup.AddComponent<MissionPickup>();
        var pickBeam = pickup.AddComponent<MissionBeam>();
        pickBeam.color = new Color(0.2f, 1f, 0.4f, 1f);
        MakeMarker(pickup.transform, pickMat);

        // Drop-off: disc + beam (hidden by the manager until the mission starts)
        var drop = new GameObject(name + "_Dropoff");
        drop.transform.SetParent(parent, false);
        drop.transform.position = Snap(dropPos);
        var dropBeam = drop.AddComponent<MissionBeam>();
        dropBeam.color = new Color(1f, 0.8f, 0.15f, 1f);
        MakeMarker(drop.transform, dropMat);

        mp.dropoff = drop.transform;
        return mp;
    }

    static void MakeMarker(Transform parent, Material mat)
    {
        var v = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        v.name = "Marker";
        Object.DestroyImmediate(v.GetComponent<Collider>());
        v.transform.SetParent(parent, false);
        v.transform.localPosition = new Vector3(0f, 0.1f, 0f);
        v.transform.localScale = new Vector3(10f, 0.1f, 10f);   // flat disc, 10 m wide
        v.GetComponent<Renderer>().sharedMaterial = mat;
        v.AddComponent<MissionMarker>();
    }

    // Puts a point on the ground (terrain or buildings) below it
    static Vector3 Snap(Vector3 p)
    {
        RaycastHit hit;
        if (Physics.Raycast(p + Vector3.up * 1000f, Vector3.down, out hit, 3000f)) p.y = hit.point.y;
        return p;
    }

    static MissionLine L(string speaker, string text)
    {
        return new MissionLine { speaker = speaker, text = text };
    }

    static void EnsureFolders()
    {
        if (!AssetDatabase.IsValidFolder("Assets/Game/Materials")) AssetDatabase.CreateFolder("Assets/Game", "Materials");
        if (!AssetDatabase.IsValidFolder(MatFolder)) AssetDatabase.CreateFolder("Assets/Game/Materials", "Missions");
    }

    static Material MakeMat(string name, Color c)
    {
        string path = MatFolder + "/" + name + ".mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            mat = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            AssetDatabase.CreateAsset(mat, path);
        }
        mat.SetColor("_BaseColor", c);
        EditorUtility.SetDirty(mat);
        AssetDatabase.SaveAssets();
        return mat;
    }
}

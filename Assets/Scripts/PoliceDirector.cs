using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Decides when the police come. Put on the Systems object and drag the police car PREFAB into "Police Prefab".
/// Heat rises while you speed (during a mission). At full heat a chase starts: a police car appears on a road
/// behind you. If a cop stays close, the BUSTED bar fills; full = mission failed. Get far away long enough to escape.
/// </summary>
public class PoliceDirector : MonoBehaviour
{
    public static PoliceDirector Instance { get; private set; }

    [SerializeField] GameObject policePrefab;
    [Tooltip("true = police only appear while a mission is running.")]
    [SerializeField] bool onlyDuringMissions = true;

    [Header("Heat (0 to 1)")]
    [SerializeField] float speedLimitKmh = 120f;
    [Tooltip("Heat per second when you are 30 km/h over the limit. 0.08 = about 12 s to a chase.")]
    [SerializeField] float heatPerSecondAt30Over = 0.08f;
    [SerializeField] float heatCoolPerSecond = 0.03f;

    [Header("Chase")]
    [SerializeField] int maxCops = 2;
    [SerializeField] float secondCopDelay = 15f;
    [Tooltip("How far behind you a police car appears (metres). Smaller = they appear nearer.")]
    [SerializeField] float spawnDistance = 220f;
    [Tooltip("A police car never appears closer than this (metres).")]
    [SerializeField] float minSpawnDistance = 90f;
    [Tooltip("A cop closer than this makes the BUSTED bar fill.")]
    [SerializeField] float bustRange = 14f;
    [SerializeField] float bustSeconds = 4f;
    [SerializeField] float bustRecoverPerSecond = 0.5f;
    [Tooltip("No cop within this distance for 'Lose Seconds' = you escaped.")]
    [SerializeField] float loseDistance = 160f;
    [SerializeField] float loseSeconds = 12f;
    [SerializeField] int fineOutsideMission = 50;

    public float Heat { get; private set; }
    public float Busted { get; private set; }
    public bool Chasing { get; private set; }

    Transform player;
    Rigidbody playerBody;
    readonly List<PoliceAI> cops = new List<PoliceAI>();
    float chaseTime, loseTimer;
    bool allowed;

    void Awake() => Instance = this;

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    void Start()
    {
        var c = PlayerCar.Find();
        if (c != null) { player = c.transform; playerBody = c.GetComponent<Rigidbody>(); }
        if (policePrefab == null) Debug.LogWarning("PoliceDirector: drag the police car prefab into 'Police Prefab'.");
    }

    public void AddHeat(float amount)
    {
        if (!Chasing && allowed) Heat = Mathf.Clamp01(Heat + amount);
    }

    void Update()
    {
        if (player == null || GameState.Paused) return;

        var m = MissionManager.Instance;
        allowed = !onlyDuringMissions || (m != null && m.State == MissionState.Active);
        if (!allowed)
        {
            if (Chasing) EndChase();
            Heat = Mathf.MoveTowards(Heat, 0f, 0.2f * Time.deltaTime);
            return;
        }

        if (!Chasing)
        {
            float kmh = playerBody.linearVelocity.magnitude * 3.6f;
            if (kmh > speedLimitKmh) Heat += heatPerSecondAt30Over * ((kmh - speedLimitKmh) / 30f) * Time.deltaTime;
            else Heat -= heatCoolPerSecond * Time.deltaTime;
            Heat = Mathf.Clamp01(Heat);
            if (Heat >= 1f) StartChase();
            return;
        }

        UpdateChase();
    }

    void StartChase()
    {
        Chasing = true;
        chaseTime = 0f;
        loseTimer = 0f;
        Busted = 0f;
        SpawnCop();
    }

    void UpdateChase()
    {
        chaseTime += Time.deltaTime;
        if (cops.Count < maxCops && chaseTime > secondCopDelay * cops.Count) SpawnCop();

        float nearest = float.MaxValue;
        foreach (var cop in cops)
            if (cop != null) nearest = Mathf.Min(nearest, Vector3.Distance(cop.transform.position, player.position));

        // Busted bar
        Busted += (nearest < bustRange ? 1f / bustSeconds : -bustRecoverPerSecond) * Time.deltaTime;
        Busted = Mathf.Clamp01(Busted);
        if (Busted >= 1f) { DoBust(); return; }

        // Escape
        loseTimer = nearest > loseDistance ? loseTimer + Time.deltaTime : 0f;
        if (loseTimer > loseSeconds) { EndChase(); Heat = 0f; }
    }

    void DoBust()
    {
        var m = MissionManager.Instance;
        if (m != null && m.State == MissionState.Active) m.Bust();
        else GameState.Money -= fineOutsideMission;
        EndChase();
        Heat = 0f;
    }

    void EndChase()
    {
        foreach (var cop in cops) if (cop != null) Destroy(cop.gameObject);
        cops.Clear();
        Chasing = false;
        Busted = 0f;
    }

    bool SpawnCop()
    {
        if (policePrefab == null || RouteGuide.Instance == null) return false;

        Vector3 back = -player.forward;
        back.y = 0f;
        back.Normalize();
        for (int tries = 0; tries < 8; tries++)
        {
            Vector3 dir = Quaternion.Euler(0f, Random.Range(-70f, 70f), 0f) * back;     // somewhere behind the player
            Vector3 wanted = player.position + dir * spawnDistance * Random.Range(0.8f, 1.2f);
            Vector3 p;
            if (!RouteGuide.Instance.NearestRoadPoint(wanted, out p)) continue;
            if (Vector3.Distance(p, player.position) < minSpawnDistance) continue;

            var go = Instantiate(policePrefab);
            var ai = go.GetComponent<PoliceAI>();
            if (ai == null) { Debug.LogError("The police prefab has no PoliceAI script."); Destroy(go); return false; }
            Vector3 toPlayer = player.position - p;
            toPlayer.y = 0f;
            ai.Target = player;
            ai.Chasing = true;
            ai.Place(p, Quaternion.LookRotation(toPlayer).eulerAngles.y);
            cops.Add(ai);
            return true;
        }
        return false;
    }

    // ---------------------------------------------------------------- HUD

    void OnGUI()
    {
        if (!Chasing && !(allowed && Heat > 0.03f)) return;
        GUI.depth = 1;
        Vector2 screen = GuiKit.Begin();
        float cx = screen.x / 2f;

        if (Chasing)
        {
            bool flash = Mathf.Repeat(Time.unscaledTime, 0.6f) < 0.3f;
            GuiKit.Text(new Rect(cx - 300, 262, 600, 40), "POLICE CHASE", 30,
                        flash ? new Color(1f, 0.3f, 0.3f) : new Color(0.4f, 0.6f, 1f));
            Bar(new Rect(cx - 160, 306, 320, 16), Busted, new Color(1f, 0.3f, 0.25f));
            GuiKit.Text(new Rect(cx - 160, 326, 320, 28), "BUSTED " + Mathf.RoundToInt(Busted * 100f) + "%", 20, Color.white);
        }
        else
        {
            GuiKit.Text(new Rect(cx - 300, 262, 600, 36), "HEAT", 24, new Color(1f, 0.7f, 0.3f));
            Bar(new Rect(cx - 120, 300, 240, 12), Heat, Color.Lerp(new Color(1f, 0.85f, 0.3f), new Color(1f, 0.3f, 0.25f), Heat));
        }
        GuiKit.End();
    }

    static void Bar(Rect r, float value, Color fill)
    {
        Color old = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.7f);
        GUI.DrawTexture(new Rect(r.x - 3, r.y - 3, r.width + 6, r.height + 6), Texture2D.whiteTexture);
        GUI.color = fill;
        GUI.DrawTexture(new Rect(r.x, r.y, r.width * Mathf.Clamp01(value), r.height), Texture2D.whiteTexture);
        GUI.color = old;
    }
}

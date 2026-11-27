using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Headlights, tail lights and brake lights. Put on the player's Car.
///   L   cycles the headlights:  Auto (on when it gets dark)  ->  On  ->  Off
/// Brake lights glow bright red when you brake (S while moving forward, or Space).
/// It creates two spot lights in front, two small red lights behind, and small lamp boxes you can see on the car.
/// Select the car in the Scene view to see where the lights will be (yellow = front, red = rear), then change the offsets.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class CarLights : MonoBehaviour
{
    public enum Mode { Auto, On, Off }

    [SerializeField] Mode mode = Mode.Auto;
    [SerializeField] Key toggleKey = Key.L;
    [Tooltip("Auto mode: lights come on when daylight drops below this (0 = night, 1 = full day).")]
    [SerializeField, Range(0f, 1f)] float autoOnBelowDaylight = 0.55f;

    [Header("Headlights")]
    [Tooltip("Where the right headlight is, relative to the car. The left one mirrors it.")]
    [SerializeField] Vector3 headOffset = new Vector3(0.65f, 0.7f, 2.0f);
    [SerializeField] float headIntensity = 60f;
    [SerializeField] float headRange = 45f;
    [SerializeField] float headInnerAngle = 25f;
    [SerializeField] float headOuterAngle = 75f;
    [Tooltip("Degrees the beams point down.")]
    [SerializeField] float headDownTilt = 4f;
    [SerializeField] Color headColor = new Color(1f, 0.95f, 0.8f);

    [Header("Tail and brake lights")]
    [SerializeField] Vector3 tailOffset = new Vector3(0.6f, 0.8f, -2.1f);
    [SerializeField] float tailRange = 5f;
    [Tooltip("Glow when only the headlights are on.")]
    [SerializeField] float tailIntensity = 1.5f;
    [Tooltip("Glow when braking.")]
    [SerializeField] float brakeIntensity = 6f;
    [SerializeField] Color brakeColor = new Color(1f, 0.05f, 0.05f);

    [Header("Lamp boxes you can see on the car")]
    [Tooltip("Untick if your model already has lamp parts you want to use instead.")]
    [SerializeField] bool createLampBoxes = true;
    [SerializeField] Vector3 lampSize = new Vector3(0.35f, 0.18f, 0.06f);

    Rigidbody rb;
    CarController car;
    Light[] heads = new Light[2];
    Light[] tails = new Light[2];
    Material headLampMat, tailLampMat;
    float headLevel, tailLevel;
    string toast;
    float toastUntil;

    static readonly Color HeadOff = new Color(0.25f, 0.25f, 0.22f);
    static readonly Color HeadOn = new Color(2.5f, 2.4f, 1.9f);
    static readonly Color TailOff = new Color(0.2f, 0f, 0f);
    static readonly Color TailBright = new Color(3f, 0.1f, 0.1f);

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        car = GetComponent<CarController>();

        headLampMat = MakeMat(HeadOff);
        tailLampMat = MakeMat(TailOff);

        for (int i = 0; i < 2; i++)
        {
            float side = i == 0 ? -1f : 1f;
            Vector3 hp = new Vector3(headOffset.x * side, headOffset.y, headOffset.z);
            Vector3 tp = new Vector3(tailOffset.x * side, tailOffset.y, tailOffset.z);

            heads[i] = MakeLight("Headlight" + (i == 0 ? "L" : "R"), LightType.Spot, hp, Quaternion.Euler(headDownTilt, 0f, 0f), headColor, headRange);
            heads[i].spotAngle = headOuterAngle;
            heads[i].innerSpotAngle = headInnerAngle;
            tails[i] = MakeLight("Taillight" + (i == 0 ? "L" : "R"), LightType.Point, tp, Quaternion.identity, brakeColor, tailRange);

            if (createLampBoxes)
            {
                MakeLampBox("HeadLamp" + i, hp, headLampMat);
                MakeLampBox("TailLamp" + i, tp, tailLampMat);
            }
        }
    }

    void OnDestroy()
    {
        if (headLampMat != null) Destroy(headLampMat);
        if (tailLampMat != null) Destroy(tailLampMat);
    }

    Light MakeLight(string name, LightType type, Vector3 localPos, Quaternion localRot, Color color, float range)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        go.transform.localPosition = localPos;
        go.transform.localRotation = localRot;
        var l = go.AddComponent<Light>();
        l.type = type;
        l.color = color;
        l.range = range;
        l.intensity = 0f;
        l.shadows = LightShadows.None;
        l.enabled = false;
        return l;
    }

    void MakeLampBox(string name, Vector3 localPos, Material mat)
    {
        var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
        box.name = name;
        Destroy(box.GetComponent<Collider>());
        box.transform.SetParent(transform, false);
        box.transform.localPosition = localPos;
        box.transform.localScale = lampSize;
        var r = box.GetComponent<Renderer>();
        r.sharedMaterial = mat;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
    }

    static Material MakeMat(Color c)
    {
        Shader sh = Shader.Find("Universal Render Pipeline/Unlit");
        if (sh == null) sh = Shader.Find("Sprites/Default");
        var m = new Material(sh);
        m.color = c;
        return m;
    }

    void Update()
    {
        var k = Keyboard.current;
        if (k != null && !GameState.Paused && !FreeCamera.Active && k[toggleKey].wasPressedThisFrame)
        {
            mode = (Mode)(((int)mode + 1) % 3);
            toast = "Headlights: " + mode;
            toastUntil = Time.unscaledTime + 1.5f;
        }

        float daylight = TimeOfDay.Instance != null ? TimeOfDay.Instance.Daylight : 1f;
        bool lightsOn = mode == Mode.On || (mode == Mode.Auto && daylight < autoOnBelowDaylight);

        float fwd = Vector3.Dot(rb.linearVelocity, transform.forward);
        bool braking = car != null && (car.Brake > 0.5f || (car.Throttle < -0.1f && fwd > 1f));

        // Fade smoothly instead of switching
        float dim = Mathf.Clamp01(tailIntensity / Mathf.Max(0.01f, brakeIntensity));
        headLevel = Mathf.MoveTowards(headLevel, lightsOn ? 1f : 0f, Time.deltaTime * 6f);
        float tailTarget = braking ? 1f : (lightsOn ? dim : 0f);
        tailLevel = Mathf.MoveTowards(tailLevel, tailTarget, Time.deltaTime * 10f);

        for (int i = 0; i < 2; i++)
        {
            heads[i].intensity = headIntensity * headLevel;
            heads[i].enabled = headLevel > 0.01f;
            tails[i].intensity = brakeIntensity * tailLevel;
            tails[i].enabled = tailLevel > 0.01f;
        }
        headLampMat.color = Color.Lerp(HeadOff, HeadOn, headLevel);
        tailLampMat.color = Color.Lerp(TailOff, TailBright, tailLevel);
    }

    void OnGUI()
    {
        if (Time.unscaledTime > toastUntil) return;
        GUI.depth = 2;
        Vector2 screen = GuiKit.Begin();
        GuiKit.Text(new Rect(screen.x / 2 - 400, 900, 800, 50), toast, 28, Color.white);
        GuiKit.End();
    }

    // Shows where the lights will be when the car is selected in the Scene view
    void OnDrawGizmosSelected()
    {
        Gizmos.matrix = transform.localToWorldMatrix;
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(new Vector3(-headOffset.x, headOffset.y, headOffset.z), 0.12f);
        Gizmos.DrawWireSphere(headOffset, 0.12f);
        Gizmos.color = Color.red;
        Gizmos.DrawWireSphere(new Vector3(-tailOffset.x, tailOffset.y, tailOffset.z), 0.12f);
        Gizmos.DrawWireSphere(tailOffset, 0.12f);
    }
}

using UnityEngine;

/// <summary>
/// Tire smoke and skid marks. Put on the Car. Drag the 4 wheel objects into "Wheels" and tick Rear on the back two.
/// It needs no assets: the smoke texture and the marks are made in code.
/// Tires skid when: the car slides sideways, brakes hard (S while moving forward), or uses the handbrake (rear wheels).
/// Burnouts (full throttle while almost stopped) make smoke from the rear wheels.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class TireEffects : MonoBehaviour
{
    [System.Serializable]
    public class Wheel
    {
        public Transform wheelObject;
        public bool rear;
        [HideInInspector] public TrailRenderer trail;
        [HideInInspector] public ParticleSystem smoke;
    }

    [SerializeField] Wheel[] wheels;
    [SerializeField] float wheelRadius = 0.35f;
    [Tooltip("The ground check goes down this much below the wheel radius. Raise it if marks never appear.")]
    [SerializeField] float rayExtra = 0.4f;

    [Header("When the tires skid")]
    [Tooltip("Sideways speed (m/s) above which the tires skid. Lower = skids more easily.")]
    [SerializeField] float slipSpeed = 4f;
    [SerializeField] float handbrakeMinSpeed = 6f;     // m/s
    [SerializeField] float hardBrakeMinSpeed = 8f;     // m/s

    [Header("Skid marks")]
    [SerializeField] float markWidth = 0.25f;
    [SerializeField] float markLifetime = 12f;
    [SerializeField] float markLift = 0.09f;           // above the ground, so they show on the road mesh

    [Header("Smoke")]
    [SerializeField] float smokeRate = 30f;
    [SerializeField] Color smokeColor = new Color(0.85f, 0.85f, 0.85f, 0.45f);

    [Header("Sound (optional)")]
    [Tooltip("A seamless tire-squeal loop (WAV or OGG). It plays whenever the tires skid or burn out.")]
    [SerializeField] AudioClip skidLoop;
    [SerializeField, Range(0f, 1f)] float skidVolume = 0.7f;
    [SerializeField] float skidLoopFade = 0.2f;

    Rigidbody rb;
    CarController car;
    Material skidMat, smokeMat;
    CrossLoop skidSound;
    float skidGain;
    Texture2D softCircle;
    readonly RaycastHit[] hits = new RaycastHit[8];

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        car = GetComponent<CarController>();

        Shader sh = Shader.Find("Sprites/Default");
        if (sh == null) sh = Shader.Find("Universal Render Pipeline/Unlit");
        skidMat = new Material(sh);
        smokeMat = new Material(sh);
        softCircle = MakeSoftCircle();
        smokeMat.mainTexture = softCircle;
        if (skidLoop != null) skidSound = new CrossLoop(transform, "TireSqueal", skidLoop, skidLoopFade);

        if (wheels == null) return;
        foreach (var w in wheels)
            if (w != null && w.wheelObject != null) w.smoke = MakeSmoke();
    }

    void OnDestroy()
    {
        if (skidMat != null) Destroy(skidMat);
        if (smokeMat != null) Destroy(smokeMat);
        if (softCircle != null) Destroy(softCircle);
    }

    void Update()
    {
        if (wheels == null) return;

        Vector3 v = rb.linearVelocity;
        float fwd = Vector3.Dot(v, transform.forward);
        float side = Mathf.Abs(Vector3.Dot(v, transform.right));
        float thr = car != null ? car.Throttle : 0f;

        bool handbrake = car != null && car.Brake > 0.5f && Mathf.Abs(fwd) > handbrakeMinSpeed;
        bool hardBrake = thr < -0.5f && fwd > hardBrakeMinSpeed;
        bool burnout = thr > 0.9f && Mathf.Abs(fwd) < 5f;
        bool anySqueal = false;

        foreach (var w in wheels)
        {
            if (w == null || w.wheelObject == null) continue;

            Vector3 point;
            bool grounded = Ground(w.wheelObject.position, out point);
            bool skid = grounded && (side > slipSpeed || hardBrake || (handbrake && w.rear));
            bool smoking = grounded && (skid || (burnout && w.rear));
            if (smoking) anySqueal = true;

            // Skid mark: one new trail for each skid, so marks never join across gaps
            if (skid)
            {
                if (w.trail == null) w.trail = NewTrail(point + Vector3.up * markLift);
                w.trail.transform.position = point + Vector3.up * markLift;
            }
            else if (w.trail != null)
            {
                Destroy(w.trail.gameObject, markLifetime + 0.5f);   // stays, fades, then disappears
                w.trail = null;
            }

            // Smoke
            if (w.smoke != null)
            {
                w.smoke.transform.position = point + Vector3.up * 0.1f;
                var em = w.smoke.emission;
                em.rateOverTime = smoking ? smokeRate * Mathf.Clamp(0.5f + side / 8f, 0.5f, 2f) : 0f;
            }
        }

        // Tire squeal: fades in and out, louder and higher the harder the car slides
        if (skidSound != null)
        {
            float target = anySqueal ? Mathf.Clamp01(0.35f + side / 10f) * skidVolume : 0f;
            skidGain = Mathf.MoveTowards(skidGain, target, Time.deltaTime * (target > skidGain ? 6f : 3f));
            skidSound.Update(skidGain, Mathf.Lerp(0.9f, 1.15f, Mathf.Clamp01(side / 12f)));
        }
    }

    bool Ground(Vector3 origin, out Vector3 point)
    {
        int n = Physics.RaycastNonAlloc(origin, Vector3.down, hits, wheelRadius + rayExtra, ~0, QueryTriggerInteraction.Ignore);
        float best = float.MaxValue;
        bool found = false;
        point = origin;
        for (int i = 0; i < n; i++)
        {
            if (hits[i].collider.attachedRigidbody == rb) continue;     // ignore the car itself
            if (hits[i].distance < best) { best = hits[i].distance; point = hits[i].point; found = true; }
        }
        return found;
    }

    TrailRenderer NewTrail(Vector3 at)
    {
        var go = new GameObject("SkidMark");
        go.transform.rotation = Quaternion.Euler(90f, 0f, 0f);          // lies flat on the ground
        go.transform.position = at;
        var t = go.AddComponent<TrailRenderer>();
        t.time = markLifetime;
        t.widthMultiplier = markWidth;
        t.minVertexDistance = 0.25f;
        t.alignment = LineAlignment.TransformZ;
        t.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        t.receiveShadows = false;
        t.sharedMaterial = skidMat;

        var dark = new Color(0.04f, 0.04f, 0.04f);
        var g = new Gradient();
        g.SetKeys(new[] { new GradientColorKey(dark, 0f), new GradientColorKey(dark, 1f) },
                  new[] { new GradientAlphaKey(0.65f, 0f), new GradientAlphaKey(0f, 1f) });   // fades toward the old end
        t.colorGradient = g;
        return t;
    }

    ParticleSystem MakeSmoke()
    {
        var go = new GameObject("TireSmoke");
        go.transform.SetParent(transform, false);
        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop();

        var main = ps.main;
        main.loop = true;
        main.playOnAwake = false;
        main.startLifetime = new ParticleSystem.MinMaxCurve(0.8f, 1.6f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(0.3f, 1.2f);
        main.startSize = new ParticleSystem.MinMaxCurve(0.6f, 1.4f);
        main.startColor = smokeColor;
        main.gravityModifier = -0.05f;                                   // drifts slightly upward
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.maxParticles = 150;

        var em = ps.emission;
        em.rateOverTime = 0f;

        var shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Sphere;
        shape.radius = 0.15f;

        var size = ps.sizeOverLifetime;
        size.enabled = true;
        size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, 0.4f, 1f, 1.6f));   // grows

        var col = ps.colorOverLifetime;
        col.enabled = true;
        var g = new Gradient();
        g.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                  new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.15f), new GradientAlphaKey(0f, 1f) });
        col.color = g;

        var r = go.GetComponent<ParticleSystemRenderer>();
        r.sharedMaterial = smokeMat;
        ps.Play();
        return ps;
    }

    static Texture2D MakeSoftCircle()
    {
        const int N = 64;
        var t = new Texture2D(N, N, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                float dx = (x + 0.5f) / N * 2f - 1f, dy = (y + 0.5f) / N * 2f - 1f;
                float a = Mathf.SmoothStep(0f, 1f, 1f - Mathf.Sqrt(dx * dx + dy * dy));
                t.SetPixel(x, y, new Color(1f, 1f, 1f, a));
            }
        t.Apply();
        return t;
    }
}

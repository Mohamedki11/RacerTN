using System.Collections.Generic;
using UnityEngine;

// Wheel-collider car with: a top speed and engine power that fades with speed, steering that
// gets smaller as you go faster, suspension tuned from the car's mass, anti-roll bars,
// downforce, a side-grip assist (less sliding) and a limit on how hard a small bump can
// throw the car upward.
public class CarController : MonoBehaviour
{
    [System.Serializable]
    public class Wheel
    {
        public WheelCollider collider;
        public Transform visual;   // the "Visual" child
        public bool steering;
        public bool powered;
    }

    [Header("Wheels")]
    [SerializeField] Wheel[] wheels;
    [SerializeField] Transform centerOfMass;

    [Header("Engine")]
    [SerializeField] float motorTorque = 2000f;     // per powered wheel
    [SerializeField] float brakeTorque = 3500f;
    [SerializeField] float topSpeedKmh = 220f;
    [SerializeField] float reverseSpeedKmh = 40f;

    [Header("Steering")]
    [SerializeField] float maxSteerAngle = 30f;
    [SerializeField] float steerFalloffKmh = 90f;   // at this speed the steering angle is halved
    [SerializeField] float steerSpeed = 3.5f;       // how fast the wheel turns toward the key
    [SerializeField] float steerReturnSpeed = 7f;   // how fast it centers when you let go

    [Header("Suspension and grip (applied when the game starts)")]
    [SerializeField] bool applyTuning = true;
    [SerializeField] float mass = 1300f;
    [SerializeField] float suspensionDistance = 0.25f;
    [SerializeField] float suspensionFrequency = 2f;   // Hz. Higher = stiffer. Try 1.5 to 2.5
    [SerializeField] float suspensionDamping = 0.5f;   // 0.3 bouncy ... 0.8 stiff and dead
    [SerializeField] float forwardStiffness = 1.5f;
    [SerializeField] float sidewaysStiffness = 2f;     // more = more grip in corners
    [SerializeField] float antiRoll = 6000f;           // stops body roll and flipping

    [Header("Stability")]
    [SerializeField] float downforce = 1.5f;           // pushes the car down harder the faster it goes
    [SerializeField] float sideGrip = 1.5f;            // kills sliding sideways. 0 = off
    [SerializeField] float maxBounceSpeed = 1.5f;      // m/s a bump may throw the car upward. Lower = fewer jumps

    // Set by CarInput now, and by AI later
    public float Throttle;
    public float Steer;
    public float Brake;

    Rigidbody rb;
    float smoothedSteer;
    readonly List<WheelCollider[]> axles = new List<WheelCollider[]>();   // left/right pairs for the anti-roll bar

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        if (applyTuning)
        {
            rb.mass = mass;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            ApplyWheelTuning();
        }
        if (centerOfMass != null) rb.centerOfMass = centerOfMass.localPosition;
        FindAxles();
    }

    void ApplyWheelTuning()
    {
        if (wheels == null || wheels.Length == 0) return;
        float sprung = rb.mass / wheels.Length;
        float w = 2f * Mathf.PI * suspensionFrequency;
        float spring = sprung * w * w;
        float damper = 2f * suspensionDamping * Mathf.Sqrt(spring * sprung);

        foreach (var wh in wheels)
        {
            WheelCollider c = wh.collider;
            if (c == null) continue;
            c.suspensionDistance = suspensionDistance;
            JointSpring js = c.suspensionSpring;
            js.spring = spring;
            js.damper = damper;
            js.targetPosition = 0.5f;
            c.suspensionSpring = js;
            c.forceAppPointDistance = 0f;

            WheelFrictionCurve f = c.forwardFriction;
            f.stiffness = forwardStiffness;
            c.forwardFriction = f;
            WheelFrictionCurve s = c.sidewaysFriction;
            s.stiffness = sidewaysStiffness;
            c.sidewaysFriction = s;
        }
    }

    // Pairs wheels on the same axle (same front/back position, opposite sides)
    void FindAxles()
    {
        axles.Clear();
        if (wheels == null) return;
        for (int i = 0; i < wheels.Length; i++)
            for (int j = i + 1; j < wheels.Length; j++)
            {
                if (wheels[i].collider == null || wheels[j].collider == null) continue;
                Vector3 a = transform.InverseTransformPoint(wheels[i].collider.transform.position);
                Vector3 b = transform.InverseTransformPoint(wheels[j].collider.transform.position);
                if (Mathf.Abs(a.z - b.z) < 0.3f && a.x * b.x < 0f)
                    axles.Add(new[] { wheels[i].collider, wheels[j].collider });
            }
    }

    static float Travel(WheelCollider w, out bool grounded)
    {
        WheelHit hit;
        grounded = w.GetGroundHit(out hit);
        if (!grounded) return 1f;
        return (-w.transform.InverseTransformPoint(hit.point).y - w.radius) / w.suspensionDistance;
    }

    void FixedUpdate()
    {
        Vector3 vel = rb.linearVelocity;
        float forwardSpeed = Vector3.Dot(vel, transform.forward);
        float speed = vel.magnitude;
        float motor = Throttle;
        float brake = Brake;

        // Pressing the opposite direction while moving = brake, not reverse
        if (Throttle * forwardSpeed < -0.5f) { brake = 1f; motor = 0f; }
        // No input = gentle engine braking so the car doesn't roll forever
        else if (Mathf.Approximately(Throttle, 0f)) brake = Mathf.Max(brake, 0.15f);

        // Engine power fades to zero at top speed
        float limit = (motor >= 0f ? topSpeedKmh : reverseSpeedKmh) / 3.6f;
        float ratio = Mathf.Clamp01(Mathf.Abs(forwardSpeed) / Mathf.Max(1f, limit));
        float powerScale = (motor * forwardSpeed > 0f) ? 1f - ratio * ratio : 1f;

        // Steering: smooth keyboard input, and a much smaller angle at high speed
        float target = Steer;
        bool easing = Mathf.Abs(target) < Mathf.Abs(smoothedSteer) || target * smoothedSteer < 0f;
        smoothedSteer = Mathf.MoveTowards(smoothedSteer, target, (easing ? steerReturnSpeed : steerSpeed) * Time.fixedDeltaTime);
        float fall = Mathf.Max(1f, steerFalloffKmh / 3.6f);
        float v = Mathf.Abs(forwardSpeed) / fall;
        float steerFactor = Mathf.Max(0.08f, 1f / (1f + v * v));
        float steerAngle = smoothedSteer * maxSteerAngle * steerFactor;

        Vector3 normalSum = Vector3.zero;
        int grounded = 0;
        foreach (var w in wheels)
        {
            if (w.collider == null) continue;
            if (w.powered) w.collider.motorTorque = motor * motorTorque * powerScale;
            w.collider.brakeTorque = brake * brakeTorque;
            if (w.steering) w.collider.steerAngle = steerAngle;

            WheelHit hit;
            if (w.collider.GetGroundHit(out hit)) { normalSum += hit.normal; grounded++; }
        }

        // Anti-roll bars
        foreach (var pair in axles)
        {
            bool gl, gr;
            float tl = Travel(pair[0], out gl);
            float tr = Travel(pair[1], out gr);
            float force = (tl - tr) * antiRoll;
            if (gl) rb.AddForceAtPosition(pair[0].transform.up * -force, pair[0].transform.position);
            if (gr) rb.AddForceAtPosition(pair[1].transform.up * force, pair[1].transform.position);
        }

        // Downforce grows with speed squared
        rb.AddForce(-transform.up * downforce * speed * speed);

        if (grounded >= 2)
        {
            Vector3 n = (normalSum / grounded).normalized;

            // Side grip: remove sideways sliding along the road
            Vector3 right = Vector3.ProjectOnPlane(transform.right, n).normalized;
            float lateral = Vector3.Dot(vel, right);
            rb.AddForce(-right * lateral * sideGrip, ForceMode.Acceleration);

            // Limit how fast a bump can throw the car away from the road
            float away = Vector3.Dot(vel, n);
            if (away > maxBounceSpeed)
                rb.linearVelocity = vel - n * (away - maxBounceSpeed);
        }
    }

    void Update()   // visuals follow the physics wheels
    {
        foreach (var w in wheels)
        {
            if (w.collider == null || w.visual == null) continue;
            w.collider.GetWorldPose(out Vector3 pos, out Quaternion rot);
            w.visual.SetPositionAndRotation(pos, rot);
        }
    }

    GUIStyle speedStyle, unitStyle;

    void OnGUI()
    {
        if (speedStyle == null)
        {
            speedStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 64,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.LowerRight
            };
            speedStyle.normal.textColor = Color.white;
            unitStyle = new GUIStyle(speedStyle) { fontSize = 22 };
        }

        float kmh = rb.linearVelocity.magnitude * 3.6f;
        float w = 260f, h = 100f;
        float x = Screen.width - w - 30f, y = Screen.height - h - 30f;

        // dark backing panel so it reads on any background
        Color old = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.55f);
        GUI.DrawTexture(new Rect(x - 10f, y - 5f, w + 20f, h + 10f), Texture2D.whiteTexture);
        GUI.color = old;

        // shadow, then text
        speedStyle.normal.textColor = Color.black;
        GUI.Label(new Rect(x + 2f, y + 2f, w - 90f, h), kmh.ToString("0"), speedStyle);
        speedStyle.normal.textColor = Color.white;
        GUI.Label(new Rect(x, y, w - 90f, h), kmh.ToString("0"), speedStyle);
        GUI.Label(new Rect(x + w - 85f, y - 4f, 85f, h), "km/h", unitStyle);
    }
}
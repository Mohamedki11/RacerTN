using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Drives a police car. It fills the same three inputs the player's keyboard does (Throttle, Steer, Brake).
/// It follows the real roads toward the target (using RouteGuide's road graph), slows for corners and
/// obstacles, reverses when stuck, and puts itself back on the road if it flips or stays stuck.
/// The police car prefab must NOT have CarInput, CarReset, CargoHealth or CrashFeedback.
/// </summary>
[RequireComponent(typeof(Rigidbody), typeof(CarController))]
public class PoliceAI : MonoBehaviour
{
    [Header("Driving")]
    [SerializeField] float maxSpeedKmh = 150f;
    [SerializeField] float cornerSpeedKmh = 35f;
    [SerializeField] float lookAheadMin = 8f;
    [SerializeField] float lookAheadPerMetreSecond = 0.5f;
    [Tooltip("Angle (degrees) to the next point that gives full steering.")]
    [SerializeField] float steerFullAngle = 35f;
    [SerializeField] float repathSeconds = 1f;
    [Tooltip("Closer than this to the target, it drives straight at it instead of following the roads.")]
    [SerializeField] float straightAtTargetDistance = 25f;

    public Transform Target { get; set; }
    public bool Chasing { get; set; }

    Rigidbody rb;
    CarController car;
    readonly List<Vector2> path = new List<Vector2>();
    readonly RaycastHit[] hits = new RaycastHit[8];
    bool hasPath, reversing;
    float repathTimer, stuckTimer, reverseTimer, flipTimer;
    int stuckCount;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        car = GetComponent<CarController>();
        repathTimer = Random.value * repathSeconds;     // cars do not all recalculate on the same frame
    }

    void Update()
    {
        if (GameState.Paused)
        {
            car.Throttle = 0f; car.Steer = 0f; car.Brake = 0f;
            return;
        }
        if (!Chasing || Target == null)
        {
            car.Throttle = 0f; car.Steer = 0f; car.Brake = 1f;      // stop
            return;
        }

        Vector3 pos = transform.position;
        Vector3 fwd = transform.forward;
        fwd.y = 0f;
        fwd.Normalize();
        float speed = Vector3.Dot(rb.linearVelocity, transform.forward) * 3.6f;   // km/h, negative = reversing

        // Find the way along the roads once per second
        repathTimer -= Time.deltaTime;
        if (repathTimer <= 0f)
        {
            repathTimer = repathSeconds;
            hasPath = RouteGuide.Instance != null && RouteGuide.Instance.GetRoute(pos, Target.position, path);
        }

        Vector3 aim, farAim;
        PickAim(pos, speed, out aim, out farAim);

        // Steering: turn toward the aim point
        Vector3 toAim = aim - pos; toAim.y = 0f;
        float angle = Vector3.SignedAngle(fwd, toAim, Vector3.up);        // positive = target is on the right
        float steer = Mathf.Clamp(angle / steerFullAngle, -1f, 1f);

        // Speed: slower for sharp turns coming up, and when something is ahead
        Vector3 toFar = farAim - pos; toFar.y = 0f;
        float curve = Mathf.Abs(Vector3.SignedAngle(fwd, toFar, Vector3.up));
        float targetSpeed = Mathf.Lerp(maxSpeedKmh, cornerSpeedKmh, Mathf.InverseLerp(12f, 75f, curve));
        targetSpeed = Mathf.Min(targetSpeed, Mathf.Lerp(maxSpeedKmh, cornerSpeedKmh, Mathf.InverseLerp(20f, 70f, Mathf.Abs(angle))));
        if (ObstacleAhead(pos, speed)) targetSpeed = Mathf.Min(targetSpeed, 20f);

        float throttle = Mathf.Clamp((targetSpeed - speed) / 12f, -1f, 1f);   // negative = brake
        if (speed < 5f && targetSpeed > 5f) throttle = 1f;

        // Stuck: pushing the gas but not moving -> reverse for a moment; stuck again and again -> put back on the road
        if (!reversing)
        {
            if (Mathf.Abs(speed) < 3f && throttle > 0.3f) stuckTimer += Time.deltaTime; else stuckTimer = 0f;
            if (speed > 15f) stuckCount = 0;
            if (stuckTimer > 2f)
            {
                stuckTimer = 0f;
                stuckCount++;
                if (stuckCount >= 3) { Recover(); stuckCount = 0; }
                else { reversing = true; reverseTimer = 1.3f; }
            }
        }
        if (reversing)
        {
            reverseTimer -= Time.deltaTime;
            throttle = -1f;
            steer = -steer;
            if (reverseTimer <= 0f) reversing = false;
        }

        // Flipped over
        if (Vector3.Dot(transform.up, Vector3.up) < 0.3f)
        {
            flipTimer += Time.deltaTime;
            if (flipTimer > 2f) { Recover(); flipTimer = 0f; }
        }
        else flipTimer = 0f;

        car.Throttle = throttle;
        car.Steer = steer;
        car.Brake = 0f;
    }

    // Next point to steer to (aim) and a point further ahead used to judge corners (farAim)
    void PickAim(Vector3 pos, float speedKmh, out Vector3 aim, out Vector3 farAim)
    {
        Vector3 tpos = Target.position;
        aim = farAim = tpos;
        Vector2 p2 = new Vector2(pos.x, pos.z);
        if (!hasPath || path.Count < 2 || Vector2.Distance(p2, new Vector2(tpos.x, tpos.z)) < straightAtTargetDistance) return;

        float look = lookAheadMin + Mathf.Max(0f, speedKmh / 3.6f) * lookAheadPerMetreSecond;
        bool gotAim = false;
        for (int i = 1; i < path.Count; i++)
        {
            float d = Vector2.Distance(path[i], p2);
            if (!gotAim && d > look) { aim = new Vector3(path[i].x, pos.y, path[i].y); gotAim = true; }
            if (d > look * 2.5f) { farAim = new Vector3(path[i].x, pos.y, path[i].y); return; }
        }
        if (!gotAim) aim = tpos;
        farAim = tpos;
    }

    bool ObstacleAhead(Vector3 pos, float speedKmh)
    {
        float dist = 8f + Mathf.Max(0f, speedKmh) * 0.12f;
        int n = Physics.SphereCastNonAlloc(pos + Vector3.up * 0.7f, 0.8f, transform.forward, hits, dist, ~0, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < n; i++)
        {
            Collider c = hits[i].collider;
            if (c is TerrainCollider) continue;                          // hills are not obstacles
            if (c.attachedRigidbody == rb) continue;                     // ourselves
            if (Target != null && c.transform.root == Target.root) continue;   // the player: we want to catch him
            return true;
        }
        return false;
    }

    /// <summary>Puts the car back on the nearest road, facing the target.</summary>
    void Recover()
    {
        Vector3 p = transform.position;
        if (RouteGuide.Instance != null) RouteGuide.Instance.NearestRoadPoint(p, out p);
        Vector3 dir = Target != null ? Target.position - p : transform.forward;
        dir.y = 0f;
        float yaw = dir.sqrMagnitude > 0.01f ? Quaternion.LookRotation(dir).eulerAngles.y : transform.eulerAngles.y;
        Place(p, yaw);
    }

    /// <summary>Places the car on the ground under 'position', facing 'yaw' degrees, upright and stopped.</summary>
    public void Place(Vector3 position, float yaw)
    {
        if (rb == null) rb = GetComponent<Rigidbody>();
        Vector3 ground = position;
        var all = Physics.RaycastAll(new Vector3(position.x, 1000f, position.z), Vector3.down, 2000f, ~0, QueryTriggerInteraction.Ignore);
        float best = float.MaxValue;
        foreach (var h in all)
        {
            if (h.collider.attachedRigidbody == rb) continue;
            if (h.distance < best) { best = h.distance; ground = h.point; }
        }

        Vector3 pos = ground + Vector3.up * 0.8f;
        Quaternion rot = Quaternion.Euler(0f, yaw, 0f);
        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
        rb.position = pos;
        rb.rotation = rot;
        transform.SetPositionAndRotation(pos, rot);
        Physics.SyncTransforms();
        reversing = false;
        stuckTimer = 0f;
    }
}

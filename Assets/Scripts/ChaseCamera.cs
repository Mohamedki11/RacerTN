using UnityEngine;

public class ChaseCamera : MonoBehaviour
{
    [SerializeField] Transform target;
    [SerializeField] float distance = 9f;
    [SerializeField] float height = 3.5f;
    [SerializeField] float lookAhead = 5f;
    [SerializeField] float headingSpeed = 2.5f;   // how fast the camera swings behind the car
    [SerializeField] float positionSpeed = 8f;
    [SerializeField] float baseFov = 60f;
    [SerializeField] float maxFov = 78f;

    Rigidbody rb;
    Camera cam;
    Vector3 heading = Vector3.forward;

    void Start()
    {
        cam = GetComponent<Camera>();
        if (target == null)
        {
            var car = FindFirstObjectByType<CarController>();
            if (car != null) target = car.transform;
        }
        if (target == null) return;

        rb = target.GetComponent<Rigidbody>();
        heading = Flat(target.forward, Vector3.forward);
        transform.position = target.position - heading * distance + Vector3.up * height;
        transform.rotation = Quaternion.LookRotation(target.position + Vector3.up * 1.2f - transform.position);
    }

    void LateUpdate()
    {
        if (target == null) return;

        Vector3 carDir = Flat(target.forward, heading);
        Vector3 desired = carDir;

        // At speed, lean towards the real travel direction (feels natural in drifts)
        if (rb != null && rb.linearVelocity.magnitude > 6f)
        {
            Vector3 velDir = Flat(rb.linearVelocity, carDir);
            if (Vector3.Dot(velDir, carDir) > 0.3f)        // moving forward, not reversing
                desired = Vector3.Slerp(carDir, velDir, 0.5f);
        }

        heading = Vector3.Slerp(heading, desired, 1f - Mathf.Exp(-headingSpeed * Time.deltaTime));
        heading = Flat(heading, carDir);

        Vector3 wantedPos = target.position - heading * distance + Vector3.up * height;
        transform.position = Vector3.Lerp(transform.position, wantedPos, 1f - Mathf.Exp(-positionSpeed * Time.deltaTime));

        Vector3 lookPoint = target.position + heading * lookAhead + Vector3.up * 1.2f;
        Quaternion wantedRot = Quaternion.LookRotation(lookPoint - transform.position);
        transform.rotation = Quaternion.Slerp(transform.rotation, wantedRot, 1f - Mathf.Exp(-12f * Time.deltaTime));

        if (cam != null && rb != null)   // wider view at high speed
        {
            float t = Mathf.InverseLerp(0f, 50f, rb.linearVelocity.magnitude);
            cam.fieldOfView = Mathf.Lerp(cam.fieldOfView, Mathf.Lerp(baseFov, maxFov, t), 3f * Time.deltaTime);
        }
    }

    static Vector3 Flat(Vector3 v, Vector3 fallback)
    {
        v.y = 0f;
        return v.sqrMagnitude > 0.0001f ? v.normalized : fallback;
    }
}
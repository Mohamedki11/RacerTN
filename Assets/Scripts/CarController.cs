using UnityEngine;

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

    [SerializeField] Wheel[] wheels;
    [SerializeField] Transform centerOfMass;
    [SerializeField] float motorTorque = 1500f;
    [SerializeField] float brakeTorque = 3000f;
    [SerializeField] float maxSteerAngle = 30f;

    [SerializeField] float highSpeedSteerFactor = 0.35f; // steering left at top speed

    // Set by CarInput now, and by AI later
    public float Throttle;
    public float Steer;
    public float Brake;

    Rigidbody rb;
    float smoothedSteer;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        if (centerOfMass != null) rb.centerOfMass = centerOfMass.localPosition;
    }

    void FixedUpdate()
{
    float forwardSpeed = Vector3.Dot(rb.linearVelocity, transform.forward);
    float motor = Throttle;
    float brake = Brake;

    // Pressing the opposite direction while moving = brake, not reverse
    if (Throttle * forwardSpeed < -0.5f) { brake = 1f; motor = 0f; }
    // No input = gentle engine braking so the car doesn't roll forever
    else if (Mathf.Approximately(Throttle, 0f)) brake = Mathf.Max(brake, 0.15f);

    // Smooth the keyboard steering and reduce it as speed increases
    smoothedSteer = Mathf.MoveTowards(smoothedSteer, Steer, 4f * Time.fixedDeltaTime);
    float speedFactor = Mathf.InverseLerp(0f, 40f, Mathf.Abs(forwardSpeed)); // 40 m/s = 144 km/h
    float steerAngle = smoothedSteer * Mathf.Lerp(maxSteerAngle, maxSteerAngle * highSpeedSteerFactor, speedFactor);

    foreach (var w in wheels)
    {
        if (w.powered) w.collider.motorTorque = motor * motorTorque;
        w.collider.brakeTorque = brake * brakeTorque;
        if (w.steering) w.collider.steerAngle = steerAngle;
    }
}

    void Update()   // visuals follow the physics wheels
    {
        foreach (var w in wheels)
        {
            w.collider.GetWorldPose(out Vector3 pos, out Quaternion rot);
            w.visual.SetPositionAndRotation(pos, rot);
        }
    }

    void OnGUI()
{
    GUI.Label(new Rect(10, 10, 300, 30),
        $"Speed: {rb.linearVelocity.magnitude * 3.6f:0} km/h");
}

}
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

    // Set by CarInput now, and by AI later
    public float Throttle;
    public float Steer;
    public float Brake;

    Rigidbody rb;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        if (centerOfMass != null) rb.centerOfMass = centerOfMass.localPosition;
    }

    void FixedUpdate()   // physics runs here
    {
        foreach (var w in wheels)
        {
            if (w.powered) w.collider.motorTorque = Throttle * motorTorque;
            w.collider.brakeTorque = Brake * brakeTorque;
            if (w.steering) w.collider.steerAngle = Steer * maxSteerAngle;
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
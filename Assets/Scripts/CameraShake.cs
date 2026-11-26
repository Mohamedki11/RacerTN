using UnityEngine;

/// <summary>
/// Put on the Main Camera (next to ChaseCamera). CameraShake.Add(0..1) shakes the view.
/// It runs after the chase camera and undoes itself before the next frame, so the chase camera never notices.
/// </summary>
[DefaultExecutionOrder(1000)]
public class CameraShake : MonoBehaviour
{
    static CameraShake instance;

    [SerializeField] float maxOffset = 0.35f;     // metres
    [SerializeField] float maxAngle = 3f;         // degrees
    [SerializeField] float frequency = 25f;
    [SerializeField] float decayPerSecond = 1.6f;

    float trauma;
    Vector3 savedPos;
    Quaternion savedRot;
    bool applied;

    void Awake() => instance = this;

    void OnDestroy()
    {
        if (instance == this) instance = null;
    }

    public static void Add(float amount)
    {
        if (instance != null) instance.trauma = Mathf.Clamp01(instance.trauma + amount);
    }

    // Update runs before every LateUpdate: give the chase camera back its untouched position
    void Update()
    {
        if (!applied) return;
        transform.SetPositionAndRotation(savedPos, savedRot);
        applied = false;
    }

    void LateUpdate()
    {
        if (GameState.Paused) { trauma = 0f; return; }
        trauma = Mathf.MoveTowards(trauma, 0f, decayPerSecond * Time.deltaTime);
        if (trauma <= 0.001f) return;

        savedPos = transform.position;
        savedRot = transform.rotation;
        applied = true;

        float s = trauma * trauma;                 // small shakes stay small, big ones hit hard
        float t = Time.time * frequency;
        Vector3 offset = new Vector3(N(t, 0f), N(t, 10f), N(t, 20f)) * (maxOffset * s);
        Quaternion tilt = Quaternion.Euler(N(t, 30f) * maxAngle * s, N(t, 40f) * maxAngle * s, N(t, 50f) * maxAngle * s);

        transform.position += transform.rotation * offset;
        transform.rotation = transform.rotation * tilt;
    }

    static float N(float t, float seed) => Mathf.PerlinNoise(t, seed) * 2f - 1f;
}

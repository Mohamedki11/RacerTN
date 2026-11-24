using UnityEngine;

// Engine sound that follows the car like a real one: RPM rises with speed, drops when the
// car "shifts gear", revs up with the throttle, and gets quieter when you lift off.
//
// Setup: add this to the Car object (next to CarController), drag in your engine loop(s).
//   - Only one engine sound?  Put it in "Mid Loop". The pitch alone will make it rise and fall.
//   - Better: also add a "Low Loop" (idle / low rpm) and a "High Loop" (high rpm). They crossfade.
// All clips must be seamless loops. Tip: in each clip's import settings use
// Load Type = Decompress On Load, and keep Force To Mono ticked.
[RequireComponent(typeof(Rigidbody))]
public class CarEngineSound : MonoBehaviour
{
    [Header("Engine loops (seamless)")]
    [SerializeField] AudioClip lowLoop;    // optional: idle / low rpm
    [SerializeField] AudioClip midLoop;    // required
    [SerializeField] AudioClip highLoop;   // optional: high rpm

    [Header("Car")]
    [SerializeField] float topSpeedKmh = 220f;   // keep equal to CarController's top speed
    [SerializeField] int gears = 6;
    [SerializeField] float idleRpm = 0.12f;      // 0..1
    [SerializeField] float upshiftRpm = 0.92f;
    [SerializeField] float downshiftSpeedFactor = 0.7f;

    [Header("Sound")]
    [SerializeField] float volume = 0.8f;
    [SerializeField] float offThrottleVolume = 0.45f;   // how loud it is when you let go of the gas
    [SerializeField] Vector2 pitchSingle = new Vector2(0.6f, 1.9f);    // used when only Mid Loop is set
    [SerializeField] Vector2 pitchLayered = new Vector2(0.85f, 1.4f);  // used with Low / High loops
    [SerializeField] float rpmRiseSpeed = 5f;
    [SerializeField] float rpmFallSpeed = 3f;
    [SerializeField] float loopFade = 0.3f;     // seconds of overlap that hide the loop point

    Rigidbody rb;
    CarController car;
    CrossLoop low, mid, high;
    int gear;
    float rpm, throttle;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        car = GetComponent<CarController>();
        mid = MakeLoop("Engine_Mid", midLoop);
        low = MakeLoop("Engine_Low", lowLoop);
        high = MakeLoop("Engine_High", highLoop);
        rpm = idleRpm;
    }

    CrossLoop MakeLoop(string name, AudioClip clip)
    {
        if (clip == null) return null;
        return new CrossLoop(transform, name, clip, loopFade);
    }

    void Update()
    {
        if (mid == null) return;

        float kmh = Mathf.Abs(Vector3.Dot(rb.linearVelocity, transform.forward)) * 3.6f;
        float input = car != null ? Mathf.Abs(car.Throttle) : 0f;
        throttle = Mathf.MoveTowards(throttle, input, 4f * Time.deltaTime);

        // Automatic gearbox: each gear covers a slice of the top speed
        int n = Mathf.Max(1, gears);
        gear = Mathf.Clamp(gear, 0, n - 1);
        float gearTop = topSpeedKmh * (gear + 1f) / n;
        float speedRpm = Mathf.Clamp01(kmh / gearTop);
        if (speedRpm > upshiftRpm && gear < n - 1) gear++;
        else if (gear > 0 && kmh < topSpeedKmh * gear / n * downshiftSpeedFactor) gear--;
        gearTop = topSpeedKmh * (gear + 1f) / n;
        speedRpm = Mathf.Clamp01(kmh / gearTop);

        // Target rpm: from the road speed, but the throttle can rev it up when slow (like a clutch slipping)
        float target = Mathf.Max(idleRpm + speedRpm * (1f - idleRpm), idleRpm + throttle * 0.45f * (1f - speedRpm));
        float rate = target > rpm ? rpmRiseSpeed : rpmFallSpeed;
        rpm = Mathf.Lerp(rpm, target, 1f - Mathf.Exp(-rate * Time.deltaTime));

        // Crossfade the layers (equal power so the loudness stays even)
        float lowW = 1f - Mathf.SmoothStep(0f, 0.5f, rpm);
        float highW = Mathf.SmoothStep(0.5f, 1f, rpm);
        float midW = Mathf.Clamp01(1f - lowW - highW);
        if (low == null) { midW += lowW; lowW = 0f; }
        if (high == null) { midW += highW; highW = 0f; }

        float vol = volume * Mathf.Lerp(offThrottleVolume, 1f, throttle);
        Vector2 range = (low != null || high != null) ? pitchLayered : pitchSingle;
        float pitch = Mathf.Lerp(range.x, range.y, rpm);

        Apply(mid, Mathf.Sqrt(midW) * vol, pitch);
        Apply(low, Mathf.Sqrt(lowW) * vol, pitch);
        Apply(high, Mathf.Sqrt(highW) * vol, pitch);
    }

    static void Apply(CrossLoop l, float vol, float pitch)
    {
        if (l != null) l.Update(vol, pitch);
    }
}

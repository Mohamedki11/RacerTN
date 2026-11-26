using UnityEngine;

/// <summary>
/// Put on the player's Car. Hard crashes shake the camera and flash the screen edge red.
/// Crashes also raise the police heat a little.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class CrashFeedback : MonoBehaviour
{
    [SerializeField] float minImpactSpeed = 6f;      // m/s: slower bumps do nothing
    [SerializeField] float maxImpactSpeed = 25f;     // m/s: this is the biggest effect
    [SerializeField, Range(0f, 1f)] float groundHitFactor = 0.25f;   // landings count less than walls
    [SerializeField] float cooldown = 0.25f;

    [Header("Red screen flash")]
    [Tooltip("Only crashes at least this strong (0 to 1) flash the screen red. Higher = rarer.")]
    [SerializeField, Range(0f, 1f)] float flashThreshold = 0.35f;
    [Tooltip("Tick to flash red only while a mission is running.")]
    [SerializeField] bool flashOnlyInMissions = true;
    [SerializeField] float heatPerCrash = 0.2f;

    float last = -10f;

    void OnCollisionEnter(Collision c)
    {
        if (Time.time - last < cooldown) return;
        float speed = c.relativeVelocity.magnitude;
        if (speed < minImpactSpeed) return;

        float k = Mathf.InverseLerp(minImpactSpeed, maxImpactSpeed, speed);
        if (c.contactCount > 0 && c.GetContact(0).normal.y > 0.85f) k *= groundHitFactor;
        if (k < 0.02f) return;
        last = Time.time;

        CameraShake.Add(0.2f + 0.7f * k);

        bool inMission = MissionManager.Instance != null && MissionManager.Instance.State == MissionState.Active;
        if (k >= flashThreshold && (!flashOnlyInMissions || inMission))
            ScreenEdgeEffect.Flash(Mathf.Lerp(0.25f, 0.85f, Mathf.InverseLerp(flashThreshold, 1f, k)));
        if (PoliceDirector.Instance != null) PoliceDirector.Instance.AddHeat(heatPerCrash * k);
    }
}

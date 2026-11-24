using UnityEngine;

/// <summary>
/// Put on the Car. While a mission is active, hard crashes damage the cargo.
/// Health goes from 1 (perfect) to 0 (destroyed = mission failed).
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class CargoHealth : MonoBehaviour
{
    [Tooltip("Crashes slower than this (m/s) do nothing. 4 m/s = about 14 km/h.")]
    [SerializeField] float minImpactSpeed = 4f;
    [Tooltip("Health lost per m/s above the minimum (1 = full health). 0.035 means a 20 m/s wall hit costs about 55%.")]
    [SerializeField] float damagePerMetre = 0.035f;
    [Tooltip("Landings on flat ground hurt less than hitting walls.")]
    [SerializeField, Range(0f, 1f)] float groundHitFactor = 0.3f;
    [SerializeField] float hitCooldown = 0.3f;

    public float Health { get; private set; } = 1f;     // 0..1
    public bool Active { get; private set; }

    float fragility = 1f;
    float lastHit = -10f;

    public void Begin(float cargoFragility)
    {
        fragility = cargoFragility;
        Health = 1f;
        Active = true;
    }

    public void End() => Active = false;

    void OnCollisionEnter(Collision c)
    {
        if (!Active || Time.time - lastHit < hitCooldown) return;

        float speed = c.relativeVelocity.magnitude;
        if (speed < minImpactSpeed) return;

        float damage = (speed - minImpactSpeed) * damagePerMetre * fragility;
        if (c.contactCount > 0 && c.GetContact(0).normal.y > 0.85f) damage *= groundHitFactor;

        lastHit = Time.time;
        Health = Mathf.Max(0f, Health - damage);
    }
}

using System;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Put on the Car (same object as the Rigidbody).
/// R = put the car back upright on the ground where it is, keeping its heading.
/// Missions also use TeleportTo() to move the car to a pickup point.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class CarReset : MonoBehaviour
{
    [Tooltip("Ray starts this far above the car and goes down to find the ground.")]
    [SerializeField] float rayStartHeight = 4f;
    [Tooltip("Car is placed this far above the ground so the wheels can settle.")]
    [SerializeField] float dropHeight = 0.8f;

    Rigidbody rb;

    void Awake() => rb = GetComponent<Rigidbody>();

    void Update()
    {
        var k = Keyboard.current;
        if (k == null || GameState.Paused) return;
        if (k.rKey.wasPressedThisFrame)
            TeleportTo(transform.position, transform.eulerAngles.y);
    }

    /// <summary>Places the car on the ground under 'position', facing 'yaw' degrees, upright and stopped.</summary>
    public void TeleportTo(Vector3 position, float yaw)
    {
        Vector3 ground;
        if (!FindGround(position + Vector3.up * rayStartHeight, rayStartHeight + 50f, out ground) &&
            !FindGround(position + Vector3.up * 500f, 1000f, out ground))
            ground = position;                       // nothing found: just use the given position

        Vector3 pos = ground + Vector3.up * dropHeight;
        Quaternion rot = Quaternion.Euler(0f, yaw, 0f);

        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
        rb.position = pos;
        rb.rotation = rot;
        transform.SetPositionAndRotation(pos, rot);
        Physics.SyncTransforms();
    }

    bool FindGround(Vector3 origin, float distance, out Vector3 point)
    {
        RaycastHit[] hits = Physics.RaycastAll(origin, Vector3.down, distance,
                                               ~0, QueryTriggerInteraction.Ignore);
        Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
        foreach (var h in hits)
        {
            if (h.collider.attachedRigidbody == rb) continue;   // ignore the car itself
            point = h.point;
            return true;
        }
        point = default;
        return false;
    }
}

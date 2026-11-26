using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Put on the Car (same object as the Rigidbody).
/// R = put the car back upright where it is, keeping its heading.
/// R again within a few seconds (you are stuck) = move the car to the nearest ROAD, facing along the route.
/// Missions also use TeleportTo() to move the car to a pickup point.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class CarReset : MonoBehaviour
{
    [Tooltip("Ray starts this far above the car and goes down to find the ground.")]
    [SerializeField] float rayStartHeight = 4f;
    [Tooltip("Car is placed this far above the ground so the wheels can settle.")]
    [SerializeField] float dropHeight = 0.8f;

    [Header("When you are stuck")]
    [Tooltip("Pressing R again within this many seconds sends the car to the nearest road.")]
    [SerializeField] float stuckWindow = 6f;
    [Tooltip("Tick to make every R press go to the nearest road.")]
    [SerializeField] bool alwaysResetToRoad = false;
    [Tooltip("Road resets only happen if a road is closer than this (metres).")]
    [SerializeField] float roadMaxDistance = 400f;

    Rigidbody rb;
    float lastReset = -100f;
    readonly List<Vector2> route = new List<Vector2>();

    void Awake() => rb = GetComponent<Rigidbody>();

    void Update()
    {
        var k = Keyboard.current;
        if (k == null || GameState.Paused) return;
        if (!k.rKey.wasPressedThisFrame) return;

        bool again = Time.unscaledTime - lastReset < stuckWindow;
        lastReset = Time.unscaledTime;

        if (alwaysResetToRoad || again) ResetToRoad();
        else TeleportTo(transform.position, transform.eulerAngles.y);
    }

    /// <summary>Moves the car to the nearest road point, facing the way to the mission drop-off if there is one.</summary>
    public void ResetToRoad()
    {
        Vector3 here = transform.position;
        Vector3 road;
        if (RouteGuide.Instance != null && RouteGuide.Instance.NearestRoadPoint(here, out road) &&
            new Vector2(road.x - here.x, road.z - here.z).magnitude <= roadMaxDistance)
        {
            TeleportTo(road, HeadingAlongRoute(road, transform.eulerAngles.y), true);
            return;
        }
        TeleportTo(here, transform.eulerAngles.y);           // no road known: just reset in place
    }

    // Direction of the first road piece toward the destination (or the old heading if there is no mission)
    float HeadingAlongRoute(Vector3 from, float fallbackYaw)
    {
        var m = MissionManager.Instance;
        if (RouteGuide.Instance == null || m == null || m.State != MissionState.Active || m.Destination == null)
            return fallbackYaw;
        if (!RouteGuide.Instance.GetRoute(from, m.Destination.position, route)) return fallbackYaw;

        Vector2 origin = new Vector2(from.x, from.z);
        for (int i = 1; i < route.Count; i++)
        {
            Vector2 d = route[i] - origin;
            if (d.magnitude > 8f) return Mathf.Atan2(d.x, d.y) * Mathf.Rad2Deg;
        }
        return fallbackYaw;
    }

    /// <summary>Places the car on the ground under 'position', facing 'yaw' degrees, upright and stopped.
    /// fromSky = look for the ground from far above (use it when the position may be inside something).</summary>
    public void TeleportTo(Vector3 position, float yaw, bool fromSky = false)
    {
        Vector3 ground = position;
        bool found = false;
        if (!fromSky) found = FindGround(position + Vector3.up * rayStartHeight, rayStartHeight + 50f, out ground);
        if (!found) found = FindGround(position + Vector3.up * 500f, 1000f, out ground);
        if (!found) ground = position;                       // nothing found: just use the given position

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

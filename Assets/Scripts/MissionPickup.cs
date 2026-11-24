using System;
using UnityEngine;

/// <summary>One line of dialogue: who says it, the subtitle, and an optional voice recording.</summary>
[Serializable]
public class MissionLine
{
    public string speaker = "Kamel";
    [TextArea(1, 3)] public string text;
    [Tooltip("Optional voice recording. If set, the subtitle stays as long as the clip lasts.")]
    public AudioClip voice;
}

/// <summary>
/// One mission = one pickup marker in the world. Put it on an empty GameObject that has a
/// Collider (it is set to Trigger automatically) and a glowing marker as a child.
/// Assign the "dropoff" object (another marker somewhere else on the map).
/// </summary>
[RequireComponent(typeof(Collider))]
public class MissionPickup : MonoBehaviour
{
    public string title = "Delivery";
    [TextArea(1, 3)] public string description = "";
    [Tooltip("Seconds you have to reach the drop-off.")]
    public float timeLimit = 90f;
    [Tooltip("Money (DT) for a perfect delivery. Damaged cargo pays 50% to 100% of this.")]
    public int reward = 100;
    [Tooltip("The drop-off marker object (starts hidden, shown while the mission runs).")]
    public Transform dropoff;
    [Tooltip("How close (metres) you must get to the drop-off to win.")]
    public float dropoffRadius = 10f;

    [Header("Cargo")]
    public string cargoName = "Cargo";
    [Tooltip("1 = normal. 2 = the cargo breaks twice as easily. 0.5 = tough.")]
    [Range(0.2f, 3f)] public float fragility = 1f;

    [Header("Story and voice lines (all optional)")]
    public MissionLine[] briefing;        // plays when the mission starts
    public MissionLine cargoWarning;      // plays once when the cargo gets low
    public MissionLine[] onSuccess;
    public MissionLine[] onFail;

    Collider trigger;

    void Awake()
    {
        trigger = GetComponent<Collider>();
        trigger.isTrigger = true;
    }

    void OnTriggerEnter(Collider other)
    {
        if (other.GetComponentInParent<CarController>() == null) return;
        if (MissionManager.Instance != null) MissionManager.Instance.SetNearby(this);
    }

    void OnTriggerExit(Collider other)
    {
        if (other.GetComponentInParent<CarController>() == null) return;
        if (MissionManager.Instance != null) MissionManager.Instance.ClearNearby(this);
    }

    /// <summary>Show / hide the pickup marker and enable / disable its trigger.</summary>
    public void SetAvailable(bool available)
    {
        if (trigger == null) trigger = GetComponent<Collider>();
        trigger.enabled = available;
        foreach (var r in GetComponentsInChildren<Renderer>()) r.enabled = available;
        if (!available && MissionManager.Instance != null) MissionManager.Instance.ClearNearby(this);
    }

    public void SetDropoffVisible(bool visible)
    {
        if (dropoff != null) dropoff.gameObject.SetActive(visible);
    }

    // Helps you place missions: green = pickup, yellow = line + win radius at the drop-off.
    void OnDrawGizmos()
    {
        Gizmos.color = Color.green;
        Gizmos.DrawWireSphere(transform.position, 3f);
        if (dropoff == null) return;
        Gizmos.color = Color.yellow;
        Gizmos.DrawLine(transform.position, dropoff.position);
        Gizmos.DrawWireSphere(dropoff.position, dropoffRadius);
    }
}

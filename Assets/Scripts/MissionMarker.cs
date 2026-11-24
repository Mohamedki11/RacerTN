using UnityEngine;

/// <summary>
/// Makes a marker (glowing cylinder) slowly spin and pulse. Put it on the VISUAL object
/// (the cylinder), not on the object that holds the trigger collider.
/// </summary>
public class MissionMarker : MonoBehaviour
{
    [SerializeField] float spinSpeed = 45f;
    [SerializeField] float pulseAmount = 0.08f;
    [SerializeField] float pulseSpeed = 2.5f;

    Vector3 baseScale;

    void Awake() => baseScale = transform.localScale;

    void Update()
    {
        transform.Rotate(0f, spinSpeed * Time.deltaTime, 0f, Space.World);
        float s = 1f + Mathf.Sin(Time.time * pulseSpeed) * pulseAmount;
        transform.localScale = new Vector3(baseScale.x * s, baseScale.y, baseScale.z * s);
    }
}

using UnityEngine;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Top-down camera that renders into a small RenderTexture a few times per second.
/// Rotates with the car (car always points up), zooms out with speed,
/// and moves the car arrow + destination icon.
/// Put on a NEW camera object called "MinimapCamera" (not tagged MainCamera, no AudioListener).
/// </summary>
[RequireComponent(typeof(Camera))]
public class MinimapCamera : MonoBehaviour
{
    [Header("What to follow")]
    [SerializeField] Transform target;                 // the Car
    [SerializeField] Rigidbody targetBody;             // the Car's Rigidbody (for speed zoom)

    [Header("Quality / cost")]
    [SerializeField] int resolution = 256;
    [SerializeField] float updatesPerSecond = 10f;
    [Tooltip("Which layers the minimap draws. Use MapGround + Minimap only.")]
    [SerializeField] LayerMask visibleLayers = ~0;

    [Header("Zoom (half-height of the view in metres)")]
    [SerializeField] float minSize = 80f;
    [SerializeField] float maxSize = 200f;
    [SerializeField] float maxSpeedKmh = 160f;
    [SerializeField] float height = 300f;

    [Header("Icons (sprites on the Minimap layer, NOT children of the car)")]
    [SerializeField] Transform carIcon;
    [SerializeField] Transform destinationIcon;
    [Tooltip("Icon size as a fraction of the map height.")]
    [SerializeField] float iconSize = 0.08f;
    [Tooltip("How close to the edge the destination icon is clamped (0-1).")]
    [SerializeField] float edgeLimit = 0.88f;

    Camera cam;
    RenderTexture rt;
    float nextRender;

    public RenderTexture Texture => rt;

    void Awake()
    {
        cam = GetComponent<Camera>();
        rt = new RenderTexture(resolution, resolution, 16) { name = "MinimapRT" };
        rt.Create();

        cam.targetTexture = rt;
        cam.orthographic = true;
        cam.orthographicSize = minSize;
        cam.nearClipPlane = 1f;
        cam.farClipPlane = height + 200f;
        cam.cullingMask = visibleLayers;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.05f, 0.08f, 0.1f);
        cam.enabled = false;                           // we render it manually (cheaper)

        var data = cam.GetUniversalAdditionalCameraData();
        data.renderPostProcessing = false;
        data.renderShadows = false;
    }

    void OnDestroy()
    {
        if (rt != null) rt.Release();
    }

    void LateUpdate()
    {
        if (target == null) return;

        // Zoom out with speed
        float kmh = targetBody != null ? targetBody.linearVelocity.magnitude * 3.6f : 0f;
        float wanted = Mathf.Lerp(minSize, maxSize, Mathf.InverseLerp(0f, maxSpeedKmh, kmh));
        cam.orthographicSize = Mathf.Lerp(cam.orthographicSize, wanted, 2f * Time.deltaTime);

        // Follow + rotate with the car
        float yaw = target.eulerAngles.y;
        Quaternion flat = Quaternion.Euler(90f, yaw, 0f);          // faces up when used on a sprite
        transform.SetPositionAndRotation(target.position + Vector3.up * height, flat);

        float iconY = height - 20f;
        float diameter = cam.orthographicSize * 2f * iconSize;

        if (carIcon != null)
        {
            carIcon.SetPositionAndRotation(target.position + Vector3.up * iconY, flat);
            SetIconSize(carIcon, diameter);
        }

        UpdateDestinationIcon(yaw, flat, iconY, diameter);

        // Render the map only a few times per second
        if (!GameState.Paused && Time.unscaledTime >= nextRender)
        {
            nextRender = Time.unscaledTime + 1f / Mathf.Max(1f, updatesPerSecond);
            cam.Render();
        }
    }

    void UpdateDestinationIcon(float yaw, Quaternion flat, float iconY, float diameter)
    {
        if (destinationIcon == null) return;

        var m = MissionManager.Instance;
        bool show = m != null && m.State == MissionState.Active && m.Destination != null;
        destinationIcon.gameObject.SetActive(show);
        if (!show) return;

        Vector3 fwd = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
        Vector3 right = Quaternion.Euler(0f, yaw, 0f) * Vector3.right;
        Vector3 off = m.Destination.position - target.position;
        off.y = 0f;

        Vector2 p = new Vector2(Vector3.Dot(off, right), Vector3.Dot(off, fwd));
        float limit = cam.orthographicSize * edgeLimit;
        float biggest = Mathf.Max(Mathf.Abs(p.x), Mathf.Abs(p.y));
        if (biggest > limit) p *= limit / biggest;                 // keep it inside the map edge

        destinationIcon.SetPositionAndRotation(
            target.position + right * p.x + fwd * p.y + Vector3.up * (iconY - 5f), flat);
        SetIconSize(destinationIcon, diameter);
    }

    static void SetIconSize(Transform icon, float worldDiameter)
    {
        var sr = icon.GetComponent<SpriteRenderer>();
        float baseSize = (sr != null && sr.sprite != null) ? sr.sprite.bounds.size.x : 1f;
        icon.localScale = Vector3.one * (worldDiameter / baseSize);
    }
}

using UnityEngine;

/// <summary>
/// A tall beam of light you can see from far away. Put it on a pickup or drop-off object.
/// It appears and disappears together with that object. Built from a LineRenderer, no assets needed.
/// </summary>
public class MissionBeam : MonoBehaviour
{
    public Color color = new Color(1f, 0.8f, 0.15f, 1f);
    public float height = 400f;
    public float width = 5f;
    [Range(0f, 0.5f)] public float pulse = 0.15f;

    LineRenderer line;
    Material mat;

    void Awake()
    {
        line = gameObject.AddComponent<LineRenderer>();
        line.positionCount = 2;
        line.useWorldSpace = true;
        line.alignment = LineAlignment.View;           // always faces the camera
        line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        line.receiveShadows = false;

        Shader sh = Shader.Find("Sprites/Default");    // simple see-through, unlit
        if (sh == null) sh = Shader.Find("Universal Render Pipeline/Unlit");
        mat = new Material(sh);
        line.sharedMaterial = mat;

        var g = new Gradient();
        g.SetKeys(new[] { new GradientColorKey(color, 0f), new GradientColorKey(color, 1f) },
                  new[] { new GradientAlphaKey(0.85f, 0f), new GradientAlphaKey(0f, 1f) });   // fades out upward
        line.colorGradient = g;
        Place();
    }

    void LateUpdate()
    {
        Place();
        line.widthMultiplier = width * (1f + Mathf.Sin(Time.time * 2.5f) * pulse);
    }

    void Place()
    {
        Vector3 p = transform.position;
        line.SetPosition(0, p);
        line.SetPosition(1, p + Vector3.up * height);
    }

    void OnDestroy()
    {
        if (mat != null) Destroy(mat);
    }
}

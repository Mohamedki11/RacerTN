using UnityEngine;

/// <summary>
/// Red edge of the screen: flashes on hard crashes and pulses when mission time is almost out.
/// Put on the Systems object. The vignette texture is made in code.
/// </summary>
public class ScreenEdgeEffect : MonoBehaviour
{
    static ScreenEdgeEffect instance;

    [SerializeField] float flashFadePerSecond = 1.5f;
    [SerializeField] float lowTimeSeconds = 10f;

    Texture2D tex;
    float flash;

    void Awake()
    {
        instance = this;
        tex = MakeVignette();
    }

    void OnDestroy()
    {
        if (instance == this) instance = null;
        if (tex != null) Destroy(tex);
    }

    public static void Flash(float strength)
    {
        if (instance != null) instance.flash = Mathf.Max(instance.flash, Mathf.Clamp01(strength));
    }

    void Update()
    {
        flash = Mathf.MoveTowards(flash, 0f, flashFadePerSecond * Time.unscaledDeltaTime);
    }

    void OnGUI()
    {
        if (Event.current.type != EventType.Repaint) return;

        float a = flash;
        var m = MissionManager.Instance;
        if (m != null && m.State == MissionState.Active && m.TimeLeft < lowTimeSeconds)
            a = Mathf.Max(a, 0.2f + 0.2f * (0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 7f)));
        if (a <= 0.01f) return;

        GUI.depth = 20;                                  // behind the HUD text
        Color old = GUI.color;
        GUI.color = new Color(1f, 1f, 1f, Mathf.Clamp01(a));
        GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), tex, ScaleMode.StretchToFill, true);
        GUI.color = old;
    }

    static Texture2D MakeVignette()
    {
        const int N = 128;
        var t = new Texture2D(N, N, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                float dx = (x + 0.5f) / N * 2f - 1f, dy = (y + 0.5f) / N * 2f - 1f;
                float d = Mathf.Sqrt(dx * dx + dy * dy) / 1.2f;
                t.SetPixel(x, y, new Color(0.9f, 0.05f, 0.05f, Mathf.SmoothStep(0.35f, 1f, d)));   // clear in the middle, red at the edges
            }
        t.Apply();
        return t;
    }
}

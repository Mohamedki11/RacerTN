using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Draws: money, "Press E" prompt, mission timer + distance + cargo bar, result screen and the minimap.
/// Put on the "Systems" GameObject.
/// </summary>
public class MissionHUD : MonoBehaviour
{
    public enum Corner { TopLeft, BottomLeft }

    [SerializeField] MinimapCamera minimap;
    [SerializeField] Corner minimapCorner = Corner.TopLeft;
    [SerializeField] float minimapSize = 300f;
    [SerializeField] float margin = 30f;

    static readonly Color Gold = new Color(1f, 0.85f, 0.3f);

    void Update()
    {
        var m = MissionManager.Instance;
        var k = Keyboard.current;
        if (m == null || k == null || !m.IsFinished) return;

        // Result screen shortcuts
        if (k.enterKey.wasPressedThisFrame) m.Continue();
        else if (k.rKey.wasPressedThisFrame) m.RestartMission();
    }

    void OnGUI()
    {
        GUI.depth = 0;
        Vector2 screen = GuiKit.Begin();
        var m = MissionManager.Instance;

        // Money (top right)
        GuiKit.Text(new Rect(screen.x - 330, 20, 300, 50), GameState.Money + " DT", 36, Gold, TextAnchor.MiddleRight);

        DrawMinimap(screen);

        if (m != null)
        {
            if (m.State == MissionState.Idle && m.Nearby != null) DrawPrompt(screen, m.Nearby);
            else if (m.State == MissionState.Active) DrawActive(screen, m);
            else if (m.IsFinished) DrawResult(screen, m);
        }

        GuiKit.End();
    }

    void DrawMinimap(Vector2 screen)
    {
        if (minimap == null || minimap.Texture == null) return;
        float y = minimapCorner == Corner.TopLeft ? margin : screen.y - minimapSize - margin;
        var r = new Rect(margin, y, minimapSize, minimapSize);

        GuiKit.Dim(new Rect(r.x - 4, r.y - 4, r.width + 8, r.height + 8), 0.9f);   // border
        GUI.DrawTexture(r, minimap.Texture, ScaleMode.StretchToFill, false);
    }

    void DrawPrompt(Vector2 screen, MissionPickup p)
    {
        GuiKit.Text(new Rect(screen.x / 2 - 500, 740, 1000, 60), "Press E to start: " + p.title, 44, Color.white);
        GuiKit.Text(new Rect(screen.x / 2 - 500, 800, 1000, 40),
                    "Time limit " + FormatTime(p.timeLimit) + "  -  Reward up to " + p.reward + " DT", 28, Gold);
        if (!string.IsNullOrEmpty(p.description))
            GuiKit.Text(new Rect(screen.x / 2 - 500, 842, 1000, 40), p.description, 24, new Color(0.85f, 0.85f, 0.85f));
    }

    void DrawActive(Vector2 screen, MissionManager m)
    {
        Color timeColor = m.TimeLeft < 10f ? new Color(1f, 0.3f, 0.25f) : Color.white;
        GuiKit.Text(new Rect(screen.x / 2 - 200, 20, 400, 80), FormatTime(m.TimeLeft), 64, timeColor);
        GuiKit.Text(new Rect(screen.x / 2 - 300, 100, 600, 40), m.Current.title, 26, new Color(0.85f, 0.85f, 0.85f));

        // Road distance when the route line is available, otherwise straight-line distance
        float shown = (RouteGuide.Instance != null && RouteGuide.Instance.RouteLength > 0f)
                      ? RouteGuide.Instance.RouteLength : m.Distance;
        string dist = shown >= 1000f ? (shown / 1000f).ToString("0.0") + " km"
                                     : Mathf.RoundToInt(shown) + " m";
        GuiKit.Text(new Rect(screen.x / 2 - 300, 135, 600, 50), dist, 36, Gold);

        // Cargo health bar
        float h = Mathf.Clamp01(m.CargoLeft);
        var bar = new Rect(screen.x / 2 - 160, 192, 320, 18);
        Fill(new Rect(bar.x - 3, bar.y - 3, bar.width + 6, bar.height + 6), new Color(0f, 0f, 0f, 0.7f));
        Fill(new Rect(bar.x, bar.y, bar.width * h, bar.height),
             Color.Lerp(new Color(1f, 0.3f, 0.25f), new Color(0.4f, 1f, 0.5f), h));
        GuiKit.Text(new Rect(bar.x, bar.y + 22, bar.width, 30),
                    m.Current.cargoName + " " + Mathf.RoundToInt(h * 100f) + "%", 22, Color.white);
    }

    void DrawResult(Vector2 screen, MissionManager m)
    {
        bool win = m.State == MissionState.Success;

        GuiKit.Dim(new Rect(0, 0, screen.x, screen.y), 0.6f);
        var panel = new Rect(screen.x / 2 - 380, 250, 760, 480);
        GuiKit.Dim(panel, 0.8f);

        GuiKit.Text(new Rect(panel.x, panel.y + 30, panel.width, 100),
                    win ? "MISSION COMPLETE" : "MISSION FAILED", 64,
                    win ? new Color(0.4f, 1f, 0.5f) : new Color(1f, 0.35f, 0.3f));

        if (win)
        {
            GuiKit.Text(new Rect(panel.x, panel.y + 150, panel.width, 50), "Time left: " + FormatTime(m.TimeLeft), 38, Color.white);
            GuiKit.Text(new Rect(panel.x, panel.y + 205, panel.width, 50), "Cargo: " + Mathf.RoundToInt(m.CargoLeft * 100f) + "%", 38, Color.white);
            GuiKit.Text(new Rect(panel.x, panel.y + 260, panel.width, 50), "Reward: +" + m.LastReward + " DT", 38, Gold);
        }
        else
        {
            GuiKit.Text(new Rect(panel.x, panel.y + 170, panel.width, 50),
                        string.IsNullOrEmpty(m.FailReason) ? "Mission failed" : m.FailReason, 38, Color.white);
        }

        if (GuiKit.Button(new Rect(panel.x + 60, panel.y + 340, 300, 80), "Restart (R)")) m.RestartMission();
        if (GuiKit.Button(new Rect(panel.x + 400, panel.y + 340, 300, 80), "Continue (Enter)")) m.Continue();
    }

    static void Fill(Rect r, Color c)
    {
        Color old = GUI.color;
        GUI.color = c;
        GUI.DrawTexture(r, Texture2D.whiteTexture);
        GUI.color = old;
    }

    static string FormatTime(float seconds)
    {
        int s = Mathf.CeilToInt(Mathf.Max(0f, seconds));
        return (s / 60) + ":" + (s % 60).ToString("00");
    }
}

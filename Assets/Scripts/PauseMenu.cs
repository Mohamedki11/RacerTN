using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// P (or Esc) opens / closes the pause menu. Stops time and all audio.
/// Put on an empty GameObject called "Systems".
/// </summary>
public class PauseMenu : MonoBehaviour
{
    bool open;

    void Start() => GameState.SetPaused(false);   // make sure we always start unpaused

    void Update()
    {
        var k = Keyboard.current;
        if (k == null) return;

        if (k.pKey.wasPressedThisFrame || k.escapeKey.wasPressedThisFrame)
        {
            if (open) Resume();
            else if (!GameState.Paused) Open();    // ignore if the result screen is showing
        }
    }

    void Open()
    {
        open = true;
        GameState.SetPaused(true, true);
    }

    void Resume()
    {
        open = false;
        GameState.SetPaused(false);
    }

    void OnGUI()
    {
        if (!open) return;
        GUI.depth = -10;                                   // draw on top of the HUD
        Vector2 screen = GuiKit.Begin();

        GuiKit.Dim(new Rect(0, 0, screen.x, screen.y), 0.65f);
        GuiKit.Text(new Rect(screen.x / 2 - 300, 250, 600, 100), "PAUSED", 80, Color.white);

        float x = screen.x / 2 - 200;
        var mm = MissionManager.Instance;
        bool canRestart = mm != null && mm.State == MissionState.Active;

        if (GuiKit.Button(new Rect(x, 400, 400, 70), "Resume (P)"))
            Resume();

        if (GuiKit.Button(new Rect(x, 490, 400, 70), "Restart mission", canRestart))
        {
            Resume();
            mm.RestartMission();
        }

        if (GuiKit.Button(new Rect(x, 580, 400, 70), "Quit"))
        {
            GameState.SetPaused(false);
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        GuiKit.End();
    }
}

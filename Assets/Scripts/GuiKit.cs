using UnityEngine;

/// <summary>
/// Small helpers so every OnGUI screen (pause menu, HUD, result screen) looks the same
/// and scales with the screen resolution. Nothing to add to the scene.
/// </summary>
public static class GuiKit
{
    static GUIStyle label, button;

    /// <summary>Call first in OnGUI. Returns the "virtual" screen size (height is always 1080).</summary>
    public static Vector2 Begin()
    {
        float s = Screen.height / 1080f;
        GUI.matrix = Matrix4x4.Scale(new Vector3(s, s, 1f));
        return new Vector2(Screen.width / s, 1080f);
    }

    /// <summary>Call last in OnGUI.</summary>
    public static void End() => GUI.matrix = Matrix4x4.identity;

    public static void Dim(Rect r, float alpha)
    {
        Color old = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, alpha);
        GUI.DrawTexture(r, Texture2D.whiteTexture);
        GUI.color = old;
    }

    public static void Text(Rect r, string text, int size, Color color,
                            TextAnchor anchor = TextAnchor.MiddleCenter)
    {
        if (label == null)
            label = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, wordWrap = false };
        label.fontSize = size;
        label.alignment = anchor;
        label.normal.textColor = new Color(0f, 0f, 0f, 0.8f);          // shadow
        GUI.Label(new Rect(r.x + 2, r.y + 2, r.width, r.height), text, label);
        label.normal.textColor = color;
        GUI.Label(r, text, label);
    }

    public static bool Button(Rect r, string text, bool enabled = true)
    {
        if (button == null)
            button = new GUIStyle(GUI.skin.button) { fontSize = 30, fontStyle = FontStyle.Bold };
        bool old = GUI.enabled;
        GUI.enabled = enabled;
        bool clicked = GUI.Button(r, text, button);
        GUI.enabled = old;
        return clicked;
    }
}

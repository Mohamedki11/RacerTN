using UnityEngine;

/// <summary>
/// Tiny shared helper: pause state + money. Nothing to add to the scene.
/// </summary>
public static class GameState
{
    const string MoneyKey = "racertn_money";

    /// <summary>True while the pause menu or a mission result screen is open.</summary>
    public static bool Paused { get; private set; }

    /// <summary>Player money (Tunisian dinars, "DT"). Saved automatically with PlayerPrefs.</summary>
    public static int Money
    {
        get => PlayerPrefs.GetInt(MoneyKey, 0);
        set => PlayerPrefs.SetInt(MoneyKey, Mathf.Max(0, value));
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetOnPlay() => Paused = false;

    /// <summary>Freezes / unfreezes the game. pauseAudio = also silence all sounds.</summary>
    public static void SetPaused(bool paused, bool pauseAudio = true)
    {
        Paused = paused;
        Time.timeScale = paused ? 0f : 1f;
        AudioListener.pause = paused && pauseAudio;
        Cursor.visible = paused;
    }
}

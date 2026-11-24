using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Plays a playlist. N = next track, M = mute / unmute.
/// Put on an empty GameObject called "Music" (an AudioSource is added automatically).
/// </summary>
[RequireComponent(typeof(AudioSource))]
public class MusicPlayer : MonoBehaviour
{
    [SerializeField] AudioClip[] tracks;
    [SerializeField, Range(0f, 1f)] float volume = 0.5f;
    [SerializeField] bool shuffle = true;

    AudioSource src;
    int index = -1;
    string toast;
    float toastUntil;

    void Awake()
    {
        src = GetComponent<AudioSource>();
        src.playOnAwake = false;
        src.loop = false;
        src.spatialBlend = 0f;          // 2D sound, same volume everywhere
        src.volume = volume;
    }

    void Start() => Next(false);

    void Update()
    {
        var k = Keyboard.current;
        if (k != null)
        {
            if (k.mKey.wasPressedThisFrame) ToggleMute();
            if (k.nKey.wasPressedThisFrame && !GameState.Paused) Next(true);
        }

        // Track finished -> play the next one.
        if (!GameState.Paused && tracks != null && tracks.Length > 0 && !src.isPlaying)
            Next(false);
    }

    void Next(bool showToast)
    {
        if (tracks == null || tracks.Length == 0) return;

        if (shuffle && tracks.Length > 1)
        {
            int n;
            do { n = Random.Range(0, tracks.Length); } while (n == index);
            index = n;
        }
        else index = (index + 1) % tracks.Length;

        src.clip = tracks[index];
        src.Play();
        if (showToast) Toast("Music: " + tracks[index].name);
    }

    void ToggleMute()
    {
        src.mute = !src.mute;
        Toast(src.mute ? "Music muted" : "Music on");
    }

    void Toast(string text)
    {
        toast = text;
        toastUntil = Time.unscaledTime + 2f;
    }

    void OnGUI()
    {
        if (Time.unscaledTime > toastUntil) return;
        GUI.depth = 1;
        Vector2 screen = GuiKit.Begin();
        GuiKit.Text(new Rect(screen.x / 2 - 400, 960, 800, 50), toast, 28, Color.white);
        GuiKit.End();
    }
}

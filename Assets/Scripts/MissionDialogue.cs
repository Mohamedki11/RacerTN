using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Plays mission lines one after another: subtitle at the bottom + optional voice clip.
/// Put on the same "Systems" object as MissionManager (the example-mission tool does it for you).
/// Keep each line short (about 70 characters) so it fits on one row.
/// </summary>
public class MissionDialogue : MonoBehaviour
{
    public static MissionDialogue Instance { get; private set; }

    [SerializeField, Range(0f, 1f)] float voiceVolume = 1f;
    [Tooltip("Subtitle time per character when a line has no voice clip.")]
    [SerializeField] float secondsPerChar = 0.055f;
    [SerializeField] float minSeconds = 2.2f;
    [SerializeField] float gapSeconds = 0.35f;

    readonly Queue<MissionLine> queue = new Queue<MissionLine>();
    AudioSource voice;
    MissionLine current;
    float timeLeft, gapLeft;

    void Awake()
    {
        Instance = this;
        voice = gameObject.AddComponent<AudioSource>();
        voice.playOnAwake = false;
        voice.spatialBlend = 0f;
        voice.volume = voiceVolume;
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    /// <summary>Queue lines. replace = true drops whatever is still playing.</summary>
    public static void Play(MissionLine[] lines, bool replace = true)
    {
        if (Instance == null || lines == null) return;
        if (replace) Instance.Clear();
        foreach (var l in lines)
            if (l != null && (!string.IsNullOrEmpty(l.text) || l.voice != null))
                Instance.queue.Enqueue(l);
    }

    public static void Stop()
    {
        if (Instance != null) Instance.Clear();
    }

    void Clear()
    {
        queue.Clear();
        current = null;
        gapLeft = 0f;
        if (voice != null) voice.Stop();
    }

    void Update()
    {
        if (AudioListener.pause) return;               // pause menu open: freeze
        float dt = Time.unscaledDeltaTime;             // also works on the result screen (time stopped)

        if (current != null)
        {
            timeLeft -= dt;
            if (timeLeft > 0f) return;
            current = null;
            gapLeft = gapSeconds;
        }
        if (gapLeft > 0f) { gapLeft -= dt; return; }
        if (queue.Count == 0) return;

        current = queue.Dequeue();
        float len = current.voice != null
            ? current.voice.length + 0.25f
            : Mathf.Max(minSeconds, (current.text != null ? current.text.Length : 0) * secondsPerChar);
        if (current.voice != null) { voice.clip = current.voice; voice.Play(); }
        timeLeft = len;
    }

    void OnGUI()
    {
        if (current == null || string.IsNullOrEmpty(current.text)) return;
        GUI.depth = -5;                                // above the HUD, below the pause menu
        Vector2 screen = GuiKit.Begin();

        var r = new Rect(screen.x / 2 - 560, 840, 1120, 104);
        GuiKit.Dim(r, 0.6f);
        if (!string.IsNullOrEmpty(current.speaker))
            GuiKit.Text(new Rect(r.x + 24, r.y + 6, r.width - 48, 34), current.speaker, 24,
                        new Color(1f, 0.85f, 0.3f), TextAnchor.MiddleLeft);
        GuiKit.Text(new Rect(r.x + 24, r.y + 40, r.width - 48, 56), current.text, 30, Color.white, TextAnchor.MiddleLeft);

        GuiKit.End();
    }
}

using UnityEngine;
using UnityEngine.InputSystem;

// Background music plus the sounds of the place: wind that grows with speed, sea waves that get
// louder near the water, and a calmer "town and birds" bed up on the hill.
//
// Setup: create an empty GameObject called "Audio", add this component, and drag in your clips.
// Everything is optional: leave a slot empty to skip it. Loops should be seamless (WAV or OGG).
public class AmbienceAudio : MonoBehaviour
{
    [Header("Follow")]
    [SerializeField] Transform car;                   // empty = finds the object with CarController

    [Header("Music")]
    [SerializeField] AudioClip[] music;               // plays in random order, one after another
    [SerializeField, Range(0f, 1f)] float musicVolume = 0.35f;   // keep it low, the world should be heard
    [SerializeField] float musicFade = 3f;            // seconds to fade in and out
    [SerializeField] bool musicOn = true;             // press M in play mode to switch it

    [Header("Ambient loops")]
    [SerializeField] AudioClip windLoop;
    [SerializeField, Range(0f, 1f)] float windVolume = 0.6f;
    [SerializeField] float windFullKmh = 160f;        // speed where the wind is at full volume
    [SerializeField] AudioClip seaLoop;
    [SerializeField, Range(0f, 1f)] float seaVolume = 0.7f;
    [SerializeField] float seaLoudBelow = 8f;         // car height (m) where the sea is at full volume
    [SerializeField] float seaSilentAbove = 40f;      // car height (m) where it fades out completely
    [SerializeField] AudioClip townLoop;              // birds, distant voices, calm day sounds
    [SerializeField, Range(0f, 1f)] float townVolume = 0.5f;
    [SerializeField] float loopFade = 0.5f;

    Rigidbody carBody;
    CrossLoop wind, sea, town;
    AudioSource musicSource;
    int lastTrack = -1;
    float musicGain;

    void Start()
    {
        if (car == null)
        {
            var c = FindFirstObjectByType<CarController>();
            if (c != null) car = c.transform;
        }
        if (car != null) carBody = car.GetComponent<Rigidbody>();

        if (windLoop != null) wind = new CrossLoop(transform, "Wind", windLoop, loopFade);
        if (seaLoop != null) sea = new CrossLoop(transform, "Sea", seaLoop, loopFade);
        if (townLoop != null) town = new CrossLoop(transform, "Town", townLoop, loopFade);

        var go = new GameObject("Music");
        go.transform.SetParent(transform, false);
        musicSource = go.AddComponent<AudioSource>();
        musicSource.spatialBlend = 0f;
        musicSource.playOnAwake = false;
        musicSource.loop = false;
        musicSource.volume = 0f;
    }

    void Update()
    {
var k = Keyboard.current;
if (k != null)
{
    if (k.mKey.wasPressedThisFrame) musicOn = !musicOn;                 // mute / unmute
    if (k.nKey.wasPressedThisFrame && !GameState.Paused
        && musicSource != null && music.Length > 0)
        musicSource.Stop();                                              // UpdateMusic picks the next track
}
        float kmh = carBody != null ? carBody.linearVelocity.magnitude * 3.6f : 0f;
        float height = car != null ? car.position.y : 0f;

        // Wind: a breath when still, strong when fast
        float wv = Mathf.Lerp(0.05f, 1f, Mathf.Clamp01(kmh / Mathf.Max(1f, windFullKmh)));
        if (wind != null) wind.Update(wv * windVolume, Mathf.Lerp(0.85f, 1.25f, Mathf.Clamp01(kmh / windFullKmh)));

        // Sea: loud at the shore, gone up the hill. Town and birds take over as it fades.
        float nearSea = 1f - Mathf.Clamp01(Mathf.InverseLerp(seaLoudBelow, seaSilentAbove, height));
        if (sea != null) sea.Update(nearSea * seaVolume, 1f);
        if (town != null) town.Update((1f - 0.7f * nearSea) * townVolume, 1f);

        UpdateMusic();
    }

    void UpdateMusic()
    {
	if (GameState.Paused) return;
        if (musicSource == null || music == null || music.Length == 0) return;

        if (!musicSource.isPlaying)
        {
            if (!musicOn) { musicGain = 0f; return; }
            int next = Random.Range(0, music.Length);
            if (music.Length > 1 && next == lastTrack) next = (next + 1) % music.Length;
            lastTrack = next;
            if (music[next] == null) return;
            musicSource.clip = music[next];
            musicSource.time = 0f;
            musicSource.Play();
        }

        float len = musicSource.clip.length;
        float t = musicSource.time;
        float fade = Mathf.Max(0.1f, Mathf.Min(musicFade, len * 0.4f));
        float env = Mathf.Min(Mathf.Clamp01(t / fade), Mathf.Clamp01((len - t) / fade));

        musicGain = Mathf.MoveTowards(musicGain, musicOn ? 1f : 0f, Time.deltaTime / 1.5f);   // smooth M on/off
        musicSource.volume = env * musicGain * musicVolume;
    }
}

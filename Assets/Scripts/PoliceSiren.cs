using UnityEngine;

/// <summary>
/// Siren sound and flashing red / blue lights for a police car. Put on the police car prefab (next to PoliceAI).
/// They only run while the car is chasing. The sound is 3D: you hear it get louder as the cop gets close.
/// Lights: real point lights are created for you. If your police model has light-bar objects,
/// drag them into Red Bars / Blue Bars and they flash too.
/// </summary>
[RequireComponent(typeof(PoliceAI))]
public class PoliceSiren : MonoBehaviour
{
    [Header("Sound")]
    [Tooltip("A seamless siren loop (WAV or OGG).")]
    [SerializeField] AudioClip sirenLoop;
    [SerializeField, Range(0f, 1f)] float volume = 0.8f;
    [Tooltip("Full volume inside this distance (metres).")]
    [SerializeField] float minDistance = 12f;
    [Tooltip("Silent beyond this distance (metres).")]
    [SerializeField] float maxDistance = 220f;
    [SerializeField] float fadeSeconds = 0.6f;

    [Header("Lights")]
    [SerializeField] bool createLights = true;
    [Tooltip("Roof position of the lights, relative to the car.")]
    [SerializeField] Vector3 lightPosition = new Vector3(0f, 1.7f, 0f);
    [SerializeField] float lightSideOffset = 0.45f;
    [Tooltip("Optional: an object on the roof where the red light should sit (overrides Light Position).")]
    [SerializeField] Transform redAnchor;
    [Tooltip("Optional: an object on the roof where the blue light should sit.")]
    [SerializeField] Transform blueAnchor;
    [SerializeField] float lightIntensity = 6f;
    [SerializeField] float lightRange = 14f;
    [SerializeField] float flashSeconds = 0.22f;
    [Tooltip("Optional: light-bar objects of your model that switch on and off with the red / blue flash.")]
    [SerializeField] GameObject[] redBars;
    [SerializeField] GameObject[] blueBars;

    PoliceAI ai;
    AudioSource src;
    Light red, blue;
    float gain;

    void Awake()
    {
        ai = GetComponent<PoliceAI>();

        src = gameObject.AddComponent<AudioSource>();
        src.clip = sirenLoop;
        src.loop = true;
        src.playOnAwake = false;
        src.spatialBlend = 1f;                          // 3D sound
        src.rolloffMode = AudioRolloffMode.Linear;
        src.minDistance = minDistance;
        src.maxDistance = maxDistance;
        src.volume = 0f;

        if (createLights)
        {
            red = MakeLight("SirenRed", new Color(1f, 0.1f, 0.1f), -lightSideOffset, redAnchor);
            blue = MakeLight("SirenBlue", new Color(0.15f, 0.3f, 1f), lightSideOffset, blueAnchor);
        }
    }

    Light MakeLight(string name, Color color, float x, Transform anchor)
    {
        var go = new GameObject(name);
        if (anchor != null)
        {
            go.transform.SetParent(anchor, false);
            go.transform.localPosition = Vector3.zero;
        }
        else
        {
            go.transform.SetParent(transform, false);
            go.transform.localPosition = lightPosition + new Vector3(x, 0f, 0f);
        }
        var l = go.AddComponent<Light>();
        l.type = LightType.Point;
        l.color = color;
        l.intensity = lightIntensity;
        l.range = lightRange;
        l.shadows = LightShadows.None;
        l.enabled = false;
        return l;
    }

    void Update()
    {
        bool on = ai.Chasing;

        // Sound: fade in and out
        gain = Mathf.MoveTowards(gain, on ? 1f : 0f, Time.deltaTime / Mathf.Max(0.05f, fadeSeconds));
        src.volume = gain * volume;
        if (sirenLoop != null)
        {
            if (on && !src.isPlaying) src.Play();
            else if (!on && gain <= 0f && src.isPlaying) src.Stop();
        }

        // Lights: red and blue take turns
        bool redPhase = Mathf.Repeat(Time.time, flashSeconds * 2f) < flashSeconds;
        bool redOn = on && redPhase, blueOn = on && !redPhase;
        if (red != null) red.enabled = redOn;
        if (blue != null) blue.enabled = blueOn;
        SetActive(redBars, redOn);
        SetActive(blueBars, blueOn);
    }

    static void SetActive(GameObject[] objects, bool active)
    {
        if (objects == null) return;
        foreach (var o in objects) if (o != null) o.SetActive(active);
    }
}

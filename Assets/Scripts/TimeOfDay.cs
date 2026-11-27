using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

/// <summary>
/// Day and night. Moves the sun (and the moon at night), and changes light colour, fog, ambient light and the sky.
///
/// Put it on an empty GameObject (for example "TimeOfDay"). It finds the sun by itself.
///
/// TIME CONTROL
///   - In the Inspector: drag "Hour" (0 to 24) while the game runs, or tick "Freeze Time".
///   - Keys: [ and ] go back / forward one hour, \ freezes or unfreezes time.
///   - From code:  TimeOfDay.Instance.SetHour(22f);            // jump to 22:00 right now
///                 TimeOfDay.Instance.GoToHour(22f, 3f);       // fast-forward to 22:00 in 3 seconds
///   - Right-click the component title for quick buttons (sunrise, noon, sunset, midnight).
/// Sun path: rises in the east (+X), is highest at 12:00, sets in the west. Change "Sun Yaw" to turn it.
/// </summary>
public class TimeOfDay : MonoBehaviour
{
    public static TimeOfDay Instance { get; private set; }

    public enum SkyMode { KeepCurrentSkybox, ProceduralSky, BlendedHdri }

    [Header("Time")]
    [Range(0f, 24f)] public float hour = 17.5f;
    [Tooltip("Real minutes for one full day of 24 hours.")]
    [SerializeField] float dayLengthMinutes = 16f;
    public bool freezeTime = false;
    [SerializeField] bool debugKeys = true;
    [SerializeField] bool showClock = true;

    [Header("Sun and moon")]
    [Tooltip("Leave empty: the sun set in Lighting settings, or the first Directional Light, is used.")]
    [SerializeField] Light sun;
    [Tooltip("Direction the sun path points. 270 = rises in the east, sets in the west.")]
    [SerializeField] float sunYaw = 270f;
    [SerializeField] float sunIntensity = 1.6f;
    [SerializeField] float moonIntensity = 0.12f;
    [SerializeField] Color moonColor = new Color(0.55f, 0.65f, 1f);
    [Tooltip("Sun colour by height: left = at the horizon, right = high in the sky.")]
    [SerializeField] Gradient sunColor = DefaultSunColor();
    [Tooltip("Sun strength by height (0 = horizon, 1 = high), multiplied by Sun Intensity.")]
    [SerializeField] AnimationCurve sunStrength = DefaultSunStrength();

    [Tooltip("Higher = the sun gets strong more slowly after sunrise, so mornings and evenings stay soft.")]
    [SerializeField] float sunRampPower = 1.8f;
    [Tooltip("How much clouds weaken the sun (Sky Mode = BlendedHdri). 0 = not at all.")]
    [Range(0f, 1f)] [SerializeField] float cloudSunDimming = 0.4f;

    [Header("Sky")]
    [Tooltip("ProceduralSky: a plain day / night sky that follows the sun. KeepCurrentSkybox: keep your HDRI and only dim it. BlendedHdri: fades between your day, sunset and night HDRIs and adds moving clouds.")]
    [SerializeField] SkyMode skyMode = SkyMode.ProceduralSky;
    [Tooltip("Keep Current Skybox only: how bright the sky is at night (0 to 1).")]
    [SerializeField] float nightExposure = 0.1f;
    [Tooltip("Keep Current Skybox only: slow turning of the sky in degrees per game hour (needs a skybox with a Rotation setting).")]
    [SerializeField] float skyDriftDegreesPerHour = 1.5f;

    [Header("Blended HDRI sky (Sky Mode = BlendedHdri)")]
    [Tooltip("Daytime sky. Pick one where the sun is hidden or high, because the sun in the photo does not move.")]
    [SerializeField] Texture2D dayHdri;
    [Tooltip("Sunrise / sunset sky. Used for both, turned around by 180 degrees for sunrise.")]
    [SerializeField] Texture2D duskHdri;
    [SerializeField] Texture2D nightHdri;
    [SerializeField] float hdriExposure = 1f;
    [Tooltip("If you leave Night Hdri empty, the day or sunset photo is used and darkened to this brightness at night.")]
    [Range(0f, 1f)] [SerializeField] float singleHdriNightBrightness = 0.05f;
    [Tooltip("Turns each photo around (degrees). Use Dusk Yaw to line up the sun in the sunset photo with the real sunset.")]
    [SerializeField] float dayYaw = 0f;
    [SerializeField] float duskYaw = 0f;
    [SerializeField] float nightYaw = 0f;
    [Tooltip("Sun and moon discs drawn on top of the sky, following the light.")]
    [SerializeField] float sunDiscSize = 1.2f;
    [SerializeField] float sunDiscBrightness = 25f;
    [SerializeField] float moonDiscSize = 1.8f;
    [SerializeField] float moonDiscBrightness = 6f;
    [SerializeField] float discGlow = 0.25f;
    [Header("Clouds (Sky Mode = BlendedHdri)")]
    [Range(0f, 1f)] [SerializeField] float cloudAmount = 0.4f;
    [SerializeField] float cloudScale = 1.5f;
    [Range(0f, 1f)] [SerializeField] float cloudOpacity = 0.85f;
    [Tooltip("How fast the clouds drift.")]
    [SerializeField] float cloudSpeed = 0.01f;
    [Tooltip("Wind direction in degrees.")]
    [SerializeField] float windDirection = 45f;
    [SerializeField] float cloudBrightness = 1.2f;
    [SerializeField] Color nightCloudColor = new Color(0.04f, 0.05f, 0.09f);

    [Header("Atmosphere")]
    [SerializeField] bool controlFog = true;
    [SerializeField] Color dayFog = new Color(0.62f, 0.72f, 0.82f);
    [SerializeField] Color duskFog = new Color(0.85f, 0.55f, 0.40f);
    [SerializeField] Color nightFog = new Color(0.03f, 0.05f, 0.10f);
    [Tooltip("Ambient light strength by day and night. Works when Lighting > Environment > Source is Skybox.")]
    [SerializeField] float dayAmbient = 1f;
    [SerializeField] float nightAmbient = 0.08f;
    [SerializeField] float dayReflection = 1f;
    [SerializeField] float nightReflection = 0.1f;
    [Tooltip("Ambient light and reflections are recalculated this often (seconds). It is the costly part.")]
    [SerializeField] float ambientUpdateSeconds = 3f;

    /// <summary>0 = full night, 1 = full day. Headlights and street lamps will read this.</summary>
    public float Daylight { get; private set; } = 1f;
    public bool IsNight => Daylight < 0.35f;
    public float Hour => hour;

    Material skyMat;
    bool hasExposure, hasRotation;
    float baseExposure = 1f, baseRotation;
    float ambientTimer;
    bool going;
    float goHour, goTarget, goRate;
    Vector2 cloudOffset;
    bool dimmedNight;

    void Awake() => Instance = this;

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
        if (skyMat != null) Destroy(skyMat);
    }

    void Start()
    {
        if (sun == null) sun = RenderSettings.sun != null ? RenderSettings.sun : FindDirectionalLight();
        if (sun == null)
        {
            Debug.LogWarning("TimeOfDay: no Directional Light found. Drag your sun into the 'Sun' slot.");
            enabled = false;
            return;
        }
        RenderSettings.sun = sun;

        // Work on a copy of the sky material, so your original asset is never changed
        if (skyMode == SkyMode.ProceduralSky)
        {
            Shader sh = Shader.Find("Skybox/Procedural");
            if (sh != null) { skyMat = new Material(sh); RenderSettings.skybox = skyMat; }
            else Debug.LogWarning("TimeOfDay: Skybox/Procedural shader not found, keeping the current skybox.");
        }
        if (skyMode == SkyMode.BlendedHdri)
        {
            Shader sh = Shader.Find("RacerTN/SkyBlend");
            if (sh == null)
                Debug.LogWarning("TimeOfDay: shader RacerTN/SkyBlend not found. Add SkyBlend.shader to the project.");
            else
            {
                skyMat = new Material(sh);

                // Works with one, two or three photos: empty slots borrow from the others
                Texture2D dayT = dayHdri != null ? dayHdri : (duskHdri != null ? duskHdri : nightHdri);
                Texture2D duskT = duskHdri != null ? duskHdri : dayT;
                Texture2D nightT = nightHdri != null ? nightHdri : duskT;
                dimmedNight = nightHdri == null;       // no night photo: darken the day / sunset one instead
                if (dayT != null)
                {
                    skyMat.SetTexture("_TexDay", dayT);
                    skyMat.SetTexture("_TexDusk", duskT);
                    skyMat.SetTexture("_TexNight", nightT);
                }
                else Debug.LogWarning("TimeOfDay: assign at least one HDRI (Day, Dusk or Night) for the blended sky.");
                RenderSettings.skybox = skyMat;
            }
        }
        if (skyMat == null && RenderSettings.skybox != null)
        {
            skyMat = new Material(RenderSettings.skybox);
            RenderSettings.skybox = skyMat;
        }
        if (skyMat != null)
        {
            hasExposure = skyMat.HasProperty("_Exposure");
            hasRotation = skyMat.HasProperty("_Rotation");
            if (hasExposure) baseExposure = skyMat.GetFloat("_Exposure");
            if (hasRotation) baseRotation = skyMat.GetFloat("_Rotation");
        }

        Apply(true);
    }

    static Light FindDirectionalLight()
    {
        foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None))
            if (l.type == LightType.Directional) return l;
        return null;
    }

    // ---------------------------------------------------------------- time control

    /// <summary>Jump to this hour right now (0 to 24, for example 22.5 = 22:30).</summary>
    public void SetHour(float newHour)
    {
        going = false;
        hour = Mathf.Repeat(newHour, 24f);
        if (sun != null) Apply(true);
    }

    /// <summary>Fast-forward the clock to this hour over 'seconds' real seconds. Time always moves forward.</summary>
    public void GoToHour(float targetHour, float seconds)
    {
        targetHour = Mathf.Repeat(targetHour, 24f);
        float delta = Mathf.Repeat(targetHour - hour, 24f);
        if (seconds <= 0.01f || delta < 0.001f) { SetHour(targetHour); return; }
        goHour = hour;
        goTarget = hour + delta;
        goRate = delta / seconds;
        going = true;
    }

    [ContextMenu("Go to sunrise (6:00)")] void Sunrise() => SetHour(6f);
    [ContextMenu("Go to noon (12:00)")] void Noon() => SetHour(12f);
    [ContextMenu("Go to sunset (18:00)")] void Sunset() => SetHour(18f);
    [ContextMenu("Go to midnight (0:00)")] void Midnight() => SetHour(0f);

    // ---------------------------------------------------------------- update

    void Update()
    {
        if (sun == null) return;

        if (going)
        {
            goHour = Mathf.MoveTowards(goHour, goTarget, goRate * Time.deltaTime);
            hour = Mathf.Repeat(goHour, 24f);
            if (Mathf.Approximately(goHour, goTarget)) going = false;
        }
        else if (!freezeTime)
        {
            hour = Mathf.Repeat(hour + 24f * Time.deltaTime / (Mathf.Max(0.1f, dayLengthMinutes) * 60f), 24f);
        }

        if (skyMode == SkyMode.BlendedHdri && skyMat != null)
        {
            float rad = windDirection * Mathf.Deg2Rad;
            cloudOffset += new Vector2(Mathf.Cos(rad), Mathf.Sin(rad)) * cloudSpeed * Time.deltaTime;
            skyMat.SetVector("_CloudOffset", new Vector4(cloudOffset.x, cloudOffset.y, 0f, 0f));
        }

        var k = Keyboard.current;
        if (debugKeys && k != null)
        {
            if (k.rightBracketKey.wasPressedThisFrame) SetHour(hour + 1f);
            if (k.leftBracketKey.wasPressedThisFrame) SetHour(hour - 1f);
            if (k.backslashKey.wasPressedThisFrame) freezeTime = !freezeTime;
        }

        Apply(false);
    }

    void Apply(bool force)
    {
        // Sun angle: 0 = sunrise (6:00), 90 = noon, 180 = sunset, 270 = midnight
        float a = Mathf.Repeat((hour - 6f) / 24f * 360f, 360f);
        float sinSun = Mathf.Sin(a * Mathf.Deg2Rad);          // above 0 = sun is up
        bool night = sinSun < 0f;
        float e = Mathf.Abs(sinSun);                           // height of the sun (or moon) in the sky, 0 to 1

        // At night the same light becomes the moon, on the opposite side, so nothing is ever lit from below
        float pitch = night ? a - 180f : a;
        sun.transform.rotation = Quaternion.Euler(pitch, sunYaw, 0f);

        if (!night)
        {
            sun.color = sunColor.Evaluate(e);
            float strength = Mathf.Pow(Mathf.Clamp01(sunStrength.Evaluate(e)), sunRampPower);
            if (skyMode == SkyMode.BlendedHdri) strength *= 1f - cloudSunDimming * cloudAmount;
            sun.intensity = sunIntensity * strength;
        }
        else
        {
            sun.color = moonColor;
            sun.intensity = moonIntensity * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0f, 0.25f, e));
        }

        // 0 = night, 1 = day, with a short twilight after sunset
        float day = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-0.08f, 0.25f, sinSun));
        Daylight = day;
        if (skyMat != null && skyMode == SkyMode.BlendedHdri) ApplyBlendedSky(sinSun, night, e, day);

        if (controlFog)
        {
            float glow = 1f - Mathf.Clamp01(Mathf.Abs(sinSun) / 0.3f);      // strongest at the horizon
            Color f = Color.Lerp(nightFog, dayFog, day);
            f = Color.Lerp(f, duskFog, glow * 0.6f * (night ? Mathf.Clamp01(day * 1.5f) : 1f));
            RenderSettings.fogColor = f;
        }

        RenderSettings.ambientIntensity = Mathf.Lerp(nightAmbient, dayAmbient, day);
        RenderSettings.reflectionIntensity = Mathf.Lerp(nightReflection, dayReflection, day);

        if (skyMat != null)
        {
            if (skyMode == SkyMode.KeepCurrentSkybox && hasExposure)
                skyMat.SetFloat("_Exposure", baseExposure * Mathf.Lerp(nightExposure, 1f, day));
            if (skyMode == SkyMode.KeepCurrentSkybox && hasRotation)
                skyMat.SetFloat("_Rotation", Mathf.Repeat(baseRotation + hour * skyDriftDegreesPerHour, 360f));
        }

        // The costly part (ambient light + reflections from the sky): only every few seconds
        ambientTimer -= Time.deltaTime;
        if (force || ambientTimer <= 0f)
        {
            ambientTimer = ambientUpdateSeconds;
            DynamicGI.UpdateEnvironment();
        }
    }

    // Sets the blended sky: which photo is visible, where the sun and moon are, and the cloud colour
    void ApplyBlendedSky(float sinSun, bool night, float e, float day)
    {
        // Night until the sun is just above the horizon, then sunrise/sunset sky, then full day
        float nightW = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-0.12f, 0.05f, sinSun));
        float dayW = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.05f, 0.35f, sinSun));
        float duskW = Mathf.Clamp01(1f - nightW - dayW);
        float nightOut = dimmedNight ? nightW * singleHdriNightBrightness : nightW;
        skyMat.SetVector("_Weights", new Vector4(dayW, duskW, nightOut, 0f));
        skyMat.SetVector("_Yaws", new Vector4(dayYaw, hour < 12f ? duskYaw + 180f : duskYaw, nightYaw, 0f));
        skyMat.SetFloat("_Exposure", hdriExposure);

        // Sun / moon disc: it sits where the light is
        float vis = night ? Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0f, 0.25f, e)) : sunStrength.Evaluate(e);
        Color disc = (night ? moonColor * moonDiscBrightness : sun.color * sunDiscBrightness) * vis;
        skyMat.SetVector("_SunDir", -sun.transform.forward);
        skyMat.SetColor("_SunColor", disc);
        skyMat.SetFloat("_SunSize", night ? moonDiscSize : sunDiscSize);
        skyMat.SetFloat("_SunGlow", discGlow);

        // Clouds: white by day, tinted orange near sunset, dark at night
        float glow = 1f - Mathf.Clamp01(Mathf.Abs(sinSun) / 0.3f);
        Color dayCloud = Color.Lerp(Color.white, sun.color, glow * 0.8f) * cloudBrightness;
        skyMat.SetColor("_CloudColor", Color.Lerp(nightCloudColor, dayCloud, day));
        skyMat.SetFloat("_CloudAmount", cloudAmount);
        skyMat.SetFloat("_CloudScale", cloudScale);
        skyMat.SetFloat("_CloudOpacity", cloudOpacity);
    }

    // ---------------------------------------------------------------- clock on screen

    void OnGUI()
    {
        if (!showClock || sun == null) return;
        GUI.depth = 3;
        Vector2 screen = GuiKit.Begin();
        int h = Mathf.FloorToInt(hour) % 24;
        int m = Mathf.FloorToInt((hour - Mathf.Floor(hour)) * 60f);
        GuiKit.Text(new Rect(screen.x - 330, 68, 300, 40), h.ToString("00") + ":" + m.ToString("00"), 28,
                    new Color(0.85f, 0.85f, 0.85f), TextAnchor.MiddleRight);
        GuiKit.End();
    }

    // ---------------------------------------------------------------- defaults

    static Gradient DefaultSunColor()
    {
        var g = new Gradient();
        g.SetKeys(new[]
        {
            new GradientColorKey(new Color(1f, 0.45f, 0.20f), 0f),
            new GradientColorKey(new Color(1f, 0.62f, 0.35f), 0.08f),
            new GradientColorKey(new Color(1f, 0.88f, 0.70f), 0.25f),
            new GradientColorKey(new Color(1f, 0.96f, 0.90f), 0.6f),
            new GradientColorKey(new Color(1f, 0.98f, 0.95f), 1f)
        }, new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });
        return g;
    }

    static AnimationCurve DefaultSunStrength()
    {
        return new AnimationCurve(new Keyframe(0f, 0f), new Keyframe(0.05f, 0.35f), new Keyframe(0.2f, 0.8f),
                                  new Keyframe(0.5f, 1f), new Keyframe(1f, 1f));
    }
}

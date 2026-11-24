using UnityEngine;

// Plays a clip forever without an audible restart: two sources take turns, and the next one
// starts a little before the current one ends, with an equal-power crossfade between them.
public class CrossLoop
{
    readonly AudioSource a, b;
    readonly AudioClip clip;
    readonly float fade;
    bool aActive = true;

    public CrossLoop(Transform parent, string name, AudioClip clip, float fade)
    {
        this.clip = clip;
        this.fade = Mathf.Clamp(fade, 0.02f, clip.length * 0.4f);
        a = Make(parent, name + "_A");
        b = Make(parent, name + "_B");
        a.Play();
    }

    AudioSource Make(Transform parent, string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var s = go.AddComponent<AudioSource>();
        s.clip = clip;
        s.loop = false;           // we do the looping ourselves
        s.spatialBlend = 0f;      // 2D: the player always hears it the same
        s.playOnAwake = false;
        s.volume = 0f;
        return s;
    }

    public void Update(float vol, float pitch)
    {
        a.pitch = pitch;
        b.pitch = pitch;
        AudioSource cur = aActive ? a : b;
        AudioSource nxt = aActive ? b : a;

        if (!cur.isPlaying && !nxt.isPlaying) { cur.time = 0f; cur.Play(); }   // e.g. after a pause
        else if (!cur.isPlaying && nxt.isPlaying) { aActive = !aActive; cur = nxt; nxt = aActive ? b : a; }

        float remaining = clip.length - cur.time;
        if (remaining <= fade && !nxt.isPlaying) { nxt.time = 0f; nxt.Play(); }

        float x = nxt.isPlaying ? Mathf.Clamp01(1f - remaining / fade) : 0f;
        cur.volume = Mathf.Cos(x * Mathf.PI * 0.5f) * vol;
        nxt.volume = Mathf.Sin(x * Mathf.PI * 0.5f) * vol;
    }
}

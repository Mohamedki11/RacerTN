using UnityEngine;
using UnityEngine.InputSystem;

public enum MissionState { Idle, Active, Success, Failed }

/// <summary>
/// The brain of the mission system. Put on the "Systems" GameObject.
/// Idle -> (press E at a pickup) -> Active -> Success / Failed -> (Continue) -> Idle.
/// Fails when the time runs out or the cargo is destroyed. Cargo damage lowers the reward.
/// </summary>
public class MissionManager : MonoBehaviour
{
    public static MissionManager Instance { get; private set; }

    [Tooltip("Leave empty: the car is found automatically.")]
    [SerializeField] CarController player;
    [Tooltip("The cargo warning line plays once when health drops below this.")]
    [SerializeField, Range(0f, 1f)] float cargoWarningLevel = 0.35f;

    MissionPickup[] pickups;
    CarReset carReset;
    CargoHealth cargo;
    bool warned;

    public MissionState State { get; private set; } = MissionState.Idle;
    public MissionPickup Current { get; private set; }
    public MissionPickup Nearby { get; private set; }
    public float TimeLeft { get; private set; }
    public float Distance { get; private set; }     // metres to the drop-off
    public float CargoLeft { get; private set; } = 1f;   // 0..1, live during a mission, kept on the result screen
    public string FailReason { get; private set; } = "";
    public int LastReward { get; private set; }
    public Transform Destination => Current != null ? Current.dropoff : null;
    public bool IsFinished => State == MissionState.Success || State == MissionState.Failed;

    void Awake()
    {
        Instance = this;
        if (player == null) player = PlayerCar.Find();
        if (player != null)
        {
            carReset = player.GetComponent<CarReset>();
            cargo = player.GetComponent<CargoHealth>();
        }
        if (carReset == null) Debug.LogWarning("MissionManager: add the CarReset script to the Car so 'Restart mission' can move it.");
        if (cargo == null) Debug.LogWarning("MissionManager: add the CargoHealth script to the Car to get cargo damage.");
        pickups = FindObjectsByType<MissionPickup>(FindObjectsInactive.Include, FindObjectsSortMode.None);
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    void Start()
    {
        foreach (var p in pickups)
        {
            p.SetDropoffVisible(false);
            p.SetAvailable(true);
        }
    }

    void Update()
    {
        if (player == null || GameState.Paused) return;

        if (State == MissionState.Idle)
        {
            var k = Keyboard.current;
            if (Nearby != null && k != null && k.eKey.wasPressedThisFrame)
                BeginMission(Nearby);
        }
        else if (State == MissionState.Active)
        {
            Tick();
        }
    }

    void Tick()
    {
        TimeLeft -= Time.deltaTime;

        Vector3 d = Current.dropoff.position - player.transform.position;
        d.y = 0f;
        Distance = d.magnitude;

        if (cargo != null)
        {
            CargoLeft = cargo.Health;
            if (!warned && CargoLeft < cargoWarningLevel)
            {
                warned = true;
                MissionDialogue.Play(new[] { Current.cargoWarning }, false);
            }
            if (CargoLeft <= 0f) { Finish(false, "Cargo destroyed!"); return; }
        }

        if (Distance <= Current.dropoffRadius) Finish(true, "");
        else if (TimeLeft <= 0f) { TimeLeft = 0f; Finish(false, "Time ran out!"); }
    }

    // ---------- called by the pickups ----------
    public void SetNearby(MissionPickup p) { if (State == MissionState.Idle) Nearby = p; }
    public void ClearNearby(MissionPickup p) { if (Nearby == p) Nearby = null; }

    // ---------- called by the UI ----------
    public void RestartMission()
    {
        if (Current == null) return;
        var p = Current;
        p.SetDropoffVisible(false);
        GameState.SetPaused(false);
        if (carReset != null) carReset.TeleportTo(p.transform.position, p.transform.eulerAngles.y);
        BeginMission(p);
    }

    /// <summary>Close the result screen and go back to free roam.</summary>
    public void Continue()
    {
        if (Current != null) Current.SetDropoffVisible(false);
        Current = null;
        State = MissionState.Idle;
        MissionDialogue.Stop();
        foreach (var p in pickups) p.SetAvailable(true);
        GameState.SetPaused(false);
    }

    /// <summary>Called by the police when they catch the player during a mission.</summary>
    public void Bust()
    {
        if (State == MissionState.Active) Finish(false, "Busted!");
    }

    // ---------- internals ----------
    void BeginMission(MissionPickup p)
    {
        if (p.dropoff == null)
        {
            Debug.LogError("MissionPickup '" + p.name + "' has no Dropoff assigned.");
            return;
        }
        Current = p;
        Nearby = null;
        TimeLeft = p.timeLimit;
        State = MissionState.Active;
        FailReason = "";
        CargoLeft = 1f;
        warned = false;
        if (cargo != null) cargo.Begin(p.fragility);
        foreach (var x in pickups) x.SetAvailable(false);
        p.SetDropoffVisible(true);
        MissionDialogue.Play(p.briefing);
        Tick();                                  // fills Distance right away
    }

    void Finish(bool success, string reason)
    {
        State = success ? MissionState.Success : MissionState.Failed;
        FailReason = reason;
        Current.SetDropoffVisible(false);

        if (cargo != null) { CargoLeft = cargo.Health; cargo.End(); }

        // Damaged cargo pays less: 100% health = full reward, 0% = half
        LastReward = success ? Mathf.RoundToInt(Current.reward * Mathf.Lerp(0.5f, 1f, CargoLeft)) : 0;
        if (success) GameState.Money += LastReward;

        MissionDialogue.Play(success ? Current.onSuccess : Current.onFail);
        GameState.SetPaused(true, false);        // freeze the game, keep sound playing
    }
}

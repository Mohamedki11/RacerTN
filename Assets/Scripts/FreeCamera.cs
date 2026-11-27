using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Free camera for looking around the map and taking screenshots. Put on the Main Camera.
///   F                  switch the free camera on / off (starts from where the game camera is)
///   W A S D / arrows   fly            Space / Left Ctrl   up / down
///   Hold right mouse   look around    Shift   fly faster     Mouse wheel   change speed
///   G                  drop the car on the ground under the camera and go back to driving
/// While it is on, the car gets no input. The chase camera is switched off and comes back on exit.
/// The camera does not collide with anything.
/// </summary>
public class FreeCamera : MonoBehaviour
{
    /// <summary>True while the free camera is flying. CarInput reads this.</summary>
    public static bool Active { get; private set; }

    [SerializeField] Key toggleKey = Key.F;
    [SerializeField] float speed = 20f;
    [SerializeField] float fastMultiplier = 4f;
    [SerializeField] float lookSensitivity = 0.12f;
    [Tooltip("true = hold the right mouse button to look. false = the mouse always looks.")]
    [SerializeField] bool needRightMouseToLook = true;

    readonly List<MonoBehaviour> switchedOff = new List<MonoBehaviour>();
    float yaw, pitch;

    void OnDestroy() => Active = false;

    void Update()
    {
        var k = Keyboard.current;
        var m = Mouse.current;
        if (k == null) return;

        if (k[toggleKey].wasPressedThisFrame && !GameState.Paused)
        {
            if (Active) Exit(); else Enter();
        }
        if (!Active || GameState.Paused) return;

        // Look
        bool looking = m != null && (!needRightMouseToLook || m.rightButton.isPressed);
        Cursor.lockState = looking ? CursorLockMode.Locked : CursorLockMode.None;
        if (looking)
        {
            Vector2 d = m.delta.ReadValue();
            yaw += d.x * lookSensitivity;
            pitch = Mathf.Clamp(pitch - d.y * lookSensitivity, -89f, 89f);
        }
        transform.rotation = Quaternion.Euler(pitch, yaw, 0f);

        // Speed
        if (m != null)
        {
            float wheel = m.scroll.ReadValue().y;
            if (Mathf.Abs(wheel) > 0.1f) speed = Mathf.Clamp(speed * Mathf.Pow(1.15f, Mathf.Sign(wheel)), 1f, 500f);
        }

        // Fly
        Vector3 move = Vector3.zero;
        if (k.wKey.isPressed || k.upArrowKey.isPressed) move += Vector3.forward;
        if (k.sKey.isPressed || k.downArrowKey.isPressed) move += Vector3.back;
        if (k.dKey.isPressed || k.rightArrowKey.isPressed) move += Vector3.right;
        if (k.aKey.isPressed || k.leftArrowKey.isPressed) move += Vector3.left;
        Vector3 dir = transform.rotation * move.normalized;
        if (k.spaceKey.isPressed) dir += Vector3.up;
        if (k.leftCtrlKey.isPressed) dir += Vector3.down;

        float s = speed * (k.leftShiftKey.isPressed ? fastMultiplier : 1f);
        transform.position += dir * s * Time.unscaledDeltaTime;

        // Drop the car here
        if (k.gKey.wasPressedThisFrame)
        {
            var car = PlayerCar.Find();
            var reset = car != null ? car.GetComponent<CarReset>() : null;
            if (reset != null)
            {
                reset.TeleportTo(transform.position, yaw, true);
                Exit();
            }
        }
    }

    void Enter()
    {
        Active = true;
        Vector3 e = transform.eulerAngles;
        yaw = e.y;
        pitch = e.x > 180f ? e.x - 360f : e.x;

        // Stop the camera scripts that would fight us (they come back on exit)
        switchedOff.Clear();
        var chase = GetComponent<ChaseCamera>();
        if (chase != null && chase.enabled) { chase.enabled = false; switchedOff.Add(chase); }
        var shake = GetComponent<CameraShake>();
        if (shake != null && shake.enabled) { shake.enabled = false; switchedOff.Add(shake); }
    }

    void Exit()
    {
        Active = false;
        foreach (var c in switchedOff) if (c != null) c.enabled = true;
        switchedOff.Clear();
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = GameState.Paused;
    }

    void OnGUI()
    {
        if (!Active) return;
        GUI.depth = -6;
        Vector2 screen = GuiKit.Begin();
        GuiKit.Text(new Rect(30, screen.y - 60, 1500, 36),
                    "FREE CAM   WASD fly   Space / Ctrl up, down   Right mouse look   Shift fast   Wheel speed (" +
                    Mathf.RoundToInt(speed) + ")   G drop car here   F exit",
                    22, Color.white, TextAnchor.MiddleLeft);
        GuiKit.End();
    }
}

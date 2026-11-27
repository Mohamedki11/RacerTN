using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(CarController))]
public class CarInput : MonoBehaviour
{
    CarController car;

    void Awake() => car = GetComponent<CarController>();

    void Update()
    {
        var k = Keyboard.current;
        if (k == null) return;

        // While paused, on the result screen, or flying the free camera, the car gets no input.
        if (GameState.Paused || FreeCamera.Active)
        {
            car.Throttle = 0f;
            car.Steer = 0f;
            car.Brake = 0f;
            return;
        }

        car.Throttle = Axis(k.wKey.isPressed || k.upArrowKey.isPressed,
                            k.sKey.isPressed || k.downArrowKey.isPressed);
        car.Steer    = Axis(k.dKey.isPressed || k.rightArrowKey.isPressed,
                            k.aKey.isPressed || k.leftArrowKey.isPressed);
        car.Brake    = k.spaceKey.isPressed ? 1f : 0f;
    }

    static float Axis(bool positive, bool negative) =>
        (positive ? 1f : 0f) - (negative ? 1f : 0f);
}

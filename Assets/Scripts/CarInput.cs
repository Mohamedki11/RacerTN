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

        car.Throttle = Axis(k.wKey.isPressed || k.upArrowKey.isPressed,
                            k.sKey.isPressed || k.downArrowKey.isPressed);
        car.Steer    = Axis(k.dKey.isPressed || k.rightArrowKey.isPressed,
                            k.aKey.isPressed || k.leftArrowKey.isPressed);
        car.Brake    = k.spaceKey.isPressed ? 1f : 0f;
    }

    static float Axis(bool positive, bool negative) =>
        (positive ? 1f : 0f) - (negative ? 1f : 0f);
}
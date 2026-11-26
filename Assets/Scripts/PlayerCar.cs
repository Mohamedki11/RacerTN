using UnityEngine;

/// <summary>
/// Finds the car the PLAYER drives (the one with CarInput), so police cars, which also have a
/// CarController, are never mistaken for it.
/// </summary>
public static class PlayerCar
{
    public static CarController Find()
    {
        foreach (var c in Object.FindObjectsByType<CarController>(FindObjectsSortMode.None))
            if (c.GetComponent<CarInput>() != null) return c;
        return null;
    }
}

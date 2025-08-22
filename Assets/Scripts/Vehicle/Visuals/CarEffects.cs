using UnityEngine;
using Vehicle;

public class CarEffects : MonoBehaviour
{
    [Header("Smoke Per Wheel (FL, FR, RL, RR)")]
    public ParticleSystem[] smoke = new ParticleSystem[4];

    [Header("Tire Marks Per Wheel (FL, FR, RL, RR)")]
    public TrailRenderer[] tireMarks = new TrailRenderer[4];

    private CarController controller;
    private float slipThreshold = 0.4f;   // adjust in inspector
    private float brakeThreshold = 50f;   // Nm at wheel for lockup smoke

    private void Start()
    {
        controller = GetComponent<CarController>();
    }

    private void FixedUpdate()
    {
        CheckWheelEffects(controller.frontLWheelCollider, 0); // FL
        CheckWheelEffects(controller.frontRWheelCollider, 1); // FR
        CheckWheelEffects(controller.rearLWheelCollider, 2);  // RL
        CheckWheelEffects(controller.rearRWheelCollider, 3);  // RR
    }

    private void CheckWheelEffects(WheelCollider wheel, int index)
    {
        if (wheel == null) return;

        bool slip = false;
        if (wheel.GetGroundHit(out WheelHit hit))
        {
            // Slip check
            if (Mathf.Abs(hit.forwardSlip) > slipThreshold || Mathf.Abs(hit.sidewaysSlip) > slipThreshold)
                slip = true;

        }

        // Apply to smoke + tire mark
        if (smoke.Length > index && smoke[index] != null)
        {
            if (slip && !smoke[index].isPlaying) smoke[index].Play();
            else if (!slip && smoke[index].isPlaying) smoke[index].Stop();
        }

        if (tireMarks.Length > index && tireMarks[index] != null)
            tireMarks[index].emitting = slip;
    }
}
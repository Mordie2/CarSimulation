// =============================================
// File: Scripts/Vehicle/Settings/VehicleSettings.cs
// Role: Tunables (can be ScriptableObject)
// =============================================
using UnityEngine;


namespace Vehicle
{
    [CreateAssetMenu(fileName = "VehicleSettings", menuName = "Vehicle/Vehicle Settings", order = 0)]
    public class VehicleSettings : ScriptableObject
    {
        [Header("Steering")]
        public float maxSteerAngle = 35f;
        public float steerAtSpeedKmh = 140f;
        public float minSteerScaleLow = 1.0f; // at 0 km/h
        public float minSteerScaleHigh = 0.30f; // at high speed
        public float steerRateDegPerSecLow = 420f;
        public float steerRateDegPerSecHigh = 220f;
        public float steerSmoothing = 0.08f;
        public float steerInputLag = 0.10f;
        public float steerInputFollow = 0.12f;
        public float yawDampAt0Kmh = 0.0f;
        public float yawDampAt120Kmh = 0.25f;


        [Header("Brakes")]
        public float brakeForce = 3000f;
        public float handbrakeForce = 5000f;
        public bool absEnabledDefault = true;
        public float absThreshold = 0.1f;
        public float absReleaseTime = 0.1f;
        public KeyCode absToggleKey = KeyCode.B;


        [Header("Drivetrain")]
        public bool automatic = true;
        public float idleRPM = 900f, maxRPM = 7000f, revLimiterRPM = 7200f;
        public float engineInertia = 0.15f;
        public float shiftUpRPM = 6500f, shiftDownRPM = 1800f;
        public float shiftTime = 0.25f;
        public float engineBrakeTorque = 120f;
        public AnimationCurve torqueCurve = new AnimationCurve(
        new Keyframe(800f, 100f),
        new Keyframe(2500f, 240f),
        new Keyframe(4000f, 300f),
        new Keyframe(6000f, 340f),
        new Keyframe(7200f, 200f)
        );
        [Range(0.5f, 1.0f)] public float drivetrainEfficiency = 0.90f;
        public float torqueMultiplier = 1.4f;
        public float minLaunchRPM = 2500f;
        public float finalDrive = 4.50f;
        public float[] gearRatios = new float[] { -3.90f, 0f, 3.80f, 2.20f, 1.55f, 1.20f, 0.98f };
        public float upshiftSlipBlock = 0.25f;
        public float minShiftInterval = 0.35f;
        public float predictedDownshiftMargin = 1.15f;
        public float reverseTorqueMultiplier = 1.35f;

        [Header("Launch Control")]
        public bool launchControlEnabledDefault = true;
        public bool launchInstantTorqueDefault = false;   // Instant mode = no softening + immediate drive
        public KeyCode launchControlToggleKey = KeyCode.L; // optional keyboard toggle
        public KeyCode launchModeToggleKey = KeyCode.K;    // optional: toggle instant vs smart


        // Rough idle/jitter settings
        public bool roughIdle = true;
        public float idleJitterAmplitude = 80f;
        public float idleJitterSpeed = 2f;


        [Header("Suspension/Aero")]
        public float frontAntiRoll = 16500f;
        public float rearAntiRoll = 14000f;
        public float downforceCoef = 30f;


        [Header("Input")]
        public string dpadYAxisName = "DPadY";
        public float dpadPressThreshold = 0.6f;
        public float dpadRepeatDelay = 0.25f;

        [Header("FX / VFX thresholds")]
        public float smokeSlipThreshold = 0.45f;   // tweak to taste (0.35–0.6 typical)


        public static VehicleSettings DefaultRuntimeCopy()
        {
            var s = ScriptableObject.CreateInstance<VehicleSettings>();
            // defaults already set via field initializers
            return s;
        }
    }
}
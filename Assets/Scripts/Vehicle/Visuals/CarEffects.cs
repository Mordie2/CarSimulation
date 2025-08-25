// CarEffects.cs
using UnityEngine;
using Vehicle;

public class CarEffects : MonoBehaviour
{
    [Header("Smoke Per Wheel (FL, FR, RL, RR)")]
    public ParticleSystem[] smoke = new ParticleSystem[4];

    [Header("Tire Marks Per Wheel (FL, FR, RL, RR)")]
    public TrailRenderer[] tireMarks = new TrailRenderer[4];

    [Header("Lock-up Detection")]
    [SerializeField] private float brakeTorqueMinNm = 30f;  // braking active per-wheel
    [SerializeField] private float lockRpmEps = 8f;   // |rpm| <= eps => likely locked
    [SerializeField] private float lockMinKmh = 6f;   // ignore near stop
    [SerializeField] private float minExpectedRpm = 8f;   // ignore when exp rpm is tiny
    [SerializeField] private float minContactForceN = 250f; // valid ground

    [Header("Smoke Control")]
    [SerializeField] private float baseRateOverTime = 60f;      // particles/sec at intensity=1
    [SerializeField] private float smokeAttackTau = 0.06f;    // quicker rise
    [SerializeField] private float smokeReleaseTau = 0.08f;    // slower fall
    [SerializeField] private float marksOnAt = 0.08f;    // trail start threshold
    [SerializeField] private float marksOffAt = 0.04f;    // trail stop threshold
    [SerializeField, Range(0f, 0.1f)]
    private float zeroSnapAt = 0.02f; // below this, snap to 0 and turn emission off

    [Header("Extra Gates")]
    [SerializeField] private float minKmhForSmoke = 8f;   // never emit below this speed
    [SerializeField] private float driveTorqueMinNm = 20f; // consider wheel "driven" above this

    // Separate thresholds so coasting forwardSlip doesn't make smoke
    [SerializeField] private float sideStart = 0.35f;         // sideways slip -> start
    [SerializeField] private float sideCeil = 0.80f;         // sideways slip -> full
    [SerializeField] private float fwdStartDriven = 0.30f;    // forward slip counted only if driven/braking
    [SerializeField] private float fwdCeilDriven = 0.90f;

    [Header("Smoke Amount")]
    [SerializeField, Min(0f)] private float smokeRateMultiplier = 3f;  // 1 = current, 2 = double
    [SerializeField, Range(0.25f, 1.25f)] private float smokeGamma = 0.8f; // <1 boosts mid-range slip

    private bool[] emittingNow = new bool[4];

    [Header("Debug")]
    [SerializeField] private bool debugSlipTransitions = false;
    [SerializeField] private bool debugSlipWhileActive = false;
    [SerializeField, Range(1, 30)] private int debugWhileHz = 5;

    private CarController controller;
    private Rigidbody rb;
    private float[] intensitySmoothed = new float[4];
    private float[] whileTimers = new float[4];

    private static readonly string[] WheelLabel = { "FL", "FR", "RL", "RR" };

    private void Awake()
    {
        controller = GetComponent<CarController>();
        rb = GetComponent<Rigidbody>();
        for (int i = 0; i < smoke.Length; i++)
        {
            var ps = smoke[i];
            if (ps == null) continue;

            var main = ps.main;
            main.playOnAwake = true;  
            main.prewarm = true;  

            var em = ps.emission;
            em.enabled = true;
            em.rateOverTime = 0f;     

            if (!ps.isPlaying) ps.Play(); 
        }
    }
    public void OnFixedUpdate()
    {
        CheckWheelEffects(controller.frontLWheelCollider, 0); // FL
        CheckWheelEffects(controller.frontRWheelCollider, 1); // FR
        CheckWheelEffects(controller.rearLWheelCollider, 2);  // RL
        CheckWheelEffects(controller.rearRWheelCollider, 3);  // RR
    }

    private void CheckWheelEffects(WheelCollider wheel, int index)
    {
        if (wheel == null) return;

        float kmhAbs = rb ? rb.velocity.magnitude * 3.6f : 0f;
        float mpsAbs = kmhAbs / 3.6f;

        float targetIntensity = 0f;
        bool hasContact = false;

        if (wheel.GetGroundHit(out WheelHit hit) && hit.force >= minContactForceN)
        {
            hasContact = true;

            // --- Sideways slip (always eligible) ---
            float sideMag = Mathf.Abs(hit.sidewaysSlip);
            float slip01 = Mathf.Clamp01((sideMag - sideStart) / Mathf.Max(0.0001f, sideCeil - sideStart));

            // --- Forward slip (only if driven OR braking) ---
            float fwdMag = Mathf.Abs(hit.forwardSlip);
            bool braking = wheel.brakeTorque >= brakeTorqueMinNm;
            bool driven = wheel.motorTorque > driveTorqueMinNm;

            float fwd01 = 0f;
            if (driven || braking)
            {
                fwd01 = Mathf.Clamp01((fwdMag - fwdStartDriven) / Mathf.Max(0.0001f, fwdCeilDriven - fwdStartDriven));
            }

            float radius = Mathf.Max(0.01f, wheel.radius);
            float expRPM = (mpsAbs / (2f * Mathf.PI * radius)) * 60f;
            bool locked = braking && kmhAbs >= lockMinKmh && expRPM >= minExpectedRpm && Mathf.Abs(wheel.rpm) <= lockRpmEps;
            float lock01 = locked ? 1f : 0f;
            targetIntensity = Mathf.Max(slip01, fwd01, lock01);
            if (kmhAbs < minKmhForSmoke) targetIntensity = 0f;
            // ---- Debugging
            bool activeNow = targetIntensity > 0.02f;
            whileTimers[index] += Time.fixedDeltaTime;
            float interval = 1f / Mathf.Max(1, debugWhileHz);

            if (debugSlipWhileActive && activeNow && whileTimers[index] >= interval)
            {
                whileTimers[index] = 0f;
                string mode;
                if (lock01 > Mathf.Max(slip01, fwd01)) mode = "LOCK";
                else if (slip01 >= fwd01) mode = "SIDE";
                else mode = "FWD";

                Debug.Log(
                    $"[CarEffects] {WheelLabel[index]} {mode} | " +
                    $"side={sideMag:F2} fwd={fwdMag:F2} | side01={slip01:F2} fwd01={fwd01:F2} | " +
                    $"exp={expRPM:F0}rpm act={wheel.rpm:F0}rpm | braking={braking} lock={locked}"
                );
            }
            if (!activeNow) whileTimers[index] = 0f;

        }
            float curr = intensitySmoothed[index];
        float tau = (targetIntensity > curr) ? Mathf.Max(0.0001f, smokeAttackTau)
                                              : Mathf.Max(0.0001f, smokeReleaseTau);
        float a = 1f - Mathf.Exp(-Time.fixedDeltaTime / tau);
        curr = Mathf.Lerp(curr, targetIntensity, a);
        if (targetIntensity <= 0f && curr <= zeroSnapAt) curr = 0f;

        intensitySmoothed[index] = curr;

        if (smoke.Length > index && smoke[index] != null)
        {
            bool wantEmit = hasContact && curr > zeroSnapAt;

            var ps = smoke[index];
            var em = ps.emission;

            if (wantEmit)
            {
                if (!ps.isPlaying) ps.Play();
                em.enabled = true;
                float visual = Mathf.Pow(Mathf.Clamp01(curr), smokeGamma);
                em.rateOverTime = baseRateOverTime * smokeRateMultiplier * visual;
            }
            else
            {
                if (emittingNow[index])
                {
                    em.enabled = false;
                    ps.Clear(false);
                }
                else
                {
                    em.enabled = false;
                }
                if (!ps.isPlaying) ps.Play();
            }

            emittingNow[index] = wantEmit;
        }

        /*
        // Skid marks with hysteresis
        if (tireMarks.Length > index && tireMarks[index] != null)
        {
            bool on = tireMarks[index].emitting;
            bool wantOn = curr >= marksOnAt;
            bool wantOff = curr <= marksOffAt;

            if (!on && wantOn) tireMarks[index].emitting = true;
            else if (on && wantOff) tireMarks[index].emitting = false;
        }
        */
    }
}

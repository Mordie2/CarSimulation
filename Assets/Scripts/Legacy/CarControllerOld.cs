using System.Collections;
using UnityEngine;
using FMODUnity;
using FMOD.Studio;

public class CarControllerOld : MonoBehaviour
{
    // ── Editor ────────────────────────────────────────────────────────────────────
    public WheelCollider frontLWheelCollider, frontRWheelCollider, rearLWheelCollider, rearRWheelCollider;
    public Transform frontLWheelTransform, frontRWheelTransform, rearLWheelTransform, rearRWheelTransform;
    public float maxSteerAngle = 35f;
    public float motorForce = 1500f;       // kept for reference, not used directly with the torque curve path
    public float brakeForce = 3000f;
    public float handbrakeForce = 5000f;

    public float EngineRPM => engineRPM;  


    public float maxSpeed;
    public float maxReverseSpeed;
    public float speedThreshold;
    public float absThreshold = 0.1f;
    public float absReleaseTime = 0.1f;
    public float driftThreshold;

    // ── Runtime state ─────────────────────────────────────────────────────────────
    public bool handbrakeInput;
    public bool playPauseSmoke = false;
    private float horizontalInput;
    public float speed;
    private float accelerationInput;     // -1..1 (negative = reverse request)
    private float brakeInput;            // 0..1 service brake
    private float handbrakeValue;
    private float steeringAngle;
    private bool isGoingForward;
    private bool testSoundInput;
    private Rigidbody rb;

    // ABS coroutines
    private Coroutine absCoroutineFrontLeft;
    private Coroutine absCoroutineFrontRight;
    private Coroutine absCoroutineRearLeft;
    private Coroutine absCoroutineRearRight;

    // ── Engine / Transmission ─────────────────────────────────────────────────────
    [Header("Engine & Transmission")]
    public bool automatic = true;
    public float idleRPM = 900f, maxRPM = 7000f, revLimiterRPM = 7200f;
    public float engineInertia = 0.15f;
    public float shiftUpRPM = 6500f, shiftDownRPM = 1800f;
    public float shiftTime = 0.25f;
    public float engineBrakeTorque = 120f;
    public AnimationCurve torqueCurve;
    [Range(0.5f, 1.0f)] public float drivetrainEfficiency = 0.90f;
    public float torqueMultiplier = 1.4f;
    public float minLaunchRPM = 2500f;
    public float finalDrive = 4.50f;

    // Steering tuning
    [SerializeField] float steerAtSpeedKmh = 140f;   // speed where we’re ~40% of max
    [SerializeField] float minSteerScale = 0.40f;  // 40% of max angle at/above that speed
    [SerializeField] float steerRateDegPerSecLow = 420f; // how fast wheels can turn at low speed
    [SerializeField] float steerRateDegPerSecHigh = 220f; // at high speed
    [SerializeField] float steerSmoothing = 0.08f;        // extra smoothing (0..0.2)
    float _steerAngleSmoothed = 0f;
    [SerializeField] float steerInputLag = 0.10f;    // ~100 ms lag
    [SerializeField] float steerInputFollow = 0.12f; // follow smoothing

    [SerializeField] float yawDampAt0Kmh = 0.0f;
    [SerializeField] float yawDampAt120Kmh = 0.25f;  // full damping at your top speed

    [SerializeField] float minSteerScaleHigh = 0.30f; // ~30% max angle at vmax



    // Ratios: [0]=Reverse (NEGATIVE), [1]=Neutral (0), [2+]=Forward
    public float[] gearRatios = new float[] { -3.90f, 0f, 3.80f, 2.20f, 1.55f, 1.20f, 0.98f };

    [SerializeField] float upshiftSlipBlock = 0.25f;
    [SerializeField] float minShiftInterval = 0.35f;
    [SerializeField] float predictedDownshiftMargin = 1.15f;

    // Traction control
    [SerializeField] bool tractionControl = true;
    [SerializeField] float tcSlip = 0.35f;
    [SerializeField] float tcGain = 0.55f;

    float lastShiftTime = -999f;
    public int CurrentGear => gearIndex;

    [Header("Chassis/Aero")]
    [SerializeField] float frontAntiRoll = 16500f;
    [SerializeField] float rearAntiRoll = 14000f;
    [SerializeField] float downforceCoef = 30f;   // N per (m/s)^2


    // Reverse tuning
    [Header("Reverse Tuning")]
    [SerializeField] float reverseRatio = -3.90f;      // was -2.90f — shorter = more wheel torque
    [SerializeField] float reverseTorqueMultiplier = 1.35f; // extra shove only in Reverse
    [SerializeField] bool reverseIgnoresTC = true;     // disable/relax TCS when reversing

    [Header("Assists")]
    public bool absEnabled = true;                 // default ON
    public KeyCode absToggleKey = KeyCode.B;       // press B to toggle

    // Manual shifting (D-Pad)
    [SerializeField] private string dpadYAxisName = "DPadY"; // must match your Input Manager axis
    [SerializeField] private float dpadPressThreshold = 0.6f; // how far you must press
    [SerializeField] private float dpadRepeatDelay = 0.25f;   // debounce so holding doesn't repeat too fast
    private float _lastDpadY = 0f;
    private float _nextDpadTime = 0f;



    // state
    int gearIndex = 2; // start in 1st
    float engineRPM = 1000f;
    float engineTorque;
    bool isShifting = false;
    float shiftTimer = 0f;
    float clutch = 1f;

    [Header("Engine Idle Behavior")]
    public bool roughIdle = true;
    public float idleJitterAmplitude = 80f;   // max +/- variation in RPM
    public float idleJitterSpeed = 2f;        // how fast it wiggles

    private float idleNoiseOffset;

    // ── FMOD ──────────────────────────────────────────────────────────────────────
    private EventInstance engineInstance;
    private EventInstance shiftInstance;
    private EventInstance windInstance;
    private EventInstance tiresInstance;
    private bool engineStarted = false;

    [SerializeField, Range(0.01f, 0.3f)] private float rpmParamSmoothing = 0.06f;
    // REPLACED: private float engineRPM01Smoothed = 0f;
    private float engineRPMSmoothed = 0f;   // smoothed in actual RPM (0..7200)

    [SerializeField] private bool sendThrottleParam = true;
    private const string EngineParamName = "EngineRPM";
    private const string ThrottleParamName = "Throttle";

    [Header("Wind Audio")]
    public bool windEnabled = true;
    public KeyCode windToggleKey = KeyCode.N;         // press N to toggle wind

    [SerializeField] float windStartAtKmh = 15f;      // when wind begins audibly
    [SerializeField] float windFullAtKmh = 220f;     // mapped to Speed=1
    [SerializeField, Range(0.01f, 0.5f)] float windParamSmoothing = 0.08f;

    private float windSpeed01Smoothed = 0f;
    float rollSmoothed;
    float skidSmoothed;

    [Header("Audio Toggles")]
    public bool engineSoundEnabled = true;                 // start with engine sound on
    public KeyCode engineSoundToggleKey = KeyCode.M;       // press M to toggle

    // ── Unity ─────────────────────────────────────────────────────────────────────
    private void Start()
    {
        rb = GetComponent<Rigidbody>();

        if (torqueCurve == null || torqueCurve.length < 2)
        {
            torqueCurve = new AnimationCurve(
                new Keyframe(800f, 100f),
                new Keyframe(2500f, 240f),
                new Keyframe(4000f, 300f),
                new Keyframe(6000f, 340f),
                new Keyframe(7200f, 300f)
            );
        }
        if (gearRatios != null && gearRatios.Length > 0)
            gearRatios[0] = reverseRatio;

        AdjustFrictionSettings(frontLWheelCollider);
        AdjustFrictionSettings(frontRWheelCollider);
        AdjustFrictionSettings(rearLWheelCollider);
        AdjustFrictionSettings(rearRWheelCollider);

        if (engineSoundEnabled && FMODEvents.instance != null && AudioManager.instance != null)
        {
            EnsureEngineInstance();
            engineInstance.start();
            engineStarted = true;
        }


        // Wind loop
        if (windEnabled && FMODEvents.instance != null && AudioManager.instance != null)
        {
            EnsureWindInstance();
            if (windInstance.isValid()) windInstance.start();
        }

        idleNoiseOffset = Random.value * 100f;
    }

    void AdjustFrictionSettings(
     WheelCollider c,
     float fLong = 0.7f, float rLong = 1.1f,   // + grip accel/brake
     float fLat = 1.5f, float rLat = 1.8f)   // + lateral grip (rear still looser)
    {
        // Longitudinal
        var fwd = c.forwardFriction;
        fwd.extremumSlip = 0.6f;    // peak grip happens at higher slip
        fwd.asymptoteSlip = 1.2f;   // takes longer to fall off
        fwd.extremumValue = 1.1f;   // slightly more peak grip
        fwd.asymptoteValue = 0.85f; // softer loss after grip

        fwd.stiffness = (c == frontLWheelCollider || c == frontRWheelCollider) ? fLong : rLong;
        c.forwardFriction = fwd;

        // Lateral
        var lat = c.sidewaysFriction;
        lat.extremumSlip = 0.5f;
        lat.asymptoteSlip = 1.0f;
        lat.extremumValue = 1.0f;
        lat.asymptoteValue = 0.8f;

        lat.stiffness = (c == frontLWheelCollider || c == frontRWheelCollider) ? fLat : rLat;
        c.sidewaysFriction = lat;

        // Suspension – a touch firmer with more damping to kill float
        var sp = c.suspensionSpring;
        sp.spring = 85000f;  // was 80k
        sp.damper = 7500f;   // was 6.5k
        c.suspensionSpring = sp;
    }



    private void FixedUpdate()
    {
        speed = rb.velocity.magnitude;

        GetInput();
        CheckDirection();
        Steer();

        // Set base brake torques first
        Decelerate();
        ApplyHandbrake();

        // Then let ABS modulate those torques
        ApplyABS();

        ApplyAntiRollAndAero();

        Accelerate();
        UpdateWheelPoses();

        if (engineSoundEnabled && engineStarted && engineInstance.isValid())
        {
            engineInstance.set3DAttributes(RuntimeUtils.To3DAttributes(transform.position));

            float rpmClamped = Mathf.Clamp(engineRPM, 0f, revLimiterRPM);
            engineRPMSmoothed = Mathf.Lerp(
                engineRPMSmoothed,
                rpmClamped,
                1f - Mathf.Exp(-Time.fixedDeltaTime / rpmParamSmoothing)
            );

            AudioManager.instance.SetInstanceParameter(engineInstance, EngineParamName, engineRPMSmoothed);

            if (sendThrottleParam)
            {
                float throttle01 = Mathf.Clamp01(Mathf.Abs(accelerationInput));
                AudioManager.instance.SetInstanceParameter(engineInstance, ThrottleParamName, throttle01);
            }
        }

        if (windEnabled && windInstance.isValid())
        {
            windInstance.set3DAttributes(RuntimeUtils.To3DAttributes(transform.position));

            float kmh = rb.velocity.magnitude * 3.6f;
            float raw01 = Mathf.InverseLerp(windStartAtKmh, windFullAtKmh, kmh);
            raw01 = Mathf.Clamp01(raw01);

            // Smooth to avoid zipper noise
            windSpeed01Smoothed = Mathf.Lerp(
                windSpeed01Smoothed,
                raw01,
                1f - Mathf.Exp(-Time.fixedDeltaTime / windParamSmoothing)
            );

            AudioManager.instance.SetInstanceParameter(windInstance, "Speed", windSpeed01Smoothed);
        }
        //UpdateTireAudio();


    }



    private void Update()
    {
        DebugControllerButtons();
        if (Input.GetKeyDown(engineSoundToggleKey))
        {
            SetEngineSoundEnabled(!engineSoundEnabled);
        }
        if (Input.GetKeyDown(windToggleKey))
        {
            SetWindEnabled(!windEnabled);
        }
    }


    private void CheckDirection()
    {
        var dot = Vector3.Dot(transform.forward, rb.velocity);
        if (rb.velocity.magnitude <= speedThreshold) return;
        isGoingForward = dot >= 0.1f;
    }

    // ── Input (OLD reverse logic integrated) ──────────────────────────────────────
    private void GetInput()
    {
        // Keyboard
        float kSteer = Input.GetAxis("Horizontal");
        float kVert = Input.GetAxis("Vertical"); // W/S or Up/Down
        bool kHB = Input.GetKey(KeyCode.Space);

        // Gamepad
        float gpSteer = Input.GetAxis("JoystickHorizontal");
        float gpAccel = Mathf.Abs(Input.GetAxis("ControllerAccelerate")); // RT
        float gpBrake = Mathf.Abs(Input.GetAxis("ControllerBrake"));      // LT
        bool gpHB = Input.GetButton("Handbrake");

        // --- Manual shifting with D-Pad (only when automatic is OFF) ---
        float dpadY = Input.GetAxisRaw(dpadYAxisName); // Up ≈ +1, Down ≈ -1 (per your Input Manager)

        if (!automatic && Time.time >= _nextDpadTime)
        {
            // Edge detection: act only when crossing the threshold
            if (dpadY > dpadPressThreshold && _lastDpadY <= dpadPressThreshold)
            {
                ShiftUp();
                _nextDpadTime = Time.time + dpadRepeatDelay;
            }
            else if (dpadY < -dpadPressThreshold && _lastDpadY >= -dpadPressThreshold)
            {
                ShiftDown();
                _nextDpadTime = Time.time + dpadRepeatDelay;
            }
        }
        _lastDpadY = dpadY;


        // Pick steering source
        horizontalInput = Mathf.Abs(kSteer) > 0.001f ? kSteer : gpSteer;

        // Separate throttle / brake with reverse logic from old controller:
        // - Negative input means reverse request when (nearly) stopped.
        // - While moving forward, negative input acts as brake.
        float throttle = 0f;
        float serviceBrake = 0f;

        bool forwardish = Vector3.Dot(transform.forward, rb.velocity) > 0f;
        float speedMs = rb.velocity.magnitude;

        // Keyboard vertical
        if (Mathf.Abs(kVert) > 0.001f)
        {
            if (kVert > 0f)
            {
                throttle = kVert;
            }
            else // kVert < 0
            {
                if (forwardish || speedMs > 1f)
                    serviceBrake = -kVert;  // braking while moving forward
                else
                    throttle = kVert;       // reverse request at (near) stop
            }
        }

        // Gamepad: RT = throttle, LT = brake-or-reverse like old logic
        if (gpAccel > 0.001f) throttle = gpAccel;
        if (gpBrake > 0.001f)
        {
            if (forwardish || speedMs > 1f)
                serviceBrake = gpBrake;   // braking while moving forward
            else
                throttle = -gpBrake;      // reverse request at (near) stop
        }

        // Small deadzone to avoid creep
        if (Mathf.Abs(throttle) < 0.05f) throttle = 0f;
        if (serviceBrake < 0.05f) serviceBrake = 0f;

        // --- Arcade behavior while in REVERSE ---
        // In Reverse: LT/S = reverse throttle, RT/W = brake
        if (gearIndex == 0)
        {
            // Keyboard
            if (kVert < -0.001f)          // S / Down
                throttle = kVert;         // negative → reverse throttle
            if (kVert > 0.001f)          // W / Up
                serviceBrake = Mathf.Max(serviceBrake, kVert); // forward pedal acts as brake

            // Gamepad
            if (gpBrake > 0.001f)         // LT
                throttle = -gpBrake;      // reverse throttle magnitude
            if (gpAccel > 0.001f)         // RT
                serviceBrake = Mathf.Max(serviceBrake, gpAccel); // RT brakes in reverse
        }



        accelerationInput = Mathf.Clamp(throttle, -1f, 1f); // negative allowed (reverse)
        brakeInput = Mathf.Clamp01(serviceBrake);

        // --- Kill service brake while actively reversing ---
        if (gearIndex == 0 && accelerationInput < -0.01f) // negative = reverse throttle
        {
            brakeInput = 0f; // ignore any stray RT/W noise while backing up
        }


        handbrakeInput = kHB || gpHB;

        // Manual shift (optional)
        if (!automatic)
        {
            if (Input.GetKeyDown(KeyCode.E)) ShiftUp();
            if (Input.GetKeyDown(KeyCode.Q)) ShiftDown();

            if (Input.GetKeyDown(KeyCode.Joystick1Button5)) ShiftUp();   // D-Pad Up
            if (Input.GetKeyDown(KeyCode.Joystick1Button6)) ShiftDown(); // D-Pad Down
        }

        // ABS toggle
        if (Input.GetKeyDown(absToggleKey))
        {
            absEnabled = !absEnabled;
            if (!absEnabled) StopABSCoroutines(); // kill any active ABS pulses
            Debug.Log($"ABS: {(absEnabled ? "ON" : "OFF")}");
        }

    }

    // ── Drive ─────────────────────────────────────────────────────────────────────
    private void Accelerate()
    {
        // Throttle magnitude 0..1 (always positive). Direction comes from gear ratio sign.
        float throttle01 = (gearIndex == 0)
            ? Mathf.Clamp01(-accelerationInput)   // reverse request magnitude (LT / S key)
            : Mathf.Clamp01(accelerationInput);   // forward request magnitude

        float reverseReq = Mathf.Clamp01(-accelerationInput); // for logic if you still use it elsewhere

        UpdateTransmission(throttle01, reverseReq);

        float gear = GetCurrentRatio(); // negative in reverse, positive in forward
        float baseEngineTorque = torqueCurve.Evaluate(engineRPM);
        float idleAssistNm = 50f;

        // Engine torque is a magnitude, not signed
        engineTorque = (baseEngineTorque + idleAssistNm) * throttle01 * torqueMultiplier;

        // Extra shove only in reverse
        if (gearIndex == 0)
            engineTorque *= reverseTorqueMultiplier;


        // Rev limiter cut
        if (engineRPM >= revLimiterRPM && throttle01 > 0.1f)
            engineTorque = 0f;

        // Direction comes from gear sign (reverse gear < 0)
        float driveTorque = engineTorque * gear * finalDrive * drivetrainEfficiency * clutch;
        float perWheelTorque = driveTorque * 0.5f;

        if (roughIdle && throttle01 <= 0.01f && gearIndex <= 2) // basically idle state
        {
            float noise = Mathf.PerlinNoise(Time.time * idleJitterSpeed, idleNoiseOffset);
            float centered = (noise - 0.5f) * 2f; // -1..1
            engineRPM += centered * idleJitterAmplitude;
        }

        // Traction control (optional)
        if (tractionControl)
        {
            float slip = GetDrivenForwardSlipAbs();
            if (slip > tcSlip)
            {
                float cut01 = Mathf.InverseLerp(tcSlip, tcSlip * 2f, slip);
                float factor = Mathf.Lerp(1f, tcGain, cut01);
                perWheelTorque *= factor;
            }
        }

        rearLWheelCollider.motorTorque = perWheelTorque;
        rearRWheelCollider.motorTorque = perWheelTorque;

        // --- Engine braking (coast) ---
        bool inGear = !Mathf.Approximately(gearRatios[gearIndex], 0f);
        bool clutchEngaged = clutch > 0.95f;
        bool noPedal = Mathf.Abs(accelerationInput) < 0.05f;        // no throttle request
        bool noServiceBrake = brakeInput < 0.05f && !handbrakeInput; // don’t double-count against foot brake

        if (inGear && clutchEngaged && noPedal && noServiceBrake)
        {
            // Wheel-space braking torque produced by engine drag.
            // Proportional to current ratio & RPM so it’s stronger at high revs.
            float ratioAbs = Mathf.Abs(GetCurrentRatio() * finalDrive);
            float rpmFactor = Mathf.InverseLerp(idleRPM * 0.9f, maxRPM, Mathf.Clamp(engineRPM, idleRPM * 0.9f, maxRPM));
            float coastNmAtWheel = engineBrakeTorque * ratioAbs * drivetrainEfficiency * rpmFactor;

            // Split across the driven axle (RWD here)
            float perWheelCoast = coastNmAtWheel * 0.5f;

            // Add to brakeTorque so ABS can still intervene
            rearLWheelCollider.brakeTorque += perWheelCoast;
            rearRWheelCollider.brakeTorque += perWheelCoast;
        }

    }


    float GetCurrentRatio() => gearRatios[Mathf.Clamp(gearIndex, 0, gearRatios.Length - 1)];

    void UpdateTransmission(float throttle01, float reverseRequest)
    {
        float wl = rearLWheelCollider.rpm;
        float wr = rearRWheelCollider.rpm;
        float wheelRPM = (wl + wr) * 0.5f;

        float ratio = GetCurrentRatio() * finalDrive;
        bool inNeutral = Mathf.Approximately(gearRatios[gearIndex], 0f);

        // Target RPM
        float targetRPM;
        if (clutch < 0.99f || inNeutral)
        {
            targetRPM = Mathf.Lerp(idleRPM, maxRPM, throttle01);
        }
        else
        {
            float speedWheelRPM = GetWheelRPMFromGround();
            float sensedWheelRPM = Mathf.Abs(wheelRPM);
            float usedWheelRPM = Mathf.Min(sensedWheelRPM, speedWheelRPM * 1.10f); // allow ~10% slip

            targetRPM = Mathf.Max(usedWheelRPM * Mathf.Abs(ratio), idleRPM);

            bool lowGear = (gearIndex == 0) || (gearIndex == 2) || (gearIndex == 3);
            if (throttle01 > 0.15f && lowGear)
                targetRPM = Mathf.Max(targetRPM, minLaunchRPM);

        }

        float follow = Time.fixedDeltaTime / Mathf.Max(0.0001f, engineInertia);
        engineRPM = Mathf.Lerp(engineRPM, targetRPM, Mathf.Clamp01(follow));
        engineRPM = Mathf.Clamp(engineRPM, idleRPM * 0.8f, revLimiterRPM + 200f);

        // ===== Old-style reverse behavior in Auto =====
        // If negative accel at near stop → go to Reverse instantly.
        if (automatic)
        {
            if (accelerationInput < -0.05f && rb.velocity.magnitude < 1.0f)
            {
                SetReverseInstant();
            }
            // If we are in Reverse and player stops requesting reverse, and we're nearly stopped → 1st
            else if (gearIndex == 0 && accelerationInput >= -0.05f && rb.velocity.magnitude < 1.0f)
            {
                SetNeutral();
                StartShift(2); // 1st
            }
        }

        // ===== Forward auto-shift (only when not in Reverse) =====
        if (automatic && gearIndex >= 2 && !isShifting && Time.time - lastShiftTime > minShiftInterval)
        {
            // Pre-emptive upshift using ground-speed RPM (slip-proof)
            float preShiftRPM = Mathf.Min(shiftUpRPM, revLimiterRPM - 200f);
            float speedWheelRPM = GetWheelRPMFromGround();

            if (gearIndex < gearRatios.Length - 1)
            {
                float predictedNextRPM = speedWheelRPM * Mathf.Abs(gearRatios[gearIndex + 1] * finalDrive);
                float mechRPM = speedWheelRPM * Mathf.Abs(gearRatios[gearIndex] * finalDrive);

                bool rpmHigh = mechRPM >= preShiftRPM;
                bool nextRPMHealthy = predictedNextRPM > shiftDownRPM * predictedDownshiftMargin;
                bool notSlipping = GetDrivenForwardSlipAbs() < upshiftSlipBlock;

                if (rpmHigh && nextRPMHealthy && notSlipping)
                {
                    StartShift(gearIndex + 1);
                    lastShiftTime = Time.time;
                }

                // Hard redline safety
                if (engineRPM >= revLimiterRPM - 50f && !isShifting)
                {
                    StartShift(Mathf.Min(gearIndex + 1, gearRatios.Length - 1));
                    lastShiftTime = Time.time;
                }
            }

            // Downshift (forward gears only)
            if (gearIndex > 2 && engineRPM < shiftDownRPM)
            {
                StartShift(gearIndex - 1);
                lastShiftTime = Time.time;
            }

            // From Neutral to 1st on throttle
            if (gearIndex == 1 && throttle01 > 0.1f)
            {
                StartShift(2);
                lastShiftTime = Time.time;
            }

        }

        // Shift timing & clutch
        if (isShifting)
        {
            engineTorque = 0;
            shiftTimer += Time.fixedDeltaTime;
            float t = shiftTimer / shiftTime;
            clutch = (t < 0.5f) ? Mathf.Lerp(1f, 0f, t * 2f) : Mathf.Lerp(0f, 1f, (t - 0.5f) * 2f);

            if (shiftTimer >= shiftTime)
            {
                isShifting = false;
                shiftTimer = 0f;
                clutch = 1f;
            }

        }
        else
        {
            clutch = Mathf.MoveTowards(clutch, 1f, Time.fixedDeltaTime * 5f);
        }
    }

    private void PlayShiftSFX()
    {
        if (FMODEvents.instance != null && AudioManager.instance != null)
        {
            AudioManager.instance.PlayOneShot(FMODEvents.instance.Shift, transform.position);
        }
    }

    void StartShift(int newIndex)
    {
        if (newIndex == gearIndex) return;
        gearIndex = Mathf.Clamp(newIndex, 0, gearRatios.Length - 1);
        isShifting = true;
        shiftTimer = 0f;
        bool wasDownshift = newIndex < gearIndex;
        if (!wasDownshift && gearIndex > 2 && EngineRPM > 4000f)
        {
            PlayShiftSFX();
        }

        if (wasDownshift && EngineRPM > 2000f)
        {
            PlayShiftSFX();
        }

    }

    public void ShiftUp() { if (gearIndex < gearRatios.Length - 1) StartShift(gearIndex + 1) ; }
    public void ShiftDown()
    {
        int target = gearIndex - 1;
        if (target == 0 && rb.velocity.magnitude > 0.5f) target = 1; // don’t drop into R while moving
        StartShift(target);
    }

    public void SetNeutral() { StartShift(1); }
    public void SetReverse() { if (rb.velocity.magnitude < 0.5f) StartShift(0); }

    // Instant reverse (no clutch blend) to avoid creep at zero speed
    void SetReverseInstant()
    {
        gearIndex = 0; // R
        isShifting = false;
        shiftTimer = 0f;
        clutch = 1f;
    }

    // ── Brakes / ABS ──────────────────────────────────────────────────────────────
    private void Decelerate()
    {
        bool reversingThrottle = (gearIndex == 0 && accelerationInput < -0.01f);

        float totalBrake01 = reversingThrottle && !handbrakeInput
            ? 0f
            : GetTotalBrake01();

        float brakeTorque = totalBrake01 * brakeForce;

        frontLWheelCollider.brakeTorque = brakeTorque;
        frontRWheelCollider.brakeTorque = brakeTorque;
        rearLWheelCollider.brakeTorque = brakeTorque;
        rearRWheelCollider.brakeTorque = brakeTorque;
    }




    private void ApplyABS()
    {
        if (!absEnabled) return;                 // toggle OFF
        if (GetTotalBrake01() <= 0.01f) return;  // no brake demand → skip
        CheckWheelSpin(frontLWheelCollider);
        CheckWheelSpin(frontRWheelCollider);
        CheckWheelSpin(rearLWheelCollider);
        CheckWheelSpin(rearRWheelCollider);
    }

    void StopABSCoroutines()
    {
        if (absCoroutineFrontLeft != null) { StopCoroutine(absCoroutineFrontLeft); absCoroutineFrontLeft = null; }
        if (absCoroutineFrontRight != null) { StopCoroutine(absCoroutineFrontRight); absCoroutineFrontRight = null; }
        if (absCoroutineRearLeft != null) { StopCoroutine(absCoroutineRearLeft); absCoroutineRearLeft = null; }
        if (absCoroutineRearRight != null) { StopCoroutine(absCoroutineRearRight); absCoroutineRearRight = null; }

        // Re-apply base brake torque so a released wheel doesn't stay at 0
        float targetBrakeTorque = GetTotalBrake01() * brakeForce;
        frontLWheelCollider.brakeTorque = targetBrakeTorque;
        frontRWheelCollider.brakeTorque = targetBrakeTorque;
        rearLWheelCollider.brakeTorque = targetBrakeTorque;
        rearRWheelCollider.brakeTorque = targetBrakeTorque;
    }


    void ApplyAntiRollAndAero()
    {
        // --- Anti-roll ---
        ApplyAntiRoll(frontLWheelCollider, frontRWheelCollider, frontAntiRoll);
        ApplyAntiRoll(rearLWheelCollider, rearRWheelCollider, rearAntiRoll);

        // --- Downforce (speed^2) ---
        float v = rb.velocity.magnitude; // m/s
        rb.AddForce(-transform.up * downforceCoef * v * v, ForceMode.Force);
    }

    void ApplyAntiRoll(WheelCollider left, WheelCollider right, float antiRoll)
    {
        bool groundedL = left.GetGroundHit(out var _);
        bool groundedR = right.GetGroundHit(out var __);

        float travelL = 1f, travelR = 1f; // 1 = fully extended, 0 = fully compressed
        if (groundedL)
        {
            float comp = (-left.transform.InverseTransformPoint(left.transform.position).y - left.suspensionDistance);
            travelL = 1f - Mathf.Clamp01(comp / left.suspensionDistance);
        }
        if (groundedR)
        {
            float comp = (-right.transform.InverseTransformPoint(right.transform.position).y - right.suspensionDistance);
            travelR = 1f - Mathf.Clamp01(comp / right.suspensionDistance);
        }

        float antiRollForce = (travelL - travelR) * antiRoll;
        if (groundedL) rb.AddForceAtPosition(left.transform.up * -antiRollForce, left.transform.position);
        if (groundedR) rb.AddForceAtPosition(right.transform.up * antiRollForce, right.transform.position);
    }



    private void CheckWheelSpin(WheelCollider wheelCollider)
    {
        float targetBrakeTorque = GetTotalBrake01() * brakeForce;

        if (!wheelCollider.GetGroundHit(out var hit))
            return;

        if (targetBrakeTorque <= 0f || handbrakeInput)
        {
            wheelCollider.brakeTorque = targetBrakeTorque; // will be 0 → clears any leftover torque
            return;
        }

        if (Mathf.Abs(hit.forwardSlip) > absThreshold)
        {
            StartCoroutine(ReleaseBrakePressure(wheelCollider));
        }
        else
        {
            wheelCollider.brakeTorque = targetBrakeTorque;
        }
    }



    private IEnumerator ReleaseBrakePressure(WheelCollider wheelCollider)
    {
        // Release
        wheelCollider.brakeTorque = 0f;
        yield return new WaitForSeconds(absReleaseTime);

        // Restore to the *current* demand (not an old saved value)
        float targetBrakeTorque = GetTotalBrake01() * brakeForce;
        wheelCollider.brakeTorque = targetBrakeTorque;

        if (wheelCollider == frontLWheelCollider) absCoroutineFrontLeft = null;
        else if (wheelCollider == frontRWheelCollider) absCoroutineFrontRight = null;
        else if (wheelCollider == rearLWheelCollider) absCoroutineRearLeft = null;
        else if (wheelCollider == rearRWheelCollider) absCoroutineRearRight = null;
    }


    // ── Steering & Wheels ─────────────────────────────────────────────────────────
    private float _steerLagState = 0f; // internal state

    private void Steer()
    {
        float kmh = rb.velocity.magnitude * 3.6f;

        // --- 1) Input delay + follow smoothing (feels like the wheel "follows" your thumb) ---
        float lagAlpha = 1f - Mathf.Exp(-Time.fixedDeltaTime / Mathf.Max(0.001f, steerInputLag));
        float followAlpha = 1f - Mathf.Exp(-Time.fixedDeltaTime / Mathf.Max(0.001f, steerInputFollow));

        // first low-pass (lag), then a second gentle follow
        _steerLagState = Mathf.Lerp(_steerLagState, horizontalInput, lagAlpha);
        float delayedIn = Mathf.Lerp(_steerLagState, horizontalInput, followAlpha);

        // --- 2) Speed based angle scaling (clamp harder at speed) ---
        float t = Mathf.Clamp01(kmh / steerAtSpeedKmh);
        // go from 1.0 @ low speed to minSteerScaleHigh @ high speed (more than before)
        float steerScale = Mathf.Lerp(1f, minSteerScaleHigh, Mathf.SmoothStep(0f, 1f, t));

        // --- 3) Yaw damping: if we're already rotating, subtract some command ---
        // Scale damping with speed so it matters more at high speed.
        float yaw = rb.angularVelocity.y; // + = turning left
                                          // use 120 instead of 200 as "top speed" reference
        float yawDamp = Mathf.Lerp(yawDampAt0Kmh, yawDampAt120Kmh, Mathf.Clamp01(kmh / 120f));

        float cmd = delayedIn - yaw * yawDamp;

        // --- 4) Speed-based steering rate limit (slower at speed) ---
        float steerRate = Mathf.Lerp(steerRateDegPerSecLow, steerRateDegPerSecHigh, t);

        // Target angle after scaling
        float target = maxSteerAngle * steerScale * Mathf.Clamp(cmd, -1f, 1f);

        // Rate-limit towards target
        float maxStep = steerRate * Time.fixedDeltaTime;
        _steerAngleSmoothed = Mathf.MoveTowards(_steerAngleSmoothed, target, maxStep);

        // Final tiny smoothing to remove last bit of twitch
        steeringAngle = Mathf.Lerp(
            steeringAngle,
            _steerAngleSmoothed,
            1f - Mathf.Exp(-Time.fixedDeltaTime / Mathf.Max(0.001f, steerSmoothing))
        );

        frontLWheelCollider.steerAngle = steeringAngle;
        frontRWheelCollider.steerAngle = steeringAngle;
    }




    public void ApplyHandbrake()
    {
        handbrakeValue = handbrakeInput ? 1 : 0;
        float hb = handbrakeValue * handbrakeForce;
        rearLWheelCollider.brakeTorque += hb;
        rearRWheelCollider.brakeTorque += hb;
    }

    private void UpdateWheelPoses()
    {
        UpdateWheelPose(frontLWheelCollider, frontLWheelTransform);
        UpdateWheelPose(frontRWheelCollider, frontRWheelTransform);
        UpdateWheelPose(rearLWheelCollider, rearLWheelTransform);
        UpdateWheelPose(rearRWheelCollider, rearRWheelTransform);
    }

    private void UpdateWheelPose(WheelCollider collider, Transform trans)
    {
        Vector3 pos; Quaternion quat;
        collider.GetWorldPose(out pos, out quat);
        trans.position = pos;
        trans.rotation = quat;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────
    float GetWheelRPMFromGround()
    {
        float r = rearLWheelCollider.radius;
        float wheelRPS = rb.velocity.magnitude / (2f * Mathf.PI * r);
        return wheelRPS * 60f;
    }

    float GetDrivenForwardSlipAbs()
    {
        float sum = 0f; int n = 0;
        if (rearLWheelCollider.GetGroundHit(out var hl)) { sum += Mathf.Abs(hl.forwardSlip); n++; }
        if (rearRWheelCollider.GetGroundHit(out var hr)) { sum += Mathf.Abs(hr.forwardSlip); n++; }
        return n > 0 ? sum / n : 0f;
    }

    float GetTotalBrake01()
    {
        // In Reverse, forward pedal (RT/W) acts as brake
        float forwardPedalAsBrake = (gearIndex == 0) ? Mathf.Clamp01(accelerationInput) : 0f;
        return Mathf.Clamp01(brakeInput + forwardPedalAsBrake);
    }

    void EnsureEngineInstance()
    {
        if (!engineInstance.isValid())
        {
            engineInstance = AudioManager.instance.GetOrCreateInstance(
                key: $"engine_{GetInstanceID()}",
                eventRef: FMODEvents.instance.Engine,
                position: transform.position
            );
        }
    }

    void EnsureWindInstance()
    {
        if (!windInstance.isValid())
        {
            windInstance = AudioManager.instance.GetOrCreateInstance(
                key: $"wind_{GetInstanceID()}",
                eventRef: FMODEvents.instance.Wind,
                position: transform.position
            );
        }
    }

    public void SetWindEnabled(bool enabled)
    {
        windEnabled = enabled;

        if (enabled)
        {
            if (FMODEvents.instance == null || AudioManager.instance == null) return;
            EnsureWindInstance();
            if (windInstance.isValid())
            {
                windInstance.set3DAttributes(RuntimeUtils.To3DAttributes(transform.position));
                windInstance.start();
            }
        }
        else
        {
            if (windInstance.isValid())
                windInstance.stop(FMOD.Studio.STOP_MODE.IMMEDIATE);
        }
    }


    /// <summary>
    /// Public toggle you can call from UI or code.
    /// </summary>
    public void SetEngineSoundEnabled(bool enabled)
    {
        engineSoundEnabled = enabled;

        if (enabled)
        {
            if (FMODEvents.instance == null || AudioManager.instance == null) return;
            EnsureEngineInstance();
            if (engineInstance.isValid())
            {
                engineInstance.set3DAttributes(RuntimeUtils.To3DAttributes(transform.position));
                engineInstance.start();
                engineStarted = true;
            }
        }
        else
        {
            if (engineInstance.isValid())
                engineInstance.stop(FMOD.Studio.STOP_MODE.IMMEDIATE);
            engineStarted = false;
        }
    }


    public string GearLabel =>
        (gearIndex == 0) ? "R" :
        (gearIndex == 1) ? "N" :
        (gearIndex - 1).ToString();

    public int EngineRPMRounded => Mathf.RoundToInt(engineRPM);

    void OnGUI()
    {
        int hudWidth = 200;
        int hudHeight = 70;
        int margin = 10;

        Rect hudRect = new Rect(margin, Screen.height - hudHeight - margin, hudWidth, hudHeight);
        GUI.Box(hudRect, "");
        GUI.Label(new Rect(hudRect.x + 5, hudRect.y + 5, hudRect.width, 22), $"Gear: {GearLabel}");
        GUI.Label(new Rect(hudRect.x + 5, hudRect.y + 25, hudRect.width, 22), $"RPM : {EngineRPMRounded}");
        GUI.Label(new Rect(hudRect.x + 5, hudRect.y + 45, hudRect.width, 22), $"Speed: {(rb.velocity.magnitude * 3.6f):0} km/h");
        GUI.Label(new Rect(hudRect.x + 110, hudRect.y + 5, 80, 22), absEnabled ? "ABS: ON" : "ABS: OFF");

    }

    private void OnDestroy()
    {
        if (engineInstance.isValid())
            engineInstance.stop(FMOD.Studio.STOP_MODE.IMMEDIATE);

        if (windInstance.isValid())
            windInstance.stop(FMOD.Studio.STOP_MODE.IMMEDIATE);
    }

    private void DebugControllerButtons()
    {
        // Check buttons 0–19 (most controllers fit in here)
        for (int i = 0; i < 20; i++)
        {
            if (Input.GetKeyDown("joystick 1 button " + i))
            {
                Debug.Log("Joystick1 Button Pressed: " + i);
            }
        }
    }
}

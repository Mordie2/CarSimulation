// =============================================
// File: Scripts/Vehicle/Drive/DrivetrainSystem.cs
// Role: Engine/flywheel, limiter/idle, torque & coast → wheels
// Note: Calls ShiftSystem.Tick() first each physics frame.
// =============================================
using UnityEngine;
using System.Collections.Generic;

namespace Vehicle
{
    public class DrivetrainSystem
    {
        // ===== Debug =====
        [System.Serializable]
        public struct DebugState
        {
            public float time;

            // Inputs / controls
            public float throttle01;
            public float clutch01;
            public bool pedalDown;
            public bool burnoutMode;

            // Gearing / speeds
            public int gearIndex;
            public string gearLabel;
            public float ratio;
            public float ratioAbs;
            public float wheelRPM;
            public float engineRPM;
            public float engOmega;
            public float gearOmegaLP;
            public float gearOmegaRaw;

            // State flags
            public bool isShifting;
            public bool lastShiftWasUpshift;
            public float shiftTimer;
            public float lastShiftTime;
            public bool launchActive;
            public bool instantActive;
            public bool torqueCutActive;
            public bool liftCutActive;
            public bool limiterActive;
            public float limiterT;
            public bool hardCutActive;

            public bool coupledNow;
            public bool coastCoupled;
            public bool flywheelBlend;
            public bool runTwoMass;

            // Torques (engine side → wheels)
            public float T_req;
            public float T_drag;
            public float inertiaNm;              // I * dω/dt
            public float T_toGear_engineSide;    // after caps
            public float axleTorque;             // at axle (+fwd, -coast)
            public float perWheelMotorNm;        // avg RL/RR motor
            public float perWheelBrakeNm;        // avg RL/RR brake
            public float engineBrakeWheelNm;     // closed-throttle EB (per axle)

            public float T_engineBrake_eng;   // injected on engine side (Nm)
            public float T_engineBrake_axle;  // the same torque after ratio*FD*eff (Nm at axle)

            // Settings echo (for sanity)
            public float engineBrakeSetting;
            public float engineInertiaSetting;

            // Kinematic sanity
            public float vehSpeedKmh;
            public float mechSpeedKmh;
            public float kmhMismatch;
        }
        public DebugState DebugInfo { get; private set; }
        public static readonly List<DrivetrainSystem> Instances = new List<DrivetrainSystem>();
        // ===== /Debug =====
        private readonly VehicleContext _ctx;
        private readonly ShiftSystem _shift;

        // Engine state
        private float _engineRPM = 1000f;
        private float _idleNoiseOffset = 0f;
        public float EngineRPM => _engineRPM;

        // Flywheel (2-mass style window)
        private float _engOmega = 0f;       // rad/s
        private float _gearOmegaLP = 0f;    // rad/s (LP of wheel-side)
        private const float TWO_PI = 6.2831853f;
        private const float GEAR_OMEGA_TAU = 0.16f;
        private const float FLYWHEEL_BLEND = 0.01f; // seconds after a shift to keep model active
        private float _TclutchPrev = 0f;
        private const float TCLUTCH_SLEW = 4000f; // Nm/s

        // Clutch/drag tunables (used in flywheel window)
        private const float CLUTCH_CAP_NM = 120000f;
        private const float ENGINE_DRAG_IDLE = 20f;
        private const float ENGINE_DRAG_COEF = 0.10f;
        private const float POST_WINDOW_SCALE = 0.90f;   // soften capacity just after shift
        private const float POS_TORQUE_REARM = 0.04f;    // s after shift end
        private const float POS_TORQUE_THR = 0.25f;      // require real pedal
        private const float SYNC_RPM_RATE = 120000f; // rpm per second when clutch is (nearly) closed
        private const float RIGID_COUPLE_THR = 0.995f;    // one source of truth
        private const float SLIP_LOCK_EPS = 45f;          // rad/s -> snap when nearly synced

        // --- Creep / bite-in at standstill (first gear) ---
        private const float CREEP_MAX_CAP_NM = 800f;   // engine-side torque allowed while slipping
        private const float CREEP_RAMP_S = 0.20f;  // seconds to reach full creep cap
        private const float CREEP_THR_THROTTLE = 0.03f;  // tiny pedal is enough to start bite-in
        private float _creepBlend = 0f;                    // 0..1 ramp for bite-in

        // add near the other tunables
        private const float COUPLED_CAP_NM = 8000f;      // engine-side cap when rigid-coupled
        private const float STOP_SPEED_EPS = 0.20f;      // m/s
        private const float STOP_WHEEL_RPM_EPS = 5f;     // rpm

        // Low-speed coast behaviour
        private const float IDLE_HANG_SPEED = 5f;        // m/s (~4.3 km/h): below this, don't hard-couple on lift
        private const float COAST_EB_RETURN_SPEED = 2.5f;  // m/s (~9 km/h): above this, full engine-brake returns
                                                           // Idle-bite / creep even with zero throttle (1st gear, near standstill)
        private const float IDLE_ASSIST_FRAC = 0.18f;    // % of idle torque to feed
        private const float IDLE_ASSIST_MAX_NM = 220f;   // engine-side clamp
        private const float IDLE_ASSIST_SPEED_MAX = 1.4f;// m/s (~5 km/h) fade out by here
        private const float IDLE_ASSIST_RAMP_S = 0.30f;  // ramp time
        private float _idleAssistBlend = 0f;             // 0..1
        private const float LAUNCH_SLIP_SPEED = 1.0f; // m/s (~18 km/h) - slip until roughly here

        // Limiter shaping
        private const float LIMITER_BAND_RPM = 350f;
        private const float LIMITER_NEG_TORQUE_NM = 140f;
        private const float LIMITER_EXTRA_DRAG_MULT = 3.0f;
        private const float LIMITER_DECEL_MULT = 12f;

        // Neutral/free-rev feel
        private const float NEUTRAL_UP_MULT = 1.15f;
        private const float NEUTRAL_DOWN_MULT = 1.5f;
        private const float NEUTRAL_PULLDOWN_RPM_PER_S = 3000f;
        private const float ANTI_HANG_DELTA_RPM = 100f;      // how far above target before we intervene
        private const float LIFT_DECEL_RPM_PER_S = 60000f;    // extra fall rate on big surplus
        private const float GEAR_LOCK_MARGIN_RPM = 100f;          // small headroom to avoid jitter
        private const float POWER_TAPER_START_RPM = 7000f; // where torque starts falling fast
        private const float POWER_TAPER_END_OFFSET = 100f; // taper ends ~this below limiter

        // --- Hard cut limiter ---
        private const float HARD_CUT_RPM = 7400f;
        private const float HARD_CUT_NEG_TORQUE_NM = 500f; // extra negative torque to pull down fast
        private bool _hardCutActive = false;

        // Hard cut behaviour
        private const float HARD_CUT_PULSE_S = 0.10f;          // 100 ms cut pulse
        private const float HARD_CUT_RPM_CAP_MARGIN = 10f;     // cap to a hair below setpoint while cutting
        private float _hardCutTimer = 0f;

        // --- Input hygiene / reverse gating ---
        private const float INPUT_DEADZONE = 0.05f; // ignore tiny noise
        private const float SPAWN_INPUT_GRACE_S = 0.35f; // ignore inputs briefly after spawn

        // Reverse requires a deliberate LT press (prevents spawn flicker)
        private const float REV_ENGAGE_THR = 0.20f; // must exceed to consider "pressing LT"
        private const float REV_RELEASE_THR = 0.10f; // hysteresis to drop the gate
        private const float REV_ENGAGE_HOLD_S = 0.06f; // hold LT this long to arm reverse input

        private const float EB_FIRST_SCALE = 0.60f; // 1st gear EB strength
        private const float EB_AXLE_MAX_NM = 1800f; // per-axle cap for EB (tune)

        private float _spawnTime;
        private float _revGateTimer = 0f;
        private bool _revGateArmed = false;

        private const bool START_IN_NEUTRAL = true; 
        private const float START_NEUTRAL_HOLD_S = 0.60f;

        // --- Engine Start/Stop (toggle on button A) ---
        [SerializeField] private float starterDuration = 0.95f;   // seconds the starter spins
        [SerializeField] private float starterTargetRPM = 500f;   
        [SerializeField] private float startStopCooldown = 0.35f; // debounce
        public event System.Action OnEngineStarted;

        private bool _engineRunning = false;     // current engine state
        private bool _starterActive = false;    // true while starter is spinning up
        private float _starterT = 0f;           // 0..1 progress
        private float _startStopCD = 0f;        // cooldown timer
        private bool _queuedStartStop = false;  // latched by input edge

        public void QueueStartStopToggle() => _queuedStartStop = true; 
        private const float REVERSE_GUARD_SPEED = 2.0f;
        public bool UsingReverseThrottleThisFrame { get; private set; }

        // Post-shift coast assist
        private const float POST_SHIFT_COAST = 0.25f;

        // Expose shift info as pass-through for old subscribers
        public event System.Action<int, int> OnGearChanged
        {
            add { _shift.OnGearChanged += value; }
            remove { _shift.OnGearChanged -= value; }
        }
        public string GearLabel => _shift.GearLabel;
        public bool IsInReverse => _shift.IsInReverse;
        public int GearIndex => _shift.GearIndex;
        public bool LaunchControlEnabled { get => _shift.LaunchControlEnabled; set => _shift.LaunchControlEnabled = value; }
        public bool LaunchInstantTorque { get => _shift.LaunchInstantTorque; set => _shift.LaunchInstantTorque = value; }
        public void ToggleLaunchControl() => _shift.ToggleLaunchControl();
        public void ToggleLaunchModeInstant() => _shift.ToggleLaunchModeInstant();
        public void RequestShiftUp() => _shift.RequestShiftUp();
        public void RequestShiftDown() => _shift.RequestShiftDown();

        public DrivetrainSystem(VehicleContext ctx)
        {
            _ctx = ctx;
            _shift = new ShiftSystem(ctx);

            // Seed engine RPM / omega
            if (_engineRunning)
            {
                _engineRPM = Mathf.Max(_engineRPM, _ctx.settings.idleRPM);
                _engOmega = _engineRPM * TWO_PI / 60f;
            }
            else
            {
                _engineRPM = 0f;
                _engOmega = 0f;
            }

            float r = _ctx.RL.radius;
            float wheelRPS = _ctx.rb.velocity.magnitude / (2f * Mathf.PI * r);
            float wheelRPM = wheelRPS * 60f;
            float ratioAbs0 = Mathf.Abs(_shift.GetCurrentRatio() * _ctx.settings.finalDrive);
            _gearOmegaLP = Mathf.Max(wheelRPM * TWO_PI / 60f * ratioAbs0, 0f);
            _idleNoiseOffset = Random.value * 100f;

            Instances.Add(this);

            _spawnTime = Time.time;
        }

        private float GetWheelRPMFromGround()
        {
            float r = _ctx.RL.radius;
            float wheelRPS = _ctx.rb.velocity.magnitude / (2f * Mathf.PI * r);
            return wheelRPS * 60f;
        }

        private float LimiterBlend(float rpm)
        {
            float soft = _ctx.settings.revLimiterRPM - LIMITER_BAND_RPM;
            return Mathf.Clamp01((rpm - soft) / Mathf.Max(1f, LIMITER_BAND_RPM));
        }

        private float MapThrottle01(IInputProvider input)
        {
            // Grace window after spawn: ignore all driving inputs
            if ((Time.time - _spawnTime) < SPAWN_INPUT_GRACE_S)
            {
                UsingReverseThrottleThisFrame = false;
                return 0f;
            }

            bool manualMode = !_ctx.settings.automatic;
            bool inReverse = _shift.IsInReverse;

            float rt = Mathf.Clamp01(input.Throttle);
            float lt = Mathf.Clamp01(input.Brake);

            // Deadzones
            rt = (rt <= INPUT_DEADZONE) ? 0f : rt;
            lt = (lt <= INPUT_DEADZONE) ? 0f : lt;

            UsingReverseThrottleThisFrame = false;

            // --- Manual: realistic (RT always throttle) ---
            if (manualMode)
            {
                _revGateArmed = false;
                _revGateTimer = 0f;
                return rt;
            }

            // --- Auto & not in reverse: arcade forward (RT throttle) ---
            if (!inReverse)
            {
                _revGateArmed = false;
                _revGateTimer = 0f;
                return rt;
            }

            // --- Auto & Reverse: arcade reverse (LT becomes throttle) ---
            if (lt > REV_ENGAGE_THR)
            {
                _revGateTimer += Time.fixedDeltaTime;
                if (_revGateTimer >= REV_ENGAGE_HOLD_S) _revGateArmed = true;
            }
            else if (lt < REV_RELEASE_THR)
            {
                _revGateArmed = false;
                _revGateTimer = 0f;
            }

            UsingReverseThrottleThisFrame = _revGateArmed && (lt > 0f);
            return UsingReverseThrottleThisFrame ? lt : 0f; // RT is ignored while in Reverse
        }

        // Main physics tick (call from FixedUpdate)
        public void Tick(IInputProvider input, float speedMs, bool burnoutMode)
        {
            // clear per-frame torques
            _ctx.RL.motorTorque = 0f;
            _ctx.RR.motorTorque = 0f;

            float throttle01 = MapThrottle01(input);
            bool onThrottle = throttle01 > 0.05f;

            // Consume engine toggle request (from input provider)
            if (input is IInputProvider ip && ip.RequestEngineToggle)
                _queuedStartStop = true;


            // --- Hard-cut pulse latch ---
            bool overRev = _engineRPM >= HARD_CUT_RPM;
            bool allowPulse = onThrottle || (_shift.GearIndex == 2); // 1st gear downhill case
            if (allowPulse && overRev && _hardCutTimer <= 0f) _hardCutTimer = HARD_CUT_PULSE_S;
            _hardCutTimer = Mathf.Max(0f, _hardCutTimer - Time.fixedDeltaTime);
            _hardCutActive = _hardCutTimer > 0f;

            // --- Shifter/LC/Cut update ---
            _shift.Tick(input, speedMs, _engineRPM, throttle01, burnoutMode);

            // Quick locals from shifter
            bool isShifting = _shift.IsShifting;
            float clutch = _shift.Clutch;
            int gearIndex = _shift.GearIndex;
            bool inFirst = (gearIndex == 2);

            float ratio = _shift.GetCurrentRatio();
            float ratioAbs = Mathf.Abs(ratio * _ctx.settings.finalDrive);
            float wheelRPM = GetWheelRPMFromGround();

            // Signed vehicle speed along the car's forward axis (+forward, -backward)
            float longSpeedSigned = Vector3.Dot(_ctx.rb.velocity, _ctx.rb.transform.forward);

            // Direction of selected gear (ratio>0 = forward, ratio<0 = reverse)
            int gearDir = (ratio >= 0f) ? 1 : -1;
            // Direction of current motion
            int motionDir = (longSpeedSigned >= 0f) ? 1 : -1;

            // True when car is rolling opposite to selected gear at meaningful speed
            bool reverseMotion = (gearDir * motionDir) < 0 && Mathf.Abs(longSpeedSigned) > REVERSE_GUARD_SPEED;

            // Dynamic freewheel speed ≈ a bit above "mechanical speed at idle" in 1st
            float wheelCirc = 2f * Mathf.PI * _ctx.RL.radius;
            float idleMechSpeedMs = (_ctx.settings.idleRPM / 60f) / Mathf.Max(ratioAbs, 1e-4f) * wheelCirc;
            // clamp between walking speed and jog
            float FREEWHEEL_SPEED = Mathf.Clamp(idleMechSpeedMs * 1.25f, 1.0f, 5.0f);

            bool manualMode = !_ctx.settings.automatic;

            // Windows that kill positive torque (instant mode can override)
            bool pedalDown = throttle01 > (INPUT_DEADZONE + 0.01f);
            bool cutWindowActive = _shift.IsTorqueCutActive;
            bool liftCutActive = _shift.IsLiftCutActive;
            bool instantActive = _shift.IsInstantTorqueActive(speedMs, inFirst, throttle01);
            if (instantActive) { cutWindowActive = false; liftCutActive = false; }
            bool launchActive = _shift.IsLaunchActive;

            bool launchingSlip = inFirst && pedalDown && (speedMs < LAUNCH_SLIP_SPEED) && (clutch < RIGID_COUPLE_THR);

            // Limiter shaping
            float limiterT = LimiterBlend(_engineRPM);
            bool limiterActive = limiterT > 0f;

            // ---- Debug seed ----
            var dbg = new DebugState
            {
                time = Time.time,
                throttle01 = throttle01,
                clutch01 = clutch,
                pedalDown = pedalDown,
                burnoutMode = burnoutMode,

                gearIndex = gearIndex,
                gearLabel = _shift.GearLabel,
                ratio = ratio,
                ratioAbs = ratioAbs,
                wheelRPM = wheelRPM,

                engineRPM = _engineRPM,
                engOmega = _engOmega,
                gearOmegaLP = _gearOmegaLP,

                isShifting = isShifting,
                lastShiftWasUpshift = _shift.LastShiftWasUpshift,
                shiftTimer = _shift.ShiftTimer,
                lastShiftTime = _shift.LastShiftTime,
                launchActive = _shift.IsLaunchActive,
                instantActive = _shift.IsInstantTorqueActive(speedMs, inFirst, throttle01),
                torqueCutActive = _shift.IsTorqueCutActive,
                liftCutActive = _shift.IsLiftCutActive,

                limiterActive = limiterActive,
                limiterT = limiterT,
                hardCutActive = _hardCutActive,

                engineBrakeSetting = _ctx.settings.engineBrakeTorque,
                engineInertiaSetting = _ctx.settings.engineInertia,

                vehSpeedKmh = _ctx.rb.velocity.magnitude * 3.6f
            };
            // ===== Engine Start/Stop state machine =====
            _startStopCD = Mathf.Max(0f, _startStopCD - Time.fixedDeltaTime);

            // Edge-triggered toggle
            if (_queuedStartStop && _startStopCD <= 0f)
            {
                _queuedStartStop = false;
                _startStopCD = startStopCooldown;

                if (_starterActive)
                {
                    _starterActive = false;
                    _engineRunning = false;
                }
                else if (_engineRunning)
                {
                    _engineRunning = false;
                }
                else
                {
                    _ctx.audio?.NotifyEngineStart();
                    _starterActive = true;          // begin starter spin
                    _starterT = 0f;
                    _engineRPM = 0f;
                }
            }

            // Starter spin (no drive torque while starting)
            if (_starterActive)
            {
                _starterT = Mathf.Min(1f, _starterT + (Time.fixedDeltaTime / Mathf.Max(0.01f, starterDuration)));

                float target = Mathf.Max(starterTargetRPM, _ctx.settings.idleRPM * 0.80f);
                _engineRPM = Mathf.Lerp(_engineRPM, target, 0.25f + 0.75f * _starterT);

                _ctx.RL.motorTorque = 0f;
                _ctx.RR.motorTorque = 0f;

                if (_starterT >= 1f)
                {
                    _starterActive = false;
                    _engineRunning = true;
                    _engineRPM = Mathf.Max(_engineRPM, _ctx.settings.idleRPM * 1.5f);


                }

                dbg.engineRPM = _engineRPM;
                DebugInfo = dbg;
                return; // short-circuit this physics frame
            }

            // Engine OFF → hold RPM at 0 and block all drive torque
            if (!_engineRunning)
            {
                _engineRPM = Mathf.MoveTowards(_engineRPM, 0f, 6000f * Time.fixedDeltaTime);
                _ctx.RL.motorTorque = 0f;
                _ctx.RR.motorTorque = 0f;

                dbg.engineRPM = _engineRPM;
                DebugInfo = dbg;
                return;
            }

            // ------------------------
            // Burnout special case
            // ------------------------
            if (burnoutMode)
            {
                float targetRPM = Mathf.Lerp(_ctx.settings.idleRPM, _ctx.settings.maxRPM, throttle01);
                float follow = Time.fixedDeltaTime / Mathf.Max(0.0001f, _ctx.settings.engineInertia);
                _engineRPM = Mathf.Clamp(
                    Mathf.Lerp(_engineRPM, targetRPM, Mathf.Clamp01(follow)),
                    _ctx.settings.idleRPM,
                    _ctx.settings.revLimiterRPM + 200f
                );

                float engineTorque = _ctx.settings.torqueCurve.Evaluate(_engineRPM) * throttle01 * _ctx.settings.torqueMultiplier;

                // Lock fronts for show
                _ctx.FL.motorTorque = 0f; _ctx.FR.motorTorque = 0f;
                _ctx.FL.brakeTorque = 1e9f; _ctx.FR.brakeTorque = 1e9f;

                float perRear = (engineTorque * ratio * _ctx.settings.finalDrive * _ctx.settings.drivetrainEfficiency) * 0.5f;
                _ctx.RL.motorTorque = perRear;
                _ctx.RR.motorTorque = perRear;
                _ctx.RL.brakeTorque = 0f; _ctx.RR.brakeTorque = 0f;
                return;
            }

            // --- Start-in-Neutral masking ---
            if (START_IN_NEUTRAL)
            {
                float sinceSpawn = Time.time - _spawnTime;
                if (sinceSpawn < START_NEUTRAL_HOLD_S)
                {
                    // Nudge visible gear toward Neutral
                    if (_shift.GearIndex < 1) _shift.RequestShiftUp();
                    else if (_shift.GearIndex > 1) _shift.RequestShiftDown();

                    // Keep RPM near idle with neutral-like inertia
                    float neutralInertiaUp = Mathf.Max(0.0001f, _ctx.settings.engineInertia * NEUTRAL_UP_MULT);
                    float neutralAlpha = Mathf.Clamp01(Time.fixedDeltaTime / neutralInertiaUp);
                    float neutralTargetIdle = _ctx.settings.idleRPM;
                    _engineRPM = Mathf.Clamp(
                        Mathf.Lerp(_engineRPM, neutralTargetIdle, neutralAlpha),
                        _ctx.settings.idleRPM,
                        _ctx.settings.revLimiterRPM + 200f
                    );

                    // Absolutely no drive torque while we hold Neutral
                    _ctx.RL.motorTorque = 0f;
                    _ctx.RR.motorTorque = 0f;

                    // Debug echo (mask as Neutral for this frame)
                    dbg.gearIndex = 1;
                    dbg.gearLabel = "N (spawn)";
                    dbg.ratio = 0f;
                    dbg.ratioAbs = 0f;
                    dbg.engineRPM = _engineRPM;
                    DebugInfo = dbg;

                    return; // bail out early this frame while in forced Neutral
                }
            }

            // Neutral-like detection
            bool inGear = !Mathf.Approximately(ratio, 0f);
            bool lowSpeedCoast = (!pedalDown && speedMs < FREEWHEEL_SPEED);
            bool physicallyCoupled =
                inGear &&
                (clutch >= RIGID_COUPLE_THR) &&
                !isShifting &&
                (wheelRPM > STOP_WHEEL_RPM_EPS) &&
                !lowSpeedCoast &&
                !reverseMotion; 
            bool flywheelBlend = ((Time.time - _shift.LastShiftTime) < FLYWHEEL_BLEND) && !lowSpeedCoast;
            bool runTwoMass = physicallyCoupled || flywheelBlend;
            bool wantFreewheel =
                inFirst && (ratio > 0f) && !pedalDown && !input.Handbrake &&
                (speedMs < FREEWHEEL_SPEED) && (wheelRPM > STOP_WHEEL_RPM_EPS);

            if (wantFreewheel)
            {
                // Do NOT treat as coupled, and skip the two-mass path entirely
                physicallyCoupled = false;
                flywheelBlend = false;
                runTwoMass = false;
            }
            bool neutralLike = !inGear || clutch < 0.2f || gearIndex == 1;

            // ------------------------
            // Active shift: rev-match, no drive torque
            // ------------------------
            if (isShifting)
            {
                float newRatioAbs = Mathf.Abs(ratio * _ctx.settings.finalDrive);
                float syncRPM = Mathf.Clamp(
                    Mathf.Max(wheelRPM * newRatioAbs, _ctx.settings.idleRPM),
                    _ctx.settings.idleRPM,
                    _ctx.settings.revLimiterRPM + 200f
                );

                float t = Mathf.Clamp01(_shift.ShiftTimer / Mathf.Max(0.0001f, _ctx.settings.shiftTime));
                float snap = Mathf.Lerp(0.25f, 1.0f, t);
                float followShift = Time.fixedDeltaTime / Mathf.Max(0.0001f, _ctx.settings.engineInertia * snap);
                _engineRPM = Mathf.Lerp(_engineRPM, syncRPM, Mathf.Clamp01(followShift));

                _ctx.RL.motorTorque = 0f;
                _ctx.RR.motorTorque = 0f;

                // Gentle coast during upshift if lifted
                //return;
            }

            // ------------------------
            // Target RPM computation
            // ------------------------
            float targetRPMNormal;
            if (clutch > 0.99f && inGear)
            {
                float mechRPM = Mathf.Max(wheelRPM * ratioAbs, _ctx.settings.idleRPM);
                if ((launchActive && inFirst) || instantActive)
                    mechRPM = Mathf.Max(mechRPM, _ctx.settings.minLaunchRPM);
                targetRPMNormal = mechRPM;
            }
            else
            {
                targetRPMNormal = Mathf.Lerp(_ctx.settings.idleRPM, _ctx.settings.maxRPM, throttle01);
            }

            // During a slip launch, don't aim above the launch RPM.
            // This keeps the engine controller from chasing max RPM while the clutch is managing slip.
            if (launchingSlip && !isShifting)
            {
                targetRPMNormal = Mathf.Min(targetRPMNormal, _ctx.settings.minLaunchRPM);
            }


            // Only cap the control target during hard cut when NOT mechanically coupled

            bool rearmedPosTorque =
                physicallyCoupled &&
                (Time.time - _shift.LastShiftTime) >= POS_TORQUE_REARM &&   
                throttle01 >= POS_TORQUE_THR &&                            
                !input.Handbrake;

            if (rearmedPosTorque)
            {
                cutWindowActive = false;
                liftCutActive = false;
            }

            if (_hardCutActive && !physicallyCoupled)
                targetRPMNormal = Mathf.Min(targetRPMNormal, HARD_CUT_RPM - HARD_CUT_RPM_CAP_MARGIN);

            // ------------------------
            // Two-mass (engine↔gear) coupling when actually coupled,
            // OR shortly after shifts (keep your smoothing feel)
            // ------------------------

            if (runTwoMass)
            {
                float dt = Time.fixedDeltaTime;
                float gearOmegaRaw = Mathf.Max(wheelRPM * TWO_PI / 60f * ratioAbs, 0f);
                float a = 1f - Mathf.Exp(-dt / GEAR_OMEGA_TAU);
                _gearOmegaLP = Mathf.Lerp(_gearOmegaLP, gearOmegaRaw, a);


                dbg.flywheelBlend = (Time.time - _shift.LastShiftTime) < FLYWHEEL_BLEND;
                dbg.runTwoMass = true;
                dbg.coupledNow = physicallyCoupled;
                dbg.gearOmegaRaw = gearOmegaRaw;


                // --- First-gear creep / bite-in state ---
                bool almostStopped = (_ctx.rb.velocity.magnitude < STOP_SPEED_EPS) || (wheelRPM < STOP_WHEEL_RPM_EPS);
                bool creepWanted = inFirst && inGear && !isShifting && almostStopped && (throttle01 > CREEP_THR_THROTTLE) && (ratio > 0f);

                // ramp 0→1 while conditions hold, 1→0 when they stop
                _creepBlend = Mathf.MoveTowards(_creepBlend, creepWanted ? 1f : 0f,
                                                Time.fixedDeltaTime / Mathf.Max(0.0001f, CREEP_RAMP_S));
                float creepCap = CREEP_MAX_CAP_NM * _creepBlend;

                // capture before any change so omegaDot is always well-defined
                float prevOmega = _engOmega;

                // Use raw when truly coupled, LP otherwise
                float targetOmega = physicallyCoupled ? _gearOmegaLP : _engOmega;

                float T_drag = ENGINE_DRAG_IDLE + ENGINE_DRAG_COEF * Mathf.Max(_engOmega, 0f);
                if (physicallyCoupled)
                {
                    float maxDeltaOmega = (SYNC_RPM_RATE * TWO_PI / 60f) * dt;
                    _engOmega = Mathf.MoveTowards(prevOmega, targetOmega, maxDeltaOmega);
                    if (Mathf.Abs(_engOmega - gearOmegaRaw) <= SLIP_LOCK_EPS) _engOmega = gearOmegaRaw;
                }

                // Update RPM
                _engineRPM = Mathf.Clamp(_engOmega * 60f / TWO_PI, _ctx.settings.idleRPM, _ctx.settings.revLimiterRPM + 200f);

                // --- Engine request & drag ---
                float T_req = 0f;

                // --- Zero-throttle idle assist in 1st to overcome static friction ---
                bool permitIdleCreep =
                    inFirst && inGear && !isShifting && ratio > 0f && !input.Handbrake &&
                    (speedMs < IDLE_ASSIST_SPEED_MAX) && (throttle01 < 0.02f) && (clutch >= 0.95f);

                // ramp 0→1 while allowed; 1→0 otherwise
                _idleAssistBlend = Mathf.MoveTowards(
                    _idleAssistBlend,
                    permitIdleCreep ? 1f : 0f,
                    Time.fixedDeltaTime / Mathf.Max(0.0001f, IDLE_ASSIST_RAMP_S)
                );

                // base on idle torque so engine sound/curve stays coherent
                float idleBaseNm = _ctx.settings.torqueCurve.Evaluate(Mathf.Max(_ctx.settings.idleRPM, _engineRPM))
                                   * _ctx.settings.torqueMultiplier;

                // fade assist out as speed rises (no long cruising on idle)
                float idleAssistSpeedScale = Mathf.Clamp01(Mathf.InverseLerp(IDLE_ASSIST_SPEED_MAX, 0f, speedMs));

                float idleAssistNm = Mathf.Min(IDLE_ASSIST_MAX_NM, idleBaseNm * IDLE_ASSIST_FRAC)
                                     * _idleAssistBlend * idleAssistSpeedScale;

                // add to engine request (positive torque at 0 throttle)
                T_req += idleAssistNm;

                float I = Mathf.Max(0.0001f, _ctx.settings.engineInertia);
                prevOmega = _engOmega;

                if (pedalDown && !cutWindowActive && !liftCutActive)
                {
                    T_req = _ctx.settings.torqueCurve.Evaluate(_engineRPM) * throttle01 * _ctx.settings.torqueMultiplier;
                    T_req *= HighRpmFalloff(_engineRPM);
                    T_req *= _shift.PostShiftTorqueScale;
                    if (gearIndex == 0) T_req *= _ctx.settings.reverseTorqueMultiplier;
                    if (limiterActive) T_req *= (1f - limiterT);
                    if (_engineRPM >= _ctx.settings.revLimiterRPM && throttle01 > 0.1f) T_req = 0f;
                    if (limiterActive) T_req -= LIMITER_NEG_TORQUE_NM * limiterT * (manualMode ? 1f : 0.5f);
                }

                float T_engineBrake_eng = 0f;
                if (physicallyCoupled && !pedalDown && !input.Handbrake)
                {
                    // Magnitude from RPM & lift
                    float rpmFactor = Mathf.InverseLerp(
                        _ctx.settings.idleRPM, _ctx.settings.maxRPM,
                        Mathf.Clamp(_engineRPM, _ctx.settings.idleRPM, _ctx.settings.maxRPM));
                    float lift = 1f - throttle01;
                    float ebMag = _ctx.settings.engineBrakeTorque * rpmFactor * lift;

                    // Speed fade (kill EB near creep, full EB above ~9 km/h)
                    float ebSpeedScale = Mathf.Clamp01(Mathf.InverseLerp(COAST_EB_RETURN_SPEED, IDLE_HANG_SPEED, speedMs));

                    // Gear scaling (less EB in 1st)
                    float ratioAbsCurr = Mathf.Abs(ratio * _ctx.settings.finalDrive);
                    float ratioAbsG1 = Mathf.Abs(_ctx.settings.gearRatios[Mathf.Clamp(2, 0, _ctx.settings.gearRatios.Length - 1)] * _ctx.settings.finalDrive);
                    float ratioAbsG2 = Mathf.Abs(_ctx.settings.gearRatios[Mathf.Clamp(3, 0, _ctx.settings.gearRatios.Length - 1)] * _ctx.settings.finalDrive);
                    float g01 = Mathf.Clamp01(Mathf.InverseLerp(ratioAbsG1, ratioAbsG2, ratioAbsCurr));
                    float ebGearScale = Mathf.Lerp(EB_FIRST_SCALE, 1f, g01);

                    ebMag *= ebSpeedScale * ebGearScale;

                    // SIGN: engine braking must oppose current motion
                    float ebEngineSign = -Mathf.Sign(longSpeedSigned) * Mathf.Sign(ratio == 0f ? 1f : ratio);

                    // Soften during post-shift cuts
                    if (_shift.IsTorqueCutActive || _shift.IsLiftCutActive) ebMag *= POST_SHIFT_COAST;

                    // Final engine-side EB torque
                    T_engineBrake_eng = ebMag * ebEngineSign;

                    // Optional axle cap
                    float ebAxleCandidate = T_engineBrake_eng * ratio * _ctx.settings.finalDrive * _ctx.settings.drivetrainEfficiency;
                    float ebAxleSignedCap = (ebAxleCandidate < 0f) ? -EB_AXLE_MAX_NM : EB_AXLE_MAX_NM;
                    if (Mathf.Abs(ebAxleCandidate) > Mathf.Abs(ebAxleSignedCap) && ratioAbsCurr > 1e-4f)
                    {
                        float scale = ebAxleSignedCap / Mathf.Max(1e-3f, ebAxleCandidate);
                        T_engineBrake_eng *= scale;
                    }

                    // Apply & log
                    T_req += T_engineBrake_eng;
                    dbg.T_engineBrake_eng = T_engineBrake_eng;
                    dbg.T_engineBrake_axle = T_engineBrake_eng * ratio * _ctx.settings.finalDrive * _ctx.settings.drivetrainEfficiency;
                    dbg.engineBrakeWheelNm = dbg.T_engineBrake_axle;
                }

                if (_hardCutActive) { T_req = 0f; T_drag += HARD_CUT_NEG_TORQUE_NM; }
                if (limiterActive) { T_drag *= (1f + LIMITER_EXTRA_DRAG_MULT * limiterT); }

                // Inertia torque & gearbox reaction

                float omegaDot = (_engOmega - prevOmega) / Mathf.Max(0.000001f, dt);

                // inertia contribution to clutch (decel => Tinertia > 0)
                float Tinertia = -I * omegaDot;

                // raw clutch torque before limits
                float T_toGear_engineSide = T_req - T_drag + Tinertia;

                float cap = (clutch >= RIGID_COUPLE_THR
                            ? COUPLED_CAP_NM
                            : CLUTCH_CAP_NM * Mathf.Clamp01(clutch) * (flywheelBlend ? POST_WINDOW_SCALE : 1f));

                // If we are creeping (clutch not yet hard-coupled), allow a small,
                // time-ramped engine-side capacity so wheels can start turning.
                if (!physicallyCoupled && creepCap > cap) cap = creepCap;

                T_toGear_engineSide = Mathf.Clamp(T_toGear_engineSide, -cap, cap);

                if (reverseMotion && !pedalDown)
                {
                    float axleCandidate = T_toGear_engineSide * ratio * _ctx.settings.finalDrive * _ctx.settings.drivetrainEfficiency;
                    if (Mathf.Sign(axleCandidate) == motionDir) 
                        T_toGear_engineSide = 0f;              
                }

                // soften sign-chatter near 0 when lifted
                bool blockPosOnLift = physicallyCoupled && !pedalDown && !input.Handbrake && !permitIdleCreep;
                float targetClutchNm = T_toGear_engineSide;

                if (blockPosOnLift && targetClutchNm > 0f)
                {
                    const float POS_HYS = 25f; // small hysteresis band
                    targetClutchNm = Mathf.Max(0f, targetClutchNm - POS_HYS);
                }

                // slew so the sign can't flip abruptly between frames
                float maxStep = TCLUTCH_SLEW * dt; // e.g. 10k Nm/s already defined
                T_toGear_engineSide = Mathf.MoveTowards(_TclutchPrev, targetClutchNm, maxStep);
                _TclutchPrev = T_toGear_engineSide;

                // Axle reaction
                // Gate torque transmission to the axle – neutral must not transmit
                // No reverse coast at (near) standstill: hold the car instead of integrating to -∞
                if (almostStopped && inGear && !pedalDown)
                {
                    // If this torque would drive the wheels in reverse in a forward gear, kill it.
                    // (ratio>0 for forward gears; adjust sign rule if your ratios differ)
                    if (ratio > 0f && T_toGear_engineSide < 0f) T_toGear_engineSide = 0f;
                    if (ratio < 0f && T_toGear_engineSide > 0f) T_toGear_engineSide = 0f; // safety for reverse gear
                }
                bool allowTransmit = inGear && (ratioAbs > 1e-4f) && (clutch >= 0.75f);

                // During creep: allow *positive* (forward) torque to flow even if clutch < 0.95
                if (!allowTransmit && _idleAssistBlend > 0.001f && T_toGear_engineSide > 0f && ratio > 0f)
                    allowTransmit = true;

                // Kill reverse coast at near-standstill (prevents integrating to -∞)
                if (almostStopped && inGear && !pedalDown)
                {
                    if (ratio > 0f && T_toGear_engineSide < 0f) T_toGear_engineSide = 0f; // forward gear, no negative
                    if (ratio < 0f && T_toGear_engineSide > 0f) T_toGear_engineSide = 0f; // reverse gear, no positive
                }
                float axleTorque = 0f;
                if (allowTransmit)
                {
                    axleTorque = T_toGear_engineSide * ratio * _ctx.settings.finalDrive * _ctx.settings.drivetrainEfficiency;
                    float perWheel = axleTorque * 0.5f;
                    _ctx.RL.motorTorque += perWheel;
                    _ctx.RR.motorTorque += perWheel;
                }

                // ---- debug ----
                dbg.axleTorque = axleTorque;
                dbg.perWheelMotorNm = 0.5f * (_ctx.RL.motorTorque + _ctx.RR.motorTorque);
                dbg.T_req = T_req;
                dbg.T_drag = T_drag;
                dbg.inertiaNm = Tinertia;
                dbg.T_toGear_engineSide = T_toGear_engineSide;
                dbg.axleTorque = axleTorque;
                dbg.perWheelMotorNm = 0.5f * (_ctx.RL.motorTorque + _ctx.RR.motorTorque);
                dbg.perWheelBrakeNm = 0.5f * (_ctx.RL.brakeTorque + _ctx.RR.brakeTorque);

                /*
                // Kinematic sanity check
                if (physicallyCoupled && ratioAbs > 0.0001f)
                {
                    float wheelRPS_mech = (_engineRPM / 60f) / ratioAbs;
                    float speedMs_mech = wheelRPS_mech * (2f * Mathf.PI * _ctx.RL.radius);
                    float kmh_mech = speedMs_mech * 3.6f;
                    float kmh_body = _ctx.rb.velocity.magnitude * 3.6f;
                    if (!float.IsInfinity(kmh_mech) && Mathf.Abs(kmh_mech - kmh_body) > 1.0f)
                    {
                        Debug.LogWarning($"[Drive] Kinematic mismatch while coupled: engine-speed {kmh_mech:F1} km/h vs vehicle {kmh_body:F1} km/h (gear {gearIndex})");
                    }
                }
                */

                if (physicallyCoupled && ratioAbs > 0.0001f)
                {
                    float wheelRPS_mech = (_engineRPM / 60f) / ratioAbs;
                    float speedMs_mech = wheelRPS_mech * (2f * Mathf.PI * _ctx.RL.radius);
                    dbg.mechSpeedKmh = speedMs_mech * 3.6f;
                    dbg.kmhMismatch = Mathf.Abs(dbg.mechSpeedKmh - dbg.vehSpeedKmh);
                }
                DebugInfo = dbg;
                return;

            }

            // ------------------------
            // Non-coupled path (neutral, clutch open, etc.)
            // ------------------------
            const float DOWN_FACTOR = 0.01f;

            float inertiaUp = Mathf.Max(0.0001f, _ctx.settings.engineInertia);
            float inertiaDown = Mathf.Max(0.0001f, _ctx.settings.engineInertia * DOWN_FACTOR);

            if (neutralLike)
            {
                inertiaUp = Mathf.Max(0.0001f, _ctx.settings.engineInertia * NEUTRAL_UP_MULT);
                inertiaDown = Mathf.Max(0.0001f, _ctx.settings.engineInertia * NEUTRAL_DOWN_MULT);
            }

            float inertia = _hardCutActive ? inertiaDown : ((targetRPMNormal < _engineRPM) ? inertiaDown : inertiaUp);

            if (!neutralLike && (targetRPMNormal < _engineRPM) && limiterT > 0f)
                inertia = Mathf.Max(0.0001f, inertia / (1f + LIMITER_DECEL_MULT * limiterT));

            float alpha = Mathf.Clamp01(Time.fixedDeltaTime / inertia);
            _engineRPM = Mathf.Clamp(
                Mathf.Lerp(_engineRPM, targetRPMNormal, alpha),
                _ctx.settings.idleRPM,
                _ctx.settings.revLimiterRPM + 200f
            );

            // Band-lock toward gear sync even here if clutch is nearly closed (insurance)
            if (inGear && clutch >= 0.95f && !isShifting && !wantFreewheel)
            {
                float mechRPMNow = Mathf.Max(wheelRPM * ratioAbs, _ctx.settings.idleRPM);
                float band = GEAR_LOCK_MARGIN_RPM;
                float syncRate = 60000f; // rpm/s
                if (_engineRPM > mechRPMNow + band)
                    _engineRPM = Mathf.MoveTowards(_engineRPM, mechRPMNow + band, syncRate * Time.fixedDeltaTime);
                else if (_engineRPM < mechRPMNow - band)
                    _engineRPM = Mathf.MoveTowards(_engineRPM, mechRPMNow - band, syncRate * Time.fixedDeltaTime);
                _engOmega = Mathf.Max(0f, _engineRPM * TWO_PI / 60f);
            }

            // Anti-hang extra fall on lift
            if (!neutralLike)
            {
                float rpmDelta = _engineRPM - targetRPMNormal;
                if (!pedalDown && rpmDelta > ANTI_HANG_DELTA_RPM)
                {
                    float k = Mathf.InverseLerp(ANTI_HANG_DELTA_RPM, ANTI_HANG_DELTA_RPM + 1500f, rpmDelta);
                    float extraFall = LIFT_DECEL_RPM_PER_S * (0.5f + 1.5f * k);
                    _engineRPM = Mathf.MoveTowards(_engineRPM, targetRPMNormal, extraFall * Time.fixedDeltaTime);
                }
            }

            // Subtle pull-down after recent upshift if lifted
            if (!_shift.IsShifting && _shift.LastShiftWasUpshift && !pedalDown && (Time.time - _shift.LastShiftTime) < 0.35f)
                _engineRPM = Mathf.MoveTowards(_engineRPM, targetRPMNormal, Time.fixedDeltaTime * 8000f);

            // Rough idle (only near idle & low gears)
            if (_ctx.settings.roughIdle && !pedalDown && gearIndex == 1 && EngineRPM < 1300f)
            {
                float noise = Mathf.PerlinNoise(Time.time * _ctx.settings.idleJitterSpeed, _idleNoiseOffset);
                float centered = (noise - 0.5f) * 2f;
                _engineRPM += centered * _ctx.settings.idleJitterAmplitude;
                _engineRPM = Mathf.Clamp(_engineRPM, _ctx.settings.idleRPM * 0.8f, _ctx.settings.revLimiterRPM + 200f);
            }

            // Normal positive engine torque (non-coupled)
            float engineTorqueNormal = 0f;
            if (pedalDown && !_shift.IsTorqueCutActive && !_shift.IsLiftCutActive)
            {
                engineTorqueNormal = _ctx.settings.torqueCurve.Evaluate(_engineRPM) * throttle01 * _ctx.settings.torqueMultiplier;
                engineTorqueNormal *= HighRpmFalloff(_engineRPM);
                engineTorqueNormal *= _shift.PostShiftTorqueScale;

                if (_hardCutActive) engineTorqueNormal = 0f;

                if (gearIndex == 0) engineTorqueNormal *= _ctx.settings.reverseTorqueMultiplier;
                if (_engineRPM >= _ctx.settings.revLimiterRPM && throttle01 > 0.1f) engineTorqueNormal = 0f;

                if (launchActive && inFirst && !instantActive)
                {
                    float tL = Mathf.Clamp01((Time.time - _shift.LastShiftTime) / 0.5f);
                    float vL = Mathf.Clamp01(speedMs / 2.0f);
                    float ease = tL * tL * (3f - 2f * tL);
                    float blend = Mathf.Max(0.25f, Mathf.Min(1f, 0.25f + 0.75f * Mathf.Max(ease, vL)));
                    engineTorqueNormal *= blend;
                }
            }

            // Extra pull-down on lift
            if (!pedalDown && _engineRPM > _ctx.settings.idleRPM * 1.1f)
            {
                if (!neutralLike) // in gear: gentle extra damping is fine
                    _engineRPM = Mathf.MoveTowards(_engineRPM, targetRPMNormal, Time.fixedDeltaTime * 4000f);
            }

            // Reverse explicit drive (non-coupled)
            if (gearIndex == 0 && pedalDown && !_shift.IsTorqueCutActive && !_shift.IsLiftCutActive)
            {
                float baseNm = _ctx.settings.torqueCurve.Evaluate(Mathf.Max(_engineRPM, _ctx.settings.idleRPM))
                               * throttle01 * _ctx.settings.torqueMultiplier
                               * Mathf.Max(0.1f, _ctx.settings.reverseTorqueMultiplier);

                float axleAbs = Mathf.Abs(ratio * _ctx.settings.finalDrive) * _ctx.settings.drivetrainEfficiency;
                float axleNm = -baseNm * axleAbs * Mathf.Clamp01(_shift.Clutch); // negative = reverse
                float perWheel = axleNm * 0.5f;

                _ctx.RL.motorTorque = perWheel;
                _ctx.RR.motorTorque = perWheel;
                return;
            }

            // Drive or kill if cuts/hard-cut/lift (keep drive in soft limiter band)
            float driveTorque = engineTorqueNormal * ratio * _ctx.settings.finalDrive * _ctx.settings.drivetrainEfficiency * Mathf.Clamp01(clutch);
            if (!pedalDown || _shift.IsTorqueCutActive || _shift.IsLiftCutActive || _hardCutActive)
            {
                _ctx.RL.motorTorque = 0f;
                _ctx.RR.motorTorque = 0f;
            }
            else
            {
                if (limiterActive) driveTorque *= Mathf.Lerp(1f, 0.6f, limiterT); // gentle taper only
                float perWheelTorque = driveTorque * 0.5f; // RWD
                _ctx.RL.motorTorque = perWheelTorque;
                _ctx.RR.motorTorque = perWheelTorque;
            }

            // Guard the hard-cut cap only when not mechanically coupled
            if (_hardCutActive && !physicallyCoupled && _engineRPM > HARD_CUT_RPM - HARD_CUT_RPM_CAP_MARGIN)
                _engineRPM = HARD_CUT_RPM - HARD_CUT_RPM_CAP_MARGIN;

            dbg.runTwoMass = false;
            dbg.coupledNow = false;
            dbg.engineRPM = _engineRPM;
            dbg.engOmega = _engOmega;
            dbg.perWheelMotorNm = 0.5f * (_ctx.RL.motorTorque + _ctx.RR.motorTorque);
            dbg.perWheelBrakeNm = 0.5f * (_ctx.RL.brakeTorque + _ctx.RR.brakeTorque);
            DebugInfo = dbg;
        }

        private float HighRpmFalloff(float rpm)
        {
            // End the taper a bit before the hard limiter
            float end = Mathf.Max(POWER_TAPER_START_RPM + 100f, _ctx.settings.revLimiterRPM - POWER_TAPER_END_OFFSET);
            if (rpm <= POWER_TAPER_START_RPM) return 1f;
            if (rpm >= end) return 0f;

            float t = Mathf.InverseLerp(POWER_TAPER_START_RPM, end, rpm);
            // Aggressive drop: (smoothstep)^2
            float s = t * t * (3f - 2f * t);
            return 1f - s * s; // 1 → 0 quickly as rpm rises through the taper band
        }
    }
}
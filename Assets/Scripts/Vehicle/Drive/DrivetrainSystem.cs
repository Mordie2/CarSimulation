// ============================================= // Role: Engine + Transmission + Auto shift // ============================================= 
using UnityEngine;

namespace Vehicle
{
    public class DrivetrainSystem
    {
        private readonly VehicleContext _ctx;
        private int _gearIndex = 2; // start in N 
        private float _engineRPM = 1000f;
        private float _clutch = 1f;
        private bool _isShifting = false;
        private float _shiftTimer = 0f;
        private float _lastShiftTime = -999f;

        public float EngineRPM => _engineRPM;
        public string GearLabel => (_gearIndex == 0) ? "R" : (_gearIndex == 1) ? "N" : (_gearIndex - 1).ToString();
        public bool IsInReverse => _gearIndex == 0;
        public int GearIndex => _gearIndex;
        public event System.Action<int, int> OnGearChanged;

        private bool _isBurnout;
        public bool IsBurnout => _isBurnout;

        private bool _lastShiftWasUpshift = false;
        private float _postShiftFadeUntil = -999f;
        private int _postShiftFromGear = -1;
        private const float POST_SHIFT_FADE_12 = 0.22f; 

        enum DriveDir { Forward, Reverse }
        DriveDir _dir = DriveDir.Forward;

        // Tunable intent + hysteresis (small noise won’t flip it) 
        const float PRESS_INTENT = 0.20f; // pedal press threshold 
        const float RELEASE_INTENT = 0.10f; // release threshold (lower than press) 

        // --- torque cut around shifts (race-style) --- 
        private float _torqueCutUntil = -999f;
        // extra cut window after a lift (even without a shift) 
        private float _liftCutUntil = -999f;

        // tunables 
        private const float CUT_AFTER_SHIFT = 0.30f; // extra cut after shift ends (s) 
        private const float CUT_PEDAL_THRESH = 0.10f; // treat as "no pedal" below this 
        private const float POST_SHIFT_COAST = 1.5f; // engine-brake boost right after shift 
        private const float LIFT_CUT_TAIL = 0.20f; // keep torque cut this long after a lift 
        private const float POS_TORQUE_REARM = 0.12f; // s after shift end 
        private const float CLUTCH_POS_ENABLE = 0.98f; // clutch almost closed 
        private const float EMERGENCY_RPM = 7400f; // force upshift here 

        // Launch control state 
        private enum LaunchState { Idle, Armed, Active, Cooldown }
        private LaunchState _launchState = LaunchState.Idle;
        private float _launchStartTime = -999f;
        private float _launchCooldownUntil = -999f;

        // Launchcontrol Tunables 
        private const float LAUNCH_ARM_SPEED = 0.4f; // m/s 
        private const float LAUNCH_ARM_THROTTLE = 0.60f;
        private const float LAUNCH_EXIT_SPEED = 3.0f; // m/s 
        private const float LAUNCH_EXIT_THR = 0.20f;
        private const float LAUNCH_MAX_DURATION = 1.5f; // s 
        private const float LAUNCH_COOLDOWN = 1.0f; // s 
        private const float LAUNCH_DISABLE_SPEED = 10f / 3.6f; // 10 km/h in m/s (~2.7778) 

        // --- minimal flywheel model (two-mass during/after shifts) --- 
        private float _engOmega = 0f; // rad/s (engine/flywheel) 
        private const float TWO_PI = 6.2831853f;
        private const float FLYWHEEL_BLEND = 0.25f; // seconds after a shift to keep model active 
        private float _TclutchPrev = 0f;
        private const float TCLUTCH_SLEW = 8000f; // Nm/s (5k–12k reasonable) 

        // Clutch Tunables
        private const float CLUTCH_CAP_NM = 1000f; // max clutch torque capacity (Nm) at full engagement 
        private const float CLUTCH_VISCOUS = 3.5f; // viscous term (Nm per rad/s of slip) 
        private const float ENGINE_DRAG_IDLE = 20f; // pumping/friction Nm near idle 
        private const float ENGINE_DRAG_COEF = 0.020f; // extra Nm per rad/s (linear approx) 
        private const float SHIFT_CLUTCH_SCALE = 0.25f; // reduce clutch capacity during shift (slip feel) 

        // --- Clutch anti-hunt / debounce --- 
        private float _upCondTimer = 0f;
        private float _downCondTimer = 0f;
        private float _gearDwellUntil = -999f; // no auto-shifts during dwell 
        private const float COND_HOLD_TIME = 0.25f; // s the condition must persist 
        private const float GEAR_DWELL_TIME = 0.60f; // s after *any* shift 

        // kickdown / demand thresholds 
        private const float DOWNSHIFT_DEMAND_THR = 0.35f; // need this much pedal to downshift 
        private const float CRUISE_NO_SHIFT_THR = 0.08f; // if pedal under this, don't auto change 
        private const float SLIP_DEADZONE = 6f; // rad/s (~57 rpm) – ignore tiny slip 
        private const float POST_WINDOW_SCALE = 0.55f; // soften clutch cap during the blend window 
        private const float POS_TORQUE_THR = 0.25f; // require real pedal to allow positive wheel drive 



        // gear side smoothing 
        private float _gearOmegaLP = 0f;
        private const float GEAR_OMEGA_TAU = 0.16f; // s (0.05–0.10 good) 

        public DrivetrainSystem(VehicleContext ctx)
        {
            _ctx = ctx;
            _dir = (_gearIndex == 0) ? DriveDir.Reverse : DriveDir.Forward;

            // --- Ensure there is a sane, negative reverse ratio --- 
            if (_ctx.settings.gearRatios == null || _ctx.settings.gearRatios.Length < 2)
            {
                _ctx.settings.gearRatios = new float[] { -3.20f, 0f, 3.20f, 2.20f, 1.55f, 1.20f, 0.98f };
                Debug.LogWarning("[Drive] gearRatios missing; using safe defaults.");
            }
            if (Mathf.Abs(_ctx.settings.gearRatios[0]) < 0.01f || _ctx.settings.gearRatios[0] > 0f)
            {
                Debug.LogWarning($"[Drive] Reverse ratio invalid ({_ctx.settings.gearRatios[0]}). Forcing -3.20.");
                _ctx.settings.gearRatios[0] = -3.20f;
            }

            // Seed flywheel from current engine RPM (ensure >= idle) 
            _engineRPM = Mathf.Max(_engineRPM, _ctx.settings.idleRPM);
            _engOmega = _engineRPM * TWO_PI / 60f;

            float r = _ctx.RL.radius;
            float wheelRPS = _ctx.rb.velocity.magnitude / (2f * Mathf.PI * r);
            float wheelRPM = wheelRPS * 60f;
            float ratioAbs0 = Mathf.Abs(GetCurrentRatio() * _ctx.settings.finalDrive);
            _gearOmegaLP = Mathf.Max(wheelRPM * TWO_PI / 60f * ratioAbs0, 0f);
        }

        private bool DetectBurnout(IInputProvider input)
        {
            // Gas + brake held, car basically stopped 
            return (input.Throttle > 0.9f && input.Brake > 0.9f && _ctx.rb.velocity.magnitude < 3f);
        }

        public void TickShifting(IInputProvider input, float speedMs, bool burnoutMode)
        {
            float throttle01 = (_gearIndex == 0) ? Mathf.Clamp01(input.Brake) : Mathf.Clamp01(input.Throttle);
            bool demand = throttle01 > CUT_PEDAL_THRESH; // only upshift when driver asks for power 

            // Burnout: lock to 1st, freeze any shift timing 
            if (burnoutMode)
            {
                _dir = DriveDir.Forward;
                if (_gearIndex <= 1) _gearIndex = 2;
                _isShifting = false;
                _shiftTimer = 0f;
                return;
            }

            bool stopped = speedMs < 1.2f;

            // Latch desired direction only when stopped 
            if (stopped)
            {
                if (input.Brake > PRESS_INTENT) _dir = DriveDir.Reverse;
                if (input.Throttle > PRESS_INTENT) _dir = DriveDir.Forward;

                // hysteresis 
                if (_dir == DriveDir.Reverse && input.Brake < RELEASE_INTENT && input.Throttle > PRESS_INTENT) _dir = DriveDir.Forward;
                if (_dir == DriveDir.Forward && input.Throttle < RELEASE_INTENT && input.Brake > PRESS_INTENT) _dir = DriveDir.Reverse;
            }

            // Apply latched direction when stopped 
            if (stopped)
            {
                if (_dir == DriveDir.Reverse && _gearIndex != 0)
                {
                    SetReverseInstant();
                    return;
                }
                if (_dir == DriveDir.Forward && _gearIndex < 2)
                {
                    StartShift(2);
                    return;
                }
            }

            // Manual bumpers (optional) 
            if (input.RequestShiftUp) ShiftUp();
            if (input.RequestShiftDown) ShiftDown();

            // --- Auto up/downshift while moving (anti-hunt) --- 
            if (_ctx.settings.automatic && _gearIndex >= 2 && !_isShifting)
            {
                // No decisions during dwell or right after a shift 
                if (Time.time >= _gearDwellUntil && (Time.time - _lastShiftTime) > _ctx.settings.minShiftInterval)
                {
                    float speedWheelRPM = GetWheelRPMFromGround();
                    int last = _ctx.settings.gearRatios.Length - 1;

                    // If driver is basically cruising/lifted, hold gear (prevents hunting on tiny pedal) 
                    bool allowAnyShift = (Mathf.Max(input.Throttle, input.Brake) > CRUISE_NO_SHIFT_THR);

                    float currRatioAbs = Mathf.Abs(_ctx.settings.gearRatios[_gearIndex] * _ctx.settings.finalDrive);
                    float mechRPM = speedWheelRPM * currRatioAbs;

                    // Hysteresis: use already-existing settings + margins 
                    float upOn = Mathf.Min(_ctx.settings.shiftUpRPM, _ctx.settings.revLimiterRPM - 200f);
                    float downOn = _ctx.settings.shiftDownRPM;

                    // ---- UPSHIFT (needs demand + sustained high RPM) ---- 
                    bool canUpshift = allowAnyShift && (_gearIndex < last) && (throttle01 >= 0.5f);
                    

                    if (canUpshift)
                    {
                        float nextRatioAbs = Mathf.Abs(_ctx.settings.gearRatios[_gearIndex + 1] * _ctx.settings.finalDrive);
                        float nextRPMPred = speedWheelRPM * nextRatioAbs;

                        // only upshift if predicted next gear keeps us above a safe band 
                        bool nextOK = nextRPMPred > _ctx.settings.shiftDownRPM * _ctx.settings.predictedDownshiftMargin;
                        bool upCond = (mechRPM >= upOn) && nextOK && (throttle01 >= 0.5f);

                        _upCondTimer = upCond ? _upCondTimer + Time.fixedDeltaTime : 0f;
                        if (_upCondTimer >= COND_HOLD_TIME)
                        {
                            StartShift(_gearIndex + 1);
                            _lastShiftTime = Time.time;
                            _gearDwellUntil = Time.time + GEAR_DWELL_TIME;
                            _upCondTimer = 0f;
                            _downCondTimer = 0f;
                            return; // only one decision per frame 
                        }
                    }
                    else _upCondTimer = 0f;

                    // ---- DOWNSHIFT (needs real demand OR lugging + sustained low RPM) ---- 
                    bool canDownshift = (_gearIndex > 2);
                    if (canDownshift)
                    {
                        // require significant pedal (kickdown) unless we're really lugging 
                        bool demandDown = (input.Throttle >= DOWNSHIFT_DEMAND_THR);
                        bool rpmLow = (mechRPM < downOn * 0.98f); // a whisper of hysteresis 
                        bool downCond = (rpmLow && (demandDown || mechRPM < downOn * 0.85f)); // force if very low 

                        _downCondTimer = downCond ? _downCondTimer + Time.fixedDeltaTime : 0f;
                        if (_downCondTimer >= COND_HOLD_TIME)
                        {
                            StartShift(_gearIndex - 1);
                            _lastShiftTime = Time.time;
                            _gearDwellUntil = Time.time + GEAR_DWELL_TIME;
                            _upCondTimer = 0f;
                            _downCondTimer = 0f;
                            return;
                        }
                    }
                    else _downCondTimer = 0f;

                    // From N to 1st when driver actually gives throttle 
                    if (_gearIndex == 1 && input.Throttle > 0.1f)
                    {
                        StartShift(2);
                        _lastShiftTime = Time.time;
                        _gearDwellUntil = Time.time + GEAR_DWELL_TIME;
                        _upCondTimer = 0f;
                        _downCondTimer = 0f;
                        return;
                    }
                }
                else
                {
                    // During dwell, reset timers so we don't “bank” a decision 
                    _upCondTimer = 0f;
                    _downCondTimer = 0f;
                }
            }

            // Emergency upshift: protect engine from overrev 
            if (!_isShifting && _gearIndex >= 2 && _engineRPM >= EMERGENCY_RPM)
            {
                int last = _ctx.settings.gearRatios.Length - 1;
                int target = Mathf.Min(_gearIndex + 1, last);
                StartShift(target);
                return;
            }

            // Shift timing/clutch ONLY – torque is handled in TickPowertrain() 
            if (_isShifting)
            {
                _shiftTimer += Time.fixedDeltaTime;
                float t = _shiftTimer / _ctx.settings.shiftTime;
                _clutch = (t < 0.5f) ? Mathf.Lerp(1f, 0f, t * 2f) : Mathf.Lerp(0f, 1f, (t - 0.5f) * 2f);

                if (_shiftTimer >= _ctx.settings.shiftTime)
                {
                    _isShifting = false;
                    if (_postShiftFromGear == 2) // from 1st 
                        _postShiftFadeUntil = Time.time + POST_SHIFT_FADE_12;
                    _postShiftFromGear = -1;
                    _shiftTimer = 0f;
                    _clutch = 1f;
                }
            }
            else
            {
                _clutch = Mathf.MoveTowards(_clutch, 1f, Time.fixedDeltaTime * 5f);
            }
        }

        public void TickPowertrain(IInputProvider input, float speedMs, bool burnoutMode)
        {
            _ctx.RL.motorTorque = 0f;
            _ctx.RR.motorTorque = 0f;

            // Map pedals to throttle (reverse uses brake as throttle) 
            float throttle01 = (_gearIndex == 0) ? Mathf.Clamp01(input.Brake) : Mathf.Clamp01(input.Throttle);

            // latch a short lift-cut tail whenever the pedal is below threshold 
            if (throttle01 < CUT_PEDAL_THRESH) _liftCutUntil = Time.time + LIFT_CUT_TAIL;

            bool pedalDown = throttle01 >= CUT_PEDAL_THRESH;
            bool cutWindowActive = (Time.time < _torqueCutUntil);
            bool liftCutActive = (Time.time < _liftCutUntil);

            // ----------- Launch Control State Machine (1st gear only) ----------- 
            bool inFirst = (_gearIndex == 2); // your 1st gear index 

            if (speedMs > LAUNCH_DISABLE_SPEED && _launchState != LaunchState.Idle)
            {
                _launchState = LaunchState.Cooldown;
                _launchCooldownUntil = Time.time + LAUNCH_COOLDOWN;
            }

            switch (_launchState)
            {
                case LaunchState.Idle:
                    if (inFirst && speedMs < LAUNCH_ARM_SPEED && throttle01 > LAUNCH_ARM_THROTTLE && Time.time >= _launchCooldownUntil)
                        _launchState = LaunchState.Armed;
                    break;

                case LaunchState.Armed:
                    // Go Active as soon as we’re still in 1st and throttle is still committed 
                    if (!inFirst || throttle01 < LAUNCH_ARM_THROTTLE)
                        _launchState = LaunchState.Idle;
                    else
                    {
                        _launchState = LaunchState.Active;
                        _launchStartTime = Time.time;
                    }
                    break;

                case LaunchState.Active:
                    // Exit conditions: rolling, lift, time cap, or we’ve already sync’d above launch 
                    if (!inFirst || speedMs > LAUNCH_EXIT_SPEED || throttle01 < LAUNCH_EXIT_THR || (Time.time - _launchStartTime) > LAUNCH_MAX_DURATION)
                    {
                        _launchState = LaunchState.Cooldown;
                        _launchCooldownUntil = Time.time + LAUNCH_COOLDOWN;
                    }
                    break;

                case LaunchState.Cooldown:
                    if (Time.time >= _launchCooldownUntil)
                        _launchState = LaunchState.Idle;
                    break;
            }
            // -------------------------------------------------------------------- 

            // ------------------------ 
            // Burnout logic (unchanged) 
            // ------------------------ 
            if (burnoutMode)
            {
                float targetRPM = Mathf.Lerp(_ctx.settings.idleRPM, _ctx.settings.maxRPM, throttle01);
                float follow = Time.fixedDeltaTime / Mathf.Max(0.0001f, _ctx.settings.engineInertia);
                _engineRPM = Mathf.Clamp(
                    Mathf.Lerp(_engineRPM, targetRPM, Mathf.Clamp01(follow)),
                    _ctx.settings.idleRPM * 0.8f,
                    _ctx.settings.revLimiterRPM + 200f
                );

                float engineTorque = _ctx.settings.torqueCurve.Evaluate(_engineRPM) * throttle01 * _ctx.settings.torqueMultiplier;

                // Lock fronts 
                _ctx.FL.motorTorque = 0f;
                _ctx.FR.motorTorque = 0f;
                _ctx.FL.brakeTorque = 1e9f;
                _ctx.FR.brakeTorque = 1e9f;

                // Drive rears only 
                float perRear = (engineTorque * GetCurrentRatio() * _ctx.settings.finalDrive * _ctx.settings.drivetrainEfficiency) * 0.5f;
                _ctx.RL.motorTorque = perRear;
                _ctx.RR.motorTorque = perRear;
                _ctx.RL.brakeTorque = 0f;
                _ctx.RR.brakeTorque = 0f;
                return;
            }

            // ------------------------ 
            // Normal drivetrain logic 
            // ------------------------ 
            float ratioAbs = Mathf.Abs(GetCurrentRatio() * _ctx.settings.finalDrive);
            float speedWheelRPM = GetWheelRPMFromGround();
            bool inGear = !Mathf.Approximately(GetCurrentRatio(), 0f);

            // If we're in the middle of a shift: free-rev and DO NOT send torque to wheels 
            if (_isShifting)
            {
                // Rev-match the engine to the *new* gear’s wheel-synced RPM 
                float newRatioAbs = Mathf.Abs(GetCurrentRatio() * _ctx.settings.finalDrive);
                float syncRPM = Mathf.Clamp(
                    Mathf.Max(speedWheelRPM * newRatioAbs, _ctx.settings.idleRPM),
                    _ctx.settings.idleRPM * 0.8f,
                    _ctx.settings.revLimiterRPM + 200f
                );

                // Blend toward syncRPM quickly as the shift progresses 
                float t = Mathf.Clamp01(_shiftTimer / Mathf.Max(0.0001f, _ctx.settings.shiftTime));
                // more aggressive snap early to kill mismatch 
                float snap = Mathf.Lerp(0.25f, 1.0f, t); // start fast, finish exact 
                float followShift = Time.fixedDeltaTime / Mathf.Max(0.0001f, _ctx.settings.engineInertia * snap);
                _engineRPM = Mathf.Lerp(_engineRPM, syncRPM, Mathf.Clamp01(followShift));

                // Absolutely no drive torque during the shift 
                _ctx.RL.motorTorque = 0f;
                _ctx.RR.motorTorque = 0f;

                // ADD: light engine-brake even while shifting IF it's an upshift and driver is lifted 
                if (_lastShiftWasUpshift && !pedalDown && !input.Handbrake)
                {
                    float tShift = Mathf.Clamp01(_shiftTimer / Mathf.Max(0.0001f, _ctx.settings.shiftTime));
                    float shiftSnap = Mathf.Lerp(0.25f, 1.0f, tShift);
                    float ratioAbsDyn = Mathf.Abs(GetCurrentRatio() * _ctx.settings.finalDrive);
                    float rpmFactor = Mathf.InverseLerp(
                        _ctx.settings.idleRPM * 0.9f,
                        _ctx.settings.maxRPM,
                        Mathf.Clamp(_engineRPM, _ctx.settings.idleRPM * 0.9f, _ctx.settings.maxRPM)
                    );
                    float coastNmAtWheel = _ctx.settings.engineBrakeTorque * ratioAbsDyn * _ctx.settings.drivetrainEfficiency * rpmFactor * shiftSnap;
                    float perWheelCoast = coastNmAtWheel * 0.5f; // RWD split 
                    _ctx.RL.brakeTorque += perWheelCoast;
                    _ctx.RR.brakeTorque += perWheelCoast;
                }
                return;
            }

            // Not shifting: compute target/mech RPM 
            float targetRPMNormal = _engineRPM; // fallback to current RPM 
            if (_clutch > 0.99f && inGear)
            {
                float mechRPM = Mathf.Max(speedWheelRPM * ratioAbs, _ctx.settings.idleRPM);
                if (_launchState == LaunchState.Active && inFirst)
                {
                    mechRPM = Mathf.Max(mechRPM, _ctx.settings.minLaunchRPM);
                }
                targetRPMNormal = mechRPM;
            }
            else
            {
                // free-rev when decoupled 
                targetRPMNormal = Mathf.Lerp(_ctx.settings.idleRPM, _ctx.settings.maxRPM, throttle01);
            }

            // --- FLYWHEEL WINDOW: use 2-mass integration during shift and ~0.35s after --- 
            bool flywheelWindow = _isShifting || ((Time.time - _lastShiftTime) < FLYWHEEL_BLEND);
            if (flywheelWindow)
            {
                float dt = Time.fixedDeltaTime;
                float ratioAbsFW = Mathf.Abs(GetCurrentRatio() * _ctx.settings.finalDrive);
                float gearOmegaRaw = Mathf.Max(speedWheelRPM * TWO_PI / 60f * ratioAbsFW, 0f);

                // 1-pole low-pass for the gear side 
                float a = 1f - Mathf.Exp(-dt / GEAR_OMEGA_TAU);
                _gearOmegaLP = Mathf.Lerp(_gearOmegaLP, gearOmegaRaw, a);
                float gearOmega = _gearOmegaLP;

                float T_req = 0f;
                if (pedalDown && !cutWindowActive && !liftCutActive)
                {
                    T_req = _ctx.settings.torqueCurve.Evaluate(_engineRPM) * throttle01 * _ctx.settings.torqueMultiplier;
                    T_req *= PostShiftScale();
                    if (_gearIndex == 0) T_req *= _ctx.settings.reverseTorqueMultiplier;
                    if (_engineRPM >= _ctx.settings.revLimiterRPM && throttle01 > 0.1f) T_req = 0f;
                }

                // Engine internal drag (pumping/friction) 
                float T_drag = ENGINE_DRAG_IDLE + ENGINE_DRAG_COEF * Mathf.Max(_engOmega, 0f);

                // Clutch coupling (limited, viscous). Reduce capacity during shift for slip feel. 
                // --- clutch coupling (directional, with deadzone, softened after shift) --- 
                float clutchEngage = _clutch;
                // soften capacity both during the shift and for the post-shift blend window 
                bool postBlend = !_isShifting; // we are in flywheelWindow but not actively shifting 
                float cap = CLUTCH_CAP_NM * clutchEngage * (_isShifting ? SHIFT_CLUTCH_SCALE : POST_WINDOW_SCALE);

                float slip = _engOmega - gearOmega;
                // deadzone to stop chatter around sync 
                float slipEff = Mathf.Abs(slip) < SLIP_DEADZONE ? 0f : (slip - Mathf.Sign(slip) * SLIP_DEADZONE);

                // viscous coupling 
                float T_clutch_visc = -CLUTCH_VISCOUS * slipEff; // resists slip 

                bool allowPositive = !_isShifting && _clutch >= CLUTCH_POS_ENABLE && (Time.time - _lastShiftTime) >= POS_TORQUE_REARM && throttle01 >= POS_TORQUE_THR && !cutWindowActive && !liftCutActive;
                float capPos = allowPositive ? cap : 0f;
                float capNeg = cap; // always allow engine-brake to wheels 

                float T_clutch = Mathf.Clamp(T_clutch_visc, -capNeg, capPos);

                float maxStep = TCLUTCH_SLEW * dt;
                T_clutch = Mathf.Clamp(T_clutch, _TclutchPrev - maxStep, _TclutchPrev + maxStep);
                _TclutchPrev = T_clutch;

                // Integrate engine omega using engine inertia 
                // (your settings.engineInertia is “time constant”-like; 
                // here we treat it as kg·m² equivalent. If your inertia is too small/large, 
                // adjust feel with CLUTCH_VISCOUS & caps.) 
                float I = Mathf.Max(0.0001f, _ctx.settings.engineInertia);
                float T_net_engine = T_req - T_drag - T_clutch;
                float domega = (T_net_engine / I) * dt;
                _engOmega = Mathf.Max(0f, _engOmega + domega);

                // Write back engineRPM from physics 
                _engineRPM = Mathf.Clamp(_engOmega * 60f / TWO_PI, _ctx.settings.idleRPM * 0.8f, _ctx.settings.revLimiterRPM + 200f);

                // Wheel drive torque contribution from clutch → through gears to axle 
                // Note: sign is POS to wheels when slip<0 (engine faster than gear), NEG when overrun 
                float driveTorqueFromClutch = T_clutch * Mathf.Sign(GetCurrentRatio()) * _ctx.settings.finalDrive * _ctx.settings.drivetrainEfficiency;

                // Apply to wheels respecting your existing cut logic (no positive drive if cut/lift) 
                // Positive drive only when driver asks for it and no cut: 
                if (pedalDown && !cutWindowActive && !liftCutActive && driveTorqueFromClutch > 0f)
                {
                    float perWheel = (driveTorqueFromClutch * 0.5f); // RWD split 
                    _ctx.RL.motorTorque = perWheel;
                    _ctx.RR.motorTorque = perWheel;
                }
                else
                {
                    _ctx.RL.motorTorque = 0f;
                    _ctx.RR.motorTorque = 0f;
                }

                // Engine braking (negative drive) always transmits via clutch during flywheel window 
                if (!input.Handbrake && driveTorqueFromClutch < 0f)
                {
                    float perWheelCoast = (-driveTorqueFromClutch) * 0.5f; // convert to brake torque 
                    _ctx.RL.brakeTorque += perWheelCoast;
                    _ctx.RR.brakeTorque += perWheelCoast;
                }
                return; // Done — skip the old lerp path for this window 
            }
            else
            {
                // Fallback to your original asymmetric inertia lerp outside the window 
                const float DOWN_FACTOR = 0.1f;
                float inertiaUp = Mathf.Max(0.0001f, _ctx.settings.engineInertia);
                float inertiaDown = Mathf.Max(0.0001f, _ctx.settings.engineInertia * DOWN_FACTOR);
                float inertia = (targetRPMNormal < _engineRPM) ? inertiaDown : inertiaUp;
                float alpha = Mathf.Clamp01(Time.fixedDeltaTime / inertia);
                _engineRPM = Mathf.Clamp(
                    Mathf.Lerp(_engineRPM, targetRPMNormal, alpha),
                    _ctx.settings.idleRPM * 0.8f,
                    _ctx.settings.revLimiterRPM + 200f
                );
                // keep your existing torque computation & wheel/brake logic below… 
            }

            // ... 
            if (!flywheelWindow)
            {
                bool recentlyShifted = (Time.time - _lastShiftTime) < 0.35f;
                if (_lastShiftWasUpshift && !pedalDown && recentlyShifted)
                    _engineRPM = Mathf.MoveTowards(_engineRPM, targetRPMNormal, Time.fixedDeltaTime * 8000f);
            }

            
            float engineTorqueNormal = 0f;
            if (pedalDown && !cutWindowActive && !liftCutActive)
            {
                engineTorqueNormal = _ctx.settings.torqueCurve.Evaluate(_engineRPM) * throttle01 * _ctx.settings.torqueMultiplier;
                engineTorqueNormal *= PostShiftScale();

                if (_gearIndex == 0) engineTorqueNormal *= _ctx.settings.reverseTorqueMultiplier;
                if (_engineRPM >= _ctx.settings.revLimiterRPM && throttle01 > 0.1f) engineTorqueNormal = 0f;

                // launch softening (keep yours)
                if (_launchState == LaunchState.Active && inFirst)
                {
                    float tL = Mathf.Clamp01((Time.time - _launchStartTime) / 0.5f);
                    float vL = Mathf.Clamp01(speedMs / 2.0f);
                    float ease = tL * tL * (3f - 2f * tL);
                    float blend = Mathf.Max(0.25f, Mathf.Min(1f, 0.25f + 0.75f * Mathf.Max(ease, vL)));
                    engineTorqueNormal *= blend;
                }
            }
            // extra RPM pull-down on lift (keep as you had) 
            if (!pedalDown && _engineRPM > _ctx.settings.idleRPM * 1.1f)
            {
                _engineRPM = Mathf.MoveTowards(_engineRPM, targetRPMNormal, Time.fixedDeltaTime * 4000f);
            }

            float driveTorque = engineTorqueNormal * GetCurrentRatio() * _ctx.settings.finalDrive * _ctx.settings.drivetrainEfficiency * _clutch;

            // Hard kill of drive torque if any cut window is active OR no pedal 
            if (!pedalDown || cutWindowActive || liftCutActive)
            {
                _ctx.RL.motorTorque = 0f;
                _ctx.RR.motorTorque = 0f;
            }
            else
            {
                float perWheelTorque = driveTorque * 0.5f; // RWD 
                _ctx.RL.motorTorque = perWheelTorque;
                _ctx.RR.motorTorque = perWheelTorque;
            }

            bool clutchEngaged = _clutch > 0.95f;
            bool noService = !input.Handbrake;
            if (inGear && clutchEngaged && !pedalDown && noService)
            {
                float rpmFactor = Mathf.InverseLerp(
                    _ctx.settings.idleRPM * 0.9f,
                    _ctx.settings.maxRPM,
                    Mathf.Clamp(_engineRPM, _ctx.settings.idleRPM * 0.9f, _ctx.settings.maxRPM)
                );
                float coastBoost = (cutWindowActive || liftCutActive) ? POST_SHIFT_COAST : 1f;
                float coastNmAtWheel = _ctx.settings.engineBrakeTorque * ratioAbs * _ctx.settings.drivetrainEfficiency * rpmFactor * coastBoost;
                float perWheelCoast = coastNmAtWheel * 0.5f;
                _ctx.RL.brakeTorque += perWheelCoast;
                _ctx.RR.brakeTorque += perWheelCoast;
            }

            // NEW: coast assist during shifts / partial clutch (covers all gears) 
            // Only for upshifts so downshifts don't feel grabby 
            if (_lastShiftWasUpshift && inGear && !pedalDown && noService && (_isShifting || _clutch < 0.95f))
            {
                float ratioAbsDyn = Mathf.Abs(GetCurrentRatio() * _ctx.settings.finalDrive);
                float tShift = Mathf.Clamp01(_shiftTimer / Mathf.Max(0.0001f, _ctx.settings.shiftTime));
                float clutchScale = Mathf.SmoothStep(0f, 1f, _clutch);
                float shiftSnap = Mathf.Lerp(0.35f, 1.0f, tShift);
                float rpmFactor = Mathf.InverseLerp(
                    _ctx.settings.idleRPM * 0.9f,
                    _ctx.settings.maxRPM,
                    Mathf.Clamp(_engineRPM, _ctx.settings.idleRPM * 0.9f, _ctx.settings.maxRPM)
                );
                float coastNmAtWheel = _ctx.settings.engineBrakeTorque * ratioAbsDyn * _ctx.settings.drivetrainEfficiency * rpmFactor * clutchScale * shiftSnap;
                float perWheelCoast = coastNmAtWheel * 0.5f;
                _ctx.RL.brakeTorque += perWheelCoast;
                _ctx.RR.brakeTorque += perWheelCoast;
            }
        }

        private float GetCurrentRatio() => _ctx.settings.gearRatios[Mathf.Clamp(_gearIndex, 0, _ctx.settings.gearRatios.Length - 1)];

        private float GetWheelRPMFromGround()
        {
            float r = _ctx.RL.radius; // <- no parentheses 
            float wheelRPS = _ctx.rb.velocity.magnitude / (2f * Mathf.PI * r);
            return wheelRPS * 60f;
        }

        public void ShiftUp()
        {
            if (_gearIndex < _ctx.settings.gearRatios.Length - 1) StartShift(_gearIndex + 1);
        }

        public void ShiftDown()
        {
            int target = _gearIndex - 1;
            if (target == 0 && _ctx.rb.velocity.magnitude > 0.5f) target = 1;
            StartShift(target);
        }

        private void StartShift(int newIndex)
        {
            newIndex = Mathf.Clamp(newIndex, 0, _ctx.settings.gearRatios.Length - 1);
            if (newIndex == _gearIndex) return;

            int old = _gearIndex;
            _gearIndex = newIndex;
            _isShifting = true;
            _shiftTimer = 0f;
            _lastShiftTime = Time.time;
            _lastShiftWasUpshift = (newIndex > old);
            _postShiftFromGear = old;
            _torqueCutUntil = Time.time + _ctx.settings.shiftTime + CUT_AFTER_SHIFT;
            _launchState = LaunchState.Cooldown;
            _launchCooldownUntil = Time.time + LAUNCH_COOLDOWN;

            OnGearChanged?.Invoke(old, newIndex);
            _gearDwellUntil = Time.time + GEAR_DWELL_TIME;
        }

        private void SetReverseInstant()
        {
            _gearIndex = 0;
            _isShifting = false;
            _shiftTimer = 0f;
        }

        private float PostShiftScale()
        {
            if (Time.time >= _postShiftFadeUntil) return 1f;
            float fadeStart = _postShiftFadeUntil - POST_SHIFT_FADE_12;   // start when we set _postShiftFadeUntil
            float t = Mathf.InverseLerp(fadeStart, _postShiftFadeUntil, Time.time);  // 0→1 over the fade window
            float minAfter = 0.55f;                                       // same flavor you had
            float smooth = t * t * (3f - 2f * t);                         // smoothstep
            return Mathf.Lerp(minAfter, 1f, smooth);
        }
    }
}

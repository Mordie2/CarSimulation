// =============================================
// File: Scripts/Vehicle/Drive/ShiftSystem.cs
// Role: Gears, clutch, auto/manual logic, launch control, torque cuts
// =============================================
using UnityEngine;

namespace Vehicle
{
    public class ShiftSystem
    {
        private readonly VehicleContext _ctx;

        // Gear model: 0=R, 1=N, 2=1st, 3=2nd, ...
        private int _gearIndex = 2; // start in 1st
        public int GearIndex => _gearIndex;
        public string GearLabel => (_gearIndex == 0) ? "R" : (_gearIndex == 1) ? "N" : (_gearIndex - 1).ToString();
        public bool IsInReverse => _gearIndex == 0;

        // Clutch & shift timing
        private bool _isShifting = false;
        private float _shiftTimer = 0f;
        private float _lastShiftTime = -999f;
        private float _clutch = 1f;             // 0..1 (0 = open, 1 = closed)
        public bool IsShifting => _isShifting;
        public float ShiftTimer => _shiftTimer;
        public float LastShiftTime => _lastShiftTime;
        public float Clutch => _clutch;

        public event System.Action<int, int> OnGearChanged;

        // Post-shift torque fade (1->2 upshift softness)
        private bool _lastShiftWasUpshift = false;
        private float _postShiftFadeUntil = -999f;
        private int _postShiftFromGear = -1;
        public bool LastShiftWasUpshift => _lastShiftWasUpshift;

        // Direction latch for auto
        private enum DriveDir { Forward, Reverse }
        private DriveDir _dir = DriveDir.Forward;

        // Intent hysteresis
        const float PRESS_INTENT = 0.20f;
        const float RELEASE_INTENT = 0.10f;

        // Torque cut windows
        private float _torqueCutUntil = -999f;
        private float _liftCutUntil = -999f;
        private const float CUT_AFTER_SHIFT = 0.1f;
        private const float CUT_PEDAL_THRESH = 0.10f;
        private const float LIFT_CUT_TAIL = 0.10f;
        public bool IsTorqueCutActive => Time.time < _torqueCutUntil;
        public bool IsLiftCutActive => Time.time < _liftCutUntil;

        // Anti-hunt & dwell
        private float _upCondTimer = 0f;
        private float _downCondTimer = 0f;
        private float _gearDwellUntil = -999f;
        private const float COND_HOLD_TIME = 0.25f;
        private const float GEAR_DWELL_TIME = 0.60f;

        // Gear protection / margins
        private const float EMERGENCY_RPM = 7200f;
        private const float CRUISE_NO_SHIFT_THR = 0.08f;
        private const float DOWNSHIFT_DEMAND_THR = 0.35f;

        // Launch control
        private enum LaunchState { Idle, Armed, Active, Cooldown }
        private LaunchState _launchState = LaunchState.Idle;
        private float _launchStartTime = -999f;
        private float _launchCooldownUntil = -999f;

        private bool _lcEnabled;
        private bool _lcInstant;
        public bool LaunchControlEnabled { get => _lcEnabled; set => _lcEnabled = value; }
        public bool LaunchInstantTorque { get => _lcInstant; set => _lcInstant = value; }

        // LC tunables
        private const float LAUNCH_ARM_SPEED = 0.4f;     // m/s
        private const float LAUNCH_ARM_THROTTLE = 0.60f;
        private const float LAUNCH_EXIT_SPEED = 3.0f;    // m/s
        private const float LAUNCH_EXIT_THR = 0.20f;
        private const float LAUNCH_MAX_DURATION = 1.5f;  // s
        private const float LAUNCH_COOLDOWN = 1.0f;      // s
        private const float LAUNCH_DISABLE_SPEED = 10f / 3.6f;

        // Post-shift fade from 1st
        private const float POST_SHIFT_FADE_12 = 0.22f;


        // Rev-protect clutch bleed (1st gear only)
        private const float REV_PROTECT_CLUTCH = 0.35f;   // target clutch while protecting
        private const float REV_PROTECT_HYST = 200f;    // RPM to drop before re-closing
        private const float REV_PROTECT_OPEN_RATE = 25f; // how fast we open (1/s)
        private const float REV_PROTECT_CLOSE_RATE = 6f;  // how fast we re-close (1/s)
        private bool _revProtectLatched = false;


        // Internal helpers
        private float GetWheelRPMFromGround()
        {
            float r = _ctx.RL.radius;
            float wheelRPS = _ctx.rb.velocity.magnitude / (2f * Mathf.PI * r);
            return wheelRPS * 60f;
        }

        public float GetCurrentRatio()
        {
            int idx = Mathf.Clamp(_gearIndex, 0, _ctx.settings.gearRatios.Length - 1);
            return _ctx.settings.gearRatios[idx];
        }

        public float PostShiftTorqueScale
        {
            get
            {
                if (Time.time >= _postShiftFadeUntil) return 1f;
                float fadeStart = _postShiftFadeUntil - POST_SHIFT_FADE_12;
                float t = Mathf.InverseLerp(fadeStart, _postShiftFadeUntil, Time.time);
                float minAfter = 0.55f;
                float smooth = t * t * (3f - 2f * t);
                return Mathf.Lerp(minAfter, 1f, smooth);
            }
        }

        public bool IsLaunchActive => _lcEnabled && _launchState == LaunchState.Active;
        public bool IsInstantTorqueActive(float speedMs, bool inFirst, float throttle01)
        {
            if (!_lcInstant) return false;
            return inFirst && (speedMs < LAUNCH_EXIT_SPEED) && (throttle01 > LAUNCH_EXIT_THR);
        }

        public ShiftSystem(VehicleContext ctx)
        {
            _ctx = ctx;
            _dir = (_gearIndex == 0) ? DriveDir.Reverse : DriveDir.Forward;

            // Ensure sane ratios
            if (_ctx.settings.gearRatios == null || _ctx.settings.gearRatios.Length < 2)
            {
                _ctx.settings.gearRatios = new float[] { -3.20f, 0f, 3.20f, 2.20f, 1.55f, 1.20f, 0.98f };
                Debug.LogWarning("[Shift] gearRatios missing; using safe defaults.");
            }
            if (Mathf.Abs(_ctx.settings.gearRatios[0]) < 0.01f || _ctx.settings.gearRatios[0] > 0f)
            {
                Debug.LogWarning($"[Shift] Reverse ratio invalid ({_ctx.settings.gearRatios[0]}). Forcing -3.20.");
                _ctx.settings.gearRatios[0] = -3.20f;
            }
            if (_ctx.settings.reverseTorqueMultiplier <= 0f)
            {
                Debug.LogWarning("[Shift] reverseTorqueMultiplier <= 0; forcing to 1");
                _ctx.settings.reverseTorqueMultiplier = 1f;
            }

            _lcEnabled = _ctx.settings.launchControlEnabledDefault;
            _lcInstant = _ctx.settings.launchInstantTorqueDefault;
        }

        // External manual requests
        public void RequestShiftUp() { ShiftUp(); }
        public void RequestShiftDown() { ShiftDown(); }
        public void ToggleLaunchControl() => _lcEnabled = !_lcEnabled;
        public void ToggleLaunchModeInstant() => _lcInstant = !_lcInstant;

        // Main shifting tick: updates gear/clutch/LC/cuts based on inputs & speed/RPM
        public void Tick(IInputProvider input, float speedMs, float engineRPM, float throttle01, bool burnoutMode)
        {
            // Latch lift-cut tail on pedal lift
            if (throttle01 < CUT_PEDAL_THRESH) _liftCutUntil = Time.time + LIFT_CUT_TAIL;

            // Burnout: lock to 1st and freeze any shift timing
            if (burnoutMode)
            {
                if (_gearIndex <= 1) _gearIndex = 2;
                _isShifting = false;
                _shiftTimer = 0f;
                _clutch = 1f;
                return;
            }

            bool stopped = speedMs < 1.2f;
            bool manualMode = !_ctx.settings.automatic;

            // Manual bumpers
            if (input.RequestShiftUp) ShiftUp();
            if (input.RequestShiftDown) ShiftDown();

            // Auto direction latch when stopped
            if (_ctx.settings.automatic)
            {
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
                    if (_dir == DriveDir.Reverse && _gearIndex != 0) { SetReverseInstant(); return; }
                    if (_dir == DriveDir.Forward && _gearIndex < 2)
                    {
                        if (_lcInstant) { SetFirstInstant(); return; }
                        StartShift(2);
                        return;
                    }
                }
            }

            // Emergency upshift
            if (_ctx.settings.automatic && !_isShifting && _gearIndex >= 2 && engineRPM >= EMERGENCY_RPM)
            {
                int last = _ctx.settings.gearRatios.Length - 1;
                int target = Mathf.Min(_gearIndex + 1, last);
                StartShift(target);
                return;
            }

            // Auto up/downshift anti-hunt
            if (_ctx.settings.automatic && _gearIndex >= 2 && !_isShifting)
            {
                if (Time.time >= _gearDwellUntil && (Time.time - _lastShiftTime) > _ctx.settings.minShiftInterval)
                {
                    float wheelRPM = GetWheelRPMFromGround();
                    int last = _ctx.settings.gearRatios.Length - 1;

                    bool allowAnyShift = (Mathf.Max(input.Throttle, input.Brake) > CRUISE_NO_SHIFT_THR);

                    float currRatioAbs = Mathf.Abs(_ctx.settings.gearRatios[_gearIndex] * _ctx.settings.finalDrive);
                    float mechRPM = wheelRPM * currRatioAbs;

                    float upOn = Mathf.Min(_ctx.settings.shiftUpRPM, _ctx.settings.revLimiterRPM - 200f);
                    float downOn = _ctx.settings.shiftDownRPM;

                    // Upshift: needs demand + sustained high RPM
                    bool canUpshift = allowAnyShift && (_gearIndex < last) && (throttle01 >= 0.5f);
                    if (canUpshift)
                    {
                        float nextRatioAbs = Mathf.Abs(_ctx.settings.gearRatios[_gearIndex + 1] * _ctx.settings.finalDrive);
                        float nextRPMPred = wheelRPM * nextRatioAbs;

                        bool nextOK = nextRPMPred > _ctx.settings.shiftDownRPM * _ctx.settings.predictedDownshiftMargin;
                        bool upCond = (mechRPM >= upOn) && nextOK && (throttle01 >= 0.5f);

                        _upCondTimer = upCond ? _upCondTimer + Time.fixedDeltaTime : 0f;
                        if (_upCondTimer >= COND_HOLD_TIME)
                        {
                            StartShift(_gearIndex + 1);
                            _upCondTimer = 0f;
                            _downCondTimer = 0f;
                            return;
                        }
                    }
                    else _upCondTimer = 0f;

                    // Downshift: demand OR lugging
                    bool canDownshift = (_gearIndex > 2);
                    if (canDownshift)
                    {
                        bool demandDown = (input.Throttle >= DOWNSHIFT_DEMAND_THR);
                        bool rpmLow = (mechRPM < downOn * 0.98f);
                        bool downCond = (rpmLow && (demandDown || mechRPM < downOn * 0.85f));

                        _downCondTimer = downCond ? _downCondTimer + Time.fixedDeltaTime : 0f;
                        if (_downCondTimer >= COND_HOLD_TIME)
                        {
                            StartShift(_gearIndex - 1);
                            _upCondTimer = 0f;
                            _downCondTimer = 0f;
                            return;
                        }
                    }
                    else _downCondTimer = 0f;

                    // From N to 1st on throttle
                    if (_gearIndex == 1 && input.Throttle > 0.1f)
                    {
                        if (_lcInstant) { SetFirstInstant(); return; }
                        StartShift(2);
                        _upCondTimer = 0f;
                        _downCondTimer = 0f;
                        return;
                    }
                }
                else
                {
                    _upCondTimer = 0f;
                    _downCondTimer = 0f;
                }
            }

            // Shift timing / clutch profile
            // Shift timing / clutch profile
            if (_isShifting)
            {
                _shiftTimer += Time.fixedDeltaTime;
                float t = _shiftTimer / _ctx.settings.shiftTime;
                _clutch = (t < 0.5f) ? Mathf.Lerp(1f, 0f, t * 2f) : Mathf.Lerp(0f, 1f, (t - 0.5f) * 2f);

                if (_shiftTimer >= _ctx.settings.shiftTime)
                {
                    _isShifting = false;
                    if (_postShiftFromGear == 2) // fade after leaving 1st
                        _postShiftFadeUntil = Time.time + POST_SHIFT_FADE_12;
                    _postShiftFromGear = -1;
                    _shiftTimer = 0f;
                    _clutch = 1f;
                }
            }
            else
            {
                // === NEW: first-gear slip control to hold launch RPM ===
                bool inFirstgear = (_gearIndex == 2);
                bool wantLaunchSlip =
                    inFirstgear &&
                    (speedMs < LAUNCH_EXIT_SPEED) &&
                    (throttle01 > LAUNCH_EXIT_THR) &&
                    !_isShifting;

                if (wantLaunchSlip)
                {
                    // Use your minLaunchRPM as the target; if it's lower than 3000, this will still work,
                    // but you likely want it ~3000 in VehicleSettings.
                    float targetLaunchRPM = Mathf.Max(_ctx.settings.minLaunchRPM, 3000f);

                    // Latch: while RPM is above target band, bias the clutch open toward a slip value.
                    if (!_revProtectLatched && engineRPM > (targetLaunchRPM + REV_PROTECT_HYST))
                        _revProtectLatched = true;

                    if (_revProtectLatched)
                    {
                        // Open quickly toward a mid clutch to let the engine spin up
                        _clutch = Mathf.MoveTowards(_clutch, REV_PROTECT_CLUTCH, REV_PROTECT_OPEN_RATE * Time.fixedDeltaTime);

                        // Drop the latch once we've fallen back into band so we can start closing again
                        if (engineRPM < (targetLaunchRPM - REV_PROTECT_HYST * 0.5f))
                            _revProtectLatched = false;
                    }
                    else
                    {
                        // Close the clutch steadily as speed rises so torque actually feeds in
                        _clutch = Mathf.MoveTowards(_clutch, 1f, REV_PROTECT_CLOSE_RATE * Time.fixedDeltaTime);
                    }
                }
                else
                {
                    // Default behavior away from a launch
                    _clutch = Mathf.MoveTowards(_clutch, 1f, Time.fixedDeltaTime * 5f);
                    _revProtectLatched = false;
                }
            }


            // Launch control state machine (only matters for 1st)
            bool inFirst = (_gearIndex == 2);
            if (speedMs > LAUNCH_DISABLE_SPEED && _launchState != LaunchState.Idle)
            {
                _launchState = LaunchState.Cooldown;
                _launchCooldownUntil = Time.time + LAUNCH_COOLDOWN;
            }

            switch (_launchState)
            {
                case LaunchState.Idle:
                    if (_lcEnabled && inFirst && speedMs < LAUNCH_ARM_SPEED && throttle01 > LAUNCH_ARM_THROTTLE && Time.time >= _launchCooldownUntil)
                        _launchState = LaunchState.Armed;
                    break;
                case LaunchState.Armed:
                    if (!inFirst || throttle01 < LAUNCH_ARM_THROTTLE)
                        _launchState = LaunchState.Idle;
                    else
                    {
                        _launchState = LaunchState.Active;
                        _launchStartTime = Time.time;
                    }
                    break;
                case LaunchState.Active:
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
        }

        // Public shifting API
        private void ShiftUp()
        {
            if (_gearIndex < _ctx.settings.gearRatios.Length - 1)
                StartShift(_gearIndex + 1);
        }

        private void ShiftDown()
        {
            int target = _gearIndex - 1;

            if (target == 0)
            {
                if (_ctx.rb.velocity.magnitude > 0.5f) { StartShift(1); return; } // moving: go to N
                if (!_ctx.settings.automatic) { SetReverseInstant(); return; }     // manual @ stop: instant R
            }

            StartShift(target);
        }

        private void StartShift(int newIndex)
        {
            newIndex = Mathf.Clamp(newIndex, 0, _ctx.settings.gearRatios.Length - 1);
            if (newIndex == _gearIndex) return;

            // Manual + reverse edge at low speed → instant latch
            bool manual = !_ctx.settings.automatic;
            bool reverseEdge = (newIndex == 0 || _gearIndex == 0);
            if (manual && reverseEdge && _ctx.rb.velocity.magnitude < 1.0f)
            {
                if (newIndex == 0) { SetReverseInstant(); }
                else
                {
                    int old = _gearIndex;
                    _gearIndex = newIndex; _isShifting = false; _shiftTimer = 0f; _clutch = 1f;
                    _torqueCutUntil = Time.time; _lastShiftTime = Time.time; _postShiftFromGear = old;
                    OnGearChanged?.Invoke(old, newIndex);
                }
                return;
            }

            int oldG = _gearIndex;
            _gearIndex = newIndex;
            _isShifting = true;
            _shiftTimer = 0f;
            _lastShiftTime = Time.time;
            _lastShiftWasUpshift = (newIndex > oldG);
            _postShiftFromGear = oldG;
            float extraCut = (_lastShiftWasUpshift ? 0.08f : 0.00f); // 80 ms tail on upshift only
            _torqueCutUntil = Time.time + _ctx.settings.shiftTime + extraCut;
            _launchState = LaunchState.Cooldown;
            _launchCooldownUntil = Time.time + LAUNCH_COOLDOWN;
            OnGearChanged?.Invoke(oldG, newIndex);
            _gearDwellUntil = Time.time + GEAR_DWELL_TIME;
        }

        public void SetReverseInstant()
        {
            int old = _gearIndex;
            _gearIndex = 0;
            _isShifting = false;
            _shiftTimer = 0f;
            _clutch = 1f;

            _torqueCutUntil = Time.time;      // no torque cut hangover
            _lastShiftTime = Time.time;
            _postShiftFromGear = old;

            OnGearChanged?.Invoke(old, 0);
        }

        public void SetFirstInstant()
        {
            int old = _gearIndex;
            _gearIndex = 2; // 1st
            _isShifting = false;
            _shiftTimer = 0f;
            _clutch = 1f;

            _torqueCutUntil = Time.time;
            _lastShiftTime = Time.time;
            _postShiftFromGear = old;

            OnGearChanged?.Invoke(old, 2);
        }
    }
}

using UnityEngine;

namespace Vehicle
{
    /// <summary>
    /// Legacy Input Manager provider that auto-detects trigger layout (combined or separate),
    /// supports keyboard fallback, and adds D-Pad Up/Down shifting (axis or button).
    /// </summary>
    public class StandardInputProvider : IInputProvider
    {
        // Axis/button name candidates (first existing is used)
        static readonly string[] AXIS_TRIGGERS = { "Triggers", "Trigger", "Axis 3", "Axis 9" };
        static readonly string[] AXIS_RT = { "ControllerAccelerate", "RT", "RightTrigger", "R2", "TriggerR", "Axis 10", "9th axis", "Axis 5" };
        static readonly string[] AXIS_LT = { "ControllerBrake", "LT", "LeftTrigger", "L2", "TriggerL", "Axis 9", "8th axis", "Axis 4" };
        static readonly string[] AXIS_STEER = { "JoystickHorizontal", "LeftStickX", "Horizontal", "Axis 1" };
        static readonly string[] AXIS_VERT_KB = { "Vertical" };

        // DPAD (vertical) axis names vary by platform/driver
        static readonly string[] AXIS_DPAD_V = { "DPadY", "DPadVertical", "D-Pad Y", "Axis 7", "7th axis", "Axis 6" };

        // Optional: if you mapped D-Pad to buttons in the Input Manager
        static readonly string[] BTN_DPAD_UP = { "DPadUp", "DPAD UP", "D-Pad Up" };
        static readonly string[] BTN_DPAD_DOWN = { "DPadDown", "DPAD DOWN", "D-Pad Down" };

        static readonly string[] BTN_HANDBRAKE = { "Handbrake" };

        const float DZ = 0.07f;     // stick/trigger deadzone
        const float HAT_THR = 0.5f; // D-Pad axis threshold

        // Detection cache
        bool _useCombined;
        string _axisCombined;
        string _axisRT;
        string _axisLT;
        string _axisSteer;
        string _axisVertKB;
        string _axisDpadV;

        // Debug values
        private float _lastRawRT = 0f;
        private float _lastRawLT = 0f;

        // D-Pad edge detection state
        private bool _prevDpadUp;
        private bool _prevDpadDown;
        private bool _shiftUpPulse;    // latched for one frame
        private bool _shiftDownPulse;  // latched for one frame

        public static bool debugInput = false;

        [SerializeField] private bool triggersAreZeroToNegOne = true;

        public StandardInputProvider()
        {
            TryPickAxis(AXIS_STEER, out _axisSteer);
            TryPickAxis(AXIS_VERT_KB, out _axisVertKB);
            TryPickAxis(AXIS_DPAD_V, out _axisDpadV);

            // pick separate first
            TryPickAxis(AXIS_RT, out _axisRT);
            TryPickAxis(AXIS_LT, out _axisLT);

            // only if neither separate exists, fall back to combined
            if (string.IsNullOrEmpty(_axisRT) && string.IsNullOrEmpty(_axisLT))
                TryPickAxis(AXIS_TRIGGERS, out _axisCombined);
            else
                _axisCombined = null;

            if (string.IsNullOrEmpty(_axisRT) && string.IsNullOrEmpty(_axisLT) && string.IsNullOrEmpty(_axisCombined))
                Debug.LogWarning("[Input] No trigger axes found. Using keyboard only (W/S).");

            if (string.IsNullOrEmpty(_axisDpadV))
                Debug.Log("[Input] No D-Pad vertical axis found; will try button fallback if defined.");
        }

        // -------- IInputProvider --------

        public float Horizontal
        {
            get
            {
                float pad = ReadAxisRaw(_axisSteer);
                float kb = Input.GetAxisRaw("Horizontal");
                float v = Mathf.Abs(pad) >= Mathf.Abs(kb) ? pad : kb;
                return DeadzoneSigned(v);
            }
        }

        public bool Handbrake =>
            GetButtonAny(BTN_HANDBRAKE) || Input.GetKey(KeyCode.Space);

        public bool RequestShiftUp
        {
            get
            {
                bool fromKeyboard = Input.GetKeyDown(KeyCode.E);
                bool fromButtons = GetButtonDownAny(BTN_DPAD_UP);
                bool fromAxis = ConsumePulse(ref _shiftUpPulse);
                return fromKeyboard || fromButtons || fromAxis;
            }
        }

        public bool RequestShiftDown
        {
            get
            {
                bool fromKeyboard = Input.GetKeyDown(KeyCode.Q);
                bool fromButtons = GetButtonDownAny(BTN_DPAD_DOWN);
                bool fromAxis = ConsumePulse(ref _shiftDownPulse);
                return fromKeyboard || fromButtons || fromAxis;
            }
        }

        public void Update()
        {
            // Latch D-Pad pulses (axis path)
            if (!string.IsNullOrEmpty(_axisDpadV))
            {
                float v = ReadAxisRaw(_axisDpadV);
                bool upNow = v > HAT_THR;
                bool dnNow = v < -HAT_THR;

                if (upNow && !_prevDpadUp) _shiftUpPulse = true;
                if (dnNow && !_prevDpadDown) _shiftDownPulse = true;

                _prevDpadUp = upNow;
                _prevDpadDown = dnNow;
            }

            // Also latch pulses via button fallback (if you mapped them)
            if (GetButtonDownAny(BTN_DPAD_UP)) _shiftUpPulse = true;
            if (GetButtonDownAny(BTN_DPAD_DOWN)) _shiftDownPulse = true;
        }

        public float Throttle
        {
            get
            {
                ReadTriggers(out float rt01, out _);
                float kb = Mathf.Max(0f, SafeGetAxisRaw(_axisVertKB)); // W/Up
                float final = Mathf.Clamp01(Mathf.Max(rt01, Deadzone01(kb)));

                if (debugInput)
                    Debug.Log($"[Throttle] rawRT={_lastRawRT:0.00} mappedRT={rt01:0.00} kb={kb:0.00} -> final={final:0.00}");

                return final;
            }
        }

        public float Brake
        {
            get
            {
                ReadTriggers(out _, out float lt01);
                float kb = Mathf.Max(0f, -SafeGetAxisRaw(_axisVertKB)); // S/Down
                float final = Mathf.Clamp01(Mathf.Max(lt01, Deadzone01(kb)));

                if (debugInput)
                    Debug.Log($"[Brake] rawLT={_lastRawLT:0.00} mappedLT={lt01:0.00} kb={kb:0.00} -> final={final:0.00}");

                return final;
            }
        }

        // Map separate trigger axis to 0..1
        float MapSeparateTriggerTo01(float v)
        {
            if (triggersAreZeroToNegOne)
            {
                // 0 at rest → -1 pressed  ==> map 0..-1 to 0..1
                // examples:  0 -> 0,  -0.5 -> 0.5,  -1 -> 1
                return Mathf.Clamp01(-v);
            }
            // legacy: handles 0..1 already, or -1..1 with -1 at rest
            return (v < 0f) ? Mathf.Clamp01((v + 1f) * 0.5f) : Mathf.Clamp01(v);
        }

        // -------- Triggers --------
        void ReadTriggers(out float rt01, out float lt01)
        {
            bool haveRT = !string.IsNullOrEmpty(_axisRT);
            bool haveLT = !string.IsNullOrEmpty(_axisLT);
            bool useCombined = (!haveRT && !haveLT) && !string.IsNullOrEmpty(_axisCombined);

            if (useCombined)
            {
                float comb = ReadAxisRaw(_axisCombined); // RT = +, LT = -
                _lastRawRT = comb; _lastRawLT = comb;
                rt01 = Deadzone01(Mathf.Max(0f, comb));
                lt01 = Deadzone01(Mathf.Max(0f, -comb));
            }
            else
            {
                float rtSep = ReadAxisRaw(_axisRT);
                float ltSep = ReadAxisRaw(_axisLT);
                _lastRawRT = rtSep; _lastRawLT = ltSep;

                rt01 = Deadzone01(MapSeparateTriggerTo01(rtSep));
                lt01 = Deadzone01(MapSeparateTriggerTo01(ltSep));
            }
        }

        // -------- Helpers --------
        static float Deadzone01(float x)
        {
            x = Mathf.Clamp01(x);
            return x <= DZ ? 0f : (x - DZ) / (1f - DZ);
        }

        static float DeadzoneSigned(float x) =>
            Mathf.Abs(x) < DZ ? 0f : x;

        static bool TryPickAxis(string[] candidates, out string chosen)
        {
            foreach (var name in candidates)
            {
                if (AxisDefined(name))
                {
                    chosen = name;
                    return true;
                }
            }
            chosen = null;
            return false;
        }

        static bool AxisDefined(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            try { Input.GetAxisRaw(name); return true; }
            catch { return false; }
        }

        static float ReadAxisRaw(string name)
        {
            if (string.IsNullOrEmpty(name)) return 0f;
            try { return Input.GetAxisRaw(name); } catch { return 0f; }
        }

        static float SafeGetAxisRaw(string name)
        {
            if (string.IsNullOrEmpty(name)) return 0f;
            try { return Input.GetAxisRaw(name); } catch { return 0f; }
        }

        static bool GetButtonAny(string[] names)
        {
            foreach (var n in names)
            {
                try { if (Input.GetButton(n)) return true; } catch { }
            }
            return false;
        }

        static bool GetButtonDownAny(string[] names)
        {
            foreach (var n in names)
            {
                try { if (Input.GetButtonDown(n)) return true; } catch { }
            }
            return false;
        }

        static bool ConsumePulse(ref bool pulse)
        {
            if (!pulse) return false;
            pulse = false;
            return true;
        }
    }
}

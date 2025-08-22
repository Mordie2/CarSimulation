using UnityEngine;

namespace Vehicle
{
    /// <summary>
    /// Legacy Input Manager provider that auto-detects whether the pad uses
    /// a combined "Triggers" axis (RT=+, LT=-, rest=0) or separate RT/LT axes.
    /// Also supports keyboard fallback (W/S, arrows) and has deadzones.
    /// </summary>
    public class StandardInputProvider : IInputProvider
    {
        // Axis/button name candidates (first existing is used)
        static readonly string[] AXIS_TRIGGERS = { "Triggers", "Trigger", "Axis 3", "Axis 9" };
        static readonly string[] AXIS_RT = { "ControllerAccelerate", "RT", "RightTrigger", "R2", "TriggerR", "Axis 10", "9th axis", "Axis 5" };
        static readonly string[] AXIS_LT = { "ControllerBrake", "LT", "LeftTrigger", "L2", "TriggerL", "Axis 9", "8th axis", "Axis 4" };
        static readonly string[] AXIS_STEER = { "JoystickHorizontal", "LeftStickX", "Horizontal", "Axis 1" };
        static readonly string[] AXIS_VERT_KB = { "Vertical" };
        static readonly string[] BTN_HANDBRAKE = { "Handbrake" };

        const float DZ = 0.07f;



        // Detection cache
        bool _useCombined;
        string _axisCombined;
        string _axisRT;
        string _axisLT;
        string _axisSteer;
        string _axisVertKB;

        // Debug values
        private float _lastRawRT = 0f;
        private float _lastRawLT = 0f;

        public static bool debugInput = true; // toggle this on/off

        [SerializeField] private bool triggersAreZeroToNegOne = true;

        public StandardInputProvider()
        {
            TryPickAxis(AXIS_STEER, out _axisSteer);
            TryPickAxis(AXIS_VERT_KB, out _axisVertKB);

            if (!_useCombined && string.IsNullOrEmpty(_axisRT) && string.IsNullOrEmpty(_axisLT))
                Debug.LogWarning("[Input] No trigger axes found. Using keyboard only (W/S).");

            // pick separate first
            TryPickAxis(AXIS_RT, out _axisRT);
            TryPickAxis(AXIS_LT, out _axisLT);

            // only if neither separate exists, fall back to combined
            if (string.IsNullOrEmpty(_axisRT) && string.IsNullOrEmpty(_axisLT))
                TryPickAxis(AXIS_TRIGGERS, out _axisCombined);
            else
                _axisCombined = null; // disable combined if we have separate

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

        public bool RequestShiftUp => Input.GetKeyDown(KeyCode.E);
        public bool RequestShiftDown => Input.GetKeyDown(KeyCode.Q);

        public void Update() { }

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

        // Deadzones
        static float Deadzone01(float x, float dz = 0.07f) => x <= dz ? 0f : (x - dz) / (1f - dz);
        static float DeadzoneSigned(float x, float dz = 0.07f) => Mathf.Abs(x) < dz ? 0f : x;

        // -------- Triggers --------
        void ReadTriggers(out float rt01, out float lt01)
        {
            // Prefer separate axes if they exist; only use combined if neither is found
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
                float rtSep = ReadAxisRaw(_axisRT);      // your case: 0..-1
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
    }
}

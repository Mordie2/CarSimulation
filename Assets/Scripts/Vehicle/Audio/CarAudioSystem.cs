using UnityEngine;
using FMODUnity;
using FMOD.Studio;

namespace Vehicle
{
    public class CarAudioSystem : System.IDisposable
    {
        private readonly VehicleContext _ctx;

        // Engine & wind
        private EventInstance _engineInstance;
        private EventInstance _windInstance;
        private bool _engineStarted;

        private float _rpmSmoothed = 0f;
        private float _windSpeed01Smoothed = 0f;

        // Events
        private EventInstance _skidInstance;       // sideways
        private bool _skidStarted = false;

        private EventInstance _wheelspinInstance;  // forward
        private bool _wheelspinStarted = false;

        // Wheel refs
        private CarController _car;
        private readonly WheelCollider[] _wheels = new WheelCollider[4];

        // FMOD params (you swapped these)
        private const string EngineParamName = "EngineRPM";
        private const string ThrottleParamName = "Throttle";
        private const string LoadParamName = "load";
        private const string SlipParamName = "WheelSpin"; // sideways on Skid event
        private const string WheelSpinParamName = "Slip";      // forward on Wheelspin event
        private const string SpeedParam01Name = "Speed";

        // ---------- Engine & Wind ----------
        [Header("Engine & Wind")]
        [SerializeField, Range(0.01f, 0.3f)] private float rpmParamSmoothing = 0.02f;
        [SerializeField, Range(0.01f, 0.5f)] private float windParamSmoothing = 0.08f;
        [SerializeField, Range(0.25f, 4f)] private float loadCurveExponent = 1.0f;
        [SerializeField] private float loadAttackTau = 0.1f;
        [SerializeField] private float loadReleaseTau = 0.6f;

        private float _loadLP1 = 0f, _loadLP2 = 0f, _load01Smoothed = 0f;

        [SerializeField] private float speedFullKmh = 150f; // Speed=1 at this km/h (for Speed param)
        private float windStartAtKmh = 15f, windFullAtKmh = 160f;

        // ---------- Sideways slip (CarEffects-style) ----------
        [Header("Sideways Slip")]
        [SerializeField] private float slipThreshold = 0.08f;  // |sidewaysSlip| threshold
        [SerializeField] private float sideParamCeil = 0.70f;  // maps |sidewaysSlip| to 0..1
        [SerializeField] private float minKmhForSide = 10f;     // avoid parking-lot squeal
        [SerializeField] private float minContactForceN = 250f;

        // ---------- Forward spin (accel) ----------
        [Header("Forward Accel Spin (RPM Δ / expected)")]
        [SerializeField, Range(0.1f, 30f)] private float fwdRatioCap = 15f;    // clamp spikes
        [SerializeField, Range(0.05f, 5f)] private float fwdDeltaK = 0.9f;  // soft-saturation knee
        [SerializeField, Range(0.3f, 1.5f)] private float fwdGamma = 1f; // shaping

        [SerializeField] private float minKmhForFwd = 10f;   // ignore crawl noise
        [SerializeField] private float minExpectedRpm = 4f;   // ignore near-standstill math noise
        [SerializeField] private float driveTorqueMinNm = 15f; // must be driven (positive torque)

        // Hysteresis for forward event (works for accel or lock)
        [SerializeField, Range(0f, 1f)] private float fwdStartAt = 0.10f;
        [SerializeField, Range(0f, 1f)] private float fwdStopAt = 0.06f;

        // ---------- Braking lock-up ----------
        [Header("Braking Lock-up")]
        [SerializeField] private float brakeTorqueMinNm = 30f;  // braking detection per-wheel
        [SerializeField] private float lockRpmEps = 8f;   // |rpm| <= eps => likely locked
        [SerializeField] private float lockMinKmh = 6f;   // ignore near stop
        [SerializeField] private float lockFullAtKmh = 60f; // => lockParam = 1 at this speed

        // ---------- Engine start SFX ----------
        [Header("Engine Start SFX")]
        [SerializeField] private float engineOffRpmThresh = 150f;   // below this we consider the engine "off"
        [SerializeField] private float engineOffHoldS = 0.25f;   // must stay below thresh this long
        [SerializeField] private float startSfxCooldownS = 1.0f;    // avoid retriggers


        // --- Brake SFX ---
        private EventInstance _brakeInstance;
        private bool _brakeStarted = false;
        private const string BrakeParamName = "Brake";

        [Header("Brake SFX")]
        [SerializeField] private float minKmhForBrake = 35f;         // no brake noise when creeping
        [SerializeField] private float brakeTorqueFullNm = 3000f;    // ~torque that should map to Brake=1
        [SerializeField] private float brakeAttackTau = 0.05f;       // how fast the param rises
        [SerializeField] private float brakeReleaseTau = 0.18f;      // how fast it falls
        [SerializeField, Range(0f, 1f)] private float brakeStartAt = 0.08f; // loop on
        [SerializeField, Range(0f, 1f)] private float brakeStopAt = 0.05f; // loop off

        private float _brakeLP = 0f; // smoothed Brake (0..1)

        // Pops
        private EventInstance _popsInstance;
        private bool _popsPlaying = false;

        [Header("Exhaust Pops")]
        [SerializeField] private float popsMinRpm = 5000f;           // only above this RPM
        [SerializeField, Range(0f, 1f)] private float popsLiftFrom = 0.8f;   // throttle was above this
        [SerializeField, Range(0f, 1f)] private float popsLiftTo = 0.01f;  // lifted to this or below
        [SerializeField, Range(0f, 1f)] private float popsMinDrop = 0.25f;  // delta required this frame
        [SerializeField][Range(0f, 1f)] private float popsChance = 1.0f;   // pop chance (lower = more)
        [SerializeField] private float popsCooldownS = 0.15f;               // anti-spam
        [SerializeField, Range(0f, 1f)] private float popsCancelThrottle = 0.05f; // cancel if throttle > this
        [SerializeField] private float popsArmWindowS = 0.25f;          // time window after “high” to accept a lift
        [SerializeField] private float popsDelayS = 0.05f;              // base delay before the one-shot
        [SerializeField] private Vector2 popsDelayJitterS = new Vector2(0f, 0.03f); 
        [SerializeField] private float popsEdgeTau = 0.03f;             // throttle smoothing for edge detection
                                                                        // Peak throttle while "high" for lift detection
        private float _peakThrLP = 0f;

        [SerializeField, Range(0f, 1f)] private float popsCancelRise = 0.12f; // how much throttle must rise (from arm) to cancel
        private float _thrAtArm = 0f;   // throttle value when we armed



        private float _prevThrottle = 0f;     // raw (kept if you need it elsewhere)
        private float _popsCooldown = 0f;

        private float _thrLP = 0f;            // smoothed throttle
        private float _prevThrLP = 0f;

        private float _armTimer = 0f;         // how long since we were last “high”
        private bool _delayArmed = false;     // waiting to fire after a qualifying lift
        private float _delayTimer = 0f;


        // Handbrake one-shot (edge trigger)
        private bool _handbrakePrev = false;




        // Debug
        [Header("Debug")]
        [SerializeField] private bool enableSlipParamDebug = false;
        [SerializeField, Range(1, 60)] private int slipParamDebugHz = 10;
        private float _slipDbgTimer = 0f;

        private static readonly string[] _wheelLabel = { "FL", "FR", "RL", "RR" };

        public CarAudioSystem(VehicleContext ctx)
        {
            _ctx = ctx;

            if (FMODEvents.instance == null) { Debug.LogError("[CarAudio] FMODEvents.instance is null"); return; }
            if (AudioManager.instance == null) { Debug.LogError("[CarAudio] AudioManager.instance is null"); return; }

            EnsureEngineInstance(); if (_engineInstance.isValid()) { _engineInstance.start(); _engineStarted = true; }
            EnsureWindInstance(); if (_windInstance.isValid()) { _windInstance.start(); }
            EnsureSkidInstance();
            EnsureWheelspinInstance();
            EnsureBrakeInstance();  
            EnsureWheels();


        }

        public void OnUpdate()
        {
            var pos = _ctx.host.transform.position;
            if (_engineInstance.isValid()) _engineInstance.set3DAttributes(RuntimeUtils.To3DAttributes(pos));
            if (_windInstance.isValid()) _windInstance.set3DAttributes(RuntimeUtils.To3DAttributes(pos));
            if (_skidInstance.isValid()) _skidInstance.set3DAttributes(RuntimeUtils.To3DAttributes(pos));
            if (_wheelspinInstance.isValid()) _wheelspinInstance.set3DAttributes(RuntimeUtils.To3DAttributes(pos));
            if (_brakeInstance.isValid()) _brakeInstance.set3DAttributes(RuntimeUtils.To3DAttributes(pos));
            if (_popsInstance.isValid()) _popsInstance.set3DAttributes(RuntimeUtils.To3DAttributes(pos));

            var car = _ctx.host as CarController;
            if (car != null)
            {
                bool hbNow = car.handbrakeInput;   // comes from IInputProvider.Handbrake
                if (hbNow && !_handbrakePrev)      // rising edge
                {
                    if (FMODEvents.instance != null && AudioManager.instance != null)
                        AudioManager.instance.PlayOneShot(FMODEvents.instance.Handbrake, pos);
                }
                _handbrakePrev = hbNow;
            }
        }

        public void OnFixedUpdate(float engineRPM, float throttle01)
        {
            float dt = Time.fixedDeltaTime;

            // -------- Engine params --------
            if (_engineStarted && _engineInstance.isValid())
            {
                float rpmClamped = Mathf.Clamp(engineRPM, 0f, _ctx.settings.revLimiterRPM);
                _rpmSmoothed = Mathf.Lerp(_rpmSmoothed, rpmClamped, 1f - Mathf.Exp(-dt / rpmParamSmoothing));
                AudioManager.instance.SetInstanceParameter(_engineInstance, EngineParamName, _rpmSmoothed);

                float throttleClamped = Mathf.Clamp01(throttle01);
                AudioManager.instance.SetInstanceParameter(_engineInstance, ThrottleParamName, throttleClamped);

                float loadTarget = Mathf.Approximately(loadCurveExponent, 1f) ? throttleClamped : Mathf.Pow(throttleClamped, loadCurveExponent);
                float aAtk = 1f - Mathf.Exp(-dt / Mathf.Max(0.0001f, loadAttackTau));
                float aRel = 1f - Mathf.Exp(-dt / Mathf.Max(0.0001f, loadReleaseTau));
                float a = (loadTarget > _loadLP1) ? aAtk : aRel;
                _loadLP1 += (loadTarget - _loadLP1) * a;
                _loadLP2 += (_loadLP1 - _loadLP2) * a;
                _load01Smoothed = Mathf.Clamp01(_loadLP2);
                AudioManager.instance.SetInstanceParameter(_engineInstance, LoadParamName, _load01Smoothed);
            }

            // -------- Speed helpers --------
            float kmhAbs = Mathf.Abs(_ctx.KmH);
            float mpsAbs = kmhAbs / 3.6f;
            float speed01 = Mathf.Clamp01(kmhAbs / Mathf.Max(1f, speedFullKmh));

            // -------- Sideways slip --------
            float maxSideMag = 0f;
            for (int i = 0; i < 4; i++)
            {
                var w = _wheels[i];
                if (w == null) continue;
                if (w.GetGroundHit(out WheelHit hit) && hit.force >= minContactForceN)
                {
                    float sideMag = Mathf.Abs(hit.sidewaysSlip);
                    if (sideMag > maxSideMag) maxSideMag = sideMag;
                }
            }
            bool sideOkSpeed = kmhAbs >= minKmhForSide;
            bool sideAbove = maxSideMag > slipThreshold;

            float sideDenom = Mathf.Max(0.0001f, sideParamCeil - slipThreshold);
            float sideParam = sideAbove ? Mathf.Clamp01((maxSideMag - slipThreshold) / sideDenom) : 0f;

            if (_skidInstance.isValid())
            {
                if (!_skidStarted) { if (sideOkSpeed && sideAbove) { _skidInstance.start(); _skidStarted = true; } }
                else { if (!sideOkSpeed || !sideAbove) { _skidInstance.stop(FMOD.Studio.STOP_MODE.ALLOWFADEOUT); _skidStarted = false; } }

                AudioManager.instance.SetInstanceParameter(_skidInstance, SlipParamName, sideParam);
                AudioManager.instance.SetInstanceParameter(_skidInstance, SpeedParam01Name, speed01);
            }

            // -------- Forward param = max(AccelSpin, LockUp) --------
            // Accel spin (driven wheels only)
            float maxAccelRatio = 0f;
            int accelIdx = -1;
            float accelExp = 0f, accelAct = 0f;

            // Lock-up (braking + rpm≈0)
            float lockParam = 0f; // speed-mapped
            int lockIdx = -1;

            for (int i = 0; i < 4; i++)
            {
                var w = _wheels[i];
                if (w == null) continue;

                if (w.GetGroundHit(out WheelHit hit) && hit.force >= minContactForceN)
                {
                    float radius = Mathf.Max(0.01f, w.radius);
                    float expRPM = (mpsAbs / (2f * Mathf.PI * radius)) * 60f;

                    // ----- Lock-up path -----
                    bool braking = w.brakeTorque >= brakeTorqueMinNm;
                    if (braking && kmhAbs >= lockMinKmh && expRPM >= minExpectedRpm && Mathf.Abs(w.rpm) <= lockRpmEps)
                    {
                        float lp = Mathf.Clamp01(kmhAbs / Mathf.Max(1f, lockFullAtKmh)); // 1.0 at 140 km/h (default)
                        if (lp > lockParam) { lockParam = lp; lockIdx = i; }
                    }

                    // ----- Accel spin path (driven wheels only, positive motor torque, not braking)
                    bool driven = w.motorTorque > driveTorqueMinNm;
                    if (driven && !braking && expRPM >= minExpectedRpm && kmhAbs >= minKmhForFwd)
                    {
                        float actRPM = Mathf.Abs(w.rpm);
                        float ratio = Mathf.Abs(actRPM - expRPM) / expRPM; // 0 = perfect rolling
                        if (ratio > maxAccelRatio) { maxAccelRatio = ratio; accelIdx = i; accelExp = expRPM; accelAct = actRPM; }
                    }
                }
            }

            // Map accel ratio → 0..1
            float r = Mathf.Min(maxAccelRatio, fwdRatioCap);
            float compressed = r / (r + Mathf.Max(0.0001f, fwdDeltaK));
            float accelParam = Mathf.Clamp01(Mathf.Pow(compressed, fwdGamma));
            float fwdParam = Mathf.Max(accelParam, lockParam);

            // Start/stop w/ hysteresis so it never lingers when neither path is active
            bool startCond = fwdParam >= fwdStartAt;
            bool stopCond = fwdParam <= fwdStopAt;

            if (_wheelspinInstance.isValid())
            {
                if (!_wheelspinStarted) { if (startCond) { _wheelspinInstance.start(); _wheelspinStarted = true; } }
                else { if (stopCond) { _wheelspinInstance.stop(FMOD.Studio.STOP_MODE.ALLOWFADEOUT); _wheelspinStarted = false; } }

                AudioManager.instance.SetInstanceParameter(_wheelspinInstance, WheelSpinParamName, fwdParam);
                AudioManager.instance.SetInstanceParameter(_wheelspinInstance, SpeedParam01Name, speed01);
            }

            // -------- Brake SFX --------
            if (_brakeInstance.isValid())
            {
                float brakeTarget01 = 0f;
                bool anyLocked = false;

                for (int i = 0; i < 4; i++)
                {
                    var w = _wheels[i];
                    if (w == null) continue;

                    if (w.GetGroundHit(out WheelHit hit) && hit.force >= minContactForceN)
                    {
                        // aggregate brake pressure (use max so one hard-braking wheel is enough)
                        float n = Mathf.Clamp01(w.brakeTorque / Mathf.Max(1f, brakeTorqueFullNm));
                        if (n > brakeTarget01) brakeTarget01 = n;

                        // lock detection (same criteria you use in the forward/lock section)
                        float radius = Mathf.Max(0.01f, w.radius);
                        float expRPM = (mpsAbs / (2f * Mathf.PI * radius)) * 60f;
                        bool brakingWheel = w.brakeTorque >= brakeTorqueMinNm;

                        if (brakingWheel &&
                            kmhAbs >= lockMinKmh &&
                            expRPM >= minExpectedRpm &&
                            Mathf.Abs(w.rpm) <= lockRpmEps)
                        {
                            anyLocked = true;
                        }
                    }
                }

                // speed gate (no squeal when creeping)
                if (kmhAbs < minKmhForBrake) brakeTarget01 = 0f;

                // if any wheel is locked, don't play Brake loop
                if (anyLocked)
                {
                    brakeTarget01 = 0f;

                    if (_brakeStarted)
                    {
                        _brakeInstance.stop(FMOD.Studio.STOP_MODE.ALLOWFADEOUT);
                        _brakeStarted = false;
                    }
                    _brakeLP = 0f; // snap param down so it won't re-trigger this frame
                }

                // Smooth with separate attack/release
                float tau = (brakeTarget01 > _brakeLP) ? Mathf.Max(0.0001f, brakeAttackTau)
                                                       : Mathf.Max(0.0001f, brakeReleaseTau);
                float a = 1f - Mathf.Exp(-dt / tau);
                _brakeLP += (brakeTarget01 - _brakeLP) * a;
                _brakeLP = Mathf.Clamp01(_brakeLP);

                // Start/stop the loop with hysteresis
                if (!_brakeStarted)
                {
                    if (_brakeLP >= brakeStartAt) { _brakeInstance.start(); _brakeStarted = true; }
                }
                else
                {
                    if (_brakeLP <= brakeStopAt) { _brakeInstance.stop(FMOD.Studio.STOP_MODE.ALLOWFADEOUT); _brakeStarted = false; }
                }

                AudioManager.instance.SetInstanceParameter(_brakeInstance, BrakeParamName, _brakeLP);
            }

            UpdatePops(engineRPM, throttle01, dt);


            // -------- Debug --------
            if (enableSlipParamDebug && slipParamDebugHz > 0)
            {
                _slipDbgTimer += dt;
                float interval = 1f / Mathf.Max(1, slipParamDebugHz);
                if (_slipDbgTimer >= interval)
                {
                    _slipDbgTimer = 0f;
                    string aWheel = (accelIdx >= 0) ? _wheelLabel[accelIdx] : "--";
                    string lWheel = (lockIdx >= 0) ? _wheelLabel[lockIdx] : "--";
                    Debug.Log(
                        $"[CarAudio][FMOD] v={kmhAbs:F1} km/h | " +
                        $"ACCEL ratio={maxAccelRatio:F3}→{accelParam:F2} (wheel={aWheel} exp={accelExp:F0}rpm act={accelAct:F0}rpm) | " +
                        $"LOCK={lockParam:F2} (wheel={lWheel}) | FWD={fwdParam:F2} (on={_wheelspinStarted}) | " +
                        $"SIDE |slip|={maxSideMag:F2}→{sideParam:F2} (on={_skidStarted})"
                    );
                }
            }

            // -------- Wind --------
            if (_windInstance.isValid())
            {
                float raw01 = Mathf.Clamp01(Mathf.InverseLerp(windStartAtKmh, windFullAtKmh, _ctx.KmH));
                _windSpeed01Smoothed = Mathf.Lerp(_windSpeed01Smoothed, raw01, 1f - Mathf.Exp(-dt / windParamSmoothing));
                AudioManager.instance.SetInstanceParameter(_windInstance, SpeedParam01Name, _windSpeed01Smoothed);
            }
        }

        private void UpdatePops(float engineRPM, float throttle01, float dt)
        {
            // cooldown & playback
            _popsCooldown = Mathf.Max(0f, _popsCooldown - dt);
            if (_popsPlaying)
            {
                _popsInstance.getPlaybackState(out PLAYBACK_STATE st);
                if (st == PLAYBACK_STATE.STOPPED) _popsPlaying = false;
            }

            // smooth throttle for robust edge detection
            float a = 1f - Mathf.Exp(-dt / Mathf.Max(0.0001f, popsEdgeTau));
            _thrLP = Mathf.Lerp(_thrLP, Mathf.Clamp01(throttle01), a);

            bool rpmOK = engineRPM >= popsMinRpm;

            // maintain "armed window" and track peak while high
            if (_thrLP >= popsLiftFrom && rpmOK)
            {
                _armTimer = popsArmWindowS;
                _peakThrLP = Mathf.Max(_peakThrLP, _thrLP);
            }
            else
            {
                _armTimer = Mathf.Max(0f, _armTimer - dt);
                if (_armTimer <= 0f) _peakThrLP = 0f;
            }

            // compute drop from the recent peak
            float dropFromPeak = Mathf.Max(0f, _peakThrLP - _thrLP);
            bool bigDrop = dropFromPeak >= popsMinDrop;
            bool nowLow = _thrLP <= popsLiftTo;           // "complete lift"

            // ---- ARM only on complete lift + big drop (deterministic; fixed delay) ----
            if (!_delayArmed && _popsCooldown <= 0f && rpmOK && _armTimer > 0f && bigDrop && nowLow)
            {
                _delayArmed = true;
                _delayTimer = popsDelayS;
                _thrAtArm = _thrLP;    // remember throttle at the moment we armed
                _armTimer = 0f;
                _peakThrLP = 0f;
                // Debug.Log($"[Pops] Armed (dropFromPeak={dropFromPeak:F2}, thrLP={_thrLP:F2})");
            }

            // ---- DELAY / CANCEL / FIRE ----
            if (_delayArmed)
            {
                // cancel if throttle climbed back up significantly from when we armed, or RPM fell
                if ((_thrLP >= _thrAtArm + popsCancelRise) || !rpmOK)
                {
                    _delayArmed = false;
                    // Debug.Log("[Pops] Cancelled");
                }
                else
                {
                    _delayTimer -= dt;
                    if (_delayTimer <= 0f)
                    {
                        EnsurePopsInstance();
                        if (_popsInstance.isValid())
                        {
                            _popsInstance.set3DAttributes(RuntimeUtils.To3DAttributes(_ctx.host.transform.position));
                            if (_popsPlaying) _popsInstance.stop(FMOD.Studio.STOP_MODE.IMMEDIATE); // hard retrigger
                            _popsInstance.start();
                            _popsPlaying = true;
                        }
                        _delayArmed = false;
                        _popsCooldown = popsCooldownS;
                        // Debug.Log("[Pops] Fired");
                    }
                }
            }

            _prevThrLP = _thrLP;
            _prevThrottle = throttle01;
        }







        public void Dispose()
        {
            if (_engineInstance.isValid()) _engineInstance.stop(FMOD.Studio.STOP_MODE.IMMEDIATE);
            if (_windInstance.isValid()) _windInstance.stop(FMOD.Studio.STOP_MODE.IMMEDIATE);
            if (_skidInstance.isValid()) _skidInstance.stop(FMOD.Studio.STOP_MODE.IMMEDIATE);
            if (_wheelspinInstance.isValid()) _wheelspinInstance.stop(FMOD.Studio.STOP_MODE.IMMEDIATE);
            if (_brakeInstance.isValid()) _brakeInstance.stop(FMOD.Studio.STOP_MODE.IMMEDIATE);
            if (_popsInstance.isValid()) _popsInstance.stop(FMOD.Studio.STOP_MODE.IMMEDIATE);


        }

        // ---------- Helpers ----------
        private void EnsureEngineInstance()
        {
            if (!_engineInstance.isValid())
            {
                _engineInstance = AudioManager.instance.GetOrCreateInstance(
                    key: $"engine_{_ctx.host.GetInstanceID()}",
                    eventRef: FMODEvents.instance.Engine,
                    position: _ctx.host.transform.position
                );
            }
        }

        private void EnsureWindInstance()
        {
            if (!_windInstance.isValid())
            {
                _windInstance = AudioManager.instance.GetOrCreateInstance(
                    key: $"wind_{_ctx.host.GetInstanceID()}",
                    eventRef: FMODEvents.instance.Wind,
                    position: _ctx.host.transform.position
                );
            }
        }

        private void EnsureSkidInstance()
        {
            if (_skidInstance.isValid()) return;
            _skidInstance = AudioManager.instance.GetOrCreateInstance(
                key: $"skid_{_ctx.host.GetInstanceID()}",
                eventRef: FMODEvents.instance.Skid,
                position: _ctx.host.transform.position
            );
            if (_skidInstance.isValid())
                AudioManager.instance.SetInstanceParameter(_skidInstance, SlipParamName, 0f);
        }

        private void EnsureWheelspinInstance()
        {
            if (_wheelspinInstance.isValid()) return;
            _wheelspinInstance = AudioManager.instance.GetOrCreateInstance(
                key: $"wheelspin_{_ctx.host.GetInstanceID()}",
                eventRef: FMODEvents.instance.WheelSpin,
                position: _ctx.host.transform.position
            );
            if (_wheelspinInstance.isValid())
                AudioManager.instance.SetInstanceParameter(_wheelspinInstance, WheelSpinParamName, 0f);
        }

        private void EnsureWheels()
        {
            if (_car == null) _car = _ctx.host.GetComponent<CarController>();
            if (_car != null)
            {
                _wheels[0] = _car.frontLWheelCollider;
                _wheels[1] = _car.frontRWheelCollider;
                _wheels[2] = _car.rearLWheelCollider;
                _wheels[3] = _car.rearRWheelCollider;
            }
        }
        private void EnsureBrakeInstance()
        {
            if (_brakeInstance.isValid()) return;
            _brakeInstance = AudioManager.instance.GetOrCreateInstance(
                key: $"brake_{_ctx.host.GetInstanceID()}",
                eventRef: FMODEvents.instance.Brake,
                position: _ctx.host.transform.position
            );
            if (_brakeInstance.isValid())
                AudioManager.instance.SetInstanceParameter(_brakeInstance, BrakeParamName, 0f);
        }

        private void EnsurePopsInstance()
        {
            if (_popsInstance.isValid()) return;
            _popsInstance = AudioManager.instance.GetOrCreateInstance(
                key: $"pops_{_ctx.host.GetInstanceID()}",
                eventRef: FMODEvents.instance.Pops,
                position: _ctx.host.transform.position
            );
        }





        // >>> braking check shared by forward logic
        private bool IsBrakingNow(float carKmhAbs)
        {
            if (carKmhAbs < minKmhForFwd) return false;
            for (int i = 0; i < 4; i++)
            {
                var w = _wheels[i];
                if (w == null) continue;
                if (w.GetGroundHit(out WheelHit hit))
                {
                    if (hit.force < minContactForceN) continue;
                    if (w.brakeTorque >= brakeTorqueMinNm) return true;
                }
            }
            return false;
        }

        public void OnGearChanged(int oldGear, int newGear)
        {
            if (FMODEvents.instance == null || AudioManager.instance == null) return;

            bool downshift = newGear < oldGear;
            if (!downshift && newGear > 2 && _rpmSmoothed > 4000f)
                AudioManager.instance.PlayOneShot(FMODEvents.instance.Shift, _ctx.host.transform.position);
            if (downshift && _rpmSmoothed > 2000f)
                AudioManager.instance.PlayOneShot(FMODEvents.instance.Shift, _ctx.host.transform.position);
        }

        public void NotifyEngineStart()
        {
            if (FMODEvents.instance == null || AudioManager.instance == null) return;
            AudioManager.instance.PlayOneShot(FMODEvents.instance.EngineStart, _ctx.host.transform.position);
        }

    }
}

// =============================================
// File: Scripts/Vehicle/CarController.cs
// Role: Orchestrator/Facade that wires sub-systems
// =============================================
using UnityEngine;
using Vehicle;

namespace Vehicle
{
    [RequireComponent(typeof(Rigidbody))]
    public class CarController : MonoBehaviour
    {
        [Header("Wheel Colliders")]
        public WheelCollider frontLWheelCollider, frontRWheelCollider, rearLWheelCollider, rearRWheelCollider;

        [Header("Wheel Visuals")]
        public Transform frontLWheelTransform, frontRWheelTransform, rearLWheelTransform, rearRWheelTransform;

        [Header("Settings")]
        public VehicleSettings settings; // ScriptableObject for tunables (optional, but recommended)

        // Runtime state exposed for HUD/Debug
        public float speedMs;
        public string gearLabel;
        public int engineRPMRounded;
        public bool absEnabled = true;

        // --- Compatibility properties for older scripts (CarEffects, etc.) ---
        public float EngineRPM => _drivetrain.EngineRPM;
        public int CurrentGear => _drivetrain.GearIndex;      // proxied by DrivetrainSystem
        public string GearLabel => _drivetrain.GearLabel;
        public float speed => _ctx.rb ? _ctx.rb.velocity.magnitude : 0f;

        // Old fields expected by CarEffects:
        public bool handbrakeInput => _input?.Handbrake ?? false;

        // Simple smoke trigger: slip or handbrake => true
        public bool playPauseSmoke => ComputeSmoke_Compat();

        public bool RearLeftSlip => GetSlip(_ctx.RL);
        public bool RearRightSlip => GetSlip(_ctx.RR);

        private bool GetSlip(WheelCollider wc)
        {
            if (wc.GetGroundHit(out var hit))
                return Mathf.Abs(hit.sidewaysSlip) > _ctx.settings.smokeSlipThreshold
                    || Mathf.Abs(hit.forwardSlip) > _ctx.settings.smokeSlipThreshold;
            return false;
        }

        private bool ComputeSmoke_Compat()
        {
            float th = _ctx.settings.smokeSlipThreshold;
            bool smoking = false;

            if (_ctx.RL.GetGroundHit(out var hl))
                smoking |= Mathf.Abs(hl.sidewaysSlip) > th || Mathf.Abs(hl.forwardSlip) > th;

            if (_ctx.RR.GetGroundHit(out var hr))
                smoking |= Mathf.Abs(hr.sidewaysSlip) > th || Mathf.Abs(hr.forwardSlip) > th;

            return smoking || handbrakeInput;
        }

        // Systems
        private Rigidbody _rb;
        private IInputProvider _input;
        private SteeringSystem _steering;
        private BrakeSystem _brakes;
        private DrivetrainSystem _drivetrain;
        private SuspensionAeroSystem _suspensionAero;
        private WheelPoseUpdater _wheelPose;
        private CarAudioSystem _audio;

        // Cached context passed into systems
        private VehicleContext _ctx;

        private void Awake()
        {
            _rb = GetComponent<Rigidbody>();

            // Build a context object all systems can share
            _ctx = new VehicleContext
            {
                rb = _rb,
                FL = frontLWheelCollider,
                FR = frontRWheelCollider,
                RL = rearLWheelCollider,
                RR = rearRWheelCollider,
                settings = settings != null ? settings : VehicleSettings.DefaultRuntimeCopy(),
                host = this
            };

            // Systems
            _input = new StandardInputProvider();

            _steering = new SteeringSystem(_ctx);
            _brakes = new BrakeSystem(_ctx);
            _drivetrain = new DrivetrainSystem(_ctx);  // <-- unified shift + powertrain

            _suspensionAero = new SuspensionAeroSystem(_ctx);
            _wheelPose = new WheelPoseUpdater(
                frontLWheelCollider, frontRWheelCollider, rearLWheelCollider, rearRWheelCollider,
                frontLWheelTransform, frontRWheelTransform, rearLWheelTransform, rearRWheelTransform);

            _audio = new CarAudioSystem(_ctx);
            _drivetrain.OnGearChanged += _audio.OnGearChanged;

            // Optional friction and suspension setup
            WheelTuning.ApplyDefaultFriction(frontLWheelCollider, true);
            WheelTuning.ApplyDefaultFriction(frontRWheelCollider, true);
            WheelTuning.ApplyDefaultFriction(rearLWheelCollider, false);
            WheelTuning.ApplyDefaultFriction(rearRWheelCollider, false);
        }

        private void Update()
        {
            // Non-physics inputs in Update
            _input.Update();
            _audio?.OnUpdate();

            // Toggle ABS (sample)
            if (Input.GetKeyDown(_ctx.settings.absToggleKey))
            {
                absEnabled = !absEnabled;
                _brakes.SetAbsEnabled(absEnabled);
            }
        }

        private void FixedUpdate()
        {
            speedMs = _rb.velocity.magnitude;

            // 1) Steering FIRST so wheel angles are updated every physics step
            _steering.Tick(_input.Horizontal, speedMs);

            // 2) Compute burnout once (gas + brake + near stop)
            bool burnout = (_input.Throttle > 0.9f && _input.Brake > 0.9f && speedMs < 3f);

            // 3) NEW: Shifting + Powertrain in one go (decide gear BEFORE brakes)
            _drivetrain.Tick(_input, speedMs, burnout);

            // 4) Brakes / ABS with correct reverse flag & burnout
            _brakes.TickBase(_input, _drivetrain.IsInReverse, burnout);
            _brakes.TickABS(burnout);

            // 5) Aero & anti-roll
            _suspensionAero.Tick();

            // 6) Visuals & audio
            _wheelPose.UpdateAll();
            _audio?.OnFixedUpdate(_drivetrain.EngineRPM, Mathf.Abs(_input.Throttle));

            // HUD
            gearLabel = _drivetrain.GearLabel;
            engineRPMRounded = Mathf.RoundToInt(_drivetrain.EngineRPM);
        }

        private void OnDestroy()
        {
            _audio?.Dispose();
        }
    }
}

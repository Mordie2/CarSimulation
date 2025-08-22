// =============================================
// File: Scripts/Vehicle/Brakes/BrakeSystem.cs
// Role: Service brake + handbrake + ABS modulation
// =============================================
using UnityEngine;

namespace Vehicle
{
    public class BrakeSystem
    {
        private readonly VehicleContext _ctx;
        private bool _absEnabled;

        public BrakeSystem(VehicleContext ctx)
        {
            _ctx = ctx;
            _absEnabled = _ctx.settings.absEnabledDefault;
            _ctx.host.gameObject.AddComponent<BrakeSystemMarker>(); // tag for coast brake stacking logic
        }

        public void SetAbsEnabled(bool enabled) => _absEnabled = enabled;

        public void TickBase(IInputProvider input, bool inReverse, bool isBurnout)
        {
            
            _ctx.FL.brakeTorque = 0f;
            _ctx.FR.brakeTorque = 0f;
            _ctx.RL.brakeTorque = 0f;
            _ctx.RR.brakeTorque = 0f;
            
            // 🔥 Burnout: fronts locked, rears free
            if (isBurnout)
            {
                float frontLock = _ctx.settings.brakeForce;
                _ctx.FL.brakeTorque = frontLock;
                _ctx.FR.brakeTorque = frontLock;
                _ctx.RL.brakeTorque = 0f;
                _ctx.RR.brakeTorque = 0f;
                return;
            }

            // Arcade reverse braking:
            // - Forward gears: service brake = Brake pedal
            // - Reverse gear:  service brake = Gas pedal (throttle)
            float serviceBrake01 = inReverse ? Mathf.Clamp01(input.Throttle)   // gas = brake in R
                                             : Mathf.Clamp01(input.Brake);     // brake = brake in D

            float brakeTorque = serviceBrake01 * _ctx.settings.brakeForce;
            _ctx.FL.brakeTorque = brakeTorque;
            _ctx.FR.brakeTorque = brakeTorque;
            _ctx.RL.brakeTorque = brakeTorque;
            _ctx.RR.brakeTorque = brakeTorque;

            // Handbrake adds on the rear axle
            if (input.Handbrake)
            {
                float hb = _ctx.settings.handbrakeForce;
                _ctx.RL.brakeTorque += hb;
                _ctx.RR.brakeTorque += hb;
            }
        }




        public void TickABS(bool isBurnout)
        {
            if (!_absEnabled) return;

            // During burnout we’re intentionally locking fronts; skip ABS entirely
            if (isBurnout) return;

            if (TotalBrakeDemand01() <= 0.01f) return;

            ApplyABSOnWheel(_ctx.FL);
            ApplyABSOnWheel(_ctx.FR);
            ApplyABSOnWheel(_ctx.RL);
            ApplyABSOnWheel(_ctx.RR);
        }

        // Small helpers to read the last input (since BrakeSystem doesn't hold it)
        private float InputProxyThrottle()
        {
            // Best: thread the input value in from CarController each frame.
            // Minimal: try to infer via total brake demand: not reliable.
            // If you can, add a cached last input into context.
            // For now, use UnityEngine.Input as a fallback (works with your StandardInputProvider mapping):
            float k = Input.GetAxis("Vertical");
            float gp = Mathf.Abs(Input.GetAxis("ControllerAccelerate"));
            return Mathf.Max(k, gp); // crude but serviceable for burnout detection in ABS
        }
        private float InputProxyBrake()
        {
            float k = Mathf.Abs(Mathf.Min(0f, Input.GetAxis("Vertical"))); // S/Down as brake if forward-moving
            float gp = Mathf.Abs(Input.GetAxis("ControllerBrake"));
            return Mathf.Max(k, gp);
        }


        private void ApplyABSOnWheel(WheelCollider wc)
        {
            float targetBrakeTorque = TotalBrakeDemand01() * _ctx.settings.brakeForce;
            if (!wc.GetGroundHit(out var hit)) return;

            if (targetBrakeTorque <= 0f) { wc.brakeTorque = 0f; return; }
            if (Mathf.Abs(hit.forwardSlip) > _ctx.settings.absThreshold)
            {
                // simple frame-based release/restore approximation to your coroutine logic
                wc.brakeTorque = 0f;
                // restore a bit after a short time window
                wc.brakeTorque = Mathf.MoveTowards(wc.brakeTorque, targetBrakeTorque, (targetBrakeTorque / Mathf.Max(0.01f, _ctx.settings.absReleaseTime)) * Time.fixedDeltaTime);
            }
            else
            {
                wc.brakeTorque = targetBrakeTorque;
            }
        }

        private float TotalBrakeDemand01()
        {
            // Use current per-wheel value to infer demand (front L as representative)
            return Mathf.Clamp01(_ctx.FL.brakeTorque / Mathf.Max(1f, _ctx.settings.brakeForce));
        }
    }

    // empty marker component just so other systems can detect presence
    public class BrakeSystemMarker : MonoBehaviour { }
}

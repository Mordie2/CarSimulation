// =============================================
// File: Scripts/Vehicle/Handling/SuspensionAeroSystem.cs
// Role: Anti-roll bars + aerodynamic downforce
// =============================================
using UnityEngine;
using Vehicle;

namespace Vehicle
{
    public class SuspensionAeroSystem
    {
        private readonly VehicleContext _ctx;
        public SuspensionAeroSystem(VehicleContext ctx) { _ctx = ctx; }

        public void Tick()
        {
            ApplyAntiRoll(_ctx.FL, _ctx.FR, _ctx.settings.frontAntiRoll);
            ApplyAntiRoll(_ctx.RL, _ctx.RR, _ctx.settings.rearAntiRoll);

            float v = _ctx.rb.velocity.magnitude;
            _ctx.rb.AddForce(-_ctx.host.transform.up * _ctx.settings.downforceCoef * v * v, ForceMode.Force);
        }

        private void ApplyAntiRoll(WheelCollider left, WheelCollider right, float antiRoll)
        {
            bool groundedL = left.GetGroundHit(out _);
            bool groundedR = right.GetGroundHit(out _);

            float travelL = 1f, travelR = 1f;
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
            if (groundedL) _ctx.rb.AddForceAtPosition(left.transform.up * -antiRollForce, left.transform.position);
            if (groundedR) _ctx.rb.AddForceAtPosition(right.transform.up * antiRollForce, right.transform.position);
        }
    }
}

// =============================================
// File: Scripts/Vehicle/Handling/WheelTuning.cs
// Role: One place for friction/suspension tweaks
// =============================================
using UnityEngine;

namespace Vehicle
{
    public static class WheelTuning
    {
        public static void ApplyDefaultFriction(WheelCollider c, bool isFront,
            float fLong = 1.0f, float rLong = 1.10f,
            float fLat = 1.45f, float rLat = 1.30f)
        {
            var fwd = c.forwardFriction;
            fwd.extremumSlip = 0.8f; fwd.asymptoteSlip = 1.2f; fwd.extremumValue = 1.1f; fwd.asymptoteValue = 0.85f;
            fwd.stiffness = isFront ? fLong : rLong; c.forwardFriction = fwd;

            var lat = c.sidewaysFriction;
            lat.extremumSlip = 0.4f; lat.asymptoteSlip = 1.0f; lat.extremumValue = 1.05f; lat.asymptoteValue = 0.8f;
            lat.stiffness = isFront ? fLat : rLat; c.sidewaysFriction = lat;

            var sp = c.suspensionSpring; sp.spring = 85000f; sp.damper = 7500f; c.suspensionSpring = sp;
        }
        public static WheelCollider RL(this VehicleContext ctx) => ctx.RL;
        public static WheelCollider RR(this VehicleContext ctx) => ctx.RR;
    }
}

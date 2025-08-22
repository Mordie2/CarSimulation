// =============================================
// File: Scripts/Vehicle/Handling/SteeringSystem.cs
// Role: Speed-sensitive steering, yaw damping
// =============================================
using UnityEngine;

namespace Vehicle
{
    public class SteeringSystem
    {
        private readonly VehicleContext _ctx;
        private float _steerLagState = 0f;
        private float _steerAngleSmoothed = 0f;
        private float _steeringAngle = 0f;

        public SteeringSystem(VehicleContext ctx) { _ctx = ctx; }

        public void Tick(float horizontalInput, float speedMs)
        {
            float kmh = speedMs * 3.6f;
            float lagAlpha = 1f - Mathf.Exp(-Time.fixedDeltaTime / Mathf.Max(0.001f, _ctx.settings.steerInputLag));
            float followAlpha = 1f - Mathf.Exp(-Time.fixedDeltaTime / Mathf.Max(0.001f, _ctx.settings.steerInputFollow));

            _steerLagState = Mathf.Lerp(_steerLagState, horizontalInput, lagAlpha);
            float delayedIn = Mathf.Lerp(_steerLagState, horizontalInput, followAlpha);

            float t = Mathf.Clamp01(kmh / _ctx.settings.steerAtSpeedKmh);
            float steerScale = Mathf.Lerp(_ctx.settings.minSteerScaleLow, _ctx.settings.minSteerScaleHigh, Mathf.SmoothStep(0f, 1f, t));

            float yaw = _ctx.rb.angularVelocity.y;
            float yawDamp = Mathf.Lerp(_ctx.settings.yawDampAt0Kmh, _ctx.settings.yawDampAt120Kmh, Mathf.Clamp01(kmh / 120f));
            float cmd = delayedIn - yaw * yawDamp;

            float steerRate = Mathf.Lerp(_ctx.settings.steerRateDegPerSecLow, _ctx.settings.steerRateDegPerSecHigh, t);
            float target = _ctx.settings.maxSteerAngle * steerScale * Mathf.Clamp(cmd, -1f, 1f);
            float maxStep = steerRate * Time.fixedDeltaTime;
            _steerAngleSmoothed = Mathf.MoveTowards(_steerAngleSmoothed, target, maxStep);

            _steeringAngle = Mathf.Lerp(_steeringAngle, _steerAngleSmoothed, 1f - Mathf.Exp(-Time.fixedDeltaTime / Mathf.Max(0.001f, _ctx.settings.steerSmoothing)));

            _ctx.FL.steerAngle = _steeringAngle;
            _ctx.FR.steerAngle = _steeringAngle;
        }
    }
}

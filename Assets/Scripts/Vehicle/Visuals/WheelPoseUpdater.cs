// =============================================
// File: Scripts/Vehicle/Visuals/WheelPoseUpdater.cs
// Role: Sync mesh transforms with wheel colliders
// =============================================
using UnityEngine;
using Vehicle;

namespace Vehicle
{
    public class WheelPoseUpdater
    {
        private readonly WheelCollider FL, FR, RL, RR;
        private readonly Transform FLt, FRt, RLt, RRt;
        public WheelPoseUpdater(WheelCollider fl, WheelCollider fr, WheelCollider rl, WheelCollider rr,
                                Transform flt, Transform frt, Transform rlt, Transform rrt)
        { FL = fl; FR = fr; RL = rl; RR = rr; FLt = flt; FRt = frt; RLt = rlt; RRt = rrt; }
        public void UpdateAll()
        {
            UpdateWheelPose(FL, FLt);
            UpdateWheelPose(FR, FRt);
            UpdateWheelPose(RL, RLt);
            UpdateWheelPose(RR, RRt);
        }
        private static void UpdateWheelPose(WheelCollider c, Transform t)
        {
            if (c == null || t == null) return;
            c.GetWorldPose(out var pos, out var rot);
            t.position = pos; t.rotation = rot;
        }
    }
}

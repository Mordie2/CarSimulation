// =============================================
// File: Scripts/Vehicle/Core/VehicleContext.cs
// Role: Shared references & settings across systems
// =============================================
using UnityEngine;


namespace Vehicle
{
    public class VehicleContext
    {
        public Rigidbody rb;
        public WheelCollider FL, FR, RL, RR;
        public VehicleSettings settings;
        public MonoBehaviour host; // for coroutines if needed


        public float KmH => rb != null ? rb.velocity.magnitude * 3.6f : 0f;
    }
}
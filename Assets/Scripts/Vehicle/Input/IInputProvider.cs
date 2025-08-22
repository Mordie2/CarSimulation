// =============================================
// File: Scripts/Vehicle/Input/IInputProvider.cs
// Role: Abstraction for input sources
// =============================================
namespace Vehicle
{
    public interface IInputProvider
    {
        float Horizontal { get; }
        float Throttle { get; } // 0..1 forward, negative values used by drivetrain for reverse intent
        float Brake { get; } // 0..1 service brake
        bool Handbrake { get; }


        // Manual shifting
        bool RequestShiftUp { get; }
        bool RequestShiftDown { get; }


        void Update();
    }
}

using UnityEngine;

namespace Vehicle
{
    public class InputDebugOverlay : MonoBehaviour
    {
        // Candidate axes we want to watch
        string[] axes = {
            "Triggers", "Trigger", "Axis 3", "Axis 9",
            "ControllerAccelerate","RT","RightTrigger","R2","TriggerR","Axis 10","9th axis","Axis 5",
            "ControllerBrake","LT","LeftTrigger","L2","TriggerL","Axis 8","Axis 4",
            "Horizontal","JoystickHorizontal","LeftStickX","Axis 1",
            "Vertical"
        };

        string[] buttons = {
            "Handbrake"
        };

        void OnGUI()
        {
            GUILayout.BeginVertical("box");
            GUILayout.Label("<b>🔎 Input Debug Overlay</b>");

            // Show all axes
            foreach (var a in axes)
            {
                float v = SafeGetAxis(a);
                GUILayout.Label($"{a}: {v:0.00}");
            }

            GUILayout.Space(10);

            // Show buttons
            foreach (var b in buttons)
            {
                bool pressed = SafeGetButton(b);
                GUILayout.Label($"{b}: {(pressed ? "DOWN" : "up")}");
            }

            GUILayout.EndVertical();
        }

        float SafeGetAxis(string n)
        {
            try { return Input.GetAxisRaw(n); }
            catch { return float.NaN; }
        }

        bool SafeGetButton(string n)
        {
            try { return Input.GetButton(n); }
            catch { return false; }
        }
    }
}

using UnityEngine;
using Vehicle;

namespace Vehicle.Debugging
{
    public class DrivetrainDebugOverlay : MonoBehaviour
    {
        [Header("Overlay")]
        public KeyCode toggleKey = KeyCode.F9;
        public bool show = true;
        public int instanceIndex = 0;
        public float scale = 1.0f;

        [Header("Window")]
        public Vector2 windowPos = new Vector2(12, 12);
        public float width = 420f;
        public float lineHeight = 18f;

        [Header("Background")]
        public bool drawBackground = true;
        public Color backgroundColor = new Color(0f, 0f, 0f, 0.65f);
        public int backgroundPadding = 8;
        public int cornerRadius = 0;

        [Header("Optional font")]
        public Font monoFont;

        GUIStyle _label, _header, _monoSmall, _panelStyle;
        Texture2D _bgTex;
        bool _stylesReady;

        void Update()
        {
            if (Input.GetKeyDown(toggleKey)) show = !show;
        }

        void EnsureStylesInOnGUI()
        {
            if (_stylesReady) return;

            _label = new GUIStyle(GUI.skin.label) { fontSize = 14, richText = true };
            _header = new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold, richText = true };
            _monoSmall = new GUIStyle(GUI.skin.label) { fontSize = 12 };

            if (monoFont != null)
            {
                _label.font = monoFont;
                _header.font = monoFont;
                _monoSmall.font = monoFont;
            }

            _bgTex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            _bgTex.name = "DrivetrainOverlayBG";
            _bgTex.hideFlags = HideFlags.HideAndDontSave;
            _bgTex.SetPixel(0, 0, Color.white);
            _bgTex.Apply();

            _panelStyle = new GUIStyle(GUI.skin.box);
            _panelStyle.normal.background = _bgTex;
            _panelStyle.border = new RectOffset(cornerRadius, cornerRadius, cornerRadius, cornerRadius);
            _panelStyle.margin = new RectOffset(0, 0, 0, 0);
            _panelStyle.padding = new RectOffset(0, 0, 0, 0);

            _stylesReady = true;
        }

        void OnGUI()
        {
            if (!show) return;
            EnsureStylesInOnGUI();

            var list = DrivetrainSystem.Instances;
            if (list == null || list.Count == 0)
            {
                GUI.Label(new Rect(windowPos.x, windowPos.y, 400, 30),
                          "DrivetrainDebugOverlay: no DrivetrainSystem instances found.", _label);
                return;
            }

            instanceIndex = Mathf.Clamp(instanceIndex, 0, list.Count - 1);
            var drive = list[instanceIndex];
            var d = drive.DebugInfo;

            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1));

            float x = windowPos.x / scale;
            float yTop = windowPos.y / scale;
            float w = width / scale;

            // -------- measure content height (no dynamic) --------
            float measureY = yTop;
            bool drawMechLine = d.mechSpeedKmh > 0f;
            MeasureContent(ref measureY, drawMechLine);
            float contentHeight = measureY - yTop;

            // -------- background --------
            if (drawBackground)
            {
                var bgRect = new Rect(
                    x - backgroundPadding,
                    yTop - backgroundPadding,
                    w + backgroundPadding * 2,
                    contentHeight + backgroundPadding * 2
                );
                var prev = GUI.color;
                GUI.color = backgroundColor;
                GUI.Box(bgRect, GUIContent.none, _panelStyle);
                GUI.color = prev;
            }

            // -------- text --------
            float y = yTop;
            float innerX = x;
            float innerW = w;

            DrawLine(ref y, $"<b>Drivetrain Debug</b>  [#{instanceIndex}]  <size=11>{(Application.isEditor ? "Editor" : "Player")}</size>", _header, innerX, innerW);
            DrawLine(ref y, $"Time: {d.time:0.000}   Speed: {d.vehSpeedKmh:0.0} km/h", _label, innerX, innerW); Space(ref y);

            DrawLine(ref y, $"Gear: <b>{d.gearIndex}</b> ({d.gearLabel})    Ratio: {d.ratio:0.000} (|abs| {d.ratioAbs:0.000})", _label, innerX, innerW);
            DrawLine(ref y, $"Throttle: {d.throttle01:0.00}   Clutch: {d.clutch01:0.00}   PedalDown: {d.pedalDown}", _label, innerX, innerW);
            DrawLine(ref y, $"Shifting: {d.isShifting}  LastUp: {d.lastShiftWasUpshift}  ShiftTimer: {d.shiftTimer:0.000}s", _label, innerX, innerW);
            DrawLine(ref y, $"Launch:{d.launchActive}  Instant:{d.instantActive}  Cut:{d.torqueCutActive}  LiftCut:{d.liftCutActive}", _label, innerX, innerW);
            DrawLine(ref y, $"Limiter:{d.limiterActive} (t={d.limiterT:0.00})  HardCut:{d.hardCutActive}", _label, innerX, innerW); Space(ref y);

            DrawLine(ref y, $"WheelRPM: {d.wheelRPM:0}    EngRPM: <b>{d.engineRPM:0}</b>", _label, innerX, innerW);
            DrawLine(ref y, $"Ω_eng: {d.engOmega:0.0} rad/s   Ω_gearLP: {d.gearOmegaLP:0.0}   Ω_gearRaw: {d.gearOmegaRaw:0.0}", _label, innerX, innerW);
            DrawLine(ref y, $"Coupled:{d.coupledNow}  CoastCoupled:{d.coastCoupled}  FlywheelBlend:{d.flywheelBlend}  TwoMass:{d.runTwoMass}", _label, innerX, innerW);
            if (drawMechLine)
                DrawLine(ref y, $"MechSpeed: {d.mechSpeedKmh:0.0} km/h  |Δ|: {(d.kmhMismatch):0.00} km/h", _label, innerX, innerW);
            Space(ref y);

            DrawLine(ref y, $"T_req: {d.T_req:0} Nm   T_drag: {d.T_drag:0} Nm   T_inertia: {d.inertiaNm:0} Nm", _label, innerX, innerW);
            DrawLine(ref y, $"T→Gear(eng): <b>{d.T_toGear_engineSide:0}</b> Nm   AxleTorque: <b>{d.axleTorque:0}</b> Nm", _label, innerX, innerW);
            DrawLine(ref y, $"Wheel Motor: {d.perWheelMotorNm:0} Nm/wheel   Wheel Brake: {d.perWheelBrakeNm:0} Nm/wheel", _label, innerX, innerW);
            DrawLine(ref y, $"EngineBrake (axle): {d.engineBrakeWheelNm:0} Nm   Setting: {d.engineBrakeSetting}   Inertia: {d.engineInertiaSetting}", _label, innerX, innerW);

            Space(ref y);
            GUI.Label(new Rect(innerX + backgroundPadding, y + backgroundPadding, innerW - backgroundPadding * 2, lineHeight),
                      "F9 to toggle • scale/position in inspector", _monoSmall);
        }

        // Measure pass: mirror layout to get total height (no dynamic)
        void MeasureContent(ref float y, bool drawMechLine)
        {
            Step(ref y);            // header
            Step(ref y);            // time/speed
            Space(ref y);

            Step(ref y);            // gear/ratio
            Step(ref y);            // throttle/clutch
            Step(ref y);            // shifting
            Step(ref y);            // launch/instant/cuts
            Step(ref y);            // limiter/hardcut
            Space(ref y);

            Step(ref y);            // wheel/eng rpm
            Step(ref y);            // omegas
            Step(ref y);            // coupled flags
            if (drawMechLine) Step(ref y); // mech speed / mismatch
            Space(ref y);

            Step(ref y);            // T_req/drag/inertia
            Step(ref y);            // ToGear / axle
            Step(ref y);            // per-wheel motor/brake
            Step(ref y);            // engine brake summary

            Space(ref y);           // footer line space
            Step(ref y);            // footer small label
        }

        // Helpers
        void DrawLine(ref float y, string text, GUIStyle style, float x, float w)
        {
            GUI.Label(new Rect(x + backgroundPadding, y + backgroundPadding, w - backgroundPadding * 2, lineHeight), text, style);
            y += lineHeight;
        }

        void Space(ref float y) => y += lineHeight * 0.35f;
        void Step(ref float y) => y += lineHeight;
    }
}

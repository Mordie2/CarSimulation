using UnityEngine;
using UnityEngine.UI;
using Vehicle;

public class TachometerUI : MonoBehaviour
{
    [Header("Source")]
    public CarController car;

    [Header("Visuals (choose any)")]
    public Image radialFill;
    public RectTransform needle;
    public Text rpmText;
    public Text gearText;
    public Text speedText;

    [Header("Ranges")]
    public float minDisplayRPM = 800f;
    public float maxDisplayRPM = 7200f;

    [Header("Look/Feel")]
    public float lerpSpeed = 10f;
    public float needleMinAngle = -130f;
    public float needleMaxAngle = 130f;

    [Header("Orientation")]
    public bool invertRadial = false;
    public bool invertNeedle = false;

    float _fill;
    int _lastGear = -999; // last gear index

    void LateUpdate()
    {
        if (!car) return;

        // normalize RPM (0..1)
        float rpm01 = Mathf.InverseLerp(minDisplayRPM, maxDisplayRPM, car.EngineRPM);

        // snap on gear change
        if (car.CurrentGear != _lastGear)
        {
            _fill = rpm01;
            _lastGear = car.CurrentGear;
        }
        else
        {
            // smooth interpolation
            float a = 1f - Mathf.Exp(-Time.deltaTime * Mathf.Max(0.01f, lerpSpeed));
            _fill = Mathf.Lerp(_fill, rpm01, a);
        }

        // --- UI updates ---
        if (radialFill)
            radialFill.fillAmount = invertRadial ? 1f - _fill : _fill;

        if (needle)
        {
            float t = invertNeedle ? (1f - _fill) : _fill;
            float ang = Mathf.Lerp(needleMinAngle, needleMaxAngle, t);
            var e = needle.localEulerAngles;
            e.z = -ang;
            needle.localEulerAngles = e;
        }

        if (rpmText) rpmText.text = $"{car.EngineRPM:0} rpm";
        if (gearText) gearText.text = car.GearLabel;
        if (speedText)
        {
            float kmh = car.speed * 3.6f;
            speedText.text = $"{kmh:0} km/h";
        }
    }
}

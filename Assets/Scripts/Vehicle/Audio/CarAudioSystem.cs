// =============================================
// File: Scripts/Vehicle/Audio/CarAudioSystem.cs
// Role: FMOD engine + wind loops, parameter smoothing
// =============================================

using UnityEngine;
using FMODUnity;
using FMOD.Studio;

namespace Vehicle
{
    public class CarAudioSystem
    {
        private readonly VehicleContext _ctx;
        private EventInstance _engineInstance;
        private EventInstance _windInstance;
        private bool _engineStarted;

        private float _rpmSmoothed = 0f;
        private float _windSpeed01Smoothed = 0f;

        private const string EngineParamName = "EngineRPM";
        private const string ThrottleParamName = "Throttle";

        [SerializeField, Range(0.01f, 0.3f)] private float rpmParamSmoothing = 0.06f;
        [SerializeField, Range(0.01f, 0.5f)] private float windParamSmoothing = 0.08f;
        private float windStartAtKmh = 15f;
        private float windFullAtKmh = 160f;

        public CarAudioSystem(VehicleContext ctx)
        {
            _ctx = ctx;

            if (FMODEvents.instance == null) { Debug.LogError("[CarAudio] FMODEvents.instance is null"); return; }
            if (AudioManager.instance == null) { Debug.LogError("[CarAudio] AudioManager.instance is null"); return; }

            EnsureEngineInstance();
            if (_engineInstance.isValid()) { _engineInstance.start(); _engineStarted = true; }
            EnsureWindInstance();
            if (_windInstance.isValid()) _windInstance.start();
        }

        public void OnUpdate()
        {
            if (_engineStarted && _engineInstance.isValid())
                _engineInstance.set3DAttributes(RuntimeUtils.To3DAttributes(_ctx.host.transform.position));
            if (_windInstance.isValid())
                _windInstance.set3DAttributes(RuntimeUtils.To3DAttributes(_ctx.host.transform.position));
        }

        public void OnFixedUpdate(float engineRPM, float throttle01)
        {

            if (_engineStarted && _engineInstance.isValid())
            {
                float rpmClamped = Mathf.Clamp(engineRPM, 0f, _ctx.settings.revLimiterRPM);
                _rpmSmoothed = Mathf.Lerp(_rpmSmoothed, rpmClamped, 1f - Mathf.Exp(-Time.fixedDeltaTime / rpmParamSmoothing));
                AudioManager.instance.SetInstanceParameter(_engineInstance, EngineParamName, _rpmSmoothed);
                AudioManager.instance.SetInstanceParameter(_engineInstance, ThrottleParamName, Mathf.Clamp01(throttle01));
            }

            if (_windInstance.isValid())
            {
                float kmh = _ctx.KmH;
                float raw01 = Mathf.Clamp01(Mathf.InverseLerp(windStartAtKmh, windFullAtKmh, kmh));
                _windSpeed01Smoothed = Mathf.Lerp(_windSpeed01Smoothed, raw01, 1f - Mathf.Exp(-Time.fixedDeltaTime / windParamSmoothing));
                AudioManager.instance.SetInstanceParameter(_windInstance, "Speed", _windSpeed01Smoothed);
            }
        }

        public void Dispose()
        {
            if (_engineInstance.isValid()) _engineInstance.stop(FMOD.Studio.STOP_MODE.IMMEDIATE);
            if (_windInstance.isValid()) _windInstance.stop(FMOD.Studio.STOP_MODE.IMMEDIATE);
        }

        private void EnsureEngineInstance()
        {
            if (!_engineInstance.isValid())
            {
                _engineInstance = AudioManager.instance.GetOrCreateInstance(
                    key: $"engine_{_ctx.host.GetInstanceID()}",
                    eventRef: FMODEvents.instance.Engine,
                    position: _ctx.host.transform.position
                );
            }
        }
        private void EnsureWindInstance()
        {
            if (!_windInstance.isValid())
            {
                _windInstance = AudioManager.instance.GetOrCreateInstance(
                    key: $"wind_{_ctx.host.GetInstanceID()}",
                    eventRef: FMODEvents.instance.Wind,
                    position: _ctx.host.transform.position
                );
            }
        }

        public void OnGearChanged(int oldGear, int newGear)
        {
            if (FMODEvents.instance == null || AudioManager.instance == null)
                return;

            // Simple thresholds (same as before)
            bool downshift = newGear < oldGear;

            if (!downshift && newGear > 2 && _rpmSmoothed > 4000f)
                AudioManager.instance.PlayOneShot(FMODEvents.instance.Shift, _ctx.host.transform.position);

            if (downshift && _rpmSmoothed > 2000f)
                AudioManager.instance.PlayOneShot(FMODEvents.instance.Shift, _ctx.host.transform.position);
        }
    }
}


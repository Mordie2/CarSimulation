using System.Collections.Generic;
using UnityEngine;
using FMODUnity;
using FMOD.Studio;
using UnityEngine.SceneManagement;

public class AudioManager : MonoBehaviour
{
    [Header("Volume")]
    [Range(0, 1)]
    public float masterVolume = 1;

    [Range(0, 1)]
    public float SFXVolume = 1;

    [Range(0, 1)]
    public float MusicVolume = 1;

    private Bus masterBus;
    private Bus SFXBus;
    private Bus MusicBus;

    private List<EventInstance> eventInstances;
    private List<StudioEventEmitter> eventEmitters;
    public bool useDebug;
    public static AudioManager instance { get; private set; }
    private Dictionary<string, EventInstance> persistentInstances = new();
    private string sceneName;
    public EventInstance MusicInstance;
    public EventInstance AmbianceInstance;

    void Awake()
    {
        sceneName = SceneManager.GetActiveScene().name;

        if (instance != null)
        {
            if (useDebug)
                Debug.Log("Found more than one Audio Manager in the scene.");

            Destroy(gameObject);
            return;
        }

        instance = this;
        DontDestroyOnLoad(gameObject);

        eventInstances = new List<EventInstance>();
        eventEmitters = new List<StudioEventEmitter>();
        masterBus = RuntimeManager.GetBus("bus:/");
        //SFXBus = RuntimeManager.GetBus("bus:/SFX");
        //MusicBus = RuntimeManager.GetBus("bus:/Music");

        SceneManager.sceneLoaded += OnSceneLoaded;

        OnSceneLoaded(SceneManager.GetActiveScene(), LoadSceneMode.Single);
    }

    public void CheckIfSceneChange()
    {
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        sceneName = SceneManager.GetActiveScene().name;
        Debug.Log(sceneName);

        CleanUp();


    }

    private void InitializeMusic(EventReference MusicReference)
    {
        Debug.Log("Initializing music for: " + sceneName);
        MusicInstance = CreateInstance(MusicReference);
        if (MusicInstance.isValid())
        {
            MusicInstance.start();
        }
        else
        {
            Debug.LogWarning("MusicInstance is not valid.");
        }

        if (sceneName == "Menu")
        {
            SetInstanceParameter(MusicInstance, "musicintensity", 0);
            AmbianceInstance.stop(FMOD.Studio.STOP_MODE.IMMEDIATE);
        }
        else if (sceneName == "Main")
        {
            SetInstanceParameter(MusicInstance, "musicintensity", 1);
            AmbianceInstance.start();
            AmbianceInstance.release();
        }
    }

    private void Update()
    {
        masterBus.setVolume(masterVolume);
        //SFXBus.setVolume(SFXVolume);
        //MusicBus.setVolume(MusicVolume);
    }

    public void PlayOneShot(EventReference sound, Vector3 worldPos)
    {
        RuntimeManager.PlayOneShot(sound, worldPos);
    }

    public StudioEventEmitter InitializeEventEmitter(EventReference eventReference, GameObject emitterGameObject)
    {
        StudioEventEmitter emitter = emitterGameObject.GetComponent<StudioEventEmitter>();
        emitter.EventReference = eventReference;
        eventEmitters.Add(emitter);
        return emitter;
    }

    private void CleanUp()
    {
        if (eventInstances != null)
        {
            foreach (EventInstance eventInstance in eventInstances)
            {
                eventInstance.stop(FMOD.Studio.STOP_MODE.IMMEDIATE);
                eventInstance.release();
            }

            eventInstances.Clear();
        }

        if (eventEmitters != null)
        {
            eventEmitters.Clear();
        }
    }

    private void OnDestroy()
    {
        if (instance == this)
        {
            CleanUp();
        }
    }

    public EventInstance CreateInstance(EventReference eventReference)
    {
        EventInstance eventInstance = RuntimeManager.CreateInstance(eventReference);
        if (eventInstance.isValid())
        {
            eventInstances.Add(eventInstance);
        }
        else
        {
            Debug.LogWarning("Failed to create a valid event instance.");
        }
        return eventInstance;
    }

    public EventInstance GetOrCreateInstance(string key, EventReference eventRef, Vector3 position)
    {
        if (!persistentInstances.TryGetValue(key, out var instance) || !instance.isValid())
        {
            instance = RuntimeManager.CreateInstance(eventRef);
            instance.set3DAttributes(RuntimeUtils.To3DAttributes(position));
            persistentInstances[key] = instance;
        }

        return instance;
    }

    public bool IsInstancePlaying(string key)
    {
        if (persistentInstances.TryGetValue(key, out var instance) && instance.isValid())
        {
            instance.getPlaybackState(out var state);
            return state == PLAYBACK_STATE.PLAYING;
        }

        return false;
    }

    public void SetInstanceParameter(EventInstance eventInstance, string parameterName, float parameterValue)
    {
        if (eventInstance.isValid())
        {
            eventInstance.setParameterByName(parameterName, parameterValue);
        }
        else
        {
            Debug.LogWarning("Attempted to set a parameter on an invalid event instance.");
        }
    }

    public void SetEmitterParameter(StudioEventEmitter emitter, string parameterName, float parameterValue)
    {
        if (emitter != null)
        {
            emitter.EventInstance.setParameterByName(parameterName, parameterValue);
        }
        else
        {
            Debug.LogWarning("Attempted to set a parameter on a null emitter.");
        }
    }

    public static void PlayOneShotWithParameters(EventReference eventReference, Vector3 position, params (string, float)[] parameters)
    {
        EventInstance eventInstance = RuntimeManager.CreateInstance(eventReference);
        eventInstance.set3DAttributes(RuntimeUtils.To3DAttributes(position));

        foreach (var parameter in parameters)
        {
            eventInstance.setParameterByName(parameter.Item1, parameter.Item2);
        }

        eventInstance.start();
        eventInstance.release();
    }

    public bool IsEventInstancePlaying(EventInstance eventInstance)
    {
        if (eventInstance.isValid())
        {
            eventInstance.getPlaybackState(out PLAYBACK_STATE state);
            return state == PLAYBACK_STATE.PLAYING;
        }
        return false;
    }

    public static void Nullify()
    {
        instance = null;
    }
}
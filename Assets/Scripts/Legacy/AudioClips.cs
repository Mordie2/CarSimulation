using System.Collections.Generic;
using UnityEngine;

[System.Serializable]
public class NamedAudioClip
{
    public string name;
    public AudioClip clip;
}

public class AudioClips : MonoBehaviour
{
    public NamedAudioClip[] namedAudioClips;

    private Dictionary<string, AudioClip> audioClipDictionary;

    void Awake()
    {
        //DontDestroyOnLoad(gameObject);

        audioClipDictionary = new Dictionary<string, AudioClip>();
        foreach (var namedClip in namedAudioClips)
        {
            if (!audioClipDictionary.ContainsKey(namedClip.name))
            {
                audioClipDictionary.Add(namedClip.name, namedClip.clip);
            }
            else
            {
                Debug.LogWarning($"Duplicate audio clip name found: {namedClip.name}. Ignoring duplicate.");
            }
        }
    }

    public AudioClip GetAudioClipByName(string name)
    {
        if (audioClipDictionary.TryGetValue(name, out var clip))
        {
            return clip;
        }
        else
        {
            Debug.LogWarning($"Audio clip with name {name} not found.");
            return null;
        }
    }
}

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using FMODUnity;

public class FMODEvents : MonoBehaviour
{
    [field: Header("SFX")]
    [field: SerializeField] public EventReference Test { get; private set; }
    [field: SerializeField] public EventReference Engine { get; private set; }
    [field: SerializeField] public EventReference Shift { get; private set; }
    [field: SerializeField] public EventReference Wind { get; private set; }
    [field: SerializeField] public EventReference Skid { get; private set; }
    [field: SerializeField] public EventReference WheelSpin { get; private set; }
    [field: SerializeField] public EventReference EngineStart { get; private set; }
    [field: SerializeField] public EventReference Brake { get; private set; }
    [field: SerializeField] public EventReference Pops { get; private set; }
    [field: SerializeField] public EventReference Handbrake { get; private set; }



    [field: Header("Music")]
    public static FMODEvents instance { get; private set; }

    private void Awake()
    {
        if (instance != null)
        {

            Debug.Log("Found more than one Audio Manager in the scene.");
            Destroy(gameObject);

        }
        else
        {
            instance = this;
            DontDestroyOnLoad(gameObject);
        }
    }
}
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class LapTimer : MonoBehaviour
{
    public float BestLapTime { get; private set; } = Mathf.Infinity;
    public float LastLapTime { get; private set; } = 0;
    public float CurrentLapTime { get; private set; } = 0;
    public int CurrentLap { get; private set; } = 0;

    private float lapTimerTimestamp;
    private int lastCheckpointPassed = 0;

    private Transform checkpointsParent;
    private int checkpointCount;
    private int checkpointLayer;

    void Start()
    {
        checkpointsParent = GameObject.Find("Checkpoints").transform;
        if (checkpointsParent == null)
        {
            Debug.LogError("Checkpoints parent object not found!");
            return;
        }

        checkpointCount = checkpointsParent.childCount;
        if (checkpointCount == 0)
        {
            Debug.LogError("No checkpoints found!");
            return;
        }

        checkpointLayer = LayerMask.NameToLayer("Checkpoint");
        if (checkpointLayer == -1)
        {
            Debug.LogError("Checkpoint layer not found!");
            return;
        }

        StartLap(); // Start the first lap
    }

    void StartLap()
    {
        Debug.Log("StartLap!");
        CurrentLap++;
        lastCheckpointPassed = 1;
        lapTimerTimestamp = Time.time;
    }

    void EndLap()
    {
        LastLapTime = Time.time - lapTimerTimestamp;
        BestLapTime = Mathf.Min(LastLapTime, BestLapTime);
        Debug.Log("Lap time was " + LastLapTime);
        Debug.Log("Best time is " + BestLapTime);
    }

    void OnTriggerEnter(Collider collider)
    {
        if (collider.gameObject.layer == checkpointLayer)
        {
            Debug.Log("Collided with checkpoint: " + collider.gameObject.name);
            if (collider.gameObject.name == "1")
            {
                if (lastCheckpointPassed == checkpointCount)
                {
                    EndLap();
                }
                StartLap();
            }
            else if (collider.gameObject.name == (lastCheckpointPassed + 1).ToString())
            {
                lastCheckpointPassed++;
            }
        }
    }

    void Update()
    {
        CurrentLapTime = lapTimerTimestamp > 0 ? Time.time - lapTimerTimestamp : 0;
    }
}

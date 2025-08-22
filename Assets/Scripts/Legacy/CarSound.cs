using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Vehicle;
public class CarSound : MonoBehaviour
{
    float pitch = 0.7f;
    AudioSource audioSource;
    CarController Car;

    void Start()
    {
        audioSource = GetComponent<AudioSource>();
        Car = GetComponent<CarController>();
        audioSource.pitch = pitch;
    }

    // Update is called once per frame
    void Update()
    {
        pitch = MapValue(Car.speed);
        audioSource.pitch = pitch;
    }

    public static float MapValue(float x)
    {
        float y1 = 0.8f;
        float y2 = 2.5f;
        float x1 = 0.0f;
        float x2 = 35.0f;

        return y1 + ((x - x1) * (y2 - y1)) / (x2 - x1);
    }
}
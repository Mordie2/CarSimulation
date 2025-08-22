using UnityEngine;

public class CameraMovement : MonoBehaviour
{
    public Transform objectToFollow;

    public Vector3 offset = new Vector3(0f, 2f, -5f);
    public float xOffsetRotation = 18.295f;

    public float followSpeed = 5f;
    public float rotationLerpSpeed = 5f;   // for Slerp
    public float yawSpeed = 120f;          // degrees/second for user yaw

    private float currentYaw = 0f;

    void FixedUpdate()
    {
        float mouseX = Input.GetAxis("Mouse X");
        float joystickX = Input.GetAxis("RightJoystickHorizontal");

        if (Input.GetMouseButton(1))
        {
            joystickX = 0f;
            RotateCamera(mouseX);
        }
        else if (Mathf.Abs(joystickX) > 0.01f)
        {
            mouseX = 0f;
            RotateCamera(joystickX);
        }

        MoveToTarget();

        if (Input.GetKeyDown(KeyCode.C) || Input.GetButton("CameraReset"))
        {
            RecenterCamera();
        }
    }

    void MoveToTarget()
    {
        // Rotate offset by the car's rotation, then by our user yaw around the car's up axis
        Quaternion yawRot = objectToFollow.rotation * Quaternion.Euler(0f, currentYaw, 0f);
        Vector3 targetPos = objectToFollow.position + yawRot * offset;

        transform.position = Vector3.Lerp(transform.position, targetPos, followSpeed * Time.deltaTime);

        // Look at the car with an X tilt; keep the car's up to avoid banking on slopes
        Quaternion lookRot = Quaternion.LookRotation(objectToFollow.position - transform.position, objectToFollow.up);
        Quaternion xTilt = Quaternion.Euler(xOffsetRotation, 0f, 0f);
        transform.rotation = Quaternion.Slerp(transform.rotation, lookRot * xTilt, rotationLerpSpeed * Time.deltaTime);
    }

    void RotateCamera(float horizontalInput)
    {
        currentYaw += horizontalInput * yawSpeed * Time.deltaTime;
    }

    void RecenterCamera()
    {
        currentYaw = 0f;
    }
}

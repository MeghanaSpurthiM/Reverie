using UnityEngine;
using UnityEngine.XR;

/// Looks around with the keyboard or mouse when no headset is active. Attached to TrackingSpace,
/// because the headset drives the eye anchors and the room is parented to the camera rig.
public class DesktopLook : MonoBehaviour
{
    public Transform eye;

    [Tooltip("Arrow key speed, in degrees per second.")]
    public float keySpeed = 90f;
    [Tooltip("Mouse speed while the right button is held, in degrees per unit of mouse movement.")]
    public float mouseSpeed = 3f;
    [Range(0f, 89f)]
    public float maxPitch = 80f;

    float _pitch;

    void Update()
    {
        if (XRSettings.isDeviceActive) return;

        float yaw = 0f, pitch = 0f;
        if (Input.GetKey(KeyCode.LeftArrow))  yaw   -= keySpeed * Time.deltaTime;
        if (Input.GetKey(KeyCode.RightArrow)) yaw   += keySpeed * Time.deltaTime;
        if (Input.GetKey(KeyCode.UpArrow))    pitch -= keySpeed * Time.deltaTime;
        if (Input.GetKey(KeyCode.DownArrow))  pitch += keySpeed * Time.deltaTime;
        if (Input.GetMouseButton(1))
        {
            yaw   += Input.GetAxis("Mouse X") * mouseSpeed;
            pitch -= Input.GetAxis("Mouse Y") * mouseSpeed;
        }
        if (yaw != 0f || pitch != 0f) Look(yaw, pitch);
    }

    /// Turns the view about the eye. Positive yaw looks right, positive pitch looks down.
    public void Look(float yawDegrees, float pitchDegrees)
    {
        transform.RotateAround(eye.position, Vector3.up, yawDegrees);

        float next = Mathf.Clamp(_pitch + pitchDegrees, -maxPitch, maxPitch);
        transform.RotateAround(eye.position, eye.right, next - _pitch);
        _pitch = next;
    }
}

using UnityEngine;
using UnityEngine.InputSystem;

namespace CAIVR.VR
{
    /// <summary>
    /// A first-person camera for sitting in the room, with no headset and no rig.
    ///
    /// Hold the right mouse button and move the mouse to look around, the way the
    /// Scene view works. The cursor stays free the whole time, because every
    /// piece of UI in this scene is an object in the room that you click.
    ///
    /// Looking is limited to what a seated person can comfortably do, so it feels
    /// like being in the chair rather than flying through the room.
    /// </summary>
    public sealed class SeatedViewpoint : MonoBehaviour
    {
        [SerializeField, Range(0.02f, 0.5f)] float degreesPerPixel = 0.12f;

        [Tooltip("How far either side of the starting direction you can turn your head.")]
        [SerializeField] float maxYaw = 110f;

        [SerializeField] float minPitch = -65f;
        [SerializeField] float maxPitch = 55f;

        [Tooltip("Hold the right mouse button to look. Off means the mouse always looks (cursor still free).")]
        [SerializeField] bool requireRightButton = true;

        float _startYaw;
        float _yaw;
        float _pitch;

        void Awake()
        {
            var euler = transform.eulerAngles;
            _startYaw = euler.y;
            _pitch = NormalisePitch(euler.x);
        }

        void Update()
        {
            var mouse = Mouse.current;
            if (mouse == null) return;
            if (requireRightButton && !mouse.rightButton.isPressed) return;

            var delta = mouse.delta.ReadValue();

            _yaw = Mathf.Clamp(_yaw + delta.x * degreesPerPixel, -maxYaw, maxYaw);
            _pitch = Mathf.Clamp(_pitch - delta.y * degreesPerPixel, minPitch, maxPitch);

            transform.rotation = Quaternion.Euler(_pitch, _startYaw + _yaw, 0f);
        }

        // Euler angles report 350 degrees for "10 below level"; we want -10.
        static float NormalisePitch(float degrees) => degrees > 180f ? degrees - 360f : degrees;
    }
}

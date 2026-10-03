using UnityEngine;

namespace CAIVR.VR
{
    /// <summary>
    /// Puts the student back at their seat if they fall out of the world.
    ///
    /// The XR rig has gravity, so any hole in the room's floor sends the student
    /// falling forever. That is nauseating in a headset and, because the floor is
    /// invisible from below, there is no way for the student to recover on their
    /// own. The first version of the consultation room had no colliders at all,
    /// which is how this was found; this is the backstop for the next gap.
    /// </summary>
    public sealed class RigFallRecovery : MonoBehaviour
    {
        [Tooltip("How far below the starting height counts as having fallen out of the world.")]
        [SerializeField] float fallLimitMeters = 3f;

        Vector3 _spawnPosition;
        Quaternion _spawnRotation;
        CharacterController _controller;

        void Awake()
        {
            _spawnPosition = transform.position;
            _spawnRotation = transform.rotation;
            _controller = GetComponent<CharacterController>();
        }

        void Update()
        {
            if (transform.position.y >= _spawnPosition.y - fallLimitMeters) return;

            Debug.LogWarning("[CAIVR] The rig fell out of the world. Returning it to its start. " +
                             "Check the environment has colliders where the student can walk.");

            // A CharacterController overwrites any position set while it is enabled.
            if (_controller != null) _controller.enabled = false;
            transform.SetPositionAndRotation(_spawnPosition, _spawnRotation);
            if (_controller != null) _controller.enabled = true;
        }
    }
}

using CAIVR.Speech;
using UnityEngine;

namespace CAIVR.VR
{
    /// <summary>
    /// The professor as a presence in the room, independent of what she looks like.
    ///
    /// Takes care of the two things that make a figure feel like she is in the
    /// room with you, so they are done once rather than redone for every model:
    ///   - her voice comes from her head, not from everywhere
    ///   - her head turns to look at you, within a believable range
    ///
    /// Which model she is does not matter here. Swapping the model means pointing
    /// <see cref="head"/> at the new model's head bone; nothing else changes.
    /// </summary>
    public sealed class ProfessorAvatar : MonoBehaviour
    {
        [SerializeField] VoiceLinePlayer voice;
        [SerializeField] Transform head;

        [Header("Voice")]
        [SerializeField] float minDistance = 1.2f;
        [SerializeField] float maxDistance = 12f;

        [Header("Gaze")]
        [Tooltip("How far, in degrees, her head will turn away from straight ahead to follow you.")]
        [SerializeField] float maxHeadYaw = 55f;
        [SerializeField] float turnDegreesPerSecond = 120f;

        AudioSource _mouthAudio;
        Quaternion _headRest;

        void Awake()
        {
            if (voice == null) voice = FindFirstObjectByType<VoiceLinePlayer>();
            if (head == null) head = transform;

            _headRest = head.localRotation;

            BuildVoiceSource();
        }

        void BuildVoiceSource()
        {
            _mouthAudio = head.GetComponent<AudioSource>();
            if (_mouthAudio == null) _mouthAudio = head.gameObject.AddComponent<AudioSource>();

            _mouthAudio.playOnAwake = false;
            _mouthAudio.loop = false;

            // Fully 3D so the voice has a direction. Linear rolloff inside a small
            // room sounds more natural than the logarithmic default, which keeps
            // a voice loud right up to the walls.
            _mouthAudio.spatialBlend = 1f;
            _mouthAudio.rolloffMode = AudioRolloffMode.Linear;
            _mouthAudio.minDistance = minDistance;
            _mouthAudio.maxDistance = maxDistance;
            _mouthAudio.dopplerLevel = 0f;      // she is not moving fast enough to matter, and pitch shifts would be distracting

            if (voice != null) voice.UseOutput(_mouthAudio);
        }

        void LateUpdate()
        {
            var cam = Camera.main;
            if (cam == null) return;

            var rest = transform.rotation * _headRest;
            var toViewer = cam.transform.position - head.position;
            toViewer.y = 0f;

            Quaternion goal = rest;

            if (toViewer.sqrMagnitude > 0.01f)
            {
                // Clamp relative to where she is facing, so she follows you
                // around the table without her head ever swivelling unnaturally.
                var forward = transform.forward;
                forward.y = 0f;
                var angle = Vector3.SignedAngle(forward, toViewer, Vector3.up);
                var clamped = Mathf.Clamp(angle, -maxHeadYaw, maxHeadYaw);

                goal = Quaternion.AngleAxis(clamped, Vector3.up) * Quaternion.LookRotation(forward, Vector3.up);
            }

            head.rotation = Quaternion.RotateTowards(head.rotation, goal, turnDegreesPerSecond * Time.deltaTime);
        }
    }
}

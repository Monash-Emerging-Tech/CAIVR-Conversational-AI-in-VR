using CAIVR.Speech;
using UnityEngine;

namespace CAIVR.VR
{
    /// <summary>
    /// The professor's voice as a presence in the room, independent of what she looks like.
    ///
    /// Her voice comes from her head, not from everywhere, so it has a direction and gets
    /// quieter as you lean away. Which model she is does not matter here. Swapping the model
    /// means pointing <see cref="head"/> at the new model's head bone; nothing else changes.
    ///
    /// Where she looks, how she moves and what her face does are separate:
    /// <see cref="ProfessorGaze"/>, <see cref="ProfessorBody"/> and <see cref="ProfessorExpression"/>.
    /// </summary>
    public sealed class ProfessorAvatar : MonoBehaviour
    {
        [SerializeField] VoiceLinePlayer voice;
        [SerializeField] Transform head;

        [Header("Voice")]
        [SerializeField] float minDistance = 1.2f;
        [SerializeField] float maxDistance = 12f;

        AudioSource _mouthAudio;

        void Awake()
        {
            if (voice == null) voice = FindFirstObjectByType<VoiceLinePlayer>();
            if (head == null) head = transform;

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
    }
}

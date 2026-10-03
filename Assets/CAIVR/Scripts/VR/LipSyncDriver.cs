using CAIVR.Speech;
using UnityEngine;

namespace CAIVR.VR
{
    /// <summary>
    /// Makes a mouth move while the professor talks.
    ///
    /// The stakeholder was explicit that this does not need to be accurate: "just
    /// make sure the mouth moves so it imitates talking", small micro movements
    /// over a loop. So this follows the loudness of the voice rather than trying
    /// to work out which sounds are being made, which is cheap, runs on a Quest,
    /// and looks believable at conversational distance.
    ///
    /// It drives whichever of these you give it, so it works with any head
    /// without touching this code:
    ///   - a blendshape on a face mesh (the usual rigged-head setup)
    ///   - a jaw bone
    ///   - a plain transform that gets squashed (for a simple stand-in figure)
    /// </summary>
    public sealed class LipSyncDriver : MonoBehaviour
    {
        [SerializeField] VoiceLinePlayer voice;

        [Header("Targets (any combination)")]
        [Tooltip("A face with named blendshapes (Character Creator style). The usual rigged-head setup.")]
        [SerializeField] FaceRig faceRig;

        [Tooltip("Shape that opens the jaw, and how far it opens at full volume (0..1).")]
        [SerializeField] string jawShape = "Jaw_Open";
        [SerializeField, Range(0f, 1f)] float jawMaxWeight = 0.35f;

        [Tooltip("Shape that rounds and parts the lips, and how far at full volume (0..1).")]
        [SerializeField] string openShape = "V_Open";
        [SerializeField, Range(0f, 1f)] float openMaxWeight = 0.55f;

        [Tooltip("Lip shapes that are mixed in over time so the mouth does not make the same shape every syllable.")]
        [SerializeField] string[] varietyShapes = { "V_Wide", "V_Tight_O" };
        [SerializeField, Range(0f, 1f)] float varietyMaxWeight = 0.4f;
        [SerializeField] float varietyHz = 3.1f;

        [SerializeField] SkinnedMeshRenderer face;
        [Tooltip("Index of the mouth-open blendshape, or -1 for none.")]
        [SerializeField] int mouthBlendShape = -1;

        [SerializeField] Transform jaw;
        [SerializeField] float jawOpenDegrees = 16f;

        [Tooltip("The jaw's hinge, in the jaw bone's own space. Zero means its local X axis. A model's bones are rarely " +
                 "oriented the same way, so the scene builder works this out rather than guessing.")]
        [SerializeField] Vector3 jawAxis;

        [SerializeField] Transform mouth;
        [SerializeField, Range(0.05f, 1f)] float mouthClosedScaleY = 0.15f;

        [Header("Feel")]
        [Tooltip("How strongly loudness opens the mouth.")]
        [SerializeField] float gain = 7f;
        [SerializeField] float openSeconds = 0.05f;
        [SerializeField] float closeSeconds = 0.10f;

        [Tooltip("Speech never holds the mouth perfectly still, so add a small steady flutter.")]
        [SerializeField, Range(0f, 0.5f)] float flutter = 0.15f;
        [SerializeField] float flutterHz = 6.5f;

        const int Window = 512;

        readonly float[] _samples = new float[256];
        float[] _clipSamples;
        Quaternion _jawClosed;
        Vector3 _mouthScale;
        float _open;

        /// <summary>0 = closed, 1 = fully open. Read by anything else that wants to react to speech.</summary>
        public float Openness => _open;

        void Awake()
        {
            if (voice == null) voice = FindFirstObjectByType<VoiceLinePlayer>();
            if (jaw != null) _jawClosed = jaw.localRotation;
            if (mouth != null) _mouthScale = mouth.localScale;
        }

        void Update()
        {
            var target = Loudness();

            if (target > 0f)
            {
                target *= 1f + flutter * Mathf.Sin(Time.time * flutterHz * Mathf.PI * 2f);
                target = Mathf.Clamp01(target);
            }

            // Open quickly, close a little slower: a mouth that snaps shut between
            // every syllable looks like a puppet.
            var seconds = target > _open ? openSeconds : closeSeconds;
            _open = Mathf.MoveTowards(_open, target, Time.deltaTime / Mathf.Max(0.001f, seconds));

            Apply();
        }

        float Loudness()
        {
            var source = voice != null ? voice.Source : null;
            if (source == null || !source.isPlaying || source.clip == null) return 0f;

            var rms = RmsFromClip(source);

            // Compressed or streamed clips cannot be read directly; fall back to
            // what the source is outputting, which is quieter when she is far away
            // but is better than a frozen mouth.
            if (rms < 0f)
            {
                source.GetOutputData(_samples, 0);
                var sum = 0f;
                foreach (var s in _samples) sum += s * s;
                rms = Mathf.Sqrt(sum / _samples.Length);
            }

            return Mathf.Clamp01(rms * gain);
        }

        /// <summary>
        /// Loudness of the audio at the current playback position, read from the
        /// clip itself rather than from the speaker output.
        ///
        /// The output data is what the listener would hear, so it falls to
        /// silence with distance and with volume. A professor whose mouth stops
        /// moving because you leaned back from her would look broken, and the
        /// mouth should follow what she is saying, not how far away you are.
        /// Returns -1 when the clip cannot be read.
        /// </summary>
        float RmsFromClip(AudioSource source)
        {
            var clip = source.clip;
            if (clip.loadState != AudioDataLoadState.Loaded || clip.samples < Window) return -1f;

            var needed = Window * clip.channels;
            if (_clipSamples == null || _clipSamples.Length != needed) _clipSamples = new float[needed];

            var start = Mathf.Clamp(source.timeSamples, 0, clip.samples - Window);

            if (!clip.GetData(_clipSamples, start)) return -1f;

            var sum = 0f;
            foreach (var s in _clipSamples) sum += s * s;
            return Mathf.Sqrt(sum / _clipSamples.Length);
        }

        void Apply()
        {
            if (faceRig != null) ApplyToFaceRig();

            if (face != null && mouthBlendShape >= 0)
                face.SetBlendShapeWeight(mouthBlendShape, _open * 100f);

            if (jaw != null)
            {
                var axis = jawAxis.sqrMagnitude > 0.0001f ? jawAxis.normalized : Vector3.right;
                jaw.localRotation = _jawClosed * Quaternion.AngleAxis(jawOpenDegrees * _open, axis);
            }

            if (mouth != null)
            {
                var scale = _mouthScale;
                scale.y = Mathf.Lerp(_mouthScale.y * mouthClosedScaleY, _mouthScale.y, _open);
                mouth.localScale = scale;
            }
        }

        /// <summary>
        /// Jaw and lips open with the volume, and one of a few other lip shapes is
        /// blended in on a slow wander. Not real visemes, which would need the sounds
        /// worked out, but enough that the mouth is never making the same shape twice
        /// in a row, which is what separates talking from a flapping jaw.
        /// </summary>
        void ApplyToFaceRig()
        {
            faceRig.Set(jawShape, _open * jawMaxWeight);
            faceRig.Set(openShape, _open * openMaxWeight);

            for (var i = 0; i < varietyShapes.Length; i++)
            {
                // Each shape wanders on its own phase, so they take turns rather than moving together.
                var wander = Mathf.PerlinNoise(Time.time * varietyHz, i * 17.3f);
                faceRig.Set(varietyShapes[i], _open * varietyMaxWeight * wander);
            }
        }
    }
}

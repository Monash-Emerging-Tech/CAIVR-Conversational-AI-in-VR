using System.Collections.Generic;
using CAIVR.Dialogue;
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
    /// What shape the mouth makes is taken from the words. The line she is saying is known,
    /// so its vowels are listed in order (ah, eh, ee, oh/oo, with a lip closure before p, b
    /// and m and a lip-to-teeth touch for f and v), and each beat of loudness in the audio
    /// moves on to the next one. That is not real phoneme alignment, but the right shapes
    /// come in the right order and at the speed she is actually speaking, which is what
    /// separates a mouth that talks from a jaw that flaps.
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
        [SerializeField] ConversationRunner runner;

        [Header("Targets (any combination)")]
        [Tooltip("A face with named blendshapes (Character Creator style). The usual rigged-head setup.")]
        [SerializeField] FaceRig faceRig;

        [Tooltip("Shape that opens the jaw, and how far it opens at full volume (0..1).")]
        [SerializeField] string jawShape = "Jaw_Open";
        [SerializeField, Range(0f, 1f)] float jawMaxWeight = 0.5f;

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
        [SerializeField] float jawOpenDegrees = 16f;      // overridden by the scene builder

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

        // Mouth shapes, as the amount of each facial shape at full volume. Character Creator's own names.
        const string JawOpen = "Jaw_Open";
        const string VOpen = "V_Open";
        const string VWide = "V_Wide";
        const string VRound = "V_Tight_O";
        const string VDental = "V_Dental_Lip";
        const string VExplosive = "V_Explosive";

        enum Vowel { Ah, Eh, Ee, Oh, Oo }

        struct Syllable
        {
            public Vowel Vowel;
            public bool Closure;       // starts with p, b or m: the lips meet
            public bool Dental;        // starts with f or v: lip to teeth
        }

        // How wide the jaw and each shape go for each vowel, 0..1.
        static void Shape(Vowel v, out float jaw, out float open, out float wide, out float round)
        {
            switch (v)
            {
                case Vowel.Ah: jaw = 1.00f; open = 0.80f; wide = 0.00f; round = 0.00f; break;
                case Vowel.Eh: jaw = 0.65f; open = 0.35f; wide = 0.45f; round = 0.00f; break;
                case Vowel.Ee: jaw = 0.35f; open = 0.10f; wide = 0.85f; round = 0.00f; break;
                case Vowel.Oh: jaw = 0.65f; open = 0.30f; wide = 0.00f; round = 0.75f; break;
                default:       jaw = 0.40f; open = 0.05f; wide = 0.00f; round = 1.00f; break;      // Oo
            }
        }

        readonly List<Syllable> _syllables = new List<Syllable>();
        int _syllable;
        float _slowLoud;
        float _lastBeat = -1f;
        float _closure;                // brief lip closure at the start of a p, b or m syllable
        float _dental;

        // The mouth's current shape, eased towards the current syllable's.
        float _jawNow, _openNow, _wideNow, _roundNow;
        float _jawV, _openV, _wideV, _roundV;

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
            if (runner == null) runner = FindFirstObjectByType<ConversationRunner>();
            if (jaw != null) _jawClosed = jaw.localRotation;
            if (mouth != null) _mouthScale = mouth.localScale;

            if (runner != null) runner.ProfessorLine += OnLine;
        }

        void OnDestroy()
        {
            if (runner != null) runner.ProfessorLine -= OnLine;
        }

        // --- reading the words -----------------------------------------------

        void OnLine(string line)
        {
            _syllables.Clear();
            _syllable = 0;
            if (string.IsNullOrEmpty(line)) return;

            var text = line.ToLowerInvariant();
            var closure = false;
            var dental = false;

            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];

                if (!IsVowel(c))
                {
                    // A p, b or m, or an f or v, in front of a vowel shapes how that vowel starts.
                    if (c == 'p' || c == 'b' || c == 'm') { closure = true; dental = false; }
                    else if (c == 'f' || c == 'v') { dental = true; closure = false; }
                    else if (!char.IsLetter(c)) { /* spaces and punctuation do not break the pending consonant */ }
                    else { closure = false; dental = false; }
                    continue;
                }

                // One syllable per run of vowels: "ee" in "meet" is one beat, not two.
                var j = i;
                while (j + 1 < text.Length && IsVowel(text[j + 1])) j++;

                _syllables.Add(new Syllable { Vowel = VowelFor(text, i, j), Closure = closure, Dental = dental });
                closure = false;
                dental = false;
                i = j;
            }
        }

        static bool IsVowel(char c) => c == 'a' || c == 'e' || c == 'i' || c == 'o' || c == 'u' || c == 'y';

        static Vowel VowelFor(string text, int from, int to)
        {
            var run = text.Substring(from, to - from + 1);

            if (run.Contains("oo") || run.Contains("ou") || run.Contains("ow") || run.Contains("u")) return Vowel.Oo;
            if (run.Contains("o")) return Vowel.Oh;
            if (run.Contains("ee") || run.Contains("ea") || run.Contains("ie") || run == "i" || run == "y") return Vowel.Ee;
            if (run.Contains("e")) return Vowel.Eh;
            return Vowel.Ah;
        }

        void Update()
        {
            var loud = Loudness();
            DetectBeat(loud);

            var target = loud;

            // Without the words, a steady flutter stops the mouth holding one shape. With them, the
            // syllable sequence already does that, so the flutter is only a hint.
            var flutterAmount = _syllables.Count > 0 ? flutter * 0.4f : flutter;
            if (target > 0f)
            {
                target *= 1f + flutterAmount * Mathf.Sin(Time.time * flutterHz * Mathf.PI * 2f);
                target = Mathf.Clamp01(target);
            }

            // Open quickly, close a little slower: a mouth that snaps shut between
            // every syllable looks like a puppet.
            var seconds = target > _open ? openSeconds : closeSeconds;
            _open = Mathf.MoveTowards(_open, target, Time.deltaTime / Mathf.Max(0.001f, seconds));

            EaseShape();
            Apply();
        }

        // --- following the beats of the speech -------------------------------

        /// <summary>
        /// Each burst of loudness is a syllable, and each syllable takes the next shape from the words.
        /// </summary>
        void DetectBeat(float loud)
        {
            _slowLoud = Mathf.Lerp(_slowLoud, loud, 1f - Mathf.Exp(-Time.deltaTime / 0.14f));

            if (loud < 0.08f || _syllables.Count == 0) return;
            if (loud - _slowLoud < 0.05f || Time.time - _lastBeat < 0.11f) return;

            _lastBeat = Time.time;

            var syllable = _syllables[Mathf.Min(_syllable, _syllables.Count - 1)];
            _syllable++;

            Shape(syllable.Vowel, out _jawGoal, out _openGoal, out _wideGoal, out _roundGoal);
            _closure = syllable.Closure ? 1f : 0f;
            _dental = syllable.Dental ? 1f : 0f;
        }

        float _jawGoal, _openGoal, _wideGoal, _roundGoal;

        /// <summary>The mouth glides to each new shape rather than snapping to it.</summary>
        void EaseShape()
        {
            const float seconds = 0.05f;
            _jawNow = Mathf.SmoothDamp(_jawNow, _jawGoal, ref _jawV, seconds);
            _openNow = Mathf.SmoothDamp(_openNow, _openGoal, ref _openV, seconds);
            _wideNow = Mathf.SmoothDamp(_wideNow, _wideGoal, ref _wideV, seconds);
            _roundNow = Mathf.SmoothDamp(_roundNow, _roundGoal, ref _roundV, seconds);

            // The lip closure and the lip-to-teeth touch last only the start of a syllable.
            _closure = Mathf.MoveTowards(_closure, 0f, Time.deltaTime / 0.09f);
            _dental = Mathf.MoveTowards(_dental, 0f, Time.deltaTime / 0.12f);
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
                // A wide vowel drops the jaw further than a closed one.
                var reach = _syllables.Count > 0 ? Mathf.Lerp(0.55f, 1f, _jawNow) : 1f;
                jaw.localRotation = _jawClosed * Quaternion.AngleAxis(jawOpenDegrees * _open * reach, axis);
            }

            if (mouth != null)
            {
                var scale = _mouthScale;
                scale.y = Mathf.Lerp(_mouthScale.y * mouthClosedScaleY, _mouthScale.y, _open);
                mouth.localScale = scale;
            }
        }

        /// <summary>
        /// The mouth for the vowel she is on: jaw, lip opening, spread or rounding, scaled by how loud she
        /// is so it closes in the pauses, plus the brief lip closure or lip-to-teeth touch of the consonant
        /// that began the syllable.
        /// </summary>
        void ApplyVowels()
        {
            var openness = Mathf.Clamp01(_open * 1.15f);

            faceRig.Mix(jawShape, openness * _jawNow * jawMaxWeight);
            faceRig.Mix(openShape, openness * _openNow * openMaxWeight);
            faceRig.Mix(VWide, openness * _wideNow * 0.7f);
            faceRig.Mix(VRound, openness * _roundNow * 0.8f);

            var onset = Mathf.Min(1f, _open * 4f);
            faceRig.Mix(VExplosive, _closure * 0.85f * onset);
            faceRig.Mix(VDental, _dental * 0.7f * onset);
        }

        /// <summary>
        /// Used when the words are not known. Jaw and lips open with the volume, and one of a few other lip shapes is
        /// blended in on a slow wander. Not real visemes, which would need the sounds
        /// worked out, but enough that the mouth is never making the same shape twice
        /// in a row, which is what separates talking from a flapping jaw.
        /// </summary>
        void ApplyToFaceRig()
        {
            if (_syllables.Count > 0)
            {
                ApplyVowels();
                return;
            }

            faceRig.Mix(jawShape, _open * jawMaxWeight);
            faceRig.Mix(openShape, _open * openMaxWeight);

            for (var i = 0; i < varietyShapes.Length; i++)
            {
                // Each shape wanders on its own phase, so they take turns rather than moving together.
                var wander = Mathf.PerlinNoise(Time.time * varietyHz, i * 17.3f);
                faceRig.Mix(varietyShapes[i], _open * varietyMaxWeight * wander);
            }
        }
    }
}

using UnityEngine;

namespace CAIVR.VR
{
    /// <summary>
    /// How the professor's face and head react to the conversation.
    ///
    /// A face with nothing happening on it reads as blank however good the model is. Research on
    /// virtual characters is consistent that a still upper face (brows, lids, forehead) is what pushes
    /// a realistic character into the uncanny, so the upper face is never still, and what it does
    /// follows the moment:
    ///   - waiting for you she looks pleasant and open, with a real smile that reaches the eyes
    ///   - when it is your turn her brows lift ("go on"), then settle into attentive, and she nods and
    ///     gives small smiles now and then
    ///   - while she speaks her brows move continually, lift on the words she stresses and flash at
    ///     the start of a thought, and her smile comes and goes with what she is saying
    ///   - while she works out an answer her brows draw together a little and her lips firm up
    ///   - at the end she smiles
    ///
    /// The amounts are measured against this model: its brow shapes at full weight are mild, so
    /// the movement here uses a good part of their range. It adds to the resting expression held by
    /// <see cref="FaceRig"/>, and head movements go through <see cref="ProfessorGaze"/> which owns
    /// the head.
    /// </summary>
    public sealed class ProfessorExpression : MonoBehaviour
    {
        [SerializeField] ProfessorMood mood;
        [SerializeField] FaceRig face;
        [SerializeField] ProfessorGaze gaze;

        [Header("Feel")]
        [Tooltip("How quickly the face settles into a new expression, in seconds. Faces change slowly; a quick change looks like a switch.")]
        [SerializeField] float settleSeconds = 0.35f;

        [Tooltip("Scales every expression. 1 is as designed, 0 leaves only the resting face.")]
        [SerializeField, Range(0f, 1.5f)] float strength = 1f;

        [Tooltip("Resting head tilt in degrees. A slight tilt reads as interested; a dead level head reads as a mannequin.")]
        [SerializeField] float restingTilt = 2f;

        // Shapes this adds to (Character Creator names).
        const string SmileL = "Mouth_Smile_L", SmileR = "Mouth_Smile_R";
        const string CheekL = "Cheek_Raise_L", CheekR = "Cheek_Raise_R";
        const string SquintL = "Eye_Squint_L", SquintR = "Eye_Squint_R";
        const string DimpleL = "Mouth_Dimple_L", DimpleR = "Mouth_Dimple_R";
        const string InnerL = "Brow_Raise_Inner_L", InnerR = "Brow_Raise_Inner_R";
        const string OuterL = "Brow_Raise_Outer_L", OuterR = "Brow_Raise_Outer_R";
        const string CompressL = "Brow_Compress_L", CompressR = "Brow_Compress_R";
        const string PressL = "Mouth_Press_L", PressR = "Mouth_Press_R";
        const string SwallowDown = "Neck_Swallow_Down";

        // The smoothed values that actually go on the face.
        float _smile, _smileV;
        float _inner, _innerV;
        float _outer, _outerV;
        float _compress, _compressV;
        float _press, _pressV;
        float _droop, _droopV;
        float _tilt, _tiltV;

        float _accent;                   // a stressed word: brows up, eyes a touch wider
        float _flash;                    // a quick brow flash: a change of turn, the start of a thought, a nod
        float _beam;                     // a small smile that goes with a nod
        float _seed;
        float _tiltSign;
        float _asymmetry;                // her left and right brows are never quite level

        float _nodElapsed = -1f, _nodLength, _nodDepth;
        int _nodsLeft;
        float _nextNod;

        float _swallowElapsed = -1f;
        float _nextSwallow;

        void Awake()
        {
            if (mood == null) mood = GetComponent<ProfessorMood>();
            if (face == null) face = GetComponentInChildren<FaceRig>();
            if (gaze == null) gaze = GetComponent<ProfessorGaze>();

            _seed = Random.value * 100f;
            _tiltSign = Random.value < 0.5f ? -1f : 1f;
            _asymmetry = Random.Range(0.15f, 0.3f) * (Random.value < 0.5f ? -1f : 1f);
            _nextNod = Time.time + Random.Range(2f, 4f);
            _nextSwallow = Time.time + Random.Range(10f, 25f);

            if (mood != null)
            {
                mood.PhaseChanged += OnPhaseChanged;
                mood.EmphasisHit += OnEmphasis;
                mood.PhraseStarted += OnPhraseStarted;
                mood.PhraseEnded += OnPhraseEnded;
            }
        }

        void OnDestroy()
        {
            if (mood == null) return;
            mood.PhaseChanged -= OnPhaseChanged;
            mood.EmphasisHit -= OnEmphasis;
            mood.PhraseStarted -= OnPhraseStarted;
            mood.PhraseEnded -= OnPhraseEnded;
        }

        void OnPhaseChanged(ProfessorPhase phase)
        {
            // A brow flash on the change of turn, the way people signal "your turn" or "right".
            if (phase == ProfessorPhase.Listening || phase == ProfessorPhase.Ended) _flash = 1f;

            if (phase == ProfessorPhase.Ended) StartNod(6f, 0.9f, 0);
            if (phase == ProfessorPhase.Listening) _nextNod = Time.time + Random.Range(2.5f, 4.5f);
        }

        void OnEmphasis()
        {
            _accent = Mathf.Max(_accent, 0.6f + 0.4f * mood.Emphasis);
            if (Random.value < 0.65f) StartNod(1.6f + 2f * mood.Emphasis, 0.45f, 0);
        }

        void OnPhraseStarted()
        {
            // Many people lift their brows as they begin a thought.
            if (mood.Phase == ProfessorPhase.Speaking && Random.value < 0.55f) _flash = Mathf.Max(_flash, 0.55f);
        }

        void OnPhraseEnded()
        {
            if (mood.Phase == ProfessorPhase.Speaking && Random.value < 0.3f) StartNod(2.5f, 0.55f, 0);
        }

        void StartNod(float degrees, float seconds, int extra)
        {
            if (_nodElapsed >= 0f) return;

            _nodElapsed = 0f;
            _nodLength = seconds;
            _nodDepth = degrees;
            _nodsLeft = extra;

            // A nod is a small social signal, and it comes with a flicker of the brows and a hint of a smile.
            _flash = Mathf.Max(_flash, 0.35f);
            _beam = Mathf.Max(_beam, 0.8f);
        }

        void Update()
        {
            if (face == null) return;

            var phase = mood != null ? mood.Phase : ProfessorPhase.Waiting;
            var t = Time.time + _seed;
            var dt = Time.deltaTime;

            // Slow wandering numbers, so no two moments on her face are the same. The quicker one is
            // the constant small brow movement of someone mid-conversation.
            var slow = Noise(t * 0.17f, 0.5f);
            var slow2 = Noise(t * 0.23f, 4.2f);
            var quick = Noise(t * 0.65f, 8.8f);

            float smile, inner, outer, compress, press, droop, tilt;

            switch (phase)
            {
                case ProfessorPhase.Speaking:
                    smile = 0.20f + 0.22f * slow;
                    outer = 0.10f + 0.16f * quick + 0.50f * _accent + 0.30f * _flash;
                    inner = 0.06f + 0.08f * slow2 + 0.26f * _accent + 0.16f * _flash;
                    compress = 0f;
                    press = 0f;
                    droop = 0.19f - 0.07f * _accent;            // eyes a touch more open on a stressed word
                    tilt = restingTilt * 0.7f;
                    break;

                case ProfessorPhase.Listening:
                    smile = 0.28f + 0.14f * slow + 0.20f * _beam;
                    outer = 0.20f + 0.10f * slow2 + 0.45f * _flash;
                    inner = 0.12f + 0.08f * slow + 0.25f * _flash;
                    compress = 0f;
                    press = 0.03f;
                    droop = 0.17f;
                    tilt = restingTilt * 1.5f;
                    break;

                case ProfessorPhase.Thinking:
                    smile = 0.04f;
                    outer = 0.06f + 0.05f * slow;
                    inner = 0.42f + 0.06f * slow2;
                    compress = 0.22f;
                    press = 0.25f;
                    droop = 0.22f;
                    tilt = restingTilt * 2.2f;
                    break;

                case ProfessorPhase.Ended:
                    smile = 0.55f + 0.08f * slow;
                    outer = 0.22f + 0.30f * _flash;
                    inner = 0.06f;
                    compress = 0f;
                    press = 0f;
                    droop = 0.20f;
                    tilt = restingTilt;
                    break;

                default:        // Waiting: pleasant and open
                    smile = 0.42f + 0.10f * slow;
                    outer = 0.14f + 0.10f * slow2;
                    inner = 0.08f + 0.05f * slow;
                    compress = 0f;
                    press = 0f;
                    droop = 0.20f;
                    tilt = restingTilt;
                    break;
            }

            _accent = Mathf.MoveTowards(_accent, 0f, dt / 0.6f);
            _flash = Mathf.MoveTowards(_flash, 0f, dt / 0.9f);
            _beam = Mathf.MoveTowards(_beam, 0f, dt / 1.3f);

            _smile = Mathf.SmoothDamp(_smile, smile * strength, ref _smileV, settleSeconds);
            _inner = Mathf.SmoothDamp(_inner, inner * strength, ref _innerV, settleSeconds * 0.6f);
            _outer = Mathf.SmoothDamp(_outer, outer * strength, ref _outerV, settleSeconds * 0.6f);
            _compress = Mathf.SmoothDamp(_compress, compress * strength, ref _compressV, settleSeconds);
            _press = Mathf.SmoothDamp(_press, press * strength, ref _pressV, settleSeconds);
            _droop = Mathf.SmoothDamp(_droop, droop, ref _droopV, settleSeconds);
            _tilt = Mathf.SmoothDamp(_tilt, tilt, ref _tiltV, settleSeconds * 2f);

            Apply();
            Swallow(dt, phase);
            HeadLanguage(dt, phase, t);
        }

        void Apply()
        {
            // A real smile is not only the mouth: the cheeks rise, the lower lids tighten and dimples
            // show. Without those the smile looks painted on and the eyes look blank above it. Her left
            // and right sides are not perfectly matched either.
            face.Mix(SmileL, _smile * (1f + _asymmetry * 0.4f));
            face.Mix(SmileR, _smile * (1f - _asymmetry * 0.4f));
            face.MixPair(CheekL, CheekR, 0.10f + _smile * 0.55f);
            face.MixPair(SquintL, SquintR, 0.05f + _smile * 0.32f);
            face.MixPair(DimpleL, DimpleR, _smile * 0.30f);

            face.Mix(InnerL, _inner * (1f + _asymmetry)); face.Mix(InnerR, _inner * (1f - _asymmetry));
            face.Mix(OuterL, _outer * (1f - _asymmetry)); face.Mix(OuterR, _outer * (1f + _asymmetry));
            face.MixPair(CompressL, CompressR, _compress);

            face.MixPair(PressL, PressR, _press);

            face.EyelidDroop = _droop;
        }

        /// <summary>A swallow now and then, which nobody notices and everybody would notice the absence of.</summary>
        void Swallow(float dt, ProfessorPhase phase)
        {
            if (_swallowElapsed < 0f)
            {
                if (Time.time >= _nextSwallow && phase != ProfessorPhase.Speaking) _swallowElapsed = 0f;
                return;
            }

            _swallowElapsed += dt;
            var u = _swallowElapsed / 0.8f;
            var down = Mathf.Sin(Mathf.Clamp01(u / 0.5f) * Mathf.PI * 0.5f) * (1f - Mathf.Clamp01((u - 0.5f) / 0.5f));
            face.Mix(SwallowDown, 0.7f * down);

            if (u >= 1f)
            {
                _swallowElapsed = -1f;
                _nextSwallow = Time.time + Random.Range(18f, 40f);
            }
        }

        void HeadLanguage(float dt, ProfessorPhase phase, float t)
        {
            if (gaze == null) return;

            // Listeners nod. Every so often, a small one, sometimes two.
            if (phase == ProfessorPhase.Listening && _nodElapsed < 0f && Time.time >= _nextNod)
            {
                StartNod(Random.Range(3f, 5f), 0.6f, Random.value < 0.35f ? 1 : 0);
                _nextNod = Time.time + Random.Range(3.5f, 8f);
            }

            var nod = 0f;
            if (_nodElapsed >= 0f)
            {
                _nodElapsed += dt;
                var u = _nodElapsed / _nodLength;

                // Down quickly, back up more slowly.
                var shape = u < 0.35f ? Mathf.SmoothStep(0f, 1f, u / 0.35f) : 1f - Mathf.SmoothStep(0f, 1f, (u - 0.35f) / 0.65f);
                nod = _nodDepth * shape;

                if (u >= 1f)
                {
                    _nodElapsed = -1f;
                    if (_nodsLeft > 0) { _nodsLeft--; StartNod(_nodDepth * 0.7f, _nodLength * 0.85f, _nodsLeft); }
                }
            }

            gaze.AddHeadOffset(nod, 0f, _tilt * _tiltSign + Noise(t * 0.13f, 9.1f) * 0.6f);
        }

        static float Noise(float x, float y) => Mathf.PerlinNoise(x, y);
    }
}

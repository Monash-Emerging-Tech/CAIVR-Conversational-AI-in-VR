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
    ///   - waiting for you she looks pleasant and open
    ///   - when it is your turn her brows lift ("go on"), then settle into attentive, and she nods and
    ///     now and then gives a small smile
    ///   - while she speaks her brows move continually and lift on the words she stresses and flash at
    ///     the start of a thought. She does NOT keep smiling: see below
    ///   - while she works out an answer her brows draw together a little and her lips firm up
    ///   - at the end she smiles
    ///
    /// Smiles are events, not a state. People smile about once every half minute when talking, a real
    /// smile lasts roughly half a second to four, it comes on slowly and fades more slowly still, the
    /// eyes join in, and a blink tends to come as it ends (smile and blink dynamics studies: genuine
    /// smiles have the longer onsets and offsets, and abrupt ones read as fake). A smile held through a
    /// whole sentence is what reads as a mask, and with the mouth moving it stretches the lips into a
    /// long flat grin. So between smiles her mouth sits in a soft, nearly neutral line, and a smile that
    /// does happen while she is mid-word is mostly carried by the cheeks and eyes rather than the mouth.
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

        // The smile event in progress, if any.
        bool _smiling;
        float _smileTime, _smileOnset, _smileHold, _smileOffset, _smilePeak;
        bool _smileBlinked;
        float _nextSmileAllowed;         // no new smile before this time, so they stay occasional
        float _nextIdleSmile;
        float _smileEyes, _smileEyesV;   // how far the eyes are smiling, which outlasts the mouth
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
            _nextIdleSmile = Time.time + Random.Range(3f, 8f);

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

            if (phase == ProfessorPhase.Ended)
            {
                StartNod(6f, 0.9f, 0);
                TriggerSmile(0.55f, 2.6f, force: true);          // a warm goodbye, held a little
            }

            if (phase == ProfessorPhase.Listening) _nextNod = Time.time + Random.Range(2.5f, 4.5f);

            // The first words of the conversation are a greeting, and a greeting has a smile in it.
            if (phase == ProfessorPhase.Speaking && _previousPhase == ProfessorPhase.Waiting)
                TriggerSmile(0.42f, 1.2f, force: true);

            _previousPhase = phase;
        }

        ProfessorPhase _previousPhase = ProfessorPhase.Waiting;

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
            if (mood.Phase != ProfessorPhase.Speaking) return;

            if (Random.value < 0.3f) StartNod(2.5f, 0.55f, 0);

            // A smile at the end of a sentence, now and then, not through it.
            if (Random.value < 0.22f) TriggerSmile(Random.Range(0.28f, 0.4f), Random.Range(0.3f, 0.8f));
        }

        /// <summary>
        /// Starts a smile that comes on slowly, holds, and fades more slowly still. A new smile is ignored
        /// while one is in progress or too soon after the last, unless forced (a greeting, a goodbye).
        /// </summary>
        void TriggerSmile(float peak, float holdSeconds, bool force = false)
        {
            if (!force && (_smiling || Time.time < _nextSmileAllowed)) return;

            _smiling = true;
            _smileTime = 0f;
            _smilePeak = peak;
            _smileOnset = Random.Range(0.45f, 0.7f);
            _smileHold = holdSeconds;
            _smileOffset = Random.Range(0.9f, 1.4f);
            _smileBlinked = false;
        }

        /// <summary>Where the smile event is, 0..peak.</summary>
        float AdvanceSmile(float dt)
        {
            if (!_smiling) return 0f;

            _smileTime += dt;
            var end = _smileOnset + _smileHold;

            float amount;
            if (_smileTime < _smileOnset) amount = Mathf.SmoothStep(0f, 1f, _smileTime / _smileOnset);
            else if (_smileTime < end) amount = 1f;
            else
            {
                amount = 1f - Mathf.SmoothStep(0f, 1f, (_smileTime - end) / _smileOffset);

                // Blinks tend to come as a smile lets go.
                if (!_smileBlinked)
                {
                    _smileBlinked = true;
                    if (face != null && Random.value < 0.6f) face.Blink();
                }
            }

            if (_smileTime >= end + _smileOffset)
            {
                _smiling = false;
                _nextSmileAllowed = Time.time + Random.Range(6f, 14f);
            }

            return amount * _smilePeak;
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
            if (Random.value < 0.35f) TriggerSmile(Random.Range(0.25f, 0.38f), Random.Range(0.3f, 0.7f));
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
                    smile = 0.02f + 0.03f * slow;                 // mid-sentence the mouth is busy talking; smiles are events
                    outer = 0.10f + 0.16f * quick + 0.50f * _accent + 0.30f * _flash;
                    inner = 0.06f + 0.08f * slow2 + 0.26f * _accent + 0.16f * _flash;
                    compress = 0f;
                    press = 0f;
                    droop = 0.19f - 0.07f * _accent;            // eyes a touch more open on a stressed word
                    tilt = restingTilt * 0.7f;
                    break;

                case ProfessorPhase.Listening:
                    smile = 0.05f + 0.04f * slow;
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
                    smile = 0.10f + 0.04f * slow;                 // the goodbye smile is an event on top of this
                    outer = 0.22f + 0.30f * _flash;
                    inner = 0.06f;
                    compress = 0f;
                    press = 0f;
                    droop = 0.20f;
                    tilt = restingTilt;
                    break;

                default:        // Waiting: pleasant and open
                    smile = 0.07f + 0.04f * slow;
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

            // An occasional small smile while she waits or listens. Rare, and never while she is mid-sentence.
            if (!_smiling && Time.time >= _nextIdleSmile && Time.time >= _nextSmileAllowed
                && (phase == ProfessorPhase.Waiting || phase == ProfessorPhase.Listening))
            {
                TriggerSmile(Random.Range(0.30f, 0.42f), Random.Range(0.5f, 1.2f));
                _nextIdleSmile = Time.time + Random.Range(14f, 30f);
            }

            var happy = AdvanceSmile(dt);

            // The mouth gives way to the words: with the lips moving, a corner pull would only stretch them into
            // a long flat grin. The cheeks and eyes keep smiling, which is how people smile through speech.
            var talking = mood != null ? Mathf.Clamp01(mood.Energy * 1.3f) : 0f;
            var mouthSmile = (smile + happy) * Mathf.Lerp(1f, 0.35f, talking);

            _smile = Mathf.SmoothDamp(_smile, mouthSmile * strength, ref _smileV, settleSeconds * 0.6f);
            _smileEyes = Mathf.SmoothDamp(_smileEyes, (smile + happy) * strength, ref _smileEyesV, settleSeconds * 0.8f);
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
            face.Mix(SmileL, _smile * (1f + _asymmetry * 0.25f));
            face.Mix(SmileR, _smile * (1f - _asymmetry * 0.25f));
            face.MixPair(CheekL, CheekR, 0.05f + _smileEyes * 0.60f);
            face.MixPair(SquintL, SquintR, 0.05f + _smileEyes * 0.38f);
            face.MixPair(DimpleL, DimpleR, _smile * 0.25f);

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

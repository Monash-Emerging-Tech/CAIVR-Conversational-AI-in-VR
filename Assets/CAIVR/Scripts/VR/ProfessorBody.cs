using UnityEngine;

namespace CAIVR.VR
{
    /// <summary>
    /// The professor's torso and arms: breathing, posture, and the hand movements of someone talking.
    ///
    /// A seated figure with locked shoulders and hands laid flat on the table is the other half of what
    /// reads as lifeless, along with a fixed stare. So:
    ///   - she breathes: the chest rises and falls, the shoulders lift a touch, and she takes a visible
    ///     breath before she speaks
    ///   - she leans in a little when she is listening, and her weight drifts slowly
    ///   - when she speaks her hands make the small movements of someone talking at a table: a
    ///     wrist lifting, a palm rolling over, with the forearms staying down, and a slight beat
    ///     on the stressed words
    ///   - between gestures her hands never go dead still: they settle, turn a little and the fingers
    ///     move
    ///   - when she is not talking, a hand settles now and then
    ///
    /// Everything is a movement away from the resting pose saved in the scene, so the scene builder
    /// decides how she sits and this only brings it to life. The hands are kept above the table.
    /// </summary>
    [DefaultExecutionOrder(50)]        // before the gaze, which turns the head on top of whatever the spine is doing
    public sealed class ProfessorBody : MonoBehaviour
    {
        [SerializeField] ProfessorMood mood;

        [Tooltip("World height of the table top. The hands are kept above it. Zero turns the check off.")]
        [SerializeField] float tableTopHeight;

        [Header("Posture")]
        [Tooltip("How far she leans towards the student, in degrees.")]
        [SerializeField] float leanForwardDegrees = 3.5f;
        [SerializeField] float breathDegrees = 0.8f;
        [SerializeField] float breathSeconds = 4.4f;

        [Tooltip("Slow drift of the torso in degrees as her weight shifts.")]
        [SerializeField] float swayDegrees = 0.9f;

        [Header("Hands")]
        [Tooltip("Scales every gesture. 1 is as designed.")]
        [SerializeField, Range(0f, 1.5f)] float gestureStrength = 1f;

        [Tooltip("Seconds between small hand movements while she is mid-sentence.")]
        [SerializeField] Vector2 secondsBetweenGestures = new Vector2(3.5f, 8f);

        sealed class Gesture
        {
            public ArmPose Right;
            public ArmPose Left;
            public Vector2 Hold;
        }

        static ArmPose P(float shoulder, float abduct, float lift, float spread, float turn, float wrist, float open) =>
            new ArmPose { Shoulder = shoulder, Abduct = abduct, Lift = lift, Spread = spread, Turn = turn, Wrist = wrist, Open = open };

        // Someone sitting with their forearms on a table does not raise their hands to talk. What they do is
        // small: the wrist lifts a little, a hand rolls over as a point is made, the fingers ease open. These
        // keep the forearms down and move only a few degrees.

        // The right wrist lifts and the fingers ease open a touch.
        static readonly Gesture RightWristLift = new Gesture
        {
            Right = P(0f, 0f, 3.5f, 0f, 6f, 6f, 0.25f),
            Left = P(0f, 0f, 0.5f, 0f, 1f, 0f, 0.05f),
            Hold = new Vector2(0.4f, 0.9f),
        };

        static readonly Gesture LeftWristLift = new Gesture
        {
            Right = P(0f, 0f, 0.5f, 0f, 1f, 0f, 0.05f),
            Left = P(0f, 0f, 3.5f, 0f, 6f, 6f, 0.25f),
            Hold = new Vector2(0.4f, 0.9f),
        };

        // A hand rolls over a little, palm turning towards the sky, as someone makes a point.
        static readonly Gesture RightPalmRoll = new Gesture
        {
            Right = P(0f, 0f, 2.5f, -1.5f, 18f, 3f, 0.35f),
            Left = default,
            Hold = new Vector2(0.5f, 1.0f),
        };

        static readonly Gesture LeftPalmRoll = new Gesture
        {
            Right = default,
            Left = P(0f, 0f, 2.5f, -1.5f, 18f, 3f, 0.35f),
            Hold = new Vector2(0.5f, 1.0f),
        };

        // Both hands ease a little apart and open, the smallest "so".
        static readonly Gesture EaseApart = new Gesture
        {
            Right = P(1f, 1.5f, 3f, 3.5f, 12f, 3f, 0.3f),
            Left = P(1f, 1.5f, 3f, 3.5f, 12f, 3f, 0.3f),
            Hold = new Vector2(0.4f, 0.9f),
        };

        static readonly Gesture[] Library = { RightWristLift, LeftWristLift, RightPalmRoll, LeftPalmRoll, EaseApart };

        // A hand settling at rest: a hint of lift and turn, nothing more.
        static readonly Gesture AdjustRight = new Gesture
        {
            Right = P(0f, 0f, 2f, -2f, 8f, 2f, 0.2f),
            Left = default,
            Hold = new Vector2(0.3f, 0.8f),
        };

        static readonly Gesture AdjustLeft = new Gesture
        {
            Right = default,
            Left = P(0f, 0f, 2f, -2f, 8f, 2f, 0.2f),
            Hold = new Vector2(0.3f, 0.8f),
        };

        const float Attack = 0.5f;
        const float Release = 0.8f;

        Transform _spineLow, _spineHigh;
        Quaternion _spineLowRest, _spineHighRest;
        ArmRig _right, _left;

        Gesture _gesture;
        float _gestureTime, _gestureLength;
        float _nextGesture, _nextAdjust;
        int _lastGesture = -1;

        float _fade = 1f;
        bool _fadingOut;

        float _beat;
        bool _beatRising;
        float _inhale;
        bool _inhaling;
        float _lean, _leanVelocity;
        float _seed;
        float _shoulderBiasR, _shoulderBiasL;

        /// <summary>Used by the scene builder, which knows where the table is.</summary>
        public void SetTableTop(float height) => tableTopHeight = height;

        public int GestureCount => Library.Length;

        void Awake()
        {
            if (mood == null) mood = GetComponent<ProfessorMood>();

            var bones = new BoneMap(transform);
            _spineLow = bones.Get("Spine01");
            _spineHigh = bones.Get("Spine02");
            if (_spineLow != null) _spineLowRest = _spineLow.localRotation;
            if (_spineHigh != null) _spineHighRest = _spineHigh.localRotation;

            _right = ArmRig.Create(bones, 'R', transform);
            _left = ArmRig.Create(bones, 'L', transform);

            _seed = Random.value * 100f;
            _shoulderBiasR = Random.Range(-0.5f, 0.8f);
            _shoulderBiasL = Random.Range(-0.5f, 0.8f);
            _nextGesture = Time.time + 1f;
            _nextAdjust = Time.time + Random.Range(8f, 14f);

            if (mood != null)
            {
                mood.PhaseChanged += OnPhaseChanged;
                mood.EmphasisHit += OnEmphasis;
            }
        }

        void OnDestroy()
        {
            if (mood == null) return;
            mood.PhaseChanged -= OnPhaseChanged;
            mood.EmphasisHit -= OnEmphasis;
        }

        void OnPhaseChanged(ProfessorPhase phase)
        {
            if (phase == ProfessorPhase.Speaking) _inhaling = true;        // a breath in before she starts
            if (phase != ProfessorPhase.Speaking) _fadingOut = true;       // hands come down when she stops
        }

        void OnEmphasis() => _beatRising = true;

        /// <summary>Plays one of the built-in gestures now. For testing and for anything that wants to trigger one.</summary>
        public void PlayGesture(int index, float holdSeconds = -1f)
        {
            if (index < 0 || index >= Library.Length) return;
            Begin(Library[index], holdSeconds);
        }

        void Begin(Gesture gesture, float holdSeconds = -1f)
        {
            _gesture = gesture;
            _gestureTime = 0f;

            var hold = holdSeconds >= 0f ? holdSeconds : Random.Range(gesture.Hold.x, gesture.Hold.y);
            _gestureLength = Attack + hold + Release;
            _fade = 1f;
            _fadingOut = false;
        }

        void Update()
        {
            var dt = Time.deltaTime;
            var phase = mood != null ? mood.Phase : ProfessorPhase.Waiting;

            // Both of these ramp in over a few frames rather than appearing at full size, which would show as a twitch.
            if (_beatRising)
            {
                _beat += dt / 0.08f;
                if (_beat >= 1f) { _beat = 1f; _beatRising = false; }
            }
            else _beat = Mathf.MoveTowards(_beat, 0f, dt / 0.35f);

            if (_inhaling)
            {
                _inhale += dt / 0.5f;
                if (_inhale >= 1f) { _inhale = 1f; _inhaling = false; }
            }
            else _inhale = Mathf.MoveTowards(_inhale, 0f, dt / 1.1f);

            if (_gesture != null)
            {
                _gestureTime += dt;
                if (_fadingOut) _fade = Mathf.MoveTowards(_fade, 0f, dt / Release);

                if (_gestureTime >= _gestureLength || _fade <= 0f)
                {
                    _gesture = null;
                    _nextGesture = Time.time + Random.Range(secondsBetweenGestures.x, secondsBetweenGestures.y);
                }

                return;
            }

            var talking = phase == ProfessorPhase.Speaking && mood != null && mood.InPhrase;
            if (talking && Time.time >= _nextGesture)
            {
                // Never the same gesture twice running.
                int pick;
                do { pick = Random.Range(0, Library.Length); } while (pick == _lastGesture && Library.Length > 1);
                _lastGesture = pick;
                Begin(Library[pick]);
            }
            else if (!talking && Time.time >= _nextAdjust)
            {
                Begin(Random.value < 0.5f ? AdjustRight : AdjustLeft);
                _nextAdjust = Time.time + Random.Range(10f, 20f);
            }
        }

        void LateUpdate()
        {
            var t = Time.time + _seed;
            var dt = Time.deltaTime;
            var phase = mood != null ? mood.Phase : ProfessorPhase.Waiting;

            PoseTorso(t, dt, phase);
            PoseArms(t);
        }

        // --- torso -----------------------------------------------------------

        void PoseTorso(float t, float dt, ProfessorPhase phase)
        {
            // Leaning in says "I am listening"; sitting back a touch says "let me explain".
            var leanGoal = phase switch
            {
                ProfessorPhase.Listening => leanForwardDegrees * 1.5f,
                ProfessorPhase.Thinking => leanForwardDegrees * 1.1f,
                ProfessorPhase.Speaking => leanForwardDegrees * 0.8f,
                _ => leanForwardDegrees,
            };

            _lean = Mathf.SmoothDamp(_lean, leanGoal, ref _leanVelocity, 1.2f);

            if (_spineLow == null || _spineHigh == null) return;

            var right = transform.right;
            var up = transform.up;
            var forward = transform.forward;

            var breath = Breath(t);
            var rise = breath * breathDegrees + _inhale * 2.2f;      // the chest lifts and tips back as it fills

            var swayPitch = Sway(t * 0.11f, 2.1f) * swayDegrees * 0.6f;
            var swayRoll = Sway(t * 0.09f, 5.3f) * swayDegrees;
            var swayYaw = Sway(t * 0.07f, 8.7f) * swayDegrees * 0.8f;

            _spineLow.localRotation = _spineLowRest;
            _spineLow.rotation = Quaternion.AngleAxis(swayRoll, forward)
                               * Quaternion.AngleAxis(swayYaw, up)
                               * Quaternion.AngleAxis(_lean * 0.4f + swayPitch, right) * _spineLow.rotation;

            _spineHigh.localRotation = _spineHighRest;
            _spineHigh.rotation = Quaternion.AngleAxis(_lean * 0.6f - rise, right) * _spineHigh.rotation;
        }

        /// <summary>A breath, 0..1: an unhurried in and out, slightly irregular as breathing is.</summary>
        float Breath(float t)
        {
            var w = Mathf.PI * 2f / breathSeconds;
            var wave = 0.78f * Mathf.Sin(t * w) + 0.22f * Mathf.Sin(t * w * 1.7f + 1.3f);
            return wave * 0.5f + 0.5f;
        }

        static float Sway(float x, float y) => (Mathf.PerlinNoise(x, y) - 0.5f) * 2f;

        // --- arms ------------------------------------------------------------

        float Envelope()
        {
            if (_gesture == null) return 0f;

            var hold = _gestureLength - Attack - Release;

            float shape;
            if (_gestureTime < Attack) shape = Mathf.SmoothStep(0f, 1f, _gestureTime / Attack);
            else if (_gestureTime < Attack + hold) shape = 1f;
            else shape = 1f - Mathf.SmoothStep(0f, 1f, (_gestureTime - Attack - hold) / Release);

            return shape * Mathf.SmoothStep(0f, 1f, _fade);
        }

        void PoseArms(float t)
        {
            var envelope = Envelope();
            var breath = Breath(t) * 0.6f;

            PoseArm(_right, _gesture != null ? _gesture.Right : default, envelope, t, breath + _shoulderBiasR, 0f);
            PoseArm(_left, _gesture != null ? _gesture.Left : default, envelope, t, breath + _shoulderBiasL, 3.7f);
        }

        void PoseArm(ArmRig arm, in ArmPose gesturePose, float envelope, float t, float shrug, float phase)
        {
            if (arm == null) return;

            var pose = ArmPose.Lerp(default, gesturePose, envelope * gestureStrength);

            // The beat on a stressed word, strongest when the hands are already up.
            var beat = _beat * (0.35f + 0.65f * envelope) * gestureStrength;
            pose.Lift += beat * 1.2f;
            pose.Wrist += beat * 2f;

            // Never dead still: the hand settles and turns by a degree or two.
            pose.Spread += Sway(t * 0.21f + phase, 11.3f) * 1.3f;
            pose.Turn += Sway(t * 0.17f + phase, 13.7f) * 3f;
            pose.Lift += Sway(t * 0.19f + phase, 17.1f) * 0.6f;

            var fidget = Mathf.Lerp(2.2f, 0.7f, envelope);

            arm.Apply(pose, 0f, shrug, fidget, phase);

            if (tableTopHeight <= 0f) return;

            // Keep the hand off the table, whatever the pose asked for.
            var extra = 0f;
            for (var pass = 0; pass < 2; pass++)
            {
                var sink = arm.Sink(tableTopHeight + 0.002f);
                if (sink <= 0.0005f) break;

                extra += Mathf.Rad2Deg * sink / arm.Lever * 1.1f;
                arm.Apply(pose, extra, shrug, fidget, phase);
            }
        }
    }
}

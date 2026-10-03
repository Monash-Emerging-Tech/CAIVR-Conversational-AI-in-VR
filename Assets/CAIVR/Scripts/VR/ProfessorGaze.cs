using UnityEngine;

namespace CAIVR.VR
{
    /// <summary>
    /// Where the professor looks, and how her eyes, neck and head get there.
    ///
    /// A fixed stare is the single biggest thing that makes a character look dead, so this
    /// is built the way people actually look at each other:
    ///   - the eyes jump to a new point fast and the head follows a beat later and only
    ///     part of the way, so the eyes lead and the head settles after
    ///   - while she holds eye contact her eyes are never quite still: they drift between
    ///     your eyes and your mouth in tiny steps
    ///   - she glances away now and then, mostly at the start of a thought and while she is
    ///     working out what to say, and comes back to you at the end of a sentence. When
    ///     she is listening she holds your eyes much longer and only drops her gaze briefly
    ///   - the lids follow the eyes (they lower when she looks down) and a big glance
    ///     usually comes with a blink
    ///
    /// The viewer is whoever the main camera is, so it works the same on a monitor and in
    /// a headset, and follows you if you lean or turn.
    ///
    /// Other scripts that want her head to do something (nod, tilt) call
    /// <see cref="AddHeadOffset"/> each frame instead of touching the bones, so there is
    /// one owner of the head.
    /// </summary>
    [DefaultExecutionOrder(100)]       // after the body has moved the spine, before the face flushes its shapes
    public sealed class ProfessorGaze : MonoBehaviour
    {
        [SerializeField] ProfessorMood mood;
        [SerializeField] FaceRig face;

        [Tooltip("Whose eyes she looks at. Leave empty to use the main camera.")]
        [SerializeField] Transform viewer;

        [Header("Head and neck")]
        [Tooltip("How far, in degrees, her head will turn away from straight ahead to follow you.")]
        [SerializeField] float maxHeadYaw = 55f;

        [Tooltip("The head ignores targets this close to straight ahead. The eyes cover them, as yours do.")]
        [SerializeField] float headDeadZone = 5f;

        [SerializeField] float headSmoothSeconds = 0.30f;

        [Tooltip("How much of the turn the neck takes, the rest going to the head. A head that turns on its own looks like it is on a stick.")]
        [SerializeField, Range(0f, 0.9f)] float neckShare = 0.45f;

        [Tooltip("Slow drift of the head in degrees, so she is never perfectly still.")]
        [SerializeField] float idleSwayDegrees = 1.1f;

        [Header("Eyes")]
        [SerializeField] float eyeYawLimit = 32f;
        [SerializeField] float eyeLookUpLimit = 18f;
        [SerializeField] float eyeLookDownLimit = 26f;

        [Tooltip("How long the smallest eye jump takes. Larger jumps take a little longer, as real ones do (about 100 ms for 30 degrees).")]
        [SerializeField] float saccadeSeconds = 0.04f;

        [Tooltip("How far, in metres at your face, her eyes wander while she holds eye contact. Fixation is never perfectly still, " +
                 "but it is small: she is looking at your eyes, not scanning your face.")]
        [SerializeField] float eyeContactScan = 0.025f;

        [Tooltip("How much the lids follow the eyes up and down, 0..1.")]
        [SerializeField, Range(0f, 1f)] float lidFollow = 0.7f;

        // Eye-lid follow shapes on the Character Creator face.
        const string LookUpL = "Eye_L_Look_Up";
        const string LookUpR = "Eye_R_Look_Up";
        const string LookDownL = "Eye_L_Look_Down";
        const string LookDownR = "Eye_R_Look_Down";
        const string PupilDilate = "Eye_Pupil_Dilate";
        const string BrowOuterL = "Brow_Raise_Outer_L";
        const string BrowOuterR = "Brow_Raise_Outer_R";
        const string BrowDropL = "Brow_Drop_L";
        const string BrowDropR = "Brow_Drop_R";

        Transform _head, _neckLow, _neckHigh, _eyeL, _eyeR;
        Quaternion _headRest, _neckLowRest, _neckHighRest, _eyeLRest, _eyeRRest;
        Quaternion _faceFromHead;
        Vector3 _eyeLForward, _eyeRForward;

        Vector3 _gazePoint, _gazeVelocity;
        bool _gazeStarted;

        float _yaw, _yawVelocity, _pitch, _pitchVelocity;
        float _offsetPitch, _offsetYaw, _offsetRoll;

        bool _away;
        Vector3 _awayPoint;
        float _awayUntil;
        float _nextAway;
        Vector2 _scan;
        float _nextScan;
        float _seed;

        /// <summary>True while she has looked away from the student.</summary>
        public bool LookingAway => _away;

        /// <summary>
        /// Adds to this frame's head movement, in degrees: pitch down is positive, yaw to her right is
        /// positive, roll is a tilt. Call every frame you want it; it clears itself.
        /// </summary>
        public void AddHeadOffset(float pitchDown, float yaw, float roll)
        {
            _offsetPitch += pitchDown;
            _offsetYaw += yaw;
            _offsetRoll += roll;
        }

        void Awake()
        {
            if (mood == null) mood = GetComponent<ProfessorMood>();
            if (face == null) face = GetComponentInChildren<FaceRig>();

            var bones = new BoneMap(transform);
            _head = bones.Get("Head");
            if (_head == null)
            {
                Debug.LogWarning("[CAIVR] ProfessorGaze: the model has no head bone, so she will not look at anyone.");
                enabled = false;
                return;
            }

            _neckLow = bones.Get("NeckTwist01");
            _neckHigh = bones.Get("NeckTwist02");
            _eyeL = bones.Side('L', "Eye");
            _eyeR = bones.Side('R', "Eye");

            _headRest = _head.localRotation;
            if (_neckLow != null) _neckLowRest = _neckLow.localRotation;
            if (_neckHigh != null) _neckHighRest = _neckHigh.localRotation;

            // The rotation that turns the head bone's own axes into "looking straight ahead".
            // Bone axes are rarely the model's forward, so work it out from how she was posed.
            _faceFromHead = Quaternion.Inverse(_head.rotation) * transform.rotation;

            if (_eyeL != null) { _eyeLRest = _eyeL.localRotation; _eyeLForward = Quaternion.Inverse(_eyeL.rotation) * transform.forward; }
            if (_eyeR != null) { _eyeRRest = _eyeR.localRotation; _eyeRForward = Quaternion.Inverse(_eyeR.rotation) * transform.forward; }

            _seed = Random.value * 100f;
            _nextAway = Time.time + Random.Range(2f, 4f);

            if (mood != null)
            {
                mood.PhaseChanged += OnPhaseChanged;
                mood.PhraseStarted += OnPhraseStarted;
                mood.PhraseEnded += OnPhraseEnded;
            }
        }

        void OnDestroy()
        {
            if (mood == null) return;
            mood.PhaseChanged -= OnPhaseChanged;
            mood.PhraseStarted -= OnPhraseStarted;
            mood.PhraseEnded -= OnPhraseEnded;
        }

        // --- when she looks away ---------------------------------------------

        void OnPhaseChanged(ProfessorPhase phase)
        {
            switch (phase)
            {
                case ProfessorPhase.Listening:
                    // The student's turn: eyes straight on them, and she stays there a good while.
                    _away = false;
                    _nextAway = Time.time + Random.Range(5f, 9f);
                    break;

                case ProfessorPhase.Thinking:
                    // Working out a reply: eyes go up and aside shortly after the student stops.
                    _away = false;
                    _nextAway = Time.time + Random.Range(0.25f, 0.55f);
                    break;

                default:
                    _away = false;
                    _nextAway = Time.time + Random.Range(1.5f, 3f);
                    break;
            }
        }

        void OnPhraseStarted()
        {
            // People tend to look away as they begin a thought, and come back to you at the end of it.
            if (mood.Phase == ProfessorPhase.Speaking && !_away && Random.value < 0.35f)
                BeginAway(Random.Range(0.5f, 1.0f));
        }

        void OnPhraseEnded()
        {
            if (_away && mood.Phase == ProfessorPhase.Speaking) EndAway();
        }

        void BeginAway(float seconds)
        {
            var phase = mood != null ? mood.Phase : ProfessorPhase.Waiting;
            var side = Random.value < 0.5f ? -1f : 1f;
            float yaw, up;

            switch (phase)
            {
                case ProfessorPhase.Thinking:
                    yaw = side * Random.Range(12f, 24f);
                    up = Random.Range(5f, 12f);
                    break;

                case ProfessorPhase.Listening:
                    // Mostly a short drop of the eyes towards the table, as if at her notes.
                    if (Random.value < 0.7f) { yaw = side * Random.Range(3f, 12f); up = -Random.Range(10f, 18f); }
                    else { yaw = side * Random.Range(12f, 22f); up = Random.Range(-3f, 3f); }
                    break;

                default:
                    var roll = Random.value;
                    if (roll < 0.5f) { yaw = side * Random.Range(12f, 24f); up = Random.Range(0f, 6f); }
                    else if (roll < 0.8f) { yaw = side * Random.Range(8f, 18f); up = -Random.Range(8f, 15f); }
                    else { yaw = side * Random.Range(4f, 10f); up = Random.Range(6f, 11f); }
                    break;
            }

            _awayPoint = EyeCentre() + transform.rotation * Quaternion.Euler(-up, yaw, 0f) * Vector3.forward * 1.3f;
            _away = true;
            _awayUntil = Time.time + seconds;

            // A big glance usually comes with a blink.
            if (face != null && Mathf.Abs(yaw) > 14f && Random.value < 0.6f) face.Blink();
        }

        void EndAway()
        {
            _away = false;
            if (face != null && Random.value < 0.35f) face.Blink();

            var phase = mood != null ? mood.Phase : ProfessorPhase.Waiting;
            _nextAway = Time.time + phase switch
            {
                ProfessorPhase.Listening => Random.Range(7f, 13f),
                ProfessorPhase.Thinking => Random.Range(1.2f, 2.2f),
                ProfessorPhase.Speaking => Random.Range(3f, 6f),
                _ => Random.Range(4f, 8f),
            };
        }

        void ChooseTarget()
        {
            var now = Time.time;

            if (_away)
            {
                if (now >= _awayUntil) EndAway();
            }
            else if (now >= _nextAway)
            {
                var phase = mood != null ? mood.Phase : ProfessorPhase.Waiting;
                BeginAway(phase switch
                {
                    ProfessorPhase.Thinking => Random.Range(1.0f, 1.8f),
                    ProfessorPhase.Listening => Random.Range(0.5f, 1.1f),
                    _ => Random.Range(0.7f, 1.4f),
                });
            }

            if (now >= _nextScan)
            {
                // Mostly between the eyes and the mouth, rather than the back wall.
                _scan = new Vector2(Random.Range(-1f, 1f), Random.Range(-1f, 0.35f));
                _nextScan = now + Random.Range(0.8f, 2.4f);
            }
        }

        // --- geometry --------------------------------------------------------

        Vector3 EyeCentre()
        {
            if (_eyeL != null && _eyeR != null) return (_eyeL.position + _eyeR.position) * 0.5f;
            return _head.position;
        }

        Transform Viewer()
        {
            if (viewer != null) return viewer;

            var cam = Camera.main;
            return cam != null ? cam.transform : null;
        }

        void LateUpdate()
        {
            var eyes = EyeCentre();
            var target = Viewer();
            ChooseTarget();

            Vector3 goal;
            if (_away) goal = _awayPoint;
            else if (target != null)
                goal = target.position + target.right * (_scan.x * eyeContactScan) + target.up * (_scan.y * eyeContactScan);
            else goal = eyes + transform.forward * 2f;

            if (!_gazeStarted) { _gazePoint = goal; _gazeStarted = true; }

            // The eyes jump fast and the point they are aimed at carries on in the world, so when
            // the head moves her eyes stay on you rather than swinging with it.
            var jump = Vector3.Angle(_gazePoint - eyes, goal - eyes);
            _gazePoint = Vector3.SmoothDamp(_gazePoint, goal, ref _gazeVelocity, saccadeSeconds + jump * 0.002f);

            MoveHead(eyes);
            MoveEyes(eyes);

            _offsetPitch = _offsetYaw = _offsetRoll = 0f;
        }

        // --- head and neck ---------------------------------------------------

        void MoveHead(Vector3 eyes)
        {
            var toGaze = Quaternion.Inverse(transform.rotation) * (_gazePoint - eyes);
            var flat = Mathf.Sqrt(toGaze.x * toGaze.x + toGaze.z * toGaze.z);
            var yawToGaze = Mathf.Atan2(toGaze.x, toGaze.z) * Mathf.Rad2Deg;
            var pitchDownToGaze = -Mathf.Atan2(toGaze.y, flat) * Mathf.Rad2Deg;

            // Contact: the head squares up to you once you are outside the dead zone. A glance: the head
            // goes only a third of the way and the eyes do the rest.
            float yawGoal;
            if (_away) yawGoal = yawToGaze * 0.35f;
            else
            {
                var beyond = Mathf.Max(0f, Mathf.Abs(yawToGaze) - headDeadZone);
                yawGoal = Mathf.Sign(yawToGaze) * beyond * 0.9f;
            }

            yawGoal = Mathf.Clamp(yawGoal, -maxHeadYaw, maxHeadYaw);
            var pitchGoal = Mathf.Clamp(pitchDownToGaze * 0.5f, -10f, 14f);

            _yaw = Mathf.SmoothDamp(_yaw, yawGoal, ref _yawVelocity, headSmoothSeconds);
            _pitch = Mathf.SmoothDamp(_pitch, pitchGoal, ref _pitchVelocity, headSmoothSeconds);

            var t = Time.time + _seed;
            var yaw = _yaw + _offsetYaw + Sway(t * 0.23f, 1.7f) * idleSwayDegrees;
            var pitch = _pitch + _offsetPitch + Sway(t * 0.19f, 7.1f) * idleSwayDegrees * 0.8f;
            var roll = _offsetRoll + Sway(t * 0.17f, 3.3f) * idleSwayDegrees * 1.2f;

            // A turn in the character's own frame, expressed as a rotation about the world axes.
            var rootRotation = transform.rotation;
            var turn = rootRotation * Quaternion.Euler(pitch, yaw, roll) * Quaternion.Inverse(rootRotation);

            var low = Quaternion.Slerp(Quaternion.identity, turn, neckShare * 0.45f);
            var high = Quaternion.Slerp(Quaternion.identity, turn, neckShare * 0.55f);

            if (_neckLow != null) { _neckLow.localRotation = _neckLowRest; _neckLow.rotation = low * _neckLow.rotation; }
            if (_neckHigh != null) { _neckHigh.localRotation = _neckHighRest; _neckHigh.rotation = high * _neckHigh.rotation; }

            // Whatever the neck did not take, the head does.
            var neckTotal = (_neckHigh != null ? high : Quaternion.identity) * (_neckLow != null ? low : Quaternion.identity);
            _head.localRotation = _headRest;
            _head.rotation = turn * Quaternion.Inverse(neckTotal) * _head.rotation;
        }

        static float Sway(float x, float y) => (Mathf.PerlinNoise(x, y) - 0.5f) * 2f;

        // --- eyes ------------------------------------------------------------

        void MoveEyes(Vector3 eyes)
        {
            // Keep the aim inside what eyes can really do relative to the face.
            var faceRotation = _head.rotation * _faceFromHead;
            var local = Quaternion.Inverse(faceRotation) * (_gazePoint - eyes);

            var distance = Mathf.Max(0.5f, local.magnitude);
            var flat = Mathf.Sqrt(local.x * local.x + local.z * local.z);
            var yaw = Mathf.Clamp(Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg, -eyeYawLimit, eyeYawLimit);
            var up = Mathf.Clamp(Mathf.Atan2(local.y, flat) * Mathf.Rad2Deg, -eyeLookDownLimit, eyeLookUpLimit);

            var point = eyes + faceRotation * (Quaternion.Euler(-up, yaw, 0f) * Vector3.forward) * distance;

            // Both eyes aim at the same point, so they converge a little on someone close, as real eyes do.
            Aim(_eyeL, _eyeLRest, _eyeLForward, point);
            Aim(_eyeR, _eyeRRest, _eyeRForward, point);

            if (face == null) return;

            var lids = lidFollow * 0.85f;
            var lookUp = Mathf.Clamp01(up / eyeLookUpLimit) * lids;
            var lookDown = Mathf.Clamp01(-up / eyeLookDownLimit) * lids;
            face.Mix(LookUpL, lookUp); face.Mix(LookUpR, lookUp);
            face.Mix(LookDownL, lookDown); face.Mix(LookDownR, lookDown);

            // The brows go with the eyes: they lift when she glances up and settle when she looks down.
            // Eyes that move with a still forehead above them is exactly what looks animatronic.
            var browUp = Mathf.Clamp01(up / eyeLookUpLimit) * 0.32f;
            var browDown = Mathf.Clamp01(-up / eyeLookDownLimit) * 0.12f;
            face.MixPair(BrowOuterL, BrowOuterR, browUp);
            face.MixPair(BrowDropL, BrowDropR, browDown);

            // Pupils are never a fixed size: they breathe in and out slowly. Slightly wide reads as soft and
            // attentive; pinprick pupils read as cold.
            face.Mix(PupilDilate, 0.22f + 0.12f * Mathf.PerlinNoise(Time.time * 0.35f + _seed, 21.7f));
        }

        static void Aim(Transform eye, Quaternion rest, Vector3 forwardInEye, Vector3 point)
        {
            if (eye == null) return;

            eye.localRotation = rest;
            var wanted = (point - eye.position).normalized;
            eye.rotation = Quaternion.FromToRotation(eye.rotation * forwardInEye, wanted) * eye.rotation;
        }
    }
}

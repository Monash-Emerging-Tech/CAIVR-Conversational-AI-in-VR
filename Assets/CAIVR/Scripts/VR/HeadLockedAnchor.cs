using UnityEngine;

namespace CAIVR.VR
{
    /// <summary>
    /// Holds a piece of UI in front of the viewer's face in a headset: captions,
    /// the briefing card. On a flat screen those are laid over the camera, but a
    /// headset has no screen to lay anything on, so the UI has to be an object
    /// that keeps itself in front of the eyes.
    ///
    /// Three things make that bearable rather than nauseating:
    ///   - it eases after your head instead of being nailed to it, so small head
    ///     movements do not make the text shudder
    ///   - it stays level, never rolling when you tilt your head
    ///   - it pulls in front of walls. A caption 1.6 m ahead would sit inside a
    ///     wall 0.8 m away, and drawing it over the wall would put it at the
    ///     wrong depth. Instead it comes closer, and shrinks to keep the same
    ///     apparent size.
    /// </summary>
    public sealed class HeadLockedAnchor : MonoBehaviour
    {
        /// <summary>The room's geometry. Set by <see cref="ExperienceRig"/>.</summary>
        public static Transform Obstacles;

        [Tooltip("How far ahead it sits when nothing is in the way.")]
        [SerializeField] float distance = 1.6f;

        [Tooltip("The distance the attached canvas was sized for. Scale is relative to this.")]
        [SerializeField] float referenceDistance = 1.6f;

        [Tooltip("Roughly how long it takes to catch up with your gaze. Smaller is tighter.")]
        [SerializeField] float followSeconds = 0.2f;

        [Tooltip("It only starts to follow once your gaze leaves this cone, then catches up. 0 follows constantly.")]
        [SerializeField] float deadzoneDegrees;

        [SerializeField] float minDistance = 0.45f;
        [SerializeField] float wallClearance = 0.12f;

        Transform _camera;
        Vector3 _direction = Vector3.forward;
        float _distance;
        float _lastTime;
        bool _following;
        bool _placed;

        static readonly RaycastHit[] Hits = new RaycastHit[24];

        public void Configure(float distance, float followSeconds, float deadzoneDegrees)
        {
            this.distance = distance;
            this.referenceDistance = distance;
            this.followSeconds = followSeconds;
            this.deadzoneDegrees = deadzoneDegrees;
            _distance = distance;
        }

        void OnEnable()
        {
            _placed = false;
            Application.onBeforeRender += Reposition;
        }

        void OnDisable() => Application.onBeforeRender -= Reposition;

        void LateUpdate() => Reposition();

        /// <summary>Jumps straight in front of the viewer, with no easing.</summary>
        public void Snap() => _placed = false;

        // Runs just before each frame is drawn as well as in LateUpdate. The headset
        // pose is applied late; placing the UI from the previous pose makes it lag a
        // frame behind the head, which reads as the text wobbling.
        void Reposition()
        {
            if (_camera == null || !_camera.gameObject.activeInHierarchy)
            {
                var main = Camera.main;
                if (main == null) return;
                _camera = main.transform;
                _placed = false;
            }

            var now = Time.unscaledTime;
            var dt = _placed ? Mathf.Max(0f, now - _lastTime) : 0f;
            _lastTime = now;

            var gaze = _camera.forward;

            if (!_placed)
            {
                _direction = gaze;
                _distance = distance;
                _following = true;
                _placed = true;
            }
            else
            {
                var off = Vector3.Angle(_direction, gaze);

                if (deadzoneDegrees <= 0f) _following = true;
                else if (off > deadzoneDegrees) _following = true;
                else if (off < 1f) _following = false;

                if (_following)
                {
                    var t = 1f - Mathf.Exp(-dt / Mathf.Max(0.001f, followSeconds));
                    _direction = Vector3.Slerp(_direction, gaze, t).normalized;
                }
            }

            var target = ClearDistance(_camera.position, _direction);

            // In front of a wall quickly, back out slowly. A late shrink is a
            // flash of text inside the wall; a late grow is nobody's problem.
            var rate = target < _distance ? 18f : 3f;
            _distance = dt <= 0f ? target : Mathf.Lerp(_distance, target, 1f - Mathf.Exp(-rate * dt));

            transform.position = _camera.position + _direction * _distance;

            // Level, facing the viewer: text stays upright when the head tilts.
            transform.rotation = Quaternion.LookRotation(_direction, Vector3.up);
            transform.localScale = Vector3.one * (_distance / referenceDistance);
        }

        float ClearDistance(Vector3 from, Vector3 direction)
        {
            if (Obstacles == null) return distance;

            var count = Physics.RaycastNonAlloc(new Ray(from, direction), Hits, distance + wallClearance,
                ~0, QueryTriggerInteraction.Ignore);

            var nearest = float.MaxValue;
            for (var i = 0; i < count; i++)
            {
                if (!Hits[i].collider.transform.IsChildOf(Obstacles)) continue;
                if (Hits[i].distance < nearest) nearest = Hits[i].distance;
            }

            if (nearest == float.MaxValue) return distance;
            return Mathf.Clamp(nearest - wallClearance, minDistance, distance);
        }
    }
}

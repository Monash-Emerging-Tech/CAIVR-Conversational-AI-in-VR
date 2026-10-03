using System.Collections;
using System.Collections.Generic;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.XR;

namespace CAIVR.VR
{
    /// <summary>
    /// Decides, once, how the student is taking part: on a flat screen with a
    /// mouse, or in a headset with their hands.
    ///
    /// The scene carries both ways in. A plain camera for the desktop, and the
    /// XR rig with hand tracking for the headset. Only one is ever switched on,
    /// so on a PC with no headset there is no rig in play and nothing to trip
    /// over, and with a headset on the same scene just works. No separate
    /// scenes to keep in step.
    ///
    /// In a headset the student is then placed in their chair. VR tracks where
    /// your head really is, so without this you would start wherever your play
    /// space happens to put you, standing at a random spot in the room.
    /// </summary>
    [DefaultExecutionOrder(-10000)]
    public sealed class ExperienceRig : MonoBehaviour
    {
        public enum Mode
        {
            /// <summary>Headset if one is running, otherwise desktop.</summary>
            Auto,
            Desktop,
            Headset,
        }

        [SerializeField] Mode mode = Mode.Auto;

        [Header("The two ways in")]
        [Tooltip("Plain camera with mouse look. Switched off in a headset.")]
        [SerializeField] GameObject desktopRig;

        [Tooltip("The XR rig with hand tracking. Switched off on a flat screen.")]
        [SerializeField] GameObject headsetRig;

        [Header("UI input (the event system serves whichever is active)")]
        [SerializeField] Behaviour desktopInput;
        [SerializeField] Behaviour headsetInput;

        [Header("Where the student sits")]
        [Tooltip("Eye position and facing of the seated student.")]
        [SerializeField] Transform seat;

        [Tooltip("The room. Head-locked UI pulls in front of anything in here so it cannot clip into a wall.")]
        [SerializeField] Transform obstacles;

        [Header("Testing without a headset (Editor only)")]
        [Tooltip("The XR Interaction Simulator. Only ever switched on in the Editor, with no headset connected.")]
        [SerializeField] GameObject simulator;

        [SerializeField] XROrigin origin;

        public static ExperienceRig Instance { get; private set; }

        /// <summary>True when the student is in a headset (or the Editor is simulating one).</summary>
        public static bool IsHeadset => Instance != null && Instance.Headset;

        public bool Headset { get; private set; }

        /// <summary>True once the student is seated and the experience can begin.</summary>
        public bool Ready { get; private set; }

        public Transform ObstacleRoot => obstacles;

        /// <summary>Which way the seated student faces: toward the professor, level.</summary>
        public Vector3 SeatForward
        {
            get
            {
                if (seat == null) return Vector3.forward;

                var forward = seat.forward;
                forward.y = 0f;
                return forward.sqrMagnitude > 0.0001f ? forward.normalized : Vector3.forward;
            }
        }

        public Vector3 SeatEyePosition => seat != null ? seat.position : Vector3.zero;

        static readonly List<XRDisplaySubsystem> Displays = new List<XRDisplaySubsystem>();
        static readonly List<XRInputSubsystem> Inputs = new List<XRInputSubsystem>();

        void Awake()
        {
            Instance = this;

            var simulated = false;
            Headset = Resolve(out simulated);

            // The UI input module has to be switched before the rig wakes up: the
            // interactors look for it the moment they are enabled.
            if (desktopInput != null) desktopInput.enabled = !Headset;
            if (headsetInput != null) headsetInput.enabled = Headset;

            if (desktopRig != null) desktopRig.SetActive(!Headset);
            if (headsetRig != null)
            {
                if (Headset) KeepHandsAboveFade();
                headsetRig.SetActive(Headset);
            }

            if (simulator != null) simulator.SetActive(Headset && simulated);

            if (origin == null && headsetRig != null) origin = headsetRig.GetComponent<XROrigin>();

            HeadLockedAnchor.Obstacles = obstacles;

            Debug.Log(Headset
                ? (simulated ? "[CAIVR] Headset mode (simulated in the Editor)." : "[CAIVR] Headset mode.")
                : "[CAIVR] Desktop mode: no headset is running.");
        }

        void OnEnable()
        {
            if (!Headset) return;

            // A long press of the headset's menu button recentres the play space.
            // Without re-seating, the student would end up wherever that put them.
            SubsystemManager.GetSubsystems(Inputs);
            foreach (var input in Inputs) input.trackingOriginUpdated += OnTrackingOriginUpdated;
        }

        void OnDisable()
        {
            foreach (var input in Inputs) input.trackingOriginUpdated -= OnTrackingOriginUpdated;
            Inputs.Clear();
        }

        IEnumerator Start()
        {
            if (!Headset)
            {
                Ready = true;
                yield break;
            }

            // The headset reports its first pose a few frames after the rig wakes.
            // Seating before that would measure the camera at the origin.
            var camera = origin != null ? origin.Camera : null;
            var waited = 0f;

            while (camera != null && waited < 4f && camera.transform.localPosition.sqrMagnitude < 0.0004f)
            {
                waited += Time.unscaledDeltaTime;
                yield return null;
            }

            SeatStudent();
            Ready = true;
        }

        void OnTrackingOriginUpdated(XRInputSubsystem subsystem) => StartCoroutine(ReseatNextFrame());

        IEnumerator ReseatNextFrame()
        {
            yield return null;
            SeatStudent();
        }

        /// <summary>Moves the rig so the student's head is at the seat, facing the professor.</summary>
        public void SeatStudent()
        {
            if (!Headset || origin == null || seat == null) return;

            // Turn first, then move: the move is measured from where the camera
            // ends up after the turn.
            origin.MatchOriginUpCameraForward(Vector3.up, SeatForward);
            origin.MoveCameraToWorldLocation(seat.position);
        }

        /// <summary>Waits until the student is in their seat. Instant on a flat screen.</summary>
        public static IEnumerator WaitUntilReady()
        {
            while (Instance != null && !Instance.Ready) yield return null;
        }

        /// <summary>Sorts above the fade to black and the briefing card.</summary>
        public const int HandsSortingOrder = 200;

        /// <summary>
        /// The fade to black is drawn over the whole room, which would blot out the
        /// student's own hands too. Losing sight of your hands while reaching for the
        /// Start button is disorienting, so everything belonging to the rig (hands,
        /// controllers, rays, the poke and pinch markers) is drawn above the fade.
        ///
        /// Anything in the opaque queue is drawn before the fade whatever its sorting
        /// order, so those materials are moved to the transparent queue.
        /// </summary>
        void KeepHandsAboveFade()
        {
            foreach (var renderer in headsetRig.GetComponentsInChildren<Renderer>(true))
            {
                // The comfort vignette belongs to the head, not the hands, and is unused
                // anyway since there is no locomotion.
                if (renderer.name == "TunnelingVignette") continue;

                renderer.sortingOrder = HandsSortingOrder;

                var materials = renderer.materials;      // instances, so the shared assets are untouched
                for (var i = 0; i < materials.Length; i++)
                {
                    if (materials[i] != null && materials[i].renderQueue < 3000) materials[i].renderQueue = 3000;
                }
            }

            // Renderers inside a sorting group take their order from the group.
            foreach (var group in headsetRig.GetComponentsInChildren<UnityEngine.Rendering.SortingGroup>(true))
            {
                if (group.name == "TunnelingVignette") continue;
                group.sortingOrder = HandsSortingOrder;
            }
        }

        // --- deciding --------------------------------------------------------

        bool Resolve(out bool simulated)
        {
            simulated = false;

            if (mode == Mode.Desktop) return false;
            if (mode == Mode.Headset) return true;

            if (HeadsetRunning()) return true;

#if UNITY_EDITOR
            // No headset: the Editor can stand in for one, so the hands, rays and
            // grabbing can be tried at a desk. Off unless asked for.
            if (UnityEditor.EditorPrefs.GetBool(SimulateKey, false) && simulator != null)
            {
                simulated = true;
                return true;
            }
#endif
            return false;
        }

        public const string SimulateKey = "caivr.vr.simulate";

        /// <summary>True when an XR runtime has a display up, that is, a headset is really there.</summary>
        public static bool HeadsetRunning()
        {
            SubsystemManager.GetSubsystems(Displays);
            foreach (var display in Displays)
            {
                if (display.running) return true;
            }

            return XRSettings.isDeviceActive;
        }
    }
}

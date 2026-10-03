using CAIVR.Dialogue;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace CAIVR.VR
{
    /// <summary>
    /// A notebook on the table that you can pick up and read, showing the
    /// background context of the scenario.
    ///
    /// This is the "if you forget the context, there is a laptop, sticky note or
    /// notebook" idea from Workerbee #2: the briefing is not lost once the
    /// fade-in is over, and getting it back is something you do with your hands
    /// rather than a menu.
    ///
    /// People drop things in VR and throw them across rooms, so a prop the whole
    /// scenario relies on has to find its own way back.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public sealed class ContextNotebook : MonoBehaviour
    {
        [SerializeField] ConversationRunner runner;
        [SerializeField] XRGrabInteractable grab;
        [SerializeField] TextMeshProUGUI pageText;

        [Tooltip("If it falls this far below where it started, put it back on the table.")]
        [SerializeField] float respawnBelowMeters = 1f;

        [Tooltip("After being put down away from the table, return it after this long.")]
        [SerializeField] float returnAfterSeconds = 25f;

        [SerializeField] float awayFromHomeMeters = 1.2f;

        [Header("Click to read (no hands needed)")]
        [Tooltip("How far in front of the camera it floats while you read it.")]
        [SerializeField] float inspectDistance = 0.55f;

        Rigidbody _body;
        Vector3 _homePosition;
        Quaternion _homeRotation;
        float _releasedAt = -1f;
        bool _inspecting;
        bool _wasKinematic;

        /// <summary>True while it is held up in front of the camera to be read.</summary>
        public bool IsBeingRead => _inspecting;

        void Awake()
        {
            _body = GetComponent<Rigidbody>();
            if (grab == null) grab = GetComponent<XRGrabInteractable>();
            if (runner == null) runner = FindFirstObjectByType<ConversationRunner>();

            _homePosition = transform.position;
            _homeRotation = transform.rotation;
        }

        void OnEnable()
        {
            if (runner != null)
            {
                runner.ContextReady += ShowContext;

                // Prepare() may already have run before this component woke up.
                if (!string.IsNullOrEmpty(runner.CurrentContext)) ShowContext(runner.CurrentContext);
            }

            if (grab != null)
            {
                grab.selectEntered.AddListener(OnGrabbed);
                grab.selectExited.AddListener(OnReleased);
            }
        }

        void OnDisable()
        {
            if (runner != null) runner.ContextReady -= ShowContext;

            if (grab != null)
            {
                grab.selectEntered.RemoveListener(OnGrabbed);
                grab.selectExited.RemoveListener(OnReleased);
            }
        }

        void ShowContext(string context)
        {
            // The page already has a "Background" heading of its own, so this is just the text.
            if (pageText != null) pageText.text = context;
        }

        void OnGrabbed(SelectEnterEventArgs args) => _releasedAt = -1f;

        void OnReleased(SelectExitEventArgs args) => _releasedAt = Time.time;

        void Update()
        {
            HandleReading();
            if (_inspecting) return;

            if (transform.position.y < _homePosition.y - respawnBelowMeters)
            {
                ResetToHome();
                return;
            }

            if (_releasedAt < 0f || (grab != null && grab.isSelected)) return;

            var farFromTable = (transform.position - _homePosition).magnitude > awayFromHomeMeters;
            if (farFromTable && Time.time - _releasedAt > returnAfterSeconds) ResetToHome();
        }

        // --- click to read ---------------------------------------------------

        /// <summary>
        /// With a mouse there is nothing to grab it with, so a click brings it up
        /// in front of the camera to be read, and a click or Escape puts it down.
        /// This is the flat-screen stand-in for picking it up with a hand.
        /// </summary>
        void HandleReading()
        {
            var mouse = Mouse.current;
            var keyboard = Keyboard.current;

            if (_inspecting)
            {
                var putDown = (keyboard != null && keyboard.escapeKey.wasPressedThisFrame)
                              || (mouse != null && mouse.leftButton.wasPressedThisFrame && !PointerOverUi());
                if (putDown) EndReading();
                else FloatInFrontOfCamera();
                return;
            }

            if (mouse == null || !mouse.leftButton.wasPressedThisFrame || PointerOverUi()) return;
            if (grab != null && grab.isSelected) return;      // already in a hand

            var camera = Camera.main;
            if (camera == null) return;

            var ray = camera.ScreenPointToRay(mouse.position.ReadValue());
            if (Physics.Raycast(ray, out var hit, 4f) && hit.collider.GetComponentInParent<ContextNotebook>() == this)
                BeginReading();
        }

        static bool PointerOverUi() =>
            EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();

        void BeginReading()
        {
            _inspecting = true;
            _wasKinematic = _body.isKinematic;
            _body.isKinematic = true;       // we are moving it by hand, so physics must not fight us
        }

        void EndReading()
        {
            _inspecting = false;
            _body.isKinematic = _wasKinematic;
            ResetToHome();
        }

        void FloatInFrontOfCamera()
        {
            var camera = Camera.main;
            if (camera == null) return;

            var camTransform = camera.transform;
            var position = camTransform.position + camTransform.forward * inspectDistance - camTransform.up * 0.04f;

            // The page text runs along the notebook's +Z with its face up (+Y).
            // Turn it so the page faces the camera and the text reads upright.
            var rotation = Quaternion.LookRotation(camTransform.up, -camTransform.forward);

            var t = 1f - Mathf.Exp(-12f * Time.unscaledDeltaTime);
            transform.SetPositionAndRotation(
                Vector3.Lerp(transform.position, position, t),
                Quaternion.Slerp(transform.rotation, rotation, t));
        }

        public void ResetToHome()
        {
            _body.linearVelocity = Vector3.zero;
            _body.angularVelocity = Vector3.zero;
            transform.SetPositionAndRotation(_homePosition, _homeRotation);
            _releasedAt = -1f;
        }
    }
}

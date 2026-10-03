using CAIVR.Dialogue;
using CAIVR.Speech;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CAIVR.VR
{
    /// <summary>
    /// Subtitles, the way a film does them: small, low and centred on the viewer's
    /// screen, there while she speaks and gone when she stops.
    ///
    /// They belong to the viewer rather than the room, so they are laid over the
    /// camera and not placed in the scene: they stay readable wherever the student
    /// looks and can never end up inside a wall.
    ///
    /// Deliberately nothing else. No "your turn", no "thinking", no buttons, no
    /// toggle: the stakeholder wants the scenario to feel like a real conversation,
    /// and a status bar constantly telling you what the software is doing breaks
    /// that. Subtitles are switched on or off in the menu before the scenario.
    ///
    /// The one exception is a genuine fault, such as having no microphone. Without a
    /// word about it the student would sit waiting on a conversation that cannot
    /// continue, so that, and only that, gets a small quiet note.
    ///
    /// It listens to the same <see cref="ConversationRunner"/> events the flat demo
    /// HUD does; the runner knows nothing about UI.
    /// </summary>
    public sealed class SubtitleHud : MonoBehaviour
    {
        [SerializeField] ConversationRunner runner;
        [SerializeField] SpeechService speech;

        [Header("Look")]
        [SerializeField] float fontSize = 27f;
        [SerializeField] float maxWidth = 960f;

        // Mostly opaque on purpose. A faint backdrop over a bright wall turns into
        // light grey, and white text on light grey is hard to read.
        static readonly Color Backdrop = new Color(0.015f, 0.03f, 0.06f, 0.82f);
        const float PadX = 26f;
        const float PadY = 11f;

        [Tooltip("Pixels up from the bottom edge, at 1080p.")]
        [SerializeField] float bottomMargin = 64f;

        [Header("Timing")]
        [Tooltip("How long a line lingers after the professor stops, so the last words can be finished.")]
        [SerializeField] float lingerSeconds = 1.1f;
        [SerializeField] float fadeSeconds = 0.35f;

        [Tooltip("How long a fault note stays up.")]
        [SerializeField] float noticeSeconds = 7f;

        Canvas _canvas;
        CanvasGroup _captionGroup;
        CanvasGroup _noticeGroup;
        TextMeshProUGUI _caption;
        RectTransform _box;
        string _sizedFor;
        TextMeshProUGUI _notice;

        string _line = "";
        float _lineShownAt;
        float _speakingEndedAt = -1f;
        float _noticeShownAt = -100f;
        bool _micWarned;

        void Awake()
        {
            if (runner == null) runner = FindFirstObjectByType<ConversationRunner>();
            if (speech == null) speech = FindFirstObjectByType<SpeechService>();

            Build();
        }

        void OnEnable()
        {
            if (runner == null) return;

            runner.ProfessorLine += OnProfessorLine;
            runner.Notice += OnNotice;
            runner.StateChanged += OnStateChanged;
        }

        void OnDisable()
        {
            if (runner == null) return;

            runner.ProfessorLine -= OnProfessorLine;
            runner.Notice -= OnNotice;
            runner.StateChanged -= OnStateChanged;
        }

        void Update()
        {
            VrUi.EnsureEventCamera(_canvas);

            DrawCaption();
            DrawNotice();
        }

        // --- events ----------------------------------------------------------

        void OnProfessorLine(string line)
        {
            _line = line;
            _lineShownAt = Time.unscaledTime;
            _speakingEndedAt = -1f;
        }

        void OnNotice(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;

            _notice.text = message;
            _noticeShownAt = Time.unscaledTime;
        }

        void OnStateChanged(ConversationState state)
        {
            if (state != ConversationState.Speaking && _speakingEndedAt < 0f && !string.IsNullOrEmpty(_line))
                _speakingEndedAt = Time.unscaledTime;

            // If there is no microphone the conversation cannot go anywhere. Say so
            // once, rather than leaving the student waiting on it.
            if (state == ConversationState.Listening && !_micWarned
                && speech != null && speech.UsingKeyboardFallback)
            {
                _micWarned = true;
                OnNotice("No microphone found, so you cannot reply.");
            }
        }

        // --- drawing ---------------------------------------------------------

        void DrawCaption()
        {
            var enabled = CAIVR.Menu.CaivrSettings.SubtitlesEnabled;
            var speaking = runner != null && runner.State == ConversationState.Speaking;

            float target;

            if (!enabled || string.IsNullOrEmpty(_line)) target = 0f;
            else if (speaking) target = Mathf.Clamp01((Time.unscaledTime - _lineShownAt) / 0.2f);
            else if (_speakingEndedAt >= 0f && Time.unscaledTime - _speakingEndedAt < lingerSeconds) target = 1f;
            else target = 0f;

            _captionGroup.alpha = Mathf.MoveTowards(_captionGroup.alpha, target, Time.unscaledDeltaTime / fadeSeconds);

            if (_captionGroup.alpha > 0.01f && _sizedFor != _line)
            {
                _caption.text = _line;
                SizeBoxToText();
                _sizedFor = _line;
            }
        }

        /// <summary>
        /// Shrink-wraps the backdrop around the line. A box that was always the full
        /// width read as a bar of interface; one that hugs the words reads as a caption.
        /// </summary>
        void SizeBoxToText()
        {
            var inner = maxWidth - PadX * 2f;

            var measured = _caption.GetPreferredValues(_line, inner, 0f);
            var width = Mathf.Min(maxWidth, measured.x + PadX * 2f);

            // Measure again at the width we settled on, so wrapping and height agree.
            var height = _caption.GetPreferredValues(_line, width - PadX * 2f, 0f).y;

            _box.sizeDelta = new Vector2(width, height + PadY * 2f);
        }

        void DrawNotice()
        {
            var age = Time.unscaledTime - _noticeShownAt;
            var target = age < noticeSeconds ? 1f : 0f;

            _noticeGroup.alpha = Mathf.MoveTowards(_noticeGroup.alpha, target, Time.unscaledDeltaTime / 0.4f);
        }

        // --- construction ----------------------------------------------------

        void Build()
        {
            _canvas = VrUi.CreateScreenCanvas("Subtitles", transform, Camera.main, sortingOrder: 50);
            var root = _canvas.transform;

            BuildCaption(root);
            BuildNotice(root);
        }

        void BuildCaption(Transform root)
        {
            var box = VrUi.Surface(root, "Captions", Layout.BottomCenter(maxWidth, 60, bottomMargin),
                Backdrop, 11f);

            _box = box.rectTransform;
            _box.anchorMin = _box.anchorMax = new Vector2(0.5f, 0f);
            _box.pivot = new Vector2(0.5f, 0f);          // pinned at the bottom, so it grows upward
            _box.sizeDelta = new Vector2(maxWidth, 60f);
            _box.anchoredPosition = new Vector2(0f, bottomMargin);

            _captionGroup = box.gameObject.AddComponent<CanvasGroup>();
            _captionGroup.alpha = 0f;
            _captionGroup.blocksRaycasts = false;     // captions must never intercept a click
            _captionGroup.interactable = false;

            _caption = VrUi.Text(box.transform, "Line", Layout.Fill(PadX, PadY, PadX, PadY), "", fontSize,
                MonashTheme.Text, TextAlignmentOptions.Center, tracking: 0.8f);

            StyleCaptionType(_caption);
        }

        /// <summary>
        /// Inter ships here in a single regular weight, which looks thin at small sizes
        /// over a busy scene. Thickening the glyphs a touch and adding a faint shadow
        /// gives the crisper, medium-weight feel of current subtitle styling without
        /// another font file. Applied to this label's own material copy, so no other
        /// text in the project changes.
        /// </summary>
        static void StyleCaptionType(TextMeshProUGUI label)
        {
            var material = label.fontMaterial;        // an instance owned by this label

            material.SetFloat(ShaderUtilities.ID_FaceDilate, 0.10f);

            material.EnableKeyword(ShaderUtilities.Keyword_Underlay);
            material.SetColor(ShaderUtilities.ID_UnderlayColor, new Color(0f, 0f, 0f, 0.55f));
            material.SetFloat(ShaderUtilities.ID_UnderlayOffsetX, 0.5f);
            material.SetFloat(ShaderUtilities.ID_UnderlayOffsetY, -0.5f);
            material.SetFloat(ShaderUtilities.ID_UnderlaySoftness, 0.35f);

            label.UpdateMeshPadding();
        }

        void BuildNotice(Transform root)
        {
            // Small, bottom left, and only ever present when something is actually wrong.
            var group = VrUi.Group(root, "Notice", Layout.BottomLeft(48, 40, 900, 40));
            _noticeGroup = group.gameObject.AddComponent<CanvasGroup>();
            _noticeGroup.alpha = 0f;
            _noticeGroup.blocksRaycasts = false;
            _noticeGroup.interactable = false;

            _notice = VrUi.Text(group, "Text", Layout.Fill(), "", 22, MonashTheme.Warning,
                TextAlignmentOptions.MidlineLeft);
        }
    }
}

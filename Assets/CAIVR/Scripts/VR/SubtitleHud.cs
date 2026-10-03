using CAIVR.Dialogue;
using CAIVR.Menu;
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
    /// For a student who has chosen a native language, a second, translated line sits under the English one.
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

        [Tooltip("The same in a headset, where the 'screen' is a virtual one hung in front of the face. Higher, so the line is not down at the table.")]
        [SerializeField] float headsetBottomMargin = 170f;

        bool _headset;

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
        TextMeshProUGUI _captionNative;
        RectTransform _box;
        string _sizedFor;
        string _nativeLine;
        LineTranslator _translator;

        // The translated line is a shade cooler than the English, so the two read as a pair.
        static readonly Color NativeColor = new Color(0.72f, 0.86f, 1f);
        const float NativeSize = 24f;
        const float LineGap = 6f;
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

            _translator = FindFirstObjectByType<LineTranslator>();
            if (_translator == null) _translator = gameObject.AddComponent<LineTranslator>();

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
            _nativeLine = null;

            if (!Loc.NativeActive || runner == null) return;

            // Authored lines have an authored translation. A line the AI made up does not, so it is translated
            // now and the second line appears when it arrives.
            if (runner.TryGetNative(line, out var native)) _nativeLine = native;
            else _translator.Request(line, Loc.Native, translated =>
            {
                if (_line == line) _nativeLine = translated;
            });
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
                OnNotice(Loc.T("notice.nomic"));
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

            var shown = Loc.NativeActive ? _line + "\n" + _nativeLine : _line;

            if (_captionGroup.alpha > 0.01f && _sizedFor != shown)
            {
                _caption.text = _line;
                _captionNative.text = Loc.NativeActive ? _nativeLine : null;
                SizeBoxToText();
                _sizedFor = shown;
            }
        }

        /// <summary>
        /// Shrink-wraps the backdrop around the line. A box that was always the full
        /// width read as a bar of interface; one that hugs the words reads as a caption.
        /// </summary>
        void SizeBoxToText()
        {
            var inner = maxWidth - PadX * 2f;
            var native = Loc.NativeActive && !string.IsNullOrEmpty(_nativeLine) ? _nativeLine : null;

            var measured = _caption.GetPreferredValues(_line, inner, 0f);
            var width = measured.x;

            if (native != null) width = Mathf.Max(width, _captionNative.GetPreferredValues(native, inner, 0f).x);
            width = Mathf.Min(maxWidth, width + PadX * 2f);

            // Measure again at the width we settled on, so wrapping and height agree.
            var height = _caption.GetPreferredValues(_line, width - PadX * 2f, 0f).y;
            var nativeHeight = native != null ? _captionNative.GetPreferredValues(native, width - PadX * 2f, 0f).y : 0f;

            _box.sizeDelta = new Vector2(width, height + (native != null ? LineGap + nativeHeight : 0f) + PadY * 2f);

            PlaceLine(_caption.rectTransform, PadY, height);
            _captionNative.gameObject.SetActive(native != null);
            if (native != null) PlaceLine(_captionNative.rectTransform, PadY + height + LineGap, nativeHeight);
        }

        /// <summary>Pins a line to the top of the box, inset by the padding, at the given offset and height.</summary>
        static void PlaceLine(RectTransform line, float top, float height)
        {
            line.anchorMin = new Vector2(0f, 1f);
            line.anchorMax = new Vector2(1f, 1f);
            line.pivot = new Vector2(0.5f, 1f);
            line.offsetMin = new Vector2(PadX, -(top + height));
            line.offsetMax = new Vector2(-PadX, -top);
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
            _headset = ExperienceRig.IsHeadset;

            if (_headset)
            {
                // A virtual screen hung in front of the face, about 85 degrees wide at
                // 1.6 m, with the captions along its lower edge. Sized so the type
                // reads as roughly 1.5 degrees tall, which is comfortable in a headset.
                _canvas = VrUi.CreateHeadCanvas("Subtitles", transform, new Vector2(1920f, 1080f), 2.98f,
                    distance: 1.6f, followSeconds: 0.22f, deadzoneDegrees: 0f);
            }
            else
            {
                _canvas = VrUi.CreateScreenCanvas("Subtitles", transform, Camera.main, sortingOrder: 50);
            }

            var root = _canvas.transform;

            BuildCaption(root);
            BuildNotice(root);
        }

        void BuildCaption(Transform root)
        {
            var margin = _headset ? headsetBottomMargin : bottomMargin;

            var box = VrUi.Surface(root, "Captions", Layout.BottomCenter(maxWidth, 60, margin),
                Backdrop, 11f);

            _box = box.rectTransform;
            _box.anchorMin = _box.anchorMax = new Vector2(0.5f, 0f);
            _box.pivot = new Vector2(0.5f, 0f);          // pinned at the bottom, so it grows upward
            _box.sizeDelta = new Vector2(maxWidth, 60f);
            _box.anchoredPosition = new Vector2(0f, margin);

            _captionGroup = box.gameObject.AddComponent<CanvasGroup>();
            _captionGroup.alpha = 0f;
            _captionGroup.blocksRaycasts = false;     // captions must never intercept a click
            _captionGroup.interactable = false;

            _caption = VrUi.Text(box.transform, "Line", Layout.Fill(PadX, PadY, PadX, PadY), "", fontSize,
                MonashTheme.Text, TextAlignmentOptions.Center, tracking: 0.8f);

            StyleCaptionType(_caption);

            _captionNative = VrUi.Text(box.transform, "NativeLine", Layout.Fill(PadX, PadY, PadX, PadY), "", NativeSize,
                NativeColor, TextAlignmentOptions.Center, tracking: 0.6f);
            _captionNative.gameObject.SetActive(false);

            StyleCaptionType(_captionNative);
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
            // In a headset the far corner of the view is out of reach of the eyes, so
            // it sits centred just below the captions instead.
            var layout = _headset ? Layout.BottomCenter(900, 40, headsetBottomMargin - 70) : Layout.BottomLeft(48, 40, 900, 40);
            var group = VrUi.Group(root, "Notice", layout);
            _noticeGroup = group.gameObject.AddComponent<CanvasGroup>();
            _noticeGroup.alpha = 0f;
            _noticeGroup.blocksRaycasts = false;
            _noticeGroup.interactable = false;

            _notice = VrUi.Text(group, "Text", Layout.Fill(), "", 22, MonashTheme.Warning,
                _headset ? TextAlignmentOptions.Midline : TextAlignmentOptions.MidlineLeft);
        }
    }
}

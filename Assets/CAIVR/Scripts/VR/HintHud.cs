using System.Collections.Generic;
using CAIVR.Dialogue;
using CAIVR.Menu;
using TMPro;
using UnityEngine;

namespace CAIVR.VR
{
    /// <summary>
    /// Help for a student who seems to be stuck, kept deliberately small.
    ///
    /// The scenario is meant to feel like a real conversation, so nothing is on screen while it goes well. When the
    /// student says nothing for a while, or says something the professor cannot use, she prompts them ("Take your
    /// time. What do you need?") and one small card appears above the captions:
    ///   - the first time: up to three short things they could talk about
    ///   - if they are still stuck: instead, one sentence they could actually say, with its translation
    /// It stays while they speak and while the professor works out her answer, and goes when she starts to reply
    /// (so they can glance at it as they talk). If their answer was not understood and she prompts again, it stays
    /// and moves on to the example sentence.
    ///
    /// One card, a few short lines, nothing else. An earlier version showed every option as a floating pill across
    /// the screen, which was harder to read than the problem it was solving.
    ///
    /// The suggestions are in the student's own language when they have chosen one, and in English otherwise. The
    /// example sentence is always English, since English is what they are practising, with a translation under it.
    /// What to suggest is authored per step in the conversation file (hints, hintsZh, hintExample, hintExampleZh).
    /// </summary>
    public sealed class HintHud : MonoBehaviour
    {
        [SerializeField] ConversationRunner runner;

        [Header("Look")]
        [Tooltip("The most suggestions shown at once. Fewer is easier to take in.")]
        [SerializeField, Range(1, 4)] int maxHints = 3;

        [SerializeField] float lineFontSize = 30f;
        [SerializeField] float maxLineWidth = 820f;

        [Tooltip("Pixels up from the bottom edge at 1080p. Above the captions, which can be two lines tall.")]
        [SerializeField] float bottomMargin = 190f;

        [Tooltip("The same in a headset.")]
        [SerializeField] float headsetBottomMargin = 300f;

        [SerializeField] float fadeSeconds = 0.35f;

        // Solid: the card sits over the professor and the table, and anything see-through behind text makes it
        // harder to read (even at 95% a cream shirt shows through).
        static readonly Color CardFill = new Color(0.02f, 0.045f, 0.09f, 1f);
        static readonly Color NativeColor = new Color(0.72f, 0.86f, 1f);

        const float PadX = 38f;
        const float PadY = 24f;
        const float HeaderHeight = 28f;
        const float LineGap = 12f;
        const float BulletSize = 14f;
        const float BulletIndent = 34f;

        Canvas _canvas;
        CanvasGroup _group;
        RectTransform _panel;
        TextMeshProUGUI _measure;
        bool _headset;

        DialogueNode _node;
        int _level;
        bool _wanted;
        bool _answered;        // the student has answered since the hints appeared

        void Awake()
        {
            if (runner == null) runner = FindFirstObjectByType<ConversationRunner>();
            Build();
        }

        void OnEnable()
        {
            if (runner == null) return;

            runner.StudentStuck += OnStuck;
            runner.StateChanged += OnStateChanged;
        }

        void OnDisable()
        {
            if (runner == null) return;

            runner.StudentStuck -= OnStuck;
            runner.StateChanged -= OnStateChanged;
        }

        void Update()
        {
            VrUi.EnsureEventCamera(_canvas);

            // The conversation has moved on to a different question: the old suggestions no longer apply.
            if (_wanted && runner != null && runner.CurrentNode != _node) _wanted = false;

            _group.alpha = Mathf.MoveTowards(_group.alpha, _wanted ? 1f : 0f, Time.unscaledDeltaTime / fadeSeconds);
        }

        // --- events ----------------------------------------------------------

        void OnStuck(DialogueNode node, int count)
        {
            if (node == null || node.hints == null || node.hints.Length == 0) return;

            _node = node;
            _level = count;
            _wanted = true;
            _answered = false;

            Rebuild();
        }

        void OnStateChanged(ConversationState state)
        {
            switch (state)
            {
                case ConversationState.Deciding:
                    // They have answered and she is working out what to say. The hints stay for now.
                    _answered = true;
                    break;

                case ConversationState.Speaking:
                    // She starts to reply: the hints have done their job. (If she is instead prompting again, the
                    // stuck event follows straight away and puts them back.)
                    if (_answered)
                    {
                        _wanted = false;
                        _answered = false;
                    }
                    break;

                case ConversationState.Ended:
                    _wanted = false;
                    break;
            }
        }

        // --- content ---------------------------------------------------------

        /// <summary>The suggestions in the student's language if there is one and it was authored, else English.</summary>
        string[] HintsFor(DialogueNode node)
        {
            if (Loc.NativeActive && node.hintsZh != null && node.hintsZh.Length == node.hints.Length) return node.hintsZh;
            return node.hints;
        }

        void Rebuild()
        {
            for (var i = _panel.childCount - 1; i >= 0; i--) Destroy(_panel.GetChild(i).gameObject);

            var example = _level >= 2 && !string.IsNullOrWhiteSpace(_node.hintExample);

            // What goes on the card: a header, then either a few short suggestions or one sentence to say.
            var header = Loc.Help(example ? "hint.example" : "hint.header");

            var lines = new List<string>();
            var suggestions = !example;

            if (example)
            {
                lines.Add("“" + _node.hintExample + "”");
            }
            else
            {
                var all = HintsFor(_node);
                for (var i = 0; i < all.Length && lines.Count < maxHints; i++) lines.Add(all[i]);
            }

            var native = example && Loc.NativeActive && !string.IsNullOrWhiteSpace(_node.hintExampleZh)
                ? _node.hintExampleZh
                : null;

            // Measure everything first so the card can hug it.
            var indent = suggestions ? BulletIndent : 0f;
            var textWidth = 0f;

            foreach (var line in lines)
                textWidth = Mathf.Max(textWidth, Mathf.Min(maxLineWidth, _measure.GetPreferredValues(line, 10000f, 0f).x));

            if (native != null)
                textWidth = Mathf.Max(textWidth, Mathf.Min(maxLineWidth, _measure.GetPreferredValues(native, 10000f, 0f).x * 0.88f));

            textWidth = Mathf.Max(textWidth, 380f);

            var cardWidth = PadX * 2f + indent + textWidth;

            // Header.
            var y = PadY;
            VrUi.Text(_panel, "Header", Layout.TopLeft(PadX, y, textWidth + indent, HeaderHeight), header, 22f,
                MonashTheme.BlueLight, TextAlignmentOptions.MidlineLeft, tracking: 5f, caps: true);
            y += HeaderHeight + 14f;

            // Lines.
            foreach (var line in lines)
            {
                var height = _measure.GetPreferredValues(line, textWidth, 0f).y;

                if (suggestions)
                    VrUi.Dot(_panel, "Bullet", Layout.TopLeft(PadX, y + (height - BulletSize) * 0.5f, BulletSize, BulletSize), MonashTheme.BlueLight);

                VrUi.Text(_panel, "Line", Layout.TopLeft(PadX + indent, y, textWidth, height), line, lineFontSize,
                    MonashTheme.Text, TextAlignmentOptions.TopLeft, tracking: 0.5f);

                y += height + LineGap;
            }

            if (native != null)
            {
                var height = _measure.GetPreferredValues(native, textWidth / 0.88f, 0f).y * 0.88f;

                VrUi.Text(_panel, "Translation", Layout.TopLeft(PadX, y - LineGap + 8f, textWidth, height), native,
                    lineFontSize * 0.88f, NativeColor, TextAlignmentOptions.TopLeft, tracking: 0.3f);

                y += height + 8f;
            }

            var cardHeight = y - LineGap + PadY;

            // The card behind it all, drawn first so everything else sits on it.
            var card = VrUi.Surface(_panel, "Card", Layout.Fill(), CardFill, 34f);
            card.transform.SetAsFirstSibling();

            _panel.sizeDelta = new Vector2(cardWidth, cardHeight);
        }

        // --- construction ----------------------------------------------------

        void Build()
        {
            _headset = ExperienceRig.IsHeadset;

            _canvas = _headset
                ? VrUi.CreateHeadCanvas("Hints", transform, new Vector2(1920f, 1080f), 2.98f,
                    distance: 1.6f, followSeconds: 0.22f, deadzoneDegrees: 0f)
                : VrUi.CreateScreenCanvas("Hints", transform, Camera.main, sortingOrder: 52);

            _canvas.sortingOrder = _headset ? 51 : 52;

            var margin = _headset ? headsetBottomMargin : bottomMargin;

            var panel = VrUi.Group(_canvas.transform, "Panel", Layout.BottomCenter(600, 100, margin));
            _panel = panel;
            _panel.anchorMin = _panel.anchorMax = new Vector2(0.5f, 0f);
            _panel.pivot = new Vector2(0.5f, 0f);
            _panel.anchoredPosition = new Vector2(0f, margin);

            _group = panel.gameObject.AddComponent<CanvasGroup>();
            _group.alpha = 0f;
            _group.blocksRaycasts = false;
            _group.interactable = false;

            // An invisible label used only to measure how big a piece of text will be.
            _measure = VrUi.Text(_canvas.transform, "Measure", Layout.Fill(), "", lineFontSize, Color.clear,
                TextAlignmentOptions.Left, tracking: 0.5f);
            _measure.overflowMode = TextOverflowModes.Overflow;
        }
    }
}

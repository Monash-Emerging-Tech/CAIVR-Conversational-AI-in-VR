using System.Text;
using CAIVR.Dialogue;
using CAIVR.Speech;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CAIVR.Demo
{
    /// <summary>
    /// Flat-screen harness for the conversation framework.
    ///
    /// Everything here is scaffolding for our own eyes - it exists so the loop
    /// (speak, listen, transcribe, branch) can be watched and judged without a
    /// headset, an environment, or a rigged professor. The shipped experience
    /// replaces this entirely with the consultation room; the components it
    /// drives underneath do not change.
    ///
    /// Keyboard-driven on purpose: no EventSystem, no raycasts, no UI prefabs to
    /// merge-conflict over while Oskar is still building the room.
    ///
    /// Builds its own UI at runtime so the scene stays a single empty GameObject
    /// and never conflicts in git.
    /// </summary>
    [RequireComponent(typeof(ConversationRunner))]
    public sealed class ConversationDemoHud : MonoBehaviour
    {
        [SerializeField] ConversationRunner runner;
        [SerializeField] SpeechService speech;

        TextMeshProUGUI _headerText;
        TextMeshProUGUI _bodyText;
        TextMeshProUGUI _transcriptText;
        TextMeshProUGUI _debugText;
        TextMeshProUGUI _footerText;

        string _professorLine = "";
        string _partial = "";
        string _lastFinal = "";
        string _lastDecision = "";
        string _notice = "";
        string _typed = "";

        float _contextShownAt;
        bool _startOffered;

        void Reset()
        {
            runner = GetComponent<ConversationRunner>();
            speech = FindFirstObjectByType<SpeechService>();
        }

        void Awake()
        {
            if (runner == null) runner = GetComponent<ConversationRunner>();
            if (speech == null) speech = FindFirstObjectByType<SpeechService>();

            BuildUi();
        }

        void OnEnable()
        {
            runner.ContextReady += OnContext;
            runner.ProfessorLine += OnProfessorLine;
            runner.PartialTranscript += OnPartial;
            runner.FinalTranscript += OnFinal;
            runner.BranchDecided += OnDecision;
            runner.StateChanged += OnStateChanged;
            runner.Notice += OnNotice;

            if (Keyboard.current != null) Keyboard.current.onTextInput += OnTextInput;
        }

        void OnDisable()
        {
            runner.ContextReady -= OnContext;
            runner.ProfessorLine -= OnProfessorLine;
            runner.PartialTranscript -= OnPartial;
            runner.FinalTranscript -= OnFinal;
            runner.BranchDecided -= OnDecision;
            runner.StateChanged -= OnStateChanged;
            runner.Notice -= OnNotice;

            if (Keyboard.current != null) Keyboard.current.onTextInput -= OnTextInput;
        }

        void Start() => runner.Prepare();

        void Update()
        {
            var keyboard = Keyboard.current;
            if (keyboard == null) return;

            switch (runner.State)
            {
                case ConversationState.ShowingContext:
                    // Workerbee #2: context first, start button appears after a beat.
                    if (!_startOffered && Time.time - _contextShownAt >= runner.ContextDelaySeconds)
                        _startOffered = true;

                    if (_startOffered && keyboard.spaceKey.wasPressedThisFrame)
                    {
                        _startOffered = false;
                        runner.Begin();
                    }
                    break;

                case ConversationState.Listening:
                    if (speech != null && speech.UsingKeyboardFallback) HandleTyping(keyboard);
                    else if (keyboard.spaceKey.wasPressedThisFrame) speech?.StartListening();
                    break;
            }

            if (keyboard.rKey.wasPressedThisFrame) runner.RepeatCurrentLine();

            if (keyboard.escapeKey.wasPressedThisFrame &&
                Application.CanStreamedLevelBeLoaded("MainMenu"))
            {
                UnityEngine.SceneManagement.SceneManager.LoadScene("MainMenu");
                return;
            }

            Redraw();
        }

        void HandleTyping(Keyboard keyboard)
        {
            if (keyboard.backspaceKey.wasPressedThisFrame && _typed.Length > 0)
                _typed = _typed.Substring(0, _typed.Length - 1);

            if (!keyboard.enterKey.wasPressedThisFrame) return;
            if (string.IsNullOrWhiteSpace(_typed)) return;

            var submission = _typed;
            _typed = "";
            speech?.SubmitTypedText(submission);
        }

        void OnTextInput(char c)
        {
            if (runner.State != ConversationState.Listening) return;
            if (speech == null || !speech.UsingKeyboardFallback) return;
            if (char.IsControl(c)) return;

            _typed += c;
        }

        void OnContext(string context)
        {
            _contextShownAt = Time.time;
            _startOffered = false;
            _professorLine = "";
            _partial = _lastFinal = _lastDecision = "";
        }

        void OnProfessorLine(string line)
        {
            _professorLine = line;
            _partial = "";
        }

        void OnPartial(string text) => _partial = text;

        void OnFinal(SpeechResult result)
        {
            _lastFinal = result.ToString();
            _partial = "";
        }

        void OnDecision(BranchDecision decision)
        {
            var node = runner.CurrentNode;

            _lastDecision = decision.Matched && node?.branches != null
                ? $"-> {node.branches[decision.BranchIndex].label}  ({decision.Confidence:0.00})  {decision.Rationale}"
                : $"-> no match  ({decision.Rationale})";
        }

        void OnStateChanged(ConversationState state)
        {
            if (state == ConversationState.Listening) _typed = "";
        }

        void OnNotice(string message) => _notice = message;

        void Redraw()
        {
            var backend = speech != null ? speech.BackendName : "no speech service";
            _headerText.text =
                $"CAIVR  |  {runner.SelectorName}  |  mic: {backend}  |  {runner.State}";

            _bodyText.text = BuildBody();
            _transcriptText.text = BuildTranscript();

            _debugText.text = string.IsNullOrEmpty(_lastDecision)
                ? ""
                : $"<color=#7FD8FF>branch  {_lastDecision}</color>";

            var footer = BuildFooter();
            _footerText.text = string.IsNullOrEmpty(footer)
                ? "<b>ESC</b> menu"
                : $"{footer}     <b>ESC</b> menu";
        }

        string BuildBody()
        {
            switch (runner.State)
            {
                case ConversationState.Idle:
                    return "<color=#FF8080>Nothing loaded. Check the Console.</color>";

                case ConversationState.ShowingContext:
                    return $"<b>Background</b>\n\n{runner.CurrentContext}";

                case ConversationState.Ended:
                    return $"<b>Professor</b>\n\n{_professorLine}\n\n<color=#9BE59B>Conversation complete.</color>";

                default:
                    // Subtitles off still shows who is speaking - the student needs
                    // to know it is their turn, they just do not get the words.
                    return Menu.CaivrSettings.SubtitlesEnabled
                        ? $"<b>Professor</b>\n\n{_professorLine}"
                        : "<b>Professor</b>\n\n<color=#555555><i>(subtitles off)</i></color>";
            }
        }

        string BuildTranscript()
        {
            var builder = new StringBuilder();

            if (!string.IsNullOrEmpty(_partial))
                builder.AppendLine($"<color=#B0B0B0><i>{_partial}...</i></color>");

            if (!string.IsNullOrEmpty(_lastFinal))
                builder.AppendLine($"<color=#FFE9A8>you said {_lastFinal}</color>");

            var typing = speech != null && speech.UsingKeyboardFallback
                         && runner.State == ConversationState.Listening;

            if (typing)
                // A literal '>' - TMP renders HTML entities verbatim rather than
                // decoding them, so "&gt;" would show up as those four characters.
                builder.AppendLine($"<color=#FFFFFF>> {_typed}<color=#888888>_</color></color>");

            if (!string.IsNullOrEmpty(_notice))
                builder.AppendLine($"<color=#FF9F9F>{_notice}</color>");

            return builder.ToString();
        }

        string BuildFooter()
        {
            var typing = speech != null && speech.UsingKeyboardFallback;

            switch (runner.State)
            {
                case ConversationState.ShowingContext:
                    return _startOffered
                        ? "<b>SPACE</b> to start"
                        : "reading the context...";

                case ConversationState.Listening:
                    return typing
                        ? "type your reply, <b>ENTER</b> to send     <b>R</b> repeat the question"
                        : "<b>speak now</b> - stops on its own     <b>SPACE</b> restart mic     <b>R</b> repeat the question";

                case ConversationState.Deciding:
                    return "thinking...";

                case ConversationState.Speaking:
                    return "<b>R</b> repeat the question";

                case ConversationState.Ended:
                    return "done - press Play again to run another";

                default:
                    return "";
            }
        }

        // --- UI construction -------------------------------------------------

        void BuildUi()
        {
            var canvasObject = new GameObject("CAIVR Demo Canvas",
                typeof(Canvas), typeof(UnityEngine.UI.CanvasScaler));
            canvasObject.transform.SetParent(transform, false);

            var canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;

            var scaler = canvasObject.GetComponent<UnityEngine.UI.CanvasScaler>();
            scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);

            AddBackdrop(canvasObject.transform);

            _headerText = AddText(canvasObject.transform, "Header",
                new Vector2(0f, 1f), new Vector2(0.65f, 1f),
                new Vector2(48f, -64f), new Vector2(0f, -16f), 24, TextAlignmentOptions.TopLeft);
            _headerText.color = new Color(0.55f, 0.75f, 0.9f);

            var version = AddText(canvasObject.transform, "Version",
                new Vector2(0.65f, 1f), new Vector2(1f, 1f),
                new Vector2(0f, -64f), new Vector2(-48f, -16f), 22, TextAlignmentOptions.TopRight);
            version.text = $"{Application.productName.ToUpperInvariant()} DEMO  v{Application.version}";
            version.color = new Color(0.45f, 0.48f, 0.54f);

            _bodyText = AddText(canvasObject.transform, "Body",
                new Vector2(0f, 0.45f), new Vector2(1f, 0.92f),
                new Vector2(96f, 0f), new Vector2(-96f, 0f), 40, TextAlignmentOptions.TopLeft);

            _transcriptText = AddText(canvasObject.transform, "Transcript",
                new Vector2(0f, 0.16f), new Vector2(1f, 0.44f),
                new Vector2(96f, 0f), new Vector2(-96f, 0f), 30, TextAlignmentOptions.TopLeft);

            _debugText = AddText(canvasObject.transform, "Debug",
                new Vector2(0f, 0.08f), new Vector2(1f, 0.15f),
                new Vector2(96f, 0f), new Vector2(-96f, 0f), 24, TextAlignmentOptions.TopLeft);

            _footerText = AddText(canvasObject.transform, "Footer",
                new Vector2(0f, 0f), new Vector2(1f, 0.07f),
                new Vector2(96f, 16f), new Vector2(-96f, 0f), 26, TextAlignmentOptions.BottomLeft);
            _footerText.color = new Color(0.7f, 0.7f, 0.7f);
        }

        static void AddBackdrop(Transform parent)
        {
            var backdrop = new GameObject("Backdrop", typeof(UnityEngine.UI.Image));
            backdrop.transform.SetParent(parent, false);

            var rect = backdrop.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            backdrop.GetComponent<UnityEngine.UI.Image>().color = new Color(0.06f, 0.07f, 0.09f, 1f);
        }

        static TextMeshProUGUI AddText(
            Transform parent, string name,
            Vector2 anchorMin, Vector2 anchorMax,
            Vector2 offsetMin, Vector2 offsetMax,
            float size, TextAlignmentOptions alignment)
        {
            var textObject = new GameObject(name, typeof(TextMeshProUGUI));
            textObject.transform.SetParent(parent, false);

            var rect = textObject.GetComponent<RectTransform>();
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;

            var text = textObject.GetComponent<TextMeshProUGUI>();
            text.fontSize = size;
            text.alignment = alignment;
            text.richText = true;
            text.color = Color.white;
            text.textWrappingMode = TextWrappingModes.Normal;

            return text;
        }
    }
}

using System;
using System.Collections;
using CAIVR.Speech;
using UnityEngine;

namespace CAIVR.Dialogue
{
    public enum ConversationState
    {
        Idle,

        /// <summary>Background context on screen, waiting for the student to press start.</summary>
        ShowingContext,

        /// <summary>Professor is delivering a line.</summary>
        Speaking,

        /// <summary>Microphone open, student's turn.</summary>
        Listening,

        /// <summary>Utterance captured, working out which branch it fits.</summary>
        Deciding,

        Ended,
    }

    public enum SelectorMode
    {
        /// <summary>System 1 - scripted keyword matching.</summary>
        Keywords,

        /// <summary>System 2 - a model picks the branch.</summary>
        AiAssisted,
    }

    /// <summary>
    /// Drives one conversation: says a line, listens, decides which branch the
    /// reply fits, moves on. This is the piece the Workerbee notes describe as
    /// "pick branch, continue conversation, proceed to next question".
    ///
    /// It deliberately knows nothing about VR. The same runner will drive the
    /// consultation room once the environment lands - the scene supplies a
    /// different presenter, not a different conversation engine.
    /// </summary>
    public sealed class ConversationRunner : MonoBehaviour
    {
        [Header("Content")]
        [Tooltip("Path under a Resources folder, without the .json extension.")]
        [SerializeField] string conversationResourcePath = "CAIVR/Conversations/consultation_demo";

        [Header("Branch selection")]
        [SerializeField] SelectorMode selectorMode = SelectorMode.Keywords;

        [Tooltip("Any OpenAI-compatible chat endpoint.\n" +
                 "Local dev:  http://localhost:11434/v1/chat/completions  (Ollama)\n" +
                 "Quest/WebGL: https://api.groq.com/openai/v1/chat/completions")]
        [SerializeField] string llmEndpoint = "http://localhost:11434/v1/chat/completions";

        [Tooltip("Local: qwen2.5:7b or llama3.2.  Groq: llama-3.3-70b-versatile.")]
        [SerializeField] string llmModel = "qwen2.5:7b";

        [Tooltip("Leave blank for a local model. Required for a cloud endpoint. " +
                 "Never commit a key - a build can be unpacked.")]
        [SerializeField] string llmApiKey = "";

        [Header("Pacing")]
        [Tooltip("Seconds the context screen shows before the start button appears (Workerbee #2 asked for 5).")]
        [SerializeField] float contextDelaySeconds = 5f;

        [Tooltip("Stand-in for how long the professor's line takes to deliver, until we have audio.")]
        [SerializeField] float secondsPerLine = 2.5f;

        [Tooltip("Give up re-prompting after this many unmatched replies and take the first branch.")]
        [SerializeField] int maxRepromptsPerNode = 2;

        [Tooltip("How many off-tree questions the professor will answer at one node " +
                 "before insisting on an answer. Stops a student stalling forever.")]
        [SerializeField] int maxAsidesPerNode = 3;

        [Header("Wiring")]
        [SerializeField] SpeechService speech;
        [SerializeField] VoiceLinePlayer voice;

        ConversationAsset _conversation;
        DialogueNode _currentNode;
        IBranchSelector _selector;
        int _repromptCount;
        int _asideCount;

        public ConversationState State { get; private set; } = ConversationState.Idle;
        public string SelectorName => _selector?.Name ?? "none";
        public string CurrentContext { get; private set; }
        public DialogueNode CurrentNode => _currentNode;

        /// <summary>Fired with the background context blurb when the conversation is armed.</summary>
        public event Action<string> ContextReady;

        /// <summary>A line for the professor to deliver. Drives subtitles, and later lipsync and TTS.</summary>
        public event Action<string> ProfessorLine;

        /// <summary>Interim speech-to-text, updated live while the student talks.</summary>
        public event Action<string> PartialTranscript;

        /// <summary>A settled utterance from the student.</summary>
        public event Action<SpeechResult> FinalTranscript;

        /// <summary>Which branch was chosen and why. Debug/telemetry.</summary>
        public event Action<BranchDecision> BranchDecided;

        public event Action<ConversationState> StateChanged;

        /// <summary>Anything the student should see: mic errors, load failures.</summary>
        public event Action<string> Notice;

        public event Action Ended;

        [Tooltip("Take the dialogue system choice from the main menu rather than the field above.")]
        [SerializeField] bool useMenuSettings = true;

        void Awake()
        {
            if (speech == null) speech = FindFirstObjectByType<SpeechService>();
            if (voice == null) voice = FindFirstObjectByType<VoiceLinePlayer>();

            // The menu is the source of truth when we came through it. Playing
            // this scene directly from the Editor still works - it just uses
            // whatever was last chosen, which is what you want while iterating.
            if (useMenuSettings)
            {
                selectorMode = (SelectorMode)Menu.CaivrSettings.SelectorMode;
                llmEndpoint = Menu.CaivrSettings.LlmEndpoint;
                llmModel = Menu.CaivrSettings.LlmModel;
                llmApiKey = Menu.CaivrSettings.LlmApiKey;
            }

            _selector = BuildSelector();
        }

        void OnEnable()
        {
            if (speech == null) return;
            speech.PartialResult += OnPartial;
            speech.FinalResult += OnFinal;
            speech.Stopped += OnListeningStopped;
            speech.Error += OnSpeechError;
        }

        void OnDisable()
        {
            if (speech == null) return;
            speech.PartialResult -= OnPartial;
            speech.FinalResult -= OnFinal;
            speech.Stopped -= OnListeningStopped;
            speech.Error -= OnSpeechError;
        }

        IBranchSelector BuildSelector()
        {
            if (selectorMode == SelectorMode.Keywords) return new KeywordBranchSelector();

            // No availability check here on purpose: an endpoint being down is a
            // runtime condition, not a startup one, and the selector reports it
            // per-request so the runner can downgrade with a visible message.
            return new LlmBranchSelector(this, llmEndpoint, llmModel, ResolveApiKey());
        }

        /// <summary>
        /// Where the API key comes from, in order of preference.
        ///
        /// Environment variable first, so a developer's key lives in their shell
        /// and never gets written into the project, a PlayerPrefs entry, or a
        /// commit. Falls back to the settings value for cases where an env var
        /// is not available.
        ///
        /// Neither route is acceptable for a shipped build: environment
        /// variables do not exist on Quest or WebGL, and anything embedded in a
        /// build can be extracted from it. Production has to route completions
        /// through a server of ours that holds the key.
        /// </summary>
        string ResolveApiKey()
        {
            if (!string.IsNullOrWhiteSpace(llmApiKey)) return llmApiKey.Trim();
            return Menu.CaivrSettings.ResolveLlmApiKey();
        }

        /// <summary>Load the script and show the background context. Does not start talking yet.</summary>
        public void Prepare()
        {
            _conversation = Load(conversationResourcePath);

            if (_conversation == null)
            {
                Notice?.Invoke($"Could not load conversation at Resources/{conversationResourcePath}.json");
                SetState(ConversationState.Idle);
                return;
            }

            var problems = _conversation.Validate();
            foreach (var problem in problems) Debug.LogError($"[CAIVR] Script problem: {problem}");
            if (problems.Count > 0)
            {
                Notice?.Invoke($"Conversation script has {problems.Count} problem(s) - see Console.");
                SetState(ConversationState.Idle);
                return;
            }

            // Ground System 2 in this script's world before it has to answer
            // anything the tree does not cover.
            if (_selector is LlmBranchSelector grounded)
                grounded.SetWorldFacts(_conversation.worldFacts);

            CurrentContext = _conversation.PickContext();
            SetState(ConversationState.ShowingContext);
            ContextReady?.Invoke(CurrentContext);
        }

        static ConversationAsset Load(string resourcePath)
        {
            var asset = Resources.Load<TextAsset>(resourcePath);
            if (asset == null) return null;

            try
            {
                return JsonUtility.FromJson<ConversationAsset>(asset.text);
            }
            catch (Exception e)
            {
                Debug.LogError($"[CAIVR] Conversation JSON is malformed: {e.Message}");
                return null;
            }
        }

        /// <summary>How long the HUD should wait before offering the start button.</summary>
        public float ContextDelaySeconds => contextDelaySeconds;

        public void Begin()
        {
            if (_conversation == null)
            {
                Notice?.Invoke("Nothing loaded - call Prepare() first.");
                return;
            }

            EnterNode(_conversation.GetNode(_conversation.startNodeId));
        }

        void EnterNode(DialogueNode node)
        {
            _currentNode = node;
            _repromptCount = 0;
            _asideCount = 0;

            if (node == null)
            {
                Notice?.Invoke("Conversation walked off the end of the script.");
                Finish();
                return;
            }

            StopAllCoroutines();
            StartCoroutine(SpeakThenListen(node));
        }

        IEnumerator SpeakThenListen(DialogueNode node)
        {
            SetState(ConversationState.Speaking);

            yield return Speak(node.id, node.speakerLine);

            if (node.isEnd)
            {
                Finish();
                yield break;
            }

            Listen();
        }

        /// <summary>
        /// Delivers one line: raises the subtitle event, speaks it, and yields
        /// until the audio has finished.
        ///
        /// The player handles the fallback chain itself - baked clip, then
        /// runtime synthesis, then a timed subtitle - so an unscripted line
        /// still gets a voice and a missing voice service still gets read.
        /// </summary>
        IEnumerator Speak(string clipKey, string line)
        {
            ProfessorLine?.Invoke(line);

            if (voice != null)
            {
                yield return voice.SpeakRoutine(clipKey, line, DurationFor(line));
                yield break;
            }

            yield return new WaitForSeconds(DurationFor(line));
        }

        /// <summary>
        /// Speaks a line that was never authored - a generated reply, say - so
        /// it goes straight to runtime synthesis with no clip key to look up.
        /// </summary>
        public IEnumerator SpeakUnscripted(string line) => Speak(null, line);

        /// <summary>
        /// Fallback pacing when a line has no audio. Scales with length so long
        /// lines are not cut off and short ones do not leave dead air.
        /// </summary>
        float DurationFor(string line)
        {
            if (string.IsNullOrEmpty(line)) return 0.5f;

            var words = line.Split(' ').Length;
            return Mathf.Clamp(words * 0.32f, 1.2f, secondsPerLine * 4f);
        }

        void Listen()
        {
            SetState(ConversationState.Listening);
            speech?.StartListening();
        }

        /// <summary>
        /// "Ask to repeat that" from the Workerbee #1 accessibility list.
        /// Re-delivers the current line without penalising the student's turn.
        /// </summary>
        public void RepeatCurrentLine()
        {
            if (_currentNode == null) return;
            if (State != ConversationState.Listening && State != ConversationState.Speaking) return;

            StopListeningQuietly();
            voice?.Stop();
            StopAllCoroutines();
            StartCoroutine(SpeakThenListen(_currentNode));
        }

        void OnPartial(string text)
        {
            if (State != ConversationState.Listening) return;
            PartialTranscript?.Invoke(text);
        }

        void OnFinal(SpeechResult result)
        {
            if (State != ConversationState.Listening) return;

            StopListeningQuietly();
            FinalTranscript?.Invoke(result);

            SetState(ConversationState.Deciding);

            var deciding = _currentNode;
            _selector.Select(deciding, result.Text, decision =>
            {
                // The AI selector answers on a later frame, by which point the
                // student may have quit or restarted. Ignore stale answers.
                if (_currentNode != deciding) return;
                if (State != ConversationState.Deciding) return;

                // System 2 with nothing behind it would fail identically on every
                // remaining turn, and the student would just see the conversation
                // lurching forward for no visible reason. Say so once, drop to
                // System 1, and redo this turn rather than burning it.
                if (!decision.Matched
                    && _selector is LlmBranchSelector llm
                    && llm.LastCallFailedToConnect)
                {
                    _selector = new KeywordBranchSelector();

                    Notice?.Invoke(
                        "System 2 endpoint unreachable - using System 1 (keywords) instead. " +
                        "Point System 2 at a reachable endpoint to use it.");

                    Debug.LogWarning($"[CAIVR] {decision.Rationale} Downgraded to System 1.");

                    _selector.Select(deciding, result.Text, retry =>
                    {
                        if (_currentNode != deciding) return;
                        if (State != ConversationState.Deciding) return;
                        Apply(retry, deciding);
                    });
                    return;
                }

                Apply(decision, deciding);
            });
        }

        void Apply(BranchDecision decision, DialogueNode node)
        {
            BranchDecided?.Invoke(decision);

            if (decision.Matched)
            {
                var branch = node.branches[decision.BranchIndex];
                EnterNode(_conversation.GetNode(branch.nextNodeId));
                return;
            }

            // The student asked something real that no branch covers. Answering
            // it and then returning to the question is the difference between a
            // conversation and a form: understanding someone and then ignoring
            // them is worse than not understanding them at all.
            if (decision.HasSideReply && _asideCount < maxAsidesPerNode)
            {
                _asideCount++;
                StopAllCoroutines();
                StartCoroutine(AnswerAside(node, decision.SideReply));
                return;
            }

            _repromptCount++;

            if (_repromptCount > maxRepromptsPerNode && node.branches.Length > 0)
            {
                // Better to move the scene along than to trap the student in a
                // loop the recogniser cannot get them out of - but say plainly
                // that this is the system failing, not the student.
                Notice?.Invoke(
                    $"Couldn't match that after {maxRepromptsPerNode} tries - " +
                    "taking the first branch so the conversation continues.");

                Debug.LogWarning(
                    $"[CAIVR] Unmatched at node '{node.id}'. Consider adding keywords " +
                    "for this phrasing, or switch to System 2.");
                EnterNode(_conversation.GetNode(node.branches[0].nextNodeId));
                return;
            }

            StopAllCoroutines();
            StartCoroutine(Reprompt(node));
        }

        /// <summary>
        /// Answers a question the tree has no branch for, then puts its own
        /// question back on the table so the thread is not lost.
        ///
        /// The aside has no baked audio - it did not exist until a moment ago -
        /// so it goes through runtime synthesis. The re-ask does have baked
        /// audio, so the conversation lands back on solid ground instantly.
        /// </summary>
        IEnumerator AnswerAside(DialogueNode node, string reply)
        {
            SetState(ConversationState.Speaking);

            yield return Speak(null, reply);

            // The reprompt line is a shorter restatement of the node's question,
            // which is exactly what is wanted after a digression - re-reading the
            // full opening line would sound like the professor lost their place.
            var hasReprompt = !string.IsNullOrWhiteSpace(node.reprompt);

            yield return hasReprompt
                ? Speak($"{node.id}_reprompt", node.reprompt)
                : Speak(node.id, node.speakerLine);

            Listen();
        }

        IEnumerator Reprompt(DialogueNode node)
        {
            SetState(ConversationState.Speaking);

            var hasOwnLine = !string.IsNullOrWhiteSpace(node.reprompt);

            var line = hasOwnLine
                ? node.reprompt
                : "Sorry, I didn't catch that. Could you say it again?";

            var clipKey = hasOwnLine
                ? $"{node.id}_reprompt"
                : VoiceLinePlayer.FallbackRepromptKey;

            yield return Speak(clipKey, line);

            Listen();
        }

        /// <summary>
        /// True while we are the ones closing the microphone.
        ///
        /// Recognisers raise Stopped synchronously from inside StopListening, so
        /// without this the runner hears its own stop, still sees the state as
        /// Listening, and re-prompts the question the student just answered.
        /// </summary>
        bool _stoppingDeliberately;

        /// <summary>Closes the mic without treating it as the student going quiet.</summary>
        void StopListeningQuietly()
        {
            _stoppingDeliberately = true;
            try { speech?.StopListening(); }
            finally { _stoppingDeliberately = false; }
        }

        void OnListeningStopped(string reason)
        {
            // Our own stop, not the student trailing off. Ignore it.
            if (_stoppingDeliberately) return;

            // Silence timeout while we were waiting on them: nudge, don't stall.
            if (State != ConversationState.Listening) return;
            if (_currentNode == null) return;

            StopAllCoroutines();
            StartCoroutine(Reprompt(_currentNode));
        }

        void OnSpeechError(string message)
        {
            Notice?.Invoke(message);

            if (State != ConversationState.Listening) return;
            if (_currentNode == null) return;

            StopAllCoroutines();
            StartCoroutine(Reprompt(_currentNode));
        }

        void Finish()
        {
            StopListeningQuietly();
            SetState(ConversationState.Ended);
            Ended?.Invoke();
        }

        void SetState(ConversationState next)
        {
            if (State == next) return;
            State = next;
            StateChanged?.Invoke(next);
        }
    }
}

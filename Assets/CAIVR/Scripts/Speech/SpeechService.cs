using System;
using UnityEngine;

namespace CAIVR.Speech
{
    public enum SpeechBackend
    {
        /// <summary>
        /// Typed input. The default because it is the only backend that works
        /// on every platform with no OS settings, no permissions and no
        /// install - press Play and it works.
        /// </summary>
        Keyboard,

        /// <summary>
        /// Windows dictation. Opt-in only: it needs "Online speech recognition"
        /// switched on in Windows privacy settings, it sends audio to Microsoft,
        /// and it does not exist on Android, so it cannot run on Quest.
        /// </summary>
        WindowsDictation,

        /// <summary>Use the mic if it is genuinely usable, otherwise typing.</summary>
        Auto,

        /// <summary>
        /// Real microphone input via a Whisper endpoint. Needs a mic and an API
        /// key, but no OS settings - and unlike Windows dictation it runs on
        /// Quest and in WebGL builds.
        /// </summary>
        Whisper,
    }

    /// <summary>
    /// Scene-facing owner of whichever <see cref="ISpeechRecognizer"/> we are using.
    ///
    /// Everything else in CAIVR listens to this component, not to a concrete
    /// recogniser, so swapping the Quest backend in later is a change to one
    /// switch statement rather than a change to the conversation code.
    /// </summary>
    public sealed class SpeechService : MonoBehaviour
    {
        [Tooltip("Keyboard works everywhere with zero setup. Mic backends are opt-in.")]
        [SerializeField] SpeechBackend backend = SpeechBackend.Keyboard;

        [Tooltip("Ignore transcriptions the backend is not confident about.")]
        [Range(0f, 1f)]
        [SerializeField] float minimumConfidence = 0.3f;

        ISpeechRecognizer _recognizer;

        public ISpeechRecognizer Recognizer => _recognizer;
        public string BackendName => _recognizer?.BackendName ?? "none";
        public bool IsListening => _recognizer?.IsListening ?? false;

        /// <summary>True when we fell back to typing because the mic was unusable.</summary>
        public bool UsingKeyboardFallback => _recognizer is KeyboardRecognizer;

        public event Action<string> PartialResult;
        public event Action<SpeechResult> FinalResult;
        public event Action<string> Stopped;
        public event Action<string> Error;

        void Awake() => Build();

        void OnDestroy() => Teardown();

        void Build()
        {
            _recognizer = Create(backend);
            _recognizer.PartialResult += OnPartial;
            _recognizer.FinalResult += OnFinal;
            _recognizer.Stopped += OnStopped;
            _recognizer.Error += OnError;
            _recognizer.Initialize();

            Debug.Log($"[CAIVR] Speech backend: {_recognizer.BackendName}");
        }

        ISpeechRecognizer Create(SpeechBackend requested)
        {
            switch (requested)
            {
                case SpeechBackend.Keyboard:
                    return new KeyboardRecognizer();

                case SpeechBackend.WindowsDictation:
                    return new WindowsDictationRecognizer();

                case SpeechBackend.Whisper:
                    return BuildWhisper();

                default:
                    // Probe rather than assume, and prefer the backend that will
                    // still exist on a headset. Whisper needs a mic and a key;
                    // dictation needs Windows with a privacy setting enabled;
                    // typing always works. Degrading is better than handing the
                    // student a microphone button that does nothing.
                    var whisper = BuildWhisper();
                    if (whisper.IsAvailable) return whisper;

                    var dictation = new WindowsDictationRecognizer();
                    if (dictation.IsAvailable) return dictation;

                    Debug.LogWarning(
                        "[CAIVR] No usable microphone backend - falling back to typed input. " +
                        "Whisper needs a microphone and an API key.");
                    return new KeyboardRecognizer();
            }
        }

        WhisperRecognizer BuildWhisper() => new WhisperRecognizer(
            this,
            Menu.CaivrSettings.SttEndpoint,
            Menu.CaivrSettings.SttModel,
            Menu.CaivrSettings.ResolveLlmApiKey());

        void Teardown()
        {
            if (_recognizer == null) return;

            _recognizer.PartialResult -= OnPartial;
            _recognizer.FinalResult -= OnFinal;
            _recognizer.Stopped -= OnStopped;
            _recognizer.Error -= OnError;
            _recognizer.Shutdown();
            _recognizer = null;
        }

        public void StartListening() => _recognizer?.StartListening();
        public void StopListening() => _recognizer?.StopListening();

        /// <summary>Route typed text in when the keyboard backend is active.</summary>
        public void SubmitTypedText(string text)
        {
            if (_recognizer is KeyboardRecognizer keyboard) keyboard.Submit(text);
        }

        void OnPartial(string text) => PartialResult?.Invoke(text);

        void OnFinal(SpeechResult result)
        {
            // A rejected transcription is worse than no transcription: acting on
            // it sends the conversation down a branch the student never asked
            // for. Surface it as an error so the runner re-prompts instead.
            if (result.Confidence < minimumConfidence)
            {
                Error?.Invoke($"Low confidence transcription discarded: {result}");
                return;
            }

            FinalResult?.Invoke(result);
        }

        void OnStopped(string reason) => Stopped?.Invoke(reason);

        void OnError(string message)
        {
            // A mic backend that cannot start is not a recoverable error - it is
            // a dead input channel, and every following turn would fail the same
            // way. Drop to typing so the conversation stays usable instead of
            // showing the student an error they cannot act on.
            if (!(_recognizer is KeyboardRecognizer))
            {
                Debug.LogWarning($"[CAIVR] Speech backend failed, switching to typed input. {message}");
                _pendingFallback = true;
                return;
            }

            Error?.Invoke(message);
        }

        bool _pendingFallback;

        void Update()
        {
            if (!_pendingFallback) return;
            _pendingFallback = false;

            // Deferred to a frame boundary: tearing the recogniser down inside
            // its own error callback risks disposing it mid-dispatch.
            var wasListening = IsListening;

            Teardown();

            _recognizer = new KeyboardRecognizer();
            _recognizer.PartialResult += OnPartial;
            _recognizer.FinalResult += OnFinal;
            _recognizer.Stopped += OnStopped;
            _recognizer.Error += OnError;
            _recognizer.Initialize();

            BackendChanged?.Invoke(_recognizer.BackendName);

            if (wasListening) _recognizer.StartListening();
        }

        /// <summary>Raised when the backend swaps at runtime, so the HUD can relabel.</summary>
        public event Action<string> BackendChanged;
    }
}

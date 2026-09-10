using System;
using UnityEngine;

namespace CAIVR.Speech
{
    public enum SpeechBackend
    {
        /// <summary>Use the mic if the platform supports it, otherwise fall back to typing.</summary>
        Auto,
        WindowsDictation,
        Keyboard,
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
        [Tooltip("Auto picks the microphone backend when the platform supports it, else typing.")]
        [SerializeField] SpeechBackend backend = SpeechBackend.Auto;

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

        static ISpeechRecognizer Create(SpeechBackend requested)
        {
            switch (requested)
            {
                case SpeechBackend.Keyboard:
                    return new KeyboardRecognizer();

                case SpeechBackend.WindowsDictation:
                    return new WindowsDictationRecognizer();

                default:
                    // Probe rather than assume: dictation reports unavailable on
                    // non-Windows platforms and on machines with the speech
                    // service switched off, and we would rather degrade to
                    // typing than hand the user a dead microphone button.
                    var dictation = new WindowsDictationRecognizer();
                    if (dictation.IsAvailable) return dictation;

                    Debug.LogWarning(
                        "[CAIVR] Windows dictation unavailable - falling back to typed input. " +
                        "On Windows, check Settings > Privacy > Speech > Online speech recognition.");
                    return new KeyboardRecognizer();
            }
        }

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
        void OnError(string message) => Error?.Invoke(message);
    }
}

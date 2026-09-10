using System;
using UnityEngine;
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
using UnityEngine.Windows.Speech;
#endif

namespace CAIVR.Speech
{
    /// <summary>
    /// Speech-to-text via Windows' built-in dictation engine.
    ///
    /// Zero setup, no API key, no per-minute cost, and it runs in the Editor,
    /// which is what makes it the right pick for proving the framework out.
    ///
    /// Hard limits worth knowing before we lean on it:
    ///   - Windows only. It compiles out entirely on Android, so it will NOT
    ///     work on a standalone Quest build. Quest needs a different backend
    ///     behind <see cref="ISpeechRecognizer"/>.
    ///   - Requires Settings > Privacy > Speech > "Online speech recognition"
    ///     to be ON. With it off, Start() throws or silently never fires.
    ///   - Cannot coexist with a running KeywordRecognizer/GrammarRecognizer.
    /// </summary>
    public sealed class WindowsDictationRecognizer : ISpeechRecognizer
    {
        public string BackendName => "Windows Dictation";

        public event Action<string> PartialResult;
        public event Action<SpeechResult> FinalResult;
        public event Action<string> Stopped;
        public event Action<string> Error;

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        DictationRecognizer _recognizer;
        bool _initialized;

        // Windows reports confidence as a coarse enum. The conversation layer
        // wants a number it can threshold against, so we flatten it here rather
        // than leaking a platform enum into the dialogue code.
        static float ToScore(ConfidenceLevel level) => level switch
        {
            ConfidenceLevel.High => 1.0f,
            ConfidenceLevel.Medium => 0.7f,
            ConfidenceLevel.Low => 0.4f,
            _ => 0.0f, // Rejected
        };

        public bool IsAvailable => PhraseRecognitionSystem.isSupported;

        public bool IsListening =>
            _recognizer != null && _recognizer.Status == SpeechSystemStatus.Running;

        public void Initialize()
        {
            if (_initialized) return;

            if (!IsAvailable)
            {
                Error?.Invoke("Windows speech recognition is not supported on this machine.");
                return;
            }

            _recognizer = new DictationRecognizer();

            // Give the student room to think before and mid-sentence. The
            // defaults cut off far too eagerly for a consultation where people
            // pause to phrase a question.
            _recognizer.InitialSilenceTimeoutSeconds = 12f;
            _recognizer.AutoSilenceTimeoutSeconds = 3f;

            _recognizer.DictationHypothesis += OnHypothesis;
            _recognizer.DictationResult += OnResult;
            _recognizer.DictationComplete += OnComplete;
            _recognizer.DictationError += OnError;

            _initialized = true;
        }

        public void StartListening()
        {
            if (!_initialized) Initialize();
            if (_recognizer == null) return;
            if (_recognizer.Status == SpeechSystemStatus.Running) return;

            try
            {
                _recognizer.Start();
            }
            catch (Exception e)
            {
                // Almost always "online speech recognition is disabled".
                Error?.Invoke($"Could not start dictation: {e.Message}");
            }
        }

        public void StopListening()
        {
            if (_recognizer == null) return;
            if (_recognizer.Status != SpeechSystemStatus.Running) return;
            _recognizer.Stop();
        }

        public void Shutdown()
        {
            if (_recognizer == null) return;

            _recognizer.DictationHypothesis -= OnHypothesis;
            _recognizer.DictationResult -= OnResult;
            _recognizer.DictationComplete -= OnComplete;
            _recognizer.DictationError -= OnError;

            if (_recognizer.Status == SpeechSystemStatus.Running) _recognizer.Stop();
            _recognizer.Dispose();
            _recognizer = null;
            _initialized = false;
        }

        void OnHypothesis(string text) => PartialResult?.Invoke(text);

        void OnResult(string text, ConfidenceLevel confidence)
            => FinalResult?.Invoke(new SpeechResult(text, ToScore(confidence)));

        void OnComplete(DictationCompletionCause cause)
        {
            // TimeoutExceeded just means they went quiet. That is a normal end
            // to a turn, not a failure, so it goes to Stopped and the runner
            // decides whether to re-prompt.
            if (cause == DictationCompletionCause.Complete ||
                cause == DictationCompletionCause.TimeoutExceeded)
            {
                Stopped?.Invoke(cause.ToString());
            }
            else
            {
                Error?.Invoke($"Dictation ended: {cause}");
            }
        }

        void OnError(string error, int hresult)
            => Error?.Invoke($"{error} (hresult 0x{hresult:X})");
#else
        // Non-Windows builds still need the type to exist so the rest of the
        // project compiles; it simply reports itself as unavailable.
        public bool IsAvailable => false;
        public bool IsListening => false;

        public void Initialize()
            => Error?.Invoke("Windows dictation is unavailable on this platform.");

        public void StartListening()
            => Error?.Invoke("Windows dictation is unavailable on this platform.");

        public void StopListening() { }
        public void Shutdown() { }
#endif
    }
}

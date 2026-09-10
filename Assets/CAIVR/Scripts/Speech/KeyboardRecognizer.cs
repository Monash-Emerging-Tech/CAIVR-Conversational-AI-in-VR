using System;

namespace CAIVR.Speech
{
    /// <summary>
    /// Types instead of talks. Not a toy: it is the fallback whenever the mic
    /// backend is unavailable, and it makes the dialogue logic testable without
    /// a microphone or a quiet room, which matters for demoing to stakeholders.
    /// The HUD calls <see cref="Submit"/> when the user presses Enter.
    /// </summary>
    public sealed class KeyboardRecognizer : ISpeechRecognizer
    {
        public string BackendName => "Keyboard (typed)";

        public bool IsAvailable => true;
        public bool IsListening { get; private set; }

        public event Action<string> PartialResult;
        public event Action<SpeechResult> FinalResult;
        public event Action<string> Stopped;
        public event Action<string> Error;

        public void Initialize() { }

        public void StartListening() => IsListening = true;

        public void StopListening()
        {
            if (!IsListening) return;
            IsListening = false;
            Stopped?.Invoke("Manual stop");
        }

        public void Shutdown() => IsListening = false;

        /// <summary>Called by the HUD's input field. Typed text is always full confidence.</summary>
        public void Submit(string text)
        {
            if (!IsListening) return;
            if (string.IsNullOrWhiteSpace(text)) return;

            IsListening = false;
            FinalResult?.Invoke(new SpeechResult(text.Trim(), 1f));
        }

        /// <summary>Optional live echo as the user types, mirroring mic hypotheses.</summary>
        public void ReportTyping(string partial) => PartialResult?.Invoke(partial);

        // Kept so the compiler does not warn about Error never being raised.
        public void ReportError(string message) => Error?.Invoke(message);
    }
}

using System;

namespace CAIVR.Speech
{
    /// <summary>
    /// Platform-agnostic speech-to-text contract.
    ///
    /// The rest of CAIVR only ever talks to this interface, so the underlying
    /// recogniser can be swapped without touching the conversation code. That
    /// matters because the current Windows implementation will not run on Quest:
    /// when we get there we drop in a cloud or on-device recogniser behind this
    /// same interface and nothing downstream changes.
    /// </summary>
    public interface ISpeechRecognizer
    {
        /// <summary>False when the backend cannot run here (wrong platform, no mic, dictation disabled).</summary>
        bool IsAvailable { get; }

        /// <summary>True between a successful StartListening and the matching stop.</summary>
        bool IsListening { get; }

        /// <summary>Human-readable name shown in the demo HUD.</summary>
        string BackendName { get; }

        /// <summary>Interim, low-confidence text while the user is still speaking. May fire many times.</summary>
        event Action<string> PartialResult;

        /// <summary>A settled utterance. This is what drives the conversation forward.</summary>
        event Action<SpeechResult> FinalResult;

        /// <summary>Listening ended on its own (silence timeout, cancellation, backend failure).</summary>
        event Action<string> Stopped;

        /// <summary>Something went wrong. The message is safe to show in the HUD.</summary>
        event Action<string> Error;

        void Initialize();
        void StartListening();
        void StopListening();
        void Shutdown();
    }

    /// <summary>A settled transcription plus how much the backend trusts it.</summary>
    public readonly struct SpeechResult
    {
        public readonly string Text;
        public readonly float Confidence;

        public SpeechResult(string text, float confidence)
        {
            Text = text;
            Confidence = confidence;
        }

        public override string ToString() => $"\"{Text}\" ({Confidence:0.00})";
    }
}

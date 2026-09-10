using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

namespace CAIVR.Speech
{
    /// <summary>
    /// Real microphone speech-to-text, via a Whisper endpoint.
    ///
    /// Records from the device the student picked in the menu, waits for them to
    /// stop talking, and uploads the audio for transcription. Unlike Windows
    /// dictation this needs no OS setting, works on any platform Unity records
    /// audio on, and - importantly - runs on a standalone Quest and in a WebGL
    /// build, because all it needs is an HTTP call.
    ///
    /// Defaults to Groq's whisper-large-v3-turbo, which is on the same free key
    /// as the dialogue model.
    ///
    /// The tradeoff against dictation is that this is turn-based rather than
    /// streaming: nothing is transcribed until the student stops speaking, so
    /// there are no live partial results, and there is a round trip afterwards.
    /// For a consultation - where people speak in whole sentences and then wait -
    /// that suits the interaction better than word-by-word transcription would.
    /// </summary>
    public sealed class WhisperRecognizer : ISpeechRecognizer
    {
        public string BackendName => "Microphone (Whisper)";

        public event Action<string> PartialResult;
        public event Action<SpeechResult> FinalResult;
        public event Action<string> Stopped;
        public event Action<string> Error;

        readonly MonoBehaviour _host;
        readonly string _endpoint;
        readonly string _model;
        readonly string _apiKey;

        // Tuning. These decide whether the professor feels attentive or impatient.
        const int MaxSeconds = 30;          // hard cap on one utterance
        const float StartLevel = 0.020f;    // RMS that counts as "started talking"
        const float SilenceLevel = 0.012f;  // RMS that counts as "stopped talking"
        const float SilenceHold = 1.1f;     // silence needed before we call it done
        const float NoSpeechTimeout = 10f;  // give up if they never start

        string _device;
        AudioClip _clip;
        Coroutine _listenRoutine;
        bool _uploading;

        public bool IsAvailable => Microphone.devices != null && Microphone.devices.Length > 0
                                   && !string.IsNullOrEmpty(_apiKey);

        public bool IsListening { get; private set; }

        public WhisperRecognizer(MonoBehaviour host, string endpoint, string model, string apiKey)
        {
            _host = host;
            _endpoint = endpoint;
            _model = model;
            _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        }

        public void Initialize()
        {
            if (Microphone.devices == null || Microphone.devices.Length == 0)
            {
                Error?.Invoke("No microphone found.");
                return;
            }

            if (string.IsNullOrEmpty(_apiKey))
                Error?.Invoke("No API key for speech-to-text.");
        }

        public void StartListening()
        {
            if (IsListening || _uploading) return;
            if (!IsAvailable)
            {
                Error?.Invoke("Microphone speech-to-text is not available.");
                return;
            }

            _device = Menu.CaivrSettings.ResolveMicrophone();

            // Whisper wants 16 kHz. Ask for it, but respect what the device can
            // actually do - a headset mic that only does 48 kHz would otherwise
            // fail to open at all.
            Microphone.GetDeviceCaps(_device, out var minFreq, out var maxFreq);
            var frequency = 16000;
            if (maxFreq > 0) frequency = Mathf.Clamp(frequency, Mathf.Max(minFreq, 1), maxFreq);

            try
            {
                _clip = Microphone.Start(_device, false, MaxSeconds, frequency);
            }
            catch (Exception e)
            {
                Error?.Invoke($"Could not open the microphone: {e.Message}");
                return;
            }

            if (_clip == null)
            {
                Error?.Invoke("Could not open the microphone.");
                return;
            }

            IsListening = true;
            _listenRoutine = _host.StartCoroutine(Listen());
        }

        /// <summary>
        /// Watches the level and ends the turn once they have stopped speaking.
        /// A fixed record-then-send window would either clip people off or make
        /// them wait after they had finished; neither reads as conversation.
        /// </summary>
        IEnumerator Listen()
        {
            var window = new float[512];
            var speechStarted = false;
            var silenceFor = 0f;
            var elapsed = 0f;

            while (IsListening)
            {
                yield return null;
                elapsed += Time.deltaTime;

                var position = Microphone.GetPosition(_device);

                if (position > window.Length)
                {
                    _clip.GetData(window, position - window.Length);
                    var level = WavEncoder.Rms(window, 0, window.Length);

                    if (!speechStarted)
                    {
                        if (level > StartLevel)
                        {
                            speechStarted = true;
                            PartialResult?.Invoke("listening…");
                        }
                        else if (elapsed > NoSpeechTimeout)
                        {
                            Abort("I didn't hear anything.");
                            yield break;
                        }
                    }
                    else
                    {
                        silenceFor = level < SilenceLevel ? silenceFor + Time.deltaTime : 0f;

                        if (silenceFor >= SilenceHold) break;   // they've finished
                    }
                }

                if (elapsed >= MaxSeconds - 0.25f) break;       // hard cap
            }

            if (!IsListening) yield break;

            var samples = Microphone.GetPosition(_device);
            Microphone.End(_device);
            IsListening = false;

            if (!speechStarted || samples <= 0)
            {
                Stopped?.Invoke("No speech captured.");
                yield break;
            }

            PartialResult?.Invoke("transcribing…");
            yield return Transcribe(WavEncoder.Encode(_clip, samples));
        }

        void Abort(string reason)
        {
            if (Microphone.IsRecording(_device)) Microphone.End(_device);
            IsListening = false;
            Stopped?.Invoke(reason);
        }

        IEnumerator Transcribe(byte[] wav)
        {
            if (wav == null || wav.Length < 1024)
            {
                Stopped?.Invoke("Recording too short to transcribe.");
                yield break;
            }

            _uploading = true;

            var form = new List<IMultipartFormSection>
            {
                new MultipartFormFileSection("file", wav, "speech.wav", "audio/wav"),
                new MultipartFormDataSection("model", _model),
                new MultipartFormDataSection("response_format", "json"),
                // Pinning the language stops short utterances being detected as
                // another language and coming back as a translation.
                new MultipartFormDataSection("language", "en"),
            };

            using var request = UnityWebRequest.Post(_endpoint, form);
            request.SetRequestHeader("Authorization", $"Bearer {_apiKey}");
            request.SetRequestHeader("User-Agent", "CAIVR/1.0 (Unity)");
            request.timeout = 30;

            yield return request.SendWebRequest();

            _uploading = false;

            if (request.result != UnityWebRequest.Result.Success)
            {
                Error?.Invoke($"Transcription failed ({request.responseCode}): {request.error}");
                yield break;
            }

            string text;
            try
            {
                text = JsonUtility.FromJson<TranscriptionResponse>(request.downloadHandler.text)?.text;
            }
            catch (Exception e)
            {
                Error?.Invoke($"Could not read transcription: {e.Message}");
                yield break;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                Stopped?.Invoke("Nothing was transcribed.");
                yield break;
            }

            // Whisper reports no confidence score in this response format, and
            // inventing one would be worse than being honest - the conversation
            // layer thresholds on it.
            FinalResult?.Invoke(new SpeechResult(text.Trim(), 1f));
        }

        public void StopListening()
        {
            if (!IsListening) return;

            IsListening = false;

            if (_listenRoutine != null)
            {
                _host.StopCoroutine(_listenRoutine);
                _listenRoutine = null;
            }

            if (Microphone.IsRecording(_device)) Microphone.End(_device);

            Stopped?.Invoke("Stopped.");
        }

        public void Shutdown()
        {
            if (_listenRoutine != null && _host != null) _host.StopCoroutine(_listenRoutine);
            _listenRoutine = null;

            if (!string.IsNullOrEmpty(_device) && Microphone.IsRecording(_device))
                Microphone.End(_device);

            IsListening = false;
            _clip = null;
        }

        [Serializable]
        class TranscriptionResponse
        {
            public string text;
        }
    }
}

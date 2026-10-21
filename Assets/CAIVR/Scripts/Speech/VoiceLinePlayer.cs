using System.Collections;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Networking;

namespace CAIVR.Speech
{
    /// <summary>
    /// Gives the professor a voice, just in time.
    ///
    /// Every line, scripted or generated, is spoken live by the voice server (Tools/voice_server.py).
    /// Audio is streamed while it is still being produced, so she starts talking a fraction of a
    /// second after the request. The server caches every line it has said, so a line is slow at
    /// most once and instant after that; it also warms the scripted lines in the background.
    ///
    /// Baked clips are the offline safety net, not the main path. If the server cannot be reached,
    /// the line falls back to its baked clip, then to a timed subtitle - never to silence or a
    /// stalled conversation.
    ///
    /// Start the server with:  python Tools/voice_server.py --prewarm
    /// </summary>
    public sealed class VoiceLinePlayer : MonoBehaviour
    {
        /// <summary>Used when a node has no reprompt line of its own.</summary>
        public const string FallbackRepromptKey = "_fallback_reprompt";

        const int MaxLineSeconds = 60;

        [Header("Live voice (just in time)")]
        [Tooltip("Speak lines live from the voice server. Turn off to use baked clips only.")]
        [SerializeField] bool useRuntimeSynthesis = true;

        [Tooltip("Base address of Tools/voice_server.py. A Quest needs the PC's address on the local network.")]
        [SerializeField] string serverUrl = "http://127.0.0.1:5111";

        [Tooltip("Which server voice to use, for example voxcpm_warm. Empty means the server's default.")]
        [SerializeField] string liveVoice = "voxcpm_warm";

        [Tooltip("Audio to hold back before playback starts, so a slow moment does not interrupt the line.")]
        [SerializeField] float prebufferSeconds = 0.6f;

        [Tooltip("If the server falls behind mid-line, wait for this much audio before carrying on.")]
        [SerializeField] float rebufferSeconds = 0.5f;

        [Tooltip("Give up on the server if nothing has played after this long.")]
        [SerializeField] float firstAudioTimeoutSeconds = 10f;

        [Tooltip("Give up on a line if no data has arrived for this long.")]
        [SerializeField] float stallTimeoutSeconds = 8f;

        [Tooltip("After the server fails, go straight to baked clips for this long before trying it again.")]
        [SerializeField] float retryAfterSeconds = 20f;

        [Header("Baked audio (offline safety net)")]
        [Tooltip("Baked voice set to fall back on. Each TTS engine writes its own folder.")]
        [SerializeField] string resourceFolder = "CAIVR/VO_edge";

        [Tooltip("Seconds of silence left after a line before the mic opens.")]
        [SerializeField] float tailPadding = 0.25f;

        [Tooltip("Take the voice choice and server address from the main menu settings.")]
        [SerializeField] bool useMenuSettings = true;

        AudioSource _source;
        bool _externalOutput;

        int _session;                    // bumped by Stop() so a running line knows it was cut off
        float _liveRetryAt;
        UnityWebRequest _activeRequest;
        AudioClip _liveClip;
        bool _streaming;
        volatile float _streamRms;

        public bool IsPlaying => _source != null && _source.isPlaying;

        /// <summary>The AudioSource, for lipsync to read amplitude from.</summary>
        public AudioSource Source => _source;

        public string ResourceFolder
        {
            get => resourceFolder;
            set => resourceFolder = value;
        }

        /// <summary>True when the last line came from the voice server rather than a baked clip.</summary>
        public bool LastLineWasSynthesized { get; private set; }

        /// <summary>
        /// Loudness (0..1, RMS) of the live audio being played right now, or -1 when nothing is
        /// streaming. A streamed clip has no readable data, so the mouth follows this instead.
        /// </summary>
        public float StreamLoudness => _streaming ? _streamRms : -1f;

        /// <summary>Re-reads the voice choice and server address from the saved menu settings.</summary>
        public void ApplyMenuSettings()
        {
            if (!useMenuSettings) return;

            var choice = Menu.CaivrSettings.VoiceSet ?? "";
            if (choice.StartsWith(Menu.CaivrSettings.LiveVoicePrefix))
            {
                liveVoice = choice.Substring(Menu.CaivrSettings.LiveVoicePrefix.Length);
            }
            else
            {
                // A baked set was chosen on purpose: play it, and only go live for lines it lacks.
                resourceFolder = choice;
                liveVoice = "";
            }

            var server = Menu.CaivrSettings.VoiceServer;
            if (!string.IsNullOrWhiteSpace(server)) serverUrl = server.Trim();
        }

        void Awake()
        {
            ApplyMenuSettings();

            // Awake order between components is not guaranteed. If something such
            // as the professor avatar has already supplied a spatial output, keep it.
            if (_externalOutput) return;

            _source = GetComponent<AudioSource>();
            if (_source == null) _source = gameObject.AddComponent<AudioSource>();

            _source.playOnAwake = false;
            _source.loop = false;

            // 2D for the flat demo. In VR the avatar supplies its own spatial
            // source on the professor's head via UseOutput.
            _source.spatialBlend = 0f;
        }

        void OnDisable() => Stop();

        /// <summary>
        /// Plays through a different AudioSource, for example one parented to the
        /// professor's head so the voice comes from where she is sitting.
        /// Safe to call before or after this component's own Awake.
        /// </summary>
        public void UseOutput(AudioSource output)
        {
            if (output == null) return;

            if (_source != null && _source != output) _source.Stop();

            _source = output;
            _source.playOnAwake = false;
            _source.loop = false;
            _externalOutput = true;
        }

        // --- speaking ----------------------------------------------------------------------------------------------

        /// <summary>
        /// Speaks a line and yields until it has finished.
        ///
        /// Live from the voice server first (which answers instantly for a line it has said before),
        /// then the baked clip, then simply holding the subtitle for <paramref name="fallbackSeconds"/>.
        /// </summary>
        public IEnumerator SpeakRoutine(string key, string text, float fallbackSeconds)
        {
            LastLineWasSynthesized = false;
            Stop();   // never talk over ourselves
            var session = _session;

            var wantLive = useRuntimeSynthesis && !string.IsNullOrWhiteSpace(text);
            var preferLive = wantLive && !string.IsNullOrEmpty(liveVoice);

            if (preferLive && Time.unscaledTime >= _liveRetryAt)
            {
                var result = new LiveResult();
                yield return SpeakLive(text, session, result);

                if (session != _session) yield break;      // cut off: nothing more to do
                if (result.Spoke)
                {
                    LastLineWasSynthesized = true;
                    yield break;
                }

                _liveRetryAt = Time.unscaledTime + retryAfterSeconds;
            }

            if (TryPlay(key, out var bakedLength))
            {
                yield return new WaitForSeconds(bakedLength);
                yield break;
            }

            // A baked set was chosen but this line is not in it (a generated reply): go live anyway.
            if (wantLive && !preferLive && Time.unscaledTime >= _liveRetryAt)
            {
                var result = new LiveResult();
                yield return SpeakLive(text, session, result);

                if (session != _session) yield break;
                if (result.Spoke)
                {
                    LastLineWasSynthesized = true;
                    yield break;
                }

                _liveRetryAt = Time.unscaledTime + retryAfterSeconds;
            }

            yield return new WaitForSeconds(fallbackSeconds);
        }

        /// <summary>
        /// Plays the baked line for <paramref name="key"/> if one exists.
        /// Returns false when there is no clip, so the caller can decide what to do.
        /// </summary>
        public bool TryPlay(string key, out float duration)
        {
            duration = 0f;
            if (string.IsNullOrEmpty(key)) return false;

            var clip = Resources.Load<AudioClip>($"{resourceFolder}/{key}");
            if (clip == null) return false;

            PlayClip(clip);
            duration = clip.length + tailPadding;
            return true;
        }

        void PlayClip(AudioClip clip)
        {
            _source.Stop();
            _source.clip = clip;
            _source.Play();
        }

        /// <summary>Cuts the current line off, including a live one that is still arriving.</summary>
        public void Stop()
        {
            _session++;
            ReleaseLive();
            if (_source != null) _source.Stop();
        }

        // --- live --------------------------------------------------------------------------------------------------

        sealed class LiveResult
        {
            public bool Spoke;
        }

        string Url(string path) => serverUrl.TrimEnd('/') + path;

        byte[] RequestBody(string text)
        {
            var json = new StringBuilder("{\"text\":").Append(Quote(text));
            if (!string.IsNullOrEmpty(liveVoice)) json.Append(",\"voice\":").Append(Quote(liveVoice));
            json.Append('}');
            return Encoding.UTF8.GetBytes(json.ToString());
        }

        IEnumerator SpeakLive(string text, int session, LiveResult result)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            // A browser build cannot feed audio to the output as it arrives, so it speaks the
            // reply a sentence at a time, fetching the next while the current one plays.
            yield return SpeakLiveBySentence(text, session, result);
#else
            yield return SpeakLiveStream(text, session, result);
#endif
        }

        /// <summary>
        /// Streams the line: playback starts once a small cushion has arrived and carries on
        /// while the rest is still being generated.
        /// </summary>
        IEnumerator SpeakLiveStream(string text, int session, LiveResult result)
        {
            var handler = new PcmStreamDownload(rebufferSeconds);
            var request = new UnityWebRequest(Url("/tts/stream"), UnityWebRequest.kHttpVerbPOST)
            {
                uploadHandler = new UploadHandlerRaw(RequestBody(text)),
                downloadHandler = handler,
                timeout = 0,
            };
            request.SetRequestHeader("Content-Type", "application/json");
            _activeRequest = request;
            request.SendWebRequest();

            var started = Time.unscaledTime;

            // Wait for the format header and a first cushion of audio.
            while (true)
            {
                if (session != _session) yield break;

                var buffer = handler.Buffer;

                // The connection ended: whatever arrived is all there will be.
                if (request.isDone && buffer != null && !buffer.Finished) buffer.Finish();

                if (buffer != null && (buffer.AvailableSeconds >= prebufferSeconds || buffer.Finished)) break;

                if (handler.BadFormat || (request.isDone && (buffer == null || buffer.AvailableSeconds <= 0f)))
                {
                    LogUnavailable(request);
                    ReleaseLive();
                    yield break;
                }

                if (Time.unscaledTime - started > firstAudioTimeoutSeconds)
                {
                    Debug.LogWarning("[CAIVR] Voice server too slow to start a line - using the baked clip.");
                    ReleaseLive();
                    yield break;
                }

                yield return null;
            }

            var live = handler.Buffer;
            var clip = AudioClip.Create("live-line", live.SampleRate * MaxLineSeconds, 1, live.SampleRate,
                true, data => _streamRms = live.Read(data));
            _liveClip = clip;

            _source.Stop();
            _source.clip = clip;
            _source.timeSamples = 0;
            _source.Play();
            _streaming = true;
            result.Spoke = true;

            // Play until every sample has gone to the audio system.
            var lastBytes = handler.BytesReceived;
            var lastData = Time.unscaledTime;

            while (!live.Drained)
            {
                if (session != _session) yield break;   // Stop() already released everything

                if (request.isDone && !live.Finished)
                {
                    live.Finish();                      // the connection ended early: play what arrived
                }
                else if (handler.BytesReceived != lastBytes)
                {
                    lastBytes = handler.BytesReceived;
                    lastData = Time.unscaledTime;
                }
                else if (!live.Finished && Time.unscaledTime - lastData > stallTimeoutSeconds)
                {
                    Debug.LogWarning("[CAIVR] Voice server stopped sending mid-line - finishing with what arrived.");
                    request.Abort();
                    live.Finish();
                }

                yield return null;
            }

            // The last block has been handed over but is still on its way to the speaker, and the
            // mic must not open on the end of her voice.
            yield return new WaitForSeconds(tailPadding + 0.2f);

            if (session == _session) ReleaseLive();
        }

        /// <summary>Tidies up a live line: cancels the request, drops the stream clip.</summary>
        void ReleaseLive()
        {
            _streaming = false;
            _streamRms = 0f;

            if (_activeRequest != null)
            {
                try
                {
                    if (!_activeRequest.isDone) _activeRequest.Abort();
                    _activeRequest.Dispose();
                }
                catch { /* already gone */ }
                _activeRequest = null;
            }

            if (_liveClip != null)
            {
                if (_source != null && _source.clip == _liveClip)
                {
                    _source.Stop();
                    _source.clip = null;
                }
                Destroy(_liveClip);
                _liveClip = null;
            }
        }

        void LogUnavailable(UnityWebRequest request)
        {
            Debug.LogWarning(
                $"[CAIVR] Voice server unavailable at {serverUrl} ({request.responseCode}): {request.error}. " +
                "Start it with: python Tools/voice_server.py --prewarm. Using baked clips meanwhile.");
        }

        // --- live, browser builds ----------------------------------------------------------------------------------

        IEnumerator SpeakLiveBySentence(string text, int session, LiveResult result)
        {
            var sentences = SplitSentences(text);
            var next = StartWav(sentences[0]);

            for (var i = 0; i < sentences.Count; i++)
            {
                var request = next;
                var started = Time.unscaledTime;

                while (!request.isDone)
                {
                    if (session != _session) { request.Abort(); request.Dispose(); yield break; }
                    if (Time.unscaledTime - started > firstAudioTimeoutSeconds * 3f) request.Abort();
                    yield return null;
                }

                if (request.result != UnityWebRequest.Result.Success)
                {
                    LogUnavailable(request);
                    request.Dispose();
                    yield break;
                }

                var clip = DownloadHandlerAudioClip.GetContent(request);
                request.Dispose();

                next = i + 1 < sentences.Count ? StartWav(sentences[i + 1]) : null;

                PlayClip(clip);
                result.Spoke = true;
                yield return new WaitForSeconds(clip.length);
                if (session != _session) yield break;
            }

            yield return new WaitForSeconds(tailPadding);
        }

        UnityWebRequest StartWav(string sentence)
        {
            var request = new UnityWebRequest(Url("/tts"), UnityWebRequest.kHttpVerbPOST)
            {
                uploadHandler = new UploadHandlerRaw(RequestBody(sentence)),
                downloadHandler = new DownloadHandlerAudioClip(Url("/tts"), AudioType.WAV),
            };
            ((DownloadHandlerAudioClip)request.downloadHandler).streamAudio = false;
            request.SetRequestHeader("Content-Type", "application/json");
            request.SendWebRequest();
            return request;
        }

        static System.Collections.Generic.List<string> SplitSentences(string text)
        {
            var pieces = Regex.Split(text.Trim(), @"(?<=[.!?])\s+");
            var merged = new System.Collections.Generic.List<string>();
            var carry = "";

            foreach (var piece in pieces)
            {
                if (string.IsNullOrWhiteSpace(piece)) continue;
                carry = carry.Length == 0 ? piece : carry + " " + piece;

                // Very short pieces ("Mm." "Right.") sound better joined to the next one.
                if (carry.Length >= 25)
                {
                    merged.Add(carry);
                    carry = "";
                }
            }

            if (carry.Length > 0) merged.Add(carry);
            if (merged.Count == 0) merged.Add(text.Trim());
            return merged;
        }

        /// <summary>Minimal JSON string escaping - the payload is a couple of fields.</summary>
        static string Quote(string value)
        {
            var builder = new StringBuilder(value.Length + 16);
            builder.Append('"');

            foreach (var c in value)
            {
                switch (c)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (c < 0x20) builder.Append("\\u").Append(((int)c).ToString("x4"));
                        else builder.Append(c);
                        break;
                }
            }

            builder.Append('"');
            return builder.ToString();
        }
    }
}

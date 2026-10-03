using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace CAIVR.Speech
{
    /// <summary>
    /// Gives the professor a voice, from two sources.
    ///
    /// 1. Baked clips for every authored line. Zero latency, no network, no
    ///    dependency - these ship as ordinary AudioClips and play identically on
    ///    Quest, PC VR, WebGL and in the Editor.
    ///
    /// 2. A local TTS service for text nobody wrote in advance - anything a
    ///    language model produces mid-conversation. Costs about 1.5-2.5s the
    ///    first time a given sentence is spoken, then it is cached and instant.
    ///
    /// The split matters: the scripted conversation never waits on the network,
    /// so the demo stays snappy, and dynamic replies are still possible when we
    /// need them. Start the service with:  python Tools/tts_server.py
    /// </summary>
    public sealed class VoiceLinePlayer : MonoBehaviour
    {
        /// <summary>Used when a node has no reprompt line of its own.</summary>
        public const string FallbackRepromptKey = "_fallback_reprompt";

        [Header("Baked audio")]
        [Tooltip("Which baked voice set to play. Each TTS engine writes its own folder, " +
                 "so switching engines is changing this string - no code, no rebuild.")]
        [SerializeField] string resourceFolder = "CAIVR/VO_edge";

        [Tooltip("Seconds of silence left after a line before the mic opens.")]
        [SerializeField] float tailPadding = 0.25f;

        [Header("Runtime synthesis (for unscripted lines)")]
        [Tooltip("Speak text that has no baked clip by calling the local TTS service.")]
        [SerializeField] bool useRuntimeSynthesis = true;

        [SerializeField] string ttsEndpoint = "http://127.0.0.1:5111/tts";

        [Tooltip("Give up and fall back to timed subtitles after this long.")]
        [SerializeField] int requestTimeoutSeconds = 12;

        AudioSource _source;

        public bool IsPlaying => _source != null && _source.isPlaying;

        /// <summary>The AudioSource, for lipsync to read amplitude from.</summary>
        public AudioSource Source => _source;

        public string ResourceFolder
        {
            get => resourceFolder;
            set => resourceFolder = value;
        }

        /// <summary>True when the last line came from the network rather than a baked clip.</summary>
        public bool LastLineWasSynthesized { get; private set; }

        [Tooltip("Take the voice set from the main menu rather than the field above.")]
        [SerializeField] bool useMenuSettings = true;

        /// <summary>Re-reads the voice choice from the saved menu settings.</summary>
        public void ApplyMenuSettings()
        {
            if (useMenuSettings) resourceFolder = Menu.CaivrSettings.VoiceSet;
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

        bool _externalOutput;

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

        /// <summary>
        /// Speaks a line and yields until it has finished.
        ///
        /// Tries the baked clip first, then runtime synthesis, then falls back to
        /// simply holding the subtitle on screen for <paramref name="fallbackSeconds"/>.
        /// A missing voice service degrades to a readable subtitle - never to silence
        /// or a stalled conversation.
        /// </summary>
        public IEnumerator SpeakRoutine(string key, string text, float fallbackSeconds)
        {
            LastLineWasSynthesized = false;

            if (TryPlay(key, out var bakedLength))
            {
                yield return new WaitForSeconds(bakedLength);
                yield break;
            }

            if (useRuntimeSynthesis && !string.IsNullOrWhiteSpace(text))
            {
                AudioClip clip = null;
                yield return Synthesize(text, result => clip = result);

                if (clip != null)
                {
                    LastLineWasSynthesized = true;
                    PlayClip(clip);
                    yield return new WaitForSeconds(clip.length + tailPadding);
                    yield break;
                }
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

        IEnumerator Synthesize(string text, System.Action<AudioClip> onDone)
        {
            var payload = Encoding.UTF8.GetBytes(
                $"{{\"text\":{Quote(text)}}}");

            using var request = new UnityWebRequest(ttsEndpoint, UnityWebRequest.kHttpVerbPOST);
            request.uploadHandler = new UploadHandlerRaw(payload);

            var handler = new DownloadHandlerAudioClip(ttsEndpoint, AudioType.MPEG);
            handler.streamAudio = false;
            request.downloadHandler = handler;

            request.SetRequestHeader("Content-Type", "application/json");
            request.timeout = requestTimeoutSeconds;

            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning(
                    $"[CAIVR] Runtime TTS unavailable ({request.responseCode}): {request.error}. " +
                    "Start it with: python Tools/tts_server.py");
                onDone(null);
                yield break;
            }

            onDone(handler.audioClip);
        }

        /// <summary>Minimal JSON string escaping - the payload is a single field.</summary>
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

        public void Stop()
        {
            if (_source != null) _source.Stop();
        }
    }
}

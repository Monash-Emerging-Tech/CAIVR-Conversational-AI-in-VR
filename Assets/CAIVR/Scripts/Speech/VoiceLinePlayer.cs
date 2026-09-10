using UnityEngine;

namespace CAIVR.Speech
{
    /// <summary>
    /// Plays the professor's pre-generated voice lines.
    ///
    /// The audio is baked to WAV ahead of time and shipped as assets, so at
    /// runtime this is nothing but AudioSource playback. That is the whole
    /// point: no TTS engine, no platform speech service, no OS settings, no
    /// permissions, no network. It behaves identically on Quest, PC VR, WebGL
    /// and in the Editor because playing an AudioClip is the one thing every
    /// Unity platform can do.
    ///
    /// Regenerate the audio with CAIVR > Generate Voice Lines after editing any
    /// line in the conversation JSON.
    /// </summary>
    public sealed class VoiceLinePlayer : MonoBehaviour
    {
        public const string ResourceFolder = "CAIVR/VO";

        /// <summary>Used when a node has no reprompt line of its own.</summary>
        public const string FallbackRepromptKey = "_fallback_reprompt";

        [Tooltip("Seconds of silence left after a line before the mic opens.")]
        [SerializeField] float tailPadding = 0.25f;

        AudioSource _source;

        public bool IsPlaying => _source != null && _source.isPlaying;

        /// <summary>The clip currently playing, for lipsync to read amplitude from.</summary>
        public AudioSource Source => _source;

        void Awake()
        {
            _source = GetComponent<AudioSource>();
            if (_source == null) _source = gameObject.AddComponent<AudioSource>();

            _source.playOnAwake = false;
            _source.loop = false;

            // 2D for the flat demo. The VR scene will set spatialBlend to 1 and
            // parent this to the professor's head so it comes from the right place.
            _source.spatialBlend = 0f;
        }

        /// <summary>
        /// Plays the line for <paramref name="key"/> if audio exists for it.
        /// Returns false when there is no clip, so the caller can fall back to
        /// timing the subtitle instead of going silent.
        /// </summary>
        public bool TryPlay(string key, out float duration)
        {
            duration = 0f;
            if (string.IsNullOrEmpty(key)) return false;

            var clip = Resources.Load<AudioClip>($"{ResourceFolder}/{key}");
            if (clip == null) return false;

            _source.Stop();
            _source.clip = clip;
            _source.Play();

            duration = clip.length + tailPadding;
            return true;
        }

        public void Stop()
        {
            if (_source != null) _source.Stop();
        }
    }
}

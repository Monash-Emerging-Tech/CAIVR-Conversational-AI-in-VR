using UnityEngine;

namespace CAIVR.Menu
{
    /// <summary>
    /// Choices made in the main menu, readable by whatever scene runs next.
    ///
    /// Backed by PlayerPrefs rather than passed between scenes, so a setting
    /// survives a restart and the demo scene can also be played directly from
    /// the Editor without going through the menu first - which is how most of
    /// our own testing happens.
    /// </summary>
    public static class CaivrSettings
    {
        const string MicKey = "caivr.microphone";
        const string SelectorKey = "caivr.selector";
        const string VoiceSetKey = "caivr.voiceset";
        const string SubtitlesKey = "caivr.subtitles";

        /// <summary>Empty string means "whatever the OS considers default".</summary>
        public static string MicrophoneDevice
        {
            get => PlayerPrefs.GetString(MicKey, "");
            set { PlayerPrefs.SetString(MicKey, value ?? ""); PlayerPrefs.Save(); }
        }

        /// <summary>0 = System 1 (keywords), 1 = System 2 (local LLM).</summary>
        public static int SelectorMode
        {
            get => PlayerPrefs.GetInt(SelectorKey, 0);
            set { PlayerPrefs.SetInt(SelectorKey, value); PlayerPrefs.Save(); }
        }

        public static string VoiceSet
        {
            get => PlayerPrefs.GetString(VoiceSetKey, "CAIVR/VO_edge");
            set { PlayerPrefs.SetString(VoiceSetKey, value); PlayerPrefs.Save(); }
        }

        public static bool SubtitlesEnabled
        {
            get => PlayerPrefs.GetInt(SubtitlesKey, 1) == 1;
            set { PlayerPrefs.SetInt(SubtitlesKey, value ? 1 : 0); PlayerPrefs.Save(); }
        }

        const string EndpointKey = "caivr.llm.endpoint";
        const string ModelKey = "caivr.llm.model";
        const string ApiKeyKey = "caivr.llm.key";

        /// <summary>
        /// Any OpenAI-compatible chat endpoint. Defaults to a local Ollama, which
        /// is right for a developer's machine; a Quest or WebGL build has to point
        /// at a cloud one, since neither can host a model.
        /// </summary>
        public static string LlmEndpoint
        {
            get => PlayerPrefs.GetString(EndpointKey, "http://localhost:11434/v1/chat/completions");
            set { PlayerPrefs.SetString(EndpointKey, value); PlayerPrefs.Save(); }
        }

        public static string LlmModel
        {
            get => PlayerPrefs.GetString(ModelKey, "qwen2.5:7b");
            set { PlayerPrefs.SetString(ModelKey, value); PlayerPrefs.Save(); }
        }

        /// <summary>
        /// Blank for a local model. PlayerPrefs is not a secret store - this is
        /// for development convenience only, and a shipped build must get its
        /// completions through a server of ours that holds the key instead.
        /// </summary>
        public static string LlmApiKey
        {
            get => PlayerPrefs.GetString(ApiKeyKey, "");
            set { PlayerPrefs.SetString(ApiKeyKey, value); PlayerPrefs.Save(); }
        }

        const string SttEndpointKey = "caivr.stt.endpoint";
        const string SttModelKey = "caivr.stt.model";

        /// <summary>
        /// Speech-to-text endpoint. Defaults to Groq, which serves Whisper on the
        /// same key as the dialogue model. Any OpenAI-compatible transcription
        /// endpoint works here.
        /// </summary>
        public static string SttEndpoint
        {
            get => PlayerPrefs.GetString(SttEndpointKey,
                "https://api.groq.com/openai/v1/audio/transcriptions");
            set { PlayerPrefs.SetString(SttEndpointKey, value); PlayerPrefs.Save(); }
        }

        public static string SttModel
        {
            get => PlayerPrefs.GetString(SttModelKey, "whisper-large-v3-turbo");
            set { PlayerPrefs.SetString(SttModelKey, value); PlayerPrefs.Save(); }
        }

        /// <summary>
        /// The key to use, preferring an environment variable so a developer's
        /// key stays in their shell rather than being written into the project.
        /// Returns null when there is none, which is the normal case for a local
        /// model.
        /// </summary>
        public static string ResolveLlmApiKey()
        {
            var stored = LlmApiKey;
            if (!string.IsNullOrWhiteSpace(stored)) return stored.Trim();

            foreach (var name in new[] { "CAIVR_LLM_API_KEY", "GROQ_API_KEY" })
            {
                string value = null;
                try { value = System.Environment.GetEnvironmentVariable(name); }
                catch { /* not available on every platform */ }

                if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
            }

            return null;
        }

        /// <summary>
        /// The provider's model-list URL, derived from the chat endpoint. Used to
        /// check availability before the student commits to a conversation.
        /// </summary>
        public static string LlmProbeUrl()
        {
            var endpoint = LlmEndpoint ?? "";
            const string suffix = "/chat/completions";

            return endpoint.EndsWith(suffix)
                ? endpoint.Substring(0, endpoint.Length - suffix.Length) + "/models"
                : endpoint;
        }

        /// <summary>
        /// The device name to hand to Microphone.Start. Falls back to the system
        /// default if the saved device has since been unplugged - a headset that
        /// was connected last session very often is not this session.
        /// </summary>
        public static string ResolveMicrophone()
        {
            var saved = MicrophoneDevice;
            if (string.IsNullOrEmpty(saved)) return null;

            foreach (var device in Microphone.devices)
                if (device == saved) return saved;

            Debug.LogWarning($"[CAIVR] Microphone '{saved}' is not connected - using system default.");
            return null;
        }
    }
}

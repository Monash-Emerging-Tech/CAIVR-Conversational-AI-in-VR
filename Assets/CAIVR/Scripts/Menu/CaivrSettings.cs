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

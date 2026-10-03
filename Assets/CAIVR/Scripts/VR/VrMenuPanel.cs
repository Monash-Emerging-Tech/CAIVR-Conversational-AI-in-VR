using System;
using System.Collections;
using System.Collections.Generic;
using CAIVR.Menu;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

namespace CAIVR.VR
{
    /// <summary>
    /// The scenario menu, shown on the TV in the consultation room.
    ///
    /// The student starts inside the room and picks their settings on an object
    /// in it, rather than on a screen laid over the world. It writes the same
    /// <see cref="CaivrSettings"/> the flat menu does, so both stay in step.
    ///
    /// Six settings, each a single row you click to change: microphone (with a
    /// live level meter so you can tell it is picking you up), dialogue system
    /// (with a status light for the AI connection), professor voice, subtitles,
    /// native language, and whether the interface itself is shown in it. Then one
    /// clear button to begin.
    ///
    /// The panel is held in front of wherever the student is looking, like a tablet:
    /// it eases after the gaze once they look well away from it, stays level, comes
    /// in front of any wall behind it, and is drawn over the room, so it can never
    /// be cut off or hidden. In a headset it sits within arm's reach to be poked.
    /// </summary>
    public sealed class VrMenuPanel : MonoBehaviour
    {
        [Tooltip("Physical width of the card. The room's TV is about 1.1 m wide.")]
        [SerializeField] float widthMeters = 1.0f;

        static readonly Vector2 CardPixels = new Vector2(1400f, 1000f);
        const float Margin = 40f;

        // How far from the eyes it is held: arm's length on a monitor, within reach of a hand in a headset.
        const float ScreenDistance = 1.05f;
        const float ReachDistance = 0.7f;

        public event Action StartRequested;

        Canvas _canvas;
        TextMeshProUGUI _micValue;
        TextMeshProUGUI _systemValue;
        TextMeshProUGUI _voiceValue;
        Image _levelFill;
        Image _aiDot;
        TextMeshProUGUI _aiLabel;
        VrSwitch _subtitlesSwitch;
        VrSwitch _interfaceSwitch;
        TextMeshProUGUI _languageValue;
        CanvasGroup _interfaceRow;
        Button _interfaceButton;
        HeadLockedAnchor _anchor;

        // Every fixed label with the key it is looked up by, so a change of language can redo them all.
        readonly List<(TextMeshProUGUI label, string key)> _bound = new List<(TextMeshProUGUI, string)>();

        string[] _microphones = Array.Empty<string>();
        string[] _voiceSets = Array.Empty<string>();

        AudioClip _micClip;
        string _monitorDevice;
        float _level;

        enum AiState { Unneeded, Checking, Ready, Offline }
        AiState _ai = AiState.Unneeded;

        void Awake()
        {
            _microphones = Microphone.devices ?? Array.Empty<string>();
            _voiceSets = MainMenuController.DiscoverVoiceSets();

            Build();
            Refresh();
        }

        void OnDisable() => StopMonitor();

        public void Show()
        {
            _anchor.SetDistance(ExperienceRig.IsHeadset ? ReachDistance : ScreenDistance);

            _canvas.gameObject.SetActive(true);
            StartMonitor();
            Refresh();

            if (CaivrSettings.SelectorMode == 1) StartCoroutine(ProbeAi());
        }

        public void Hide()
        {
            StopMonitor();
            _canvas.gameObject.SetActive(false);
        }

        void Update()
        {
            if (!_canvas.gameObject.activeSelf) return;

            VrUi.EnsureEventCamera(_canvas);
            UpdateLevel();
        }

        // --- settings --------------------------------------------------------

        void CycleMicrophone()
        {
            if (_microphones.Length == 0) return;

            // [system default] followed by each named device.
            var options = new List<string> { "" };
            options.AddRange(_microphones);

            var index = options.IndexOf(CaivrSettings.MicrophoneDevice);
            if (index < 0) index = 0;

            CaivrSettings.MicrophoneDevice = options[(index + 1) % options.Count];

            StartMonitor();          // point the level meter at the new device
            Refresh();
        }

        void CycleSystem()
        {
            CaivrSettings.SelectorMode = CaivrSettings.SelectorMode == 0 ? 1 : 0;

            if (CaivrSettings.SelectorMode == 1) StartCoroutine(ProbeAi());
            else _ai = AiState.Unneeded;

            Refresh();
        }

        void CycleVoice()
        {
            if (_voiceSets.Length == 0) return;

            var index = Array.IndexOf(_voiceSets, CaivrSettings.VoiceSet);
            CaivrSettings.VoiceSet = _voiceSets[(index + 1) % _voiceSets.Length];
            Refresh();
        }

        void CycleLanguage()
        {
            var languages = Enum.GetValues(typeof(Language)).Length;
            CaivrSettings.NativeLanguage = (CaivrSettings.NativeLanguage + 1) % languages;

            // Make sure the characters of a language that needs its own font are drawn before they are shown.
            if (Loc.NativeActive) FindFirstObjectByType<UiFontWarmup>()?.Begin();

            Refresh();
        }

        void ToggleInterface()
        {
            if (!Loc.NativeActive) return;

            CaivrSettings.LocalizeInterface = !CaivrSettings.LocalizeInterface;
            Refresh();
        }

        /// <summary>Remembers a fixed label and the key it is looked up by, and sets it now.</summary>
        TextMeshProUGUI Bind(TextMeshProUGUI label, string key)
        {
            _bound.Add((label, key));
            label.text = Loc.T(key);
            return label;
        }

        void Refresh()
        {
            if (_micValue == null) return;

            foreach (var (label, key) in _bound) label.text = Loc.T(key);

            _micValue.text = MicrophoneLabel();
            _voiceValue.text = MainMenuController.PrettyVoiceSet(CaivrSettings.VoiceSet);
            _systemValue.text = Loc.T(CaivrSettings.SelectorMode == 0 ? "menu.system1" : "menu.system2");
            _languageValue.text = Loc.NameOf(Loc.Native);

            switch (_ai)
            {
                case AiState.Checking:
                    _aiDot.color = MonashTheme.Warning; _aiLabel.text = Loc.T("ai.connecting"); break;
                case AiState.Ready:
                    _aiDot.color = MonashTheme.Success; _aiLabel.text = Loc.T("ai.ready"); break;
                case AiState.Offline:
                    _aiDot.color = MonashTheme.Danger; _aiLabel.text = Loc.T("ai.offline"); break;
                default:
                    _aiDot.color = MonashTheme.Success; _aiLabel.text = Loc.T("ai.local"); break;
            }

            if (_subtitlesSwitch != null && _subtitlesSwitch.IsOn != CaivrSettings.SubtitlesEnabled)
                _subtitlesSwitch.SetValue(CaivrSettings.SubtitlesEnabled);

            // Whether the interface follows the language only means something once a language is chosen.
            var applicable = Loc.NativeActive;
            _interfaceRow.alpha = applicable ? 1f : 0.4f;
            _interfaceRow.blocksRaycasts = applicable;
            _interfaceButton.interactable = applicable;

            if (_interfaceSwitch != null && _interfaceSwitch.IsOn != CaivrSettings.LocalizeInterface)
                _interfaceSwitch.SetValue(CaivrSettings.LocalizeInterface);
        }

        string MicrophoneLabel()
        {
            if (_microphones.Length == 0) return Loc.T("mic.none");

            var saved = CaivrSettings.MicrophoneDevice;
            var name = string.IsNullOrEmpty(saved) ? _microphones[0] : saved;

            if (!string.IsNullOrEmpty(saved) && Array.IndexOf(_microphones, saved) < 0)
                return Loc.T("mic.disconnected");

            return name.Length > 44 ? name.Substring(0, 43) + "..." : name;
        }

        // --- AI status -------------------------------------------------------

        IEnumerator ProbeAi()
        {
            _ai = AiState.Checking;
            Refresh();

            using var request = UnityWebRequest.Get(CaivrSettings.LlmProbeUrl());
            request.timeout = 4;
            request.SetRequestHeader("User-Agent", "CAIVR/1.0 (Unity)");

            var key = CaivrSettings.ResolveLlmApiKey();
            if (!string.IsNullOrWhiteSpace(key)) request.SetRequestHeader("Authorization", $"Bearer {key}");

            yield return request.SendWebRequest();

            // The student may have switched back to System 1 while this was in flight.
            if (CaivrSettings.SelectorMode != 1) yield break;

            _ai = request.result == UnityWebRequest.Result.Success ? AiState.Ready : AiState.Offline;
            Refresh();
        }

        // --- microphone level ------------------------------------------------

        void StartMonitor()
        {
            StopMonitor();
            if (_microphones.Length == 0) return;

            _monitorDevice = CaivrSettings.ResolveMicrophone();

            try
            {
                // A looping 1 second buffer: only the newest samples are ever read.
                _micClip = Microphone.Start(_monitorDevice, true, 1, 44100);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[CAIVR] Could not open the microphone for the level meter: {e.Message}");
                _micClip = null;
            }
        }

        void StopMonitor()
        {
            if (_micClip == null) return;

            if (Microphone.IsRecording(_monitorDevice)) Microphone.End(_monitorDevice);
            _micClip = null;
        }

        void UpdateLevel()
        {
            var target = 0f;

            if (_micClip != null)
            {
                var position = Microphone.GetPosition(_monitorDevice);
                const int window = 256;

                if (position >= window)
                {
                    var samples = new float[window];
                    _micClip.GetData(samples, position - window);

                    var sum = 0f;
                    foreach (var s in samples) sum += s * s;

                    // Speech at a desk sits low, so boost or the meter looks broken.
                    target = Mathf.Clamp01(Mathf.Sqrt(sum / window) * 9f);
                }
            }

            // Quick to rise, slow to fall, so words read as pulses rather than flicker.
            _level = Mathf.Lerp(_level, target, target > _level ? 0.5f : 0.1f);

            _levelFill.rectTransform.anchorMax = new Vector2(Mathf.Max(0.015f, _level), 1f);
            _levelFill.color = Color.Lerp(MonashTheme.BlueLight, MonashTheme.Success, _level);
        }

        // --- construction ----------------------------------------------------

        void Build()
        {
            var canvasPixels = CardPixels + new Vector2(Margin * 2f, Margin * 2f);
            var canvasWidth = widthMeters * canvasPixels.x / CardPixels.x;

            // Held in front of the viewer, a little below where they look, easing after them once they turn well
            // away. Sized for ScreenDistance, and scaled down to ReachDistance in a headset.
            _canvas = VrUi.CreateHeadCanvas("Menu", transform, canvasPixels, canvasWidth,
                distance: ScreenDistance, followSeconds: 0.45f, deadzoneDegrees: 22f,
                referenceDistance: ScreenDistance, lowerDegrees: 6f);

            _anchor = _canvas.GetComponentInParent<HeadLockedAnchor>();

            var card = VrUi.Card(_canvas.transform, "Card", Layout.Fill(Margin, Margin, Margin, Margin), 44f);
            VrUi.AddBezel(_canvas, CardPixels, Vector2.zero);

            var face = card.transform;

            // Header.
            var brand = VrUi.Surface(face, "Brand", Layout.TopLeft(52, 44, 158, 48), MonashTheme.Blue, 24f);
            VrUi.Text(brand.transform, "Label", Layout.Fill(), "CAIVR", 24, MonashTheme.Text,
                TextAlignmentOptions.Center, tracking: 5f);

            Bind(VrUi.Text(face, "Title", Layout.TopLeft(230, 44, 760, 48), "", 34,
                MonashTheme.Text, TextAlignmentOptions.MidlineLeft), "menu.title");

            VrUi.Text(face, "Version", Layout.TopRight(52, 48, 260, 40), $"v{Application.version}", 26,
                MonashTheme.TextDim, TextAlignmentOptions.MidlineRight);

            Bind(VrUi.Text(face, "Subtitle", Layout.TopStretch(56, 108, 56, 44), "", 28,
                MonashTheme.TextDim, TextAlignmentOptions.MidlineLeft), "menu.subtitle");

            VrUi.Surface(face, "Divider", Layout.TopStretch(52, 170, 52, 2), MonashTheme.Border, 1f);

            // Settings rows.
            const float tallRow = 112f;      // the microphone row also holds a level meter
            const float rowHeight = 96f;
            const float rowGap = 10f;
            var y = 192f;

            // Microphone, with a live level meter.
            var micRow = VrUi.Row(face, "MicRow", Layout.TopStretch(52, y, 52, tallRow), CycleMicrophone);
            Eyebrow(micRow.transform, "menu.microphone");
            _micValue = VrUi.Text(micRow.transform, "Value", Layout.TopStretch(30, 46, 200, 44), "", 36,
                MonashTheme.Text, TextAlignmentOptions.MidlineLeft);
            ChangeHint(micRow.transform, 42f);

            var track = VrUi.Surface(micRow.transform, "LevelTrack", Layout.BottomStretch(30, 14, 30, 8), MonashTheme.Border, 4f);
            _levelFill = VrUi.Surface(track.transform, "LevelFill", Layout.Fill(), MonashTheme.BlueLight, 4f);

            y += tallRow + rowGap;

            // Dialogue system, with an AI status chip.
            var systemRow = VrUi.Row(face, "SystemRow", Layout.TopStretch(52, y, 52, rowHeight), CycleSystem);
            Eyebrow(systemRow.transform, "menu.dialogue");
            _systemValue = Value(systemRow.transform, 380f);
            VrUi.Chip(systemRow.transform, "AiStatus", Layout.TopRight(30, 20, 330, 56), out _aiDot, out _aiLabel, 26f);

            y += rowHeight + rowGap;

            // Professor voice.
            var voiceRow = VrUi.Row(face, "VoiceRow", Layout.TopStretch(52, y, 52, rowHeight), CycleVoice);
            Eyebrow(voiceRow.transform, "menu.voice");
            _voiceValue = Value(voiceRow.transform, 200f);
            ChangeHint(voiceRow.transform, 34f);

            y += rowHeight + rowGap;

            // Subtitles: the whole row toggles, and the switch mirrors it.
            Button subtitlesRow = null;
            subtitlesRow = VrUi.Row(face, "SubtitlesRow", Layout.TopStretch(52, y, 52, rowHeight), () =>
            {
                CaivrSettings.SubtitlesEnabled = !CaivrSettings.SubtitlesEnabled;
                Refresh();
            });
            Eyebrow(subtitlesRow.transform, "menu.subtitles");
            Bind(Value(subtitlesRow.transform, 200f), "menu.subtitles.value");

            _subtitlesSwitch = VrUi.Switch(subtitlesRow.transform, "Switch", Layout.TopRight(30, 16, 120, 64),
                CaivrSettings.SubtitlesEnabled);

            // The row itself does the toggling, so the switch must not eat the click
            // and flip it a second time.
            _subtitlesSwitch.GetComponent<Image>().raycastTarget = false;

            y += rowHeight + rowGap;

            // Native language: subtitles get a translated second line, hints come in this language.
            var languageRow = VrUi.Row(face, "LanguageRow", Layout.TopStretch(52, y, 52, rowHeight), CycleLanguage);
            Eyebrow(languageRow.transform, "menu.language");
            _languageValue = Value(languageRow.transform, 200f);
            ChangeHint(languageRow.transform, 34f);

            y += rowHeight + rowGap;

            // Interface language: whether menus, cards and the notebook are in that language too.
            _interfaceButton = VrUi.Row(face, "InterfaceRow", Layout.TopStretch(52, y, 52, rowHeight), ToggleInterface);
            _interfaceRow = _interfaceButton.gameObject.AddComponent<CanvasGroup>();
            Eyebrow(_interfaceButton.transform, "menu.interface");
            Bind(Value(_interfaceButton.transform, 200f), "menu.interface.value");

            _interfaceSwitch = VrUi.Switch(_interfaceButton.transform, "Switch", Layout.TopRight(30, 16, 120, 64),
                CaivrSettings.LocalizeInterface);
            _interfaceSwitch.GetComponent<Image>().raycastTarget = false;

            // The call to action.
            var start = VrUi.PillButton(face, "StartButton", "", Layout.BottomCenter(620, 104, 36),
                () => StartRequested?.Invoke(), ButtonStyle.Primary, 44);
            Bind(start.GetComponentInChildren<TextMeshProUGUI>(), "menu.start");

            _canvas.gameObject.SetActive(false);
        }

        void Eyebrow(Transform row, string key) =>
            Bind(VrUi.Eyebrow(row, "Label", Layout.TopLeft(30, 10, 500, 30), "", MonashTheme.TextDim), key);

        TextMeshProUGUI Value(Transform row, float rightInset) =>
            VrUi.Text(row, "Value", Layout.TopStretch(30, 38, rightInset, 50), "", 34, MonashTheme.Text,
                TextAlignmentOptions.MidlineLeft);

        void ChangeHint(Transform row, float top) =>
            Bind(VrUi.Text(row, "Hint", Layout.TopRight(30, top, 150, 40), "", 26, MonashTheme.BlueLight,
                TextAlignmentOptions.MidlineRight, tracking: 3f, caps: true), "menu.change");
    }
}

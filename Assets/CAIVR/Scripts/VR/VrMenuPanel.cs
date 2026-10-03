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
    /// Four settings, each a single row you click to change: microphone (with a
    /// live level meter so you can tell it is picking you up), dialogue system
    /// (with a status light for the AI connection), professor voice, and
    /// subtitles. Then one clear button to begin.
    /// </summary>
    public sealed class VrMenuPanel : MonoBehaviour
    {
        [Tooltip("Physical width of the card. The room's TV is about 1.1 m wide.")]
        [SerializeField] float widthMeters = 1.0f;

        static readonly Vector2 CardPixels = new Vector2(1400f, 860f);
        const float Margin = 40f;

        public event Action StartRequested;

        Canvas _canvas;
        TextMeshProUGUI _micValue;
        TextMeshProUGUI _systemValue;
        TextMeshProUGUI _voiceValue;
        Image _levelFill;
        Image _aiDot;
        TextMeshProUGUI _aiLabel;
        VrSwitch _subtitlesSwitch;

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
            if (ExperienceRig.IsHeadset) BringWithinReach();

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

        /// <summary>
        /// On a screen the menu hangs a metre away, which suits a mouse. With hands it
        /// has to be somewhere an arm can reach to poke, so in a headset it is brought
        /// in front of wherever the student's head actually is, at the same apparent
        /// size, with the whole card still in view.
        /// </summary>
        void BringWithinReach()
        {
            var rig = ExperienceRig.Instance;
            var camera = Camera.main;
            if (rig == null || camera == null) return;

            const float reach = 0.7f;
            const float screenDistance = 1.05f;      // where the scene builder hangs it for a mouse

            var forward = rig.SeatForward;
            var position = camera.transform.position + forward * reach + Vector3.down * 0.14f;

            transform.SetPositionAndRotation(position, Quaternion.LookRotation(forward) * Quaternion.Euler(8f, 0f, 0f));
            transform.localScale = Vector3.one * (reach / screenDistance);
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

        void Refresh()
        {
            if (_micValue == null) return;

            _micValue.text = MicrophoneLabel();
            _voiceValue.text = MainMenuController.PrettyVoiceSet(CaivrSettings.VoiceSet);

            _systemValue.text = CaivrSettings.SelectorMode == 0
                ? "System 1  -  scripted"
                : "System 2  -  AI assisted";

            switch (_ai)
            {
                case AiState.Checking:
                    _aiDot.color = MonashTheme.Warning; _aiLabel.text = "Connecting"; break;
                case AiState.Ready:
                    _aiDot.color = MonashTheme.Success; _aiLabel.text = "AI connected"; break;
                case AiState.Offline:
                    _aiDot.color = MonashTheme.Danger; _aiLabel.text = "AI offline"; break;
                default:
                    _aiDot.color = MonashTheme.Success; _aiLabel.text = "Works offline"; break;
            }

            if (_subtitlesSwitch != null && _subtitlesSwitch.IsOn != CaivrSettings.SubtitlesEnabled)
                _subtitlesSwitch.SetValue(CaivrSettings.SubtitlesEnabled);
        }

        string MicrophoneLabel()
        {
            if (_microphones.Length == 0) return "No microphone found";

            var saved = CaivrSettings.MicrophoneDevice;
            var name = string.IsNullOrEmpty(saved) ? _microphones[0] : saved;

            if (!string.IsNullOrEmpty(saved) && Array.IndexOf(_microphones, saved) < 0)
                return "Not connected";

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

            _canvas = VrUi.CreateWorldCanvas("Menu", transform, canvasPixels, canvasWidth);

            var card = VrUi.Card(_canvas.transform, "Card", Layout.Fill(Margin, Margin, Margin, Margin), 44f);
            VrUi.AddBezel(_canvas, CardPixels, Vector2.zero);

            var face = card.transform;

            // Header.
            var brand = VrUi.Surface(face, "Brand", Layout.TopLeft(52, 44, 158, 48), MonashTheme.Blue, 24f);
            VrUi.Text(brand.transform, "Label", Layout.Fill(), "CAIVR", 24, MonashTheme.Text,
                TextAlignmentOptions.Center, tracking: 5f);

            VrUi.Text(face, "Title", Layout.TopLeft(230, 44, 760, 48), "Consultation  -  Monash College", 34,
                MonashTheme.Text, TextAlignmentOptions.MidlineLeft);

            VrUi.Text(face, "Version", Layout.TopRight(52, 48, 260, 40), $"v{Application.version}", 26,
                MonashTheme.TextDim, TextAlignmentOptions.MidlineRight);

            VrUi.Text(face, "Subtitle", Layout.TopStretch(56, 108, 56, 44),
                "Ask a professor for an extension, or about your assignment.", 28,
                MonashTheme.TextDim, TextAlignmentOptions.MidlineLeft);

            VrUi.Surface(face, "Divider", Layout.TopStretch(52, 170, 52, 2), MonashTheme.Border, 1f);

            // Settings rows.
            const float rowHeight = 112f;
            const float rowGap = 12f;
            var y = 192f;

            // Microphone, with a live level meter.
            var micRow = VrUi.Row(face, "MicRow", Layout.TopStretch(52, y, 52, rowHeight), CycleMicrophone);
            VrUi.Eyebrow(micRow.transform, "Label", Layout.TopLeft(30, 14, 400, 30), "Microphone", MonashTheme.TextDim);
            _micValue = VrUi.Text(micRow.transform, "Value", Layout.TopStretch(30, 46, 200, 44), "", 36,
                MonashTheme.Text, TextAlignmentOptions.MidlineLeft);
            VrUi.Text(micRow.transform, "Hint", Layout.TopRight(30, 42, 150, 40), "Change", 26,
                MonashTheme.BlueLight, TextAlignmentOptions.MidlineRight, tracking: 3f, caps: true);

            var track = VrUi.Surface(micRow.transform, "LevelTrack", Layout.BottomStretch(30, 14, 30, 8), MonashTheme.Border, 4f);
            _levelFill = VrUi.Surface(track.transform, "LevelFill", Layout.Fill(), MonashTheme.BlueLight, 4f);

            y += rowHeight + rowGap;

            // Dialogue system, with an AI status chip.
            var systemRow = VrUi.Row(face, "SystemRow", Layout.TopStretch(52, y, 52, rowHeight), CycleSystem);
            VrUi.Eyebrow(systemRow.transform, "Label", Layout.TopLeft(30, 14, 400, 30), "Dialogue system", MonashTheme.TextDim);
            _systemValue = VrUi.Text(systemRow.transform, "Value", Layout.TopStretch(30, 46, 380, 52), "", 36,
                MonashTheme.Text, TextAlignmentOptions.MidlineLeft);
            VrUi.Chip(systemRow.transform, "AiStatus", Layout.TopRight(30, 28, 330, 56), out _aiDot, out _aiLabel, 26f);

            y += rowHeight + rowGap;

            // Professor voice.
            var voiceRow = VrUi.Row(face, "VoiceRow", Layout.TopStretch(52, y, 52, rowHeight), CycleVoice);
            VrUi.Eyebrow(voiceRow.transform, "Label", Layout.TopLeft(30, 14, 400, 30), "Professor voice", MonashTheme.TextDim);
            _voiceValue = VrUi.Text(voiceRow.transform, "Value", Layout.TopStretch(30, 46, 200, 52), "", 36,
                MonashTheme.Text, TextAlignmentOptions.MidlineLeft);
            VrUi.Text(voiceRow.transform, "Hint", Layout.TopRight(30, 42, 150, 40), "Change", 26,
                MonashTheme.BlueLight, TextAlignmentOptions.MidlineRight, tracking: 3f, caps: true);

            y += rowHeight + rowGap;

            // Subtitles: the whole row toggles, and the switch mirrors it.
            Button subtitlesRow = null;
            subtitlesRow = VrUi.Row(face, "SubtitlesRow", Layout.TopStretch(52, y, 52, rowHeight), () =>
            {
                CaivrSettings.SubtitlesEnabled = !CaivrSettings.SubtitlesEnabled;
                Refresh();
            });
            VrUi.Eyebrow(subtitlesRow.transform, "Label", Layout.TopLeft(30, 14, 400, 30), "Subtitles", MonashTheme.TextDim);
            VrUi.Text(subtitlesRow.transform, "Value", Layout.TopStretch(30, 46, 200, 52),
                "Show what the professor says", 36, MonashTheme.Text, TextAlignmentOptions.MidlineLeft);

            _subtitlesSwitch = VrUi.Switch(subtitlesRow.transform, "Switch", Layout.TopRight(30, 24, 120, 64),
                CaivrSettings.SubtitlesEnabled);

            // The row itself does the toggling, so the switch must not eat the click
            // and flip it a second time.
            _subtitlesSwitch.GetComponent<Image>().raycastTarget = false;

            // The call to action.
            VrUi.PillButton(face, "StartButton", "Start consultation",
                Layout.BottomCenter(620, 104, 36), () => StartRequested?.Invoke(), ButtonStyle.Primary, 44);

            _canvas.gameObject.SetActive(false);
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace CAIVR.Menu
{
    /// <summary>
    /// Main menu: pick a microphone, pick which dialogue system to run, start.
    ///
    /// Builds its own UI at runtime for the same reason the demo HUD does -
    /// a generated menu is one C# file to review instead of a .unity file that
    /// four people conflict on. The VR build will replace this with a world-space
    /// panel; the settings it writes are read the same way either side.
    /// </summary>
    public sealed class MainMenuController : MonoBehaviour
    {
        const string DemoSceneName = "SpeechDemo";
        const string VoRoot = "Assets/CAIVR/Resources/CAIVR";

        static readonly Color Ink = new Color(0.93f, 0.94f, 0.96f);
        static readonly Color Dim = new Color(0.55f, 0.58f, 0.63f);
        static readonly Color Accent = new Color(0.42f, 0.72f, 0.95f);
        static readonly Color RowBg = new Color(0.12f, 0.13f, 0.16f);
        static readonly Color RowHover = new Color(0.17f, 0.19f, 0.23f);

        readonly List<SettingRow> _rows = new List<SettingRow>();

        string[] _microphones = Array.Empty<string>();
        string[] _voiceSets = Array.Empty<string>();

        AudioClip _micClip;
        string _monitoringDevice;
        Image _levelFill;
        TextMeshProUGUI _micHint;

        sealed class SettingRow
        {
            public TextMeshProUGUI Value;
            public Func<string> Read;
        }

        string _llmStatus = "checking…";

        void Awake()
        {
            _microphones = Microphone.devices ?? Array.Empty<string>();
            _voiceSets = DiscoverVoiceSets();

            BuildUi();
            RefreshRows();
            StartMicMonitor();

            // Find out now whether System 2 has anything behind it. Discovering
            // that mid-conversation is how you get a professor who appears to
            // answer at random.
            StartCoroutine(ProbeLlm());
        }

        System.Collections.IEnumerator ProbeLlm()
        {
            var url = CaivrSettings.LlmProbeUrl();

            using var request = UnityEngine.Networking.UnityWebRequest.Get(url);
            request.timeout = 4;

            var key = CaivrSettings.ResolveLlmApiKey();
            if (!string.IsNullOrWhiteSpace(key))
                request.SetRequestHeader("Authorization", $"Bearer {key}");

            yield return request.SendWebRequest();

            var reachable = request.result == UnityEngine.Networking.UnityWebRequest.Result.Success;

            _llmStatus = reachable
                ? $"{CaivrSettings.LlmModel}  [ok]"
                : "no endpoint reachable";

            RefreshRows();
        }

        void OnDestroy() => StopMicMonitor();

        // --- data ------------------------------------------------------------

        /// <summary>
        /// Finds the baked voice sets on disk in the Editor, and falls back to the
        /// known folders in a build, where the Assets folder no longer exists.
        /// </summary>
        static string[] DiscoverVoiceSets()
        {
#if UNITY_EDITOR
            if (Directory.Exists(VoRoot))
            {
                var found = Directory.GetDirectories(VoRoot)
                    .Select(Path.GetFileName)
                    .Where(name => name.StartsWith("VO_"))
                    .Select(name => $"CAIVR/{name}")
                    .OrderBy(name => name)
                    .ToArray();

                if (found.Length > 0) return found;
            }
#endif
            return new[] { "CAIVR/VO_edge", "CAIVR/VO_sapi" };
        }

        static string PrettyVoiceSet(string resourcePath)
        {
            var name = resourcePath.Replace("CAIVR/VO_", "");
            return name switch
            {
                "edge" => "Natasha (en-AU, neural)",
                "sapi" => "Zira (en-US, robotic)",
                _ => name,
            };
        }

        string CurrentMicLabel()
        {
            if (_microphones.Length == 0) return "<no microphone detected>";

            var saved = CaivrSettings.MicrophoneDevice;
            if (string.IsNullOrEmpty(saved)) return $"System default ({_microphones[0]})";

            return Array.IndexOf(_microphones, saved) >= 0
                ? saved
                : $"{saved}  <not connected>";
        }

        // --- cycling ---------------------------------------------------------

        void CycleMicrophone()
        {
            if (_microphones.Length == 0) return;

            // The list is [system default] followed by each named device.
            var options = new List<string> { "" };
            options.AddRange(_microphones);

            var index = options.IndexOf(CaivrSettings.MicrophoneDevice);
            if (index < 0) index = 0;

            CaivrSettings.MicrophoneDevice = options[(index + 1) % options.Count];

            RefreshRows();
            StartMicMonitor();   // re-point the level meter at the new device
        }

        void CycleSelector()
        {
            CaivrSettings.SelectorMode = CaivrSettings.SelectorMode == 0 ? 1 : 0;
            RefreshRows();
        }

        void CycleVoiceSet()
        {
            if (_voiceSets.Length == 0) return;

            var index = Array.IndexOf(_voiceSets, CaivrSettings.VoiceSet);
            CaivrSettings.VoiceSet = _voiceSets[(index + 1) % _voiceSets.Length];
            RefreshRows();
        }

        void ToggleSubtitles()
        {
            CaivrSettings.SubtitlesEnabled = !CaivrSettings.SubtitlesEnabled;
            RefreshRows();
        }

        void RefreshRows()
        {
            foreach (var row in _rows) row.Value.text = row.Read();

            if (_micHint == null) return;

            _micHint.text = _microphones.Length == 0
                ? "No microphone found - the conversation will fall back to typed input."
                : "Speak to check the level. Typed input is always available as a fallback.";
        }

        // --- microphone level ------------------------------------------------

        void StartMicMonitor()
        {
            StopMicMonitor();
            if (_microphones.Length == 0) return;

            _monitoringDevice = CaivrSettings.ResolveMicrophone();

            try
            {
                // Looping 1-second buffer: we only ever read the most recent
                // samples, so there is nothing to manage and nothing to leak.
                _micClip = Microphone.Start(_monitoringDevice, true, 1, 44100);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[CAIVR] Could not open microphone: {e.Message}");
                _micClip = null;
            }
        }

        void StopMicMonitor()
        {
            if (_micClip == null) return;

            if (Microphone.IsRecording(_monitoringDevice)) Microphone.End(_monitoringDevice);

            _micClip = null;
        }

        float ReadLevel()
        {
            if (_micClip == null) return 0f;

            var position = Microphone.GetPosition(_monitoringDevice);
            const int window = 256;
            if (position < window) return 0f;

            var samples = new float[window];
            _micClip.GetData(samples, position - window);

            var sum = 0f;
            foreach (var sample in samples) sum += sample * sample;

            // RMS, then a generous boost - speech at a normal desk distance sits
            // low enough that a raw meter looks broken.
            return Mathf.Clamp01(Mathf.Sqrt(sum / window) * 8f);
        }

        void Update()
        {
            if (_levelFill == null) return;

            var target = ReadLevel();
            var current = _levelFill.rectTransform.anchorMax.x;

            // Fast attack, slow release: makes speech legible rather than jittery.
            var smoothed = target > current
                ? Mathf.Lerp(current, target, 0.5f)
                : Mathf.Lerp(current, target, 0.12f);

            _levelFill.rectTransform.anchorMax = new Vector2(smoothed, 1f);
            _levelFill.color = Color.Lerp(Accent, new Color(0.45f, 0.85f, 0.5f), smoothed);
        }

        // --- start -----------------------------------------------------------

        void StartScenario()
        {
            StopMicMonitor();

            if (Application.CanStreamedLevelBeLoaded(DemoSceneName))
            {
                SceneManager.LoadScene(DemoSceneName);
                return;
            }

            Debug.LogError(
                $"[CAIVR] Scene '{DemoSceneName}' is not in Build Settings. " +
                "Run CAIVR > Create Demo Scenes.");
        }

        // --- UI construction -------------------------------------------------

        void BuildUi()
        {
            EnsureEventSystem();

            var canvasObject = new GameObject("Menu Canvas",
                typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasObject.transform.SetParent(transform, false);

            var canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;

            var scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);

            var root = canvasObject.transform;

            Panel(root, "Backdrop", Vector2.zero, Vector2.one,
                Vector2.zero, Vector2.zero, new Color(0.06f, 0.07f, 0.09f));

            // Header -----------------------------------------------------------
            var title = Text(root, "Title", new Vector2(0f, 1f), new Vector2(0.6f, 1f),
                new Vector2(96f, -150f), new Vector2(0f, -60f), 68, TextAlignmentOptions.Left);
            title.text = "CAIVR";
            title.fontStyle = FontStyles.Bold;

            var subtitle = Text(root, "Subtitle", new Vector2(0f, 1f), new Vector2(0.7f, 1f),
                new Vector2(100f, -196f), new Vector2(0f, -150f), 26, TextAlignmentOptions.Left);
            subtitle.text = "Conversational AI in VR  ·  Monash Emerging Tech";
            subtitle.color = Dim;

            var version = Text(root, "Version", new Vector2(0.6f, 1f), new Vector2(1f, 1f),
                new Vector2(0f, -110f), new Vector2(-96f, -60f), 26, TextAlignmentOptions.TopRight);
            version.text = $"{Application.productName.ToUpperInvariant()} DEMO  v{Application.version}";
            version.color = Dim;

            // Scenario ---------------------------------------------------------
            SectionLabel(root, "SCENARIO", -250f);

            var card = Panel(root, "ScenarioCard", new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(96f, -390f), new Vector2(-96f, -288f), RowBg);

            var scenarioName = Text(card.transform, "Name", new Vector2(0f, 0f), new Vector2(1f, 1f),
                new Vector2(28f, 44f), new Vector2(-28f, -14f), 30, TextAlignmentOptions.Left);
            scenarioName.text = "Consultation — Monash College";

            var scenarioDesc = Text(card.transform, "Desc", new Vector2(0f, 0f), new Vector2(1f, 1f),
                new Vector2(28f, 12f), new Vector2(-28f, -50f), 22, TextAlignmentOptions.Left);
            scenarioDesc.text = "Ask a professor for an extension, or about your assignment.";
            scenarioDesc.color = Dim;

            // Settings ---------------------------------------------------------
            SectionLabel(root, "SETTINGS", -440f);

            var y = -478f;

            AddRow(root, "Microphone", ref y, CurrentMicLabel, CycleMicrophone);
            AddLevelMeter(root, ref y);

            AddRow(root, "Dialogue system", ref y,
                () => CaivrSettings.SelectorMode == 0
                    ? "System 1 — scripted keywords"
                    : $"System 2 — {_llmStatus}",
                CycleSelector);

            AddRow(root, "Professor voice", ref y,
                () => PrettyVoiceSet(CaivrSettings.VoiceSet), CycleVoiceSet);

            AddRow(root, "Subtitles", ref y,
                () => CaivrSettings.SubtitlesEnabled ? "On" : "Off", ToggleSubtitles);

            // Start ------------------------------------------------------------
            var start = Button(root, "StartButton",
                new Vector2(0f, 0f), new Vector2(1f, 0f),
                new Vector2(96f, 108f), new Vector2(-96f, 190f),
                Accent, StartScenario);

            var startLabel = Text(start.transform, "Label", Vector2.zero, Vector2.one,
                Vector2.zero, Vector2.zero, 34, TextAlignmentOptions.Center);
            startLabel.text = "START";
            startLabel.color = new Color(0.05f, 0.07f, 0.1f);
            startLabel.fontStyle = FontStyles.Bold;

            var footer = Text(root, "Footer", new Vector2(0f, 0f), new Vector2(1f, 0f),
                new Vector2(96f, 48f), new Vector2(-96f, 96f), 20, TextAlignmentOptions.Left);
            footer.text = "Click a setting to change it.";
            footer.color = Dim;
        }

        static void EnsureEventSystem()
        {
            if (FindFirstObjectByType<EventSystem>() != null) return;

            // The project is on the new Input System only, so the legacy
            // StandaloneInputModule would silently never deliver a click.
            var eventSystem = new GameObject("EventSystem",
                typeof(EventSystem), typeof(InputSystemUIInputModule));
            DontDestroyOnLoad(eventSystem);
        }

        void SectionLabel(Transform parent, string text, float top)
        {
            var label = Text(parent, $"Section_{text}", new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(96f, top - 34f), new Vector2(-96f, top), 20, TextAlignmentOptions.Left);
            label.text = text;
            label.color = new Color(0.4f, 0.44f, 0.5f);
            label.characterSpacing = 8f;
        }

        void AddRow(Transform parent, string label, ref float y,
                    Func<string> read, Action onClick)
        {
            const float height = 62f;

            var button = Button(parent, $"Row_{label}",
                new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(96f, y - height), new Vector2(-96f, y),
                RowBg, onClick);

            var name = Text(button.transform, "Label", new Vector2(0f, 0f), new Vector2(0.45f, 1f),
                new Vector2(26f, 0f), new Vector2(0f, 0f), 25, TextAlignmentOptions.Left);
            name.text = label;
            name.color = Dim;

            var value = Text(button.transform, "Value", new Vector2(0.45f, 0f), new Vector2(1f, 1f),
                new Vector2(0f, 0f), new Vector2(-56f, 0f), 25, TextAlignmentOptions.Right);
            value.color = Ink;

            var chevron = Text(button.transform, "Chevron", new Vector2(1f, 0f), new Vector2(1f, 1f),
                new Vector2(-44f, 0f), new Vector2(-18f, 0f), 22, TextAlignmentOptions.Center);
            chevron.text = "›";
            chevron.color = new Color(0.4f, 0.44f, 0.5f);

            _rows.Add(new SettingRow { Value = value, Read = read });

            y -= height + 10f;
        }

        void AddLevelMeter(Transform parent, ref float y)
        {
            const float height = 34f;

            var track = Panel(parent, "MicLevelTrack", new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(96f, y - 10f), new Vector2(-96f, y - 2f),
                new Color(0.16f, 0.17f, 0.2f));

            _levelFill = Panel(track.transform, "Fill", Vector2.zero, new Vector2(0f, 1f),
                Vector2.zero, Vector2.zero, Accent);

            _micHint = Text(parent, "MicHint", new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(96f, y - height), new Vector2(-96f, y - 14f), 19,
                TextAlignmentOptions.Left);
            _micHint.color = new Color(0.4f, 0.44f, 0.5f);

            y -= height + 10f;
        }

        // --- primitives ------------------------------------------------------

        static Image Panel(Transform parent, string name,
                           Vector2 anchorMin, Vector2 anchorMax,
                           Vector2 offsetMin, Vector2 offsetMax, Color color)
        {
            var go = new GameObject(name, typeof(Image));
            go.transform.SetParent(parent, false);

            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;

            var image = go.GetComponent<Image>();
            image.color = color;
            return image;
        }

        static Button Button(Transform parent, string name,
                             Vector2 anchorMin, Vector2 anchorMax,
                             Vector2 offsetMin, Vector2 offsetMax,
                             Color color, Action onClick)
        {
            var image = Panel(parent, name, anchorMin, anchorMax, offsetMin, offsetMax, color);

            var button = image.gameObject.AddComponent<Button>();
            button.targetGraphic = image;

            var colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.18f, 1.18f, 1.18f);
            colors.pressedColor = new Color(0.85f, 0.85f, 0.85f);
            colors.fadeDuration = 0.08f;
            button.colors = colors;

            button.onClick.AddListener(() => onClick());
            return button;
        }

        static TextMeshProUGUI Text(Transform parent, string name,
                                    Vector2 anchorMin, Vector2 anchorMax,
                                    Vector2 offsetMin, Vector2 offsetMax,
                                    float size, TextAlignmentOptions alignment)
        {
            var go = new GameObject(name, typeof(TextMeshProUGUI));
            go.transform.SetParent(parent, false);

            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;

            var text = go.GetComponent<TextMeshProUGUI>();
            text.fontSize = size;
            text.alignment = alignment;
            text.color = Ink;
            text.richText = true;
            text.raycastTarget = false;   // never swallow clicks meant for the row
            text.textWrappingMode = TextWrappingModes.NoWrap;

            return text;
        }
    }
}

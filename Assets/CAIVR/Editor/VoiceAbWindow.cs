using System.Collections.Generic;
using System.IO;
using System.Linq;
using CAIVR.Dialogue;
using CAIVR.Speech;
using UnityEditor;
using UnityEngine;

namespace CAIVR.EditorTools
{
    /// <summary>
    /// Play the same professor line through every baked voice set, back to back.
    ///
    /// Judging TTS from separate demo pages is unreliable - the only comparison
    /// that means anything is the same sentence, in this script's own wording,
    /// switched instantly. That is what this does.
    /// </summary>
    public sealed class VoiceAbWindow : EditorWindow
    {
        const string VoRoot = "Assets/CAIVR/Resources/CAIVR";
        const string ConversationPath = VoRoot + "/Conversations/consultation_demo.json";

        string[] _lineKeys = new string[0];
        int _selected;
        List<string> _engines = new List<string>();
        Vector2 _scroll;

        [MenuItem("CAIVR/Voice A-B Compare", priority = 11)]
        public static void Open()
        {
            var window = GetWindow<VoiceAbWindow>("Voice A/B");
            window.minSize = new Vector2(460, 320);
            window.Refresh();
        }

        void OnFocus() => Refresh();

        void Refresh()
        {
            _engines = Directory.Exists(VoRoot)
                ? Directory.GetDirectories(VoRoot)
                    .Select(Path.GetFileName)
                    .Where(n => n.StartsWith("VO_"))
                    .OrderBy(n => n)
                    .ToList()
                : new List<string>();

            var asset = AssetDatabase.LoadAssetAtPath<TextAsset>(ConversationPath);
            if (asset == null) return;

            var conversation = JsonUtility.FromJson<ConversationAsset>(asset.text);
            if (conversation?.nodes == null) return;

            var keys = new List<string>();
            foreach (var node in conversation.nodes)
            {
                if (!string.IsNullOrWhiteSpace(node.speakerLine)) keys.Add(node.id);
                if (!string.IsNullOrWhiteSpace(node.reprompt)) keys.Add($"{node.id}_reprompt");
            }
            keys.Add(VoiceLinePlayer.FallbackRepromptKey);

            _lineKeys = keys.ToArray();
            _selected = Mathf.Clamp(_selected, 0, Mathf.Max(0, _lineKeys.Length - 1));
        }

        void OnGUI()
        {
            if (_lineKeys.Length == 0)
            {
                EditorGUILayout.HelpBox("No conversation script found.", MessageType.Warning);
                if (GUILayout.Button("Refresh")) Refresh();
                return;
            }

            EditorGUILayout.LabelField("Line", EditorStyles.boldLabel);
            _selected = EditorGUILayout.Popup(_selected, _lineKeys);

            var key = _lineKeys[_selected];
            EditorGUILayout.SelectableLabel(LineText(key),
                EditorStyles.wordWrappedLabel, GUILayout.Height(48));

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Voice sets", EditorStyles.boldLabel);

            if (_engines.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "No VO_* folders yet. Generate one:\n" +
                    "  python Tools/generate_voice_lines.py --engine azure --voice en-AU-NatashaNeural",
                    MessageType.Info);
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (var engine in _engines)
            {
                var clip = LoadClip(engine, key);

                EditorGUILayout.BeginHorizontal("box");

                EditorGUILayout.LabelField(engine.Substring(3),
                    GUILayout.Width(110));

                EditorGUILayout.LabelField(
                    clip == null ? "<missing>" : $"{clip.length:0.00}s",
                    GUILayout.Width(70));

                using (new EditorGUI.DisabledScope(clip == null))
                {
                    if (GUILayout.Button("Play", GUILayout.Width(60))) Play(clip);
                }

                if (GUILayout.Button("Use in scene", GUILayout.Width(100)))
                    UseInScene($"CAIVR/{engine}");

                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space();
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Stop")) StopAll();
            if (GUILayout.Button("Refresh")) Refresh();
            EditorGUILayout.EndHorizontal();
        }

        string LineText(string key)
        {
            var asset = AssetDatabase.LoadAssetAtPath<TextAsset>(ConversationPath);
            if (asset == null) return "";

            var conversation = JsonUtility.FromJson<ConversationAsset>(asset.text);
            var isReprompt = key.EndsWith("_reprompt");
            var nodeId = isReprompt ? key.Substring(0, key.Length - "_reprompt".Length) : key;

            if (key == VoiceLinePlayer.FallbackRepromptKey)
                return "Sorry, I didn't catch that. Could you say it again?";

            var node = conversation?.GetNode(nodeId);
            if (node == null) return "";

            return isReprompt ? node.reprompt : node.speakerLine;
        }

        static AudioClip LoadClip(string engineFolder, string key)
        {
            foreach (var ext in new[] { ".wav", ".mp3", ".ogg" })
            {
                var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(
                    $"{VoRoot}/{engineFolder}/{key}{ext}");
                if (clip != null) return clip;
            }
            return null;
        }

        // Unity has no public API for previewing audio in the Editor, so this
        // reaches the internal one the Project window's own preview uses.
        static void Play(AudioClip clip)
        {
            StopAll();

            var utilType = typeof(AudioImporter).Assembly.GetType("UnityEditor.AudioUtil");
            var method = utilType?.GetMethod("PlayPreviewClip",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public,
                null, new[] { typeof(AudioClip), typeof(int), typeof(bool) }, null);

            if (method == null)
            {
                Debug.LogWarning("[CAIVR] Editor audio preview API not found in this Unity version.");
                return;
            }

            method.Invoke(null, new object[] { clip, 0, false });
        }

        static void StopAll()
        {
            var utilType = typeof(AudioImporter).Assembly.GetType("UnityEditor.AudioUtil");
            utilType?.GetMethod("StopAllPreviewClips",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public)
                ?.Invoke(null, null);
        }

        static void UseInScene(string resourceFolder)
        {
            var player = FindFirstObjectByType<VoiceLinePlayer>();
            if (player == null)
            {
                EditorUtility.DisplayDialog("CAIVR",
                    "No VoiceLinePlayer in the open scene. Open SpeechDemo first.", "OK");
                return;
            }

            var serialized = new SerializedObject(player);
            serialized.FindProperty("resourceFolder").stringValue = resourceFolder;
            serialized.ApplyModifiedProperties();

            EditorUtility.SetDirty(player);
            Debug.Log($"[CAIVR] Scene now uses {resourceFolder}");
        }
    }
}

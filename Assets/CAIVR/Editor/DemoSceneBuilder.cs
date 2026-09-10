using System.IO;
using CAIVR.Demo;
using CAIVR.Dialogue;
using CAIVR.Speech;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace CAIVR.EditorTools
{
    /// <summary>
    /// Builds the flat-screen demo scene from code.
    ///
    /// Generated rather than committed so there is no .unity file for four
    /// people to conflict on while the framework is still moving. Regenerating
    /// it is always safe - delete the scene and run the menu item again.
    /// </summary>
    public static class DemoSceneBuilder
    {
        const string SceneFolder = "Assets/CAIVR/Scenes";
        const string ScenePath = SceneFolder + "/SpeechDemo.unity";
        const string MenuScenePath = SceneFolder + "/MainMenu.unity";

        /// <summary>
        /// Builds both scenes and registers them, menu first so a build starts there.
        /// </summary>
        [MenuItem("CAIVR/Create Demo Scenes", priority = 0)]
        public static void CreateAllScenes()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            CreateMenuScene();
            CreateScene();

            EditorBuildSettings.scenes = new[]
            {
                new EditorBuildSettingsScene(MenuScenePath, true),
                new EditorBuildSettingsScene(ScenePath, true),
            };

            EditorSceneManager.OpenScene(MenuScenePath);
            Debug.Log("[CAIVR] Both scenes created and added to Build Settings. Press Play.");
        }

        static void CreateMenuScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var cameraObject = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener));
            cameraObject.tag = "MainCamera";
            var camera = cameraObject.GetComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.06f, 0.07f, 0.09f);

            new GameObject("Main Menu").AddComponent<Menu.MainMenuController>();

            Directory.CreateDirectory(SceneFolder);
            EditorSceneManager.SaveScene(scene, MenuScenePath);
        }

        [MenuItem("CAIVR/Create Speech Demo Scene", priority = 1)]
        public static void CreateScene()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            var scene = EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // A flat 2D HUD still needs a camera to clear the frame; without one
            // the game view renders whatever was last in the buffer.
            var cameraObject = new GameObject("Main Camera", typeof(Camera));
            cameraObject.tag = "MainCamera";
            var camera = cameraObject.GetComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.06f, 0.07f, 0.09f);

            var rig = new GameObject("Conversation Demo", typeof(AudioSource));
            var speech = rig.AddComponent<SpeechService>();
            var voice = rig.AddComponent<VoiceLinePlayer>();
            var runner = rig.AddComponent<ConversationRunner>();
            rig.AddComponent<ConversationDemoHud>();

            // An AudioListener is normally on the camera; without one the voice
            // lines play into nothing and the scene is silent.
            if (cameraObject.GetComponent<AudioListener>() == null)
                cameraObject.AddComponent<AudioListener>();

            // Wire the references explicitly so the scene is correct on load
            // rather than relying on the runtime FindFirstObjectByType fallbacks.
            Wire(runner, "speech", speech);
            Wire(runner, "voice", voice);

            Directory.CreateDirectory(SceneFolder);
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.Refresh();

            Debug.Log($"[CAIVR] Demo scene created at {ScenePath}. Press Play.");
            EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<Object>(ScenePath));
        }

        static void Wire(Object target, string fieldName, Object value)
        {
            var serialized = new SerializedObject(target);
            var property = serialized.FindProperty(fieldName);

            if (property == null)
            {
                Debug.LogWarning($"[CAIVR] Could not find field '{fieldName}' on {target.GetType().Name}.");
                return;
            }

            property.objectReferenceValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        [MenuItem("CAIVR/Validate Conversation Scripts", priority = 20)]
        public static void ValidateConversations()
        {
            var assets = Resources.LoadAll<TextAsset>("CAIVR/Conversations");

            if (assets.Length == 0)
            {
                Debug.LogWarning("[CAIVR] No conversation JSON found under a Resources/CAIVR/Conversations folder.");
                return;
            }

            var totalProblems = 0;

            foreach (var asset in assets)
            {
                ConversationAsset conversation;
                try
                {
                    conversation = JsonUtility.FromJson<ConversationAsset>(asset.text);
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"[CAIVR] {asset.name}: malformed JSON - {e.Message}", asset);
                    totalProblems++;
                    continue;
                }

                var problems = conversation.Validate();
                foreach (var problem in problems)
                    Debug.LogError($"[CAIVR] {asset.name}: {problem}", asset);

                totalProblems += problems.Count;

                if (problems.Count == 0)
                    Debug.Log($"[CAIVR] {asset.name}: OK ({conversation.nodes.Length} nodes).", asset);
            }

            if (totalProblems == 0)
                Debug.Log($"[CAIVR] All {assets.Length} conversation script(s) valid.");
        }
    }
}

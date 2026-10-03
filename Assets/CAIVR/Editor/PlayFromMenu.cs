using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CAIVR.EditorTools
{
    /// <summary>
    /// Makes Play start at the main menu, whichever scene happens to be open.
    ///
    /// Unity plays the open scene, while a build always starts at the first
    /// scene in Build Settings. That difference bites constantly: you are
    /// working in the conversation scene, press Play to check something, and
    /// skip the menu that a real player would see first.
    ///
    /// Toggle it from CAIVR > Play From Main Menu when you deliberately want to
    /// iterate on one scene in isolation.
    /// </summary>
    [InitializeOnLoad]
    public static class PlayFromMenu
    {
        const string MenuPath = "CAIVR/Play From Main Menu";
        const string PrefKey = "caivr.editor.playFromMenu";

        // The experience now begins inside the consultation room, where the menu is an
        // object on the table. The old flat menu is only a fallback for a fresh clone
        // where the room has not been generated yet.
        const string EntryScenePath = "Assets/CAIVR/Scenes/ConsultationVR.unity";
        const string LegacyMenuScenePath = "Assets/CAIVR/Scenes/MainMenu.unity";

        static bool Enabled
        {
            get => EditorPrefs.GetBool(PrefKey, true);
            set => EditorPrefs.SetBool(PrefKey, value);
        }

        static PlayFromMenu()
        {
            // Deferred: the asset database is not necessarily ready at the moment
            // an InitializeOnLoad constructor runs.
            EditorApplication.delayCall += Apply;
        }

        static void Apply()
        {
            if (!Enabled)
            {
                EditorSceneManager.playModeStartScene = null;
                return;
            }

            var scene = AssetDatabase.LoadAssetAtPath<SceneAsset>(EntryScenePath)
                        ?? AssetDatabase.LoadAssetAtPath<SceneAsset>(LegacyMenuScenePath);

            if (scene == null)
            {
                // Not an error worth shouting about - the scene is generated, and
                // may simply not exist yet on a fresh clone.
                EditorSceneManager.playModeStartScene = null;
                return;
            }

            EditorSceneManager.playModeStartScene = scene;
        }

        [MenuItem(MenuPath, priority = 30)]
        static void Toggle()
        {
            Enabled = !Enabled;
            Apply();

            Debug.Log(Enabled
                ? "[CAIVR] Play now starts at the entry scene (the consultation room), like a build does."
                : "[CAIVR] Play now starts at whichever scene is open.");
        }

        [MenuItem(MenuPath, validate = true)]
        static bool ToggleValidate()
        {
            // Fully qualified: CAIVR.Menu (our own namespace) otherwise wins.
            UnityEditor.Menu.SetChecked(MenuPath, Enabled);
            return true;
        }
    }
}

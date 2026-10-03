using System.Collections;
using CAIVR.Dialogue;
using CAIVR.Speech;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CAIVR.VR
{
    /// <summary>
    /// Runs the whole scenario start inside the room:
    ///
    ///   menu on the TV -> fade to black -> background card -> (5 seconds)
    ///   -> Start -> fade in to the room -> the professor begins
    ///
    /// This is the flow from Workerbee #2. The delay before Start comes from the
    /// runner, so the stakeholder's "after 5 seconds" lives in one place.
    ///
    /// If the scene has no menu panel it skips straight to the briefing.
    /// </summary>
    public sealed class VrScenarioFlow : MonoBehaviour
    {
        [SerializeField] ConversationRunner runner;
        [SerializeField] VoiceLinePlayer voice;
        [SerializeField] VrIntroOverlay overlay;
        [SerializeField] VrMenuPanel menu;

        [SerializeField] float fadeOutSeconds = 0.9f;
        [SerializeField] float fadeInSeconds = 1.6f;

        IEnumerator Start()
        {
            if (runner == null) runner = FindFirstObjectByType<ConversationRunner>();
            if (voice == null) voice = FindFirstObjectByType<VoiceLinePlayer>();
            if (overlay == null) overlay = FindFirstObjectByType<VrIntroOverlay>();
            if (menu == null) menu = FindFirstObjectByType<VrMenuPanel>();

            if (runner == null || overlay == null)
            {
                Debug.LogError("[CAIVR] VrScenarioFlow needs a ConversationRunner and a VrIntroOverlay in the scene.");
                yield break;
            }

            // The camera is not guaranteed to exist the instant this runs.
            while (Camera.main == null) yield return null;

            // In a headset, wait until the student is sat in their chair. Showing the
            // menu before that would put it wherever their head happened to be.
            yield return ExperienceRig.WaitUntilReady();

            overlay.Attach(Camera.main);

            if (menu != null)
            {
                overlay.SetAlphaImmediate(0f);          // the room is visible behind the menu
                yield return RunMenu();
                yield return overlay.FadeTo(1f, fadeOutSeconds);
            }
            else
            {
                overlay.SetAlphaImmediate(1f);          // never show the room before the briefing
            }

            // Whatever was chosen in the menu applies to everything built at load.
            runner.ApplyMenuSettings();
            if (voice != null) voice.ApplyMenuSettings();

            runner.Prepare();

            if (runner.State != ConversationState.ShowingContext)
            {
                overlay.ShowCard("Could not start", "The conversation script failed to load. See the Console for details.");
                yield break;
            }

            overlay.ShowCard("Your consultation", runner.CurrentContext);

            var started = false;
            yield return overlay.Countdown(runner.ContextDelaySeconds, () => started = true);

            while (!started)
            {
                // Space is a convenience for testing without a pointer.
                if (Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame) started = true;
                yield return null;
            }

            overlay.HideCard();
            yield return overlay.FadeTo(0f, fadeInSeconds);

            runner.Begin();
        }

        IEnumerator RunMenu()
        {
            var go = false;
            void OnStart() => go = true;

            menu.StartRequested += OnStart;
            menu.Show();

            while (!go)
            {
                if (Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame) go = true;
                yield return null;
            }

            menu.StartRequested -= OnStart;
            menu.Hide();
        }
    }
}

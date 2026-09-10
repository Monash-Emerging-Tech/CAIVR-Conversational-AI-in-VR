using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace CAIVR.EditorTools
{
    /// <summary>
    /// Bakes the professor's lines to WAV assets.
    ///
    /// This is an authoring step, not a runtime one. It runs on a developer's
    /// Windows machine; the WAVs it produces are committed and play on every
    /// platform with no engine, no settings and no network. Nobody running the
    /// game ever needs any of this.
    ///
    /// Swapping in better-sounding audio later (Piper, a recorded actor) means
    /// replacing the files in Resources/CAIVR/VO. No runtime code changes.
    /// </summary>
    public static class VoiceLineGenerator
    {
        const string ScriptRelativePath = "../Tools/generate_voice_lines.ps1";

        [MenuItem("CAIVR/Generate Voice Lines", priority = 10)]
        public static void Generate()
        {
            var scriptPath = Path.GetFullPath(Path.Combine(Application.dataPath, ScriptRelativePath));

            if (!File.Exists(scriptPath))
            {
                Debug.LogError($"[CAIVR] Generator script not found at {scriptPath}");
                return;
            }

#if UNITY_EDITOR_WIN
            EditorUtility.DisplayProgressBar("CAIVR", "Generating voice lines...", 0.5f);

            try
            {
                var info = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };

                using var process = Process.Start(info);
                if (process == null)
                {
                    Debug.LogError("[CAIVR] Could not start PowerShell.");
                    return;
                }

                var output = process.StandardOutput.ReadToEnd();
                var errors = process.StandardError.ReadToEnd();
                process.WaitForExit();

                if (!string.IsNullOrWhiteSpace(errors))
                    Debug.LogError($"[CAIVR] Voice generation errors:\n{errors}");

                Debug.Log($"[CAIVR] Voice generation output:\n{output}");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            AssetDatabase.Refresh();
#else
            Debug.LogError(
                "[CAIVR] Voice line generation currently runs on Windows only (it uses the " +
                "built-in Windows speech engine). The generated WAVs work on every platform - " +
                "ask a Windows teammate to regenerate and commit them.");
#endif
        }
    }
}

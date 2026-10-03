using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using CAIVR.Menu;
using UnityEngine;
using UnityEngine.Networking;

namespace CAIVR.Dialogue
{
    /// <summary>
    /// Translates a line the professor made up on the spot into the student's language.
    ///
    /// Every scripted line has an authored translation in the conversation file. An answer System 2 invents
    /// to a question the script never covered does not, so the subtitles ask for one here, through the same
    /// chat endpoint the dialogue uses. It arrives a moment after the English, which is fine: the second line of
    /// the subtitles simply appears when it is ready, and if the endpoint cannot be reached nothing is shown
    /// instead of a wrong or empty line.
    ///
    /// Results are remembered, so repeating a line costs nothing.
    /// </summary>
    public sealed class LineTranslator : MonoBehaviour
    {
        [SerializeField] float timeoutSeconds = 8f;

        readonly Dictionary<string, string> _cache = new Dictionary<string, string>();
        readonly HashSet<string> _inFlight = new HashSet<string>();

        /// <summary>Calls <paramref name="done"/> with the translation, or never if there is none.</summary>
        public void Request(string english, Language language, Action<string> done)
        {
            if (string.IsNullOrWhiteSpace(english) || language == Language.English) return;

            var key = $"{(int)language}|{english}";

            if (_cache.TryGetValue(key, out var cached))
            {
                done?.Invoke(cached);
                return;
            }

            if (!_inFlight.Add(key)) return;
            StartCoroutine(Translate(key, english, language, done));
        }

        IEnumerator Translate(string key, string english, Language language, Action<string> done)
        {
            var instruction = language == Language.Chinese
                ? "Translate the user's message from English into natural Simplified Chinese, as it would be said aloud. " +
                  "Reply with the translation only: no quotation marks, no notes, no pinyin."
                : "Translate the user's message into the requested language. Reply with the translation only.";

            var payload = JsonUtility.ToJson(new ChatRequest
            {
                model = CaivrSettings.LlmModel,
                temperature = 0f,
                max_tokens = 600,
                messages = new[]
                {
                    new Message { role = "system", content = instruction },
                    new Message { role = "user", content = english },
                },
            });

            using var request = new UnityWebRequest(CaivrSettings.LlmEndpoint, UnityWebRequest.kHttpVerbPOST);
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(payload));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("User-Agent", "CAIVR/1.0 (Unity)");

            var apiKey = CaivrSettings.ResolveLlmApiKey();
            if (!string.IsNullOrWhiteSpace(apiKey)) request.SetRequestHeader("Authorization", $"Bearer {apiKey}");

            request.timeout = Mathf.CeilToInt(timeoutSeconds);

            yield return request.SendWebRequest();
            _inFlight.Remove(key);

            if (request.result != UnityWebRequest.Result.Success) yield break;

            var response = JsonUtility.FromJson<ChatResponse>(request.downloadHandler.text);
            var text = response?.choices != null && response.choices.Length > 0
                ? response.choices[0].message?.content
                : null;

            if (string.IsNullOrWhiteSpace(text)) yield break;

            text = text.Trim().Trim('"', '“', '”');
            _cache[key] = text;
            done?.Invoke(text);
        }

        [Serializable]
        class ChatRequest
        {
            public string model;
            public Message[] messages;
            public float temperature;
            public int max_tokens;
        }

        [Serializable]
        class Message
        {
            public string role;
            public string content;
        }

        [Serializable]
        class ChatResponse
        {
            public Choice[] choices;
        }

        [Serializable]
        class Choice
        {
            public Message message;
        }
    }
}

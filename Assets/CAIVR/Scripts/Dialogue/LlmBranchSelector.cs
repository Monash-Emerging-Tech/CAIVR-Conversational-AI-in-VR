using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace CAIVR.Dialogue
{
    /// <summary>
    /// System 2: the dialogue tree still owns the structure, but a language
    /// model decides which branch the student's answer corresponds to.
    ///
    /// Speaks the OpenAI chat-completions format, which almost every provider
    /// implements. That is the whole point of this class: the same code runs
    /// against a model on the developer's machine and against a cloud endpoint
    /// on a headset, and only a URL changes between them.
    ///
    /// This matters because CAIVR ships to two places that cannot host a model:
    /// a standalone Quest, and Mike's web platform as a WebGL build. Tying the
    /// dialogue system to a locally installed runtime would have meant System 2
    /// working on our desks and nowhere else.
    ///
    ///   Local dev (free, offline, private):
    ///     endpoint  http://localhost:11434/v1/chat/completions   (Ollama)
    ///     model     qwen2.5:7b  /  llama3.2
    ///     key       none
    ///
    ///   Quest and WebGL (free tier, no credit card):
    ///     endpoint  https://api.groq.com/openai/v1/chat/completions
    ///     model     llama-3.3-70b-versatile
    ///     key       required
    ///
    /// SECURITY: a key embedded in a build can be extracted from it. Fine for a
    /// desk demo; before this reaches students the request must go through a
    /// small server of ours that holds the key instead.
    /// </summary>
    public sealed class LlmBranchSelector : IBranchSelector
    {
        readonly MonoBehaviour _host;
        readonly string _endpoint;
        readonly string _apiKey;
        readonly string _model;
        readonly int _timeoutSeconds;

        public string Name => string.IsNullOrEmpty(_apiKey)
            ? "System 2 (local LLM)"
            : "System 2 (cloud LLM)";

        /// <summary>
        /// True when the last attempt could not reach the server at all, as
        /// opposed to reaching it and getting an unusable answer. The runner
        /// treats these very differently: a bad answer costs one turn, an
        /// unreachable server means every remaining turn fails the same way.
        /// </summary>
        public bool LastCallFailedToConnect { get; private set; }

        public LlmBranchSelector(
            MonoBehaviour host,
            string endpoint,
            string model,
            string apiKey = null,
            int timeoutSeconds = 20)
        {
            _host = host;
            _endpoint = endpoint;
            _model = model;
            _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
            _timeoutSeconds = timeoutSeconds;
        }

        public void Select(DialogueNode node, string utterance, Action<BranchDecision> onDecided)
        {
            if (node?.branches == null || node.branches.Length == 0)
            {
                onDecided(BranchDecision.NoMatch("Node has no branches."));
                return;
            }

            _host.StartCoroutine(Request(node, utterance, onDecided));
        }

        IEnumerator Request(DialogueNode node, string utterance, Action<BranchDecision> onDecided)
        {
            LastCallFailedToConnect = false;

            var payload = JsonUtility.ToJson(new ChatRequest
            {
                model = _model,
                temperature = 0f,   // the same sentence must not wander branches
                messages = new[]
                {
                    new Message { role = "system", content = BuildSystemPrompt() },
                    new Message { role = "user", content = BuildUserPrompt(node, utterance) },
                },
                response_format = new ResponseFormat { type = "json_object" },
            });

            using var request = new UnityWebRequest(_endpoint, UnityWebRequest.kHttpVerbPOST);
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(payload));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");

            if (_apiKey != null)
                request.SetRequestHeader("Authorization", $"Bearer {_apiKey}");

            // A stalled request must not strand the conversation. Blowing the
            // budget re-prompts, which beats the professor going silent while
            // the student sits there waiting.
            request.timeout = _timeoutSeconds;

            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                LastCallFailedToConnect =
                    request.result == UnityWebRequest.Result.ConnectionError ||
                    request.responseCode == 0;

                var hint = LastCallFailedToConnect
                    ? "nothing answering at that address"
                    : $"{request.error} {Truncate(request.downloadHandler?.text)}";

                onDecided(BranchDecision.NoMatch($"LLM unreachable ({request.responseCode}): {hint}"));
                yield break;
            }

            BranchDecision decision;
            try
            {
                decision = Parse(request.downloadHandler.text, node.branches.Length);
            }
            catch (Exception e)
            {
                decision = BranchDecision.NoMatch($"Could not read model response: {e.Message}");
            }

            onDecided(decision);
        }

        static string Truncate(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text.Length <= 200 ? text : text.Substring(0, 200);
        }

        static string BuildSystemPrompt() =>
            "You route a student's spoken reply to the correct branch of a scripted " +
            "conversation in a VR training simulation.\n" +
            "The student is talking to a university professor during a consultation. " +
            "Their words arrive from imperfect speech-to-text, so tolerate " +
            "mistranscriptions, filler words and false starts, and judge intent " +
            "rather than exact wording.\n" +
            "Reply with ONLY this JSON object and nothing else:\n" +
            "{\"branch\": <0-based index, or -1 if none fit>, " +
            "\"confidence\": <number between 0 and 1>, \"why\": \"<one short sentence>\"}\n" +
            "Use -1 when the reply is off-topic, unintelligible, or matches no " +
            "option. Do not invent a branch to be helpful.";

        static string BuildUserPrompt(DialogueNode node, string utterance)
        {
            var builder = new StringBuilder();

            builder.AppendLine($"The professor just said: \"{node.speakerLine}\"");

            if (!string.IsNullOrWhiteSpace(node.expectation))
                builder.AppendLine($"What the student is expected to do: {node.expectation}");

            builder.AppendLine();
            builder.AppendLine("Branches:");
            for (var i = 0; i < node.branches.Length; i++)
            {
                var branch = node.branches[i];
                var intent = string.IsNullOrWhiteSpace(branch.intent) ? branch.label : branch.intent;
                builder.AppendLine($"  {i}: {intent}");
            }

            builder.AppendLine();
            builder.AppendLine($"The student said: \"{utterance}\"");
            builder.AppendLine();
            builder.Append("Which branch index does this correspond to?");

            return builder.ToString();
        }

        static BranchDecision Parse(string json, int branchCount)
        {
            var response = JsonUtility.FromJson<ChatResponse>(json);

            var content = response?.choices != null && response.choices.Length > 0
                ? response.choices[0].message?.content
                : null;

            if (string.IsNullOrWhiteSpace(content))
                return BranchDecision.NoMatch("Model returned an empty answer.");

            var reply = JsonUtility.FromJson<BranchReply>(StripFences(content));

            if (reply == null)
                return BranchDecision.NoMatch($"Unparseable decision: {Truncate(content)}");

            if (reply.branch < 0 || reply.branch >= branchCount)
                return BranchDecision.NoMatch(
                    string.IsNullOrWhiteSpace(reply.why) ? "Model matched no branch." : reply.why);

            return new BranchDecision(reply.branch, reply.confidence, reply.why);
        }

        /// <summary>Smaller models still wrap JSON in code fences despite instructions.</summary>
        static string StripFences(string text)
        {
            var trimmed = text.Trim();
            if (!trimmed.StartsWith("`")) return trimmed;

            var firstNewline = trimmed.IndexOf('\n');
            if (firstNewline < 0) return trimmed;

            trimmed = trimmed.Substring(firstNewline + 1);

            var closing = trimmed.LastIndexOf("`", StringComparison.Ordinal);
            if (closing < 0) return trimmed.Trim();

            while (closing > 0 && trimmed[closing - 1] == '`') closing--;

            return trimmed.Substring(0, closing).Trim();
        }

        // --- Wire shapes. JsonUtility needs plain serializable fields. ---

        [Serializable]
        class ChatRequest
        {
            public string model;
            public Message[] messages;
            public float temperature;
            public ResponseFormat response_format;
        }

        [Serializable]
        class Message
        {
            public string role;
            public string content;
        }

        [Serializable]
        class ResponseFormat
        {
            public string type;
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

        [Serializable]
        class BranchReply
        {
            public int branch = -1;
            public float confidence;
            public string why;
        }
    }
}

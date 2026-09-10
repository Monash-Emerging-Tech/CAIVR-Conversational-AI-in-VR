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

        /// <summary>
        /// Ground truth about the scenario, supplied by the runner when a script
        /// is loaded. Without it the model answers off-tree questions from
        /// imagination; with it, it answers from the script's own world.
        /// </summary>
        string[] _worldFacts = System.Array.Empty<string>();

        public void SetWorldFacts(string[] facts)
            => _worldFacts = facts ?? System.Array.Empty<string>();

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
                    new Message { role = "system", content = BuildSystemPrompt() + BuildFacts() },
                    new Message { role = "user", content = BuildUserPrompt(node, utterance) },
                },
                response_format = new ResponseFormat { type = "json_object" },
                // Bounds worst-case latency. The answer is a small JSON object;
                // without a cap a model that decides to think at length leaves
                // the student staring at a silent professor for ten seconds.
                max_tokens = 400,
            });

            using var request = new UnityWebRequest(_endpoint, UnityWebRequest.kHttpVerbPOST);
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(payload));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");

            // Some providers sit behind Cloudflare, which rejects unfamiliar
            // user agents with a 403 before the request ever reaches the API -
            // a failure that looks exactly like a bad key if you do not know to
            // look for it.
            request.SetRequestHeader("User-Agent", "CAIVR/1.0 (Unity)");

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

        string BuildFacts()
        {
            if (_worldFacts.Length == 0) return string.Empty;

            var builder = new StringBuilder();
            builder.AppendLine();
            builder.AppendLine();
            builder.AppendLine("Facts about this scenario. Treat these as true, and answer " +
                               "from them rather than from general knowledge:");

            foreach (var fact in _worldFacts)
            {
                if (string.IsNullOrWhiteSpace(fact)) continue;
                builder.Append("- ").AppendLine(fact.Trim());
            }

            return builder.ToString();
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
            "\"confidence\": <number between 0 and 1>, \"why\": \"<one short sentence>\", " +
            "\"reply\": \"<see below, or empty>\"}\n" +
            "\n" +
            "Use -1 when the reply is off-topic, unintelligible, or matches no " +
            "option. Do not invent a branch to be helpful.\n" +
            "\n" +
            "When branch is -1 because the student asked a reasonable question " +
            "that no branch covers, write \"reply\": the professor's answer to " +
            "it, in character. One or two short sentences, spoken aloud, plain " +
            "prose with no markdown. Do not ask the student a new question and " +
            "do not repeat what you just said; the simulation will return to its " +
            "own question afterwards.\n" +
            "\n" +
            "NEVER invent specifics that are not established in the conversation " +
            "above - unit names or codes, dates, marks, staff names, or policies. " +
            "A confidently wrong answer teaches the student something false. " +
            "When you do not know, say so the way a real academic would: point " +
            "them to the unit guide, or say you will check and email them.\n" +
            "\n" +
            "Leave \"reply\" empty when the student said nothing worth answering, " +
            "was unintelligible, or when you matched a branch.";

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
                    string.IsNullOrWhiteSpace(reply.why) ? "Model matched no branch." : reply.why,
                    reply.reply);

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
            public int max_tokens;
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

            /// <summary>An in-character answer when the student asked something off-tree.</summary>
            public string reply;
        }
    }
}

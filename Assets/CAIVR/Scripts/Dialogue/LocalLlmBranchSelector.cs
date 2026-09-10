using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace CAIVR.Dialogue
{
    /// <summary>
    /// System 2: the dialogue tree still owns the structure, but a language
    /// model decides which branch the student's answer actually corresponds to.
    ///
    /// Runs against a local Ollama server, so it is free and stays free:
    /// no API key, no account, no per-request cost, no usage cap, and no student
    /// speech ever leaves the machine. That last part is worth keeping in mind
    /// given this is going in front of Monash students.
    ///
    /// Setup (about five minutes, one time):
    ///   1. Install Ollama from https://ollama.com/download
    ///   2. In a terminal:  ollama pull llama3.2
    ///   3. Leave it running. It serves on http://localhost:11434 by default.
    ///
    /// The tree is unchanged - same nodes, same branches, same authored lines.
    /// All that moves is the matching step, which is why this drops in behind
    /// <see cref="IBranchSelector"/> without the runner noticing.
    ///
    /// Deployment note: a Quest standalone build cannot host Ollama. Options
    /// when we get there are pointing at a PC on the same network, or moving to
    /// an on-device model via Unity's Inference Engine. Both sit behind this
    /// same interface, so neither is a rewrite.
    /// </summary>
    public sealed class LocalLlmBranchSelector : IBranchSelector
    {
        public string Name => "System 2 (local LLM)";

        readonly MonoBehaviour _host;
        readonly string _endpoint;
        readonly string _model;
        readonly int _timeoutSeconds;

        /// <param name="host">Any live MonoBehaviour; used to run the web request coroutine.</param>
        /// <param name="endpoint">Ollama chat endpoint.</param>
        /// <param name="model">
        /// A pulled Ollama model. llama3.2 (3B) is the sensible default: small
        /// enough to answer in about a second on a normal laptop, which matters
        /// because this latency lands in the middle of a conversation. Larger
        /// models classify better but the pause starts to feel wrong.
        /// </param>
        public LocalLlmBranchSelector(
            MonoBehaviour host,
            string endpoint = "http://localhost:11434/api/chat",
            string model = "llama3.2",
            int timeoutSeconds = 20)
        {
            _host = host;
            _endpoint = endpoint;
            _model = model;
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
            var payload = JsonUtility.ToJson(new ChatRequest
            {
                model = _model,
                stream = false,
                // Ollama constrains the output to valid JSON. Without this, small
                // models like to prefix their answer with "Sure! Here's the JSON:".
                format = "json",
                messages = new[]
                {
                    new Message { role = "system", content = BuildSystemPrompt() },
                    new Message { role = "user", content = BuildUserPrompt(node, utterance) },
                },
                options = new Options { temperature = 0f },
            });

            using var request = new UnityWebRequest(_endpoint, UnityWebRequest.kHttpVerbPOST);
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(payload));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");

            // A stalled request must not strand the conversation. If we blow the
            // budget the runner re-prompts, which is a far better failure than
            // the professor going silent while the student waits.
            request.timeout = _timeoutSeconds;

            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                var hint = request.result == UnityWebRequest.Result.ConnectionError
                    ? "Is Ollama running? Try 'ollama serve' in a terminal."
                    : request.error;

                onDecided(BranchDecision.NoMatch($"Local model unreachable ({request.responseCode}): {hint}"));
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

            var content = response?.message?.content;
            if (string.IsNullOrWhiteSpace(content))
                return BranchDecision.NoMatch("Empty response from local model.");

            var reply = JsonUtility.FromJson<BranchReply>(content.Trim());

            if (reply == null)
                return BranchDecision.NoMatch($"Unparseable decision: {content}");

            if (reply.branch < 0 || reply.branch >= branchCount)
                return BranchDecision.NoMatch(
                    string.IsNullOrWhiteSpace(reply.why) ? "Model matched no branch." : reply.why);

            return new BranchDecision(reply.branch, reply.confidence, reply.why);
        }

        // --- Wire shapes. JsonUtility needs plain serializable fields. ---

        [Serializable]
        class ChatRequest
        {
            public string model;
            public Message[] messages;
            public bool stream;
            public string format;
            public Options options;
        }

        [Serializable]
        class Message
        {
            public string role;
            public string content;
        }

        [Serializable]
        class Options
        {
            // Branch classification should be repeatable: the same sentence must
            // not wander down a different branch on a second run.
            public float temperature;
        }

        [Serializable]
        class ChatResponse
        {
            public Message message;
            public bool done;
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

using System;
using System.Collections.Generic;
using UnityEngine;

namespace CAIVR.Dialogue
{
    /// <summary>
    /// One conversation, authored as JSON so writers can edit scenarios without
    /// opening Unity and without merge-conflicting on a .unity or .asset file.
    /// Shapes here are deliberately JsonUtility-friendly: plain fields, arrays,
    /// no dictionaries, no nullable value types.
    /// </summary>
    [Serializable]
    public class ConversationAsset
    {
        public string id;
        public string title;

        /// <summary>
        /// Premade background contexts, per Workerbee #2: picking a scenario
        /// shows one of these before the conversation starts, so repeat runs of
        /// "consultation" are not identical.
        /// </summary>
        public string[] contextVariants;

        /// <summary>
        /// Ground truth about this scenario's world: who the professor is, what
        /// the unit is, what the assignment requires, what the policies are.
        ///
        /// System 2 answers student questions the tree has no branch for, and a
        /// model with no facts will cheerfully invent them - it named a unit
        /// that does not exist on the first run. Telling it "do not invent" does
        /// not hold; giving it the answers does. Anything a student might
        /// plausibly ask should be listed here.
        /// </summary>
        public string[] worldFacts;

        public string startNodeId;
        public DialogueNode[] nodes;

        Dictionary<string, DialogueNode> _index;

        public DialogueNode GetNode(string nodeId)
        {
            if (string.IsNullOrEmpty(nodeId)) return null;

            if (_index == null)
            {
                _index = new Dictionary<string, DialogueNode>(nodes?.Length ?? 0);
                if (nodes != null)
                {
                    foreach (var node in nodes)
                    {
                        if (node == null || string.IsNullOrEmpty(node.id)) continue;
                        _index[node.id] = node;
                    }
                }
            }

            return _index.TryGetValue(nodeId, out var found) ? found : null;
        }

        public string PickContext()
        {
            if (contextVariants == null || contextVariants.Length == 0)
                return "(no background context authored)";

            return contextVariants[UnityEngine.Random.Range(0, contextVariants.Length)];
        }

        /// <summary>
        /// Catches authoring mistakes at load rather than mid-conversation.
        /// Returns human-readable problems; empty means the graph is sound.
        /// </summary>
        public List<string> Validate()
        {
            var problems = new List<string>();

            if (nodes == null || nodes.Length == 0)
            {
                problems.Add("Conversation has no nodes.");
                return problems;
            }

            if (GetNode(startNodeId) == null)
                problems.Add($"startNodeId '{startNodeId}' does not match any node.");

            foreach (var node in nodes)
            {
                if (node == null) continue;

                if (node.isEnd) continue;

                if (node.branches == null || node.branches.Length == 0)
                {
                    problems.Add($"Node '{node.id}' is not an end node but has no branches.");
                    continue;
                }

                foreach (var branch in node.branches)
                {
                    if (GetNode(branch.nextNodeId) == null)
                        problems.Add($"Node '{node.id}' branch '{branch.label}' points at missing node '{branch.nextNodeId}'.");
                }
            }

            return problems;
        }
    }

    [Serializable]
    public class DialogueNode
    {
        public string id;

        /// <summary>What the professor says when we arrive at this node.</summary>
        [TextArea(2, 5)] public string speakerLine;

        /// <summary>Designer note describing what the student is expected to do here. Also fed to the AI selector.</summary>
        [TextArea(1, 3)] public string expectation;

        /// <summary>Said when nothing matched, before we listen again.</summary>
        [TextArea(1, 3)] public string reprompt;

        public bool isEnd;

        public DialogueBranch[] branches;
    }

    [Serializable]
    public class DialogueBranch
    {
        /// <summary>Short designer-facing name, shown in the debug HUD.</summary>
        public string label;

        /// <summary>
        /// Plain-English description of the intent this branch represents.
        /// Unused by keyword matching; this is what System 2 reasons over.
        /// </summary>
        [TextArea(1, 3)] public string intent;

        /// <summary>Words/phrases that select this branch under System 1.</summary>
        public string[] keywords;

        public string nextNodeId;
    }
}

using System;
using System.Collections.Generic;
using System.Text;

namespace CAIVR.Dialogue
{
    /// <summary>
    /// System 1: strict dialogue trees, no model in the loop.
    ///
    /// Scores each branch by how many of its keywords appear in the utterance,
    /// normalised so a branch with two keywords is not automatically beaten by
    /// one with ten. Fast, free, offline, fully deterministic - and it only
    /// understands what we thought to type in advance, which is exactly the
    /// tradeoff the team needs to see side by side with System 2.
    /// </summary>
    public sealed class KeywordBranchSelector : IBranchSelector
    {
        public string Name => "System 1 (keywords)";

        readonly float _threshold;

        /// <param name="threshold">Minimum normalised score to count as a match.</param>
        public KeywordBranchSelector(float threshold = 0.34f)
        {
            _threshold = threshold;
        }

        public void Select(DialogueNode node, string utterance, Action<BranchDecision> onDecided)
        {
            if (node?.branches == null || node.branches.Length == 0)
            {
                onDecided(BranchDecision.NoMatch("Node has no branches."));
                return;
            }

            var spoken = Normalize(utterance);

            var bestIndex = -1;
            var bestScore = 0f;
            string bestHits = null;

            for (var i = 0; i < node.branches.Length; i++)
            {
                var branch = node.branches[i];
                if (branch.keywords == null || branch.keywords.Length == 0) continue;

                var hits = new List<string>();
                foreach (var keyword in branch.keywords)
                {
                    if (string.IsNullOrWhiteSpace(keyword)) continue;
                    if (Mentions(spoken, keyword)) hits.Add(keyword);
                }

                if (hits.Count == 0) continue;

                var score = (float)hits.Count / branch.keywords.Length;

                // Any single keyword hit is meaningful evidence; give a floor so
                // one hit on a ten-keyword branch still beats nothing at all.
                score = Math.Max(score, 0.34f);

                if (score > bestScore)
                {
                    bestScore = score;
                    bestIndex = i;
                    bestHits = string.Join(", ", hits);
                }
            }

            if (bestIndex < 0 || bestScore < _threshold)
            {
                onDecided(BranchDecision.NoMatch("No branch keywords appeared in the utterance."));
                return;
            }

            onDecided(new BranchDecision(
                bestIndex,
                bestScore,
                $"matched on: {bestHits}"));
        }

        /// <summary>
        /// Does the utterance mention this keyword?
        ///
        /// Whole-word first, then a prefix match so a keyword also catches its
        /// inflections - "sick" finds "sickness", "medical" finds "medically",
        /// "injur" finds both "injury" and "injured". Without this the list has
        /// to enumerate every ending a student might use, which is exactly the
        /// brittleness that makes System 1 lose to System 2.
        ///
        /// Restricted to keywords of four characters or more: prefix-matching
        /// something like "ill" would fire on "I'll" and "illustrate".
        /// </summary>
        static bool Mentions(string spokenPadded, string keyword)
        {
            var needle = Normalize(keyword);          // " medical "
            if (spokenPadded.Contains(needle)) return true;

            var core = needle.Trim();
            if (core.Length < 4 || core.Contains(' ')) return false;

            return spokenPadded.Contains(" " + core);
        }

        /// <summary>
        /// Lowercase, strip punctuation, collapse whitespace. Dictation returns
        /// things like "Yes, I've read it." and a naive Contains would miss
        /// "ive read" against "I've read".
        /// </summary>
        static string Normalize(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;

            var builder = new StringBuilder(input.Length + 2);
            builder.Append(' ');

            var lastWasSpace = true;
            foreach (var raw in input)
            {
                var c = char.ToLowerInvariant(raw);

                if (char.IsLetterOrDigit(c))
                {
                    builder.Append(c);
                    lastWasSpace = false;
                }
                else if (!lastWasSpace)
                {
                    builder.Append(' ');
                    lastWasSpace = true;
                }
            }

            if (!lastWasSpace) builder.Append(' ');
            return builder.ToString();
        }
    }
}

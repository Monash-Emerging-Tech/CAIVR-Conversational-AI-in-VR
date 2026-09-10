using System;

namespace CAIVR.Dialogue
{
    /// <summary>
    /// The seam between the two systems Billy put to the group in Workerbee #1.
    ///
    ///   System 1 - <see cref="KeywordBranchSelector"/>: pure scripted matching.
    ///   System 2 - <see cref="AnthropicBranchSelector"/>: ask a model which branch fits.
    ///
    /// The runner does not know or care which one it has. That is the point:
    /// the System 1 vs System 2 decision is still open, so nothing downstream is
    /// allowed to depend on the answer. Swapping them is a one-line change in
    /// <see cref="ConversationRunner"/>, and running both against the same
    /// script is how we compare them honestly.
    ///
    /// Deliberately callback-based rather than a plain return value: System 2
    /// needs a network round trip, and a synchronous signature would have to be
    /// torn up to accommodate it.
    /// </summary>
    public interface IBranchSelector
    {
        string Name { get; }

        void Select(DialogueNode node, string utterance, Action<BranchDecision> onDecided);
    }

    public readonly struct BranchDecision
    {
        /// <summary>Index into the node's branches, or -1 when nothing matched.</summary>
        public readonly int BranchIndex;

        public readonly float Confidence;

        /// <summary>Why this branch won. Shown in the debug HUD so we can eyeball quality.</summary>
        public readonly string Rationale;

        public bool Matched => BranchIndex >= 0;

        public BranchDecision(int branchIndex, float confidence, string rationale)
        {
            BranchIndex = branchIndex;
            Confidence = confidence;
            Rationale = rationale;
        }

        public static BranchDecision NoMatch(string why) => new BranchDecision(-1, 0f, why);
    }
}

using System;
using CAIVR.Dialogue;
using UnityEngine;

namespace CAIVR.VR
{
    /// <summary>What the professor is doing in the conversation, as far as her body language cares.</summary>
    public enum ProfessorPhase
    {
        /// <summary>Before the first line, or between scenes. Present and friendly, waiting for the student.</summary>
        Waiting,

        /// <summary>Delivering a line.</summary>
        Speaking,

        /// <summary>The student's turn. She is listening.</summary>
        Listening,

        /// <summary>The student has answered and she is working out what to say.</summary>
        Thinking,

        /// <summary>The conversation is over.</summary>
        Ended,
    }

    /// <summary>
    /// Turns the conversation into the few things her body language reacts to: which phase
    /// she is in, how loud she is, when a phrase starts and ends, when a word is stressed.
    ///
    /// Gaze, expression and gestures all need the same answers, and working them out in one
    /// place keeps them in step: the glance away at the start of a sentence, the brow lift
    /// on the stressed word and the hand that moves with it are the same moment.
    ///
    /// It watches <see cref="ConversationRunner"/> by polling its state, so it needs no
    /// wiring and cannot miss a change. Without a runner she simply stays in Waiting.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public sealed class ProfessorMood : MonoBehaviour
    {
        [SerializeField] ConversationRunner runner;
        [SerializeField] LipSyncDriver lips;

        [Header("Speech")]
        [Tooltip("Loudness above which she counts as talking, 0..1.")]
        [SerializeField, Range(0f, 0.5f)] float talkingThreshold = 0.10f;

        [Tooltip("How long she must be quiet before a phrase counts as finished. Short enough to catch the pause between sentences, long enough to ignore the gap between words.")]
        [SerializeField] float phraseGapSeconds = 0.35f;

        [Tooltip("How far above her recent average a burst of loudness must rise to count as a stressed word.")]
        [SerializeField, Range(0.05f, 0.6f)] float emphasisRise = 0.22f;

        public ProfessorPhase Phase { get; private set; } = ProfessorPhase.Waiting;
        public float SecondsInPhase => Time.time - _phaseStarted;

        /// <summary>How loud she is, 0..1, lightly smoothed.</summary>
        public float Energy { get; private set; }

        /// <summary>1 on a stressed word, then fading to 0 over about half a second.</summary>
        public float Emphasis { get; private set; }

        /// <summary>True during a phrase of speech, false in the pauses between them.</summary>
        public bool InPhrase { get; private set; }

        public bool Speaking => Phase == ProfessorPhase.Speaking;

        public event Action<ProfessorPhase> PhaseChanged;
        public event Action PhraseStarted;
        public event Action PhraseEnded;
        public event Action EmphasisHit;

        float _phaseStarted;
        float _average;
        float _quietFor;
        float _lastHit = -10f;

        void Awake()
        {
            if (runner == null) runner = FindFirstObjectByType<ConversationRunner>();
            if (lips == null) lips = GetComponent<LipSyncDriver>();
        }

        void Update()
        {
            var dt = Time.deltaTime;

            ReadPhase();

            var loud = lips != null ? lips.Openness : 0f;
            Energy = Mathf.Lerp(Energy, loud, 1f - Mathf.Exp(-dt / 0.06f));
            Emphasis = Mathf.MoveTowards(Emphasis, 0f, dt / 0.55f);

            TrackPhrases(loud, dt);
        }

        void ReadPhase()
        {
            var next = runner == null ? ProfessorPhase.Waiting : runner.State switch
            {
                ConversationState.Speaking => ProfessorPhase.Speaking,
                ConversationState.Listening => ProfessorPhase.Listening,
                ConversationState.Deciding => ProfessorPhase.Thinking,
                ConversationState.Ended => ProfessorPhase.Ended,
                _ => ProfessorPhase.Waiting,
            };

            if (next == Phase) return;

            Phase = next;
            _phaseStarted = Time.time;
            PhaseChanged?.Invoke(next);
        }

        void TrackPhrases(float loud, float dt)
        {
            if (loud > talkingThreshold)
            {
                _quietFor = 0f;

                if (!InPhrase)
                {
                    InPhrase = true;
                    _average = loud;
                    PhraseStarted?.Invoke();
                }

                // A stressed word is a burst well above how she has been speaking lately.
                if (loud - _average > emphasisRise && Time.time - _lastHit > 0.45f)
                {
                    _lastHit = Time.time;
                    Emphasis = Mathf.Clamp01((loud - _average) * 3f);
                    EmphasisHit?.Invoke();
                }

                _average = Mathf.Lerp(_average, loud, 1f - Mathf.Exp(-dt / 0.7f));
                return;
            }

            if (!InPhrase) return;

            _quietFor += dt;
            if (_quietFor >= phraseGapSeconds)
            {
                InPhrase = false;
                PhraseEnded?.Invoke();
            }
        }
    }
}

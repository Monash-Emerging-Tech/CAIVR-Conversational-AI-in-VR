using System.Collections.Generic;
using UnityEngine;

namespace CAIVR.VR
{
    /// <summary>
    /// The professor's face, driven by blendshape name.
    ///
    /// A Character Creator face is not one mesh. The skin, brows, eyelids, tear
    /// line, tongue and hair each carry their own copy of the facial shapes, and a
    /// shape such as "Jaw_Open" only looks right when it is applied to all of them
    /// at once. Doing that here means nothing else has to know how the head is
    /// built: lipsync, blinking, gaze and expressions ask for a shape by name.
    ///
    /// Several things want the same shape at once (the smile is part of her resting
    /// face, of a warm greeting and of a laugh in the voice), so nothing sets a shape
    /// outright. Everything calls <see cref="Mix"/> each frame and the amounts add up,
    /// capped at 1, when the frame ends. A shape nobody mixes falls back to zero.
    ///
    /// It also blinks. A face that never blinks is the quickest way to make a
    /// character look like a mannequin, and it costs nothing.
    /// </summary>
    [DefaultExecutionOrder(1000)]      // after everything that mixes, so it sees the whole frame
    public sealed class FaceRig : MonoBehaviour
    {
        [Tooltip("Every mesh that carries facial shapes. Filled in by the scene builder.")]
        [SerializeField] SkinnedMeshRenderer[] meshes = new SkinnedMeshRenderer[0];

        [Header("Resting expression")]
        [Tooltip("Shapes held on all the time, 0..1. A face with every shape at zero reads as stern; a little warmth in the mouth, cheeks and brows reads as calm and attentive.")]
        [SerializeField] RestingShape[] resting = new RestingShape[0];

        [System.Serializable]
        public struct RestingShape
        {
            public string shape;
            [Range(0f, 1f)] public float weight;
        }

        [Header("Eyelids")]
        [Tooltip("How far the upper lids hang at rest, 0..1. Wide open lids with a ring of white above the iris read as startled or " +
                 "staring; relaxed lids just cover the top of the iris. Other scripts nudge this (a little wider on emphasis).")]
        [SerializeField, Range(0f, 0.5f)] float eyelidDroop = 0.14f;

        [Header("Blinking")]
        [SerializeField] bool autoBlink = true;
        [SerializeField] Vector2 secondsBetweenBlinks = new Vector2(2.4f, 6.5f);
        [SerializeField] Vector2 blinkSeconds = new Vector2(0.12f, 0.19f);
        [Tooltip("Chance that a blink is followed straight away by a second one, as real blinks sometimes are.")]
        [SerializeField, Range(0f, 0.5f)] float doubleBlinkChance = 0.16f;

        sealed class Slot
        {
            public readonly List<Target> Targets = new List<Target>();
            public float Sum;
            public float Last;
        }

        struct Target
        {
            public SkinnedMeshRenderer Mesh;
            public int Index;
        }

        readonly Dictionary<string, Slot> _slots = new Dictionary<string, Slot>();
        readonly List<Slot> _slotList = new List<Slot>();

        float _nextBlink;
        float _blinkElapsed = -1f;
        float _blinkLength;
        float _doubleAt = -1f;

        const string BlinkLeft = "Eye_Blink_L";
        const string BlinkRight = "Eye_Blink_R";

        /// <summary>How far the upper lids hang, 0..1. Read and written by other scripts.</summary>
        public float EyelidDroop { get => eyelidDroop; set => eyelidDroop = Mathf.Clamp(value, 0f, 0.6f); }

        public void SetMeshes(SkinnedMeshRenderer[] faceMeshes) => meshes = faceMeshes;

        public void SetResting(RestingShape[] shapes) => resting = shapes;

        void Awake()
        {
            Index();
            ScheduleBlink();
        }

        /// <summary>Adds to a shape for this frame, 0 = nothing, 1 = fully on, on every mesh that has it.</summary>
        public void Mix(string shape, float weight01)
        {
            if (weight01 <= 0f) return;
            if (_slots.TryGetValue(shape, out var slot)) slot.Sum += weight01;
        }

        /// <summary>Mixes the same amount into the left and right version of a shape.</summary>
        public void MixPair(string left, string right, float weight01)
        {
            Mix(left, weight01);
            Mix(right, weight01);
        }

        public bool Has(string shape) => _slots.ContainsKey(shape);

        /// <summary>Blinks once now, unless she is already blinking.</summary>
        public void Blink()
        {
            if (_blinkElapsed >= 0f) return;

            _blinkElapsed = 0f;
            _blinkLength = Random.Range(blinkSeconds.x, blinkSeconds.y);
            _doubleAt = -1f;
        }

        void Index()
        {
            _slots.Clear();
            _slotList.Clear();

            foreach (var mesh in meshes)
            {
                if (mesh == null || mesh.sharedMesh == null) continue;

                var count = mesh.sharedMesh.blendShapeCount;
                for (var i = 0; i < count; i++)
                {
                    var name = mesh.sharedMesh.GetBlendShapeName(i);

                    if (!_slots.TryGetValue(name, out var slot))
                    {
                        slot = new Slot();
                        _slots[name] = slot;
                        _slotList.Add(slot);
                    }

                    slot.Targets.Add(new Target { Mesh = mesh, Index = i });
                }
            }
        }

        void LateUpdate()
        {
            foreach (var r in resting) Mix(r.shape, r.weight);

            var blink = autoBlink ? AdvanceBlink() : 0f;

            // Relaxed lids, then a blink closes the rest of the way from there.
            var lids = eyelidDroop + blink * (1f - eyelidDroop);
            MixPair(BlinkLeft, BlinkRight, lids);

            Flush();
        }

        void Flush()
        {
            for (var s = 0; s < _slotList.Count; s++)
            {
                var slot = _slotList[s];
                var weight = Mathf.Clamp01(slot.Sum);
                slot.Sum = 0f;

                if (Mathf.Abs(weight - slot.Last) < 0.0005f) continue;
                slot.Last = weight;

                var percent = weight * 100f;      // Unity blendshape weights run 0..100
                for (var i = 0; i < slot.Targets.Count; i++)
                    slot.Targets[i].Mesh.SetBlendShapeWeight(slot.Targets[i].Index, percent);
            }
        }

        /// <summary>Where the blink is, 0 = open, 1 = shut. The lid drops fast and lifts slower.</summary>
        float AdvanceBlink()
        {
            var now = Time.time;

            if (_blinkElapsed < 0f)
            {
                if (_doubleAt >= 0f && now >= _doubleAt) { _doubleAt = -1f; Blink(); }
                else if (_doubleAt < 0f && now >= _nextBlink) Blink();
                else return 0f;
            }

            _blinkElapsed += Time.deltaTime;
            var t = _blinkElapsed / Mathf.Max(0.05f, _blinkLength);

            float weight;
            if (t < 0.38f) weight = Mathf.SmoothStep(0f, 1f, t / 0.38f);
            else weight = 1f - Mathf.SmoothStep(0f, 1f, (t - 0.38f) / 0.62f);

            if (t >= 1f)
            {
                _blinkElapsed = -1f;
                weight = 0f;

                if (Random.value < doubleBlinkChance) _doubleAt = now + 0.1f;
                else ScheduleBlink();
            }

            return weight;
        }

        void ScheduleBlink() =>
            _nextBlink = Time.time + Random.Range(secondsBetweenBlinks.x, secondsBetweenBlinks.y);
    }
}

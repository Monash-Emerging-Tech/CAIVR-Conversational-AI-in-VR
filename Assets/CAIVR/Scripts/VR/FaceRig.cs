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
    /// built: lipsync, blinking and any future expression ask for a shape by name.
    ///
    /// It also blinks. A face that never blinks is the quickest way to make a
    /// character look like a mannequin, and it costs nothing.
    /// </summary>
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

        [Header("Blinking")]
        [SerializeField] bool autoBlink = true;
        [SerializeField] Vector2 secondsBetweenBlinks = new Vector2(2.5f, 6f);
        [SerializeField] float blinkSeconds = 0.14f;

        struct Target
        {
            public SkinnedMeshRenderer Mesh;
            public int Index;
        }

        readonly Dictionary<string, List<Target>> _shapes = new Dictionary<string, List<Target>>();
        float _nextBlink;
        float _blinkStarted = -1f;

        const string BlinkLeft = "Eye_Blink_L";
        const string BlinkRight = "Eye_Blink_R";

        public void SetMeshes(SkinnedMeshRenderer[] faceMeshes) => meshes = faceMeshes;

        public void SetResting(RestingShape[] shapes) => resting = shapes;

        void Awake()
        {
            Index();
            ScheduleBlink();
            foreach (var r in resting) Set(r.shape, r.weight);
        }

        /// <summary>Sets a shape, 0 = off, 1 = fully on, on every mesh that has it.</summary>
        public void Set(string shape, float weight01)
        {
            if (!_shapes.TryGetValue(shape, out var targets)) return;

            var weight = Mathf.Clamp01(weight01) * 100f;      // Unity blendshape weights run 0..100
            for (var i = 0; i < targets.Count; i++)
                targets[i].Mesh.SetBlendShapeWeight(targets[i].Index, weight);
        }

        public bool Has(string shape) => _shapes.ContainsKey(shape);

        void Index()
        {
            _shapes.Clear();

            foreach (var mesh in meshes)
            {
                if (mesh == null || mesh.sharedMesh == null) continue;

                var count = mesh.sharedMesh.blendShapeCount;
                for (var i = 0; i < count; i++)
                {
                    var name = mesh.sharedMesh.GetBlendShapeName(i);

                    if (!_shapes.TryGetValue(name, out var list))
                    {
                        list = new List<Target>();
                        _shapes[name] = list;
                    }

                    list.Add(new Target { Mesh = mesh, Index = i });
                }
            }
        }

        void Update()
        {
            if (autoBlink) Blink();
        }

        void Blink()
        {
            var now = Time.time;

            if (_blinkStarted < 0f)
            {
                if (now < _nextBlink) return;
                _blinkStarted = now;
            }

            // Up and back down over blinkSeconds, with the close faster than the open.
            var t = (now - _blinkStarted) / Mathf.Max(0.05f, blinkSeconds);
            var weight = t < 0.4f ? t / 0.4f : 1f - (t - 0.4f) / 0.6f;

            Set(BlinkLeft, weight);
            Set(BlinkRight, weight);

            if (t >= 1f)
            {
                Set(BlinkLeft, 0f);
                Set(BlinkRight, 0f);
                _blinkStarted = -1f;
                ScheduleBlink();
            }
        }

        void ScheduleBlink() =>
            _nextBlink = Time.time + Random.Range(secondsBetweenBlinks.x, secondsBetweenBlinks.y);
    }
}

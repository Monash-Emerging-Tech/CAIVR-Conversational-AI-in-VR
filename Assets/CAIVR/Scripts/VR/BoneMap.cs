using System.Collections.Generic;
using UnityEngine;

namespace CAIVR.VR
{
    /// <summary>
    /// Finds a character's bones by name, once.
    ///
    /// The professor's gaze, posture and hands all drive bones directly, and a model's
    /// bones can be anywhere in its hierarchy. Looking them up here means each script
    /// states which joints it wants ("Head", "L" "Forearm") and nothing else about how
    /// the skeleton is laid out. The names are Character Creator's. A different model
    /// with different names only needs <see cref="Prefix"/> changed, or a map that
    /// renames them; a bone that is missing simply returns null and that part of the
    /// motion is skipped.
    /// </summary>
    public sealed class BoneMap
    {
        public const string Prefix = "CC_Base_";

        readonly Dictionary<string, Transform> _bones = new Dictionary<string, Transform>();

        public BoneMap(Transform root)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                _bones[t.name] = t;
        }

        /// <summary>A bone by its short name, e.g. "Head", "Spine02".</summary>
        public Transform Get(string part) =>
            _bones.TryGetValue(Prefix + part, out var t) ? t : null;

        /// <summary>A left or right bone, e.g. Side('L', "Forearm").</summary>
        public Transform Side(char side, string part) =>
            _bones.TryGetValue(Prefix + side + "_" + part, out var t) ? t : null;
    }
}

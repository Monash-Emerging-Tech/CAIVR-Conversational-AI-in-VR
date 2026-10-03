using System.Collections.Generic;
using CAIVR.Speech;
using CAIVR.VR;
using UnityEditor;
using UnityEngine;

namespace CAIVR.EditorTools
{
    /// <summary>
    /// Puts Dr Ellery in the room: the Character Creator model Oskar made, sat in a
    /// chair, facing the student, with her voice coming from her head and her mouth
    /// and eyelids moving.
    ///
    /// The model arrives in a T-pose with no animation, so the seated pose is worked
    /// out here by aiming bones, and saved into the scene. Nothing is animated at
    /// runtime except the face.
    ///
    /// Her materials come from the textures embedded in the model file, see
    /// <see cref="ProfessorTextures"/> and <see cref="ProfessorMaterials"/>.
    /// </summary>
    static class ProfessorBuilder
    {
        public const string CharacterPath = "Assets/BAsic_Character_with_face_articulation.Fbx";

        /// <summary>How high above the seat the hip joint sits when sitting.</summary>
        const float HipAboveSeat = 0.10f;

        // Skeleton, as Character Creator names it.
        const string Hip = "CC_Base_Hip";
        const string Head = "CC_Base_Head";
        const string JawBone = "CC_Base_JawRoot";

        /// <summary>How far the jaw swings at full volume.</summary>
        const float JawOpenDegrees = 13f;

        public static bool CharacterAvailable => AssetDatabase.LoadAssetAtPath<GameObject>(CharacterPath) != null;

        /// <param name="seatPosition">On the floor, under her hips.</param>
        /// <param name="seatTopHeight">Height of the chair's seat above the floor.</param>
        /// <param name="facing">Horizontal direction she faces: toward the student.</param>
        public static GameObject Build(Vector3 seatPosition, float seatTopHeight, Vector3 facing, VoiceLinePlayer voice)
        {
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(CharacterPath);
            if (asset == null)
            {
                Debug.LogError($"[CAIVR] The professor model is missing at {CharacterPath}. " +
                               "Merge the VR-assests branch, where it lives.");
                return null;
            }

            facing.y = 0f;
            if (facing.sqrMagnitude < 0.0001f) facing = Vector3.back;

            // The container is what the rest of the project sees: it sits on the floor
            // at her seat and faces the student, so "forward" means what you expect.
            var root = new GameObject("Professor");
            root.transform.SetPositionAndRotation(seatPosition, Quaternion.LookRotation(facing.normalized));

            var character = (GameObject)PrefabUtility.InstantiatePrefab(asset, root.transform);
            character.name = "Dr Ellery";
            character.transform.localPosition = Vector3.zero;
            character.transform.localRotation = Quaternion.identity;

            // No controller, so nothing should ever move the bones but us.
            var animator = character.GetComponent<Animator>();
            if (animator != null) Object.DestroyImmediate(animator);

            var bones = MapBones(character);

            Seat(character, bones, root.transform, seatTopHeight);

            if (!ProfessorMaterials.Apply(character))
                Debug.LogWarning("[CAIVR] The professor has no textures, so she will be plain white.");

            var face = SetUpFace(character);
            SetUpComponents(root, bones, face, voice);
            LightFromFront(root.transform);

            return root;
        }

        /// <summary>
        /// Makes sure the room's one real light is falling on her face.
        ///
        /// The project renders with a Quest-friendly URP profile that has additional lights
        /// switched off, so there is exactly one light that matters: the sun. In the blockout
        /// room it shines from behind her, so her face gets nothing but grey sky ambient, and skin
        /// lit that way looks dull and a little unwell. A face is the one thing in a VR scene that
        /// people study.
        ///
        /// So, only if the sun would leave her backlit, it is turned to come from the student's
        /// side, from above and a little to one side. If the room already lights her properly
        /// (a lit room from Oskar, say) it is left exactly as the room has it.
        /// This changes the generated scene's copy of the light only, never the source room.
        /// </summary>
        static void LightFromFront(Transform professor)
        {
            var sun = RenderSettings.sun;

            if (sun == null)
            {
                foreach (var light in Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
                {
                    if (light.type == LightType.Directional) { sun = light; break; }
                }
            }

            if (sun == null) return;

            var towardStudent = professor.forward;                 // she faces the student
            var towardLight = -sun.transform.forward;

            // Anything above this already lights her face reasonably.
            if (Vector3.Dot(towardLight.normalized, towardStudent) > 0.35f) return;

            // Light travels from the student's side toward her, angled down, turned 20 degrees so it
            // models the face instead of flattening it.
            var along = Quaternion.AngleAxis(20f, Vector3.up) * -towardStudent;
            var direction = along * Mathf.Cos(48f * Mathf.Deg2Rad) + Vector3.down * Mathf.Sin(48f * Mathf.Deg2Rad);

            sun.transform.rotation = Quaternion.LookRotation(direction.normalized);
            Debug.Log("[CAIVR] The room's light shone from behind the professor, so it was turned to light her face.");
        }

        // --- skeleton --------------------------------------------------------

        static Dictionary<string, Transform> MapBones(GameObject character)
        {
            var map = new Dictionary<string, Transform>();
            foreach (var t in character.GetComponentsInChildren<Transform>(true))
                map[t.name] = t;
            return map;
        }

        static Transform Bone(Dictionary<string, Transform> bones, string name)
        {
            if (bones.TryGetValue(name, out var t)) return t;

            Debug.LogWarning($"[CAIVR] The professor model has no bone called {name}, so part of her pose is skipped.");
            return null;
        }

        /// <summary>Turns a bone so the line from it to its child points along <paramref name="direction"/>.</summary>
        static void Aim(Transform bone, Transform child, Vector3 direction)
        {
            if (bone == null || child == null) return;

            var current = (child.position - bone.position).normalized;
            bone.rotation = Quaternion.FromToRotation(current, direction.normalized) * bone.rotation;
        }

        /// <summary>
        /// Sits her in the chair. Everything is aimed in world directions rather than
        /// in bone axes, because the model's bone axes are not the same from joint to
        /// joint and guessing them is how you get a knee bending backwards.
        /// </summary>
        static void Seat(GameObject character, Dictionary<string, Transform> bones, Transform root, float seatTop)
        {
            var hip = Bone(bones, Hip);
            if (hip == null) return;

            // Lower her until her hips are where they would be on the seat.
            var hipRestHeight = hip.position.y - root.position.y;
            var hipSeatedHeight = seatTop + HipAboveSeat;
            character.transform.localPosition = new Vector3(0f, hipSeatedHeight - hipRestHeight, 0f);

            var forward = root.forward;
            var up = Vector3.up;
            var right = root.right;

            // Rest rotations of the forearms, for putting the palms back down afterwards.
            var restForearm = new Dictionary<string, Quaternion>();
            foreach (var side in new[] { "L", "R" })
            {
                var forearm = Bone(bones, $"CC_Base_{side}_Forearm");
                if (forearm != null) restForearm[side] = forearm.rotation;
            }

            foreach (var side in new[] { "L", "R" })
            {
                var sign = side == "R" ? 1f : -1f;

                // Legs: thighs forward along the seat, shins down and slightly forward so the
                // feet land on the floor, feet flat.
                var thigh = Bone(bones, $"CC_Base_{side}_Thigh");
                var calf = Bone(bones, $"CC_Base_{side}_Calf");
                var foot = Bone(bones, $"CC_Base_{side}_Foot");
                var toe = Bone(bones, $"CC_Base_{side}_ToeBase");

                Aim(thigh, calf, forward - up * 0.04f);
                Aim(calf, foot, forward * 0.50f - up * 0.87f);
                Aim(foot, toe, forward - up * 0.10f);

                // Arms: upper arms down by the sides, forearms forward and a little up so the
                // hands rest on the table.
                var upperArm = Bone(bones, $"CC_Base_{side}_Upperarm");
                var forearmBone = Bone(bones, $"CC_Base_{side}_Forearm");
                var hand = Bone(bones, $"CC_Base_{side}_Hand");

                Aim(upperArm, forearmBone, -up + right * sign * 0.12f + forward * 0.18f);
                Aim(forearmBone, hand, forward + up * 0.28f - right * sign * 0.30f);

                // The arm swings changed which way the palm faces. Turn the forearm about its
                // own length until the palm faces down again.
                if (forearmBone != null && hand != null && restForearm.TryGetValue(side, out var rest))
                {
                    var axis = (hand.position - forearmBone.position).normalized;
                    var palmNow = (forearmBone.rotation * Quaternion.Inverse(rest)) * Vector3.down;
                    var angle = Vector3.SignedAngle(
                        Vector3.ProjectOnPlane(palmNow, axis), Vector3.ProjectOnPlane(Vector3.down, axis), axis);
                    forearmBone.rotation = Quaternion.AngleAxis(angle, axis) * forearmBone.rotation;
                }
            }
        }

        // --- face and components ---------------------------------------------

        static FaceRig SetUpFace(GameObject character)
        {
            var face = character.AddComponent<FaceRig>();

            var meshes = new List<SkinnedMeshRenderer>();
            foreach (var r in character.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (r.sharedMesh != null && r.sharedMesh.blendShapeCount > 0) meshes.Add(r);
            }

            face.SetMeshes(meshes.ToArray());

            // Calm and attentive: a slight smile, lifted cheeks, softly raised inner brows. All
            // small. The point is to take the stern edge off a neutral face, not to make her grin.
            face.SetResting(new[]
            {
                new FaceRig.RestingShape { shape = "Mouth_Smile_L", weight = 0.22f },
                new FaceRig.RestingShape { shape = "Mouth_Smile_R", weight = 0.22f },
                new FaceRig.RestingShape { shape = "Cheek_Raise_L", weight = 0.10f },
                new FaceRig.RestingShape { shape = "Cheek_Raise_R", weight = 0.10f },
                new FaceRig.RestingShape { shape = "Brow_Raise_Inner_L", weight = 0.14f },
                new FaceRig.RestingShape { shape = "Brow_Raise_Inner_R", weight = 0.14f },
            });

            EditorUtility.SetDirty(face);
            return face;
        }

        static void SetUpComponents(GameObject root, Dictionary<string, Transform> bones, FaceRig face, VoiceLinePlayer voice)
        {
            var head = Bone(bones, Head);

            // Her voice comes from her head, and the head turns to follow the student.
            var avatar = root.AddComponent<ProfessorAvatar>();
            SetRef(avatar, "voice", voice);
            SetRef(avatar, "head", head != null ? head : root.transform);

            var lipSync = root.AddComponent<LipSyncDriver>();
            SetRef(lipSync, "voice", voice);
            SetRef(lipSync, "faceRig", face);

            // On this model the jaw is a real bone: it carries the chin, the teeth, the tongue and
            // the skin around the mouth with it. The facial shapes then part the lips and round them.
            var jaw = Bone(bones, JawBone);
            if (jaw != null)
            {
                SetRef(lipSync, "jaw", jaw);
                SetFloat(lipSync, "jawOpenDegrees", JawOpenDegrees);

                // Hinge about the character's left-right axis. This bone's own axes point in odd
                // directions, so the axis is worked out in its space. This sign is the one that
                // swings the chin down and opens the mouth; the opposite sign pushes the chin up into
                // the lips and squashes the face. (Checked on the textured face, both directions.)
                SetVector(lipSync, "jawAxis", jaw.InverseTransformDirection(root.transform.right));
            }
        }

        static void SetRef(Object target, string field, Object value)
        {
            var so = new SerializedObject(target);
            var property = so.FindProperty(field);

            if (property == null)
            {
                Debug.LogWarning($"[CAIVR] {target.GetType().Name} has no field '{field}'.");
                return;
            }

            property.objectReferenceValue = value;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static void SetFloat(Object target, string field, float value)
        {
            var so = new SerializedObject(target);
            var property = so.FindProperty(field);
            if (property == null) return;

            property.floatValue = value;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static void SetVector(Object target, string field, Vector3 value)
        {
            var so = new SerializedObject(target);
            var property = so.FindProperty(field);
            if (property == null) return;

            property.vector3Value = value;
            so.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}

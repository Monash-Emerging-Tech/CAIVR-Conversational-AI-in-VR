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
    /// out here by aiming bones, and saved into the scene: forearms on the table, hands
    /// relaxed with a loose curl in the fingers. At runtime the pose is brought to life
    /// by <see cref="ProfessorGaze"/>, <see cref="ProfessorBody"/> and
    /// <see cref="ProfessorExpression"/>, which only ever move away from this rest.
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
        const float JawOpenDegrees = 15f;

        public static bool CharacterAvailable => AssetDatabase.LoadAssetAtPath<GameObject>(CharacterPath) != null;

        /// <param name="seatPosition">On the floor, under her hips.</param>
        /// <param name="seatTopHeight">Height of the chair's seat above the floor.</param>
        /// <param name="facing">Horizontal direction she faces: toward the student.</param>
        /// <param name="tableTopHeight">World height of the table top her forearms rest on. Zero for no table.</param>
        public static GameObject Build(Vector3 seatPosition, float seatTopHeight, Vector3 facing, VoiceLinePlayer voice,
                                       float tableTopHeight = 0f)
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

            Seat(character, bones, root.transform, seatTopHeight, tableTopHeight);
            RelaxHands(character, root.transform, tableTopHeight);

            if (!ProfessorMaterials.Apply(character))
                Debug.LogWarning("[CAIVR] The professor has no textures, so she will be plain white.");

            FitClothing(character);

            var face = SetUpFace(character);
            SetUpComponents(root, bones, face, voice, tableTopHeight);
            LightFromFront(root.transform);
            AddFaceLight(root.transform);

            return root;
        }

        /// <summary>
        /// A soft warm light on her face. The room is lit by baked light, which a moving character can
        /// only pick up from light probes: a blurry average that leaves a face dim and flat. One
        /// real light, aimed at her and kept gentle, gives the face shape and a little life.
        ///
        /// It uses the render pipeline's one additional light, so it is only added if the project has
        /// additional lights switched on (the Quest-friendly profile used to have them off).
        /// </summary>
        static void AddFaceLight(Transform professor)
        {
            var pipeline = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline
                as UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset;

            if (pipeline == null || pipeline.additionalLightsRenderingMode == UnityEngine.Rendering.Universal.LightRenderingMode.Disabled)
            {
                Debug.Log("[CAIVR] Additional lights are off in the render pipeline asset, so the professor has no face light.");
                return;
            }

            var go = new GameObject("Professor Face Light");
            go.transform.SetParent(professor, false);

            // In front of her at about the student's end of the table, a little above her face and to one side.
            // Bright enough that her skin reads warm rather than muddy, and far and low enough that it strikes
            // her upright face squarely but the horizontal table only at a glancing angle: closer and higher,
            // the same light blew the tabletop out to white. Both were compared side by side on her face.
            go.transform.localPosition = new Vector3(-0.40f, 1.75f, 2.20f);
            go.transform.LookAt(professor.position + Vector3.up * 1.12f);

            var light = go.AddComponent<Light>();
            light.type = LightType.Spot;
            light.color = new Color(1f, 0.94f, 0.86f);
            light.intensity = FaceLightIntensity;
            light.range = 4.0f;
            light.spotAngle = 40f;
            light.innerSpotAngle = 24f;
            light.shadows = LightShadows.None;
            light.lightmapBakeType = LightmapBakeType.Realtime;
        }

        const float FaceLightIntensity = 7.5f;

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
        /// <summary>How high above the table top the elbow joint sits when the forearm rests on it (the arm has thickness).</summary>
        const float ElbowAboveTable = 0.032f;

        static void Seat(GameObject character, Dictionary<string, Transform> bones, Transform root, float seatTop, float tableTop)
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

                // Arms.
                var upperArm = Bone(bones, $"CC_Base_{side}_Upperarm");
                var forearmBone = Bone(bones, $"CC_Base_{side}_Forearm");
                var hand = Bone(bones, $"CC_Base_{side}_Hand");

                if (tableTop > 0f && upperArm != null && forearmBone != null && hand != null)
                {
                    // Forearms resting on the table, the way someone sits at a desk to talk to you. The elbow
                    // is where the table top puts it, so how far the upper arm swings forward follows from how
                    // far the shoulder is above the table. The hands are not mirror images: that alone is
                    // what makes a seated figure look posed.
                    var reach = Vector3.Distance(upperArm.position, forearmBone.position);
                    var drop = upperArm.position.y - (tableTop + ElbowAboveTable);
                    var cos = Mathf.Clamp(drop / reach, 0.3f, 1f);
                    var sin = Mathf.Sqrt(1f - cos * cos);
                    var inward = side == "L" ? 0.42f : 0.30f;

                    Aim(upperArm, forearmBone, -up * cos + forward * sin + right * sign * 0.10f);
                    Aim(forearmBone, hand, forward - right * sign * inward + up * 0.02f);
                }
                else
                {
                    // No table to rest on: upper arms down by the sides, forearms forward and a little up.
                    Aim(upperArm, forearmBone, -up + right * sign * 0.12f + forward * 0.18f);
                    Aim(forearmBone, hand, forward + up * 0.28f - right * sign * 0.30f);
                }

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

        /// <summary>
        /// Gives each hand the loose curl of a hand at ease and settles it on the table. The model's fingers
        /// are as straight and splayed as in its modelling pose, which is the stiffest thing about a seated figure.
        /// </summary>
        static void RelaxHands(GameObject character, Transform root, float tableTop)
        {
            var map = new BoneMap(character.transform);

            foreach (var side in new[] { 'L', 'R' })
            {
                var arm = ArmRig.Create(map, side, root);
                if (arm == null) continue;

                arm.RelaxFingers();
                if (tableTop > 0f) arm.RestOnTable(tableTop);
            }
        }

        // --- clothing --------------------------------------------------------

        const string FittedMeshFolder = "Assets/CAIVR/Professor/Meshes";

        /// <summary>
        /// Pushes the shirt a few millimetres off the body. Posed with her forearms on the table, the arm's skin
        /// shows through the rolled sleeve as a brown patch, because the model's clothes sit right on the
        /// skin. The shirt is copied, moved outwards along its own surface normals and saved, so the
        /// model file itself is untouched.
        /// </summary>
        static void FitClothing(GameObject character)
        {
            const float outward = 0.005f;

            foreach (var renderer in character.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (!renderer.name.Contains("shirt") || renderer.sharedMesh == null) continue;

                var fitted = Object.Instantiate(renderer.sharedMesh);
                fitted.name = renderer.sharedMesh.name + " fitted";

                var vertices = fitted.vertices;
                var normals = fitted.normals;
                if (normals.Length != vertices.Length) { Object.DestroyImmediate(fitted); continue; }

                for (var i = 0; i < vertices.Length; i++) vertices[i] += normals[i] * outward;

                fitted.vertices = vertices;
                fitted.RecalculateBounds();

                System.IO.Directory.CreateDirectory(FittedMeshFolder);
                var path = $"{FittedMeshFolder}/{fitted.name}.asset";
                var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);

                if (existing != null)
                {
                    EditorUtility.CopySerialized(fitted, existing);
                    Object.DestroyImmediate(fitted);
                    renderer.sharedMesh = existing;
                }
                else
                {
                    AssetDatabase.CreateAsset(fitted, path);
                    renderer.sharedMesh = fitted;
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

            // Calm and attentive: the faintest smile. The model's all-zero face is stern with wide, staring
            // lids, so everything else comes from ProfessorExpression, which is always moving, and from
            // FaceRig's lid droop. Raised brows with open lids read as startled, so none are held here.
            face.SetResting(new[]
            {
                new FaceRig.RestingShape { shape = "Mouth_Smile_L", weight = 0.05f },
                new FaceRig.RestingShape { shape = "Mouth_Smile_R", weight = 0.05f },
            });

            EditorUtility.SetDirty(face);
            return face;
        }

        static void SetUpComponents(GameObject root, Dictionary<string, Transform> bones, FaceRig face, VoiceLinePlayer voice,
                                    float tableTopHeight)
        {
            var head = Bone(bones, Head);

            // Her voice comes from her head.
            var avatar = root.AddComponent<ProfessorAvatar>();
            SetRef(avatar, "voice", voice);
            SetRef(avatar, "head", head != null ? head : root.transform);

            var lipSync = root.AddComponent<LipSyncDriver>();
            SetRef(lipSync, "voice", voice);
            SetRef(lipSync, "faceRig", face);

            // She is alive: she follows the conversation, looks at the student, breathes, gestures and
            // reacts. These find each other and the conversation themselves when the scene starts.
            root.AddComponent<ProfessorMood>();
            root.AddComponent<ProfessorGaze>();
            root.AddComponent<ProfessorExpression>();

            var body = root.AddComponent<ProfessorBody>();
            SetFloat(body, "tableTopHeight", tableTopHeight);

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

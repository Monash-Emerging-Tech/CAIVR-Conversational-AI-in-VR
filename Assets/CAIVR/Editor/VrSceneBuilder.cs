using System.IO;
using CAIVR.Dialogue;
using CAIVR.Speech;
using CAIVR.VR;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.SceneManagement;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace CAIVR.EditorTools
{
    /// <summary>
    /// Assembles the consultation scene.
    ///
    /// Works on a COPY of Oskar's consultation scene, so his file is never
    /// touched and cannot be conflicted on. The scene is generated rather than
    /// committed by hand for the same reason as the others: rerun the menu item
    /// whenever the room changes and everything is placed again.
    ///
    /// Two variants:
    ///   - the default has NO rig. A plain camera sits at seated eye height, the
    ///     mouse looks around, and every piece of UI is an object in the room.
    ///   - an optional variant adds the template's own XR rig, event system and
    ///     hand-tracking permission prefab, kept so the headset path is not lost.
    /// </summary>
    public static class VrSceneBuilder
    {
        const string SourceScene = "Assets/Scenes/Consultation Scene.unity";
        const string TemplateScene = "Assets/Scenes/SampleScene.unity";
        const string OutputScene = "Assets/CAIVR/Scenes/ConsultationVR.unity";
        const string OutputSceneRig = "Assets/CAIVR/Scenes/ConsultationVR_Rig.unity";

        const string MaterialFolder = "Assets/CAIVR/Materials";
        const string ResourcesMaterialFolder = "Assets/CAIVR/Resources/CAIVR/Materials";
        const string FontFolder = "Assets/CAIVR/Resources/CAIVR/Fonts";
        const string TemplateFont = "Assets/VRTemplateAssets/Fonts/Inter/Inter-Regular SDF.asset";

        const string RigPrefab =
            "Assets/VRTemplateAssets/Prefabs/Setup/Complete XR Origin Set Up Hands Variant.prefab";
        const string PermissionsPrefab =
            "Assets/VRTemplateAssets/Prefabs/Setup/Hands Permissions Manager.prefab";

        // Measured from Oskar's room: the meeting room spans roughly x 0.1..4.2 and
        // z -1.45..2.1, the glass door is on the west wall at z -0.69, and the
        // table's top is at y 0.74. The student sits on the door side facing east.
        static readonly Vector3 DefaultStudent = new Vector3(1.30f, 0f, -0.66f);
        static readonly Vector3 DefaultProfessor = new Vector3(3.15f, 0f, -0.47f);
        static readonly Vector3 DefaultNotebook = new Vector3(1.85f, 0.76f, -0.55f);
        const float SeatedEyeHeight = 1.2f;

        // Resolved per build. A room scene may contain empty objects with these names to
        // say exactly where people sit; otherwise the measured defaults above are used.
        // Everything else (which way people face, where the menu floats) derives from
        // the two seat positions, so a re-imported or rotated room still works.
        const string StudentMarker = "CAIVR_StudentSpot";
        const string ProfessorMarker = "CAIVR_ProfessorSpot";
        const string NotebookMarker = "CAIVR_NotebookSpot";

        static Vector3 StudentSpot;
        static Vector3 ProfessorSpot;
        static Vector3 NotebookSpot;

        /// <summary>Horizontal direction from the student toward the professor.</summary>
        static Vector3 Forward = Vector3.right;

        /// <summary>
        /// The room to build around. Prefers the most recently changed scene whose name
        /// mentions "Consult", so when a teammate lands a textured, lit version it is
        /// picked up without touching this file. Our own generated scenes are skipped.
        /// </summary>
        static string ResolveSourceScene()
        {
            string best = null;
            var bestTime = System.DateTime.MinValue;

            foreach (var guid in AssetDatabase.FindAssets("Consult t:Scene"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.StartsWith("Assets/CAIVR/")) continue;

                var time = File.GetLastWriteTimeUtc(path);
                if (time > bestTime) { best = path; bestTime = time; }
            }

            if (best == null) return SourceScene;

            Debug.Log($"[CAIVR] Building around room scene: {best}");
            return best;
        }

        /// <summary>
        /// Reads seat positions from marker objects in the room, falling back to the
        /// measured defaults, and works out which way everyone faces.
        /// </summary>
        static void ResolveSpots()
        {
            StudentSpot = Spot(StudentMarker, DefaultStudent);
            ProfessorSpot = Spot(ProfessorMarker, DefaultProfessor);
            NotebookSpot = Spot(NotebookMarker, DefaultNotebook);

            var flat = ProfessorSpot - StudentSpot;
            flat.y = 0f;
            Forward = flat.sqrMagnitude > 0.01f ? flat.normalized : Vector3.right;
        }

        static Vector3 Spot(string markerName, Vector3 fallback)
        {
            var marker = GameObject.Find(markerName);
            if (marker == null) return fallback;

            Debug.Log($"[CAIVR] Using {markerName} from the room scene at {marker.transform.position}.");
            return marker.transform.position;
        }

        [MenuItem("CAIVR/VR/Create VR Consultation Scene", priority = 100)]
        public static void Create() => Build(withRig: false);

        [MenuItem("CAIVR/VR/Create VR Consultation Scene (with template rig)", priority = 101)]
        public static void CreateWithRig() => Build(withRig: true);

        static void Build(bool withRig)
        {
            var source = ResolveSourceScene();
            if (!File.Exists(source))
            {
                Debug.LogError($"[CAIVR] {source} not found. Merge the Consult-Room branch first.");
                return;
            }

            if (withRig && AssetDatabase.LoadAssetAtPath<GameObject>(RigPrefab) == null)
            {
                Debug.LogError($"[CAIVR] Template rig prefab missing at {RigPrefab}.");
                return;
            }

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            var output = withRig ? OutputSceneRig : OutputScene;

            Directory.CreateDirectory(Path.GetDirectoryName(output));
            Directory.CreateDirectory(MaterialFolder);
            Directory.CreateDirectory(ResourcesMaterialFolder);
            Directory.CreateDirectory(FontFolder);

            EnsureUiAssets();

            AssetDatabase.DeleteAsset(output);
            AssetDatabase.CopyAsset(source, output);
            var scene = EditorSceneManager.OpenScene(output, OpenSceneMode.Single);

            RemoveStandaloneCamera(scene);
            ResolveSpots();

            // The room has no colliders as modelled. Without them anything with physics
            // (the notebook) falls through the table, so every variant needs them.
            AddEnvironmentColliders();

            if (withRig) AddTemplateRig(scene);
            else AddSeatedCamera();

            var conversation = AddConversation();
            AddProfessor(conversation.voice);
            AddSubtitles(conversation.runner, conversation.speech);
            var menu = AddMenu();
            AddIntro(conversation.runner, conversation.voice, menu);
            AddNotebook(conversation.runner, physical: withRig);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            RegisterInBuildSettings(output);

            Debug.Log($"[CAIVR] Consultation scene created at {output} ({(withRig ? "with template rig" : "no rig")}).");
        }

        // --- shared assets ---------------------------------------------------

        /// <summary>
        /// The UI reads two things from Resources at runtime: the Inter font and
        /// the bezel material. Generated here so a fresh clone needs no manual setup.
        /// </summary>
        static void EnsureUiAssets()
        {
            var font = $"{FontFolder}/Inter SDF.asset";
            if (AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(font) == null)
            {
                if (AssetDatabase.CopyAsset(TemplateFont, font))
                    Debug.Log("[CAIVR] Copied the Inter font into Resources for runtime UI.");
                else
                    Debug.LogWarning("[CAIVR] Could not copy the Inter font; the UI will fall back to the default font.");
            }

            Mat("UiBezel", new Color(0.02f, 0.05f, 0.09f), 0.55f, 0.5f, ResourcesMaterialFolder);
        }

        // --- scene plumbing --------------------------------------------------

        static void RemoveStandaloneCamera(Scene scene)
        {
            // We bring our own camera, or the rig brings one. A second one tagged
            // MainCamera, and a second AudioListener, would fight for both roles.
            foreach (var root in scene.GetRootGameObjects())
            {
                if (root.GetComponent<Camera>() != null) Object.DestroyImmediate(root);
            }
        }

        static void AddSeatedCamera()
        {
            var go = new GameObject("Student Camera", typeof(Camera), typeof(AudioListener), typeof(SeatedViewpoint));
            go.tag = "MainCamera";

            // Facing east, toward the professor and the menu in front of the student.
            go.transform.SetPositionAndRotation(
                new Vector3(StudentSpot.x, SeatedEyeHeight, StudentSpot.z), Quaternion.LookRotation(Forward));

            var camera = go.GetComponent<Camera>();
            camera.fieldOfView = 70f;
            camera.nearClipPlane = 0.03f;      // close enough to read the notebook held up to the face

            // A normal event system. Mouse clicks reach the in-room UI through it.
            new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
        }

        static void AddTemplateRig(Scene target)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(RigPrefab);
            var rig = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            rig.name = "XR Origin Hands (template rig)";
            rig.transform.SetPositionAndRotation(StudentSpot, Quaternion.LookRotation(Forward));
            rig.AddComponent<RigFallRecovery>();

            // XR rays and pokes only reach UI through an event system carrying
            // XRUIInputModule wired to the XR input actions. The template scene has a
            // correctly wired one, so copy that rather than rebuild it.
            var template = EditorSceneManager.OpenScene(TemplateScene, OpenSceneMode.Additive);
            try
            {
                foreach (var root in template.GetRootGameObjects())
                {
                    if (root.name != "EventSystem") continue;

                    var copy = Object.Instantiate(root);
                    copy.name = "EventSystem";
                    SceneManager.MoveGameObjectToScene(copy, target);
                    break;
                }
            }
            finally
            {
                EditorSceneManager.CloseScene(template, true);
            }

            // Quest asks for hand-tracking permission at runtime. Without this prefab
            // the hands silently never appear on a standalone build.
            var permissions = AssetDatabase.LoadAssetAtPath<GameObject>(PermissionsPrefab);
            if (permissions != null) PrefabUtility.InstantiatePrefab(permissions);

            new GameObject("XR Interaction Manager", typeof(XRInteractionManager));
        }

        /// <summary>
        /// The room as modelled has no colliders at all, and the rig has gravity,
        /// so without these the student falls through the floor on load. Only needed
        /// when a rig is present, and added to this generated copy only.
        /// </summary>
        static void AddEnvironmentColliders()
        {
            var environment = GameObject.Find("Consultation_Env");
            if (environment == null) return;

            var added = 0;
            foreach (var filter in environment.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter.sharedMesh == null || filter.GetComponent<Collider>() != null) continue;

                filter.gameObject.AddComponent<MeshCollider>().sharedMesh = filter.sharedMesh;
                added++;
            }

            Debug.Log($"[CAIVR] Added {added} mesh colliders to the environment.");
        }

        // --- conversation ----------------------------------------------------

        static (ConversationRunner runner, SpeechService speech, VoiceLinePlayer voice) AddConversation()
        {
            var go = new GameObject("Conversation");
            var speech = go.AddComponent<SpeechService>();
            var voice = go.AddComponent<VoiceLinePlayer>();
            var runner = go.AddComponent<ConversationRunner>();

            // Auto: use the microphone if there is one and a key, else fall back.
            SetEnum(speech, "backend", (int)SpeechBackend.Auto);
            SetRef(runner, "speech", speech);
            SetRef(runner, "voice", voice);

            return (runner, speech, voice);
        }

        // --- professor -------------------------------------------------------

        static void AddProfessor(VoiceLinePlayer voice)
        {
            var skin = Mat("Professor_Skin", new Color(0.85f, 0.68f, 0.56f), 0.25f);
            var cloth = Mat("Professor_Cloth", Hex("006DAE"), 0.1f);
            var dark = Mat("Professor_Dark", new Color(0.06f, 0.05f, 0.05f), 0.1f);

            var root = new GameObject("Professor (placeholder)");
            // Facing west (-X), toward the student.
            root.transform.SetPositionAndRotation(ProfessorSpot, Quaternion.LookRotation(-Forward));

            var torso = Primitive(PrimitiveType.Capsule, "Torso", root.transform, cloth);
            torso.transform.localPosition = new Vector3(0f, 0.82f, 0f);
            torso.transform.localScale = new Vector3(0.40f, 0.34f, 0.28f);

            var head = Primitive(PrimitiveType.Sphere, "Head", root.transform, skin);
            head.transform.localPosition = new Vector3(0f, 1.28f, 0f);
            head.transform.localScale = Vector3.one * 0.22f;

            foreach (var side in new[] { -1f, 1f })
            {
                var eye = Primitive(PrimitiveType.Sphere, side < 0 ? "Eye_L" : "Eye_R", head.transform, dark);
                eye.transform.localPosition = new Vector3(0.20f * side, 0.12f, 0.43f);
                eye.transform.localScale = Vector3.one * 0.11f;
            }

            var mouth = Primitive(PrimitiveType.Cube, "Mouth", head.transform, dark);
            mouth.transform.localPosition = new Vector3(0f, -0.22f, 0.46f);
            mouth.transform.localScale = new Vector3(0.36f, 0.14f, 0.06f);

            var avatar = root.AddComponent<ProfessorAvatar>();
            SetRef(avatar, "voice", voice);
            SetRef(avatar, "head", head.transform);

            var lipSync = root.AddComponent<LipSyncDriver>();
            SetRef(lipSync, "voice", voice);
            SetRef(lipSync, "mouth", mouth.transform);
        }

        // --- UI --------------------------------------------------------------

        static void AddSubtitles(ConversationRunner runner, SpeechService speech)
        {
            // Subtitles are laid over the camera's view, not placed in the room: they
            // stay readable wherever the student looks and cannot clip into a wall.
            var go = new GameObject("Subtitles");
            var hud = go.AddComponent<SubtitleHud>();
            SetRef(hud, "runner", runner);
            SetRef(hud, "speech", speech);
        }

        static VrMenuPanel AddMenu()
        {
            // Floating just in front of the student, tilted down a little the way
            // you would hold a tablet. Read, choose, press Start, and it is gone.
            var go = new GameObject("Scenario Menu");
            go.transform.SetPositionAndRotation(
                StudentSpot + Forward * 1.05f + Vector3.up * 1.12f,
                Quaternion.LookRotation(Forward) * Quaternion.Euler(8f, 0f, 0f));

            var menu = go.AddComponent<VrMenuPanel>();
            SetFloat(menu, "widthMeters", 1.15f);
            return menu;
        }

        static void AddIntro(ConversationRunner runner, VoiceLinePlayer voice, VrMenuPanel menu)
        {
            var go = new GameObject("Scenario Intro");
            var overlay = go.AddComponent<VrIntroOverlay>();
            var flow = go.AddComponent<VrScenarioFlow>();

            SetRef(flow, "runner", runner);
            SetRef(flow, "voice", voice);
            SetRef(flow, "overlay", overlay);
            SetRef(flow, "menu", menu);
        }

        // --- notebook --------------------------------------------------------

        static void AddNotebook(ConversationRunner runner, bool physical)
        {
            var cover = Mat("Notebook_Cover", Hex("006DAE"), 0.2f);

            var root = new GameObject("Context Notebook");
            // Text on the page should read away from the student, who looks east.
            root.transform.SetPositionAndRotation(NotebookSpot, Quaternion.LookRotation(Forward));

            var body = root.AddComponent<Rigidbody>();
            body.mass = 0.3f;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

            // With no hands there is nothing to pick it up, so physics could only make
            // it fall off the table. Frozen in place; click to read still works.
            body.isKinematic = !physical;
            body.useGravity = physical;

            // Collider on the parent so the visual can be scaled freely.
            var box = root.AddComponent<BoxCollider>();
            box.size = new Vector3(0.20f, 0.025f, 0.27f);

            // Kept on the prop so hands can pick it up when a rig is present.
            var grab = root.AddComponent<XRGrabInteractable>();
            grab.movementType = XRBaseInteractable.MovementType.VelocityTracking;

            var visual = Primitive(PrimitiveType.Cube, "Cover", root.transform, cover);
            visual.transform.localScale = new Vector3(0.20f, 0.022f, 0.27f);

            // The page: a canvas lying on the cover, rotated so its +Z points down
            // into the cover, which makes the text readable from above.
            var canvas = VrUi.CreateWorldCanvas("Page", root.transform, new Vector2(400f, 540f), 0.19f);
            canvas.transform.localPosition = new Vector3(0f, 0.0125f, 0f);
            canvas.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);

            var page = canvas.transform;
            VrUi.Surface(page, "Paper", Layout.Fill(), new Color(0.98f, 0.97f, 0.93f), 14f);
            VrUi.Surface(page, "Accent", Layout.TopStretch(24, 24, 24, 8), MonashTheme.Blue, 4f);
            VrUi.Eyebrow(page, "Heading", Layout.TopLeft(24, 44, 340, 30), "Background", MonashTheme.Blue);

            var text = VrUi.Text(page, "Text", Layout.TopStretch(24, 88, 24, 380),
                "Your situation appears here when the consultation starts.", 30, MonashTheme.BlueDeep,
                TextAlignmentOptions.TopLeft, autoSize: true);

            VrUi.Text(page, "Footer", Layout.BottomStretch(24, 16, 24, 30), "Click to read", 22,
                new Color(0.42f, 0.48f, 0.55f), TextAlignmentOptions.MidlineLeft);

            var notebook = root.AddComponent<ContextNotebook>();
            SetRef(notebook, "runner", runner);
            SetRef(notebook, "grab", grab);
            SetRef(notebook, "pageText", text);
        }

        // --- helpers ---------------------------------------------------------

        static GameObject Primitive(PrimitiveType type, string name, Transform parent, Material material)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.GetComponent<Renderer>().sharedMaterial = material;

            // Visual only. A collider on the head or torso would add nothing, and
            // the notebook has its own box on the parent.
            var collider = go.GetComponent<Collider>();
            if (collider != null) Object.DestroyImmediate(collider);

            return go;
        }

        static Material Mat(string name, Color color, float smoothness, float metallic = 0f,
                            string folder = MaterialFolder)
        {
            var path = $"{folder}/{name}.mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null) return existing;

            var shader = Shader.Find("Universal Render Pipeline/Lit");
            var material = new Material(shader) { name = name };
            material.SetColor("_BaseColor", color);
            material.SetFloat("_Smoothness", smoothness);
            material.SetFloat("_Metallic", metallic);

            AssetDatabase.CreateAsset(material, path);
            return material;
        }

        static Color Hex(string rgb)
        {
            ColorUtility.TryParseHtmlString("#" + rgb, out var color);
            return color;
        }

        static void SetRef(Object target, string field, Object value)
        {
            var so = new SerializedObject(target);
            var property = so.FindProperty(field);

            if (property == null) { Debug.LogWarning($"[CAIVR] {target.GetType().Name} has no field '{field}'."); return; }

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

        static void SetEnum(Object target, string field, int index)
        {
            var so = new SerializedObject(target);
            var property = so.FindProperty(field);
            if (property == null) return;

            property.enumValueIndex = index;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static void RegisterInBuildSettings(string path)
        {
            var scenes = EditorBuildSettings.scenes;
            foreach (var s in scenes) if (s.path == path) return;

            var list = new System.Collections.Generic.List<EditorBuildSettingsScene>(scenes)
            {
                new EditorBuildSettingsScene(path, true),
            };
            EditorBuildSettings.scenes = list.ToArray();
        }
    }
}

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
    /// One scene, two ways in. It carries a plain camera with mouse look for the
    /// desktop AND the XR rig with hand tracking for a headset; at runtime
    /// <see cref="ExperienceRig"/> switches on whichever applies. On a PC with no
    /// headset there is no rig in play, with one the same scene just works, and
    /// there are no separate scenes to keep in step.
    ///
    /// The XR rig is the template's own, trimmed for sitting at a table: no
    /// walking, no teleporting, no gravity, no locomotion tutorial tooltips.
    /// </summary>
    public static class VrSceneBuilder
    {
        const string SourceScene = "Assets/Scenes/Consultation Scene.unity";
        const string TemplateScene = "Assets/Scenes/SampleScene.unity";
        const string OutputScene = "Assets/CAIVR/Scenes/ConsultationVR.unity";

        // An earlier version generated a second scene for the rig. It is gone now that
        // the one scene carries both, and is removed if an old checkout still has it.
        const string RetiredRigScene = "Assets/CAIVR/Scenes/ConsultationVR_Rig.unity";

        const string SimulatorPrefab =
            "Assets/Samples/XR Interaction Toolkit/3.4.1/XR Interaction Simulator/XR Interaction Simulator.prefab";

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

        // The professor's chair is the one at the far end of the table that is closest to
        // the student's line of sight. This is on the floor under her hips, pulled back
        // from the table's end so she sits clear of it rather than through it.
        static readonly Vector3 DefaultProfessor = new Vector3(3.12f, 0f, -0.92f);
        const float ProfessorSeatTop = 0.42f;

        // On the student's left side of the table, not straight ahead. The menu hangs
        // in front of the student and its lowest button, Start, is only a hand's width
        // above the table. A notebook underneath it counts as a grab target, and while
        // a hand is near a grab target it cannot poke, so Start would not press.
        static readonly Vector3 DefaultNotebook = new Vector3(1.75f, 0.76f, -0.22f);
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
        public static void Create() => Build();

        static void Build()
        {
            var source = ResolveSourceScene();
            if (!File.Exists(source))
            {
                Debug.LogError($"[CAIVR] {source} not found. Merge the Consult-Room branch first.");
                return;
            }

            if (AssetDatabase.LoadAssetAtPath<GameObject>(RigPrefab) == null)
            {
                Debug.LogError($"[CAIVR] Template rig prefab missing at {RigPrefab}.");
                return;
            }

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            var output = OutputScene;
            RetireRigScene();

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

            // The room has no colliders as modelled. Without them the notebook falls
            // through the table, and the head-locked UI cannot tell where the walls are.
            var environment = AddEnvironmentColliders();

            AddRigs(scene, environment);

            var conversation = AddConversation();
            AddProfessor(conversation.voice);
            AddSubtitles(conversation.runner, conversation.speech);
            var menu = AddMenu();
            AddIntro(conversation.runner, conversation.voice, menu);
            AddNotebook(conversation.runner);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            RegisterInBuildSettings(output);

            Debug.Log($"[CAIVR] Consultation scene created at {output}.");
        }

        /// <summary>Removes the old generated rig scene, and its build settings entry, if present.</summary>
        static void RetireRigScene()
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(RetiredRigScene) == null) return;

            var kept = new System.Collections.Generic.List<EditorBuildSettingsScene>();
            foreach (var s in EditorBuildSettings.scenes)
            {
                if (s.path != RetiredRigScene) kept.Add(s);
            }
            EditorBuildSettings.scenes = kept.ToArray();

            AssetDatabase.DeleteAsset(RetiredRigScene);
            Debug.Log("[CAIVR] Removed the old ConsultationVR_Rig scene; ConsultationVR now carries the headset rig itself.");
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

        /// <summary>
        /// Both ways in, plus what lets the runtime choose between them: a desktop
        /// camera, the headset rig, an event system that can serve either, and the
        /// <see cref="ExperienceRig"/> that switches one on.
        /// </summary>
        static void AddRigs(Scene target, GameObject environment)
        {
            // Where the student's eyes are, and which way they face. Both rigs are
            // brought to this.
            var seat = new GameObject("Student Seat");
            seat.transform.SetPositionAndRotation(
                new Vector3(StudentSpot.x, SeatedEyeHeight, StudentSpot.z), Quaternion.LookRotation(Forward));

            var desktop = AddDesktopCamera();
            var headset = AddHeadsetRig();
            var events = AddEventSystem(target);
            var simulator = AddSimulator();

            // Quest asks for hand-tracking permission at runtime. Without this prefab
            // the hands silently never appear on a standalone build.
            var permissions = AssetDatabase.LoadAssetAtPath<GameObject>(PermissionsPrefab);
            if (permissions != null) PrefabUtility.InstantiatePrefab(permissions);

            new GameObject("XR Interaction Manager", typeof(XRInteractionManager));

            var experience = new GameObject("Experience", typeof(ExperienceRig));
            var rig = experience.GetComponent<ExperienceRig>();

            SetRef(rig, "desktopRig", desktop);
            SetRef(rig, "headsetRig", headset);
            SetRef(rig, "desktopInput", events.GetComponent<InputSystemUIInputModule>());
            SetRef(rig, "headsetInput", events.GetComponent<UnityEngine.XR.Interaction.Toolkit.UI.XRUIInputModule>());
            SetRef(rig, "seat", seat.transform);
            SetRef(rig, "obstacles", environment != null ? environment.transform : null);
            SetRef(rig, "simulator", simulator);
            SetRef(rig, "origin", headset.GetComponent<Unity.XR.CoreUtils.XROrigin>());
        }

        static GameObject AddDesktopCamera()
        {
            var go = new GameObject("Student Camera", typeof(Camera), typeof(AudioListener), typeof(SeatedViewpoint));
            go.tag = "MainCamera";

            // Facing the professor and the menu in front of the student.
            go.transform.SetPositionAndRotation(
                new Vector3(StudentSpot.x, SeatedEyeHeight, StudentSpot.z), Quaternion.LookRotation(Forward));

            var camera = go.GetComponent<Camera>();
            camera.fieldOfView = 70f;
            camera.nearClipPlane = 0.03f;      // close enough to read the notebook held up to the face

            return go;
        }

        /// <summary>
        /// The template's own rig, with hand tracking, hand meshes, poke and pinch
        /// interactors, and the switch between hands and controllers. Trimmed for
        /// sitting at a table: the student cannot walk, teleport or fall, and the
        /// tutorial tooltips about those are gone.
        /// </summary>
        static GameObject AddHeadsetRig()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(RigPrefab);
            var rig = (GameObject)PrefabUtility.InstantiatePrefab(prefab);

            // Unpacked so the pieces below can be removed from this scene's copy.
            PrefabUtility.UnpackPrefabInstance(rig, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);

            rig.name = "XR Rig (headset)";
            rig.transform.SetPositionAndRotation(
                new Vector3(StudentSpot.x, 0f, StudentSpot.z), Quaternion.LookRotation(Forward));

            // No walking, turning, jumping or gravity.
            var locomotion = rig.transform.Find("Locomotion");
            if (locomotion != null) locomotion.gameObject.SetActive(false);

            var body = rig.GetComponent<CharacterController>();
            if (body != null) Object.DestroyImmediate(body);

            foreach (var manager in rig.GetComponentsInChildren<MonoBehaviour>(true))
            {
                // The controller's thumbsticks only drive locomotion and teleport.
                if (manager != null && manager.GetType().Name == "ControllerInputActionManager") manager.enabled = false;
            }

            // Things that do not belong in a seated conversation:
            //   - the aiming ray used for teleporting, and the "how to move" tooltips
            //   - eye gaze interaction, which only does anything on headsets with eye
            //     tracking, and logs a warning on every run on ones without
            //   - the XR Hands sample's debug visualiser, which draws a gizmo on every
            //     joint over the real hand meshes
            foreach (var t in rig.GetComponentsInChildren<Transform>(true))
            {
                if (t == null) continue;

                if (t.name == "Teleport Interactor" || t.name.StartsWith("Affordance Callouts")
                    || t.name == "Gaze Interactor" || t.name == "Gaze Stabilized"
                    || t.name == "Hand Visualizer")
                    t.gameObject.SetActive(false);
            }

            // Off in the saved scene. On a flat screen it stays off, so there is no rig
            // in play; in a headset ExperienceRig switches it on.
            rig.SetActive(false);
            return rig;
        }

        /// <summary>
        /// One event system that can serve both: the standard module for the mouse, and
        /// the XR module (copied from the template, where its input actions are already
        /// wired) for rays and pokes. <see cref="ExperienceRig"/> switches on the one
        /// that applies.
        /// </summary>
        static GameObject AddEventSystem(Scene target)
        {
            GameObject events = null;

            var template = EditorSceneManager.OpenScene(TemplateScene, OpenSceneMode.Additive);
            try
            {
                foreach (var root in template.GetRootGameObjects())
                {
                    if (root.name != "EventSystem") continue;

                    events = Object.Instantiate(root);
                    events.name = "EventSystem";
                    SceneManager.MoveGameObjectToScene(events, target);
                    break;
                }
            }
            finally
            {
                EditorSceneManager.CloseScene(template, true);
            }

            if (events == null)
            {
                Debug.LogError("[CAIVR] The template scene has no EventSystem to copy. Hands will not be able to press UI.");
                events = new GameObject("EventSystem", typeof(EventSystem));
            }

            if (events.GetComponent<InputSystemUIInputModule>() == null)
                events.AddComponent<InputSystemUIInputModule>();

            // The desktop module starts on; ExperienceRig swaps them if there is a headset.
            events.GetComponent<InputSystemUIInputModule>().enabled = true;
            var xr = events.GetComponent<UnityEngine.XR.Interaction.Toolkit.UI.XRUIInputModule>();
            if (xr != null) xr.enabled = false;

            return events;
        }

        /// <summary>
        /// Lets the hands, rays and grabbing be tried at a desk. Tagged EditorOnly, so it
        /// is not in a build at all, and the runtime only switches it on in the Editor
        /// with no headset connected and the testing option ticked.
        /// </summary>
        static GameObject AddSimulator()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(SimulatorPrefab);
            if (prefab == null)
            {
                Debug.LogWarning("[CAIVR] XR Interaction Simulator sample is not imported, so a headset cannot be " +
                                 "simulated in the Editor. Import it from Package Manager > XR Interaction Toolkit > Samples.");
                return null;
            }

            var simulator = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            simulator.tag = "EditorOnly";
            simulator.SetActive(false);
            return simulator;
        }

        /// <summary>
        /// The room as modelled has no colliders at all. Without these the notebook
        /// falls through the table and head-locked UI cannot tell where the walls are.
        /// Added to this generated copy only, never to the source room.
        /// </summary>
        static GameObject AddEnvironmentColliders()
        {
            var environment = GameObject.Find("Consultation_Env");
            if (environment == null) return null;

            var added = 0;
            foreach (var filter in environment.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter.sharedMesh == null || filter.GetComponent<Collider>() != null) continue;

                filter.gameObject.AddComponent<MeshCollider>().sharedMesh = filter.sharedMesh;
                added++;
            }

            Debug.Log($"[CAIVR] Added {added} mesh colliders to the environment.");
            return environment;
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
            // Dr Ellery, the Character Creator model, sat in the chair and facing the student.
            // See ProfessorBuilder for how she is posed and why her colours are flat for now.
            var facing = StudentSpot - ProfessorSpot;
            ProfessorBuilder.Build(ProfessorSpot, ProfessorSeatTop, facing, voice);
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

        static void AddNotebook(ConversationRunner runner)
        {
            var cover = Mat("Notebook_Cover", Hex("006DAE"), 0.2f);

            var root = new GameObject("Context Notebook");
            // Text on the page should read away from the student, who looks east.
            root.transform.SetPositionAndRotation(NotebookSpot, Quaternion.LookRotation(Forward));

            var body = root.AddComponent<Rigidbody>();
            body.mass = 0.3f;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

            // Saved frozen: on a flat screen there is no hand to pick it up, so physics
            // could only make it fall off the table, and click-to-read still works. In a
            // headset ContextNotebook makes it a real object at runtime.
            body.isKinematic = true;
            body.useGravity = false;

            // Collider on the parent so the visual can be scaled freely.
            var box = root.AddComponent<BoxCollider>();
            box.size = new Vector3(0.20f, 0.025f, 0.27f);

            // Hands pick it up in a headset. It stays in the hand however it was
            // grabbed, so it can be turned to read, and it cannot be thrown across the
            // room: it should be set down, not launched.
            var grab = root.AddComponent<XRGrabInteractable>();
            grab.movementType = XRBaseInteractable.MovementType.VelocityTracking;
            grab.useDynamicAttach = true;
            grab.throwOnDetach = false;
            grab.trackScale = false;

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

            var hint = VrUi.Text(page, "Footer", Layout.BottomStretch(24, 16, 24, 30), "Click to read", 22,
                new Color(0.42f, 0.48f, 0.55f), TextAlignmentOptions.MidlineLeft);

            var notebook = root.AddComponent<ContextNotebook>();
            SetRef(notebook, "runner", runner);
            SetRef(notebook, "grab", grab);
            SetRef(notebook, "pageText", text);
            SetRef(notebook, "hint", hint);
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

using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CAIVR.EditorTools
{
    /// <summary>
    /// Lights the consultation room the way a real meeting room is lit: soft light from
    /// the ceiling panels, bouncing around the walls, with shadows under the table and
    /// chairs, instead of one hard point light that burns out the ceiling and leaves
    /// everything else black.
    ///
    /// It is baked into lightmaps, not computed live. That is what makes it soft enough
    /// to look real, and it costs a headset nothing at runtime, which matters because a
    /// Quest cannot afford to light a room with live lights.
    ///
    /// The people and the notebook move, so they cannot be baked. They pick the room's
    /// light up from light probes placed around the table.
    ///
    /// Everything here is applied to the generated scene only. The source room is never
    /// touched, apart from one import setting on its model (see <see cref="EnableLightmapUvs"/>).
    /// </summary>
    static class RoomLighting
    {
        const string SettingsPath = "Assets/CAIVR/Settings/ConsultationVR Lighting.lighting";
        public const string PendingBakeKey = "caivr.bake.pendingScene";

        // The room's two ceiling light panels, as offsets from the middle of the meeting room
        // (x across the table, z along it). Read off the model: they are two glowing squares
        // set into the ceiling, off to the professor's end of the room.
        static readonly Vector2[] PanelOffsets =
        {
            new Vector2(1.36f, 1.02f),
            new Vector2(1.36f, -0.66f),
        };

        const float PanelSize = 0.60f;

        // Warm white, around 4000K: office lighting.
        static readonly Color PanelColour = new Color(1f, 0.93f, 0.82f);
        const float PanelIntensity = 24f;

        // Light that has bounced in from elsewhere. A closed room with no windows would otherwise
        // be black wherever a panel's light does not reach directly.
        static readonly Color Ambient = new Color(0.64f, 0.64f, 0.66f);

        /// <summary>Sets the generated scene up for baking. Does not start the bake.</summary>
        public static bool Prepare(GameObject environment)
        {
            if (environment == null) return false;

            var walls = FindRenderer(environment, "Walls");
            if (walls == null)
            {
                Debug.LogWarning("[CAIVR] The room has no 'Walls' object, so its lighting cannot be set up. " +
                                 "The scene will use whatever lights the room came with.");
                return false;
            }

            EnableLightmapUvs();
            RemoveOriginalLights();
            MarkStatic(environment);

            var bounds = walls.bounds;
            var ceiling = bounds.max.y - 0.01f;

            for (var i = 0; i < PanelOffsets.Length; i++)
            {
                var position = new Vector3(bounds.center.x + PanelOffsets[i].x, ceiling, bounds.center.z + PanelOffsets[i].y);
                AddPanel($"Ceiling Light {i + 1}", position);
            }

            AddAmbient();
            AddLightProbes(bounds);
            AddReflectionProbe(bounds);
            AssignLightingSettings();

            return true;
        }

        /// <summary>Starts the bake. When it finishes the scene is saved so the lightmaps stay attached.</summary>
        public static void BakeAsync(Scene scene)
        {
            EditorPrefs.SetString(PendingBakeKey, scene.path);

            if (!Lightmapping.BakeAsync())
            {
                Debug.LogWarning("[CAIVR] The lighting bake could not be started.");
                EditorPrefs.DeleteKey(PendingBakeKey);
                return;
            }

            Debug.Log("[CAIVR] Baking the room's lighting. This takes a minute or two; the scene is saved when it is done.");
        }

        [MenuItem("CAIVR/VR/Bake Room Lighting")]
        static void BakeMenu() => BakeAsync(SceneManager.GetActiveScene());

        // --- steps -----------------------------------------------------------

        /// <summary>
        /// A lightmap needs a second set of UVs that does not overlap. The room's model has none,
        /// which is why the baked light that shipped with it never produced a lightmap. Unity can
        /// generate them on import, a change to the model's import settings only.
        /// </summary>
        static void EnableLightmapUvs()
        {
            const string path = "Assets/Meshes/Consultation_Env.fbx";
            if (AssetImporter.GetAtPath(path) is not ModelImporter importer) return;
            if (importer.generateSecondaryUV) return;

            importer.generateSecondaryUV = true;
            importer.SaveAndReimport();
            Debug.Log($"[CAIVR] Turned on lightmap UV generation for {path}.");
        }

        /// <summary>
        /// The room came with a point light that burns out the ceiling and a baked area light with no
        /// baked data, in the open-plan office next door. Both are replaced by the rig below.
        /// </summary>
        static void RemoveOriginalLights()
        {
            foreach (var light in Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
            {
                if (light.type == LightType.Directional) continue;
                Object.DestroyImmediate(light.gameObject);
            }
        }

        static void MarkStatic(GameObject root)
        {
            const StaticEditorFlags flags = StaticEditorFlags.ContributeGI | StaticEditorFlags.ReflectionProbeStatic
                                            | StaticEditorFlags.OccluderStatic | StaticEditorFlags.OccludeeStatic;

            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                GameObjectUtility.SetStaticEditorFlags(t.gameObject, flags);
        }

        static void AddPanel(string name, Vector3 position)
        {
            var go = new GameObject(name);
            go.transform.SetPositionAndRotation(position - Vector3.up * 0.02f, Quaternion.Euler(90f, 0f, 0f));   // faces down

            var light = go.AddComponent<Light>();
            light.type = LightType.Rectangle;
            light.areaSize = new Vector2(PanelSize, PanelSize);
            light.color = PanelColour;
            light.intensity = PanelIntensity;
            light.lightmapBakeType = LightmapBakeType.Baked;
            light.shadows = LightShadows.Soft;
        }

        static void AddAmbient()
        {
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = Ambient;
            RenderSettings.ambientIntensity = 1f;
        }

        /// <summary>A grid of probes around the table, where the people and the notebook are.</summary>
        static void AddLightProbes(Bounds room)
        {
            var go = new GameObject("Light Probes");
            go.transform.position = room.center;

            var group = go.AddComponent<LightProbeGroup>();

            var positions = new System.Collections.Generic.List<Vector3>();
            var halfX = room.extents.x - 0.35f;
            var halfZ = room.extents.z - 0.35f;

            foreach (var y in new[] { 0.35f, 1.05f, 1.75f })
                for (var ix = 0; ix < 4; ix++)
                    for (var iz = 0; iz < 4; iz++)
                    {
                        var x = Mathf.Lerp(-halfX, halfX, ix / 3f);
                        var z = Mathf.Lerp(-halfZ, halfZ, iz / 3f);
                        positions.Add(new Vector3(x, y - room.extents.y, z));      // local to the room's centre
                    }

            group.probePositions = positions.ToArray();
        }

        /// <summary>Glossy things (eyes, skin, the table) reflect the room around them, not the sky.</summary>
        static void AddReflectionProbe(Bounds room)
        {
            var go = new GameObject("Room Reflection Probe");
            go.transform.position = room.center;

            var probe = go.AddComponent<ReflectionProbe>();
            probe.mode = UnityEngine.Rendering.ReflectionProbeMode.Baked;
            probe.size = room.size;
            probe.boxProjection = true;
            probe.resolution = 128;
            probe.importance = 1;

            GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.ReflectionProbeStatic);
        }

        /// <summary>
        /// Our own bake settings, so the shared ones the room came with are not touched. GPU lightmapper
        /// with the denoiser, modest resolution (the room is small), non-directional lightmaps which are
        /// the cheap kind and enough for a diffuse interior.
        /// </summary>
        static void AssignLightingSettings()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));

            var settings = AssetDatabase.LoadAssetAtPath<LightingSettings>(SettingsPath);
            if (settings == null)
            {
                settings = new LightingSettings { name = "ConsultationVR Lighting" };
                AssetDatabase.CreateAsset(settings, SettingsPath);
            }

            settings.lightmapper = LightingSettings.Lightmapper.ProgressiveGPU;
            settings.bakedGI = true;
            settings.realtimeGI = false;
            settings.mixedBakeMode = MixedLightingMode.Shadowmask;

            settings.lightmapResolution = 48f;
            settings.lightmapMaxSize = 1024;
            settings.lightmapPadding = 4;

            settings.directSampleCount = 64;
            settings.indirectSampleCount = 512;
            settings.environmentSampleCount = 256;
            settings.maxBounces = 4;

            settings.ao = true;
            settings.aoMaxDistance = 0.8f;

            settings.denoiserTypeDirect = LightingSettings.DenoiserType.Optix;
            settings.denoiserTypeIndirect = LightingSettings.DenoiserType.Optix;
            settings.denoiserTypeAO = LightingSettings.DenoiserType.Optix;

            settings.directionalityMode = LightmapsMode.NonDirectional;

            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();

            Lightmapping.lightingSettings = settings;
        }

        static Renderer FindRenderer(GameObject root, string name)
        {
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                if (r.name == name) return r;
            return null;
        }
    }

    /// <summary>
    /// A bake runs in the background and the lightmaps it makes are only attached to a scene once
    /// the scene is saved. This does that, so nobody has to remember.
    /// </summary>
    [InitializeOnLoad]
    static class RoomLightingBakeWatcher
    {
        static RoomLightingBakeWatcher() => Lightmapping.bakeCompleted += OnBakeCompleted;

        static void OnBakeCompleted()
        {
            var pending = EditorPrefs.GetString(RoomLighting.PendingBakeKey, "");
            if (string.IsNullOrEmpty(pending)) return;

            EditorPrefs.DeleteKey(RoomLighting.PendingBakeKey);

            var scene = SceneManager.GetActiveScene();
            if (scene.path != pending) return;

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log("[CAIVR] Room lighting baked and saved.");
        }
    }
}

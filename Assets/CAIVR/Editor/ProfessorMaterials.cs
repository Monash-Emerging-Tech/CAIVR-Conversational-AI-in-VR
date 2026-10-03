using System.IO;
using UnityEditor;
using UnityEngine;

namespace CAIVR.EditorTools
{
    /// <summary>
    /// Builds the professor's materials from her own textures.
    ///
    /// Character Creator materials do not map one to one onto URP's Lit shader, so
    /// each part of her gets the closest honest setup: skin, eyes, teeth and clothes are
    /// plain opaque materials with their diffuse and normal maps; hair, lashes and brows
    /// are cut out by their alpha and drawn two-sided; the clear dome over each eye is a
    /// glossy highlight with no colour of its own.
    ///
    /// These are regenerated on every build, in place, so a change to the table below always
    /// takes effect without the materials changing identity.
    /// </summary>
    static class ProfessorMaterials
    {
        const string Folder = "Assets/CAIVR/Professor/Materials";

        enum Kind
        {
            /// <summary>Solid, with a diffuse and normal map.</summary>
            Opaque,
            /// <summary>Cut out by the diffuse's alpha, two-sided: hair, lashes, brows.</summary>
            Cutout,
            /// <summary>Alpha blended with the diffuse's alpha.</summary>
            Blended,
            /// <summary>Invisible except for its shine: the clear dome over the eye.</summary>
            Glass,
        }

        struct Recipe
        {
            public Kind Kind;
            public float Smoothness;
            public float Cutoff;
            public float NormalStrength;
            public string Diffuse;       // texture name, or null to use <material>_Diffuse
        }

        static Recipe R(Kind kind, float smoothness, float cutoff = 0.5f, float normal = 1f, string diffuse = null) =>
            new Recipe { Kind = kind, Smoothness = smoothness, Cutoff = cutoff, NormalStrength = normal, Diffuse = diffuse };

        /// <summary>How each of her materials is set up, by the name the model gives it.</summary>
        static Recipe RecipeFor(string material)
        {
            var n = material.ToLowerInvariant();

            if (n.Contains("cornea")) return R(Kind.Glass, 0.85f);
            if (n.Contains("eye_occlusion")) return R(Kind.Blended, 0f);        // not drawn, see Apply
            if (n.Contains("tearline")) return R(Kind.Blended, 1f);
            if (n.Contains("eyelash")) return R(Kind.Cutout, 0.15f, 0.40f);
            if (n.Contains("brow_base")) return R(Kind.Blended, 0.1f);
            if (n.Contains("brow")) return R(Kind.Cutout, 0.2f, 0.12f);
            if (n.Contains("scalp")) return R(Kind.Cutout, 0.18f, 0.18f);
            if (n.Contains("hair")) return R(Kind.Cutout, 0.22f, 0.35f);
            if (n.Contains("eye")) return R(Kind.Opaque, 0.55f);
            if (n.Contains("teeth")) return R(Kind.Opaque, 0.55f);
            if (n.Contains("tongue")) return R(Kind.Opaque, 0.55f);
            if (n.Contains("nails")) return R(Kind.Opaque, 0.65f);
            if (n.Contains("heel")) return R(Kind.Opaque, 0.45f);
            if (n.Contains("shirt")) return R(Kind.Opaque, 0.18f);
            if (n.Contains("skirt")) return R(Kind.Opaque, 0.15f);
            if (n.Contains("skin")) return R(Kind.Opaque, 0.32f);

            return R(Kind.Opaque, 0.3f);
        }

        /// <summary>
        /// Gives every skinned mesh under <paramref name="character"/> proper materials. Returns false
        /// if her textures could not be found.
        /// </summary>
        public static bool Apply(GameObject character)
        {
            if (!ProfessorTextures.Extract()) return false;

            Reset();

            foreach (var renderer in character.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var originals = renderer.sharedMaterials;
                var materials = new Material[originals.Length];

                for (var i = 0; i < originals.Length; i++)
                    materials[i] = originals[i] != null ? Build(originals[i].name) : null;

                renderer.sharedMaterials = materials;

                // Character Creator darkens the corners of the eye with a shader of its own that
                // reads the mesh's vertex colours. Lit has no equivalent, and without it the mesh is
                // a flat dark smear around each eye, so it is left out.
                if (renderer.name.Contains("EyeOcclusion")) renderer.enabled = false;

                // These shapes never need to cast a shadow of their own: they are skin-thin.
                if (renderer.name.Contains("TearLine") || renderer.name.Contains("Tear_Ducts")
                    || renderer.name.Contains("EyeOcclusion") || renderer.name.Contains("Brow"))
                    renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }

            AssetDatabase.SaveAssets();
            return true;
        }

        static void Reset()
        {
            Directory.CreateDirectory(Folder);
            AssetDatabase.Refresh();
        }

        static Material Build(string name)
        {
            var path = $"{Folder}/{name}.mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);

            var recipe = RecipeFor(name);

            var material = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = name };

            var diffuse = ProfessorTextures.Find(recipe.Diffuse ?? name + "_Diffuse");
            var normal = ProfessorTextures.Find(name + "_Normal");

            if (diffuse != null) material.SetTexture("_BaseMap", diffuse);
            material.SetColor("_BaseColor", Color.white);
            material.SetFloat("_Smoothness", recipe.Smoothness);
            material.SetFloat("_Metallic", 0f);

            if (normal != null)
            {
                material.SetTexture("_BumpMap", normal);
                material.SetFloat("_BumpScale", recipe.NormalStrength);
                material.EnableKeyword("_NORMALMAP");
            }

            // A mirror-smooth surface in a bright room reflects the whole sky. On the clear dome
            // over the eye that is a white sheet over the iris. Real eyes show small window-shaped
            // glints instead, so here only the lights shine on it, not the sky.
            if (name.ToLowerInvariant().Contains("eye") || name.ToLowerInvariant().Contains("cornea"))
            {
                material.SetFloat("_EnvironmentReflections", 0f);
                material.EnableKeyword("_ENVIRONMENTREFLECTIONS_OFF");
            }

            switch (recipe.Kind)
            {
                case Kind.Cutout: SetCutout(material, recipe.Cutoff); break;
                case Kind.Blended: SetBlended(material, premultiplied: false); break;
                case Kind.Glass: SetGlass(material); break;
            }

            // Updated in place when it already exists, so its identity (and everything in the scene
            // that points at it) stays the same from one build to the next.
            if (existing != null)
            {
                EditorUtility.CopySerialized(material, existing);
                Object.DestroyImmediate(material);
                EditorUtility.SetDirty(existing);
                return existing;
            }

            AssetDatabase.CreateAsset(material, path);
            return material;
        }

        // --- URP Lit surface modes -------------------------------------------
        // Set the way the inspector would, so the material behaves identically if someone
        // opens it and flips a switch.

        static void SetCutout(Material m, float cutoff)
        {
            m.SetFloat("_AlphaClip", 1f);
            m.SetFloat("_Cutoff", cutoff);
            m.EnableKeyword("_ALPHATEST_ON");

            m.SetFloat("_Cull", 0f);                 // two-sided: a strand is seen from both faces
            if (m.HasProperty("_AlphaToMask")) m.SetFloat("_AlphaToMask", 1f);   // soft edges when MSAA is on

            m.SetOverrideTag("RenderType", "TransparentCutout");
            m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;
        }

        static void SetBlended(Material m, bool premultiplied)
        {
            m.SetFloat("_Surface", 1f);
            m.SetFloat("_Blend", premultiplied ? 1f : 0f);
            m.SetFloat("_SrcBlend", premultiplied ? 1f : 5f);
            m.SetFloat("_DstBlend", 10f);
            m.SetFloat("_SrcBlendAlpha", 1f);
            m.SetFloat("_DstBlendAlpha", 10f);
            m.SetFloat("_ZWrite", 0f);
            m.SetFloat("_Cull", 0f);

            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            if (premultiplied) m.EnableKeyword("_ALPHAPREMULTIPLY_ON");
            else m.DisableKeyword("_ALPHAPREMULTIPLY_ON");

            m.SetOverrideTag("RenderType", "Transparent");
            m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        }

        /// <summary>
        /// The clear dome over the eye. Character Creator draws it as a wet shine over the iris.
        /// A premultiplied-alpha version of that is easy to get subtly wrong (a mismatch between
        /// the blend factors and the shader keyword adds a solid white sheet over the whole eye,
        /// which is exactly what it did), so it is a plain alpha blend faded to nothing: the
        /// eye underneath does the work.
        /// </summary>
        static void SetGlass(Material m)
        {
            SetBlended(m, premultiplied: false);
            m.SetColor("_BaseColor", new Color(1f, 1f, 1f, 0f));
            m.SetTexture("_BaseMap", null);
            m.SetFloat("_Cull", 2f);
        }
    }
}

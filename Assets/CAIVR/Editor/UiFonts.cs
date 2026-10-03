using System.Collections.Generic;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

namespace CAIVR.EditorTools
{
    /// <summary>
    /// Sets up the fonts the interface draws with, so a fresh clone needs nothing done by hand.
    ///
    /// Inter, the interface font, has no Chinese in it. Noto Sans SC does (open licence, see
    /// Assets/CAIVR/Fonts/OFL.txt; the file is trimmed to the common Simplified Chinese characters plus the
    /// game's own text so it stays small). It is added as a FALLBACK of Inter: any text set in Inter that
    /// needs a character Inter lacks borrows it from Noto, with no per-label work and no change to English.
    ///
    /// There are two copies of each, because UI that is drawn over the room (the menu, the card, captions,
    /// hints) needs TextMeshPro's depth-ignoring "Overlay" shader, and a fallback font draws with its own
    /// material, not the parent's. Everything that is part of the room (the notebook page) keeps the normal
    /// pair.
    ///
    ///     Inter SDF           ->  NotoSansSC SDF            (in the room)
    ///     Inter SDF Overlay   ->  NotoSansSC SDF Overlay    (over the room)
    /// </summary>
    static class UiFonts
    {
        const string Folder = "Assets/CAIVR/Resources/CAIVR/Fonts";
        const string SourceFont = "Assets/CAIVR/Fonts/NotoSansSC-Regular-Subset.otf";

        const string Inter = "Inter SDF";
        const string Noto = "NotoSansSC SDF";
        const string OverlaySuffix = " Overlay";

        [MenuItem("CAIVR/UI/Set Up Fonts (Chinese and on-top)")]
        public static void Ensure()
        {
            var interPath = $"{Folder}/{Inter}.asset";
            var inter = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(interPath);
            if (inter == null)
            {
                Debug.LogWarning("[CAIVR] The Inter font is not in Resources yet; fonts will be set up on the next build of the scene.");
                return;
            }

            var noto = EnsureNoto();
            MatchLineMetrics(noto, inter);
            Link(inter, noto);

            var interOverlay = EnsureOverlayCopy(Inter);
            var notoOverlay = noto != null ? EnsureOverlayCopy(Noto) : null;
            MatchLineMetrics(notoOverlay, inter);
            Link(interOverlay, notoOverlay);

            AssetDatabase.SaveAssets();
        }

        // --- the Chinese font ------------------------------------------------

        static TMP_FontAsset EnsureNoto()
        {
            var path = $"{Folder}/{Noto}.asset";
            var existing = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(path);

            var source = AssetDatabase.LoadAssetAtPath<Font>(SourceFont);
            if (source == null)
            {
                Debug.LogWarning($"[CAIVR] {SourceFont} is missing, so there will be no Chinese text.");
                return existing;
            }

            if (existing == null)
            {
                // Dynamic: any character the font has can be drawn on demand, which an AI-translated line needs.
                // Small pages that grow as needed, and nothing drawn into them here: a pre-filled atlas for a few
                // hundred Chinese characters is tens of megabytes of asset. UiFontWarmup fills it as the scene loads.
                existing = TMP_FontAsset.CreateFontAsset(source, 64, 6, GlyphRenderMode.SDFAA, 1024, 1024,
                    AtlasPopulationMode.Dynamic, true);
                existing.name = Noto;

                AssetDatabase.CreateAsset(existing, path);

                existing.material.name = Noto + " Material";
                AssetDatabase.AddObjectToAsset(existing.material, existing);

                for (var i = 0; i < existing.atlasTextures.Length; i++)
                {
                    existing.atlasTextures[i].name = $"{Noto} Atlas {i}";
                    AssetDatabase.AddObjectToAsset(existing.atlasTextures[i], existing);
                }
            }

            // Keep the asset as light as it was made: whatever a previous setup drew into it is dropped.
            if (existing.characterTable != null && existing.characterTable.Count > 0)
            {
                existing.ClearFontAssetData(true);
                EditorUtility.SetDirty(existing);
            }

            return existing;
        }

        // --- the on-top copies ----------------------------------------------

        static TMP_FontAsset EnsureOverlayCopy(string baseName)
        {
            var source = $"{Folder}/{baseName}.asset";
            var copy = $"{Folder}/{baseName}{OverlaySuffix}.asset";

            if (AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(copy) == null)
            {
                if (!AssetDatabase.CopyAsset(source, copy))
                {
                    Debug.LogWarning($"[CAIVR] Could not make the on-top copy of {baseName}.");
                    return null;
                }
            }

            var asset = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(copy);
            if (asset == null) return null;

            // Same atlas, same look; only the shader differs, and it ignores depth.
            var overlay = Shader.Find("TextMeshPro/Distance Field Overlay");
            if (overlay != null && asset.material != null && asset.material.shader != overlay)
            {
                asset.material.shader = overlay;
                EditorUtility.SetDirty(asset.material);
            }

            if (asset.atlasPopulationMode == AtlasPopulationMode.Dynamic && asset.characterTable != null && asset.characterTable.Count > 0)
            {
                asset.ClearFontAssetData(true);
                EditorUtility.SetDirty(asset);
            }

            return asset;
        }

        /// <summary>
        /// Gives the Chinese font the same line spacing, in proportion, as the interface font.
        ///
        /// Noto's lines are about 20% taller than Inter's. TextMeshPro takes a line's height from the tallest font on
        /// it, so one Chinese character would make a label's line taller than its box, and a label that cannot fit
        /// even one line draws nothing at all. Chinese characters sit comfortably within the tighter spacing.
        /// </summary>
        static void MatchLineMetrics(TMP_FontAsset font, TMP_FontAsset reference)
        {
            if (font == null || reference == null) return;

            var scale = (float)font.faceInfo.pointSize / reference.faceInfo.pointSize;

            var so = new SerializedObject(font);
            Set(so, "m_FaceInfo.m_LineHeight", reference.faceInfo.lineHeight * scale);
            Set(so, "m_FaceInfo.m_AscentLine", reference.faceInfo.ascentLine * scale);
            Set(so, "m_FaceInfo.m_DescentLine", reference.faceInfo.descentLine * scale);

            if (so.ApplyModifiedPropertiesWithoutUndo()) EditorUtility.SetDirty(font);
        }

        static void Set(SerializedObject so, string path, float value)
        {
            var property = so.FindProperty(path);
            if (property != null) property.floatValue = value;
        }

        static void Link(TMP_FontAsset font, TMP_FontAsset fallback)
        {
            if (font == null || fallback == null) return;

            font.fallbackFontAssetTable ??= new List<TMP_FontAsset>();
            font.fallbackFontAssetTable.RemoveAll(f => f == null);      // a font that was deleted and remade
            if (font.fallbackFontAssetTable.Contains(fallback)) return;

            font.fallbackFontAssetTable.Add(fallback);
            EditorUtility.SetDirty(font);
        }
    }
}

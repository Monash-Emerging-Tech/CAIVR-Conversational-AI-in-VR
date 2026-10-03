using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace CAIVR.EditorTools
{
    /// <summary>
    /// Gets the professor's own textures out of her model file.
    ///
    /// Character Creator packs every texture inside the FBX. Unity's importer does not
    /// surface them for this file, so the model arrives with all-white materials, which
    /// is what made her look like a mannequin. The images are all still in there, byte
    /// for byte as they were exported, so this reads them back out.
    ///
    /// An FBX stores an embedded image as a raw byte property: a type marker 'R', a
    /// four byte length, then the file itself. Each image's name sits a short way
    /// before it. That is all that is needed; no FBX library required.
    /// </summary>
    public static class ProfessorTextures
    {
        public const string Folder = "Assets/CAIVR/Professor/Textures";

        static readonly byte[] PngMagic = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        static readonly Regex FileName = new Regex(@"([A-Za-z0-9_\-]+\.(?:png|jpg|jpeg))", RegexOptions.IgnoreCase);

        /// <summary>How many bytes before an image to look for its file name.</summary>
        const int NameWindow = 3000;

        /// <summary>
        /// Reads the images out of the model into <see cref="Folder"/> and imports them.
        /// Does nothing if they are already there, unless <paramref name="force"/> is set.
        /// </summary>
        public static bool Extract(bool force = false)
        {
            if (!force && Directory.Exists(Folder) && Directory.GetFiles(Folder, "*.*").Length > 20)
                return true;

            var source = ProfessorBuilder.CharacterPath;
            if (!File.Exists(source))
            {
                Debug.LogError($"[CAIVR] The professor model is missing at {source}.");
                return false;
            }

            var bytes = File.ReadAllBytes(source);
            Directory.CreateDirectory(Folder);

            var written = 0;
            var used = new System.Collections.Generic.HashSet<string>();

            for (var i = 5; i < bytes.Length - 8; i++)
            {
                if (!IsImageStart(bytes, i)) continue;

                // The raw-property header: 'R' then the data length, just before the data.
                if (bytes[i - 5] != (byte)'R') continue;

                var length = BitConverter.ToUInt32(bytes, i - 4);
                if (length < 32 || i + length > bytes.Length) continue;

                var name = NameBefore(bytes, i);
                if (name == null) continue;

                // The same file name can be embedded twice; keep both rather than lose one.
                var unique = name;
                var n = 1;
                while (!used.Add(unique))
                    unique = $"{Path.GetFileNameWithoutExtension(name)}_{n++}{Path.GetExtension(name)}";

                var data = new byte[length];
                Buffer.BlockCopy(bytes, i, data, 0, (int)length);
                File.WriteAllBytes($"{Folder}/{unique}", data);
                written++;

                i += (int)length - 1;
            }

            if (written == 0)
            {
                Debug.LogWarning("[CAIVR] No embedded textures were found in the professor model.");
                return false;
            }

            AssetDatabase.Refresh();
            ConfigureImports();

            Debug.Log($"[CAIVR] Extracted {written} professor textures into {Folder}.");
            return true;
        }

        [MenuItem("CAIVR/Professor/Extract Textures From Model")]
        static void ExtractMenu() => Extract(force: true);

        static bool IsImageStart(byte[] b, int i)
        {
            if (b[i] == 0x89)
            {
                for (var k = 0; k < PngMagic.Length; k++)
                    if (b[i + k] != PngMagic[k]) return false;
                return true;
            }

            // JPEG: FF D8 FF, then a marker that starts a real image (JFIF, Exif, or a table).
            return b[i] == 0xFF && b[i + 1] == 0xD8 && b[i + 2] == 0xFF
                   && (b[i + 3] == 0xE0 || b[i + 3] == 0xE1 || b[i + 3] == 0xDB);
        }

        static string NameBefore(byte[] b, int index)
        {
            var start = Math.Max(0, index - NameWindow);
            var text = Encoding.GetEncoding("iso-8859-1").GetString(b, start, index - start);

            string last = null;
            foreach (Match m in FileName.Matches(text)) last = m.Value;
            return last;
        }

        /// <summary>Normal maps as normal maps, cut-out images as transparent, everything capped at 2K.</summary>
        static void ConfigureImports()
        {
            foreach (var path in Directory.GetFiles(Folder))
            {
                if (path.EndsWith(".meta")) continue;

                var assetPath = path.Replace('\\', '/');
                var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
                if (importer == null) continue;

                var name = Path.GetFileName(assetPath);

                importer.textureType = name.Contains("_Normal")
                    ? TextureImporterType.NormalMap
                    : TextureImporterType.Default;

                // Strand textures (hair, brows, lashes) are cut out by their alpha.
                var cutout = name.Contains("Hair_") || name.Contains("Scalp_") || name.Contains("Brow")
                             || name.Contains("Eyelash") || name.Contains("Tearline") || name.Contains("Opacity");
                importer.alphaSource = cutout ? TextureImporterAlphaSource.FromInput : TextureImporterAlphaSource.None;
                importer.alphaIsTransparency = cutout;

                importer.mipmapEnabled = true;
                importer.maxTextureSize = 2048;       // the hair is 4096; 2048 is plenty at conversational distance and far kinder to a Quest
                importer.SaveAndReimport();
            }
        }

        /// <summary>A texture from <see cref="Folder"/> by file name without its extension, or null.</summary>
        public static Texture2D Find(string nameWithoutExtension)
        {
            foreach (var guid in AssetDatabase.FindAssets(nameWithoutExtension + " t:Texture2D", new[] { Folder }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileNameWithoutExtension(path) == nameWithoutExtension)
                    return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            }

            return null;
        }
    }
}

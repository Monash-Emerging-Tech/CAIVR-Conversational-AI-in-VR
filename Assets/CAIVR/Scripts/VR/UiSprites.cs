using UnityEngine;

namespace CAIVR.VR
{
    /// <summary>
    /// Generates the few shapes a modern UI is built from, in code.
    ///
    /// Unity's UI has no rounded corners of its own, and rounded corners are most
    /// of the difference between a panel that looks like a debug overlay and one
    /// that looks designed. Rather than ship image files that must be kept in
    /// sync, one smooth rounded rectangle is drawn here and stretched everywhere
    /// as a 9-slice, tinted by colour.
    /// </summary>
    public static class UiSprites
    {
        // Master corner radius in texels. Everything else scales this down with
        // Image.pixelsPerUnitMultiplier, so one texture serves every radius.
        public const int MasterRadius = 48;

        static Sprite _rounded;
        static Sprite _fadeDown;

        /// <summary>A white rounded rectangle, 9-sliced. Tint it with Image.color.</summary>
        public static Sprite Rounded
        {
            get
            {
                if (_rounded == null) _rounded = BuildRounded();
                return _rounded;
            }
        }

        /// <summary>White at the top fading to transparent at the bottom, for sheens and glows.</summary>
        public static Sprite FadeDown
        {
            get
            {
                if (_fadeDown == null) _fadeDown = BuildFadeDown();
                return _fadeDown;
            }
        }

        static Sprite BuildRounded()
        {
            var size = MasterRadius * 2 + 2;     // corners plus a 2 texel stretchable middle
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "CAIVR_RoundedRect",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave,
            };

            var pixels = new Color32[size * size];
            var half = size * 0.5f;
            var inner = half - MasterRadius;

            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    // Signed distance to a rounded box, then one texel of
                    // smoothing so the edge is antialiased rather than stair-stepped.
                    var qx = Mathf.Abs(x + 0.5f - half) - inner;
                    var qy = Mathf.Abs(y + 0.5f - half) - inner;
                    var outside = new Vector2(Mathf.Max(qx, 0f), Mathf.Max(qy, 0f)).magnitude;
                    var distance = outside + Mathf.Min(Mathf.Max(qx, qy), 0f) - MasterRadius;

                    var alpha = Mathf.Clamp01(0.5f - distance);
                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(alpha * 255f));
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, true);

            var border = new Vector4(MasterRadius, MasterRadius, MasterRadius, MasterRadius);
            return Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f),
                100f, 0, SpriteMeshType.FullRect, border);
        }

        static Sprite BuildFadeDown()
        {
            const int height = 64;
            var texture = new Texture2D(1, height, TextureFormat.RGBA32, false)
            {
                name = "CAIVR_FadeDown",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave,
            };

            for (var y = 0; y < height; y++)
            {
                // Row 0 is the bottom of a texture, so alpha rises with y.
                var t = y / (height - 1f);
                texture.SetPixel(0, y, new Color(1f, 1f, 1f, t * t));
            }

            texture.Apply(false, true);
            return Sprite.Create(texture, new Rect(0, 0, 1, height), new Vector2(0.5f, 0.5f), 100f);
        }
    }
}

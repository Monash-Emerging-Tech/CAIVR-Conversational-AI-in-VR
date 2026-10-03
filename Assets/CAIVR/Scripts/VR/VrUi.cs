using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace CAIVR.VR
{
    public enum ButtonStyle
    {
        /// <summary>Solid Monash blue. The one thing on a panel you are meant to press.</summary>
        Primary,

        /// <summary>A faint translucent fill. Secondary actions on a card.</summary>
        Ghost,

        /// <summary>Dark navy glass. Controls that sit directly over the 3D scene.</summary>
        Glass,
    }

    /// <summary>
    /// Where a UI element sits inside its parent, described the way a designer
    /// would: pixels in from an edge. Saves every panel from anchor arithmetic.
    /// </summary>
    public readonly struct Layout
    {
        public readonly Vector2 AnchorMin;
        public readonly Vector2 AnchorMax;
        public readonly Vector2 OffsetMin;
        public readonly Vector2 OffsetMax;

        Layout(Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax)
        {
            AnchorMin = anchorMin;
            AnchorMax = anchorMax;
            OffsetMin = offsetMin;
            OffsetMax = offsetMax;
        }

        /// <summary>Height in pixels when the element has a fixed height, else NaN.</summary>
        public float Height => AnchorMin.y == AnchorMax.y ? OffsetMax.y - OffsetMin.y : float.NaN;

        public float Width => AnchorMin.x == AnchorMax.x ? OffsetMax.x - OffsetMin.x : float.NaN;

        /// <summary>x and y measured in from the top-left corner.</summary>
        public static Layout TopLeft(float x, float y, float w, float h) =>
            new Layout(new Vector2(0, 1), new Vector2(0, 1), new Vector2(x, -(y + h)), new Vector2(x + w, -y));

        public static Layout TopRight(float right, float y, float w, float h) =>
            new Layout(new Vector2(1, 1), new Vector2(1, 1), new Vector2(-(right + w), -(y + h)), new Vector2(-right, -y));

        public static Layout BottomLeft(float x, float bottom, float w, float h) =>
            new Layout(new Vector2(0, 0), new Vector2(0, 0), new Vector2(x, bottom), new Vector2(x + w, bottom + h));

        public static Layout BottomRight(float right, float bottom, float w, float h) =>
            new Layout(new Vector2(1, 0), new Vector2(1, 0), new Vector2(-(right + w), bottom), new Vector2(-right, bottom + h));

        public static Layout BottomCenter(float w, float h, float bottom) =>
            new Layout(new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(-w / 2f, bottom), new Vector2(w / 2f, bottom + h));

        public static Layout Center(float w, float h, float dy = 0f) =>
            new Layout(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(-w / 2f, dy - h / 2f), new Vector2(w / 2f, dy + h / 2f));

        /// <summary>Full width, fixed height, margins left and right, measured down from the top.</summary>
        public static Layout TopStretch(float left, float top, float right, float h) =>
            new Layout(new Vector2(0, 1), new Vector2(1, 1), new Vector2(left, -(top + h)), new Vector2(-right, -top));

        public static Layout BottomStretch(float left, float bottom, float right, float h) =>
            new Layout(new Vector2(0, 0), new Vector2(1, 0), new Vector2(left, bottom), new Vector2(-right, bottom + h));

        public static Layout Fill(float left = 0, float top = 0, float right = 0, float bottom = 0) =>
            new Layout(Vector2.zero, Vector2.one, new Vector2(left, bottom), new Vector2(-right, -top));

        public void ApplyTo(RectTransform rect)
        {
            rect.anchorMin = AnchorMin;
            rect.anchorMax = AnchorMax;
            rect.offsetMin = OffsetMin;
            rect.offsetMax = OffsetMax;
        }
    }

    /// <summary>
    /// Builds the CAIVR look in code: dark navy glass cards, Monash blue accents,
    /// rounded everything, Inter type.
    ///
    /// All of it is generated at runtime rather than saved as prefabs, for the
    /// same reason as the rest of the UI: scenes and prefabs are what four people
    /// end up conflicting on, and a generated panel is one C# file to review.
    ///
    /// Two kinds of canvas:
    ///   - world canvases, for things that are objects in the room (the menu, the
    ///     notebook). Authored in pixels, scaled so 1000 px is one metre.
    ///   - screen canvases, for things that belong to the viewer rather than the
    ///     room (subtitles, the briefing card). They sit on the camera, so they
    ///     are always readable and can never clip into a wall.
    /// </summary>
    public static class VrUi
    {
        const string FontResource = "CAIVR/Fonts/Inter SDF";
        const string BezelResource = "CAIVR/Materials/UiBezel";

        static TMP_FontAsset _font;
        static bool _fontLooked;

        /// <summary>Inter if it has been set up, otherwise TextMeshPro's default.</summary>
        public static TMP_FontAsset Font
        {
            get
            {
                if (!_fontLooked)
                {
                    _font = Resources.Load<TMP_FontAsset>(FontResource);
                    _fontLooked = true;
                }
                return _font;
            }
        }

        // --- canvases --------------------------------------------------------

        /// <summary>
        /// Creates a world-space canvas, <paramref name="widthMeters"/> wide. At
        /// 1000 pixels per metre one pixel is one millimetre, so a 56 px font is a
        /// 5.6 cm letter, which makes legibility easy to reason about.
        /// </summary>
        public static Canvas CreateWorldCanvas(string name, Transform parent, Vector2 pixelSize, float widthMeters)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Canvas), typeof(GraphicRaycaster));
            go.transform.SetParent(parent, false);

            var canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;

            var rect = go.GetComponent<RectTransform>();
            rect.sizeDelta = pixelSize;
            rect.localScale = Vector3.one * (widthMeters / pixelSize.x);

            // The standard GraphicRaycaster serves the mouse. The tracked-device
            // one serves XR rays and pokes, and sits idle until a rig is added.
            go.AddComponent<TrackedDeviceGraphicRaycaster>();

            return canvas;
        }

        /// <summary>
        /// Creates a canvas that is laid over the camera's view, sized to 1920 x 1080
        /// reference pixels and scaled to fit. This is what subtitles are: they belong
        /// to the viewer, not to the room, so they stay put wherever the student looks.
        ///
        /// It is drawn a short distance in front of the camera, so geometry would have
        /// to be closer than that to hide it. Unlike a floating world panel it cannot
        /// end up inside a wall.
        /// </summary>
        public static Canvas CreateScreenCanvas(string name, Transform parent, Camera camera,
                                                int sortingOrder, float planeDistance = 0.4f)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            go.transform.SetParent(parent, false);

            var canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = camera;
            canvas.planeDistance = planeDistance;
            canvas.sortingOrder = sortingOrder;

            var scaler = go.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            return canvas;
        }

        public static void EnsureEventCamera(Canvas canvas)
        {
            if (canvas != null && canvas.worldCamera == null) canvas.worldCamera = Camera.main;
        }

        // --- surfaces --------------------------------------------------------

        public static RectTransform Group(Transform parent, string name, Layout layout)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var rect = go.GetComponent<RectTransform>();
            layout.ApplyTo(rect);
            return rect;
        }

        /// <summary>A flat rounded rectangle.</summary>
        public static Image Surface(Transform parent, string name, Layout layout, Color color,
                                    float radius, bool blocksRaycasts = false)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);

            layout.ApplyTo(go.GetComponent<RectTransform>());

            var image = go.GetComponent<Image>();
            image.sprite = UiSprites.Rounded;
            image.type = Image.Type.Sliced;
            image.pixelsPerUnitMultiplier = UiSprites.MasterRadius / Mathf.Max(1f, radius);
            image.color = color;
            image.raycastTarget = blocksRaycasts;
            return image;
        }

        /// <summary>
        /// A raised glass card: soft shadow, hairline border, dark face.
        /// Returns the face; put content inside it.
        /// </summary>
        public static Image Card(Transform parent, string name, Layout layout, float radius = 36f,
                                 bool shadow = true, bool blocksRaycasts = true)
        {
            var root = Group(parent, name, layout);

            if (shadow)
            {
                // Stacked, widening, fainter layers read as a blur without a blur shader.
                Shadow(root, 6f, 0.20f, radius);
                Shadow(root, 16f, 0.12f, radius + 8f);
                Shadow(root, 30f, 0.07f, radius + 16f);
            }

            Surface(root, "Border", Layout.Fill(), MonashTheme.Border, radius);
            return Surface(root, "Face", Layout.Fill(2, 2, 2, 2), MonashTheme.Surface, radius - 2f, blocksRaycasts);
        }

        static void Shadow(Transform parent, float spread, float alpha, float radius)
        {
            // Dropped down and slightly wider than the card, as if lit from above.
            var layout = Layout.Fill(-spread, -spread + 14f, -spread, -spread - 14f);
            Surface(parent, "Shadow", layout, new Color(0f, 0.02f, 0.06f, alpha), radius);
        }

        // --- text ------------------------------------------------------------

        public static TextMeshProUGUI Text(Transform parent, string name, Layout layout, string text,
                                           float size, Color color, TextAlignmentOptions align,
                                           float tracking = 0f, bool caps = false, bool autoSize = false)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
            go.transform.SetParent(parent, false);

            layout.ApplyTo(go.GetComponent<RectTransform>());

            var label = go.GetComponent<TextMeshProUGUI>();
            if (Font != null) label.font = Font;

            label.text = text;
            label.fontSize = size;
            label.color = color;
            label.alignment = align;
            label.characterSpacing = tracking;
            label.richText = true;
            label.raycastTarget = false;      // text must never swallow a click meant for a button
            label.textWrappingMode = TextWrappingModes.Normal;
            label.overflowMode = TextOverflowModes.Ellipsis;

            if (caps) label.fontStyle |= FontStyles.UpperCase;

            if (autoSize)
            {
                label.enableAutoSizing = true;
                label.fontSizeMax = size;
                label.fontSizeMin = size * 0.55f;
            }

            return label;
        }

        /// <summary>Small spaced capitals, used above content as a quiet label.</summary>
        public static TextMeshProUGUI Eyebrow(Transform parent, string name, Layout layout, string text,
                                              Color? color = null)
        {
            return Text(parent, name, layout, text, 24f, color ?? MonashTheme.BlueLight,
                TextAlignmentOptions.MidlineLeft, tracking: 6f, caps: true);
        }

        // --- controls --------------------------------------------------------

        public static Button PillButton(Transform parent, string name, string label, Layout layout,
                                        UnityAction onClick, ButtonStyle style = ButtonStyle.Primary,
                                        float fontSize = 36f)
        {
            var height = float.IsNaN(layout.Height) ? 80f : layout.Height;

            Color fill;
            switch (style)
            {
                case ButtonStyle.Ghost: fill = MonashTheme.Border; break;
                case ButtonStyle.Glass: fill = MonashTheme.Glass; break;
                default: fill = MonashTheme.Blue; break;
            }

            var face = Surface(parent, name, layout, fill, Mathf.Min(UiSprites.MasterRadius, height / 2f),
                blocksRaycasts: true);

            var button = face.gameObject.AddComponent<Button>();
            button.targetGraphic = face;
            button.transition = Selectable.Transition.None;     // VrHoverLift drives colour and depth

            if (onClick != null) button.onClick.AddListener(onClick);

            Text(face.transform, "Label", Layout.Fill(), label, fontSize, MonashTheme.Text,
                TextAlignmentOptions.Center, tracking: 1f);

            face.gameObject.AddComponent<VrHoverLift>();
            return button;
        }

        public static void SetButtonLabel(Button button, string label)
        {
            var text = button != null ? button.GetComponentInChildren<TextMeshProUGUI>() : null;
            if (text != null) text.text = label;
        }

        /// <summary>
        /// A wide, faintly raised row that is itself the button, for settings lists.
        /// Put labels and values inside it; clicking anywhere on the row fires onClick.
        /// </summary>
        public static Button Row(Transform parent, string name, Layout layout, UnityAction onClick,
                                 float radius = 28f)
        {
            var face = Surface(parent, name, layout, MonashTheme.Raised, radius, blocksRaycasts: true);

            var button = face.gameObject.AddComponent<Button>();
            button.targetGraphic = face;
            button.transition = Selectable.Transition.None;

            if (onClick != null) button.onClick.AddListener(onClick);

            face.gameObject.AddComponent<VrHoverLift>().Configure(6f, 1.01f);
            return button;
        }

        public static VrSwitch Switch(Transform parent, string name, Layout layout, bool on)
        {
            var track = Surface(parent, name, layout, MonashTheme.SwitchOff, 32f, blocksRaycasts: true);
            var knob = Surface(track.transform, "Knob", Layout.TopLeft(6, 6, 52, 52), Color.white, 26f);

            var control = track.gameObject.AddComponent<VrSwitch>();
            control.Initialise(track, knob.rectTransform, on);
            return control;
        }

        /// <summary>A small coloured circle, used for status.</summary>
        public static Image Dot(Transform parent, string name, Layout layout, Color color)
        {
            var size = Mathf.Max(2f, layout.Height);
            return Surface(parent, name, layout, color, size / 2f);
        }

        /// <summary>
        /// A rounded pill holding a status dot and a label. Pass a background when it
        /// sits over the 3D scene rather than on a card, where the faint default
        /// would vanish.
        /// </summary>
        public static void Chip(Transform parent, string name, Layout layout, out Image dot,
                                out TextMeshProUGUI label, float fontSize = 28f, Color? background = null)
        {
            var height = float.IsNaN(layout.Height) ? 56f : layout.Height;

            var pill = Surface(parent, name, layout, background ?? MonashTheme.Raised, height / 2f);

            dot = Dot(pill.transform, "Dot", Layout.TopLeft(height * 0.38f, height * 0.36f, height * 0.28f, height * 0.28f),
                MonashTheme.Success);

            label = Text(pill.transform, "Label", Layout.Fill(left: height * 0.38f + height * 0.28f + 14f, right: height * 0.3f),
                "", fontSize, MonashTheme.Text, TextAlignmentOptions.MidlineLeft);
        }

        // --- physical frame --------------------------------------------------

        /// <summary>
        /// A thin slab behind a card so a floating panel reads as an object with
        /// thickness rather than a sticker. Does nothing if the material asset has
        /// not been generated yet.
        /// </summary>
        public static void AddBezel(Canvas canvas, Vector2 cardPixels, Vector2 centerPixels,
                                    float depthPixels = 16f, float marginPixels = 14f)
        {
            var material = Resources.Load<Material>(BezelResource);
            if (material == null) return;

            var slab = GameObject.CreatePrimitive(PrimitiveType.Cube);
            slab.name = "Bezel";
            slab.transform.SetParent(canvas.transform, false);

            var collider = slab.GetComponent<Collider>();
            if (collider != null) Object.Destroy(collider);

            slab.GetComponent<Renderer>().sharedMaterial = material;

            // The canvas is scaled to 1 px = 1 mm, so these sizes are in millimetres.
            slab.transform.localScale = new Vector3(cardPixels.x + marginPixels * 2f, cardPixels.y + marginPixels * 2f, depthPixels);
            slab.transform.localPosition = new Vector3(centerPixels.x, centerPixels.y, depthPixels * 0.5f + 2f);
        }
    }
}

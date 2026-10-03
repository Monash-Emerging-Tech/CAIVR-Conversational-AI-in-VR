using UnityEngine;

namespace CAIVR.VR
{
    /// <summary>
    /// Colours for the Monash-themed UI the stakeholder asked for.
    ///
    /// These are an approximation of Monash blue, picked by eye. Confirm them
    /// against the official brand guidelines before the cohort demo and change
    /// them here; everything built through <see cref="VrUi"/> picks them up.
    /// </summary>
    public static class MonashTheme
    {
        /// <summary>Primary brand blue, used for headers and primary buttons.</summary>
        public static readonly Color Blue = Hex("006DAE");

        /// <summary>Pressed states and borders.</summary>
        public static readonly Color BlueDark = Hex("004C7A");

        /// <summary>Panel backgrounds. Dark enough that white text reads comfortably.</summary>
        public static readonly Color BlueDeep = Hex("0A2540");

        /// <summary>Hover and highlight.</summary>
        public static readonly Color BlueLight = Hex("5FB3E3");

        public static readonly Color Text = Hex("FFFFFF");

        /// <summary>Secondary text. Kept light so it stays legible against the deep blue.</summary>
        public static readonly Color TextDim = Hex("A8C4DB");

        public static readonly Color Warning = Hex("FFB347");

        /// <summary>Good: microphone ready, AI connected.</summary>
        public static readonly Color Success = Hex("3DDC97");

        /// <summary>Bad: something is offline.</summary>
        public static readonly Color Danger = Hex("FF6B6B");

        /// <summary>Card face. Deep navy, nearly opaque so the room does not show through.</summary>
        public static readonly Color Surface = new Color(0.039f, 0.145f, 0.251f, 0.985f);

        /// <summary>
        /// Navy used for controls that sit directly over the 3D scene (the on-screen
        /// subtitle bar and its buttons). Needs enough opacity to stay readable
        /// against a bright wall, but a little give so it does not feel like a slab.
        /// </summary>
        public static readonly Color Glass = new Color(0.039f, 0.145f, 0.251f, 0.90f);

        /// <summary>Behind subtitle text: dark enough for white text on any background.</summary>
        public static readonly Color SubtitleBackdrop = new Color(0f, 0f, 0f, 0.55f);

        // NOTE on the translucent whites below: this project renders in LINEAR colour
        // space, where blending a little white over a dark colour lifts it far more than
        // it would in sRGB. An alpha that looks right in a design tool (0.08) comes out
        // as washed-out grey here, so these are deliberately tiny.

        /// <summary>A step lighter than a card, for rows and chips sitting on one.</summary>
        public static readonly Color Raised = new Color(1f, 1f, 1f, 0.022f);

        /// <summary>Hairlines, dividers and meter tracks.</summary>
        public static readonly Color Border = new Color(1f, 1f, 1f, 0.07f);

        /// <summary>The off position of a switch track.</summary>
        public static readonly Color SwitchOff = new Color(1f, 1f, 1f, 0.16f);

        static Color Hex(string rgb)
        {
            ColorUtility.TryParseHtmlString("#" + rgb, out var color);
            return color;
        }
    }
}

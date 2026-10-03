using System.Collections;
using System.Collections.Generic;
using System.Text;
using CAIVR.Dialogue;
using CAIVR.Menu;
using TMPro;
using UnityEngine;

namespace CAIVR.VR
{
    /// <summary>
    /// Draws every Chinese character the game says into the Chinese font's atlas, a little each frame, as the scene
    /// loads or the student picks Chinese.
    ///
    /// The Chinese font is a dynamic one, so it can draw any character it has the moment it is asked, but drawing a
    /// glyph the first time costs a few milliseconds. Doing a whole caption's worth at once is a visible hitch, worst
    /// on a headset. Doing them all up front, spread over a second or so, means nothing is ever drawn at the moment it
    /// is needed. (The font asset itself is kept empty on disk, so it adds almost nothing to the project.)
    /// </summary>
    public sealed class UiFontWarmup : MonoBehaviour
    {
        const string ConversationResource = "CAIVR/Conversations/consultation_demo";
        const int CharactersPerFrame = 40;

        static readonly string[] FontResources =
        {
            "CAIVR/Fonts/NotoSansSC SDF",
            "CAIVR/Fonts/NotoSansSC SDF Overlay",
        };

        Coroutine _running;

        void Start()
        {
            if (Loc.NativeActive) Begin();
        }

        /// <summary>Starts filling the atlas if it is not already doing so. Safe to call whenever the language changes.</summary>
        public void Begin()
        {
            if (_running == null) _running = StartCoroutine(Warm());
        }

        IEnumerator Warm()
        {
            var fonts = new List<TMP_FontAsset>();
            foreach (var path in FontResources)
            {
                var font = Resources.Load<TMP_FontAsset>(path);
                if (font != null) fonts.Add(font);
            }

            var text = CollectText();

            for (var i = 0; i < text.Length; i += CharactersPerFrame)
            {
                var chunk = text.Substring(i, Mathf.Min(CharactersPerFrame, text.Length - i));
                foreach (var font in fonts) font.TryAddCharacters(chunk, out _);
                yield return null;
            }

            _running = null;
        }

        static string CollectText()
        {
            var seen = new HashSet<char>();
            var text = new StringBuilder();

            void Add(string s)
            {
                if (string.IsNullOrEmpty(s)) return;
                foreach (var c in s)
                    if (c > 0x2E80 && seen.Add(c)) text.Append(c);       // only the characters Inter cannot draw
            }

            foreach (var s in Loc.AllNativeText()) Add(s);

            var json = Resources.Load<TextAsset>(ConversationResource);
            var conversation = json != null ? JsonUtility.FromJson<ConversationAsset>(json.text) : null;

            if (conversation?.nodes != null)
            {
                if (conversation.contextVariantsZh != null)
                    foreach (var s in conversation.contextVariantsZh) Add(s);

                foreach (var node in conversation.nodes)
                {
                    Add(node.speakerLineZh);
                    Add(node.repromptZh);
                    Add(node.hintExampleZh);
                    if (node.hintsZh != null) foreach (var s in node.hintsZh) Add(s);
                }
            }

            return text.ToString();
        }
    }
}

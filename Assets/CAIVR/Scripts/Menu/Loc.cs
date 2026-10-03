using System.Collections.Generic;
using UnityEngine;

namespace CAIVR.Menu
{
    public enum Language
    {
        English = 0,
        Chinese = 1,
    }

    /// <summary>
    /// The few words the interface says, in English and in the student's own language.
    ///
    /// The professor always speaks English, since this is practice for a conversation in English. What
    /// changes for a student who sets a native language is the help around it:
    ///   - subtitles get a second, translated line under the English one
    ///   - hints appear in their own language rather than in English
    ///   - (optionally) the menu, the briefing card and the notebook are in their language too
    ///
    /// Text is looked up by key. A key with no translation falls back to the English, so a missing string
    /// shows English instead of nothing. To add a language, add it to <see cref="Language"/>, give every
    /// entry below a second column, and add its font as a fallback of the UI font (see UiFonts in the editor).
    /// </summary>
    public static class Loc
    {
        public static Language Native => (Language)Mathf.Clamp(CaivrSettings.NativeLanguage, 0, 1);

        /// <summary>The student has chosen a language other than English.</summary>
        public static bool NativeActive => Native != Language.English;

        /// <summary>Menus, cards and the notebook should be in the native language.</summary>
        public static bool InterfaceNative => NativeActive && CaivrSettings.LocalizeInterface;

        /// <summary>A language's name written in that language, so it can always be recognised.</summary>
        public static string NameOf(Language language) =>
            language == Language.Chinese ? "中文  (Chinese)" : "English";

        /// <summary>Interface text in the language the interface is currently showing.</summary>
        public static string T(string key) => Get(key, InterfaceNative);

        /// <summary>Interface text, always in English.</summary>
        public static string English(string key) => Get(key, false);

        /// <summary>
        /// Text for the help the student asked for by choosing a language (hints, mainly): in their language
        /// whenever one is chosen, whether or not the rest of the interface is.
        /// </summary>
        public static string Help(string key) => Get(key, NativeActive);

        static string Get(string key, bool native)
        {
            if (!Table.TryGetValue(key, out var entry)) return key;
            return native && !string.IsNullOrEmpty(entry.Zh) ? entry.Zh : entry.En;
        }

        /// <summary>
        /// The native text for a fixed English line the professor can say that is not part of the script
        /// file (the "take your time" prompt and the generic re-ask), or null.
        /// </summary>
        public static string NativeForFixedLine(string english)
        {
            foreach (var key in FixedLineKeys)
            {
                var entry = Table[key];
                if (entry.En == english) return entry.Zh;
            }

            return null;
        }

        /// <summary>Every native string, so the font can be given all its characters before they are needed.</summary>
        public static IEnumerable<string> AllNativeText()
        {
            foreach (var entry in Table.Values)
                if (!string.IsNullOrEmpty(entry.Zh)) yield return entry.Zh;

            yield return NameOf(Language.Chinese);
        }

        public const string TakeYourTime = "line.takeYourTime";
        public const string FallbackReprompt = "line.fallbackReprompt";

        static readonly string[] FixedLineKeys = { TakeYourTime, FallbackReprompt };

        struct Entry
        {
            public string En;
            public string Zh;
        }

        static Entry E(string en, string zh) => new Entry { En = en, Zh = zh };

        static readonly Dictionary<string, Entry> Table = new Dictionary<string, Entry>
        {
            // Menu.
            ["menu.title"] = E("Consultation  -  Monash College", "咨询  -  莫纳什学院"),
            ["menu.subtitle"] = E("Ask a professor for an extension, or about your assignment.", "向教授申请延期，或询问你的作业。"),
            ["menu.microphone"] = E("Microphone", "麦克风"),
            ["menu.dialogue"] = E("Dialogue system", "对话系统"),
            ["menu.voice"] = E("Professor voice", "教授声音"),
            ["menu.subtitles"] = E("Subtitles", "字幕"),
            ["menu.subtitles.value"] = E("Show what the professor says", "显示教授说的话"),
            ["menu.language"] = E("Native language", "母语"),
            ["menu.interface"] = E("Interface language", "界面语言"),
            ["menu.interface.value"] = E("Show menus and cards in my language", "菜单和卡片使用我的语言"),
            ["menu.change"] = E("Change", "更改"),
            ["menu.start"] = E("Start consultation", "开始咨询"),
            ["menu.system1"] = E("System 1  -  scripted", "系统1  -  预设对话"),
            ["menu.system2"] = E("System 2  -  AI assisted", "系统2  -  AI辅助"),
            ["ai.connecting"] = E("Connecting", "连接中"),
            ["ai.ready"] = E("AI connected", "AI已连接"),
            ["ai.offline"] = E("AI offline", "AI离线"),
            ["ai.local"] = E("Works offline", "可离线使用"),
            ["mic.none"] = E("No microphone found", "未找到麦克风"),
            ["mic.disconnected"] = E("Not connected", "未连接"),

            // Briefing card.
            ["card.eyebrow"] = E("Your situation", "你的情况"),
            ["card.title"] = E("Your consultation", "你的咨询"),
            ["card.wait"] = E("Take a moment to read this", "请花点时间阅读"),
            ["card.start"] = E("Start consultation", "开始咨询"),
            ["card.error.title"] = E("Could not start", "无法开始"),
            ["card.error.body"] = E("The conversation script failed to load. See the Console for details.", "对话脚本加载失败。详情请查看控制台。"),

            // Hints and notices.
            ["hint.header"] = E("You could say", "你可以说"),
            ["hint.example"] = E("Try saying", "试着说"),
            ["notice.nomic"] = E("No microphone found, so you cannot reply.", "未找到麦克风，所以无法回答。"),

            // Notebook.
            ["notebook.heading"] = E("Background", "背景"),
            ["notebook.pickup"] = E("Pick up to read", "拿起来阅读"),
            ["notebook.click"] = E("Click to read", "点击阅读"),
            ["notebook.empty"] = E("Your situation appears here when the consultation starts.", "咨询开始后，你的情况会显示在这里。"),

            // Lines the professor can say that are not in the script file.
            [TakeYourTime] = E("Take your time. What do you need?", "慢慢来。你需要什么？"),
            [FallbackReprompt] = E("Sorry, I didn't catch that. Could you say it again?", "抱歉，我没听清。你能再说一遍吗？"),
        };
    }
}

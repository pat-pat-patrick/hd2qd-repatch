using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace Hd2Qd.Repatch
{
    /// <summary>The plugin's text, one file per language: Lang\&lt;tag&gt;.json, a flat object of
    /// keys and texts, embedded in the DLL (the project file names each one Lang.&lt;tag&gt;.json).
    /// Adding a language is adding a file. A tag is looked up exactly, then by its language
    /// alone (de for de-AT), then in English; a key missing there is shown as itself.</summary>
    static class Words
    {
        static readonly Dictionary<string, Dictionary<string, string>> ByTag = Load();

        static Dictionary<string, Dictionary<string, string>> Load()
        {
            var asm = typeof(Words).Assembly;
            var all = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("Lang.") && n.EndsWith(".json")))
                using (var r = new StreamReader(asm.GetManifestResourceStream(name)))
                    all[name.Substring(5, name.Length - 10)] = new JavaScriptSerializer().Deserialize<Dictionary<string, string>>(r.ReadToEnd());
            return all;
        }

        static IEnumerable<Dictionary<string, string>> Chain(string lang)
        {
            if (!string.IsNullOrEmpty(lang))
            {
                if (ByTag.TryGetValue(lang, out var exact)) yield return exact;
                var dash = lang.IndexOf('-');
                if (dash > 0 && ByTag.TryGetValue(lang.Substring(0, dash), out var language)) yield return language;
            }
            if (ByTag.TryGetValue("en", out var en)) yield return en;
        }

        public static string Get(string lang, string key)
        {
            foreach (var t in Chain(lang))
                if (t.TryGetValue(key, out var s)) return s;
            return key;
        }

        /// <summary>A text with arguments; a first argument of 1 picks key_1 where the language has it.</summary>
        public static string F(string lang, string key, params object[] args)
        {
            if (args.Length > 0 && Convert.ToString(args[0]) == "1")
                foreach (var t in Chain(lang))
                {
                    if (t.TryGetValue(key + "_1", out var one)) return string.Format(one, args);
                    if (t.ContainsKey(key)) break;            // this language has no form for one
                }
            return string.Format(Get(lang, key), args);
        }

        /// <summary>The language the manager shows, from the host.</summary>
        public static string Lang(Dictionary<string, object> host) => host.TryGetValue("lang", out var l) ? l as string : null;
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Hd2Qd.Repatch
{
    /// <summary>Model repair: after a game update, a mod's models (units) made for an earlier
    /// version of the game may no longer load. This brings them up to the game's version, as
    /// hd2-repatcher (RaidingForPants) does, with hd2-mod-crates' (eigeen) safer rules: a model
    /// is never moved to an older version, across 10800437 -> 10800438 only the vertex formats
    /// change, unknown vertex formats are refused, and models the game does not have are kept.
    ///
    /// A window plugin of HD2 Mod Quick Deploy, called by name (core\Plugins.cs, PluginFiles):
    /// every method takes and gives strings, numbers, bools, delegates and Json shapes only.</summary>
    public static partial class Plugin
    {
        public const int Api = 2;
        public const string Kind = "window";

        /// <summary>In lang, a language tag ("en", "zh-CN"): the text comes from that language's
        /// file, else English (Words, Lang\*.json).</summary>
        public static string Title(string lang) => Words.Get(lang, "title");

        public static string Summary(string lang) => Words.Get(lang, "summary");

        /// <summary>The command line's help and output are English only, as the manager's own.</summary>
        public static readonly string[][] Commands =
        {
            new[] { "repatch-check", "list the library's mods whose models were made for an earlier version of the game" },
            new[] { "repatch", "repatch MOD... | --all: bring those mods' models up to the game's version (the original files are kept)" },
            new[] { "repatch-undo", "repatch-undo MOD: put back the files the mod's last repair changed" },
        };

        static string Home(Dictionary<string, object> host) => (string)host["home"];
        static string Data(Dictionary<string, object> host) => host.TryGetValue("data", out var d) ? d as string : null;

        /// <summary>The library's mods, each {name, path, enabled}.</summary>
        static List<(string Name, string Path, bool Enabled)> Library(Dictionary<string, object> host) =>
            ((Func<List<Dictionary<string, object>>>)host["library"])()
                .Select(m => ((string)m["name"], (string)m["path"], m.TryGetValue("enabled", out var e) && e is bool b && b)).ToList();

        /// <summary>Reads the game's models once in the background when the window starts (about
        /// two seconds; kept while the game is not updated). True when it read them, so the rows
        /// show the notes.</summary>
        public static bool Start(Dictionary<string, object> host)
        {
            var data = Data(host);
            if (data == null || !File.Exists(Path.Combine(data, "bundles.nxa")) || GameUnits.Cached(Home(host), data) != null) return false;
            GameUnits.Load(Home(host), data);
            return true;
        }

        /// <summary>A word on the row of a mod with models made for an earlier version of the game
        /// (or a later one). Only from what is cheap: nothing until the game's models are read.</summary>
        public static Dictionary<string, object> ModNote(string modFolder, Dictionary<string, object> host)
        {
            var game = GameUnits.Cached(Home(host), Data(host));
            if (game == null) return null;
            var m = Repairs.Scan(Home(host), null, modFolder, false, game);
            var lang = Words.Lang(host);
            if (m.Update > 0)
                return new Dictionary<string, object>
                {
                    ["text"] = Words.Get(lang, "note_outdated"), ["tip"] = Words.F(lang, "note_outdated_tip", m.Update), ["colour"] = "Yellow",
                };
            if (m.Newer > 0)
                return new Dictionary<string, object>
                {
                    ["text"] = Words.Get(lang, "note_newer"), ["tip"] = Words.F(lang, "note_newer_tip", m.Newer), ["colour"] = "Muted",
                };
            return null;
        }

        public static int Run(string command, string[] args, TextWriter o, Dictionary<string, object> host)
        {
            var data = Data(host) ?? throw new InvalidOperationException("no game folder: run  hd2qd init --game \"<...>\\steamapps\\common\\Helldivers 2\"");
            var game = GameUnits.Load(Home(host), data);
            var lib = Library(host);
            switch (command)
            {
                case "repatch-check":
                {
                    var mods = lib.Select(m => Repairs.Scan(Home(host), m.Name, m.Path, m.Enabled, game)).ToList();
                    o.WriteLine("the game's models: version " + game.Latest);
                    var shown = mods.Where(m => m.Update + m.Newer > 0 || m.Errors.Count > 0 || m.Undoable).ToList();
                    foreach (var m in shown)
                        o.WriteLine("  " + (m.Update > 0 ? "outdated" : m.Newer > 0 ? "newer" : m.Errors.Count > 0 ? "unreadable" : "repaired").PadRight(11) +
                                    m.Name + (m.Update > 0 ? "  (" + Of(m.Update, "model", "models") + ")" : "") + (m.Undoable ? "  [can be undone]" : "") +
                                    (m.Errors.Count > 0 ? "  " + string.Join("; ", m.Errors) : ""));
                    int current = mods.Count(m => m.Models > m.Added && m.Update + m.Newer == 0 && m.Errors.Count == 0);
                    o.WriteLine(Of(mods.Count(m => m.Update > 0), "mod", "mods") + " to repair; " + Of(current, "mod", "mods") + " with game models current");
                    return 0;
                }
                case "repatch":
                {
                    if (args.Length == 0) return 2;
                    var targets = args.Contains("--all")
                        ? lib.Where(m => Repairs.Scan(Home(host), m.Name, m.Path, m.Enabled, game).Update > 0).ToList()
                        : args.Select(a => Find(lib, a)).ToList();
                    if (targets.Count == 0) { o.WriteLine("no mod needs a repair"); return 0; }
                    foreach (var m in targets)
                    {
                        var d = Repairs.Repair(Home(host), m.Path, game);
                        o.WriteLine(m.Name + ": " + (d.Files == 0 ? "nothing to repair" : Of(d.Models, "model", "models") + " in " + Of(d.Files, "file", "files") + " repaired; originals kept in " + d.Backup));
                        foreach (var r in d.Refused) o.WriteLine("  not repaired: " + r);
                    }
                    o.WriteLine("deploy to put the repaired files in the game");
                    return 0;
                }
                case "repatch-undo":
                {
                    if (args.Length != 1) return 2;
                    var m = Find(lib, args[0]);
                    var (restored, changed) = Repairs.Undo(Home(host), m.Path);
                    o.WriteLine(m.Name + ": " + Of(restored, "file", "files") + " put back" + (changed.Count > 0 ? "; changed since the repair, left as they are: " + string.Join(", ", changed) : ""));
                    o.WriteLine("deploy to put them in the game");
                    return changed.Count > 0 ? 1 : 0;
                }
            }
            return 2;
        }

        /// <summary>The command line's counts (English only).</summary>
        static string Of(int n, string one, string many) => n + " " + (n == 1 ? one : many);

        /// <summary>A library mod by its exact name, else the one whose name contains the text.</summary>
        static (string Name, string Path, bool Enabled) Find(List<(string Name, string Path, bool Enabled)> lib, string text)
        {
            var exact = lib.Where(m => string.Equals(m.Name, text, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count == 1) return exact[0];
            var some = lib.Where(m => m.Name.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (some.Count == 1) return some[0];
            throw new InvalidOperationException(some.Count == 0 ? "no mod in the library is named " + text : text + " names " + some.Count + " mods: " + string.Join(", ", some.Select(m => m.Name)));
        }
    }
}

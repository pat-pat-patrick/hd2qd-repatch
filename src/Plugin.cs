using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Hd2Qd.Repatch
{
    /// <summary>Repair: after a game update, a mod's models (units), sound banks (Wwise) and
    /// strings tables (subtitles) made for an earlier version of the game may no longer load, or
    /// lose parts of the game's own content. This brings them up to the game's current form: the
    /// models as hd2-repatcher (RaidingForPants) does, with hd2-mod-crates' (eigeen) safer rules;
    /// a bank rebuilt on the game's current HIRC with the mod's media put back, as the standalone
    /// audio-repair tools do; a strings table merged with the game's current one. A resource is
    /// never moved to an older version, and one the game does not have is kept.
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
            new[] { "repatch-check", "list the library's mods whose models, sound banks or subtitles were made for an earlier version of the game" },
            new[] { "repatch", "repatch MOD... | --all: bring those mods' models, banks and subtitles up to the game's version (the original files are kept)" },
            new[] { "repatch-undo", "repatch-undo MOD: put back the files the mod's last repair changed" },
        };

        static string Home(Dictionary<string, object> host) => (string)host["home"];
        static string Data(Dictionary<string, object> host) => host.TryGetValue("data", out var d) ? d as string : null;

        /// <summary>The library's mods, each {name, path, enabled}.</summary>
        static List<(string Name, string Path, bool Enabled)> Library(Dictionary<string, object> host) =>
            ((Func<List<Dictionary<string, object>>>)host["library"])()
                .Select(m => ((string)m["name"], (string)m["path"], m.TryGetValue("enabled", out var e) && e is bool b && b)).ToList();

        /// <summary>Reads the game's models, and its banks and strings tables, once in the
        /// background when the window starts (a few seconds; kept while the game is not
        /// updated). True when it read the models, so the rows show the notes.</summary>
        public static bool Start(Dictionary<string, object> host)
        {
            var data = Data(host);
            if (data == null || !File.Exists(Path.Combine(data, "bundles.nxa"))) return false;
            var home = Home(host);
            var loaded = GameUnits.Cached(home, data) == null;
            GameUnits.Load(home, data);
            GameBanks.Load(home, data);            // each Load reads only what is not held or cached yet
            return loaded;
        }

        /// <summary>A word on the row of a mod with resources made for an earlier version of the
        /// game (or a later one). Only from what is cheap: nothing until the game is read.</summary>
        public static Dictionary<string, object> ModNote(string modFolder, Dictionary<string, object> host)
        {
            var home = Home(host);
            var data = Data(host);
            var game = GameUnits.Cached(home, data);
            if (game == null) return null;
            var m = Repairs.Scan(home, null, modFolder, false, game, data, cachedBanksOnly: true);
            var lang = Words.Lang(host);
            if (m.Update + m.BankUpdate + m.TableUpdate > 0)
            {
                var kinds = new List<string>();
                if (m.Update > 0) kinds.Add(Words.Get(lang, "note_models"));
                if (m.BankUpdate > 0) kinds.Add(Words.Get(lang, "note_banks"));
                if (m.TableUpdate > 0) kinds.Add(Words.Get(lang, "note_subtitles"));
                var tips = new List<string>();
                if (m.Update > 0) tips.Add(Words.F(lang, "note_outdated_tip", m.Update));
                if (m.BankUpdate > 0) tips.Add(Words.F(lang, "note_banks_tip", m.BankUpdate));
                if (m.TableUpdate > 0) tips.Add(Words.F(lang, "note_subtitles_tip", m.TableUpdate));
                return new Dictionary<string, object>
                {
                    ["text"] = Words.F(lang, "note_outdated", string.Join(Words.Get(lang, "note_join"), kinds)),
                    ["tip"] = string.Join("\n", tips), ["colour"] = "Yellow",
                };
            }
            if (m.Newer + m.BankNewer + m.TableNewer > 0)
                return new Dictionary<string, object>
                {
                    ["text"] = Words.Get(lang, "note_newer"),
                    ["tip"] = Words.F(lang, "note_newer_tip", m.Newer + m.BankNewer + m.TableNewer), ["colour"] = "Muted",
                };
            return null;
        }

        public static int Run(string command, string[] args, TextWriter o, Dictionary<string, object> host)
        {
            var data = Data(host) ?? throw new InvalidOperationException("no game folder: run  hd2qd init --game \"<...>\\steamapps\\common\\Helldivers 2\"");
            var home = Home(host);
            var game = GameUnits.Load(home, data);
            var lib = Library(host);
            switch (command)
            {
                case "repatch-check":
                {
                    var mods = Repairs.ScanAll(home, lib, game, data);
                    o.WriteLine("the game's models: version " + game.Latest + " (the check also covers sound banks and subtitle tables)");
                    var shown = mods.Where(m => Stale(m) + Newer(m) > 0 || m.Errors.Count > 0 || m.Undoable).ToList();
                    foreach (var m in shown)
                        o.WriteLine("  " + (Stale(m) > 0 ? "outdated" : Newer(m) > 0 ? "newer" : m.Errors.Count > 0 ? "unreadable" : "repaired").PadRight(11) +
                                    m.Name + (Stale(m) > 0 ? "  (" + Shortage(m) + ")" : "") + (m.Undoable ? "  [can be undone]" : "") +
                                    (m.Errors.Count > 0 ? "  " + string.Join("; ", m.Errors) : ""));
                    int current = mods.Count(m => m.Models + m.Banks + m.Tables > m.Added && Stale(m) + Newer(m) == 0 && m.Errors.Count == 0);
                    o.WriteLine(Of(mods.Count(m => Stale(m) > 0), "mod", "mods") + " to repair; " + Of(current, "mod", "mods") + " with game resources current");
                    return 0;
                }
                case "repatch":
                {
                    if (args.Length == 0) return 2;
                    var targets = args.Contains("--all")
                        ? Repairs.ScanAll(home, lib, game, data).Where(m => Stale(m) > 0).Select(m => (m.Name, m.Path, true)).ToList()
                        : args.Select(a => Find(lib, a)).ToList();
                    if (targets.Count == 0) { o.WriteLine("no mod needs a repair"); return 0; }
                    foreach (var m in targets)
                    {
                        var d = Repairs.Repair(home, m.Path, game, data);
                        o.WriteLine(m.Name + ": " + (d.Files == 0 ? "nothing to repair" : Fixed(d) + " in " + Of(d.Files, "file", "files") + " repaired; originals kept in " + d.Backup));
                        foreach (var r in d.Refused) o.WriteLine("  not repaired: " + r);
                        foreach (var r in d.Notes) o.WriteLine("  " + r);
                    }
                    o.WriteLine("deploy to put the repaired files in the game");
                    return 0;
                }
                case "repatch-undo":
                {
                    if (args.Length != 1) return 2;
                    var m = Find(lib, args[0]);
                    var (restored, changed) = Repairs.Undo(home, m.Path);
                    o.WriteLine(m.Name + ": " + Of(restored, "file", "files") + " put back" + (changed.Count > 0 ? "; changed since the repair, left as they are: " + string.Join(", ", changed) : ""));
                    o.WriteLine("deploy to put them in the game");
                    return changed.Count > 0 ? 1 : 0;
                }
            }
            return 2;
        }

        /// <summary>How many of the mod's resources an earlier version of the game was made for,
        /// over every kind.</summary>
        static int Stale(Repairs.Mod m) => m.Update + m.BankUpdate + m.TableUpdate;

        static int Newer(Repairs.Mod m) => m.Newer + m.BankNewer + m.TableNewer;

        /// <summary>The command line's counts (English only).</summary>
        static string Of(int n, string one, string many) => n + " " + (n == 1 ? one : many);

        static string Shortage(Repairs.Mod m)
        {
            var parts = new List<string>();
            if (m.Update > 0) parts.Add(Of(m.Update, "model", "models"));
            if (m.BankUpdate > 0) parts.Add(Of(m.BankUpdate, "bank", "banks"));
            if (m.TableUpdate > 0) parts.Add(Of(m.TableUpdate, "subtitle table", "subtitle tables"));
            return string.Join(", ", parts);
        }

        static string Fixed(Repairs.Done d)
        {
            var parts = new List<string>();
            if (d.Models > 0) parts.Add(Of(d.Models, "model", "models"));
            if (d.Banks > 0) parts.Add(Of(d.Banks, "bank", "banks"));
            if (d.Tables > 0) parts.Add(Of(d.Tables, "subtitle table", "subtitle tables"));
            return string.Join(" and ", parts);
        }

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

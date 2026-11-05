using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Hd2Qd.Repatch
{
    /// <summary>A library mod's models, sound banks and strings tables against the game's, and
    /// their repair. A repair keeps a copy of every main file it changes in the manager's
    /// settings folder (repatch\backups), so it can be undone. It never writes into a file: it
    /// writes a new one beside it and puts it in the old one's place, because the manager deploys
    /// library files as hard links into the game folder, and the deployed copy must stay as it is
    /// until the next deploy.</summary>
    static class Repairs
    {
        /// <summary>One mod: its archives' resources of each kind that need an update, are newer
        /// than the game's, or that the game does not have; what could not be read; whether a
        /// repair can be undone.</summary>
        public sealed class Mod
        {
            public string Name, Path;
            public bool Enabled;
            public int Models, Update, Newer, Added;
            public int Banks, BankUpdate, BankNewer;
            public int Tables, TableUpdate, TableNewer;
            public List<string> Errors = new List<string>();
            public bool Undoable;
        }

        static readonly object gate = new object();
        static readonly Dictionary<string, (long Len, long Time, GameUnits Game, GameBanks Banks, Patches.Scan Scan)> scans =
            new Dictionary<string, (long, long, GameUnits, GameBanks, Patches.Scan)>(StringComparer.OrdinalIgnoreCase);

        /// <summary>A mod's state; each archive read again only when it, the game or the game's
        /// bank index changed.</summary>
        public static Mod Scan(string home, string name, string path, bool enabled, GameUnits game, string data)
        {
            var m = new Mod { Name = name, Path = path, Enabled = enabled, Undoable = Last(home, path) != null };
            GameBanks banks = data == null ? null : GameBanks.Cached(home, data);
            foreach (var f in Patches.Files(path))
            {
                Patches.Scan s;
                var fi = new FileInfo(f);
                lock (gate)
                {
                    if (scans.TryGetValue(f, out var c) && c.Len == fi.Length && c.Time == fi.LastWriteTimeUtc.Ticks && c.Game == game && c.Banks == banks)
                        s = c.Scan;
                    else scans[f] = (fi.Length, fi.LastWriteTimeUtc.Ticks, game, banks, s = Patches.Check(f, game, banks));
                }
                if (s.NeedsBanks && banks == null)
                {
                    banks = GameBanks.Load(home, data);
                    lock (gate) scans[f] = (fi.Length, fi.LastWriteTimeUtc.Ticks, game, banks, s = Patches.Check(f, game, banks));
                }
                m.Models += s.Models; m.Update += s.Update; m.Newer += s.Newer; m.Added += s.Added;
                m.Banks += s.Banks; m.BankUpdate += s.BankUpdate; m.BankNewer += s.BankNewer;
                m.Tables += s.Tables; m.TableUpdate += s.TableUpdate; m.TableNewer += s.TableNewer;
                if (s.Error != null) m.Errors.Add(System.IO.Path.GetFileName(f) + ": " + s.Error);
            }
            return m;
        }

        /// <summary>What a repair did.</summary>
        public sealed class Done
        {
            public int Files, Models, Formats, Lods, Banks, Tables;
            public List<string> Refused = new List<string>();
            public List<string> Notes = new List<string>();
            public string Backup;
        }

        static string Backups(string home) => System.IO.Path.Combine(home, "repatch", "backups");

        /// <summary>Repairs every archive of the mod whose resources need it. Every changed file
        /// is copied to a new backup folder first; nothing is changed when nothing needs it. A
        /// file that cannot be repaired (damaged, or a resource it cannot update) is named in
        /// Refused and the rest go on.</summary>
        public static Done Repair(string home, string modPath, GameUnits game, string data)
        {
            var done = new Done();
            var banks = data != null && GameBanks.Needed(modPath) ? GameBanks.Load(home, data) : null;
            var plans = new List<(string File, byte[] Bytes)>();
            foreach (var f in Patches.Files(modPath))
            {
                var res = new Patches.Result();
                byte[] fixedBytes;
                try { fixedBytes = Patches.Update(File.ReadAllBytes(f), game, banks, res); }
                catch (InvalidDataException e) { done.Refused.Add(Rel(modPath, f) + ": " + e.Message); continue; }
                done.Refused.AddRange(res.Refused.Select(r => Rel(modPath, f) + ": " + r));
                done.Notes.AddRange(res.Notes.Select(r => Rel(modPath, f) + ": " + r));
                if (fixedBytes == null) continue;
                plans.Add((f, fixedBytes));
                done.Models += res.Updated; done.Formats += res.Formats; done.Lods += res.Lods;
                done.Banks += res.Banks; done.Tables += res.Tables;
            }
            if (plans.Count == 0) return done;

            var folder = System.IO.Path.Combine(Backups(home), DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(folder);
            var record = new StringBuilder("mod\t" + System.IO.Path.GetFullPath(modPath) + "\n");
            foreach (var (f, _) in plans)
            {
                var keep = System.IO.Path.Combine(folder, "files", Rel(modPath, f));
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(keep));
                File.Copy(f, keep);
            }
            foreach (var (f, bytes) in plans)
            {
                Put(f, bytes);
                record.Append("file\t" + Rel(modPath, f) + "\t" + Sha(bytes) + "\n");
                done.Files++;
            }
            File.WriteAllText(System.IO.Path.Combine(folder, "record.txt"), record.ToString(), new UTF8Encoding(false));
            done.Backup = folder;
            return done;
        }

        /// <summary>The newest repair of the mod that has not been undone: its backup folder and
        /// its files (relative path, hash of what the repair wrote).</summary>
        public static (string Folder, List<(string Rel, string Sha)> Files)? Last(string home, string modPath)
        {
            var root = Backups(home);
            if (modPath == null || !Directory.Exists(root)) return null;
            string want = System.IO.Path.GetFullPath(modPath);
            foreach (var d in Directory.GetDirectories(root).OrderByDescending(x => x, StringComparer.Ordinal))
            {
                var rec = System.IO.Path.Combine(d, "record.txt");
                if (!File.Exists(rec)) continue;
                var lines = File.ReadAllLines(rec, Encoding.UTF8);
                if (lines.Length == 0 || !string.Equals(lines[0], "mod\t" + want, StringComparison.OrdinalIgnoreCase)) continue;
                var files = lines.Skip(1).Select(l => l.Split('\t')).Where(p => p.Length == 3 && p[0] == "file").Select(p => (p[1], p[2])).ToList();
                return (d, files);
            }
            return null;
        }

        /// <summary>Puts back the files of the mod's newest repair: (restored, the ones changed
        /// since the repair, which are left as they are). The backup is removed when every file
        /// is back.</summary>
        public static (int Restored, List<string> Changed) Undo(string home, string modPath)
        {
            var last = Last(home, modPath) ?? throw new InvalidOperationException("no repair of this mod can be undone");
            int restored = 0;
            var changed = new List<string>();
            foreach (var (rel, sha) in last.Files)
            {
                var f = System.IO.Path.Combine(modPath, rel);
                var keep = System.IO.Path.Combine(last.Folder, "files", rel);
                if (!File.Exists(keep)) { changed.Add(rel); continue; }
                if (File.Exists(f) && Sha(File.ReadAllBytes(f)) != sha) { changed.Add(rel); continue; }
                Put(f, File.ReadAllBytes(keep));
                restored++;
            }
            if (changed.Count == 0) Directory.Delete(last.Folder, true);
            else File.Move(System.IO.Path.Combine(last.Folder, "record.txt"), System.IO.Path.Combine(last.Folder, "record.undone.txt"));
            return (restored, changed);
        }

        /// <summary>A file's new bytes, under a new name beside it, then in its place: the old file
        /// is removed from this folder only, so a hard link elsewhere keeps the old bytes.</summary>
        static void Put(string path, byte[] bytes)
        {
            var tmp = path + ".repatch-" + Guid.NewGuid().ToString("N").Substring(0, 6);
            File.WriteAllBytes(tmp, bytes);
            try
            {
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch
            {
                try { File.Delete(tmp); } catch (IOException) { }
                throw;
            }
        }

        static string Rel(string root, string path)
        {
            var r = System.IO.Path.GetFullPath(root).TrimEnd('\\', '/') + "\\";
            var p = System.IO.Path.GetFullPath(path);
            return p.StartsWith(r, StringComparison.OrdinalIgnoreCase) ? p.Substring(r.Length) : System.IO.Path.GetFileName(p);
        }

        static string Sha(byte[] b)
        {
            using (var h = SHA256.Create()) return BitConverter.ToString(h.ComputeHash(b)).Replace("-", "").ToLowerInvariant();
        }
    }
}

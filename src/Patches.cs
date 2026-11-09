using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Hd2Qd.Repatch
{
    /// <summary>A patch archive's main file: a 72-byte header (magic 0xF0000011, the type count
    /// at 4, the resource count at 8), 32-byte type rows (type id at 8, count at 16), then
    /// 80-byte resource rows (id, type, main bytes' offset at 16 and size at 56), then the
    /// resources' main bytes. Only the main file changes when a resource is brought up to the
    /// game's; a bank's streamed media lives in .stream, which a repair never touches.</summary>
    static class Patches
    {
        public static readonly Regex Main = new Regex(@"^[0-9a-f]{16}\.patch_\d+$");

        /// <summary>Every patch archive's main file under a mod's folder (its options' folders too).</summary>
        public static List<string> Files(string modFolder)
        {
            if (modFolder == null || !Directory.Exists(modFolder)) return new List<string>();
            try
            {
                return Directory.EnumerateFiles(modFolder, "*.patch_*", SearchOption.AllDirectories)
                    .Where(f => Main.IsMatch(Path.GetFileName(f))).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return new List<string>(); }
        }

        public struct Row
        {
            public int At;            // the row's offset in the file
            public ulong Id, Type, Off;
            public uint Size;
        }

        /// <summary>The header's rows, checked as hd2-mod-crates checks a patch before it changes
        /// one: type ids are 64-bit hashes, the types' counts add up to the rows, no resource
        /// twice, every resource's bytes inside the file and none overlapping another.</summary>
        public static List<Row> Rows(byte[] toc)
        {
            if (toc.Length < 72 || BitConverter.ToUInt32(toc, 0) != 0xF0000011) throw new InvalidDataException("not a patch archive");
            uint tc = BitConverter.ToUInt32(toc, 4), fc = BitConverter.ToUInt32(toc, 8);
            if (tc > 4096 || fc > 1000000 || 72L + tc * 32 + fc * 80L > toc.Length) throw new InvalidDataException("the archive's header is damaged");
            ulong total = 0;
            for (int t = 0; t < tc; t++)
            {
                int o = 72 + t * 32;
                if (BitConverter.ToUInt64(toc, o + 8) < 1UL << 32) throw new InvalidDataException("the archive's type table is damaged");
                total += BitConverter.ToUInt64(toc, o + 16);
            }
            if (total != fc) throw new InvalidDataException("the archive's type table counts " + total + " resources, its rows " + fc);
            var rows = new List<Row>((int)fc);
            var seen = new HashSet<(ulong, ulong)>();
            int b = 72 + (int)tc * 32;
            for (int i = 0; i < fc; i++)
            {
                int o = b + i * 80;
                var r = new Row
                {
                    At = o, Id = BitConverter.ToUInt64(toc, o), Type = BitConverter.ToUInt64(toc, o + 8),
                    Off = BitConverter.ToUInt64(toc, o + 16), Size = BitConverter.ToUInt32(toc, o + 56),
                };
                if (!seen.Add((r.Id, r.Type))) throw new InvalidDataException("the archive holds resource " + r.Id.ToString("x16") + " twice");
                if (r.Off + r.Size > (ulong)toc.Length) throw new InvalidDataException("a resource lies outside the archive");
                rows.Add(r);
            }
            ulong end = (ulong)(b + fc * 80);
            foreach (var r in rows.Where(x => x.Size > 0).OrderBy(x => x.Off))
            {
                if (r.Off < end) throw new InvalidDataException("two resources of the archive overlap");
                end = r.Off + r.Size;
            }
            return rows;
        }

        /// <summary>What one archive needs: each resource of the kinds this plugin repairs (a
        /// model, a sound bank, a strings table) with the game's verdict; a resource the game
        /// does not have counts as added. NeedsBanks says the archive holds banks or tables but
        /// the game's index was not read yet, so the check must run again with it.</summary>
        public sealed class Scan
        {
            public string Path;
            public int Models, Update, Newer, Added;
            public int Banks, BankUpdate, BankNewer;
            public int Tables, TableUpdate, TableNewer;
            public bool NeedsBanks;
            public string Error;
        }

        /// <summary>Reads the header, and each model's version (cheap) or each bank's and table's
        /// bytes (bigger, but only for mods that carry them). The bytes come off the file one
        /// after another; the compare with the game's own copy, which is the slow part (a parse
        /// and a read of the game's bank), runs on a few threads at once.</summary>
        public static Scan Check(string path, GameUnits game, GameBanks banks)
        {
            var s = new Scan { Path = path };
            try
            {
                using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096))
                {
                    var head = new byte[72];
                    if (f.Read(head, 0, 72) < 72 || BitConverter.ToUInt32(head, 0) != 0xF0000011) { s.Error = "not a patch archive"; return s; }
                    uint tc = BitConverter.ToUInt32(head, 4), fc = BitConverter.ToUInt32(head, 8);
                    if (tc > 4096 || fc > 1000000) { s.Error = "the archive's header is damaged"; return s; }
                    var types = new byte[tc * 32];
                    if (f.Read(types, 0, types.Length) < types.Length) { s.Error = "the archive's header is damaged"; return s; }
                    bool any = false;
                    for (int t = 0; t < tc; t++)
                    {
                        var ty = BitConverter.ToUInt64(types, t * 32 + 8);
                        any |= ty == GameUnits.Unit || ty == GameBanks.Bank || ty == GameBanks.Table;
                    }
                    if (!any) return s;
                    var rows = new byte[fc * 80];
                    if (f.Read(rows, 0, rows.Length) < rows.Length) { s.Error = "the archive's header is damaged"; return s; }
                    var v = new byte[4];
                    var targets = new List<(ulong Id, ulong Off, uint Size, bool Bank)>();
                    for (int i = 0; i < fc; i++)
                    {
                        int o = i * 80;
                        ulong ty = BitConverter.ToUInt64(rows, o + 8), id = BitConverter.ToUInt64(rows, o);
                        ulong off = BitConverter.ToUInt64(rows, o + 16);
                        uint size = BitConverter.ToUInt32(rows, o + 56);
                        if (ty == GameUnits.Unit)
                        {
                            s.Models++;
                            if (!game.Units.TryGetValue(id, out var g)) { s.Added++; continue; }
                            if (size < UnitBytes.Version + 4 || off + size > (ulong)f.Length) { s.Error = "a model lies outside the archive"; continue; }
                            f.Seek((long)off + UnitBytes.Version, SeekOrigin.Begin);
                            if (f.Read(v, 0, 4) < 4) { s.Error = "a model lies outside the archive"; continue; }
                            var n = UnitBytes.Judge(BitConverter.ToUInt32(v, 0), g.Version);
                            if (n == Need.Update) s.Update++;
                            else if (n == Need.Newer) s.Newer++;
                        }
                        else if (ty == GameBanks.Bank || ty == GameBanks.Table)
                        {
                            if (ty == GameBanks.Bank) s.Banks++; else s.Tables++;
                            if (banks == null) { s.NeedsBanks = true; continue; }
                            if (size < 4 || off + size > (ulong)f.Length) { s.Error = (ty == GameBanks.Bank ? "a bank" : "a strings table") + " lies outside the archive"; continue; }
                            targets.Add((id, off, size, ty == GameBanks.Bank));
                        }
                    }

                    var bodies = new byte[targets.Count][];
                    var errors = new string[targets.Count];
                    var verdict = new byte[targets.Count];               // 0 none, 1 update, 2 newer, 3 added
                    using (var f2 = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096))
                        for (int i = 0; i < targets.Count; i++)
                        {
                            var t = targets[i];
                            var bytes = new byte[t.Size];
                            f2.Seek((long)t.Off, SeekOrigin.Begin);
                            if (f2.Read(bytes, 0, bytes.Length) < bytes.Length) { errors[i] = (t.Bank ? "a bank" : "a strings table") + " lies outside the archive"; bodies[i] = null; }
                            else bodies[i] = bytes;
                        }
                    Parallel.For(0, targets.Count, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i =>
                    {
                        if (bodies[i] == null) return;
                        var t = targets[i];
                        if (t.Bank)
                        {
                            if (!banks.BankRows.ContainsKey(t.Id)) { verdict[i] = 3; return; }       // the mod's own bank
                            Banks.BankFile mine;
                            try { mine = Banks.Info(Banks.Unwrap(bodies[i], t.Id).Raw); }
                            catch (InvalidDataException e) { errors[i] = e.Message; return; }
                            var official = banks.OfficialBank(t.Id);
                            if (official == null) { errors[i] = "the game's own bank of " + t.Id.ToString("x16") + " cannot be read"; return; }
                            var n = Banks.Judge(mine, official);
                            if (n == Need.Update) verdict[i] = 1;
                            else if (n == Need.Newer) verdict[i] = 2;
                        }
                        else
                        {
                            if (!banks.Tables.ContainsKey(t.Id)) { verdict[i] = 3; return; }         // the mod's own table
                            Subtitles.Table mine;
                            try { mine = Subtitles.Parse(bodies[i]); }
                            catch (InvalidDataException e) { errors[i] = e.Message; return; }
                            var official = banks.OfficialTable(t.Id);
                            if (official == null) { errors[i] = "the game's own strings table of " + t.Id.ToString("x16") + " cannot be read"; return; }
                            var n = Subtitles.Judge(mine, official);
                            if (n == Need.Update) verdict[i] = 1;
                            else if (n == Need.Newer) verdict[i] = 2;
                        }
                    });
                    for (int i = 0; i < targets.Count; i++)
                    {
                        if (verdict[i] == 1 && targets[i].Bank) s.BankUpdate++;
                        else if (verdict[i] == 1) s.TableUpdate++;
                        else if (verdict[i] == 2 && targets[i].Bank) s.BankNewer++;
                        else if (verdict[i] == 2) s.TableNewer++;
                        else if (verdict[i] == 3) s.Added++;
                        if (errors[i] != null && s.Error == null) s.Error = errors[i];
                    }
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { s.Error = e.Message; }
            return s;
        }

        /// <summary>What Update did to one archive.</summary>
        public sealed class Result
        {
            public int Updated, Formats, Lods, Banks, Tables, Added, Current;
            public List<string> Refused = new List<string>();
            public List<string> Notes = new List<string>();
        }

        /// <summary>The archive's main file with every resource of the kinds this plugin repairs
        /// brought up to the game's; null when none needed it. Resources the game does not have
        /// are kept as they are (a mod may add its own). One that cannot be brought up is left as
        /// it is and named in Refused. Everything else keeps its bytes: the resources after one
        /// whose size changed move, and their rows' offsets with them.</summary>
        public static byte[] Update(byte[] toc, GameUnits game, GameBanks banks, Result res)
        {
            var rows = Rows(toc);
            var fresh = new Dictionary<int, byte[]>();          // row index -> its new bytes
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                if (r.Type != GameUnits.Unit && r.Type != GameBanks.Bank && r.Type != GameBanks.Table) continue;
                byte[] bytes = null;
                if (r.Type == GameUnits.Unit)
                {
                    if (!game.Units.TryGetValue(r.Id, out var g)) { res.Added++; continue; }
                    var unit = new byte[r.Size];
                    Buffer.BlockCopy(toc, (int)r.Off, unit, 0, unit.Length);
                    try
                    {
                        var u = UnitBytes.Update(unit, g.Version, g.Lod, out int formats, out bool lod);
                        if (u == null) { res.Current++; continue; }
                        bytes = u;
                        res.Updated++;
                        res.Formats += formats;
                        if (lod) res.Lods++;
                    }
                    catch (InvalidDataException e) { res.Refused.Add(r.Id.ToString("x16") + ": " + e.Message); }
                }
                else
                {
                    if (banks == null)                                          // the index was never built (a locked
                    {                                                           // archive hid the rows from Needed)
                        res.Refused.Add(r.Id.ToString("x16") + ": the game's banks and tables are not read yet; check again");
                        continue;
                    }
                    var resource = new byte[r.Size];
                    Buffer.BlockCopy(toc, (int)r.Off, resource, 0, resource.Length);
                    bytes = r.Type == GameBanks.Bank ? Banks.Repair(resource, r.Id, banks, res) : Subtitles.Repair(resource, r.Id, banks, res);
                }
                if (bytes != null) fresh[i] = bytes;
            }
            if (fresh.Count == 0) return null;

            // the bytes in the file's order, the updated resources in place of the old ones
            var order = Enumerable.Range(0, rows.Count).Where(i => rows[i].Size > 0).OrderBy(i => rows[i].Off).ToList();
            var outp = new MemoryStream(toc.Length + fresh.Values.Sum(u => u.Length));
            int b = 72 + (int)BitConverter.ToUInt32(toc, 4) * 32 + rows.Count * 80;
            outp.Write(toc, 0, b);
            long at = b;
            var moved = new Dictionary<int, (long Off, int Size)>();
            foreach (int i in order)
            {
                var r = rows[i];
                outp.Write(toc, (int)at, (int)((long)r.Off - at));              // what lies between
                long off = outp.Position;
                if (fresh.TryGetValue(i, out var u)) outp.Write(u, 0, u.Length);
                else outp.Write(toc, (int)r.Off, (int)r.Size);
                moved[i] = (off, fresh.TryGetValue(i, out var u2) ? u2.Length : (int)r.Size);
                at = (long)r.Off + r.Size;
            }
            outp.Write(toc, (int)at, (int)(toc.Length - at));                  // the tail
            var o = outp.ToArray();
            foreach (var kv in moved)
            {
                int row = rows[kv.Key].At;
                Array.Copy(BitConverter.GetBytes((ulong)kv.Value.Off), 0, o, row + 16, 8);
                Array.Copy(BitConverter.GetBytes((uint)kv.Value.Size), 0, o, row + 56, 4);
            }
            return o;
        }
    }
}

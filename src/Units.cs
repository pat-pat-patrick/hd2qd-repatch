using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Hd2Qd.Repatch
{
    /// <summary>A model (unit) resource's bytes, as hd2-repatcher (RaidingForPants, MIT) and
    /// hd2-mod-crates (eigeen, MIT or Apache-2.0) read them: the format version at 0x2C; the
    /// detail-level (LOD) group from the offset at 0x30 to the one at 0x34, which opens a table
    /// of 16 offsets into the unit; the vertex layout list's offset at 0x5C.</summary>
    static class UnitBytes
    {
        public const int Version = 0x2C, LodStart = 0x30, Table = 0x34, TableCount = 16, Layouts = 0x5C;
        /// <summary>The version before which vertex formats were numbered 4 lower (hd2-mod-crates:
        /// CURRENT_STREAM_FORMAT_VERSION), and the one before it.</summary>
        public const uint FormatsRenumbered = 10800438, BeforeRenumbering = 10800437;

        public static uint U32(byte[] b, int o) => BitConverter.ToUInt32(b, o);
        static void Put(byte[] b, int o, uint v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24); }

        static void Within(byte[] u, long at, long len, string what)
        {
            if (at < 0 || len < 0 || at + len > u.Length) throw new InvalidDataException(what + " lies outside the model's bytes");
        }

        /// <summary>The LOD group's range [start, end).</summary>
        public static (int Start, int End) Lod(byte[] u)
        {
            Within(u, Version, 12, "the model's header");
            uint s = U32(u, LodStart), e = U32(u, Table);
            if (e < s) throw new InvalidDataException("the model's detail-level group ends before it starts");
            Within(u, s, e - s, "the model's detail-level group");
            return ((int)s, (int)e);
        }

        public static byte[] LodBytes(byte[] u)
        {
            var (s, e) = Lod(u);
            var b = new byte[e - s];
            Buffer.BlockCopy(u, s, b, 0, b.Length);
            return b;
        }

        /// <summary>hd2-mod-crates upgraded_stream_format: the formats renumbered in 10800438;
        /// 0 to 4 kept as they are; any other one is not known, and the model is refused.</summary>
        static uint? Renumbered(uint f)
        {
            switch (f)
            {
                case 20: return 24;
                case 24: return 28;
                case 25: return 29;
                case 26: return 30;
                case 29: return 33;
                case 31: return 35;
                default: return f <= 4 ? f : (uint?)null;
            }
        }

        /// <summary>Renumbers the vertex formats of every layout (16 components of 20 bytes from
        /// 8, the count as a u64 after them): how many changed.</summary>
        static int RenumberFormats(byte[] u)
        {
            Within(u, Layouts, 4, "the vertex layout list's offset");
            int list = (int)U32(u, Layouts);
            if (list == 0) return 0;
            Within(u, list, 4, "the vertex layout list");
            int n = (int)U32(u, list);
            Within(u, list + 4, (long)n * 4, "the vertex layout offsets");
            int changed = 0;
            for (int i = 0; i < n; i++)
            {
                long rec = (long)list + U32(u, list + 4 + i * 4);
                Within(u, rec + 8, 16 * 20 + 8, "a vertex layout");
                ulong count = BitConverter.ToUInt64(u, (int)rec + 8 + 16 * 20);
                if (count > 16) throw new InvalidDataException("a vertex layout has " + count + " components, more than 16");
                for (int c = 0; c < (int)count; c++)
                {
                    int at = (int)rec + 8 + c * 20 + 4;
                    uint f = U32(u, at);
                    var to = Renumbered(f) ?? throw new InvalidDataException("vertex format " + f + " is not one this plugin knows how to update");
                    if (to != f) { Put(u, at, to); changed++; }
                }
            }
            return changed;
        }

        /// <summary>What a mod's model needs against the game's copy (version, LOD group).</summary>
        public static Need Judge(uint mine, uint game) => mine < game ? Need.Update : mine > game ? Need.Newer : Need.None;

        /// <summary>The model brought up to the game's: its version, its vertex formats when it
        /// crosses 10800438, and the game's LOD group, except across 10800437 -> 10800438, where
        /// hd2-mod-crates verified that the mod's own group is right (the formats changed, not
        /// the detail levels). Null when it is already current; throws when it cannot be updated
        /// (newer than the game, or a layout it does not know). The input is not changed.</summary>
        public static byte[] Update(byte[] unit, uint gameVersion, byte[] gameLod, out int formats, out bool lod)
        {
            formats = 0; lod = false;
            Within(unit, Version, 12, "the model's header");
            uint v = U32(unit, Version);
            if (v > gameVersion) throw new InvalidDataException("the model's version " + v + " is newer than the game's " + gameVersion);
            if (v == gameVersion) return null;
            var u = (byte[])unit.Clone();
            if (v < FormatsRenumbered && gameVersion >= FormatsRenumbered) formats = RenumberFormats(u);
            bool keepLod = v >= BeforeRenumbering && gameVersion == FormatsRenumbered;
            if (!keepLod) u = ReplaceLod(u, gameLod, out lod);
            Put(u, Version, gameVersion);
            return u;
        }

        /// <summary>The unit with another LOD group: the offsets of the table that point past the
        /// group's start move by the change in size (hd2-repatcher update_patch_file).</summary>
        static byte[] ReplaceLod(byte[] u, byte[] lodGroup, out bool changed)
        {
            var (s, e) = Lod(u);
            Within(u, Table, TableCount * 4, "the model's offset table");
            changed = e - s != lodGroup.Length || !u.Skip(s).Take(e - s).SequenceEqual(lodGroup);
            if (!changed) return u;
            int delta = lodGroup.Length - (e - s);
            var o = new byte[u.Length + delta];
            Buffer.BlockCopy(u, 0, o, 0, s);
            Buffer.BlockCopy(lodGroup, 0, o, s, lodGroup.Length);
            Buffer.BlockCopy(u, e, o, s + lodGroup.Length, u.Length - e);
            for (int i = 0; i < TableCount; i++)
            {
                int at = Table + i * 4;
                uint x = U32(o, at);
                if (x == 0 || x <= (uint)s) continue;
                long moved = (long)x + delta;
                if (moved < 0 || moved > uint.MaxValue) throw new InvalidDataException("an offset of the model moves out of range");
                Put(o, at, (uint)moved);
            }
            return o;
        }
    }

    enum Need { None, Update, Newer }

    /// <summary>The game's models: each one's version and LOD group, by id, read from the game's
    /// packages (the first package that holds a model, as the game's own scan does), and kept in
    /// the manager's settings folder while bundles.nxa is unchanged.</summary>
    sealed class GameUnits
    {
        public const ulong Unit = 0xE0A48D0BE9A7453F;
        const string CacheName = "repatch_game_units.bin";
        const uint CacheMagic = 0x31504552;          // "REP1"

        public readonly Dictionary<ulong, (uint Version, byte[] Lod)> Units = new Dictionary<ulong, (uint, byte[])>();
        public readonly HashSet<ulong> Unreadable = new HashSet<ulong>();

        /// <summary>The version most of the game's models carry.</summary>
        public uint Latest => Units.Count == 0 ? 0 : Units.Values.GroupBy(u => u.Version).OrderByDescending(g => g.Count()).First().Key;

        static readonly object gate = new object();
        static (long Stamp, string Data, GameUnits Units) held;

        static long Stamp(string data)
        {
            var index = new FileInfo(Path.Combine(data, "bundles.nxa"));
            if (!index.Exists) throw new FileNotFoundException("bundles.nxa is not in " + data + ": this plugin reads the game as it has been packed since 2025");
            return index.Length ^ index.LastWriteTimeUtc.Ticks;
        }

        /// <summary>Only what is cheap: held in memory, or from the cache file; else null.</summary>
        public static GameUnits Cached(string home, string data)
        {
            if (data == null) return null;
            long stamp;
            try { stamp = Stamp(data); } catch (IOException) { return null; }
            lock (gate)
            {
                if (held.Units != null && held.Stamp == stamp && held.Data == data) return held.Units;
                var c = ReadCache(Path.Combine(home, CacheName), stamp);
                if (c != null) held = (stamp, data, c);
                return c;
            }
        }

        /// <summary>The game's models: from memory or the cache, else read from the game (seconds).</summary>
        public static GameUnits Load(string home, string data, Action<double> progress = null)
        {
            var c = Cached(home, data);
            if (c != null) return c;
            long stamp = Stamp(data);
            c = Read(data, progress);
            WriteCache(Path.Combine(home, CacheName), stamp, c);
            lock (gate) held = (stamp, data, c);
            return c;
        }

        /// <summary>Forgets what is held (tests).</summary>
        public static void Forget() { lock (gate) held = default; }

        static GameUnits Read(string data, Action<double> progress)
        {
            var c = new GameUnits();
            using (var game = new Game(data))
            {
                var names = game.Packages.Where(n => n.IndexOf('.') < 0).OrderBy(n => n, StringComparer.Ordinal).ToList();
                var rows = new List<(string Pkg, ulong Off, uint Size, ulong Fid)>();
                var seen = new HashSet<ulong>();
                for (int n = 0; n < names.Count; n++)
                {
                    if (progress != null && n % 100 == 0) progress(0.5 * n / names.Count);
                    var pkg = names[n];
                    var pre = game.Read(pkg, 0, 12);
                    if (pre.Length < 12) continue;
                    uint magic = Game.U32(pre, 0), tc = Game.U32(pre, 4), fc = Game.U32(pre, 8);
                    if (magic != 0xF0000011 || tc > 4096 || fc > 100000) continue;
                    int b = 0x48 + (int)tc * 32;
                    var head = game.Read(pkg, 0, b + (int)fc * 80);
                    for (int i = 0; i < fc; i++)
                    {
                        int o = b + i * 80;
                        if (o + 80 > head.Length) break;
                        if (Game.U64(head, o + 8) != Unit) continue;
                        ulong fid = Game.U64(head, o);
                        if (seen.Add(fid)) rows.Add((pkg, Game.U64(head, o + 16), Game.U32(head, o + 56), fid));
                    }
                }
                rows.Sort((x, y) => string.CompareOrdinal(x.Pkg, y.Pkg) != 0 ? string.CompareOrdinal(x.Pkg, y.Pkg) : x.Off.CompareTo(y.Off));
                for (int r = 0; r < rows.Count; r++)
                {
                    if (progress != null && r % 500 == 0) progress(0.5 + 0.5 * r / rows.Count);
                    var x = rows[r];
                    try
                    {
                        if (x.Size < UnitBytes.Table + 4) throw new InvalidDataException("too short");
                        var h = game.Read(x.Pkg, x.Off, UnitBytes.Table + 4);
                        uint s = UnitBytes.U32(h, UnitBytes.LodStart), e = UnitBytes.U32(h, UnitBytes.Table);
                        if (e < s || e > x.Size) throw new InvalidDataException("bad detail-level group");
                        var lod = game.Read(x.Pkg, x.Off + s, (int)(e - s));
                        if (lod.Length != e - s) throw new InvalidDataException("short read");
                        c.Units[x.Fid] = (UnitBytes.U32(h, UnitBytes.Version), lod);
                    }
                    catch (InvalidDataException) { c.Unreadable.Add(x.Fid); }
                }
            }
            progress?.Invoke(1);
            return c;
        }

        static GameUnits ReadCache(string path, long stamp)
        {
            try
            {
                if (!File.Exists(path)) return null;
                using (var r = new BinaryReader(File.OpenRead(path), Encoding.UTF8))
                {
                    if (r.ReadUInt32() != CacheMagic || r.ReadInt64() != stamp) return null;
                    var c = new GameUnits();
                    int n = r.ReadInt32();
                    for (int i = 0; i < n; i++)
                    {
                        ulong fid = r.ReadUInt64();
                        uint v = r.ReadUInt32();
                        c.Units[fid] = (v, r.ReadBytes(r.ReadInt32()));
                    }
                    n = r.ReadInt32();
                    for (int i = 0; i < n; i++) c.Unreadable.Add(r.ReadUInt64());
                    return c;
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return null; }
        }

        static void WriteCache(string path, long stamp, GameUnits c)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using (var w = new BinaryWriter(File.Create(path + ".tmp"), Encoding.UTF8))
                {
                    w.Write(CacheMagic);
                    w.Write(stamp);
                    w.Write(c.Units.Count);
                    foreach (var kv in c.Units) { w.Write(kv.Key); w.Write(kv.Value.Version); w.Write(kv.Value.Lod.Length); w.Write(kv.Value.Lod); }
                    w.Write(c.Unreadable.Count);
                    foreach (var x in c.Unreadable) w.Write(x);
                }
                if (File.Exists(path)) File.Delete(path);
                File.Move(path + ".tmp", path);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }      // read again next time
        }
    }
}

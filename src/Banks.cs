using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Hd2Qd.Repatch
{
    /// <summary>A Wwise bank, as a sound mod replaces it whole: chunks of tag and size, of which
    /// BKHD (its first number, exclusive-or'd with 2450635948, is the Wwise version), DIDX and
    /// DATA (the embedded media, each entry 12 bytes onto the DATA bytes) and HIRC (the objects;
    /// a Sound, type 2, carries its source's id at body+5 and, embedded, its mem size at body+13).
    /// Checked against real mods and the game's own banks on 2026-11-06.
    ///
    /// After a game update the game's banks are rebuilt: object ids move and entries are added,
    /// so the mod's whole replaced bank keeps the old structure and parts of the game go silent
    /// (issue #1, 2026-11-06). The repair follows the standalone audio-repair tools: the game's
    /// current HIRC is the base, the mod's media is put back where it replaces the game's, the
    /// mem sizes follow the media, and the result is read back before it is written. A bank that
    /// cannot be read with certainty is refused, and the game's own sounds are never merged away.</summary>
    static class Banks
    {
        /// <summary>The block a patch archive wraps a bank in: the magic, the bank's length and
        /// the file id (mods on 2026-11-06). The game's packages wrap their banks in another,
        /// opaque block, which only GameBanks reads.</summary>
        public const uint Block = 0x78762FD8;
        public const uint Bkhd = 0x44484B42;         // "BKHD", little-endian
        public const uint VersionKey = 2450635948, SoundGuard = 262145;

        /// <summary>The bank's bytes with the block taken off; had says whether there was one,
        /// so a repair can put a new bank back under the same form. A bank that starts with BKHD
        /// directly is taken as it is; anything else is refused.</summary>
        public static (byte[] Raw, bool HadBlock) Unwrap(byte[] resource, ulong fileId)
        {
            if (resource.Length >= 4 && BitConverter.ToUInt32(resource, 0) == Block)
            {
                if (resource.Length < 16) throw new InvalidDataException("the bank's block is too short");
                if (BitConverter.ToUInt32(resource, 4) != resource.Length - 16 || BitConverter.ToUInt64(resource, 8) != fileId)
                    throw new InvalidDataException("the bank's block does not match its bytes");
                return (resource.Skip(16).ToArray(), true);
            }
            if (resource.Length >= 4 && BitConverter.ToUInt32(resource, 0) == Bkhd) return (resource, false);
            throw new InvalidDataException("the bank's bytes begin with neither a block nor BKHD");
        }

        /// <summary>The resource's bytes with a new bank under the block it had.</summary>
        public static byte[] Wrap(byte[] raw, bool hadBlock, ulong fileId)
        {
            if (!hadBlock) return raw;
            var b = new byte[raw.Length + 16];
            BitConverter.GetBytes(Block).CopyTo(b, 0);
            BitConverter.GetBytes(raw.Length).CopyTo(b, 4);
            BitConverter.GetBytes(fileId).CopyTo(b, 8);
            raw.CopyTo(b, 16);
            return b;
        }

        public sealed class Sound
        {
            public uint Id, Source, MemSize;
            public int Kind;            // 0 embeds its media in the bank; streaming kinds do not
            public int MemAt;           // where the mem size sits in the HIRC chunk's bytes
        }

        /// <summary>A bank read once: its chunks, its embedded media, its HIRC entries and
        /// Sounds.</summary>
        public sealed class BankFile
        {
            public uint Version;
            public List<(string Tag, byte[] Bytes)> Chunks = new List<(string, byte[])>();
            public Dictionary<uint, byte[]> Media = new Dictionary<uint, byte[]>();
            public HashSet<uint> Entries = new HashSet<uint>();
            public List<Sound> Sounds = new List<Sound>();

            public byte[] Hirc => Chunks.First(c => c.Tag == "HIRC").Bytes;
        }

        /// <summary>Reads a bank's bytes; throws when they are not one this plugin can answer
        /// for (no BKHD, no HIRC, a chunk that runs past the end).</summary>
        public static BankFile Parse(byte[] raw)
        {
            var f = new BankFile();
            int at = 0;
            while (at < raw.Length)
            {
                if (at + 8 > raw.Length) throw new InvalidDataException("the bank ends inside a chunk header");
                var tag = Encoding.ASCII.GetString(raw, at, 4);
                if (!tag.All(c => c >= 32 && c < 127)) throw new InvalidDataException("the bank's chunk names are not text");
                int size = (int)BitConverter.ToUInt32(raw, at + 4);
                if (size < 0 || at + 8L + size > raw.Length) throw new InvalidDataException("a chunk of the bank runs past its end");
                f.Chunks.Add((tag, raw.Skip(at + 8).Take(size).ToArray()));
                at += 8 + size;
            }
            var bkhd = f.Chunks.FirstOrDefault(c => c.Tag == "BKHD");
            var hirc = f.Chunks.FirstOrDefault(c => c.Tag == "HIRC");
            var didx = f.Chunks.FirstOrDefault(c => c.Tag == "DIDX");
            var data = f.Chunks.FirstOrDefault(c => c.Tag == "DATA");
            if (bkhd.Bytes == null || bkhd.Bytes.Length < 4) throw new InvalidDataException("the bank has no BKHD chunk");
            if (hirc.Bytes == null) throw new InvalidDataException("the bank has no HIRC chunk");
            f.Version = BitConverter.ToUInt32(bkhd.Bytes, 0) ^ VersionKey;
            if (didx.Bytes != null && data.Bytes != null)
            {
                if (didx.Bytes.Length % 12 != 0) throw new InvalidDataException("the bank's media index is damaged");
                for (int i = 0; i < didx.Bytes.Length; i += 12)
                {
                    uint id = BitConverter.ToUInt32(didx.Bytes, i), off = BitConverter.ToUInt32(didx.Bytes, i + 4), len = BitConverter.ToUInt32(didx.Bytes, i + 8);
                    if (off + (long)len > data.Bytes.Length) throw new InvalidDataException("a media of the bank lies outside its DATA");
                    f.Media[id] = data.Bytes.Skip((int)off).Take((int)len).ToArray();
                }
            }
            int n = BitConverter.ToInt32(hirc.Bytes, 0);
            if (n < 0 || 4L + n * 5 > hirc.Bytes.Length) throw new InvalidDataException("the bank's HIRC header is damaged");
            int q = 4;
            for (int i = 0; i < n; i++)
            {
                if (q + 5 > hirc.Bytes.Length) throw new InvalidDataException("the bank's HIRC ends inside an entry");
                int size = (int)BitConverter.ToUInt32(hirc.Bytes, q + 1);
                if (size < 4 || q + 5L + size > hirc.Bytes.Length) throw new InvalidDataException("an entry of the bank's HIRC runs past its end");
                uint id = BitConverter.ToUInt32(hirc.Bytes, q + 5);
                f.Entries.Add(id);
                if (hirc.Bytes[q] == 2 && size >= 17 + 4)
                {
                    int body = q + 9;                                        // past the type, the size and the id
                    if (BitConverter.ToUInt32(hirc.Bytes, body) == SoundGuard)
                    {
                        var s = new Sound
                        {
                            Id = id,
                            Kind = hirc.Bytes[body + 4],
                            Source = BitConverter.ToUInt32(hirc.Bytes, body + 5),
                            MemSize = BitConverter.ToUInt32(hirc.Bytes, body + 13),
                            MemAt = body + 13,
                        };
                        f.Sounds.Add(s);
                    }
                }
                q += 5 + size;
            }
            return f;
        }

        /// <summary>What a mod's bank needs against the game's: a newer format version, or
        /// entries or media the game has added. A mem size out of step with its media is not
        /// staleness: the game's own banks carry mem sizes that differ from the media's length
        /// (five to eight sounds of one working mod's bank on 2026-11-06), so only the sounds
        /// whose media a repair actually replaces get their mem size re-written.</summary>
        public static Need Judge(BankFile mine, BankFile game)
        {
            if (mine.Version > game.Version) return Need.Newer;
            if (mine.Version < game.Version) return Need.Update;
            if (game.Entries.Any(id => !mine.Entries.Contains(id))) return Need.Update;
            if (game.Media.Keys.Any(id => !mine.Media.ContainsKey(id))) return Need.Update;
            return Need.None;
        }

        /// <summary>The game's bank with the mod's media where it replaces the game's: the game's
        /// BKHD, HIRC and other chunks, the media merged (a media only the mod has is dropped:
        /// the game's objects do not name it), the mem sizes of the sounds whose media changed
        /// following the media. Throws when the merged bank does not read back; the caller then
        /// puts the game's bank in its place. The input is not changed.</summary>
        public static byte[] Update(BankFile game, BankFile mine)
        {
            var media = new Dictionary<uint, byte[]>(game.Media);
            var changed = new HashSet<uint>();
            foreach (var kv in mine.Media)
                if (media.TryGetValue(kv.Key, out var have) && !have.SequenceEqual(kv.Value)) { media[kv.Key] = kv.Value; changed.Add(kv.Key); }

            var hirc = (byte[])game.Hirc.Clone();
            foreach (var s in game.Sounds.Where(s => s.Kind == 0 && changed.Contains(s.Source)))
                Put(hirc, s.MemAt, media[s.Source].Length);

            byte[] merged = null;                                        // the DIDX rebuild's DATA, for the DATA chunk
            using (var o = new MemoryStream())
            {
                foreach (var (tag, _) in game.Chunks)
                {
                    byte[] bytes;
                    if (tag == "DIDX") bytes = Didx(media, out merged);
                    else if (tag == "DATA") bytes = merged ?? new byte[0];
                    else if (tag == "HIRC") bytes = hirc;
                    else bytes = game.Chunks.First(c => c.Tag == tag).Bytes;
                    o.Write(Encoding.ASCII.GetBytes(tag), 0, 4);
                    o.Write(BitConverter.GetBytes(bytes.Length), 0, 4);
                    o.Write(bytes, 0, bytes.Length);
                }
                var raw = o.ToArray();
                var back = Parse(raw);
                if (back.Media.Count != media.Count
                    || back.Sounds.Any(s => s.Kind == 0 && (!back.Media.ContainsKey(s.Source)
                        || (changed.Contains(s.Source) && back.Media[s.Source].Length != s.MemSize))))
                    throw new InvalidDataException("the rebuilt bank does not read back as it was built");
                return raw;
            }
        }

        static void Put(byte[] b, int at, int v) { b[at] = (byte)v; b[at + 1] = (byte)(v >> 8); b[at + 2] = (byte)(v >> 16); b[at + 3] = (byte)(v >> 24); }

        /// <summary>The media index and bytes: ids ascending, each entry 16-byte aligned but the
        /// last, as the tools that build banks write them.</summary>
        static byte[] Didx(Dictionary<uint, byte[]> media, out byte[] data)
        {
            var didx = new List<byte>();
            var blob = new List<byte>();
            foreach (var id in media.Keys.OrderBy(id => id))
            {
                if (blob.Count % 16 != 0) blob.AddRange(new byte[16 - blob.Count % 16]);
                didx.AddRange(BitConverter.GetBytes(id));
                didx.AddRange(BitConverter.GetBytes((uint)blob.Count));
                didx.AddRange(BitConverter.GetBytes((uint)media[id].Length));
                blob.AddRange(media[id]);
            }
            data = blob.ToArray();
            return didx.ToArray();
        }

        /// <summary>One bank resource of a mod, brought up to the game's bank of the same id; null
        /// when it needs nothing. A bank the game does not have counts as added; one that cannot
        /// be read with certainty, or that is newer than the game, is refused; a rebuild that does
        /// not read back puts the game's own bank in its place, so at least the game's sounds play
        /// (the standalone tools' fallback), and says so in the result's notes.</summary>
        public static byte[] Repair(byte[] resource, ulong fileId, GameBanks game, Patches.Result res)
        {
            byte[] raw;
            bool hadBlock;
            try { (raw, hadBlock) = Unwrap(resource, fileId); }
            catch (InvalidDataException e) { res.Refused.Add(fileId.ToString("x16") + ": " + e.Message); return null; }
            if (!game.BankRows.ContainsKey(fileId)) { res.Added++; return null; }
            BankFile mine, official;
            try { mine = Parse(raw); }
            catch (InvalidDataException e) { res.Refused.Add(fileId.ToString("x16") + ": " + e.Message); return null; }
            official = game.OfficialBank(fileId);
            if (official == null) { res.Refused.Add(fileId.ToString("x16") + ": the game's own bank cannot be read"); return null; }
            var need = Judge(mine, official);
            if (need == Need.None) return null;
            if (need == Need.Newer) { res.Refused.Add(fileId.ToString("x16") + ": the bank was made for a newer version of the game"); return null; }
            try
            {
                var fresh = Wrap(Update(official, mine), hadBlock, fileId);
                res.Banks++;
                return fresh;
            }
            catch (InvalidDataException)
            {
                var officialRaw = game.ReadBank(fileId);
                if (officialRaw == null) { res.Refused.Add(fileId.ToString("x16") + ": the rebuild did not verify and the game's bank cannot be read either"); return null; }
                res.Notes.Add(fileId.ToString("x16") + ": the rebuild did not verify; the game's own bank was put in its place");
                res.Banks++;
                return Wrap(officialRaw, hadBlock, fileId);
            }
        }
    }

    /// <summary>The game's banks, their names and strings tables, by id, read from the game's
    /// packages (the rows only; the bytes on demand) and kept in the manager's settings folder
    /// while bundles.nxa is unchanged. The DEP resource names a bank (a length and UTF-8 text;
    /// its first number is not one value across tools, so it is not checked).</summary>
    sealed class GameBanks : IDisposable
    {
        public const ulong Bank = 0x535A7BD3E650D799, Dep = 0xAF32095C82F2B070, Table = 0x0D972BAB10B40FD3;
        const string CacheName = "repatch_game_banks.bin";
        const uint CacheMagic = 0x32504552;          // "REP2"

        public readonly Dictionary<ulong, (string Pkg, ulong Off, uint Size)> BankRows = new Dictionary<ulong, (string, ulong, uint)>();
        public readonly Dictionary<ulong, string> Names = new Dictionary<ulong, string>();
        public readonly Dictionary<ulong, (string Pkg, ulong Off, uint Size)> Tables = new Dictionary<ulong, (string, ulong, uint)>();

        Game game;
        string data;                                               // for the body reads, opened on demand
        readonly Dictionary<ulong, Banks.BankFile> bankInfo = new Dictionary<ulong, Banks.BankFile>();
        readonly Dictionary<ulong, Subtitles.Table> tableInfo = new Dictionary<ulong, Subtitles.Table>();
        readonly List<ulong> bankOrder = new List<ulong>();          // oldest first, to drop when the cache is full
        readonly HashSet<ulong> unreadable = new HashSet<ulong>();
        int infoBytes;

        /// <summary>A mod's archives hold a bank or a strings table, so a repair of it needs this
        /// index (the headers only, cheap).</summary>
        public static bool Needed(string modFolder)
        {
            foreach (var f in Patches.Files(modFolder))
            {
                try
                {
                    using (var s = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096))
                    {
                        var head = new byte[72];
                        if (s.Read(head, 0, 72) < 72 || BitConverter.ToUInt32(head, 0) != 0xF0000011) continue;
                        uint tc = BitConverter.ToUInt32(head, 4), fc = BitConverter.ToUInt32(head, 8);
                        if (tc > 4096 || fc > 1000000) continue;
                        var types = new byte[tc * 32];
                        if (s.Read(types, 0, types.Length) < types.Length) continue;
                        for (int t = 0; t < tc; t++)
                        {
                            ulong id = BitConverter.ToUInt64(types, t * 32 + 8);
                            if (id == Bank || id == Table) return true;
                        }
                    }
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
            }
            return false;
        }

        static readonly object gate = new object();
        static (long Stamp, string Data, GameBanks Held) held;

        /// <summary>Only what is cheap: held in memory, or from the cache file; else null.</summary>
        public static GameBanks Cached(string home, string data)
        {
            if (data == null) return null;
            long stamp;
            try { stamp = GameUnits.Stamp(data); } catch (IOException) { return null; }
            lock (gate)
            {
                if (held.Held != null && held.Stamp == stamp && held.Data == data) return held.Held;
                var c = ReadCache(Path.Combine(home, CacheName), stamp);
                if (c != null) { c.data = data; held = (stamp, data, c); }
                return c;
            }
        }

        /// <summary>The game's banks: from memory or the cache, else read from the game (seconds,
        /// the package walk the models do too).</summary>
        public static GameBanks Load(string home, string data, Action<double> progress = null)
        {
            var c = Cached(home, data);
            if (c != null) return c;
            long stamp = GameUnits.Stamp(data);
            c = Read(data, progress);
            WriteCache(Path.Combine(home, CacheName), stamp, c);
            lock (gate)
            {
                if (held.Held != null && !ReferenceEquals(held.Held, c)) held.Held.Dispose();     // the game changed under it
                held = (stamp, data, c);
            }
            return c;
        }

        /// <summary>Forgets what is held (tests).</summary>
        public static void Forget()
        {
            lock (gate)
            {
                held.Held?.Dispose();
                held = default;
            }
        }

        public void Dispose() => game?.Dispose();

        static GameBanks Read(string data, Action<double> progress)
        {
            var c = new GameBanks { game = new Game(data), data = data };
            var names = c.game.Packages.Where(n => n.IndexOf('.') < 0).OrderBy(n => n, StringComparer.Ordinal).ToList();
            for (int n = 0; n < names.Count; n++)
            {
                if (progress != null && n % 100 == 0) progress(1.0 * n / names.Count);
                var pkg = names[n];
                var pre = c.game.Read(pkg, 0, 12);
                if (pre.Length < 12) continue;
                uint magic = Game.U32(pre, 0), tc = Game.U32(pre, 4), fc = Game.U32(pre, 8);
                if (magic != 0xF0000011 || tc > 4096 || fc > 100000) continue;
                int b = 0x48 + (int)tc * 32;
                var head = c.game.Read(pkg, 0, b + (int)fc * 80);
                for (int i = 0; i < fc; i++)
                {
                    int o = b + i * 80;
                    if (o + 80 > head.Length) break;
                    ulong fid = Game.U64(head, o), ty = Game.U64(head, o + 8);
                    ulong off = Game.U64(head, o + 16);
                    uint size = Game.U32(head, o + 56);
                    if (ty == Bank && !c.BankRows.ContainsKey(fid)) c.BankRows[fid] = (pkg, off, size);
                    else if (ty == Table && !c.Tables.ContainsKey(fid)) c.Tables[fid] = (pkg, off, size);
                    else if (ty == Dep && !c.Names.ContainsKey(fid))
                    {
                        var d = c.game.Read(pkg, off, (int)Math.Min(size, 4096));
                        if (d.Length >= 8)
                        {
                            int len = (int)Math.Min(BitConverter.ToUInt32(d, 4), d.Length - 8);
                            c.Names[fid] = Encoding.UTF8.GetString(d, 8, len).TrimEnd('\0');
                        }
                    }
                }
            }
            progress?.Invoke(1);
            return c;
        }

        /// <summary>The game's bank's bytes, its own block taken off (the game wraps a bank in an
        /// opaque block of 16 bytes; BKHD follows). Null when it cannot be read with certainty.</summary>
        public byte[] ReadBank(ulong fid)
        {
            if (!BankRows.TryGetValue(fid, out var row)) return null;
            try
            {
                if (game == null) game = new Game(data);               // a cache from the file: open on demand
                var resource = game.Read(row.Pkg, row.Off, (int)row.Size);
                if (resource.Length < 20 || BitConverter.ToUInt32(resource, 16) != Banks.Bkhd) return null;
                return resource.Skip(16).ToArray();
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return null; }
        }

        /// <summary>The game's bank, read once (its media and entries, for the compare). Null when
        /// it cannot be read; remembered as unreadable so a damaged bank is not read twice.</summary>
        public Banks.BankFile OfficialBank(ulong fid)
        {
            if (bankInfo.TryGetValue(fid, out var f)) return f;
            if (unreadable.Contains(fid)) return null;
            f = null;
            try { f = Banks.Parse(ReadBank(fid) ?? throw new InvalidDataException("the game's bank is not in its package")); }
            catch (InvalidDataException) { unreadable.Add(fid); return null; }
            if (f.Media.Values.Sum(m => m.Length) > BankCacheLimit / 2) return f;          // a huge bank: read again next time
            if (infoBytes > BankCacheLimit) Drop();
            bankInfo[fid] = f;
            bankOrder.Add(fid);
            infoBytes += f.Media.Values.Sum(m => m.Length) + f.Entries.Count * 8 + 256;
            return f;
        }

        const int BankCacheLimit = 128 * 1024 * 1024;

        void Drop()
        {
            while (infoBytes > BankCacheLimit / 2 && bankOrder.Count > 1)
            {
                var old = bankOrder[0];
                bankOrder.RemoveAt(0);
                if (bankInfo.TryGetValue(old, out var f))
                {
                    infoBytes -= f.Media.Values.Sum(m => m.Length) + f.Entries.Count * 8 + 256;
                    bankInfo.Remove(old);
                }
            }
        }

        /// <summary>The game's strings table, read once. Null when it cannot be read.</summary>
        public Subtitles.Table OfficialTable(ulong fid)
        {
            if (tableInfo.TryGetValue(fid, out var t)) return t;
            if (!Tables.TryGetValue(fid, out var row)) return null;
            try
            {
                if (game == null) game = new Game(data);
                t = Subtitles.Parse(game.Read(row.Pkg, row.Off, (int)row.Size));
            }
            catch (Exception e) when (e is InvalidDataException || e is IOException) { return null; }
            tableInfo[fid] = t;
            return t;
        }

        static GameBanks ReadCache(string path, long stamp)
        {
            try
            {
                if (!File.Exists(path)) return null;
                using (var r = new BinaryReader(File.OpenRead(path), Encoding.UTF8))
                {
                    if (r.ReadUInt32() != CacheMagic || r.ReadInt64() != stamp) return null;
                    var c = new GameBanks();
                    int n = r.ReadInt32();
                    for (int i = 0; i < n; i++) c.BankRows[r.ReadUInt64()] = (r.ReadString(), r.ReadUInt64(), r.ReadUInt32());
                    n = r.ReadInt32();
                    for (int i = 0; i < n; i++) c.Names[r.ReadUInt64()] = r.ReadString();
                    n = r.ReadInt32();
                    for (int i = 0; i < n; i++) c.Tables[r.ReadUInt64()] = (r.ReadString(), r.ReadUInt64(), r.ReadUInt32());
                    return c;
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return null; }
        }

        static void WriteCache(string path, long stamp, GameBanks c)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using (var w = new BinaryWriter(File.Create(path + ".tmp"), Encoding.UTF8))
                {
                    w.Write(CacheMagic);
                    w.Write(stamp);
                    w.Write(c.BankRows.Count);
                    foreach (var kv in c.BankRows) { w.Write(kv.Key); w.Write(kv.Value.Pkg); w.Write(kv.Value.Off); w.Write(kv.Value.Size); }
                    w.Write(c.Names.Count);
                    foreach (var kv in c.Names) { w.Write(kv.Key); w.Write(kv.Value); }
                    w.Write(c.Tables.Count);
                    foreach (var kv in c.Tables) { w.Write(kv.Key); w.Write(kv.Value.Pkg); w.Write(kv.Value.Off); w.Write(kv.Value.Size); }
                }
                if (File.Exists(path)) File.Delete(path);
                File.Move(path + ".tmp", path);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }      // read again next time
        }
    }
}

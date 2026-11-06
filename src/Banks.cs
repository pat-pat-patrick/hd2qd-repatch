using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

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
    /// cannot be read with certainty is refused, and the game's own sounds are never merged away.
    ///
    /// A bank is read two ways: whole (Parse), for a repair, and light (Info), for a scan, which
    /// keeps only the ids and sizes and no bytes — a scan of a library reads hundreds of banks,
    /// and copying every media of every one of them made the first check take minutes
    /// (the user, 2026-11-06: the window took very long to list what needed a repair).</summary>
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
                return (Slice(resource, 16, resource.Length - 16), true);
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

        /// <summary>A bank read once: its version, its chunks, its HIRC entries, its Sounds, and
        /// where its media sit in the bank's own bytes. The media are slices, not copies: a scan
        /// reads hundreds of banks and only compares them; a repair takes bytes out with
        /// MediaBytes and Hirc. Raw is the bank they slice into, and is dropped by a light read.
        /// UnknownSounds counts the Sounds of a layout this plugin does not read; a bank that
        /// has one is never rewritten, because a media swap could not keep its mem size true.</summary>
        public sealed class BankFile
        {
            public uint Version;
            public HashSet<uint> Entries = new HashSet<uint>();
            public List<Sound> Sounds = new List<Sound>();
            public List<(string Tag, int At, int Len)> Chunks = new List<(string, int, int)>();
            public Dictionary<uint, (int Off, int Len)> Media = new Dictionary<uint, (int, int)>();
            public byte[] Raw;
            public int HircAt, HircLen;
            public int UnknownSounds;

            public byte[] MediaBytes(uint id) { var m = Media[id]; return Banks.Slice(Raw, m.Off, m.Len); }
            public byte[] Hirc() => Banks.Slice(Raw, HircAt, HircLen);
            public byte[] Slice(int at, int len) => Banks.Slice(Raw, at, len);
        }

        internal static byte[] Slice(byte[] raw, int at, int len)
        {
            if (raw == null) throw new InvalidDataException("the bank was read lightly; its bytes are gone");
            var b = new byte[len];
            Buffer.BlockCopy(raw, at, b, 0, len);
            return b;
        }

        /// <summary>Two byte ranges, without copying either.</summary>
        static bool Same(byte[] a, int at, byte[] b)
        {
            if (a.Length - at != b.Length) return false;
            for (int i = 0; i < b.Length; i++) if (a[at + i] != b[i]) return false;
            return true;
        }

        /// <summary>Reads a bank whole; throws when the bytes are not one this plugin can answer
        /// for (no BKHD, no HIRC, a chunk that runs past the end).</summary>
        public static BankFile Parse(byte[] raw) => Read(raw, true);

        /// <summary>Reads a bank for a scan: the same checks, but the bank's bytes are dropped
        /// once the ids and sizes are out.</summary>
        public static BankFile Info(byte[] raw) => Read(raw, false);

        static BankFile Read(byte[] raw, bool keep)
        {
            var f = new BankFile { Raw = keep ? raw : null };
            int at = 0, didxAt = -1, didxLen = 0, dataAt = -1, dataLen = 0;
            while (at < raw.Length)
            {
                if (at + 8 > raw.Length) throw new InvalidDataException("the bank ends inside a chunk header");
                var tag = Encoding.ASCII.GetString(raw, at, 4);
                if (!tag.All(c => c >= 32 && c < 127)) throw new InvalidDataException("the bank's chunk names are not text");
                int size = (int)BitConverter.ToUInt32(raw, at + 4);
                if (size < 0 || at + 8L + size > raw.Length) throw new InvalidDataException("a chunk of the bank runs past its end");
                f.Chunks.Add((tag, at + 8, size));
                if (tag == "BKHD") f.Version = BitConverter.ToUInt32(raw, at + 8) ^ VersionKey;
                else if (tag == "HIRC") { f.HircAt = at + 8; f.HircLen = size; }
                else if (tag == "DIDX") { didxAt = at + 8; didxLen = size; }
                else if (tag == "DATA") { dataAt = at + 8; dataLen = size; }
                at += 8 + size;
            }
            if (f.Version == 0 || f.HircLen == 0) throw new InvalidDataException("the bank has no BKHD or HIRC chunk");
            if (didxAt >= 0 || dataAt >= 0)
            {
                if (didxAt < 0 || dataAt < 0 || didxLen % 12 != 0) throw new InvalidDataException("the bank's media index is damaged");
                for (int i = 0; i < didxLen; i += 12)
                {
                    uint id = BitConverter.ToUInt32(raw, didxAt + i), off = BitConverter.ToUInt32(raw, didxAt + i + 4), len = BitConverter.ToUInt32(raw, didxAt + i + 8);
                    if (off + (long)len > dataLen) throw new InvalidDataException("a media of the bank lies outside its DATA");
                    f.Media[id] = (dataAt + (int)off, (int)len);
                }
            }
            if (f.HircLen < 4) throw new InvalidDataException("the bank's HIRC header is damaged");
            int n = BitConverter.ToInt32(raw, f.HircAt);
            if (n < 0 || 4L + n * 5 > f.HircLen) throw new InvalidDataException("the bank's HIRC header is damaged");
            int q = f.HircAt + 4;
            for (int i = 0; i < n; i++)
            {
                if (q + 5 > raw.Length) throw new InvalidDataException("the bank's HIRC ends inside an entry");
                int size = (int)BitConverter.ToUInt32(raw, q + 1);
                if (size < 4 || q + 5L + size > raw.Length) throw new InvalidDataException("an entry of the bank's HIRC runs past its end");
                uint id = BitConverter.ToUInt32(raw, q + 5);
                f.Entries.Add(id);
                if (raw[q] == 2)
                {
                    int body = q + 9;                                        // past the type, the size and the id
                    if (size >= 21 && BitConverter.ToUInt32(raw, body) == SoundGuard)
                        f.Sounds.Add(new Sound
                        {
                            Id = id,
                            Kind = raw[body + 4],
                            Source = BitConverter.ToUInt32(raw, body + 5),
                            MemSize = BitConverter.ToUInt32(raw, body + 13),
                            MemAt = body + 13 - f.HircAt,                    // where it sits in the HIRC chunk's bytes
                        });
                    else f.UnknownSounds++;                                  // a Sound of a layout this plugin does not read
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
        /// BKHD, HIRC and other chunks verbatim, the media merged (a media only the mod has is
        /// dropped: the game's objects do not name it), the mem sizes of the sounds whose media
        /// changed following the media. Throws when the merged bank does not read back; the
        /// caller then puts the game's bank in its place. Neither input is changed.</summary>
        public static byte[] Update(BankFile game, BankFile mine)
        {
            if (game.UnknownSounds > 0 || mine.UnknownSounds > 0)
                throw new InvalidDataException("the bank has a Sound of a layout this plugin does not know, so its media are left alone");
            var changed = new Dictionary<uint, byte[]>();
            foreach (var kv in mine.Media)
                if (game.Media.TryGetValue(kv.Key, out var g))
                {
                    var bytes = mine.MediaBytes(kv.Key);
                    if (!Same(game.Raw, g.Off, bytes)) changed[kv.Key] = bytes;
                }

            var hirc = game.Hirc();
            foreach (var s in game.Sounds.Where(s => s.Kind == 0 && changed.ContainsKey(s.Source)))
                Put(hirc, s.MemAt, changed[s.Source].Length);

            byte[] data = null;                                          // the rebuilt DIDX's DATA, for the DATA chunk
            using (var o = new MemoryStream())
            {
                foreach (var (tag, at, len) in game.Chunks)
                {
                    byte[] bytes;
                    if (tag == "DIDX") bytes = Didx(game, changed, out data);
                    else if (tag == "DATA") bytes = data;
                    else if (tag == "HIRC") bytes = hirc;
                    else bytes = game.Slice(at, len);
                    o.Write(Encoding.ASCII.GetBytes(tag), 0, 4);
                    o.Write(BitConverter.GetBytes(bytes.Length), 0, 4);
                    o.Write(bytes, 0, bytes.Length);
                }
                var raw = o.ToArray();
                var back = Info(raw);
                if (back.Media.Count != game.Media.Count
                    || back.Sounds.Any(s => s.Kind == 0 && (!back.Media.ContainsKey(s.Source)
                        || (changed.ContainsKey(s.Source) && back.Media[s.Source].Len != s.MemSize))))
                    throw new InvalidDataException("the rebuilt bank does not read back as it was built");
                return raw;
            }
        }

        static void Put(byte[] b, int at, int v) { b[at] = (byte)v; b[at + 1] = (byte)(v >> 8); b[at + 2] = (byte)(v >> 16); b[at + 3] = (byte)(v >> 24); }

        /// <summary>The media index and bytes: ids ascending, each entry 16-byte aligned but the
        /// last, as the tools that build banks write them. The game's own media are sliced
        /// straight out of its bank; only the mod's changed ones are copied.</summary>
        static byte[] Didx(BankFile game, Dictionary<uint, byte[]> changed, out byte[] data)
        {
            var didx = new MemoryStream();
            var blob = new MemoryStream();
            foreach (var id in game.Media.Keys.OrderBy(id => id))
            {
                if (blob.Length % 16 != 0) blob.Write(new byte[16 - blob.Length % 16], 0, (int)(16 - blob.Length % 16));
                didx.Write(BitConverter.GetBytes(id), 0, 4);
                didx.Write(BitConverter.GetBytes((uint)blob.Length), 0, 4);
                var bytes = changed.TryGetValue(id, out var c) ? c : game.Slice(game.Media[id].Off, game.Media[id].Len);
                didx.Write(BitConverter.GetBytes((uint)bytes.Length), 0, 4);
                blob.Write(bytes, 0, bytes.Length);
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
            if (official.UnknownSounds > 0 || mine.UnknownSounds > 0)
            { res.Refused.Add(fileId.ToString("x16") + ": the bank has a Sound of a layout this plugin does not know, so it is left as it is"); return null; }
            var officialRaw = game.ReadBank(fileId);
            if (officialRaw == null) { res.Refused.Add(fileId.ToString("x16") + ": the game's own bank cannot be read"); return null; }
            try
            {
                var fresh = Wrap(Update(Parse(officialRaw), mine), hadBlock, fileId);
                res.Banks++;
                return fresh;
            }
            catch (InvalidDataException)
            {
                res.Notes.Add(fileId.ToString("x16") + ": the rebuild did not verify; the game's own bank was put in its place");
                res.Banks++;
                return Wrap(officialRaw, hadBlock, fileId);
            }
        }
    }

    /// <summary>The game's banks, their names and strings tables, by id, read from the game's
    /// packages (the rows only; the bytes on demand) and kept in the manager's settings folder
    /// while bundles.nxa is unchanged. The DEP resource names a bank (a length and UTF-8 text;
    /// its first number is not one value across tools, so it is not checked).
    ///
    /// The reads come from a small pool of package readers: the game's archives are not safe to
    /// read from two threads at once, and the scan of the library's mods runs on several.</summary>
    sealed class GameBanks : IDisposable
    {
        public const ulong Bank = 0x535A7BD3E650D799, Dep = 0xAF32095C82F2B070, Table = 0x0D972BAB10B40FD3;
        const string CacheName = "repatch_game_banks.bin";
        const uint CacheMagic = 0x32504552;          // "REP2"
        const int BankCacheLimit = 1024;             // light reads only: ids and sizes, a few KB each

        public readonly Dictionary<ulong, (string Pkg, ulong Off, uint Size)> BankRows = new Dictionary<ulong, (string, ulong, uint)>();
        public readonly Dictionary<ulong, string> Names = new Dictionary<ulong, string>();
        public readonly Dictionary<ulong, (string Pkg, ulong Off, uint Size)> Tables = new Dictionary<ulong, (string, ulong, uint)>();

        string data;
        readonly object poolGate = new object(), infoGate = new object();
        readonly Stack<Game> readers = new Stack<Game>();
        readonly SemaphoreSlim readersAt = new SemaphoreSlim(Readers, Readers);   // at most a few readers: each
        readonly Dictionary<ulong, Banks.BankFile> bankInfo = new Dictionary<ulong, Banks.BankFile>();   // holds the game's ~15 MB package index
        readonly Dictionary<ulong, Subtitles.Table> tableInfo = new Dictionary<ulong, Subtitles.Table>();
        readonly List<ulong> bankOrder = new List<ulong>();          // oldest first, to drop when the cache is full
        readonly List<ulong> tableOrder = new List<ulong>();
        readonly HashSet<ulong> unreadable = new HashSet<ulong>();
        Game game;                                                   // the walk's own reader, opened on demand

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

        static readonly object gate = new object(), loadGate = new object();
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

        /// <summary>The game's banks: from memory or the cache, else read from the game. Several
        /// scans of the library can want it at once; only the first walks.</summary>
        public static GameBanks Load(string home, string data, Action<double> progress = null)
        {
            var c = Cached(home, data);
            if (c != null) return c;
            lock (loadGate)
            {
                c = Cached(home, data);
                if (c != null) return c;
                long stamp = GameUnits.Stamp(data);
                c = Read(data, progress);
                WriteCache(Path.Combine(home, CacheName), stamp, c);
                lock (gate)
                {
                    // the old index is not disposed here: a check or repair that started before
                    // the game changed may still hold it, and its reads then report unreadable
                    // for that round instead of failing; the streams close when it is collected
                    held = (stamp, data, c);
                }
                return c;
            }
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

        public void Dispose()
        {
            game?.Dispose();
            lock (poolGate)
            {
                while (readers.Count > 0) readers.Pop().Dispose();
            }
        }

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

        /// <summary>How many package readers the pool keeps: each holds the game's package index
        /// (about 15 MB, measured 2026-11-06), so a handful, not one per thread.</summary>
        internal static int Readers => Math.Max(2, Math.Min(4, Environment.ProcessorCount));

        /// <summary>A package reader: waits for one of the pool's slots, then takes a pooled
        /// reader or builds one. The caller puts it back; nobody waits forever.</summary>
        Game Rent()
        {
            readersAt.Wait();
            try
            {
                lock (poolGate)
                {
                    if (readers.Count > 0) return readers.Pop();
                }
                return new Game(data);
            }
            catch { readersAt.Release(); throw; }
        }

        void Free(Game g)
        {
            lock (poolGate)
            {
                readers.Push(g);
            }
            readersAt.Release();
        }

        /// <summary>The game's bank's bytes, its own block taken off (the game wraps a bank in an
        /// opaque block of 16 bytes; BKHD follows). Null when it cannot be read with certainty —
        /// including after the game changed under this index and its readers were let go.</summary>
        public byte[] ReadBank(ulong fid)
        {
            if (!BankRows.TryGetValue(fid, out var row)) return null;
            var g = Rent();
            try
            {
                var resource = g.Read(row.Pkg, row.Off, (int)row.Size);
                if (resource.Length < 20 || BitConverter.ToUInt32(resource, 16) != Banks.Bkhd) return null;
                return Banks.Slice(resource, 16, resource.Length - 16);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ObjectDisposedException) { return null; }
            finally { Free(g); }
        }

        /// <summary>The game's bank, read lightly and once (its ids and sizes, for the compare —
        /// no media bytes: a scan reads hundreds of banks). Null when it cannot be read;
        /// remembered as unreadable so a damaged bank is not read twice.</summary>
        public Banks.BankFile OfficialBank(ulong fid)
        {
            lock (infoGate)
            {
                if (bankInfo.TryGetValue(fid, out var f)) return f;
                if (unreadable.Contains(fid)) return null;
            }
            Banks.BankFile parsed;
            try { parsed = Banks.Info(ReadBank(fid) ?? throw new InvalidDataException("the game's bank is not in its package")); }
            catch (InvalidDataException) { parsed = null; }
            lock (infoGate)
            {
                if (parsed == null) { unreadable.Add(fid); return null; }
                if (bankInfo.Count >= BankCacheLimit && bankOrder.Count > 0)
                {
                    var old = bankOrder[0];
                    bankOrder.RemoveAt(0);
                    bankInfo.Remove(old);
                }
                bankInfo[fid] = parsed;
                bankOrder.Add(fid);
                return parsed;
            }
        }

        /// <summary>The game's strings table, read once and kept up to a few hundred (their texts
        /// are sizeable; the oldest makes room). Null when it cannot be read.</summary>
        public Subtitles.Table OfficialTable(ulong fid)
        {
            lock (infoGate)
            {
                if (tableInfo.TryGetValue(fid, out var t)) return t;
            }
            if (!Tables.TryGetValue(fid, out var row)) return null;
            Subtitles.Table t2 = null;
            var g = Rent();
            try
            {
                t2 = Subtitles.Parse(g.Read(row.Pkg, row.Off, (int)row.Size));
            }
            catch (Exception e) when (e is InvalidDataException || e is IOException || e is ObjectDisposedException) { }
            finally { Free(g); }
            if (t2 == null) return null;
            lock (infoGate)
            {
                if (tableInfo.TryGetValue(fid, out var t)) return t;          // another thread was first
                if (tableInfo.Count >= TableCacheLimit && tableOrder.Count > 0)
                {
                    var old = tableOrder[0];
                    tableOrder.RemoveAt(0);
                    tableInfo.Remove(old);
                }
                tableInfo[fid] = t2;
                tableOrder.Add(fid);
                return t2;
            }
        }

        const int TableCacheLimit = 256;

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

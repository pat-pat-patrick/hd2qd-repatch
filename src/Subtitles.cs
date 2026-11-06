using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Hd2Qd.Repatch
{
    /// <summary>A strings resource, the game's table of one language's text: the magic 0x3E85F3AE,
    /// a format version, the entry count, the language's hash, then the ids, the absolute offsets
    /// of the texts and one blob of null-terminated UTF-8. The same text may be stored once and
    /// pointed at twice (checked against the game's own tables on 2026-11-06). A subtitle mod
    /// replaces a whole table; after a game update the official table has entries the mod's copy
    /// lacks, and the new lines stay untranslated (issue #1, 2026-11-06).</summary>
    static class Subtitles
    {
        public const uint Magic = 0x3E85F3AE;

        /// <summary>One table, as its ids and each id's text bytes.</summary>
        public sealed class Table
        {
            public uint Version, Language;
            public uint[] Ids;
            public byte[][] Texts;

            public HashSet<uint> IdSet => ids ?? (ids = new HashSet<uint>(Ids));
            HashSet<uint> ids;
        }

        /// <summary>Reads a table; throws when the bytes are not one (a damaged resource, or a
        /// wrapper this plugin does not know).</summary>
        public static Table Parse(byte[] b)
        {
            if (b.Length < 12 || BitConverter.ToUInt32(b, 0) != Magic) throw new InvalidDataException("not a strings table");
            uint version = BitConverter.ToUInt32(b, 4), count = BitConverter.ToUInt32(b, 8);
            if (count > 1 << 20 || 12L + (count > 0 ? 4 : 0) + 8L * count > b.Length) throw new InvalidDataException("the strings table's header is damaged");
            var t = new Table { Version = version, Language = count > 0 ? BitConverter.ToUInt32(b, 12) : 0, Ids = new uint[count], Texts = new byte[count][] };
            int n = (int)count, at = 12 + (count > 0 ? 4 : 0);
            for (int i = 0; i < n; i++) t.Ids[i] = BitConverter.ToUInt32(b, at + 4 * i);
            int blob = at + 8 * n;                                       // the texts begin after the ids and the offsets
            for (int i = 0; i < n; i++)
            {
                uint off = BitConverter.ToUInt32(b, at + 4 * n + 4 * i);
                if (off < blob || off >= b.Length) throw new InvalidDataException("a text of the strings table lies outside the texts");
                int end = Array.IndexOf(b, (byte)0, (int)off);
                if (end < 0) throw new InvalidDataException("a text of the strings table does not end");
                t.Texts[i] = Slice(b, (int)off, end - (int)off);
            }
            return t;
        }

        /// <summary>What a mod's table needs against the game's (a newer format version, or
        /// entries the game has added). A table for another language than the game's needs
        /// nothing here: a merge would mix the two, so it is left as it is rather than flagged
        /// for a repair that could never take.</summary>
        public static Need Judge(Table mine, Table game)
        {
            if (mine.Language != game.Language) return Need.None;
            if (mine.Version > game.Version) return Need.Newer;
            if (mine.Version < game.Version || game.IdSet.Any(id => !mine.IdSet.Contains(id))) return Need.Update;
            return Need.None;
        }

        /// <summary>The game's table with the mod's texts over it: the game's entries keep their
        /// order and its version and language, the mod's texts replace the entries they know, its
        /// own entries are added at the end. A text is stored once even when several entries share
        /// it. Throws when the two tables are for different languages, where a merge would mix
        /// them; the input is not changed.</summary>
        public static byte[] Merge(Table game, Table mine)
        {
            if (game.Language != mine.Language) throw new InvalidDataException("the mod's table is for another language than the game's");
            var mineBy = ByText(mine);
            var gameBy = ByText(game);
            var ids = game.Ids.Concat(mineOnly(mine, game)).ToArray();
            var texts = new byte[ids.Length][];
            for (int i = 0; i < ids.Length; i++)
            {
                if (!mineBy.TryGetValue(ids[i], out var text) && !gameBy.TryGetValue(ids[i], out text))
                    throw new InvalidDataException("the game's table does not hold entry " + ids[i]);
                texts[i] = text;
            }
            return Write(game.Version, game.Language, ids, texts);
        }

        static IEnumerable<uint> mineOnly(Table mine, Table game) => mine.Ids.Where(id => !game.IdSet.Contains(id)).Distinct();

        static Dictionary<uint, byte[]> ByText(Table t)
        {
            var by = new Dictionary<uint, byte[]>(t.Ids.Length);
            for (int i = 0; i < t.Ids.Length; i++) by[t.Ids[i]] = t.Texts[i];
            return by;
        }

        /// <summary>The table's bytes: magic, version, count, language, ids, absolute offsets and
        /// the texts, each distinct one stored once.</summary>
        public static byte[] Write(uint version, uint language, uint[] ids, byte[][] texts)
        {
            if (ids.Length != texts.Length) throw new InvalidDataException("a table of " + ids.Length + " entries got " + texts.Length + " texts");
            var seen = new Dictionary<byte[], int>(ByteTextComparer.Default);   // a text -> its offset, by its bytes
            var blob = new List<byte>();
            var offs = new int[ids.Length];
            for (int i = 0; i < ids.Length; i++)
            {
                if (!seen.TryGetValue(texts[i], out int at))
                {
                    at = blob.Count; seen[texts[i]] = at;
                    blob.AddRange(texts[i]); blob.Add(0);
                }
                offs[i] = at;
            }
            using (var o = new MemoryStream())
            {
                int head = 16;
                o.Write(BitConverter.GetBytes(Magic), 0, 4);
                o.Write(BitConverter.GetBytes(version), 0, 4);
                o.Write(BitConverter.GetBytes((uint)ids.Length), 0, 4);
                if (ids.Length > 0) o.Write(BitConverter.GetBytes(language), 0, 4); else head = 12;
                for (int i = 0; i < ids.Length; i++) o.Write(BitConverter.GetBytes(ids[i]), 0, 4);
                for (int i = 0; i < ids.Length; i++) o.Write(BitConverter.GetBytes(head + 8 * ids.Length + offs[i]), 0, 4);
                o.Write(blob.ToArray(), 0, blob.Count);
                return o.ToArray();
            }
        }

        /// <summary>One strings resource of a mod, merged with the game's table of the same id;
        /// null when it needs nothing. A table the game does not have counts as added; one that
        /// cannot be read, that is newer than the game's, or that is for another language (which
        /// the judge leaves alone), is refused.</summary>
        public static byte[] Repair(byte[] resource, ulong fileId, GameBanks game, Patches.Result res)
        {
            Table mine;
            try { mine = Parse(resource); }
            catch (InvalidDataException e) { res.Refused.Add(fileId.ToString("x16") + ": " + e.Message); return null; }
            if (!game.Tables.ContainsKey(fileId)) { res.Added++; return null; }
            var official = game.OfficialTable(fileId);
            if (official == null) { res.Refused.Add(fileId.ToString("x16") + ": the game's own strings table cannot be read"); return null; }
            var need = Judge(mine, official);
            if (need == Need.None) return null;
            if (need == Need.Newer) { res.Refused.Add(fileId.ToString("x16") + ": the table was made for a newer version of the game"); return null; }
            byte[] fresh;
            try { fresh = Merge(official, mine); }
            catch (InvalidDataException e) { res.Refused.Add(fileId.ToString("x16") + ": " + e.Message); return null; }
            try
            {
                var back = Parse(fresh);
                var backBy = ByText(back);
                var mineBy = ByText(mine);
                if (back.Ids.Length != official.Ids.Length + mineOnly(mine, official).Count()
                    || back.Version != official.Version || back.Language != official.Language
                    || mine.Ids.Any(id => !backBy.TryGetValue(id, out var got) || !got.SequenceEqual(mineBy[id])))
                    throw new InvalidDataException("the merged table does not read back as it was built");
            }
            catch (InvalidDataException e) { res.Refused.Add(fileId.ToString("x16") + ": " + e.Message); return null; }
            res.Tables++;
            return fresh;
        }

        /// <summary>Texts by their bytes: two invalid-UTF-8 texts can decode to the same string,
        /// and one of them must not silently replace the other.</summary>
        sealed class ByteTextComparer : IEqualityComparer<byte[]>
        {
            public static readonly ByteTextComparer Default = new ByteTextComparer();
            public bool Equals(byte[] a, byte[] b) => a != null && b != null && a.Length == b.Length && a.SequenceEqual(b);
            public int GetHashCode(byte[] b) { unchecked { int h = 17; foreach (var x in b) h = h * 31 + x; return h; } }
        }

        static byte[] Slice(byte[] b, int at, int len)
        {
            var t = new byte[len];
            Buffer.BlockCopy(b, at, t, 0, len);
            return t;
        }
    }
}

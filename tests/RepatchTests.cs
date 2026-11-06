using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Hd2Qd.Repatch;
using Xunit;

namespace Hd2Qd.Repatch.Tests
{
    /// <summary>The plugin on a game data folder made here (bundles.nxa and bundles.00.nxa) and mods
    /// in a library made here. Checked against real files on 2026-11-03: the installed game's 8019
    /// models are all version 10800438, as are the models of the 94 mods of a real library; a
    /// February 2025 mod (version 1) changes in 12 bytes, its version and nine vertex formats,
    /// each renumbered as the same components are in its author's later release. The banks' and
    /// strings tables' layouts were checked against real mods and the game's own files on
    /// 2026-11-06 (Banks.cs, Subtitles.cs): a mod bank sits in the archive under a 16-byte block
    /// (magic, length, file id), the game's banks under an opaque one; a table's texts may share
    /// one stored copy.</summary>
    public class Repatch : IDisposable
    {
        readonly string Root = Path.Combine(Path.GetTempPath(), "hd2qd_rp_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        readonly string Home, Data, Lib;
        readonly List<Dictionary<string, object>> library = new List<Dictionary<string, object>>();
        const ulong Unit = 0xE0A48D0BE9A7453F, Material = 0xEAC0B497876ADEDF;
        const ulong U1 = 0x1111, U2 = 0x2222, New = 0x9999, M1 = 0x5555;
        const uint Now = 10800438, Before = 10800437;

        public Repatch()
        {
            Home = Path.Combine(Root, "home");
            Data = Path.Combine(Root, "game", "data");
            Lib = Path.Combine(Root, "library");
            Directory.CreateDirectory(Home);
            Directory.CreateDirectory(Data);
            GameUnits.Forget();
            GameBanks.Forget();
        }

        public void Dispose()
        {
            GameUnits.Forget();
            GameBanks.Forget();
            try { Directory.Delete(Root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        // -- models and archives ----------------------------------------------------------

        const int LodAt = 0x80, Layouts = 0x100, Size = 0x260;

        /// <summary>A model: its version at 0x2C, a LOD group of lod bytes at 0x80 (the offset at
        /// 0x34 its end), the layout list at 0x100 (the offset at 0x5C) with one layout of the
        /// given vertex formats, and an offset before the group (0x40) that must not move.</summary>
        static byte[] Model(uint version, byte[] lod, params uint[] formats)
        {
            var u = new byte[Size + lod.Length];
            BitConverter.GetBytes(version).CopyTo(u, 0x2C);
            BitConverter.GetBytes((uint)LodAt).CopyTo(u, 0x30);
            BitConverter.GetBytes((uint)(LodAt + lod.Length)).CopyTo(u, 0x34);
            BitConverter.GetBytes(0x20u).CopyTo(u, 0x40);
            int list = Layouts + lod.Length;
            BitConverter.GetBytes((uint)list).CopyTo(u, 0x5C);
            lod.CopyTo(u, LodAt);
            BitConverter.GetBytes(1u).CopyTo(u, list);
            BitConverter.GetBytes(8u).CopyTo(u, list + 4);
            int rec = list + 8;
            for (int c = 0; c < formats.Length; c++)
            {
                BitConverter.GetBytes((uint)c).CopyTo(u, rec + 8 + c * 20);
                BitConverter.GetBytes(formats[c]).CopyTo(u, rec + 8 + c * 20 + 4);
            }
            BitConverter.GetBytes((ulong)formats.Length).CopyTo(u, rec + 8 + 320);
            return u;
        }

        static byte[] Lod(int n, byte fill) => Enumerable.Repeat(fill, n).ToArray();
        static uint V(byte[] unit) => BitConverter.ToUInt32(unit, 0x2C);

        /// <summary>A patch archive of (id, type, main bytes), the bodies one after another.</summary>
        static byte[] Archive(params (ulong Id, ulong Type, byte[] Body)[] rs)
        {
            var types = rs.Select(r => r.Type).Distinct().ToList();
            var b = new List<byte>();
            var head = new byte[72];
            BitConverter.GetBytes(0xF0000011u).CopyTo(head, 0);
            BitConverter.GetBytes((uint)types.Count).CopyTo(head, 4);
            BitConverter.GetBytes((uint)rs.Length).CopyTo(head, 8);
            b.AddRange(head);
            foreach (var t in types)
            {
                var row = new byte[32];
                BitConverter.GetBytes(t).CopyTo(row, 8);
                BitConverter.GetBytes((ulong)rs.Count(r => r.Type == t)).CopyTo(row, 16);
                b.AddRange(row);
            }
            ulong at = (ulong)(72 + 32 * types.Count + 80 * rs.Length);
            foreach (var r in rs)
            {
                var row = new byte[80];
                BitConverter.GetBytes(r.Id).CopyTo(row, 0);
                BitConverter.GetBytes(r.Type).CopyTo(row, 8);
                BitConverter.GetBytes(at).CopyTo(row, 16);
                BitConverter.GetBytes((uint)r.Body.Length).CopyTo(row, 56);
                BitConverter.GetBytes(0xABCDu).CopyTo(row, 64);       // a GPU size: never changed
                b.AddRange(row);
                at += (ulong)r.Body.Length;
            }
            foreach (var r in rs) b.AddRange(r.Body);
            return b.ToArray();
        }

        /// <summary>A resource's main bytes, read back through the archive's row.</summary>
        static byte[] Body(byte[] archive, ulong id)
        {
            var row = Patches.Rows(archive).Single(r => r.Id == id);
            return archive.Skip((int)row.Off).Take((int)row.Size).ToArray();
        }

        // -- the game ---------------------------------------------------------------------

        static byte[] Dsar(byte[] data)
        {
            var b = new byte[0x40];
            Encoding.ASCII.GetBytes("DSAR").CopyTo(b, 0);
            BitConverter.GetBytes(1u).CopyTo(b, 8);
            BitConverter.GetBytes(0UL).CopyTo(b, 0x20);
            BitConverter.GetBytes(0x40UL).CopyTo(b, 0x28);
            BitConverter.GetBytes((uint)data.Length).CopyTo(b, 0x30);
            BitConverter.GetBytes((uint)data.Length).CopyTo(b, 0x34);
            b[0x38] = 2;
            return b.Concat(data).ToArray();
        }

        int stamp;

        /// <summary>The game's data folder with one package holding the given resources.</summary>
        void Game(params (ulong Id, byte[] Body)[] models) => GameRows(models.Select(m => (m.Id, Unit, m.Body)).ToArray());

        void GameRows(params (ulong Id, ulong Type, byte[] Body)[] rs)
        {
            var package = Archive(rs);
            File.WriteAllBytes(Path.Combine(Data, "bundles.00.nxa"), Dsar(package));
            var bc = new byte[0x18 + 0x18 + 64 + 0x10];
            BitConverter.GetBytes(1u).CopyTo(bc, 0x0C);
            BitConverter.GetBytes(1u).CopyTo(bc, 0x10);
            int noff = 0x30, ioff = 0x30 + 32;
            BitConverter.GetBytes((ulong)package.Length).CopyTo(bc, 0x18);
            BitConverter.GetBytes((uint)noff).CopyTo(bc, 0x18 + 8);
            BitConverter.GetBytes(1u).CopyTo(bc, 0x18 + 12);
            BitConverter.GetBytes((uint)ioff).CopyTo(bc, 0x18 + 16);
            Encoding.ASCII.GetBytes("9ba626afa44a3aa3").CopyTo(bc, noff);
            var nxa = Path.Combine(Data, "bundles.nxa");
            File.WriteAllBytes(nxa, Dsar(bc));
            File.SetLastWriteTimeUtc(nxa, DateTime.UtcNow.AddSeconds(stamp++));
            GameUnits.Forget();
            GameBanks.Forget();
        }

        static readonly byte[] GameLod = Lod(32, 0xEE);

        /// <summary>The game of now: U1 and U2 at 10800438 with a 32-byte LOD group.</summary>
        void GameNow() => Game((U1, Model(Now, GameLod, 2, 30)), (U2, Model(Now, GameLod, 2)));

        GameUnits Units() => GameUnits.Load(Home, Data);

        // -- the library and the host -----------------------------------------------------

        string AddMod(string name, params (string Rel, byte[] Bytes)[] files)
        {
            var dir = Path.Combine(Lib, name);
            foreach (var (rel, bytes) in files)
            {
                var p = Path.Combine(dir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(p));
                File.WriteAllBytes(p, bytes);
            }
            library.Add(new Dictionary<string, object> { ["name"] = name, ["path"] = dir, ["enabled"] = true, ["settings"] = new List<object>() });
            return dir;
        }

        Dictionary<string, object> Host() => new Dictionary<string, object>
        {
            ["api"] = 2L, ["home"] = Home, ["lang"] = "en", ["data"] = Data,
            ["library"] = (Func<List<Dictionary<string, object>>>)(() => library),
        };

        string Command(string name, params string[] args)
        {
            var o = new StringWriter();
            int code = Plugin.Run(name, args, o, Host());
            Assert.True(code == 0, o.ToString());
            return o.ToString();
        }

        /// <summary>The old mod of these tests: U1 at version 1 with a 16-byte LOD group and
        /// formats from before 10800438, then a material that must keep its bytes.</summary>
        static readonly byte[] OldU1 = Model(1, Lod(16, 0x11), 2, 26, 29, 31, 24);
        static readonly byte[] Mat = Enumerable.Range(0, 40).Select(i => (byte)i).ToArray();
        static byte[] OldArchive() => Archive((U1, Unit, OldU1), (M1, Material, Mat));

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CreateHardLink(string link, string existing, IntPtr security);

        // -- tests ------------------------------------------------------------------------

        [Fact]
        public void The_plugin_says_its_kind_names_and_commands()
        {
            Assert.Equal(2, Plugin.Api);
            Assert.Equal("window", Plugin.Kind);
            Assert.Equal("修复", Plugin.Title("zh-CN"));
            Assert.Equal("Repair", Plugin.Title("en"));
            Assert.Equal("Repair", Plugin.Title("de"));                           // no German file: English
            Assert.Equal("Repair", Plugin.Title(null));
            Assert.StartsWith("游戏更新后", Plugin.Summary("zh-CN"));
            Assert.StartsWith("After a game update", Plugin.Summary("pt-BR"));
            Assert.NotNull(typeof(Plugin).GetMethod("Open"));
            Assert.Equal(new[] { "repatch-check", "repatch", "repatch-undo" }, Plugin.Commands.Select(c => c[0]));
        }

        [Fact]
        public void Every_language_file_has_the_English_keys_and_no_others()
        {
            var asm = typeof(Plugin).Assembly;
            var files = asm.GetManifestResourceNames().Where(n => n.StartsWith("Lang.") && n.EndsWith(".json")).ToList();
            Assert.Contains("Lang.en.json", files);
            Assert.Contains("Lang.zh-CN.json", files);
            Dictionary<string, string> Keys(string name)
            {
                using (var r = new StreamReader(asm.GetManifestResourceStream(name)))
                    return new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string, string>>(r.ReadToEnd());
            }
            var en = Keys("Lang.en.json");
            foreach (var f in files.Where(f => f != "Lang.en.json"))
            {
                var t = Keys(f);
                Assert.Equal(new string[0], t.Keys.Where(k => !en.ContainsKey(k)).ToArray());                       // no key English lacks
                Assert.Equal(new string[0], en.Keys.Where(k => !k.EndsWith("_1") && !t.ContainsKey(k)).ToArray());  // every text but the forms for one
            }
        }

        [Fact]
        public void The_game_table_holds_each_models_version_and_LOD_group_until_the_game_changes()
        {
            GameNow();
            var g = Units();
            Assert.Equal(2, g.Units.Count);
            Assert.Equal(Now, g.Units[U1].Version);
            Assert.Equal(GameLod, g.Units[U1].Lod);
            Assert.Equal(Now, g.Latest);
            Assert.True(File.Exists(Path.Combine(Home, "repatch_game_units.bin")));
            GameUnits.Forget();
            Assert.NotNull(GameUnits.Cached(Home, Data));                   // from the file
            Game((U1, Model(Now + 1, GameLod, 2)));
            Assert.Null(GameUnits.Cached(Home, Data));                      // the game changed
            Assert.Equal(Now + 1, Units().Units[U1].Version);
        }

        [Fact]
        public void An_old_model_gets_the_games_version_formats_and_LOD_group_and_the_offsets_after_it_move()
        {
            var u = UnitBytes.Update(OldU1, Now, GameLod, out int formats, out bool lod);
            Assert.Equal(Now, V(u));
            Assert.Equal(4, formats);                                       // 26, 29, 31, 24; 2 stays
            Assert.True(lod);
            Assert.Equal(OldU1.Length + 16, u.Length);
            Assert.Equal((uint)(LodAt + 32), BitConverter.ToUInt32(u, 0x34)); // the group's end
            Assert.Equal(GameLod, UnitBytes.LodBytes(u));
            Assert.Equal(0x20u, BitConverter.ToUInt32(u, 0x40));             // before the group: kept
            int list = (int)BitConverter.ToUInt32(u, 0x5C);
            Assert.Equal(BitConverter.ToUInt32(OldU1, 0x5C) + 16, (uint)list);
            int rec = list + 8 + 8;
            Assert.Equal(new uint[] { 2, 30, 33, 35, 28 }, Enumerable.Range(0, 5).Select(c => BitConverter.ToUInt32(u, rec + c * 20 + 4)));
            Assert.Equal(1u, V(OldU1));                                     // the input is not changed
        }

        [Fact]
        public void From_10800437_only_the_formats_change_and_the_mods_LOD_group_stays()
        {
            var mine = Model(Before, Lod(16, 0x11), 2, 20);
            var u = UnitBytes.Update(mine, Now, GameLod, out int formats, out bool lod);
            Assert.False(lod);
            Assert.Equal(1, formats);
            Assert.Equal(mine.Length, u.Length);
            Assert.Equal(Lod(16, 0x11), UnitBytes.LodBytes(u));
        }

        [Fact]
        public void A_current_model_is_left_alone_even_when_its_LOD_group_differs_from_the_games()
        {
            // 2026-11-03: 278 models of working mods at the game's version have a LOD group of
            // their own; a repair must not take it away
            Assert.Null(UnitBytes.Update(Model(Now, Lod(16, 0x11), 2, 30), Now, GameLod, out _, out _));
        }

        [Fact]
        public void A_model_newer_than_the_game_or_with_an_unknown_format_is_refused()
        {
            Assert.Throws<InvalidDataException>(() => UnitBytes.Update(Model(Now + 1, GameLod, 2), Now, GameLod, out _, out _));
            Assert.Throws<InvalidDataException>(() => UnitBytes.Update(Model(1, GameLod, 2, 17), Now, GameLod, out _, out _));
        }

        [Fact]
        public void In_an_archive_the_resources_after_a_grown_model_move_and_keep_their_bytes()
        {
            GameNow();
            var added = Model(1, Lod(8, 0x22), 2, 26);                       // a model the game does not have
            var a = Archive((U1, Unit, OldU1), (M1, Material, Mat), (New, Unit, added));
            var res = new Patches.Result();
            var b = Patches.Update(a, Units(), null, res);
            Assert.Equal(1, res.Updated);
            Assert.Equal(1, res.Added);
            Assert.Equal(a.Length + 16, b.Length);
            Assert.Equal(Mat, Body(b, M1));
            Assert.Equal(added, Body(b, New));                              // kept as it is
            Assert.Equal(Now, V(Body(b, U1)));
            var rows = Patches.Rows(b);
            Assert.All(rows, r => Assert.Equal(0xABCDu, BitConverter.ToUInt32(b, r.At + 64)));
            Assert.Null(Patches.Update(b, Units(), null, new Patches.Result()));   // nothing left to do
        }

        [Fact]
        public void A_damaged_archive_is_refused()
        {
            var a = OldArchive();
            var lie = (byte[])a.Clone();
            BitConverter.GetBytes(3UL).CopyTo(lie, 72 + 16);                  // the type table counts 3
            Assert.Throws<InvalidDataException>(() => Patches.Rows(lie));
            var twice = Archive((U1, Unit, OldU1), (U1, Unit, OldU1));
            Assert.Throws<InvalidDataException>(() => Patches.Rows(twice));
            var past = (byte[])a.Clone();
            BitConverter.GetBytes(100000u).CopyTo(past, 72 + 64 + 56);       // U1 runs past the end
            Assert.Throws<InvalidDataException>(() => Patches.Rows(past));
        }

        [Fact]
        public void A_repair_keeps_the_original_leaves_a_hard_link_as_it_was_and_can_be_undone()
        {
            GameNow();
            var dir = AddMod("Old", ("9ba626afa44a3aa3.patch_0", OldArchive()), ("Option\\9ba626afa44a3aa3.patch_1", OldArchive()),
                                    ("9ba626afa44a3aa3.patch_0.gpu_resources", new byte[] { 1, 2, 3 }));
            var main = Path.Combine(dir, "9ba626afa44a3aa3.patch_0");
            var deployed = Path.Combine(Root, "deployed.patch_0");
            Assert.True(CreateHardLink(deployed, main, IntPtr.Zero));

            var done = Repairs.Repair(Home, dir, Units(), Data);
            Assert.Equal(2, done.Files);
            Assert.Equal(2, done.Models);
            Assert.Equal(Now, V(Body(File.ReadAllBytes(main), U1)));
            Assert.Equal(OldArchive(), File.ReadAllBytes(deployed));        // the game's copy until the next deploy
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(main + ".gpu_resources"));
            Assert.Equal(OldArchive(), File.ReadAllBytes(Path.Combine(done.Backup, "files", "Option", "9ba626afa44a3aa3.patch_1")));
            Assert.Equal(0, Repairs.Scan(Home, "Old", dir, true, Units(), Data).Update);
            Assert.True(Repairs.Scan(Home, "Old", dir, true, Units(), Data).Undoable);

            var (restored, changed) = Repairs.Undo(Home, dir);
            Assert.Equal(2, restored);
            Assert.Empty(changed);
            Assert.Equal(OldArchive(), File.ReadAllBytes(main));
            Assert.False(Directory.Exists(done.Backup));
            Assert.False(Repairs.Scan(Home, "Old", dir, true, Units(), Data).Undoable);
        }

        [Fact]
        public void Undo_leaves_a_file_changed_after_the_repair()
        {
            GameNow();
            var dir = AddMod("Old", ("9ba626afa44a3aa3.patch_0", OldArchive()), ("9ba626afa44a3aa3.patch_1", OldArchive()));
            Repairs.Repair(Home, dir, Units(), Data);
            File.WriteAllBytes(Path.Combine(dir, "9ba626afa44a3aa3.patch_1"), new byte[] { 9 });
            var (restored, changed) = Repairs.Undo(Home, dir);
            Assert.Equal(1, restored);
            Assert.Equal(new[] { "9ba626afa44a3aa3.patch_1" }, changed);
            Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(Path.Combine(dir, "9ba626afa44a3aa3.patch_1")));
        }

        [Fact]
        public void A_current_mod_is_not_touched()
        {
            GameNow();
            var current = Archive((U1, Unit, Model(Now, Lod(16, 0x11), 2, 30)));
            var dir = AddMod("Current", ("9ba626afa44a3aa3.patch_0", current));
            var done = Repairs.Repair(Home, dir, Units(), Data);
            Assert.Equal(0, done.Files);
            Assert.Null(done.Backup);
            Assert.Equal(current, File.ReadAllBytes(Path.Combine(dir, "9ba626afa44a3aa3.patch_0")));
        }

        [Fact]
        public void The_row_note_waits_for_the_game_table_then_names_an_outdated_mod_until_it_is_repaired()
        {
            GameNow();
            var dir = AddMod("Old", ("9ba626afa44a3aa3.patch_0", OldArchive()));
            AddMod("Current", ("9ba626afa44a3aa3.patch_0", Archive((U1, Unit, Model(Now, GameLod, 2)))));
            Assert.Null(Plugin.ModNote(dir, Host()));                       // the game is not read yet
            Assert.True(Plugin.Start(Host()));
            Assert.False(Plugin.Start(Host()));                             // read once
            var note = Plugin.ModNote(dir, Host());
            Assert.Equal("Yellow", note["colour"]);
            Assert.Equal("Models outdated", note["text"]);
            Assert.Equal("1 of its models was made for an earlier version of the game and may not load. Repair them in the Repair plugin.", note["tip"]);
            Assert.False(note.ContainsKey("text_zh"));
            var zhHost = Host();
            zhHost["lang"] = "zh-CN";
            var zhNote = Plugin.ModNote(dir, zhHost);
            Assert.Equal("Yellow", zhNote["colour"]);
            Assert.Equal("模型需更新", zhNote["text"]);
            Assert.Equal("其中 1 个模型为旧版游戏制作，可能无法加载。可在“修复”插件中修复。", zhNote["tip"]);
            zhHost["lang"] = "zh-TW";                                       // no zh-TW file: by its language alone, zh: none, so English
            Assert.Equal("Models outdated", Plugin.ModNote(dir, zhHost)["text"]);
            Assert.Null(Plugin.ModNote(Path.Combine(Lib, "Current"), Host()));
            Repairs.Repair(Home, dir, Units(), Data);
            Assert.Null(Plugin.ModNote(dir, Host()));
        }

        [Fact]
        public void The_commands_check_repair_and_undo()
        {
            GameNow();
            AddMod("Old one", ("9ba626afa44a3aa3.patch_0", OldArchive()));
            AddMod("Old two", ("9ba626afa44a3aa3.patch_0", OldArchive()));
            AddMod("Current", ("9ba626afa44a3aa3.patch_0", Archive((U1, Unit, Model(Now, GameLod, 2)))));
            var said = Command("repatch-check");
            Assert.Contains("2 mods to repair; 1 mod with game resources current", said);
            said = Command("repatch", "--all");
            Assert.Contains("Old one: 1 model in 1 file repaired", said);
            Assert.Contains("Old two: 1 model in 1 file repaired", said);
            Assert.Contains("0 mods to repair", Command("repatch-check"));
            Assert.Contains("Old two: 1 file put back", Command("repatch-undo", "Old two"));
            Assert.Contains("1 mod to repair", Command("repatch-check"));
            var o = new StringWriter();
            Assert.Throws<InvalidOperationException>(() => Plugin.Run("repatch", new[] { "Old" }, o, Host()));   // names two mods
        }

        // -- sound banks and strings tables -----------------------------------------------

        const ulong B1 = 0x7777, T1 = 0x6666;
        const uint Wwise = 154;                                       // the game's banks' version on 2026-11-06
        const uint Lang = 0x5942CCF7;                                 // a table's language hash, as the game's are

        /// <summary>A bank: a BKHD of the version, a HIRC of one event and one Sound per given
        /// source (embedded unless kind says otherwise; its mem size right for the media unless
        /// given), and the media in DIDX/DATA.</summary>
        static byte[] Bank(uint version, (uint id, byte[] wem)[] media, (uint src, int kind, uint? mem)[] sounds, params uint[] events)
        {
            var bkhd = new byte[40];
            BitConverter.GetBytes(version ^ Banks.VersionKey).CopyTo(bkhd, 0);
            var hirc = new List<byte>();
            hirc.AddRange(BitConverter.GetBytes((uint)(events.Length + sounds.Length)));
            foreach (var e in events)
            {
                hirc.Add(3);
                hirc.AddRange(BitConverter.GetBytes(12u));
                hirc.AddRange(BitConverter.GetBytes(e));
                hirc.AddRange(new byte[8]);
            }
            foreach (var (src, kind, mem) in sounds)
            {
                var body = new byte[17];
                BitConverter.GetBytes(Banks.SoundGuard).CopyTo(body, 0);
                body[4] = (byte)kind;
                BitConverter.GetBytes(src).CopyTo(body, 5);
                BitConverter.GetBytes(mem ?? (uint)media.First(m => m.id == src).wem.Length).CopyTo(body, 13);
                hirc.Add(2);
                hirc.AddRange(BitConverter.GetBytes(4 + (uint)body.Length));
                hirc.AddRange(BitConverter.GetBytes(1000 + src));
                hirc.AddRange(body);
            }
            var didx = new List<byte>();
            var data = new List<byte>();
            foreach (var (id, wem) in media)
            {
                if (data.Count % 16 != 0) data.AddRange(new byte[16 - data.Count % 16]);
                didx.AddRange(BitConverter.GetBytes(id));
                didx.AddRange(BitConverter.GetBytes((uint)data.Count));
                didx.AddRange(BitConverter.GetBytes((uint)wem.Length));
                data.AddRange(wem);
            }
            var b = new List<byte>();
            void Chunk(string tag, byte[] bytes)
            {
                b.AddRange(Encoding.ASCII.GetBytes(tag));
                b.AddRange(BitConverter.GetBytes((uint)bytes.Length));
                b.AddRange(bytes);
            }
            Chunk("BKHD", bkhd);
            if (media.Length > 0) { Chunk("DIDX", didx.ToArray()); Chunk("DATA", data.ToArray()); }
            Chunk("HIRC", hirc.ToArray());
            return b.ToArray();
        }

        /// <summary>The game's form of a bank resource: an opaque block of 16 bytes, then BKHD.</summary>
        static byte[] GameBank(byte[] bank)
        {
            var b = new byte[16 + bank.Length];
            BitConverter.GetBytes(0x78A3F4A5u).CopyTo(b, 0);
            BitConverter.GetBytes((uint)bank.Length).CopyTo(b, 4);
            bank.CopyTo(b, 16);
            return b;
        }

        /// <summary>A mod's form: the block with the magic, the length and the file id.</summary>
        static byte[] ModBank(byte[] bank, ulong fid) => Banks.Wrap(bank, true, fid);

        static byte[] Dep(string name)
        {
            var n = Encoding.UTF8.GetBytes(name);
            var b = new byte[8 + n.Length + 1];
            BitConverter.GetBytes(0x78A3F4A5u).CopyTo(b, 0);
            BitConverter.GetBytes(n.Length + 1).CopyTo(b, 4);
            n.CopyTo(b, 8);
            return b;
        }

        static byte[] Table(uint version, uint lang, params (uint id, string text)[] entries) =>
            Subtitles.Write(version, lang, entries.Select(e => e.id).ToArray(), entries.Select(e => Encoding.UTF8.GetBytes(e.text)).ToArray());

        static byte[] Unwrap(byte[] resource, ulong fid) => Banks.Unwrap(resource, fid).Raw;

        static Banks.BankFile Parsed(byte[] resource, ulong fid) => Banks.Parse(Unwrap(resource, fid));

        /// <summary>The game of now: the models U1 and U2 at 10800438, the bank B1 (one event,
        /// one embedded Sound, one media) and the strings table T1 of three entries.</summary>
        readonly byte[] WemGame = Enumerable.Range(0, 48).Select(i => (byte)i).ToArray();
        readonly byte[] WemMod = Enumerable.Range(0, 64).Select(i => (byte)(i + 5)).ToArray();

        void GameNowWithAudio()
        {
            GameRows(
                (U1, Unit, Model(Now, GameLod, 2, 30)), (U2, Unit, Model(Now, GameLod, 2)),
                (B1, GameBanks.Bank, GameBank(Bank(Wwise, new[] { (7u, WemGame) }, new[] { (7u, 0, (uint?)null) }, 5))),
                (B1, GameBanks.Dep, Dep("content/audio/us/test")),      // a DEP carries its bank's id
                (T1, GameBanks.Table, Table(1, Lang, (1u, "a"), (2u, "b"), (3u, "c"))));
        }

        /// <summary>The mod of the audio tests: its bank is from before the game's (no event 5,
        /// its own media bytes), its table lacks the game's entry 3 and rewords the others.</summary>
        byte[] AudioArchive() => Archive(
            (B1, GameBanks.Bank, ModBank(Bank(Wwise - 1, new[] { (7u, WemMod) }, new[] { (7u, 0, (uint?)null) }), B1)),
            (T1, GameBanks.Table, Table(1, Lang, (1u, "A"), (2u, "B"))));

        [Fact]
        public void A_stale_bank_and_table_are_flagged_repaired_and_can_be_undone()
        {
            GameNowWithAudio();
            var dir = AddMod("Audio", ("9ba626afa44a3aa3.patch_0", AudioArchive()));
            var m = Repairs.Scan(Home, "Audio", dir, true, Units(), Data);
            Assert.Equal(1, m.Banks);
            Assert.Equal(1, m.BankUpdate);
            Assert.Equal(1, m.Tables);
            Assert.Equal(1, m.TableUpdate);

            var done = Repairs.Repair(Home, dir, Units(), Data);
            Assert.Equal(1, done.Files);
            Assert.Equal(1, done.Banks);
            Assert.Equal(1, done.Tables);
            var file = File.ReadAllBytes(Path.Combine(dir, "9ba626afa44a3aa3.patch_0"));
            var bank = Parsed(Body(file, B1), B1);
            Assert.Equal(Wwise, bank.Version);
            Assert.Contains(5u, bank.Entries);                          // the game's event is there
            Assert.Equal(WemMod, bank.MediaBytes(7u));                  // the mod's media is back
            var s = bank.Sounds.Single(x => x.Source == 7u);
            Assert.Equal((uint)WemMod.Length, s.MemSize);
            var table = Subtitles.Parse(Body(file, T1));
            Assert.Equal(1u, table.Version);                            // the game's version and language
            Assert.Equal(Lang, table.Language);
            Assert.Equal(new uint[] { 1, 2, 3 }, table.Ids);            // the game's entries in its order
            Assert.Equal("A", Encoding.UTF8.GetString(table.Texts[0]));
            Assert.Equal("B", Encoding.UTF8.GetString(table.Texts[1]));
            Assert.Equal("c", Encoding.UTF8.GetString(table.Texts[2])); // the game's own text for the new entry

            var after = Repairs.Scan(Home, "Audio", dir, true, Units(), Data);
            Assert.Equal(0, after.BankUpdate + after.TableUpdate);
            Assert.True(after.Undoable);
            var (restored, changed) = Repairs.Undo(Home, dir);
            Assert.Equal(1, restored);
            Assert.Empty(changed);
            Assert.Equal(AudioArchive(), File.ReadAllBytes(Path.Combine(dir, "9ba626afa44a3aa3.patch_0")));
        }

        [Fact]
        public void A_mem_size_out_of_step_with_its_media_is_not_staleness()
        {
            // 2026-11-06: the game's own banks carry mem sizes that differ from the media's
            // length (five to eight sounds of one working mod's bank), so this alone is not
            // a reason to touch a bank
            GameNowWithAudio();
            var wrong = Bank(Wwise, new[] { (7u, WemGame) }, new[] { (7u, 0, (uint?)(WemGame.Length + 5)) }, 5);
            var archive = Archive((B1, GameBanks.Bank, ModBank(wrong, B1)));
            var dir = AddMod("Wrong", ("9ba626afa44a3aa3.patch_0", archive));
            var m = Repairs.Scan(Home, "Wrong", dir, true, Units(), Data);
            Assert.Equal(0, m.BankUpdate);
            Assert.Empty(m.Errors);
            var done = Repairs.Repair(Home, dir, Units(), Data);
            Assert.Equal(0, done.Files);
            Assert.Equal(archive, File.ReadAllBytes(Path.Combine(dir, "9ba626afa44a3aa3.patch_0")));
        }

        [Fact]
        public void A_bank_newer_than_the_game_and_one_the_game_lacks_are_left_as_they_are()
        {
            GameNowWithAudio();
            var archive = Archive(
                (B1, GameBanks.Bank, ModBank(Bank(Wwise + 1, new[] { (7u, WemMod) }, new[] { (7u, 0, (uint?)null) }, 5), B1)),
                (0xABCD, GameBanks.Bank, ModBank(Bank(Wwise, new[] { (7u, WemMod) }, new[] { (7u, 0, (uint?)null) }, 5), 0xABCD)));
            var dir = AddMod("Newer", ("9ba626afa44a3aa3.patch_0", archive));
            var m = Repairs.Scan(Home, "Newer", dir, true, Units(), Data);
            Assert.Equal(1, m.BankNewer);
            Assert.Equal(1, m.Added);                                       // the mod's own bank, not an error
            Assert.Empty(m.Errors);
            var done = Repairs.Repair(Home, dir, Units(), Data);
            Assert.Equal(0, done.Files);
            Assert.Contains("newer version of the game", string.Join("\n", done.Refused));
            Assert.Equal(archive, File.ReadAllBytes(Path.Combine(dir, "9ba626afa44a3aa3.patch_0")));
        }

        [Fact]
        public void A_damaged_bank_is_refused_and_the_tables_of_its_archive_go_on()
        {
            GameNowWithAudio();
            var bad = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17 };
            var dir = AddMod("Mixed", ("9ba626afa44a3aa3.patch_0", Archive(
                (B1, GameBanks.Bank, ModBank(bad, B1)),
                (T1, GameBanks.Table, Table(1, Lang, (1u, "A"), (2u, "B"))))));
            var done = Repairs.Repair(Home, dir, Units(), Data);
            Assert.Equal(1, done.Files);
            Assert.Equal(1, done.Tables);
            Assert.Contains(done.Refused, r => r.Contains("chunk names are not text"));
            var file = File.ReadAllBytes(Path.Combine(dir, "9ba626afa44a3aa3.patch_0"));
            Assert.Equal(ModBank(bad, B1), Body(file, B1));             // left as it was
            Assert.Equal("c", Encoding.UTF8.GetString(Subtitles.Parse(Body(file, T1)).Texts[2]));
        }

        [Fact]
        public void A_table_of_another_language_is_left_alone_and_a_newer_one_is_refused()
        {
            GameNowWithAudio();
            var other = Archive((T1, GameBanks.Table, Table(1, 0x1234, (1u, "A"), (2u, "B"))));   // another language, entry 3 missing
            var newer = Archive((T1, GameBanks.Table, Table(2, Lang, (1u, "A"), (2u, "B"))));
            var dir = AddMod("Other", ("9ba626afa44a3aa3.patch_0", other), ("9ba626afa44a3aa3.patch_1", newer));
            var m = Repairs.Scan(Home, "Other", dir, true, Units(), Data);
            Assert.Equal(0, m.TableUpdate);                                 // a merge could never take: not flagged
            Assert.Empty(m.Errors);
            var done = Repairs.Repair(Home, dir, Units(), Data);
            Assert.Equal(0, done.Files);
            Assert.Equal(other, File.ReadAllBytes(Path.Combine(dir, "9ba626afa44a3aa3.patch_0")));
            Assert.Contains("newer version of the game", string.Join("\n", done.Refused));
        }

        [Fact]
        public void A_bank_with_a_sound_of_an_unknown_layout_is_left_alone()
        {
            // a Sound whose layout the plugin does not read: a media swap could not keep its
            // mem size true, so the bank is refused rather than half-repaired
            GameNowWithAudio();
            var officialRaw = Bank(Wwise, new[] { (7u, WemGame) }, new[] { (7u, 0, (uint?)null) }, 5);
            var g = BitConverter.GetBytes(Banks.SoundGuard);
            int at = officialRaw.Length - 4;
            while (at >= 0 && !(officialRaw[at] == g[0] && officialRaw[at + 1] == g[1] && officialRaw[at + 2] == g[2] && officialRaw[at + 3] == g[3])) at--;
            Assert.True(at >= 0);
            officialRaw[at] = 0x99;                                         // the guard is gone: an unknown layout
            GameRows((B1, GameBanks.Bank, GameBank(officialRaw)));
            var archive = Archive((B1, GameBanks.Bank, ModBank(Bank(Wwise - 1, new[] { (7u, WemMod) }, new[] { (7u, 0, (uint?)null) }), B1)));
            var dir = AddMod("Unknown", ("9ba626afa44a3aa3.patch_0", archive));
            var done = Repairs.Repair(Home, dir, Units(), Data);
            Assert.Equal(0, done.Files);
            Assert.Contains("does not know", string.Join("\n", done.Refused));
            Assert.Equal(archive, File.ReadAllBytes(Path.Combine(dir, "9ba626afa44a3aa3.patch_0")));
        }

        [Fact]
        public void A_bank_whose_HIRC_header_is_truncated_is_refused()
        {
            var b = new List<byte>();
            b.AddRange(Encoding.ASCII.GetBytes("BKHD"));
            b.AddRange(BitConverter.GetBytes(40u));
            b.AddRange(new byte[40]);
            b.AddRange(Encoding.ASCII.GetBytes("HIRC"));
            b.AddRange(BitConverter.GetBytes(2u));
            b.AddRange(new byte[] { 1, 0 });
            Assert.Throws<InvalidDataException>(() => Banks.Parse(b.ToArray()));
        }

        [Fact]
        public void A_table_whose_text_offset_points_into_its_header_is_refused()
        {
            var raw = Table(1, Lang, (1u, "a"), (2u, "b"));
            BitConverter.GetBytes(4u).CopyTo(raw, 24);                      // the first text's offset: into the header
            Assert.Throws<InvalidDataException>(() => Subtitles.Parse(raw));
        }

        [Fact]
        public void A_written_table_is_read_back_to_its_very_bytes_and_shares_repeated_texts()
        {
            var raw = Table(1, Lang, (1u, "dup"), (2u, "dup"), (3u, "z"));
            var t = Subtitles.Parse(raw);
            Assert.Equal(raw, Subtitles.Write(t.Version, t.Language, t.Ids, t.Texts));
            Assert.Equal(16 + 4 * 3 + 4 * 3 + 6, raw.Length);           // "dup" stored once: two texts, 6 bytes
            var merged = Subtitles.Merge(Subtitles.Parse(Table(2, Lang, (1u, "a"), (2u, "b"))), Subtitles.Parse(Table(1, Lang, (1u, "a"), (3u, "b"))));
            var back = Subtitles.Parse(merged);
            Assert.Equal(new uint[] { 1, 2, 3 }, back.Ids);
            Assert.Equal(new[] { "a", "b", "b" }, back.Texts.Select(x => Encoding.UTF8.GetString(x)));
        }

        [Fact]
        public void Two_checks_at_once_agree_and_do_not_trip_over_each_other()
        {
            // the window can be opened again while the first check is still running
            GameNowWithAudio();
            var dir = AddMod("Audio", ("9ba626afa44a3aa3.patch_0", AudioArchive()));
            var results = new Repairs.Mod[4];
            Parallel.For(0, 4, i => results[i] = Repairs.Scan(Home, "Audio", dir, true, Units(), Data));
            foreach (var m in results)
            {
                Assert.Equal(1, m.Banks);
                Assert.Equal(1, m.BankUpdate);
                Assert.Equal(1, m.Tables);
                Assert.Equal(1, m.TableUpdate);
                Assert.Empty(m.Errors);
            }
        }

        [Fact]
        public void The_commands_and_notes_name_the_kinds()
        {
            GameNowWithAudio();
            var dir = AddMod("Audio", ("9ba626afa44a3aa3.patch_0", AudioArchive()));
            Assert.True(Plugin.Start(Host()));                          // the game's models, for the note
            var note = Plugin.ModNote(dir, Host());
            Assert.Equal("Banks, Subtitles outdated", note["text"]);
            var zhHost = Host();
            zhHost["lang"] = "zh-CN";
            Assert.Equal("音库、字幕需更新", Plugin.ModNote(dir, zhHost)["text"]);
            var said = Command("repatch-check");
            Assert.Contains("(1 bank, 1 subtitle table)", said);
            said = Command("repatch", "Audio");
            Assert.Contains("1 bank and 1 subtitle table in 1 file repaired", said);
            Assert.Contains("0 mods to repair; 1 mod with game resources current", Command("repatch-check"));
        }

        [Fact]
        public void The_game_bank_index_is_kept_until_the_game_changes()
        {
            GameNowWithAudio();
            var g = GameBanks.Load(Home, Data);
            Assert.Equal(1, g.BankRows.Count);
            Assert.Equal(1, g.Tables.Count);
            Assert.Equal("content/audio/us/test", g.Names[B1]);
            Assert.True(File.Exists(Path.Combine(Home, "repatch_game_banks.bin")));
            GameBanks.Forget();
            Assert.NotNull(GameBanks.Cached(Home, Data));               // from the file
            Assert.NotNull(GameBanks.Cached(Home, Data).OfficialBank(B1));
            GameRows((B1, GameBanks.Bank, GameBank(Bank(Wwise, new[] { (7u, WemGame) }, new[] { (7u, 0, (uint?)null) }, 5))));
            Assert.Null(GameBanks.Cached(Home, Data));                  // the game changed
        }
    }
}

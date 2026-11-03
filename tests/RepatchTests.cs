using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Hd2Qd.Repatch;
using Xunit;

namespace Hd2Qd.Repatch.Tests
{
    /// <summary>The plugin on a game data folder made here (bundles.nxa and bundles.00.nxa) and mods
    /// in a library made here. Checked against real files on 2026-11-03: the installed game's 8019
    /// models are all version 10800438, as are the models of the 94 mods of a real library; a
    /// February 2025 mod (version 1) changes in 12 bytes, its version and nine vertex formats,
    /// each renumbered as the same components are in its author's later release.</summary>
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
        }

        public void Dispose()
        {
            GameUnits.Forget();
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

        /// <summary>The game's data folder with one package holding the given models.</summary>
        void Game(params (ulong Id, byte[] Body)[] models)
        {
            var package = Archive(models.Select(m => (m.Id, Unit, m.Body)).ToArray());
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
            Assert.Equal("模型修复", Plugin.Title("zh-CN"));
            Assert.Equal("Model repair", Plugin.Title("en"));
            Assert.Equal("Model repair", Plugin.Title("de"));                     // no German file: English
            Assert.Equal("Model repair", Plugin.Title(null));
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
            var b = Patches.Update(a, Units(), res);
            Assert.Equal(1, res.Updated);
            Assert.Equal(1, res.Added);
            Assert.Equal(a.Length + 16, b.Length);
            Assert.Equal(Mat, Body(b, M1));
            Assert.Equal(added, Body(b, New));                              // kept as it is
            Assert.Equal(Now, V(Body(b, U1)));
            var rows = Patches.Rows(b);
            Assert.All(rows, r => Assert.Equal(0xABCDu, BitConverter.ToUInt32(b, r.At + 64)));
            Assert.Null(Patches.Update(b, Units(), new Patches.Result()));   // nothing left to do
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

            var done = Repairs.Repair(Home, dir, Units());
            Assert.Equal(2, done.Files);
            Assert.Equal(2, done.Models);
            Assert.Equal(Now, V(Body(File.ReadAllBytes(main), U1)));
            Assert.Equal(OldArchive(), File.ReadAllBytes(deployed));        // the game's copy until the next deploy
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(main + ".gpu_resources"));
            Assert.Equal(OldArchive(), File.ReadAllBytes(Path.Combine(done.Backup, "files", "Option", "9ba626afa44a3aa3.patch_1")));
            Assert.Equal(0, Repairs.Scan(Home, "Old", dir, true, Units()).Update);
            Assert.True(Repairs.Scan(Home, "Old", dir, true, Units()).Undoable);

            var (restored, changed) = Repairs.Undo(Home, dir);
            Assert.Equal(2, restored);
            Assert.Empty(changed);
            Assert.Equal(OldArchive(), File.ReadAllBytes(main));
            Assert.False(Directory.Exists(done.Backup));
            Assert.False(Repairs.Scan(Home, "Old", dir, true, Units()).Undoable);
        }

        [Fact]
        public void Undo_leaves_a_file_changed_after_the_repair()
        {
            GameNow();
            var dir = AddMod("Old", ("9ba626afa44a3aa3.patch_0", OldArchive()), ("9ba626afa44a3aa3.patch_1", OldArchive()));
            Repairs.Repair(Home, dir, Units());
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
            var done = Repairs.Repair(Home, dir, Units());
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
            Assert.Equal("1 of its models was made for an earlier version of the game and may not load. Repair them in the Model repair plugin.", note["tip"]);
            Assert.False(note.ContainsKey("text_zh"));
            var zhHost = Host();
            zhHost["lang"] = "zh-CN";
            var zhNote = Plugin.ModNote(dir, zhHost);
            Assert.Equal("Yellow", zhNote["colour"]);
            Assert.Equal("模型需更新", zhNote["text"]);
            Assert.Equal("其中 1 个模型为旧版游戏制作，可能无法加载。可在“模型修复”插件中修复。", zhNote["tip"]);
            zhHost["lang"] = "zh-TW";                                       // no zh-TW file: by its language alone, zh: none, so English
            Assert.Equal("Models outdated", Plugin.ModNote(dir, zhHost)["text"]);
            Assert.Null(Plugin.ModNote(Path.Combine(Lib, "Current"), Host()));
            Repairs.Repair(Home, dir, Units());
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
            Assert.Contains("2 mods to repair; 1 mod with game models current", said);
            said = Command("repatch", "--all");
            Assert.Contains("Old one: 1 model in 1 file repaired", said);
            Assert.Contains("Old two: 1 model in 1 file repaired", said);
            Assert.Contains("0 mods to repair", Command("repatch-check"));
            Assert.Contains("Old two: 1 file put back", Command("repatch-undo", "Old two"));
            Assert.Contains("1 mod to repair", Command("repatch-check"));
            var o = new StringWriter();
            Assert.Throws<InvalidOperationException>(() => Plugin.Run("repatch", new[] { "Old" }, o, Host()));   // names two mods
        }
    }
}

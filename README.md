# HD2QD Mod Repair

A plugin of [HD2 Mod Quick Deploy](https://github.com/pat-pat-patrick/hd2-mod-cli). After a game
update, mods made for the earlier version may no longer load, or may leave parts of the game
without their sound or text. The plugin finds those mods in the library and brings three kinds of
resources up to the game's current form:

- **Models.** The model's format version; the vertex formats, which the game renumbered in
  version 10800438; the detail-level (LOD) group, taken from the game's own copy of the model —
  except across 10800437 → 10800438, where the mod's own group is correct.
- **Sound banks.** A mod replaces whole banks, and an update rebuilds the game's own (object ids
  move, entries are added). The repair rebuilds the mod's bank on the game's current one: the
  game's structure, with the mod's replaced sounds put back and their sizes synced, so the bank
  loads and the mod's sounds play. Only the bank in the patch's main file changes; media that
  streams from a `.stream` file stays as it is, and a bank whose sounds all stream (many voice
  packs) is brought up to the game's structure only.
- **Subtitles.** A text mod replaces a whole strings table, and an update adds entries to the
  game's. The repair merges the two: the game's current table as the base, the mod's texts over
  the entries they replace, the mod's own entries added. New lines stay translated, the game's
  new lines stay the game's. A table for another language than the game's is left alone.

Only the patch archive's main file changes; `.stream` and `.gpu_resources` files stay as they
are.

## What it does not change

- **Resources already at the game's version.** Even when they differ from the game's on purpose:
  many working mods change detail levels, mixes and texts of their own will.
- **Resources the game does not have.** A mod may add models, banks or tables of its own; they
  are kept.
- **Anything it cannot read with certainty.** A damaged archive, a resource newer than the game,
  a layout it does not know — reported and left as it is. And when a rebuilt bank does not read
  back the way it was built, the game's own bank is put in its place, so at least the game's
  sounds play; the repair says so.

## Safety

- The original of every changed file is kept in the manager's settings folder
  (`repatch\backups`), and every repair can be undone from the plugin's window.
- The plugin writes a new file and puts it in the old one's place. The deployed copy in the
  game folder is not touched; deploy afterwards to apply the repair in game.

## Use

1. In HD2 Mod Quick Deploy, open the Plugins page (the jigsaw piece beside the gear).
2. Choose **Add plugin…** and select `HD2QD.Repatch.dll`, or drop the file on the page.
3. A mod with outdated resources shows **Outdated** in the mod list, naming the kinds (models,
   banks, subtitles). Choose **Open** on the plugin's card, then **Repair** (or **Repair all**),
   and deploy.

On the command line: `hd2qd repatch-check`, `hd2qd repatch <mod>…` or `hd2qd repatch --all`, and
`hd2qd repatch-undo <mod>`.

## Build

`build.bat` runs the tests, then writes `release\HD2QD.Repatch.dll`. It needs the .NET SDK
8 or later (`dotnet` on PATH, or `DOTNET` set to its `dotnet.exe`). The plugin itself runs on
the .NET Framework 4.8 that Windows 10 (1903 and later) and Windows 11 include.

## Credits

Sound bank and subtitle repair: contributed by [x402](https://github.com/x402) ([#2](https://github.com/pat-pat-patrick/hd2qd-repatch/pull/2)).

The repair follows three earlier tools, written again here in C#:

- [hd2-repatcher](https://github.com/RaidingForPants/hd2-repatcher) by Evie / RaidingForPants
  (MIT License, © 2026 RaidingForPants; see `THIRD_PARTY_NOTICES.txt`): how a model's version and detail-level group are
  updated, and the offsets that move with them.
- [hd2-mod-crates](https://github.com/eigeen/hd2-mod-crates) by eigeen (MIT or Apache-2.0):
  the safer rules: no update to an older version, the 10800437 → 10800438 path that keeps the
  mod's detail levels, the table of renumbered vertex formats, and the checks of an archive
  before it is changed.
- **Sound Restoration Studio (音效维修台)** by [40Hour](https://space.bilibili.com/157681854) (Bilibili): the sound bank repair method comes from it —
  the game's current bank as the base, the mod's media back in, the mem sizes following the
  media, the result read back before it is written, and the game's own bank as the fallback.

---

# HD2QD MOD 修复

[HD2 Mod Quick Deploy](https://github.com/pat-pat-patrick/hd2-mod-cli) 的插件。游戏更新后，为旧版本
制作的 MOD 可能无法加载，或让游戏的部分内容失去声音与文字。本插件会找出 MOD 库中的此类 MOD，
并将三类资源更新为当前游戏的格式：

- **模型。** 模型的格式版本号；顶点格式（游戏在 10800438 版本中对其重新编号）；细节层级（LOD）
  组，取自游戏中该模型的原版数据——从 10800437 更新到 10800438 时除外，此时 MOD 自身的细节
  层级组是正确的。
- **音库。** 音效 MOD 整库替换官方音库，而游戏更新会重建官方音库（对象 ID 变动、条目新增）。
  修复以游戏当前的音库为底重建 MOD 的音库：保留游戏的结构，放回 MOD 替换过的声音并同步长度，
  使音库能够加载、MOD 的声音能够播放。补丁主文件里的音库会更新；从 `.stream` 流式读取的媒体
  保持不变，声音全部流式的音库（许多语音包）只更新到游戏的结构。
- **字幕。** 文本 MOD 整表替换 strings 表，而游戏更新会为官方表新增条目。修复将两者合并：以
  游戏当前的表为底，MOD 的文本覆盖其替换的条目，MOD 自己的条目追加在后。新内容保持有翻译，
  官方新增的行保持原样。与游戏语言不同的表不做合并。

修复只更改补丁的主文件；`.stream` 与 `.gpu_resources` 文件保持不变。

## 不会更改的内容

- **已是当前版本的资源。** 即使与原版不同也不会更改：许多正常工作的 MOD 会有意修改细节层级、
  混音与文本。
- **游戏中不存在的资源。** MOD 可能新增自己的模型、音库或字幕表，这些会原样保留。
- **无法可靠读取的内容。** 损坏的文件、比游戏版本更新的资源、未知的结构，均会列出并保持
  原样。重建后的音库若读不回所构建的结构，则会以游戏原版音库代替（至少保证游戏的声音），
  修复会注明这一点。

## 安全性

- 每个被更改文件的原文件都会保留在管理器的设置文件夹中（`repatch\backups`），每次修复均可在
  插件窗口中撤销。
- 插件会写入新文件并替换原文件，不会改动游戏目录中已部署的副本；修复后需重新部署，改动才会
  在游戏中生效。

## 使用

1. 在 HD2 Mod Quick Deploy 中打开“插件”页（齿轮旁的拼图按钮）。
2. 点击“添加插件…”，选择 `HD2QD.Repatch.dll`，或将该文件拖到插件页上。
3. 资源需要更新的 MOD 会在 MOD 列表中显示“需更新”，并注明类别（模型、音库、字幕）。在插件
   卡片上点击“打开”，再点击“修复”（或“全部修复”），然后重新部署。

命令行：`hd2qd repatch-check`、`hd2qd repatch <MOD>…` 或 `hd2qd repatch --all`，以及
`hd2qd repatch-undo <MOD>`。

## 构建

`build.bat` 先运行测试，再生成 `release\HD2QD.Repatch.dll`。构建需要 .NET SDK 8 或更高版本
（`dotnet` 在 PATH 中，或将 `DOTNET` 设为其 `dotnet.exe`）。插件本身运行于 Windows 10（1903
及以上）和 Windows 11 自带的 .NET Framework 4.8。

## 致谢

音库与字幕修复：由 [x402](https://github.com/x402) 贡献（[#2](https://github.com/pat-pat-patrick/hd2qd-repatch/pull/2)）。

修复方法沿用以下工具，并以 C# 重新实现：

- Evie / RaidingForPants 的 [hd2-repatcher](https://github.com/RaidingForPants/hd2-repatcher)
  （MIT 许可，© 2026 RaidingForPants；见 `THIRD_PARTY_NOTICES.txt`）：模型版本号与细节层级组的
  更新方法，以及随之移动的偏移量。
- eigeen 的 [hd2-mod-crates](https://github.com/eigeen/hd2-mod-crates)（MIT 或 Apache-2.0 许可）：
  更稳妥的规则，包括不降级版本、10800437 → 10800438 时保留 MOD 自身的细节层级、顶点格式的
  重新编号表，以及修改前对文件的检查。
- Bilibili UP 主 [40Hour](https://space.bilibili.com/157681854) 的 **音效维修台（Sound Restoration Studio）**：音库修复方法来自该工具——以游戏当前音库为底、
  放回 MOD 媒体、mem size 跟随媒体、写入前回读校验，以及以游戏原版音库兜底。

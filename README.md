# HD2QD Model Repair

A plugin of [HD2 Mod Quick Deploy](https://github.com/pat-pat-patrick/hd2-mod-cli). After a game
update, mod models made for the earlier version of the game may no longer load. The plugin finds
those mods in the library and brings their models up to the game's current format:

- the model's format version;
- the vertex formats, which the game renumbered in version 10800438;
- the detail-level (LOD) group, taken from the game's own copy of the model, except across
  10800437 → 10800438, where the mod's own group is correct.

Only the patch archive's main file changes. The vertex and index data (`.gpu_resources`,
`.stream`) stay as they are.

## What it does not change

- **Current models.** A model already at the game's version is left alone, even when its
  detail levels differ from the game's: many working mods change them on purpose.
- **Models the game does not have.** A mod may add models of its own; they are kept.
- **Anything it cannot read with certainty.** A damaged archive, a model newer than the game,
  or a vertex format it does not know is reported and left as it is.

## Safety

- The original of every changed file is kept in the manager's settings folder
  (`repatch\backups`), and every repair can be undone from the plugin's window.
- The plugin writes a new file and puts it in the old one's place. The deployed copy in the
  game folder is not touched; deploy afterwards to apply the repair in game.

## Use

1. In HD2 Mod Quick Deploy, open the Plugins page (the jigsaw piece beside the gear).
2. Choose **Add plugin…** and select `HD2QD.Repatch.dll`, or drop the file on the page.
3. A mod with outdated models shows **Models outdated** in the mod list. Choose **Open** on the
   plugin's card, then **Repair** (or **Repair all**), and deploy.

On the command line: `hd2qd repatch-check`, `hd2qd repatch <mod>…` or `hd2qd repatch --all`, and
`hd2qd repatch-undo <mod>`.

## Build

`build.bat` runs the tests, then writes `release\HD2QD.Repatch.dll`. It needs the .NET SDK
8 or later (`dotnet` on PATH, or `DOTNET` set to its `dotnet.exe`). The plugin itself runs on
the .NET Framework 4.8 that Windows 10 (1903 and later) and Windows 11 include.

## Credits

The repair follows two earlier tools, written again here in C#:

- [hd2-repatcher](https://github.com/RaidingForPants/hd2-repatcher) by Evie / RaidingForPants
  (MIT License, © 2026 RaidingForPants; see `THIRD_PARTY_NOTICES.txt`): how a model's version and detail-level group are
  updated, and the offsets that move with them.
- [hd2-mod-crates](https://github.com/eigeen/hd2-mod-crates) by eigeen (MIT or Apache-2.0):
  the safer rules: no update to an older version, the 10800437 → 10800438 path that keeps the
  mod's detail levels, the table of renumbered vertex formats, and the checks of an archive
  before it is changed.

---

# HD2QD 模型修复

[HD2 Mod Quick Deploy](https://github.com/pat-pat-patrick/hd2-mod-cli) 的插件。游戏更新后，为旧版本
制作的 MOD 模型可能无法加载。本插件会找出 MOD 库中的此类 MOD，并将其模型更新为当前游戏的格式：

- 模型的格式版本号；
- 顶点格式（游戏在 10800438 版本中对其重新编号）；
- 细节层级（LOD）组，取自游戏中该模型的原版数据；从 10800437 更新到 10800438 时除外，此时
  MOD 自身的细节层级组是正确的。

修复只更改 patch 文件的主文件，顶点与索引数据（`.gpu_resources`、`.stream`）保持不变。

## 不会更改的内容

- **已是当前版本的模型。** 即使其细节层级与原版不同也不会更改：许多正常工作的 MOD 会有意修改细节层级。
- **游戏中不存在的模型。** MOD 可能新增自己的模型，这些模型会原样保留。
- **无法可靠读取的内容。** 损坏的文件、比游戏版本更新的模型，以及未知的顶点格式，均会列出并保持原样。

## 安全性

- 每个被更改文件的原文件都会保留在管理器的设置文件夹中（`repatch\backups`），每次修复均可在插件窗口中撤销。
- 插件会写入新文件并替换原文件，不会改动游戏目录中已部署的副本；修复后需重新部署，改动才会在游戏中生效。

## 使用

1. 在 HD2 Mod Quick Deploy 中打开“插件”页（齿轮旁的拼图按钮）。
2. 点击“添加插件…”，选择 `HD2QD.Repatch.dll`，或将该文件拖到插件页上。
3. 模型需要更新的 MOD 会在 MOD 列表中显示“模型需更新”。在插件卡片上点击“打开”，再点击“修复”（或“全部修复”），然后重新部署。

命令行：`hd2qd repatch-check`、`hd2qd repatch <MOD>…` 或 `hd2qd repatch --all`，以及 `hd2qd repatch-undo <MOD>`。

## 构建

`build.bat` 先运行测试，再生成 `release\HD2QD.Repatch.dll`。构建需要 .NET SDK 8 或更高版本
（`dotnet` 在 PATH 中，或将 `DOTNET` 设为其 `dotnet.exe`）。插件本身运行于 Windows 10（1903
及以上）和 Windows 11 自带的 .NET Framework 4.8。

## 致谢

修复方法沿用以下两个工具，并以 C# 重新实现：

- Evie / RaidingForPants 的 [hd2-repatcher](https://github.com/RaidingForPants/hd2-repatcher)
  （MIT 许可，© 2026 RaidingForPants）：模型版本号与细节层级组的更新方法，以及随之移动的偏移量。
- eigeen 的 [hd2-mod-crates](https://github.com/eigeen/hd2-mod-crates)（MIT 或 Apache-2.0 许可）：
  更稳妥的规则，包括不降级版本、10800437 → 10800438 时保留 MOD 自身的细节层级、顶点格式的重新编号表，以及修改前对文件的检查。

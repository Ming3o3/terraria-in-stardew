# 泰拉瑞亚 × 星露谷

通过 SMAPI Mod、tModLoader Mod 和本机 Python bridge，在星露谷画面中显示泰拉瑞亚角色与快捷栏，并将已适配的物品动作作用于星露谷世界。两款游戏仍各自运行，使用本机端口交换画面、输入和物品状态。

## 源码目录

- `TerrariaInStardew/`：星露谷侧 Mod，负责画面合成、输入、物品交互及翻译。
- `TerrariaBridge/`：泰拉瑞亚侧 Mod，负责角色与物品数据、画面采集及库存校验。
- `bridge/`：Python 通信协调程序与协议测试，仅使用 Python 标准库。
- `docs/`：通信协议、实现方案及开发路线。
- 根目录的三个 `.sh` 文件：bridge 与两款游戏的启动入口。

## 本地环境与构建

当前联调环境为 macOS、Stardew Valley 1.6.15、SMAPI 4.5.2、tModLoader，以及 .NET SDK 8 和 Python 3。游戏本体、工具链、生成的 Mod 包、存档及外部参考仓库不包含在此仓库中。

星露谷项目使用 `Pathoschild.Stardew.ModBuildConfig` 恢复构建依赖、检测本机游戏安装并提供包括 `xTile.dll` 在内的游戏程序集引用，同时部署到本机 SMAPI Mods 目录。使用其他安装位置时，为构建传入 `-p:GamePath=/path/to/game`。

```sh
dotnet build TerrariaInStardew/TerrariaInStardew.csproj -c Release
dotnet build TerrariaBridge/TerrariaBridge.csproj -c Release \
  -p:TmlInstallDir="$HOME/Library/Application Support/Steam/steamapps/common/tModLoader"
```

本机已下载的 SDK 也可通过 `./.tools/dotnet/dotnet` 调用。启动脚本默认使用 macOS Steam 安装位置，可通过 `STARDEW_GAME_DIR` 和 `TMODLOADER_DIR` 覆盖。

启动与操作步骤见 [运行说明](RUN_GUIDE.md)。协议测试步骤见 [bridge 说明](bridge/README.md#smoke-test)；开发进度与实测边界见 [MODLOG.md](MODLOG.md)。macOS 融合模式需在 tModLoader 中通过“多人游戏 → 创建并游玩（Host & Play）”载入世界。

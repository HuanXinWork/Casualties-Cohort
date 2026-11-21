# Casualties: Cohort for Casualties: Unknown

> 《未知伤亡》（Casualties: Unknown `v1.0.2`）联机合作模组，[Casualties: Together](https://github.com/creaturefeaturelarry/casualties-together) 的模块化分支版本（当前协议基线：`v5.5.4`）
> 协议：LGPL-2.1，与上游一致

English guide: see [README.en.md](README.en.md)

## 简介

Casualties: Cohort 保留了上游的插件 GUID 与主程序集名（`CasualtiesMP.dll`），线上网络协议与 Casualties: Together `v5.5.4` 房间直接互通，现有配套模组（如 `SkinSync`、`CuSaveManager`）无需修改即可继续加载协同。

相比原版，本分支包含以下改动：

- **全量模块化架构**：自有源码单文件控制在 `<= 800` 行以内，清零 `Harmony003` 编译警告，职责边界清晰
- **显式跨模组扩展门面**：提供 `Together.Api`（面板输入门控、存档世界桥接、网络收发）与 `Together.ModHooks` 静态事件入口，外部模组无需反射内部私有实现
- **同步 `v5.5.4` 协议与新特性**：合入房主在线迁移（`HostTransfer`，房主离开时可将世界与存档状态移交其他玩家）、房间规则分类折叠/关键词搜索/重置、主菜单多语言切换页、5 语种（`EN` / `es-ES` / `ru-RU` / `zh-CN` / `zh-TW`）706 键本地化对齐、深层世界模拟窗口自适应、v4 布娃娃与抓手植物/水晶怪同步
- **双模诊断日志与单机直通守卫**：区分 Release / Debug 日志粒度并附带结构化通信诊断；单人模式下补丁自动直通放行
- **`v1.0.2` 真机稳定性与上游缺陷修复**：修复 `PlayerCamera.SetTimeScale` 四参签名、训练关协程短路、跨程序集 DMD 访问权限、Mono 字典遍历修改异常、系统字体动态回退（Unicode 昵称/聊天渲染）、UseAck 兼容探测、未命名罐头、枪械空引用、义肢缝合、蜱虫巢、兴奋剂、物品拖拽 Finalizer 与退房断网空保护

网络底层边界与上游一致：无客户端预测、无网络所有权、无确定性随机、无反作弊，仅面向合作联机场景。

## 安装

### 环境要求

- 游戏：[Casualties: Unknown](https://store.steampowered.com/app/4576510)（`v1.0.2`）
- [BepInEx 5.4.23.5 (x64)](https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_win_x64_5.4.23.5.zip)

### 安装步骤

1. 打开游戏根目录（Steam 库右键游戏 -> `管理 > 浏览本地文件`）
2. 将 `BepInEx_win_x64_5.4.23.5.zip` 直接解压到游戏根目录（确保 `winhttp.dll` 与 `CasualtiesUnknown.exe` 同级）
3. 从 [Releases](https://github.com/HuanXinWork/Casualties-Cohort/releases) 下载 `CasualtiesCohort-v5.5.4.zip`，**直接解压合并到游戏根目录**（压缩包顶层已为 `BepInEx/plugins/Together/`，不再包含旧版 `mod/` 子目录或已废弃的 `autoupdater_patcher.dll`）
4. 通过 Steam 库启动游戏，主菜单底部出现联机入口即安装成功

> **单文件快速更新**：若已安装过完整包且 `BepInEx/plugins/Together/` 下已有 7 个运行时依赖库，后续小版本可直接下载单文件 `CasualtiesMP.dll` 覆盖 `BepInEx/plugins/Together/CasualtiesMP.dll`。

### 发布资产说明

每个正式版 Release 与每日 `nightly` 预发布均包含 5 个文件：

| 文件 | 用途 |
| --- | --- |
| `CasualtiesCohort-<tag>.zip` | Release 完整安装包（顶层 `BepInEx/plugins/Together/`，含 `CasualtiesMP.dll` 及 7 个运行时依赖 DLL） |
| `CasualtiesCohort-<tag>+debug.zip` | Debug 完整调试包（含调试版 `CasualtiesMP.dll`、`CasualtiesMP.pdb` 符号表及 7 个运行时依赖 DLL，开启完整诊断日志） |
| `CasualtiesMP.dll` | Release 单文件更新程序集 |
| `CasualtiesMP+debug.dll` | Debug 单文件程序集（手动覆盖时需重命名为 `CasualtiesMP.dll`） |
| `SHA256SUMS.txt` | 上述 4 项资产的 SHA-256 校验和清单 |

需要提前体验最新修复时，可在 Releases 页面选用带 `-nightly.<YYYYMMDD>` 后缀的 Pre-release 构建。

### 常见说明

- 返回单人模式：`设置 > 通用 > 停用联机模组`（或启动参数 `--mp-disable`）
- Linux / Proton 启动项：`WINEDLLOVERRIDES="winhttp=n,b" %command%`
- 若无法搜到或创建 Steam 大厅，请从 Steam 客户端库内启动游戏，不要直接双击快捷方式或 `CasualtiesUnknown.exe`
- 与原版 `v5.5.4` 房间联机时，由于双方 DLL 二进制不同，若房主开启了验证游戏哈希（`Verify Game Hash`）会提示哈希不一致；关闭该校验或忽略提示即可正常进房互通
- 联机模式下出现的崩溃或同步异常均由模组引起，请勿向游戏原作者反馈模组问题

## 核心功能

- Steam 大厅浏览、直连与房主在线迁移（`HostTransfer`）
- 房间规则分类折叠、关键词搜索、预设重置与主菜单多语言切换
- 带游戏内距离衰减与角色生理失真效果的实时语音 / 文字聊天（MP3 播放器改为外放音响）
- 鼠标中键位置标记（双击发送高亮警报）
- 右键队友打开交互菜单：查看健康面板与协同医疗小游戏、查看背包取物、喂食/穿戴、背负（Piggyback）/搬运/推开
- 靠近队友共享体温并减缓情绪流失；饥饿、口渴与濒死流速针对联机平衡调整
- 死亡后进入旁观模式（支持自由视角与查看被旁观者伤情），默认到达下一层全员重生或通过商人招募复活（可由 `Permadeath` / `ReviveFromTrader` 规则控制）
- 跨层结算按存活玩家抵达比例触发（`LayerFinishConditionPlayerPercent`），钻探舱同样生效
- 未植入芯片（Unchipped）状态按玩家独立计算
- 支持专用服务器模式运行（非真无头）

所有房间规则可在大厅面板调整，或由房主在控制台执行 `rule <名称> <值>` 修改。

## 控制台

房主开启作弊命令权限：

```text
rule sv_cheats 1
```

客户端需要管理员权限（`adminpriv <玩家>` 或 `adminpriv-password`）并通过 `admin` 进入服务端控制台后方可执行管理指令。

## 构建与 CI/CD

### 零 Secret 直接构建（无需本地安装游戏）

仓库已纳入剥离全部方法体 IL 与内嵌资源的精简引用元数据桩（`references/*.dll`）及 7 个可再分发运行时依赖库（`libs/dlls/*.dll`）。克隆仓库后安装 .NET 8 SDK 即可直接编译打包，无需配置任何私有密钥或本地游戏目录：

```powershell
# 编译 ReleaseFullgame
dotnet build ScavPrototypeMP/CasualtiesTogether.csproj -c ReleaseFullgame

# 一键构建 ReleaseFullgame + DebugFullgame 并产出 5 项标准发布资产
& ./scripts/package-release.ps1
```

### 本地真机联调与刷新引用桩

- 若本机装有游戏，可通过环境变量 `CASUALTIESTOGETHER_GAME_PATH`、MSBuild 参数 `-p:BaseGamePath="D:\SteamLibrary\steamapps\common\Casualties Unknown"` 或运行 `python scripts/prepare_workspace.py` 指向真实游戏目录并在编译后自动部署到 `BepInEx/plugins/Together/`。
- 游戏版本更新后，在装有游戏的机器上运行 `& ./scripts/generate-ref-stubs.ps1` 即可利用 `BepInEx.AssemblyPublicizer` 重新生成 `references/*.dll` 精简引用桩。

### GitHub Actions 自动构建与发版

工作流 [`.github/workflows/build-and-release.yml`](.github/workflows/build-and-release.yml) 覆盖以下自动化流程：

- **编译与打包检查**：每次向 `main` 推送或发起 PR 时，自动编译验证 Demo（`Release` / `Debug`）与 Fullgame（`ReleaseFullgame` / `DebugFullgame`）全部配置并上传构建产物 Artifact。
- **版本升级自动发版**：当 `main` 分支 `Directory.Build.props` 的 `<ModVersion>` 更新且远端尚无对应 `v<ModVersion>` 标签时，自动创建标签并发布正式 GitHub Release（含全部 5 项资产）。
- **每日定时 Nightly 预发布**：每日 UTC `18:00` 定时检查 `main` 分支；仅当存在尚未打标发版的新提交时，自动发布 `v<ModVersion>-nightly.<YYYYMMDD>` Pre-release 预发布包（亦支持 `workflow_dispatch` 手动按需触发 `auto` / `nightly` / `build_only`）。

## 致谢

完整名单见游戏内「关于」菜单。

- [Krokosha666](https://github.com/Krokosha666) — 原始作者（已隐退）
- [Creature](https://github.com/creaturefeaturelarry) — 上游维护者
- [Dune](https://github.com/Jyan-sei) — 同步、性能优化
- [VtLoader](https://github.com/vtloader) — 缺陷修复、本地化
- Gary the Cat、Dannad、Meow、Jimmyking、Kidihh、Dawn、mayzmae、ItsVoidSK — 代码与测试贡献
- 以及上游社区的测试者与支持者

## 协议

LGPL-2.1，详见 [LICENSE](LICENSE)。本分支与上游采用相同开源协议。

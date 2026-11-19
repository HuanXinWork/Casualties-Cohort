# Casualties: Cohort for Casualties: Unknown

> 《未知伤亡》联机合作模组，[Casualties: Together](https://github.com/creaturefeaturelarry/casualties-together) 的分支版本
> 协议：LGPL-2.1，与上游一致

English guide: see [README.en.md](README.en.md)

## 简介

Casualties: Cohort 是 Casualties: Together 的分支，保留上游的插件 GUID 与程序集名（`CasualtiesMP`），因此现有的配套模组无需修改即可继续使用。

与原版相比，本分支做了这些事：

- 全量模块化拆分，单文件控制在 800 行以内，Harmony 警告清零
- 提供 `Together.Api` 门面与 `Together.ModHooks` 静态入口，配套模组不必碰内部实现
- 对齐 v5.5.3 线上协议，补齐单机直通守卫
- Debug / Release 双模日志，附带结构化通信诊断
- 修复上游既有缺陷：UseAck、罐头食品、枪械、义肢、蜱虫巢、兴奋剂、Unicode、俄语本地化、拖拽 Finalizer

网络设计限制与上游一致：没有客户端预测、没有网络所有权、没有确定性随机、没有反作弊。本模组只提供一套简单的合作体验。

## 开发状态

重构仍在进行中，部分模块尚未完成验证。当前构建可以正常使用，但与上游的差异还在逐步收敛，遇到问题请提 issue。

## 安装

### 环境要求

- 游戏：[Casualties: Unknown](https://store.steampowered.com/app/4576510)
- [BepInEx 5.4.23.5](https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_win_x64_5.4.23.5.zip)

### 步骤

1. 把 `BepInEx` 解压到游戏根目录
2. 把本模组发布包里的 `mod` 文件夹内容解压到游戏根目录
3. 通过 Steam 启动游戏
4. 主菜单会出现联机相关按钮

返回单人模式：`设置 > 通用 > 停用联机模组`

Linux / Proton：在启动项加入 `WINEDLLOVERRIDES="winhttp=n,b" %command%`

如果 Steam 大厅不工作，请从 Steam 库启动游戏，不要用快捷方式。

### 注意事项

- 装不上就再试一次
- 建议使用稳定的网络连接
- 遇到崩溃或异常，责任在本模组，与游戏原作者无关

## 功能

- Steam 大厅
- 带游戏内音效的真实语音
- 鼠标中键标记位置
- 右键与队友交互、查看、治疗
- 靠近队友共享体温
- 死亡后进入旁观模式
- 禁用睡觉，饥饿与口渴减缓
- 搬运失能队友
- 未植入芯片选项按个人计算
- 可运行专用服务器（非真无头）

所有规则由主机通过控制台调整（`rule <名称> <值>`）。

## 完整功能列表

- 禁用睡觉（对应规则 `EnableSleep`）
- 禁用时间流速按钮（不建议重新打开）
- 玩家互伤通过 `PVP` 规则开启
- 鼠标中键为其他玩家高亮光标位置
- 靠近其他玩家共享体温
- 文字与语音聊天取决于角色的语言能力，消息会带上相同的失真效果
- MP3 播放器变成手提音响，不再替换背景音乐
- 右键玩家打开交互菜单
  - 查看健康面板并执行相关操作
  - 查看对方背包并取走物品
  - 对他人使用可用物品（喂食）
- 玩家死亡后进入旁观模式
  - 存活玩家到达下一层后，所有死亡玩家重生
  - 若规则 `Permadeath` 为真，则禁用重生
  - 可以从商人处复活
- 前往下一层需要半数以上玩家抵达终点
  - 落后的玩家超时会被击杀
  - 只有主机会收到继续提示
  - 钻探舱同理，所有玩家必须在舱内
  - 由规则 `LayerFinishConditionPlayerPercent` 调整
- 玩家血量回复与衰减经过调整
  - 附近没有同伴时死亡更快（规则 `AdditionalHealthDecay`）
  - 血量回复速度提升（规则 `AdditionalHealthRegen`）
- 可以吃掉死去的队友
  - 规则 `CanAmputateHealthyPlayers` 允许从活人身上取肢体
- 商人的初次印象由队伍中最好看的那位决定

以上规则只有主机可以通过 `rule [规则名] [新值]` 修改。

## 控制台

主机开启全部命令：

```
rule sv_cheats 1
```

客户端需要管理员权限（`adminpriv <玩家>` 或 `adminpriv-password`）才能使用服务器控制台命令。

## 构建

```
python scripts/prepare_workspace.py
```

脚本会复制游戏并安装 BepInEx。推荐使用 Visual Studio 2022，打开 `CasualtiesCohort.sln` 编译，按 F5 启动。多开游戏实例需要手动或通过脚本启动。

## 致谢

原始致谢在游戏内「关于」菜单中。

- [Krokosha666](https://github.com/Krokosha666) — 原始作者（已隐退）
- [Creature](https://github.com/creaturefeaturelarry) — 上游维护者
- [Dune](https://github.com/Jyan-sei) — 同步、优化
- [VtLoader](https://github.com/vtloader) — 缺陷修复、本地化
- Gary the Cat、Dannad、Meow、Jimmyking、Kidihh、Dawn、mayzmae、ItsVoidSK — 贡献
- 以及上游项目的大量测试者与支持者

## 协议

LGPL-2.1，详见 [LICENSE](LICENSE)。本分支与上游使用同一协议。
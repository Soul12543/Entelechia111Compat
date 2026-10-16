# Entelechia111Compat

[本补丁 Steam 创意工坊](https://steamcommunity.com/sharedfiles/filedetails/?id=3820580518) · [GitHub 源码](https://github.com/Soul12543/Entelechia111Compat) · [下载发布包](https://github.com/Soul12543/Entelechia111Compat/releases/tag/v1.0.0)

由 **Soul12543** 维护的《杀戮尖塔 2》隐德来希 **v0.111 Beta 独立非官方兼容补丁**。本补丁需与 lemon 的原版隐德来希及 BaseLib 同时启用，原角色、卡牌、文案与美术均归原作者或相应权利人所有。

原版入口：[Steam 创意工坊](https://steamcommunity.com/sharedfiles/filedetails/?id=3763861013) · [公开源码仓库](https://github.com/lemon970/Entelechia-Sts2Mod)

前置入口：[BaseLib 创意工坊](https://steamcommunity.com/sharedfiles/filedetails/?id=3737335127)

## AI 生成说明

本补丁的代码、自动化测试和说明文档主要由 OpenAI Codex 根据发布者需求生成，自动检查和本机加载检查由 Codex 调用工具完成。由 Soul12543 发布。12/12 自动检查与本机加载通过；双客户端实时联机及完整长局尚未实测。源码和测试在 GitHub 公开，便于审阅与复现。原 Mod 的代码与美术不包含在本补丁内，原作者署名和许可关系见下文。

详见 [docs/AI_DISCLOSURE.md](docs/AI_DISCLOSURE.md)。

## 兼容工作范围

- **断续脉搏、缝合的可选消耗**：适配游戏 `CardCmd.Exhaust` 返回类型变化导致的方法失配。补丁在原 Mod 的 `TryExhaustAnotherCard` helper 入口安装 Prefix，使用当前游戏的消耗指令，并保留选择上下文和游戏本身的消耗结算流程。
- **心烛倍率的存档与网络序列化**：对 `BloodDemonFormPower.HeartCandlePercent` 的 `Decimal` 字段进行定向兼容处理，处理 BaseLib 的 `SavedProperty` 不支持该类型的问题。

具体实现、检查结果、实际游戏验证与尚未完成的验证，**以 [docs/VALIDATION.md](docs/VALIDATION.md) 为准**。

## 目标版本

本项目当前验收目标为以下组合，补丁只在游戏 v0.111.0 与原 Mod v1.0.3 下安装修复；其他版本会明确拒绝接管：

| 项目 | 目标版本 |
| --- | --- |
| 杀戮尖塔 2 | v0.111.0，Steam `public-beta` 分支 |
| 隐德来希 / Entelechia | v1.0.3 |
| BaseLib | v3.4.7 |
| Entelechia111Compat | 1.0.0 |

## 安装

1. 订阅并启用 [BaseLib](https://steamcommunity.com/sharedfiles/filedetails/?id=3737335127) 和[原版隐德来希](https://steamcommunity.com/sharedfiles/filedetails/?id=3763861013)。
2. 使用本补丁的工坊发布包，或将手动安装包中的两个文件放入游戏目录下独立的 `mods/Entelechia111Compat/` 文件夹。
3. 在游戏 Mod 管理界面启用 `BaseLib`、`Entelechia`、`Entelechia111Compat`，重启游戏。

手动安装后的目录：

```text
Slay the Spire 2/
└── mods/
    └── Entelechia111Compat/
        ├── Entelechia111Compat.dll
        └── Entelechia111Compat.json
```

macOS 的本地 Mod 目录位于 `SlayTheSpire2.app/Contents/MacOS/mods/`。

原 Mod 继续通过其原有安装方式提供。补丁清单声明对 `BaseLib` 和 `Entelechia` 的依赖，以便游戏安排加载顺序。补丁不需要 PCK，也不替换原 Mod 文件；避免同时安装工坊版与手动版补丁而产生重复 ID。

## 联机与验证状态

所有联机参与者应使用相同的**游戏、原版 Entelechia、BaseLib 和本补丁版本**，并启用这些 Mod。版本变化后应重新验证相关卡牌和存档流程。

测试状态以 [docs/VALIDATION.md](docs/VALIDATION.md) 为准。构建成功、逻辑测试通过或单机验证，不等同于双客户端联机验证。

## 从源码构建

需要 .NET 9 SDK，以及本机合法安装的目标游戏。将 `Sts2DataDir` 指向同时包含 `sts2.dll`、`GodotSharp.dll` 和 `0Harmony.dll` 的游戏数据目录：

```sh
dotnet build src/Entelechia111Compat/Entelechia111Compat.csproj -c Release -p:Sts2DataDir="/path/to/game/data"
```

Apple Silicon macOS 的常见位置为：

```text
~/Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app/Contents/Resources/data_sts2_macos_arm64
```

构建得到的补丁位于 `src/Entelechia111Compat/bin/Release/net9.0/Entelechia111Compat.dll`。发布时将它与仓库根目录的 `Entelechia111Compat.json` 配对；游戏程序集仅用作本机构建引用，不随补丁分发。

运行逻辑测试：

```sh
dotnet run --project tests/Entelechia111Compat.Tests -c Release
```

测试覆盖范围及结果仍以 [docs/VALIDATION.md](docs/VALIDATION.md) 为准。

## 署名与许可证

本仓库中新编写的兼容补丁代码按 [MIT License](LICENSE) 发布，作者为 **Soul12543**。该许可仅覆盖本项目有权授予许可的新增内容，不改变原 Mod、游戏或第三方组件的权利。

原版隐德来希作者为 **lemon**，公开仓库为 [lemon970/Entelechia-Sts2Mod](https://github.com/lemon970/Entelechia-Sts2Mod)。本次检查未发现原仓库明确的 LICENSE；公开可读源码不等于获得自由再分发授权。因此，本项目不打包原 Mod 的 DLL、PCK、源码或美术，也不将原 Mod 宣称为可自由再分发的开源项目。完整关系与核查记录见 [docs/ATTRIBUTION.md](docs/ATTRIBUTION.md)。

## 原作者修复后的处理

当原版隐德来希提供对应修复时，应优先使用原作者发布的版本。确认新版本已涵盖本补丁修复、并按验证记录确认存档兼容后，停用并移除独立的 `Entelechia111Compat` 补丁；原 Mod 与 BaseLib 按其自身要求保留。

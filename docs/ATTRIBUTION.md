# 署名、来源与许可范围

## 项目关系

`Entelechia111Compat` 是 **Soul12543** 维护的独立非官方兼容补丁，面向《杀戮尖塔 2》v0.111.0 `public-beta` 与原版隐德来希 v1.0.3 的兼容问题。原作者没有参与维护本补丁的声明；本项目不代表 lemon、BaseLib 维护者或 Mega Crit。

本补丁以自己的 Mod ID、程序集和安装目录加载，依赖玩家另行安装原版隐德来希与 BaseLib。补丁针对原 Mod 的运行时接口和指定字段提供适配，不发布原角色内容的替代版本。

## 原版隐德来希 / Entelechia

- 作者署名：**lemon**。
- 公开源码仓库：[lemon970/Entelechia-Sts2Mod](https://github.com/lemon970/Entelechia-Sts2Mod)。
- 原版创意工坊：[隐德来希，项目 ID 3763861013](https://steamcommunity.com/sharedfiles/filedetails/?id=3763861013)。
- 本项目目标原版版本：**v1.0.3**。

**许可核查记录（2026-10-16）**：GitHub 仓库许可元数据为 `license: null`，本次取得的本地源码快照未发现明确的 LICENSE 文件。这里记录的是检查时的状态；原作者今后可能补充或调整授权。

公开源码可用于查阅与理解接口，但不能据此把原作品视为可自由再分发的开源软件。GitHub 对未附许可证仓库的说明见 [Licensing a repository](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/customizing-your-repository/licensing-a-repository)。

本项目的 MIT License 不授予对原版隐德来希的重新许可、再分发或美术使用权。本项目发布范围不包含原版 `Entelechia.dll`、`Entelechia.pck`、原仓库源码、角色立绘、卡图、图标、场景或其他原始资源。名称及必要的类型、方法、字段名称用于说明依赖关系、实现接口适配与定位兼容问题。

## BaseLib

- 创意工坊：[BaseLib，项目 ID 3737335127](https://steamcommunity.com/sharedfiles/filedetails/?id=3737335127)。
- 本项目目标版本：**v3.4.7**。
- 用途：原版隐德来希的前置库，以及本补丁涉及的保存和网络字段处理环境。

BaseLib 由其维护者按其自身声明的条款提供。本项目要求玩家从原发布渠道安装，未将 BaseLib 二进制或源码打包发布，也未将本项目的 MIT License 扩展到 BaseLib。

## 游戏及本机构建引用

《杀戮尖塔 2》由 **Mega Crit** 开发。游戏名称与商标属于相应权利人。本项目不是官方补丁，也没有获得官方认可的声明。

构建时引用本机游戏安装目录中的 `sts2.dll`、`GodotSharp.dll`、`0Harmony.dll`。这些程序集及其依赖保留各自的许可和权利归属，仅作为本机构建引用，补丁发布包不包含它们。

## 本补丁的 MIT License

作者：**Soul12543**。

仓库根目录的 [LICENSE](../LICENSE) 仅适用于本项目新编写且有权授权的兼容补丁代码及相应新增内容。该许可不覆盖上述游戏、原 Mod、第三方库或其资源，也不通过依赖关系改变上游许可。

若后续贡献包含来自其他项目的代码或资源，应在合入和发布前明确其来源与适用许可，并更新本文件。不能以本仓库存在 MIT License 为理由复制或再分发原版隐德来希的文件。

## 分发与后续维护

发布包仅包含本补丁所需的独立程序集、Mod 清单及本项目自己的说明与许可文件；工坊必需项应指向原版隐德来希与 BaseLib，保留原作者入口和署名。

当原作者修复对应问题时，应在确认版本与存档兼容后优先使用原版修复并移除本补丁。实现和验证状态以 [VALIDATION.md](VALIDATION.md) 为准；本说明不代表已完成双客户端联机验证。

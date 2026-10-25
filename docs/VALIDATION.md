# 验证记录
> 2026-10-25：**v1.2.0** 保留既有兼容修复并加入玩法和中文描述更新。发布构建 0 警告、0 错误，**38/38** 本地检查通过；本轮游戏内和双客户端实机尚未验证。测试范围及隔离方式见 [玩法与验证范围](LOCAL_GAMEPLAY_CHANGE.md)。以下为 v1.0.0 的历史验收记录。

日期：2026-10-16。发布版本：Entelechia111Compat 1.0.0。

## 目标环境

| 项目 | 已验证版本 |
| --- | --- |
| 游戏 | v0.111.0，Steam public-beta，Build ID 24724944 |
| 游戏 release commit | 41cef1ea |
| 系统 | macOS Apple Silicon |
| 原 Mod | Entelechia v1.0.3 |
| BaseLib | v3.4.7 |
| .NET SDK | 9.0.319 |

原源码参考提交：`f48968bf28cab34fbce55da3066468803ceacc46`。
原安装 DLL SHA256：`fb4608b0d8786333ccd0c1762a8dda635665260cbea4d52db7ed560c2ae6c466`。
补丁 DLL SHA256：`087685fd7bf7af1cbb09fba62287a843e6460157c77d2311125bcc8fbef5e88a`。

## 结果

- Release 构建：通过，0 警告、0 错误。
- 自动检查：**12/12 通过**。
- 实际游戏启动检查：通过。游戏加载原 Mod 与补丁，补丁的两个修复均完成初始化；未开始或操作已有游戏。
- 双客户端实时联机：**尚未实测**。
- 完整流程战斗与长局、任意其他 Mod 组合：尚未实测。

启动检查使用最终发布 DLL；日志确认：
```text
[Entelechia111Compat] v1.0.0: replaced Entelechia.EntelechiaCode.Cards.EntelechiaCard.TryExhaustAnotherCard; engine exhaust hooks and PlayerChoiceContext are preserved.
[Entelechia111Compat] HeartCandlePercent: exact decimal save and network serialization compatibility installed for BloodDemonFormPower.
Finished mod initialization for '隐德来希 v0.111 Beta 兼容补丁' (Entelechia111Compat).
```

本机同时安装的其他 Mod 有既有报错，headless 自动退出也有渲染资源释放提示；这里的“通过”仅指本补丁的两项修复成功初始化，不代表所有其他 Mod 无问题。BaseLib 原有“不支持 Decimal SavedProperty”检查警告可能仍出现；本补丁通过定向接管该属性的读写处理此问题。

## 自动检查覆盖

1. 无候选、空手牌或没有战斗手牌时不触发选牌与消耗。
2. 允许取消可选消耗。
3. 成功选择只消耗指定对象一次。
4. 同名卡牌的另一个实例仍可选择。
5. 选牌和消耗保持传入的同一个 PlayerChoiceContext。
6. 完整等待选牌与消耗任务。
7. 不吞选牌或消耗异常。
8. 确认原 DLL 的旧 Exhaust 返回类型与当前 v0.111 返回类型不同。
9. 对实际原 DLL helper 安装并执行 Harmony Prefix；验证当前引擎命令的参数和异步次序。
10. 对实际 BloodDemonFormPower 使用真实 SavedProperties 与游戏 JSON 序列化上下文，验证区域设置、小数、负值及 Decimal 极值的精确往返。
11. 缺失值保持现值、整数表示兼容；非法、溢出、逗号小数及重复值明确拒绝；非目标模型保持引擎原行为。
12. 使用真实 PacketWriter/PacketReader 与 SavedProperties 网络序列化往返，复用原属性键、精确保留小数，写入与读取位数一致。

第 9 项对引擎选牌和消耗指令使用测试替身，以便在游戏外验证真实补丁入口。第 12 项验证数据包编码与恢复，不建立真实 Steam 联机连接。游戏外 Sentry 扩展未加载提示属于此测试进程的预期情况。

静态元数据审计核对了原 Mod 的 140 个外部成员引用、95 个类型引用、283 个 override，以及 33 个可达 BaseLib 方法；仅发现已由补丁绕开的旧 Exhaust 调用。这项审计不能证明全部运行时行为正确。

## 联机实测建议

所有参与者使用相同的游戏、Entelechia、BaseLib、补丁版本并启用它们。分别由房主和客机打出断续脉搏、缝合，覆盖确认/取消/无候选/同名副本；检查双方手牌与消耗堆一致、后续行动和结束回合正常。启用血魔形态后测试保存、重连与心烛比例恢复。

旧版本未能写入保存文件的状态无法凭空恢复。检查旧战斗存档时应确认原状态是否存在；可从新战斗验证新写入的状态。

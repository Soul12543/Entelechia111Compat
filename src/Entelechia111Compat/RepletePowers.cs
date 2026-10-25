using BaseLib.Abstracts;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace Entelechia111Compat;

public sealed class RepleteReadyPower : CustomPowerModel
{
    public override PowerType Type => PowerType.Buff;
    public override PowerStackType StackType => PowerStackType.Single;
    public override List<(string, string)> Localization => [("title", "重盈"), ("description", DescriptionText), ("smartDescription", DescriptionText)];
    internal const string DescriptionText = "本场战斗可触发1次：生命将耗尽时，回复至最大生命的40%（可由永续重盈强化，向下取整，至少1点），清除易伤和虚弱，抽2张牌，获得2点能量，并对所有敌人施加3层萃血。此后本场战斗受到的攻击伤害降低25%。触发后消失。";
    public override string CustomPackedIconPath => "res://Entelechia/images/powers/eternal_replete_power.png";
    public override string CustomBigIconPath => "res://Entelechia/images/powers/big/eternal_replete_power.png";
    [SavedProperty(SerializationCondition.SaveIfNotTypeDefault)]
    public string? EntelechiaCompatState { get; set; }
}
public sealed class RepleteWardPower : CustomPowerModel
{
    public override PowerType Type => PowerType.Buff;
    public override PowerStackType StackType => PowerStackType.Single;
    public override List<(string, string)> Localization => [("title", "重盈·庇护"), ("description", "本场战斗受到的攻击伤害降低25%。"), ("smartDescription", "本场战斗受到的攻击伤害降低25%。")];
    public override string CustomPackedIconPath => "res://Entelechia/images/powers/crimson_ward_power.png";
    public override string CustomBigIconPath => "res://Entelechia/images/powers/big/crimson_ward_power.png";
}

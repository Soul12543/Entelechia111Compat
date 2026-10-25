using System.Reflection;
using System.Runtime.CompilerServices;
using BaseLib.Abstracts;
using BaseLib.Extensions;
using BaseLib.Utils;
using Entelechia.EntelechiaCode;
using Entelechia.EntelechiaCode.Cards;
using Entelechia.EntelechiaCode.Powers;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.ValueProps;

namespace Entelechia111Compat;

internal static class RevisedCards
{
    private static readonly MethodInfo Attack = AccessTools.Method(typeof(EntelechiaCard), "ExecuteCardAttack");
    private static readonly MethodInfo PayHp = AccessTools.Method(typeof(EntelechiaCard), "TryPayHpCost");
    internal static void Install(Harmony h)
    {
        h.Patch(AccessTools.Constructor(typeof(EntelechiaCard), [typeof(int), typeof(CardType), typeof(CardRarity), typeof(TargetType)]), prefix: Patch(nameof(BaseCost)));
        foreach (var type in new[] { typeof(BloodShield), typeof(HeartCandleRitual), typeof(ResidualPulseConduit) })
            h.Patch(AccessTools.Constructor(type, Type.EmptyTypes), postfix: Patch(nameof(Constructed)));
        foreach (var type in new[] { typeof(BloodShield), typeof(ResidualPulseConduit) })
            h.Patch(AccessTools.DeclaredMethod(type, "OnUpgrade"), prefix: Patch(nameof(UpgradeInstead)));
        foreach (var type in new[] { typeof(BloodFeast), typeof(BloodRebuild) })
            h.Patch(AccessTools.DeclaredMethod(type, "OnUpgrade"), postfix: Patch(nameof(UpgradeExtra)));
        foreach (var type in new[] { typeof(DiscontinuousPulse), typeof(HeartBrand), typeof(BloodDebtSettlement), typeof(BloodStorm), typeof(SoulBloodDraw), typeof(BloodSweep), typeof(Suture) })
            h.Patch(AccessTools.DeclaredMethod(type, "OnPlay"), prefix: Patch(nameof(PlayPrefix)));
        h.Patch(AccessTools.DeclaredMethod(typeof(ImmortalBloodline), "OnPlay"), postfix: Patch(nameof(ImmortalEnergy)));
    }
    private static HarmonyMethod Patch(string name) => new(typeof(RevisedCards), name);
    private static void BaseCost(EntelechiaCard __instance, ref int cost)
    {
        cost = __instance switch { BloodShield => 0, HeartCandleRitual => 1, ResidualPulseConduit => 3, _ => cost };
    }
    private static void Constructed(EntelechiaCard __instance)
    {
        var vars = (List<MegaCrit.Sts2.Core.Localization.DynamicVars.DynamicVar>)AccessTools.Field(typeof(ConstructedCardModel), "_constructedDynamicVars").GetValue(__instance)!;
        if (__instance is BloodShield) vars.Single(v => v.Name == "Block").BaseValue = 4;
        if (__instance is ResidualPulseConduit) vars.Single(v => v.Name == "ResidualPulseConduitPower").BaseValue = 2;
        if (__instance is HeartCandleRitual)
        {
            var keywords = (List<CardKeyword>)AccessTools.Field(typeof(ConstructedCardModel), "_cardKeywords").GetValue(__instance)!;
            keywords.Remove(CardKeyword.Exhaust); keywords.Add(CardKeyword.Ethereal);
        }
    }
    private static bool UpgradeInstead(EntelechiaCard __instance)
    {
        if (__instance is BloodShield) __instance.DynamicVars.Block.UpgradeValueBy(2);
        else { __instance.DynamicVars.Power<ResidualPulseConduitPower>().UpgradeValueBy(1); __instance.AddKeyword(CardKeyword.Innate); }
        return false;
    }
    private static void UpgradeExtra(EntelechiaCard __instance)
    {
        if (__instance is BloodFeast) __instance.EnergyCost.UpgradeBy(-1);
        else __instance.RemoveKeyword(CardKeyword.Exhaust);
    }
    private static async Task ImmortalEnergy(Task __result, EntelechiaCard __instance)
    {
        await __result;
        if (Low(__instance)) await PlayerCmd.GainEnergy(1, __instance.Owner);
    }
    internal static bool Low(CardModel card) => card.Owner.Creature.CurrentHp <= card.Owner.Creature.MaxHp * .5m;
    internal static Task<AttackCommand> AttackAsync(EntelechiaCard card, PlayerChoiceContext context, CardPlay play) => (Task<AttackCommand>)Attack.Invoke(card, [context, play, 1])!;
    private static bool PlayPrefix(EntelechiaCard __instance, PlayerChoiceContext context, CardPlay cardPlay, ref Task __result)
    { __result = PlayAsync(__instance, context, cardPlay); return false; }
    internal static async Task PlayAsync(EntelechiaCard card, PlayerChoiceContext ctx, CardPlay play)
    {
        switch (card)
        {
            case DiscontinuousPulse:
                var target = card.Owner.Creature.CombatState?.Enemies.FirstOrDefault(e => e.IsAlive && OwnHarvest(e, card)?.Amount > 0);
                if (target != null)
                {
                    var harvest = OwnHarvest(target, card)!;
                    await PowerCmd.ModifyAmount(ctx, harvest, -1, card.Owner.Creature, card);
                    await PowerCmd.Apply<BloodlossPower>(ctx, target, card.DynamicVars.Power<BloodlossPower>().BaseValue, card.Owner.Creature, card);
                }
                if (await ExhaustPatch.RunAsync(card, ctx)) await CardPileCmd.Draw(ctx, 2, card.Owner);
                break;
            case HeartBrand:
                if (!await (Task<bool>)PayHp.Invoke(card, [ctx, 3m, play])!) break;
                var hadCandle = play.Target != null && HeartCandleOwnershipPatch.FindOwned(play.Target.Powers, card.Owner.Creature) != null;
                if (play.Target != null) await HeartCandlePower.ApplyPercent(ctx, play.Target, card, card.DynamicVars.Power<HeartCandlePower>().BaseValue / 2, true);
                await CardPileCmd.Draw(ctx, 1, card.Owner);
                if (!hadCandle && play.Target != null)
                { await HeartCandlePower.ApplyPercent(ctx, play.Target, card, card.DynamicVars.Power<HeartCandlePower>().BaseValue / 2, true); await PlayerCmd.GainEnergy(1, card.Owner); }
                break;
            case BloodDebtSettlement:
                var low = Low(card);
                var stacks = play.Target == null ? 0 : OwnHarvest(play.Target, card)?.Amount ?? 0;
                if (stacks > 0) await TurnStateTracker.HealTracking(card.Owner.Creature, stacks * 3m, true);
                if (low) await PlayerCmd.GainEnergy(1, card.Owner); else await CardPileCmd.Draw(ctx, 1, card.Owner);
                break;
            case BloodStorm:
                if (!await (Task<bool>)PayHp.Invoke(card, [ctx, 4m, play])!) break;
                await AttackAsync(card, ctx, play);
                if (play.Target is { IsAlive: true } stormTarget)
                    await PowerCmd.Apply<BloodlossPower>(ctx, stormTarget, card.DynamicVars.Power<BloodlossPower>().BaseValue, card.Owner.Creature, card);
                if (Low(card)) await PlayerCmd.GainEnergy(1, card.Owner); else await CardPileCmd.Draw(ctx, 1, card.Owner);
                break;
            case SoulBloodDraw:
                var wasLow = Low(card);
                var before = BloodHarvestPower.GetTriggerCountForCurrentCardPlay(card);
                await AttackAsync(card, ctx, play);
                if (BloodHarvestPower.GetTriggerCountForCurrentCardPlay(card) > before)
                {
                    await TurnStateTracker.HealTracking(card.Owner.Creature, card.DynamicVars.Heal.BaseValue, true);
                    if (wasLow) { await PlayerCmd.GainEnergy(1, card.Owner); await CardPileCmd.Draw(ctx, 1, card.Owner); }
                }
                break;
            case BloodSweep:
                var targets = card.GetTargets().ToList();
                await AttackAsync(card, ctx, play);
                var count = 0;
                foreach (var enemy in targets.Where(e => e.IsAlive))
                { await HeartCandlePower.ApplyPercent(ctx, enemy, card, card.DynamicVars.Power<HeartCandlePower>().BaseValue, true); count++; }
                if (count >= 2) { await CardPileCmd.Draw(ctx, 1, card.Owner); await PlayerCmd.GainEnergy(1, card.Owner); }
                break;
            case Suture:
                await ExhaustPatch.RunAsync(card, ctx);
                await CommonActions.CardBlock(card, play);
                if (Low(card) || TurnStateTracker.HealedThisTurnFor(card.Owner.Creature))
                    await CreatureCmd.GainBlock(card.Owner.Creature, card.IsUpgraded ? 5 : 4, default, play, false);
                break;
            default: throw new InvalidOperationException("Unsupported revised card: " + card.GetType().Name);
        }
    }
    private static BloodHarvestPower? OwnHarvest(Creature target, CardModel card) => target.Powers.OfType<BloodHarvestPower>().FirstOrDefault(p => ReferenceEquals(p.Applier, card.Owner.Creature));
}

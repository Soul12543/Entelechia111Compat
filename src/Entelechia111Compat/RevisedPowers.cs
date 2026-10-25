using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using BaseLib.Utils;
using Entelechia.EntelechiaCode;
using Entelechia.EntelechiaCode.Cards;
using Entelechia.EntelechiaCode.Powers;
using Entelechia.EntelechiaCode.Relics;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.ValueProps;

namespace Entelechia111Compat;

internal static class RevisedPowers
{
    private static readonly AsyncLocal<bool> ResolvingCandle = new();
    private static readonly AsyncLocal<HashSet<PowerModel>?> Absorbing = new();
    internal static void Install(Harmony h)
    {
        h.Patch(AccessTools.DeclaredMethod(typeof(HeartCandlePower), "AfterDamageGiven"), prefix: Patch(nameof(CandlePrefix)));
        h.Patch(AccessTools.DeclaredMethod(typeof(BloodDemonFormPower), "AfterCurrentHpChanged"), prefix: Patch(nameof(FormPrefix)));
        h.Patch(AccessTools.DeclaredMethod(typeof(PainConversionPower), "AfterCurrentHpChanged"), prefix: Patch(nameof(PainPrefix)));
        foreach (var name in new[] { "CountHit", "CountLethalHit" })
            h.Patch(AccessTools.DeclaredMethod(typeof(BloodFeastPower), name), prefix: Patch(nameof(FeastPrefix)));
        h.Patch(AccessTools.DeclaredMethod(typeof(ResidualPulseConduitPower), "AfterDamageReceived"), prefix: Patch(nameof(SkipOldConduit)));
        h.Patch(AccessTools.DeclaredMethod(typeof(AbstractModel), "AfterCurrentHpChanged"), prefix: Patch(nameof(HpChangedPrefix)));
        h.Patch(AccessTools.DeclaredMethod(typeof(AbstractModel), "BeforeCombatStart"), prefix: Patch(nameof(CombatStartPrefix)));
        h.Patch(AccessTools.DeclaredMethod(typeof(BloodDemonReplete), "AfterPreventingDeath"), postfix: Patch(nameof(AfterRevive)));
        h.Patch(HeartCandleOwnershipPatch.Body(typeof(BloodDemonReplete).Assembly, typeof(BloodDemonReplete).FullName!, "AfterPreventingDeath"), transpiler: Patch(nameof(ReviveFloor)));
    }
    private static HarmonyMethod Patch(string name) => new(typeof(RevisedPowers), name);
    internal static decimal BurstDamage(decimal pool, int formStacks, bool redCandle) => Math.Ceiling(pool * (1m + Math.Max(formStacks, 0) * .5m + (redCandle ? .75m : 0m)));
    private static bool CandlePrefix(HeartCandlePower __instance, PlayerChoiceContext choiceContext, Creature? dealer, DamageResult result, ValueProp props, Creature target, CardModel? cardSource, ref Task __result)
    { __result = BurstAsync(__instance, choiceContext, dealer, result, props, target, cardSource); return false; }
    internal static async Task BurstAsync(HeartCandlePower power, PlayerChoiceContext ctx, Creature? dealer, DamageResult result, ValueProp props, Creature target, CardModel? card)
    {
        if (ResolvingCandle.Value || dealer == null || !ReferenceEquals(power.Applier, dealer) || target != power.Owner || !target.IsAlive
            || power.Amount <= 0 || result.UnblockedDamage <= 0 || card == null || !EntelechiaDamage.IsAttackDamage(props, card)) return;
        var pool = power.Amount;
        var damage = BurstDamage(pool, dealer.Powers.OfType<BloodDemonFormPower>().FirstOrDefault()?.Amount ?? 0, card is RedCandleAll);
        ResolvingCandle.Value = true;
        try
        {
            var before = target.CurrentHp;
            await DamageAttribution.LoseHpWithSourceAsync(ctx, target, Math.Min(damage, before), DamageProps.nonCardHpLoss, dealer, null, null, power);
            var actual = Math.Max(before - target.CurrentHp, 0);
            HeartCandleOwnershipPatch.RecordOwnBurst(target, dealer, pool, actual);
            HeartCandleLedger.RecordConsumed(target, actual);
            if (actual > 0 && target.IsAlive)
            {
                var ember = dealer.Powers.OfType<CandleEmberPower>().FirstOrDefault();
                if (ember?.Amount > 0)
                {
                    await DamageAttribution.ApplyPassiveBleedAsync(ctx, target, null, ember.Amount, false, power);
                    await ember.TryHealLowHealth();
                }
            }
            if (power.Amount <= pool) await PowerCmd.Remove(power);
            else await PowerCmd.ModifyAmount(ctx, power, -pool, dealer, card);
        }
        finally { ResolvingCandle.Value = false; }
    }
    private static void FormPrefix(BloodDemonFormPower __instance) => AccessTools.Field(typeof(BloodDemonFormPower), "_triggeredThisTurn").SetValue(__instance, false);
    private static void PainPrefix(PainConversionPower __instance) => __instance.RefundedEnergyThisTurn = false;
    internal static void FeastPrefix(BloodFeastPower __instance)
    {
        if (__instance.TriggeredThisTurn) { __instance.HitCount = 0; __instance.TriggeredThisTurn = false; }
    }
    private static bool SkipOldConduit(ref Task __result) { __result = Task.CompletedTask; return false; }
    private static bool HpChangedPrefix(AbstractModel __instance, Creature creature, decimal delta, ref Task __result)
    {
        if (__instance is not ResidualPulseConduitPower power) return true;
        __result = AbsorbAsync(power, creature, delta); return false;
    }
    internal static int ActualLoss(IEnumerable<DamageResult> results) => results.Sum(r => Math.Max(0, r.UnblockedDamage));
    internal static async Task AbsorbAsync(ResidualPulseConduitPower power, Creature creature, decimal delta)
    {
        if (power.Owner != creature || delta >= 0 || power.Amount <= 0 || !creature.IsAlive || !TurnStateTracker.IsOwnerPlayerTurn(creature)) return;
        var active = Absorbing.Value ??= [];
        if (!active.Add(power)) return;
        try
        {
            var enemies = creature.CombatState!.Enemies.Where(e => e.IsAlive).ToArray();
            foreach (var enemy in enemies)
            {
                if (!enemy.IsAlive || !creature.IsAlive) continue;
                var source = new DamageAttribution.Source(enemy, "ENTELECHIA-RESIDUAL_PULSE_CONDUIT_POWER", [new(creature.Player!.NetId, 1)]);
                var results = await DamageAttribution.WithSourceAsync(source, () => CreatureCmd.Damage(new BlockingPlayerChoiceContext(), enemy, power.Amount, ValueProp.Unpowered, creature));
                var amount = ActualLoss(results);
                if (amount <= 0 || !creature.IsAlive) continue;
                var before = creature.CurrentHp;
                // The native max-HP command already heals by the gained amount; do not heal twice.
                await CreatureCmd.GainMaxHp(creature, amount);
                AccessTools.Method(typeof(TurnStateTracker), "TrackHpDelta").Invoke(null, [creature, (decimal)(creature.CurrentHp - before)]);
            }
        }
        finally { active.Remove(power); }
    }
    private static bool CombatStartPrefix(AbstractModel __instance, ref Task __result)
    {
        if (__instance is not BloodDemonReplete relic) return true;
        __result = StartCombatAsync(relic); return false;
    }
    internal static async Task StartCombatAsync(BloodDemonReplete relic)
    {
        relic.LastRevivedFloor = -1;
        AccessTools.Field(typeof(BloodDemonReplete), "_emberAppliedThisCombat").SetValue(relic, false);
        await PowerCmd.Apply<RepleteReadyPower>(new BlockingPlayerChoiceContext(), relic.Owner.Creature, 1, relic.Owner.Creature, null);
    }
    private static async Task AfterRevive(Task __result, BloodDemonReplete __instance, Creature creature)
    {
        await __result;
        if (creature != __instance.Owner.Creature) return;
        foreach (var power in creature.Powers.OfType<RepleteReadyPower>().ToArray()) await PowerCmd.Remove(power);
        if (creature.IsAlive && !creature.Powers.OfType<RepleteWardPower>().Any())
            await PowerCmd.Apply<RepleteWardPower>(new BlockingPlayerChoiceContext(), creature, 1, creature, null);
    }
    private static IEnumerable<CodeInstruction> ReviveFloor(IEnumerable<CodeInstruction> instructions)
    {
        var ceil = AccessTools.Method(typeof(Math), nameof(Math.Ceiling), [typeof(decimal)]);
        var floor = AccessTools.Method(typeof(Math), nameof(Math.Floor), [typeof(decimal)]);
        var count = 0;
        foreach (var instruction in instructions)
        { if (instruction.Calls(ceil)) { instruction.operand = floor; count++; } yield return instruction; }
        if (count != 1) throw new InvalidOperationException("Expected one revive rounding call.");
    }
}

internal static class BloodSpeedReplay
{
    private sealed record Replay(int Index);
    private static readonly ConditionalWeakTable<CardModel, Replay> Replays = new();
    internal static void Install(Harmony h)
    {
        h.Patch(AccessTools.DeclaredMethod(typeof(BloodSpeedPower), "AfterDamageGiven"), prefix: new(typeof(BloodSpeedReplay), nameof(SkipExtraHit)));
        h.Patch(AccessTools.DeclaredMethod(typeof(CardModel), "GeneratePlayCount"), postfix: new(typeof(BloodSpeedReplay), nameof(AddReplay)));
        h.Patch(AccessTools.DeclaredMethod(typeof(CardModel), "OnPlayWrapper"), postfix: new(typeof(BloodSpeedReplay), nameof(CleanupAfterPlay)));
        h.Patch(AccessTools.Method(typeof(CreatureCmd), "Damage", [typeof(PlayerChoiceContext), typeof(IEnumerable<Creature>), typeof(decimal), typeof(ValueProp), typeof(Creature), typeof(CardModel), typeof(CardPlay)]), prefix: new(typeof(BloodSpeedReplay), nameof(ScaleDamage)));
    }
    private static bool SkipExtraHit(ref Task __result) { __result = Task.CompletedTask; return false; }
    private static async Task<int> AddReplay(Task<int> __result, CardModel __instance)
    {
        var count = await __result;
        Replays.Remove(__instance);
        if (__instance is not EntelechiaCard || __instance.Type != CardType.Attack) return count;
        var speed = __instance.Owner.Creature.Powers.OfType<BloodSpeedPower>().FirstOrDefault();
        if (speed?.Amount <= 0 || speed == null) return count;
        Replays.Add(__instance, new(count));
        await PowerCmd.ModifyAmount(new BlockingPlayerChoiceContext(), speed, -1, __instance.Owner.Creature, __instance);
        return count + 1;
    }
    private static async Task CleanupAfterPlay(Task __result, CardModel __instance)
    { try { await __result; } finally { Replays.Remove(__instance); } }
    internal static decimal ScaledDamage(decimal amount) => amount <= 0 ? 0 : Math.Max(2, Math.Ceiling(amount * .35m));
    private static void ScaleDamage(CardModel? cardSource, CardPlay? cardPlay, ValueProp props, ref decimal amount)
    {
        if (cardSource != null && cardPlay != null && props.HasFlag(ValueProp.Move) && !props.HasFlag(ValueProp.Unpowered)
            && Replays.TryGetValue(cardSource, out var replay) && cardPlay.PlayIndex == replay.Index)
            amount = ScaledDamage(amount);
    }
}

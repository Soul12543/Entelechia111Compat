using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;

namespace Entelechia111Compat;

internal static class HeartCandleOwnershipPatch
{
    internal const string TargetTypeName = "Entelechia.EntelechiaCode.Powers.HeartCandlePower";
    private const string LedgerTypeName = "Entelechia.EntelechiaCode.HeartCandleLedger";
    private static Func<PlayerChoiceContext, Creature, CardModel?, decimal, bool, Creature?, Task> _apply = null!;
    private static Action<Creature, decimal> _recordNatural = null!, _recordConsumed = null!, _recordRevived = null!;
    private sealed class Account
    {
        internal decimal OriginalConsumed, OutstandingRevived, TotalRevived;
    }
    private static readonly Dictionary<(Creature Target, Creature Applier), Account> Accounts = [];

    internal static void Install(Harmony harmony, Assembly original)
    {
        var candle = original.GetType(TargetTypeName, true)!;
        var ledger = original.GetType(LedgerTypeName, true)!;
        _apply = AccessTools.Method(typeof(HeartCandleOwnershipPatch), nameof(ApplyPercentCoreAsync))
            .MakeGenericMethod(candle).CreateDelegate<Func<PlayerChoiceContext, Creature, CardModel?, decimal, bool, Creature?, Task>>();
        _recordNatural = AccessTools.Method(ledger, "RecordNaturalGenerated").CreateDelegate<Action<Creature, decimal>>();
        _recordConsumed = AccessTools.Method(ledger, "RecordConsumed").CreateDelegate<Action<Creature, decimal>>();
        _recordRevived = AccessTools.Method(ledger, "RecordRevived").CreateDelegate<Action<Creature, decimal>>();
        var bodies = new[]
        {
            Body(original, TargetTypeName, "AfterDamageGiven"),
            Body(original, "Entelechia.EntelechiaCode.Powers.BloodDemonFormPower", "AfterPlayerTurnStart"),
            Body(original, "Entelechia.EntelechiaCode.Cards.HeartBrand", "OnPlay"),
            Body(original, "Entelechia.EntelechiaCode.Cards.ReviveCandle", "OnPlay")
        };
        harmony.Patch(AccessTools.PropertyGetter(typeof(PowerModel), nameof(PowerModel.InstanceType)), postfix: Patch(nameof(InstanceTypePostfix)));
        harmony.Patch(AccessTools.DeclaredMethod(candle, "AfterDamageGiven"), prefix: Patch(nameof(DamagePrefix)));
        harmony.Patch(AccessTools.DeclaredMethod(candle, "ApplyPercent"), prefix: Patch(nameof(ApplyPercentPrefix)));
        foreach (var body in bodies) harmony.Patch(body, transpiler: Patch(nameof(OwnershipTranspiler)));
        harmony.Patch(AccessTools.Method(ledger, "Clear"), postfix: Patch(nameof(Clear)));
        harmony.Patch(AccessTools.Method(ledger, "CleanupDeadEntries"), postfix: Patch(nameof(Cleanup)));
        harmony.Patch(AccessTools.Method(typeof(LocString), nameof(LocString.GetFormattedText)), postfix: Patch(nameof(DescriptionPostfix)));
    }

    private static HarmonyMethod Patch(string name) => new(typeof(HeartCandleOwnershipPatch), name);
    internal static MethodInfo Body(Assembly original, string typeName, string methodName)
    {
        var type = original.GetType(typeName, true)!;
        var method = AccessTools.DeclaredMethod(type, methodName) ?? throw new MissingMethodException(typeName, methodName);
        var state = method.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType
            ?? throw new InvalidOperationException($"{typeName}.{methodName} must remain async.");
        if (AccessTools.Field(state, "<>4__this")?.FieldType != type)
            throw new InvalidOperationException("Unsupported async source: " + typeName);
        return AccessTools.Method(state, "MoveNext");
    }
    internal static CodeInstruction LoadSource(CodeInstruction instruction, FieldInfo source)
    {
        var load = new CodeInstruction(OpCodes.Ldarg_0);
        load.labels.AddRange(instruction.labels); load.blocks.AddRange(instruction.blocks);
        instruction.labels.Clear(); instruction.blocks.Clear();
        return load;
    }
    private static bool IsCandle(PowerModel power) => power.GetType().FullName == TargetTypeName;
    private static void InstanceTypePostfix(PowerModel __instance, ref PowerInstanceType __result)
    { if (IsCandle(__instance)) __result = PowerInstanceType.InstancedPerApplier; }
    private static bool DamagePrefix(PowerModel __instance, Creature? dealer, ref Task __result)
    {
        if (__instance.Applier != null && ReferenceEquals(__instance.Applier, dealer)) return true;
        __result = Task.CompletedTask; return false;
    }
    private static bool ApplyPercentPrefix(PlayerChoiceContext context, Creature target, CardModel? source, decimal percent, bool showEffect, ref Task __result)
    { __result = _apply(context, target, source, percent, showEffect, source?.Owner?.Creature); return false; }
    private static async Task ApplyPercentCoreAsync<T>(PlayerChoiceContext context, Creature target, CardModel? source, decimal percent, bool showEffect, Creature? applier) where T : PowerModel
    {
        if (applier == null) return;
        var pool = Math.Ceiling(Math.Max(target.CurrentHp, 0m) * percent / 100m);
        if (pool <= 0) return;
        var before = FindOwned(target.Powers, applier)?.Amount ?? 0;
        await PowerCmd.Apply<T>(context, target, pool, applier, source, showEffect);
        var after = FindOwned(target.Powers, applier)?.Amount ?? 0;
        _recordNatural(target, Math.Max(after - before, 0));
    }
    internal static Task ApplyFromPowerAsync(PlayerChoiceContext context, Creature target, CardModel? card, decimal percent, bool showEffect, PowerModel source) =>
        _apply(context, target, card, percent, showEffect, source.Owner);
    internal static PowerModel? FindOwned(IEnumerable<PowerModel> powers, Creature applier) =>
        powers.FirstOrDefault(power => IsCandle(power) && power.Applier != null && ReferenceEquals(power.Applier, applier));
    internal static PowerModel? FirstOwnedForCard(IEnumerable<PowerModel> powers, Func<PowerModel, bool> predicate, CardModel source) =>
        powers.FirstOrDefault(power => IsCandle(power) && ReferenceEquals(power.Applier, source.Owner.Creature) && predicate(power));
    internal static bool AnyOwnedForCard(IEnumerable<PowerModel> powers, Func<PowerModel, bool> predicate, CardModel source) =>
        FirstOwnedForCard(powers, predicate, source) != null;
    internal static Creature? FindAutomaticTarget(IEnumerable<Creature> enemies, Func<Creature, bool> predicate, PowerModel source) =>
        enemies.FirstOrDefault(enemy => enemy.CurrentHp > 0 && FindOwned(enemy.Powers, source.Owner) == null);

    private static IEnumerable<CodeInstruction> OwnershipTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        var source = AccessTools.Field(__originalMethod.DeclaringType, "<>4__this");
        var found = new Dictionary<string, int>();
        foreach (var instruction in instructions)
        {
            string? wrapper = null;
            if (instruction.operand is MethodInfo call)
            {
                if (source.FieldType.Name == "BloodDemonFormPower")
                {
                    if (call.DeclaringType?.FullName == TargetTypeName && call.Name == "ApplyPercent") wrapper = nameof(ApplyFromPowerAsync);
                    else if (IsLinq(call, "FirstOrDefault", typeof(Creature))) wrapper = nameof(FindAutomaticTarget);
                }
                else if (source.FieldType.Name == "HeartBrand" && IsLinq(call, "Any", typeof(PowerModel))) wrapper = nameof(AnyOwnedForCard);
                else if (source.FieldType.Name == "ReviveCandle")
                {
                    if (call.DeclaringType?.FullName == LedgerTypeName)
                        wrapper = call.Name switch { "PreviewRevivePool" => nameof(PreviewForCard), "RecordRevived" => nameof(RecordRevivedForCard), _ => null };
                    else if (IsLinq(call, "FirstOrDefault", typeof(PowerModel))) wrapper = nameof(FirstOwnedForCard);
                }
                else if (source.FieldType.Name == "HeartCandlePower" && call.DeclaringType?.FullName == LedgerTypeName && call.Name == "RecordConsumed")
                    wrapper = nameof(RecordConsumedForPower);
            }
            if (wrapper != null)
            {
                yield return LoadSource(instruction, source);
                yield return new CodeInstruction(OpCodes.Ldfld, source);
                instruction.operand = AccessTools.Method(typeof(HeartCandleOwnershipPatch), wrapper);
                found[wrapper] = found.GetValueOrDefault(wrapper) + 1;
            }
            yield return instruction;
        }
        Dictionary<string, int> expected = source.FieldType.Name switch
        {
            "BloodDemonFormPower" => new() { [nameof(FindAutomaticTarget)] = 1, [nameof(ApplyFromPowerAsync)] = 1 },
            "HeartBrand" => new() { [nameof(AnyOwnedForCard)] = 1 },
            "ReviveCandle" => new() { [nameof(PreviewForCard)] = 1, [nameof(RecordRevivedForCard)] = 1, [nameof(FirstOwnedForCard)] = 2 },
            "HeartCandlePower" => new() { [nameof(RecordConsumedForPower)] = 1 },
            _ => throw new InvalidOperationException("Unsupported candle source: " + source.FieldType.Name)
        };
        if (found.Count != expected.Count || expected.Any(entry => found.GetValueOrDefault(entry.Key) != entry.Value))
            throw new InvalidOperationException("Unexpected candle call sites in " + source.FieldType.Name);
    }
    private static bool IsLinq(MethodInfo method, string name, Type argument) =>
        method.DeclaringType == typeof(Enumerable) && method.Name == name && method.IsGenericMethod
        && method.GetGenericArguments().SequenceEqual(new[] { argument }) && method.GetParameters().Length == 2
        && method.GetParameters()[1].ParameterType == typeof(Func<,>).MakeGenericType(argument, typeof(bool));

    internal static void RecordOwnConsumed(Creature target, Creature applier, decimal amount)
    {
        if (amount <= 0) return;
        var key = (target, applier);
        if (!Accounts.TryGetValue(key, out var account)) Accounts[key] = account = new();
        var recycled = Math.Min(amount, account.OutstandingRevived);
        account.OutstandingRevived -= recycled;
        account.OriginalConsumed += amount - recycled;
    }
    internal static void RecordOwnBurst(Creature target, Creature applier, decimal spentPool, decimal actualDamage)
    {
        if (spentPool <= 0) return;
        var key = (target, applier);
        if (!Accounts.TryGetValue(key, out var account)) Accounts[key] = account = new();
        var recycled = Math.Min(spentPool, account.OutstandingRevived);
        account.OutstandingRevived -= recycled;
        account.OriginalConsumed += Math.Max(actualDamage, 0) * (spentPool - recycled) / spentPool;
    }
    internal static decimal PreviewOwnedRevive(Creature target, Creature applier, decimal ratio)
    {
        if (ratio <= 0 || !Accounts.TryGetValue((target, applier), out var account)) return 0;
        var eligible = Math.Max(Math.Ceiling(account.OriginalConsumed * ratio) - account.TotalRevived, 0);
        var capacity = Math.Max(target.CurrentHp - (FindOwned(target.Powers, applier)?.Amount ?? 0), 0);
        return Math.Min(eligible, capacity);
    }
    internal static void RecordOwnRevived(Creature target, Creature applier, decimal amount)
    {
        if (amount <= 0) return;
        var key = (target, applier);
        if (!Accounts.TryGetValue(key, out var account)) Accounts[key] = account = new();
        account.OutstandingRevived += amount; account.TotalRevived += amount;
    }
    private static void RecordConsumedForPower(Creature target, decimal amount, PowerModel source)
    { if (source.Applier != null) RecordOwnConsumed(target, source.Applier, amount); _recordConsumed(target, amount); }
    private static decimal PreviewForCard(Creature target, decimal ratio, CardModel source) => PreviewOwnedRevive(target, source.Owner.Creature, ratio);
    private static void RecordRevivedForCard(Creature target, decimal amount, CardModel source)
    { RecordOwnRevived(target, source.Owner.Creature, amount); _recordRevived(target, amount); }
    internal static void Clear() => Accounts.Clear();
    private static void Cleanup()
    {
        foreach (var key in Accounts.Keys.Where(key => key.Target.CombatState == null || key.Target.CurrentHp <= 0).ToArray()) Accounts.Remove(key);
    }
    private static void DescriptionPostfix(LocString __instance, ref string __result)
    {
        if (__instance.LocTable != "powers" || __instance.LocEntryKey is not
            ("ENTELECHIA-HEART_CANDLE_POWER.description" or "ENTELECHIA-HEART_CANDLE_POWER.smartDescription" or "HEART_CANDLE_POWER.description" or "HEART_CANDLE_POWER.smartDescription")) return;
        bool chinese = __result.Any(character => character is >= '\u4e00' and <= '\u9fff');
        __result += chinese ? " 仅施加者的攻击可触发；不同施加者的心烛池与复燃记录独立。" : " Only its applier's attacks can trigger it. Each applier has a separate pool and rekindling record.";
        if (__instance.Variables.TryGetValue("ApplierName", out var name)) __result += chinese ? $" 施加者：{name}。" : $" Applier: {name}.";
    }
}
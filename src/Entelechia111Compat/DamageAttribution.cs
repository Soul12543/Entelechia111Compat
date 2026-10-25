using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using BaseLib.Utils;
using Entelechia.EntelechiaCode;
using Entelechia.EntelechiaCode.Powers;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.ValueProps;

namespace Entelechia111Compat;

internal static class DamageAttribution
{
    internal sealed record Share(ulong? PlayerId, int Weight);
    internal sealed record Source(Creature Target, string Key, IReadOnlyList<Share> Shares);
    private sealed class BleedAccount
    {
        internal SortedDictionary<ulong,int> Known = [];
        internal int Unknown;
        internal int Total => Known.Values.Sum() + Unknown;
    }
    private static readonly ConditionalWeakTable<PowerModel,BleedAccount> Bleed = new();
    private static readonly ConditionalWeakTable<DamageResult,Source> Results = new();
    private static readonly AsyncLocal<Source?> CurrentDamage = new();
    private static readonly AsyncLocal<Creature?> PassiveApplier = new();
    internal static void Install(Harmony harmony,Assembly original)
    {
        foreach (var type in new[] { HeartCandleOwnershipPatch.TargetTypeName, BloodHarvestOwnershipPatch.BloodlossTypeName })
            harmony.Patch(HeartCandleOwnershipPatch.Body(original,type,type == HeartCandleOwnershipPatch.TargetTypeName ? "AfterDamageGiven" : "AfterSideTurnStart"),
                transpiler:new(typeof(DamageAttribution),nameof(SourceTranspiler)));
        harmony.Patch(AccessTools.Method(typeof(Creature),nameof(Creature.LoseHpInternal)),postfix:new(typeof(DamageAttribution),nameof(TagPostfix)));
        harmony.Patch(AccessTools.Method(typeof(Hook),nameof(Hook.AfterPowerAmountChanged)),postfix:new(typeof(DamageAttribution),nameof(PowerChangedPostfix)));

    }
    private static IEnumerable<CodeInstruction> SourceTranspiler(IEnumerable<CodeInstruction> instructions,MethodBase __originalMethod)
    {
        var source = AccessTools.Field(__originalMethod.DeclaringType,"<>4__this");
        int damage = 0, applications = 0;
        foreach (var instruction in instructions)
        {
            string? wrapper = null;
            if (instruction.operand is MethodInfo call)
            {
                if (call.DeclaringType == typeof(TurnStateTracker) && call.Name == nameof(TurnStateTracker.LoseHpTracking))
                { wrapper = nameof(LoseHpWithSourceAsync); damage++; }
                else if (source.FieldType.Name == "HeartCandlePower" && call.DeclaringType == typeof(CommonActions) && call.Name == "Apply"
                    && call.IsGenericMethod && call.GetGenericArguments().SequenceEqual(new[] { typeof(BloodlossPower) }))
                { wrapper = nameof(ApplyPassiveBleedAsync); applications++; }
            }
            if (wrapper != null)
            {
                yield return HeartCandleOwnershipPatch.LoadSource(instruction,source);
                yield return new CodeInstruction(OpCodes.Ldfld,source);
                instruction.operand = AccessTools.Method(typeof(DamageAttribution),wrapper);
            }
            yield return instruction;
        }
        if (damage != 1 || applications != (source.FieldType.Name == "HeartCandlePower" ? 1 : 0))
            throw new InvalidOperationException("Unexpected attribution call sites in " + source.FieldType.Name);
    }
    internal static async Task LoseHpWithSourceAsync(PlayerChoiceContext? context,Creature target,decimal amount,ValueProp props,Creature? dealer,CardModel? card,CardPlay? play,PowerModel source)
    {
        var previous = CurrentDamage.Value;
        CurrentDamage.Value = Describe(source,dealer);
        try { await TurnStateTracker.LoseHpTracking(context,target,amount,props,dealer,card,play); }
        finally { CurrentDamage.Value = previous; }
    }
    internal static async Task<BloodlossPower?> ApplyPassiveBleedAsync(PlayerChoiceContext context,Creature target,CardModel? card,decimal amount,bool silent,PowerModel source)
    {
        var previous = PassiveApplier.Value;
        PassiveApplier.Value = source.Applier;
        try { return await CommonActions.Apply<BloodlossPower>(context,target,card,amount,silent); }
        finally { PassiveApplier.Value = previous; }
    }
    internal static async Task<T> WithSourceAsync<T>(Source source, Func<Task<T>> action)
    {
        var previous = CurrentDamage.Value; CurrentDamage.Value = source;
        try { return await action(); } finally { CurrentDamage.Value = previous; }
    }
    private static void TagPostfix(Creature __instance,ValueProp props,DamageResult __result)
    {
        var source = CurrentDamage.Value;
        if (source != null && ReferenceEquals(source.Target,__instance))
            Tag(__result,source);
    }
    internal static void Tag(DamageResult result,Source source) { Results.Remove(result); Results.Add(result,source); }
    internal static bool TryGet(DamageResult result,out Source source) => Results.TryGetValue(result,out source!);
    private static async Task PowerChangedPostfix(Task __result,PowerModel power,decimal amount,Creature? applier)
    {
        await __result;
        if (power is BloodlossPower) TrackBleed(power,amount,applier ?? PassiveApplier.Value);
    }
    internal static void TrackBleed(PowerModel power,decimal offset,Creature? applier)
    {
        int current = Math.Max(power.Amount,0);
        if (!Bleed.TryGetValue(power,out var account))
        {
            account = new() { Unknown = Math.Max(current-(int)offset,0) };
            Bleed.Add(power,account);
        }
        int delta = current-account.Total;
        if (delta > 0)
        {
            if (applier?.Player != null) account.Known[applier.Player.NetId] = account.Known.GetValueOrDefault(applier.Player.NetId) + delta;
            else account.Unknown += delta;
        }
        else if (delta < 0) Resize(account,current);
    }
    private static void Resize(BleedAccount account,int total)
    {
        var shares = Shares(account);
        var amounts = Allocate(total,shares);
        account.Known.Clear(); account.Unknown = 0;
        for (int i=0;i<shares.Count;i++)
            if (shares[i].PlayerId is { } id) account.Known[id] = amounts[i]; else account.Unknown = amounts[i];
    }
    private static IReadOnlyList<Share> Shares(BleedAccount account) => account.Known.Where(pair=>pair.Value>0)
        .Select(pair=>new Share(pair.Key,pair.Value)).Concat(account.Unknown>0 ? new[] { new Share(null,account.Unknown) } : []).ToArray();
    private static BleedAccount Reconcile(PowerModel power)
    {
        var account = Bleed.GetValue(power,p => new() { Unknown=Math.Max(p.Amount,0) });
        if (account.Total < power.Amount) account.Unknown += power.Amount-account.Total;
        else if (account.Total > Math.Max(power.Amount,0)) Resize(account,Math.Max(power.Amount,0));
        return account;
    }
    internal static Source Describe(PowerModel power,Creature? dealer = null)
    {
        if (power is BloodlossPower) return new(power.Owner,"ENTELECHIA-BLOODLOSS_POWER",Shares(Reconcile(power)));
        var applier = power.Applier ?? dealer;
        return new(power.Owner,"ENTELECHIA-HEART_CANDLE_POWER",[new(applier?.Player?.NetId,1)]);
    }
    internal static CompatStatePatch.Payload CaptureBleed(PowerModel power)
    {
        var account = Reconcile(power);
        return new() { Kind="bleed", BleedSources=new(account.Known), UnknownBleed=account.Unknown };
    }
    internal static void RestoreBleed(PowerModel power,CompatStatePatch.Payload? payload)
    {
        Bleed.Remove(power);
        if (payload != null) Bleed.Add(power,new() { Known=new(payload.BleedSources ?? []), Unknown=payload.UnknownBleed });
    }
    internal static int[] Allocate(int total,IReadOnlyList<Share> shares)
    {
        var amounts = new int[shares.Count];
        long weight = shares.Sum(share=>(long)Math.Max(share.Weight,0));
        if (total<=0 || weight<=0) return amounts;
        var remainders = new long[shares.Count];
        for (int i=0;i<shares.Count;i++)
        {
            long value=(long)total*Math.Max(shares[i].Weight,0);
            amounts[i]=(int)(value/weight); remainders[i]=value%weight;
        }
        int left=total-amounts.Sum();
        foreach (var index in Enumerable.Range(0,shares.Count).OrderByDescending(i=>remainders[i]).ThenBy(i=>shares[i].PlayerId ?? ulong.MaxValue).Take(left)) amounts[index]++;
        return amounts;
    }
}
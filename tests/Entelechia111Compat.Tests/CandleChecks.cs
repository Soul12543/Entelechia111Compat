using System.Reflection;
using System.Runtime.CompilerServices;
using Entelechia111Compat;
using Entelechia.EntelechiaCode;
using Entelechia.EntelechiaCode.Cards;
using Entelechia.EntelechiaCode.Powers;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;

internal static class CandleChecks
{
    private const string HarmonyId = "Entelechia111Compat.Tests.Candle";
    private static int DamageCalls, Removed;
    private static decimal Lost;
    private static Creature? LastDealer;
    private static HeartCandlePower? RecursionPower;
    private static string Text = "";

    internal static async Task SourcesAndStacking()
    {
        using var scope = new Scope();
        var a = PlayerCreature(); var b = PlayerCreature(); var enemy = Enemy();
        var pa = Power<HeartCandlePower>(enemy, a, 10); var pb = Power<HeartCandlePower>(enemy, b, 20);
        Add(enemy, pb, Power<HeartCandlePower>(enemy), pa);
        Assert.That(pa.InstanceType == PowerInstanceType.InstancedPerApplier, "Candle needs native per-applier stacking.");
        Assert.That(ReferenceEquals(PowerCmd.FindExistingInstanceForStacking(pa, enemy, a), pa)
            && ReferenceEquals(PowerCmd.FindExistingInstanceForStacking(pa, enemy, b), pb), "Native lookup must keep A and B separate.");
        Assert.That(Power<StrengthPower>(a).InstanceType == PowerInstanceType.None
            && Power<BloodHarvestPower>(enemy, a).InstanceType == PowerInstanceType.InstancedPerApplier, "Candle and harvest patches must coexist without changing other powers.");
        Assert.That(ReferenceEquals(HeartCandleOwnershipPatch.FirstOwnedForCard(enemy.Powers, _ => true, Card(a)), pa), "Rekindle must find its owner's pool, including when it comes last.");
        var foreignOnly = Enemy(); Add(foreignOnly, Power<HeartCandlePower>(foreignOnly, b));
        Assert.That(!HeartCandleOwnershipPatch.AnyOwnedForCard(foreignOnly.Powers, _ => true, Card(a)), "A teammate's candle must not count as A's existing candle.");
        var form = Power<BloodDemonFormPower>(a);
        var combat = Combat([a, b], [foreignOnly, enemy]); a.CombatState = combat;
        var applyField = AccessTools.Field(typeof(HeartCandleOwnershipPatch), "_apply");
        var originalApply = applyField.GetValue(null);
        Creature? capturedApplier = null; Creature? capturedTarget = null; CardModel? capturedCard = null;
        var capture = new Func<PlayerChoiceContext, Creature, CardModel?, decimal, bool, Creature?, Task>((ctx, target, card, percent, silent, applier) =>
        { capturedApplier = applier; capturedTarget = target; capturedCard = card; return Task.CompletedTask; });
        applyField.SetValue(null, capture);
        try
        {
            var card = Card(b);
            await (Task)AccessTools.Method(typeof(HeartCandlePower), "ApplyPercent").Invoke(null, new object?[] { new ThrowingPlayerChoiceContext(), enemy, card, 12m, true })!;
            Assert.That(ReferenceEquals(capturedApplier, b) && ReferenceEquals(capturedCard, card), "Real ApplyPercent entry must attribute card applications to their owner.");
            await form.AfterPlayerTurnStart(new ThrowingPlayerChoiceContext(), a.Player!);
            Assert.That(ReferenceEquals(capturedApplier, a) && ReferenceEquals(capturedTarget, foreignOnly) && capturedCard == null,
                "Real passive hook must choose the first enemy lacking A's candle and inject A even without a card.");
        }
        finally { applyField.SetValue(null, originalApply); }
        await (Task)AccessTools.Method(typeof(HeartCandlePower), "ApplyPercent").Invoke(null, new object?[] { new ThrowingPlayerChoiceContext(), enemy, null, 12m, true })!;
        Assert.That(pa.Amount == 10 && pb.Amount == 20, "Unattributed application must not steal an existing pool.");
        foreach (var item in new[] { (typeof(HeartCandlePower), "AfterDamageGiven", 1), (typeof(BloodDemonFormPower), "AfterPlayerTurnStart", 2), (typeof(HeartBrand), "OnPlay", 1), (typeof(ReviveCandle), "OnPlay", 4) })
        {
            var body = HeartCandleOwnershipPatch.Body(item.Item1.Assembly, item.Item1.FullName!, item.Item2);
            int count = PatchProcessor.GetCurrentInstructions(body).Count(i => i.operand is MethodInfo m && m.DeclaringType == typeof(HeartCandleOwnershipPatch));
            Assert.That(count == item.Item3, "Actual original candle call sites must all be redirected: " + item.Item1.Name);
        }
    }

    internal static async Task OwnDamageOnly()
    {
        using var scope = new Scope(commands: true);
        var a = PlayerCreature(); var b = PlayerCreature(); var enemy = Enemy();
        var combat = Combat([a, b], [enemy]); a.CombatState = b.CombatState = enemy.CombatState = combat;
        var candle = Power<HeartCandlePower>(enemy, a, 50); var card = Card(a);
        Add(enemy, candle);
        var hit = new DamageResult(enemy, DamageProps.card) { UnblockedDamage = 4 };
        Reset();
        await candle.AfterDamageGiven(new ThrowingPlayerChoiceContext(), b, hit, DamageProps.card, enemy, Card(b));
        await candle.AfterDamageGiven(new ThrowingPlayerChoiceContext(), null, hit, DamageProps.card, enemy, card);
        await Power<HeartCandlePower>(enemy, null, 20).AfterDamageGiven(new ThrowingPlayerChoiceContext(), a, hit, DamageProps.card, enemy, card);
        Assert.That(DamageCalls == 0 && candle.Amount == 50 && HeartCandleLedger.GetTotalConsumed(enemy) == 0, "Foreign and missing appliers must cause no HP loss, pool consumption or ledger changes.");
        RecursionPower = candle;
        await candle.AfterDamageGiven(new ThrowingPlayerChoiceContext(), a, hit, DamageProps.card, enemy, card);
        RecursionPower = null;
        Assert.That(DamageCalls == 1 && Lost == 8 && ReferenceEquals(LastDealer, a) && candle.Amount == 42 && enemy.CurrentHp == 92,
            "Own attacks retain the ordinary 2x multiplier, attribution and actual pool consumption; nested candle damage must not recurse.");
        Assert.That(HeartCandleOwnershipPatch.PreviewOwnedRevive(enemy, a, .5m) == 4
            && HeartCandleOwnershipPatch.PreviewOwnedRevive(enemy, b, .5m) == 0
            && HeartCandleLedger.GetTotalConsumed(enemy) == 8, "Rekindling is private; original aggregate totals must remain valid.");
        await candle.AfterDamageGiven(new ThrowingPlayerChoiceContext(), a, new DamageResult(enemy, DamageProps.card), DamageProps.card, enemy, card);
        await candle.AfterDamageGiven(new ThrowingPlayerChoiceContext(), a, hit, DamageProps.nonCardHpLoss, enemy, card);
        Assert.That(DamageCalls == 1, "Fully blocked and non-attack damage must still be ineligible.");
        Set(candle, "_amount", 2);
        await candle.AfterDamageGiven(new ThrowingPlayerChoiceContext(), a, hit, DamageProps.card, enemy, card);
        Assert.That(Lost == 10 && candle.Amount == 0 && Removed == 1, "Damage stays capped by the remaining pool and removes the depleted instance.");
    }

    internal static Task RekindlingLedger()
    {
        using var scope = new Scope();
        var a = PlayerCreature(); var b = PlayerCreature(); var enemy = Enemy();
        var pa = Power<HeartCandlePower>(enemy, a, 10); Add(enemy, Power<HeartCandlePower>(enemy, b, 90), pa);
        HeartCandleOwnershipPatch.RecordOwnConsumed(enemy, a, 20);
        HeartCandleOwnershipPatch.RecordOwnConsumed(enemy, b, 10);
        Assert.That(HeartCandleOwnershipPatch.PreviewOwnedRevive(enemy, a, .5m) == 10
            && HeartCandleOwnershipPatch.PreviewOwnedRevive(enemy, b, .5m) == 5, "Each source keeps its own consumed balance and capacity.");
        HeartCandleOwnershipPatch.RecordOwnRevived(enemy, a, 10);
        HeartCandleOwnershipPatch.RecordOwnConsumed(enemy, a, 10);
        Assert.That(HeartCandleOwnershipPatch.PreviewOwnedRevive(enemy, a, .5m) == 0
            && HeartCandleOwnershipPatch.PreviewOwnedRevive(enemy, a, 1m) == 10
            && HeartCandleOwnershipPatch.PreviewOwnedRevive(enemy, b, .5m) == 5, "Recycled damage must not create new natural credit, and A cannot use B's balance.");
        Set(pa, "_amount", 100);
        Assert.That(HeartCandleOwnershipPatch.PreviewOwnedRevive(enemy, a, 1) == 0
            && HeartCandleOwnershipPatch.PreviewOwnedRevive(enemy, a, 0) == 0, "Rekindling retains its original HP capacity and zero-ratio boundaries.");
        HeartCandleLedger.Clear();
        Assert.That(HeartCandleOwnershipPatch.PreviewOwnedRevive(enemy, b, 1) == 0, "Original combat-reset hook must also clear private accounts.");
        return Task.CompletedTask;
    }

    internal static Task Tooltips()
    {
        using var scope = new Scope();
        scope.Harmony.Patch(AccessTools.Method(typeof(LocString), nameof(LocString.GetFormattedText)), prefix: new(typeof(CandleChecks), nameof(TextPrefix)));
        Text = "心烛伤害。";
        var loc = new LocString("powers", "ENTELECHIA-HEART_CANDLE_POWER.smartDescription"); loc.Add("ApplierName", "A");
        Assert.That(loc.GetFormattedText().Contains("仅施加者") && loc.GetFormattedText().Contains("施加者：A"), "Candle tooltip must explain ownership.");
        Text = "Heart Candle damage.";
        Assert.That(loc.GetFormattedText().Contains("Only its applier"), "English candle tooltip must explain ownership.");
        Assert.That(new LocString("powers", "STRENGTH_POWER.description").GetFormattedText() == Text, "Other descriptions must stay unchanged.");
        return Task.CompletedTask;
    }

    internal static Task ImmortalApplication()
    {
        using var scope = new Scope();
        var a = PlayerCreature();
        var absent = Power<ImmortalBloodlinePower>(a, a, 0); absent.ApplyInternal(a, 0, true);
        Assert.That(!a.Powers.Contains(absent), "Actual engine ignores a zero-amount power.");
        var active = Power<ImmortalBloodlinePower>(a, a, 0);
        active.ApplyInternal(a, ImmortalBloodlinePatch.ApplicationAmount(0, false), true);
        Assert.That(a.Powers.Contains(active) && active.Amount == 1, "Upgraded protection needs a positive native amount to exist in the effect bar.");
        Assert.That(ImmortalBloodlinePatch.ApplicationAmount(0, true) == 0 && ImmortalBloodlinePatch.ApplicationAmount(4, false) == 4,
            "Repeated upgrades add no delayed loss; normal cards retain four.");
        a.SetCurrentHpInternal(5);
        Assert.That(active.ModifyHpLostAfterOsty(a, 9, DamageProps.cardHpLoss, a, Card(a)) == 4, "Actual original protection caps self HP loss at one.");
        a.SetCurrentHpInternal(1);
        Assert.That(active.ModifyHpLostAfterOsty(a, 9, DamageProps.cardHpLoss, a, Card(a)) == 0, "At one HP self damage is prevented, so no real HP loss occurs.");
        var body = HeartCandleOwnershipPatch.Body(typeof(ImmortalBloodline).Assembly, typeof(ImmortalBloodline).FullName!, "OnPlay");
        Assert.That(PatchProcessor.GetCurrentInstructions(body).Count(i => i.operand is MethodInfo m && m.DeclaringType == typeof(ImmortalBloodlinePatch)) == 1,
            "The actual original card must use the repaired application.");
        return Task.CompletedTask;
    }

    internal static async Task ImmortalExpiry()
    {
        using var scope = new Scope(commands: true);
        var a = PlayerCreature(); var b = PlayerCreature();
        foreach (var pair in new[] { (1, 0), (4, 4), (5, 4), (8, 8), (9, 8) })
        {
            var power = Power<ImmortalBloodlinePower>(a, a, pair.Item1); Reset();
            await power.AfterPlayerTurnStart(new ThrowingPlayerChoiceContext(), b.Player!);
            Assert.That(DamageCalls == 0 && Removed == 0, "Another player's turn must not expire protection.");
            await power.AfterPlayerTurnStart(new ThrowingPlayerChoiceContext(), a.Player!);
            Assert.That(Lost == pair.Item2 && Removed == 1, "Upgraded, normal and mixed applications must expire with exactly the original delayed HP cost.");
        }
    }

    private static void Reset() { DamageCalls = Removed = 0; Lost = 0; LastDealer = null; RecursionPower = null; }
    private static bool LosePrefix(PlayerChoiceContext? context, Creature creature, decimal amount, ValueProp props, Creature? dealer, CardModel? cardSource, ref Task __result)
    {
        DamageCalls++; Lost += amount; LastDealer = dealer;
        if (RecursionPower != null)
            RecursionPower.AfterDamageGiven(context!, dealer, new DamageResult(creature, DamageProps.card) { UnblockedDamage = 1 }, DamageProps.card, creature, cardSource).GetAwaiter().GetResult();
        creature.SetCurrentHpInternal(creature.CurrentHp - amount);
        __result = Task.CompletedTask; return false;
    }
    private static bool NativeDamagePrefix(PlayerChoiceContext choiceContext, Creature target, decimal amount, ValueProp props, Creature? dealer, CardModel? cardSource, ref Task<IEnumerable<DamageResult>> __result)
    {
        Task completion = Task.CompletedTask;
        int before = target.CurrentHp;
        LosePrefix(choiceContext, target, amount, props, dealer, cardSource, ref completion);
        __result = Task.FromResult<IEnumerable<DamageResult>>(new[] { new DamageResult(target, props) { UnblockedDamage = before - target.CurrentHp } });
        return false;
    }
    private static bool ModifyPrefix(PowerModel power, decimal offset, ref Task<int> __result)
    { Set(power, "_amount", power.Amount + (int)offset); __result = Task.FromResult(power.Amount); return false; }
    private static bool RemovePrefix(PowerModel power, ref Task __result)
    { Removed++; Set(power, "_amount", 0); __result = Task.CompletedTask; return false; }
    private static bool TextPrefix(ref string __result) { __result = Text; return false; }
    private sealed class Scope : IDisposable
    {
        internal readonly Harmony Harmony = new(HarmonyId);
        internal Scope(bool commands = false)
        {
            try
            {
                if (commands) Commands();
                BloodHarvestOwnershipPatch.Install(Harmony, typeof(HeartCandlePower).Assembly);
                HeartCandleOwnershipPatch.Install(Harmony, typeof(HeartCandlePower).Assembly);
                ImmortalBloodlinePatch.Install(Harmony, typeof(HeartCandlePower).Assembly);
                HeartCandleLedger.Clear();
            }
            catch { Harmony.UnpatchAll(HarmonyId); throw; }
        }
        internal void Commands()
        {
            Harmony.Patch(AccessTools.Method(typeof(CreatureCmd), nameof(CreatureCmd.Damage), new[] { typeof(PlayerChoiceContext), typeof(Creature), typeof(decimal), typeof(ValueProp), typeof(Creature), typeof(CardModel), typeof(CardPlay) }), prefix: new(typeof(CandleChecks), nameof(NativeDamagePrefix)));
            Harmony.Patch(AccessTools.Method(typeof(TurnStateTracker), "LoseHpTracking"), prefix: new(typeof(CandleChecks), nameof(LosePrefix)));
            Harmony.Patch(AccessTools.Method(typeof(PowerCmd), nameof(PowerCmd.ModifyAmount)), prefix: new(typeof(CandleChecks), nameof(ModifyPrefix)));
            Harmony.Patch(AccessTools.Method(typeof(PowerCmd), nameof(PowerCmd.Remove), new[] { typeof(PowerModel) }), prefix: new(typeof(CandleChecks), nameof(RemovePrefix)));
        }
        public void Dispose() { HeartCandleLedger.Clear(); Harmony.UnpatchAll(HarmonyId); }
    }
    public class CombatProxy : DispatchProxy
    {
        internal IReadOnlyList<Creature> Players = [], Enemies = [];
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
        { "get_PlayerCreatures" => Players, "get_Enemies" => Enemies, "get_Encounter" => null, _ => throw new NotSupportedException(method?.Name) };
    }
    private static ICombatState Combat(Creature[] players, Creature[] enemies)
    { var state = DispatchProxy.Create<ICombatState, CombatProxy>(); var proxy = (CombatProxy)(object)state; proxy.Players = players; proxy.Enemies = enemies; return state; }
    private static T Raw<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    private static T Power<T>(Creature owner, Creature? applier = null, int amount = 1) where T : PowerModel
    {
        var power = Raw<T>(); Set(power, "<IsMutable>k__BackingField", true);
        Set(power, "<Id>k__BackingField", new ModelId("POWER", typeof(T).Name));
        Set(power, "_owner", owner); Set(power, "_applier", applier); Set(power, "_amount", amount); return power;
    }
    private static Creature PlayerCreature()
    { var player = Raw<Player>(); var creature = new Creature(player, 30, 60); Set(player, "<Creature>k__BackingField", creature); return creature; }
    private static Creature Enemy() => new(null!, 100, 100);
    private static CardModel Card(Creature owner)
    { var card = Raw<DiscontinuousPulse>(); Set(card, "<IsMutable>k__BackingField", true); Set(card, "_owner", owner.Player); Set(card, "<Type>k__BackingField", CardType.Attack); return card; }
    private static void Add(Creature creature, params PowerModel[] powers) => ((List<PowerModel>)AccessTools.Field(typeof(Creature), "_powers").GetValue(creature)!).AddRange(powers);
    private static void Set(object value, string field, object? data) => (AccessTools.Field(value.GetType(), field) ?? throw new MissingFieldException(value.GetType().FullName, field)).SetValue(value, data);
}
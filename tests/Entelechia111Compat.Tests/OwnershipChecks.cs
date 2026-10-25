using System.Reflection;
using System.Runtime.CompilerServices;
using Entelechia111Compat;
using Entelechia.EntelechiaCode.Cards;
using Entelechia.EntelechiaCode.Powers;
using Entelechia.EntelechiaCode.Relics;
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

internal static class OwnershipChecks
{
    private const string HarmonyId = "Entelechia111Compat.Tests.Harvest";
    private static decimal Healed;
    private static Creature? HealedCreature;
    private static int Changes;
    private static string Description = "";

    internal static Task InstallAndStacking()
    {
        using var scope = new Scope();
        var a = PlayerCreature(); var b = PlayerCreature(); var enemy = Enemy();
        var pa = Power<BloodHarvestPower>(enemy, a); var pb = Power<BloodHarvestPower>(enemy, b);
        Add(enemy, pa, pb);
        Assert.That(pa.InstanceType == PowerInstanceType.InstancedPerApplier, "Harvest must use native per-applier instances.");
        Assert.That(ReferenceEquals(PowerCmd.FindExistingInstanceForStacking(pa, enemy, a), pa), "Reapplying A must find A's existing stack.");
        Assert.That(ReferenceEquals(PowerCmd.FindExistingInstanceForStacking(pa, enemy, b), pb), "Reapplying B must find B's existing stack.");
        Assert.That(PowerCmd.FindExistingInstanceForStacking(pa, enemy, PlayerCreature()) == null, "A new applier must not inherit another player's stacks.");
        Assert.That(Power<StrengthPower>(a).InstanceType == PowerInstanceType.None, "Other powers must retain vanilla stacking.");
        return Task.CompletedTask;
    }

    internal static async Task DamageOwnershipAndCaps()
    {
        using var scope = new Scope();
        scope.Harmony.Patch(AccessTools.Method(typeof(Entelechia.EntelechiaCode.TurnStateTracker), "HealTracking"), prefix: new(typeof(OwnershipChecks), nameof(HealPrefix)));
        scope.Harmony.Patch(AccessTools.Method(typeof(PowerCmd), nameof(PowerCmd.ModifyAmount)), prefix: new(typeof(OwnershipChecks), nameof(ModifyPrefix)));
        scope.Harmony.Patch(AccessTools.Method(typeof(PowerCmd), nameof(PowerCmd.Remove), new[] { typeof(PowerModel) }), prefix: new(typeof(OwnershipChecks), nameof(RemovePrefix)));
        var a = PlayerCreature(); var b = PlayerCreature(); var enemy = Enemy();
        var card = Card(a); var harvest = Power<BloodHarvestPower>(enemy, a, 6);
        var result = new DamageResult(enemy, DamageProps.card) { UnblockedDamage = 1 };
        var foreignInstinct = Power<ClotInstinctPower>(b);
        Add(b, foreignInstinct, Power<ClottingBarrierPower>(b));
        BloodHarvestPower.ResetCardPlayHealLedger(); Healed = 0; Changes = 0; HealedCreature = null;
        await harvest.AfterDamageGiven(new ThrowingPlayerChoiceContext(), b, result, DamageProps.card, enemy, Card(b));
        await harvest.AfterDamageGiven(new ThrowingPlayerChoiceContext(), null, result, DamageProps.card, enemy, card);
        Assert.That(harvest.Amount == 6 && Healed == 0 && Changes == 0 && !foreignInstinct.TriggeredThisTurn && b.Block == 0 && BloodHarvestPower.GetTriggerCountForCurrentCardPlay(card) == 0,
            "Foreign or missing dealers must have no healing, ledger or stack side effects.");
        for (int i = 0; i < 3; i++)
            await harvest.AfterDamageGiven(new ThrowingPlayerChoiceContext(), a, result, DamageProps.card, enemy, card);
        Assert.That(Healed == 6 && ReferenceEquals(HealedCreature, a) && harvest.Amount == 3 && Changes == 3,
            "Own attacks must retain 3 HP per hit, 6 HP per card cap and one stack consumed per hit.");
        Assert.That(BloodHarvestPower.GetTriggerCountForCurrentCardPlay(card) == 3, "Only eligible hits belong in the original trigger ledger.");
        var noOwner = Power<BloodHarvestPower>(enemy, null, 2);
        await noOwner.AfterDamageGiven(new ThrowingPlayerChoiceContext(), a, result, DamageProps.card, enemy, card);
        Assert.That(noOwner.Amount == 2 && Changes == 3, "Unknown-applier legacy stacks must not be assigned to the next attacker.");
        await harvest.AfterDamageGiven(new ThrowingPlayerChoiceContext(), a, new DamageResult(enemy, DamageProps.card), DamageProps.card, enemy, card);
        await harvest.AfterDamageGiven(new ThrowingPlayerChoiceContext(), a, result, DamageProps.nonCardHpLoss, enemy, card);
        Assert.That(harvest.Amount == 3 && Changes == 3, "Blocked hits and non-attack HP loss must remain ineligible.");
        Add(a, Power<BloodClanCourtPower>(a));
        BloodHarvestPower.ResetCardPlayHealLedger(); Healed = 0;
        for (int i = 0; i < 3; i++)
            await harvest.AfterDamageGiven(new ThrowingPlayerChoiceContext(), a, result, DamageProps.card, enemy, card);
        Assert.That(Healed == 8 && harvest.Amount == 0, "Court must retain 4 HP per hit, 8 HP per card and final-stack removal.");
    }

    internal static Task PassiveSourcesAndConsumers()
    {
        using var scope = new Scope();
        var a = PlayerCreature(); var b = PlayerCreature(); var enemy = Enemy();
        foreach (var source in new PowerModel[] { Power<BloodFeastPower>(a), Power<RoseStepPower>(a), Power<EmberBloodlinePower>(a) })
            Assert.That(BloodHarvestOwnershipPatch.ResolveAppliers(source, enemy).SequenceEqual(new[] { a }), "A passive harvest must belong to the source buff's owner.");
        // RelicModel initializes Godot native resources; its real call-site injection is checked below.
        var combat = DispatchProxy.Create<ICombatState, CombatProxy>();
        ((CombatProxy)(object)combat).Players = new[] { a, b };
        enemy.CombatState = combat;
        Add(a, Power<BloodClanCourtPower>(a)); Add(b, Power<BloodClanCourtPower>(b));
        Assert.That(BloodHarvestOwnershipPatch.ResolveAppliers(Power<BloodlossPower>(enemy), enemy).SequenceEqual(new[] { a, b }),
            "Bleeding must attribute separate harvest to each court holder in player order.");
        var pa = Power<BloodHarvestPower>(enemy, a); var pb = Power<BloodHarvestPower>(enemy, b);
        var unknown = Power<BloodHarvestPower>(enemy);
        Assert.That(ReferenceEquals(BloodHarvestOwnershipPatch.FirstOwnedHarvest(new PowerModel[] { pb, unknown, pa }, _ => true, Card(a)), pa),
            "Active consumers must skip other appliers and find their own stack even when it comes last.");
        Assert.That(BloodHarvestOwnershipPatch.FirstOwnedHarvest(new PowerModel[] { pb, unknown }, _ => true, Card(a)) == null,
            "Active consumers must not take foreign or unknown stacks.");
        var foreignEnemy = Enemy(); Add(foreignEnemy, Power<BloodHarvestPower>(foreignEnemy, b));
        var ownedEnemy = Enemy(); Add(ownedEnemy, Power<BloodHarvestPower>(ownedEnemy, a));
        Assert.That(ReferenceEquals(BloodHarvestOwnershipPatch.FirstOwnedHarvestTarget(new[] { foreignEnemy, ownedEnemy }, _ => true, Card(a)), ownedEnemy),
            "Discontinuous Pulse must skip an earlier enemy with only a teammate's harvest.");
        foreach (var entry in new[] { (typeof(BloodFeastPower), "CountHit", 1), (typeof(RoseStepPower), "AfterCardPlayed", 1),
            (typeof(EmberBloodlinePower), "AfterSideTurnStart", 2), (typeof(BloodlossPower), "AfterSideTurnStart", 1),
            (typeof(BloodDemonReplete), "AfterPreventingDeath", 1), (typeof(BloodDissect), "OnPlay", 1), (typeof(BloodDebtSettlement), "OnPlay", 1), (typeof(DiscontinuousPulse), "OnPlay", 2) })
        {
            var factory = AccessTools.DeclaredMethod(entry.Item1, entry.Item2);
            var state = factory.GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType;
            var body = AccessTools.Method(state, "MoveNext");
            var instructions = PatchProcessor.GetCurrentInstructions(body);
            int calls = instructions.Count(instruction => instruction.operand is MethodInfo method && method.DeclaringType == typeof(BloodHarvestOwnershipPatch));
            Assert.That(calls == entry.Item3, "Actual original DLL must have every audited application/consumer redirected: " + entry.Item1.Name);
        }
        return Task.CompletedTask;
    }

    internal static Task Tooltips()
    {
        using var scope = new Scope();
        scope.Harmony.Patch(AccessTools.Method(typeof(LocString), nameof(LocString.GetFormattedText)), prefix: new(typeof(OwnershipChecks), nameof(TextPrefix)));
        var harvest = new LocString("powers", "ENTELECHIA-BLOOD_HARVEST_POWER.smartDescription"); harvest.Add("ApplierName", "Player A");
        Description = "攻击者回复3点生命。";
        var text = harvest.GetFormattedText();
        Assert.That(text.Contains("仅施加者") && text.Contains("Player A"), "Chinese tooltip must explain ownership and identify the applier.");
        Description = "Heal the attacker for 3 HP.";
        Assert.That(harvest.GetFormattedText().Contains("Only its applier"), "English tooltip must explain the new rule.");
        Assert.That(new LocString("powers", "STRENGTH_POWER.description").GetFormattedText() == Description, "Unrelated tooltips must remain unchanged.");
        return Task.CompletedTask;
    }

    private static bool HealPrefix(Creature creature, decimal amount, ref Task __result)
    { Healed += amount; HealedCreature = creature; __result = Task.CompletedTask; return false; }
    private static bool ModifyPrefix(PowerModel power, decimal offset, ref Task<int> __result)
    { Changes++; Set(power, "_amount", power.Amount + (int)offset); __result = Task.FromResult(power.Amount); return false; }
    private static bool RemovePrefix(PowerModel power, ref Task __result)
    { Changes++; Set(power, "_amount", 0); __result = Task.CompletedTask; return false; }
    private static bool TextPrefix(ref string __result) { __result = Description; return false; }

    private sealed class Scope : IDisposable
    {
        internal readonly Harmony Harmony = new(HarmonyId);
        internal Scope()
        {
            try { BloodHarvestOwnershipPatch.Install(Harmony, typeof(BloodHarvestPower).Assembly); }
            catch { Harmony.UnpatchAll(HarmonyId); throw; }
        }
        public void Dispose() => Harmony.UnpatchAll(HarmonyId);
    }
    public class CombatProxy : DispatchProxy
    {
        internal IReadOnlyList<Creature> Players = [];
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name == "get_PlayerCreatures" ? Players : throw new NotSupportedException(method?.Name);
    }
    private static T Raw<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    private static T Power<T>(Creature owner, Creature? applier = null, int amount = 1) where T : PowerModel
    {
        var power = Raw<T>(); Set(power, "<IsMutable>k__BackingField", true);
        Set(power, "<Id>k__BackingField", new ModelId("POWER", typeof(T).Name));
        Set(power, "_owner", owner); Set(power, "_applier", applier); Set(power, "_amount", amount);
        return power;
    }
    private static Creature PlayerCreature()
    {
        var player = Raw<Player>(); var creature = new Creature(player, 30, 60);
        Set(player, "<Creature>k__BackingField", creature); return creature;
    }
    private static Creature Enemy() => new(null!, 30, 60);
    private static CardModel Card(Creature owner)
    {
        var card = Raw<DiscontinuousPulse>(); Set(card, "<IsMutable>k__BackingField", true);
        Set(card, "_owner", owner.Player); Set(card, "<Type>k__BackingField", CardType.Attack); return card;
    }
    private static void Add(Creature creature, params PowerModel[] powers) => ((List<PowerModel>)AccessTools.Field(typeof(Creature), "_powers").GetValue(creature)!).AddRange(powers);
    private static void Set(object value, string field, object? data)
    {
        var target = AccessTools.Field(value.GetType(), field) ?? throw new MissingFieldException(value.GetType().FullName, field);
        target.SetValue(value, data);
    }
}
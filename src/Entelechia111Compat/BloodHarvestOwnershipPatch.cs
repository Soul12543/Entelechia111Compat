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

/// <summary>Keep harvest stacks and all their benefits private to their actual applier.</summary>
internal static class BloodHarvestOwnershipPatch
{
    internal const string TargetTypeName = "Entelechia.EntelechiaCode.Powers.BloodHarvestPower";
    internal const string BloodlossTypeName = "Entelechia.EntelechiaCode.Powers.BloodlossPower";
    internal const string CourtTypeName = "Entelechia.EntelechiaCode.Powers.BloodClanCourtPower";
    private const string PowerNamespace = "Entelechia.EntelechiaCode.Powers.";
    private const string CardNamespace = "Entelechia.EntelechiaCode.Cards.";

    internal static void Install(Harmony harmony, Assembly original)
    {
        var harvestType = original.GetType(TargetTypeName, throwOnError: true)!;
        if (!typeof(PowerModel).IsAssignableFrom(harvestType))
            throw new InvalidOperationException("BloodHarvestPower must remain a PowerModel.");
        // Inject the actual owning model at each no-card application, including after awaits.
        // This avoids guessing the current player, which is ambiguous in multiplayer.
        var applications = new (string Type, string Method)[]
        {
            (PowerNamespace + "BloodFeastPower", "CountHit"),
            (PowerNamespace + "RoseStepPower", "AfterCardPlayed"),
            (PowerNamespace + "EmberBloodlinePower", "AfterSideTurnStart"),
            (BloodlossTypeName, "AfterSideTurnStart"),
            ("Entelechia.EntelechiaCode.Relics.BloodDemonReplete", "AfterPreventingDeath")
        };
        var applicationBodies = applications.Select(entry => AsyncBody(original, entry.Type, entry.Method)).ToArray();
        var consumers = new[] { "BloodDissect", "BloodDebtSettlement" }
            .Select(name => AsyncBody(original, CardNamespace + name, "OnPlay")).ToArray();
        var damage = harvestType.GetMethod("AfterDamageGiven", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            ?? throw new MissingMethodException(TargetTypeName, "AfterDamageGiven");
        if (damage.ReturnType != typeof(Task))
            throw new InvalidOperationException("Unsupported BloodHarvestPower damage hook.");

        harmony.Patch(AccessTools.PropertyGetter(typeof(PowerModel), nameof(PowerModel.InstanceType)),
            postfix: Patch(nameof(InstanceTypePostfix)));
        harmony.Patch(damage, prefix: Patch(nameof(DamagePrefix)));
        harmony.Patch(AsyncBody(original, CardNamespace + "DiscontinuousPulse", "OnPlay"), transpiler: Patch(nameof(PulseConsumerTranspiler)));
        foreach (var body in applicationBodies)
            harmony.Patch(body, transpiler: Patch(nameof(ApplicationTranspiler)));
        foreach (var body in consumers)
            harmony.Patch(body, transpiler: Patch(nameof(ConsumerTranspiler)));
        harmony.Patch(AccessTools.Method(typeof(LocString), nameof(LocString.GetFormattedText)),
            postfix: Patch(nameof(DescriptionPostfix)));
    }

    private static HarmonyMethod Patch(string name) => new(typeof(BloodHarvestOwnershipPatch), name);
    private static bool IsHarvest(PowerModel power) => power.GetType().FullName == TargetTypeName;

    private static MethodInfo AsyncBody(Assembly original, string typeName, string methodName)
    {
        var type = original.GetType(typeName, throwOnError: true)!;
        var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            ?? throw new MissingMethodException(typeName, methodName);
        var state = method.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType
            ?? throw new InvalidOperationException($"{typeName}.{methodName} must remain async.");
        var owner = state.GetField("<>4__this", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (owner?.FieldType != type)
            throw new InvalidOperationException($"Unsupported async owner in {typeName}.{methodName}.");
        return AccessTools.Method(state, "MoveNext");
    }

    private static void InstanceTypePostfix(PowerModel __instance, ref PowerInstanceType __result)
    {
        if (IsHarvest(__instance))
            __result = PowerInstanceType.InstancedPerApplier;
    }

    private static bool DamagePrefix(PowerModel __instance, Creature? dealer, ref Task __result)
    {
        if (__instance.Applier != null && ReferenceEquals(dealer, __instance.Applier))
            return true;
        // Skip all side effects: healing, block, trigger bookkeeping and stack consumption.
        __result = Task.CompletedTask;
        return false;
    }

    private static IEnumerable<CodeInstruction> ApplicationTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        var source = __originalMethod.DeclaringType!.GetField("<>4__this")!;
        int replacements = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.operand is MethodInfo call && call.DeclaringType?.FullName == "BaseLib.Utils.CommonActions"
                && call.Name == "Apply" && call.IsGenericMethod
                && call.GetGenericArguments() is [var powerType] && powerType.FullName == TargetTypeName)
            {
                var parameters = call.GetParameters().Select(parameter => parameter.ParameterType).ToArray();
                string wrapper = parameters.SequenceEqual(new[] { typeof(PlayerChoiceContext), typeof(Creature), typeof(CardModel), typeof(decimal), typeof(bool) })
                    ? nameof(ApplyFromModelAsync)
                    : parameters.SequenceEqual(new[] { typeof(Creature), typeof(CardModel), typeof(decimal), typeof(bool) })
                        ? nameof(ApplyFromModelWithoutContextAsync)
                        : throw new InvalidOperationException("Unsupported harvest application overload.");
                yield return LoadOwner(instruction);
                yield return new CodeInstruction(OpCodes.Ldfld, source);
                instruction.operand = AccessTools.Method(typeof(BloodHarvestOwnershipPatch), wrapper).MakeGenericMethod(powerType);
                replacements++;
            }
            yield return instruction;
        }
        int expected = source.FieldType.Name == "EmberBloodlinePower" ? 2 : 1;
        if (replacements != expected)
            throw new InvalidOperationException($"Expected {expected} harvest applications in {source.FieldType.Name}; found {replacements}.");
    }

    private static IEnumerable<CodeInstruction> ConsumerTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        var source = __originalMethod.DeclaringType!.GetField("<>4__this")!;
        int replacements = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.operand is MethodInfo call && call.DeclaringType == typeof(Enumerable)
                && call.Name == nameof(Enumerable.FirstOrDefault) && call.IsGenericMethod
                && call.GetGenericArguments().SequenceEqual(new[] { typeof(PowerModel) })
                && call.GetParameters() is [_, var predicate] && predicate.ParameterType == typeof(Func<PowerModel, bool>))
            {
                yield return LoadOwner(instruction);
                yield return new CodeInstruction(OpCodes.Ldfld, source);
                instruction.operand = AccessTools.Method(typeof(BloodHarvestOwnershipPatch), nameof(FirstOwnedHarvest));
                replacements++;
            }
            yield return instruction;
        }
        if (replacements != 1)
            throw new InvalidOperationException($"Expected one harvest lookup in {source.FieldType.Name}; found {replacements}.");
    }

    private static IEnumerable<CodeInstruction> PulseConsumerTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        var source = __originalMethod.DeclaringType!.GetField("<>4__this")!;
        int targets = 0, lookups = 0;
        foreach (var instruction in instructions)
        {
            string? wrapper = null;
            if (instruction.operand is MethodInfo call && call.DeclaringType == typeof(Enumerable) && call.IsGenericMethod && call.GetParameters().Length == 2)
            {
                if (call.Name == nameof(Enumerable.FirstOrDefault) && call.GetGenericArguments().SequenceEqual(new[] { typeof(Creature) }))
                { wrapper = nameof(FirstOwnedHarvestTarget); targets++; }
                else if (call.Name == nameof(Enumerable.First) && call.GetGenericArguments().SequenceEqual(new[] { typeof(PowerModel) }))
                { wrapper = nameof(FirstOwnedHarvestRequired); lookups++; }
            }
            if (wrapper != null)
            {
                yield return LoadOwner(instruction);
                yield return new CodeInstruction(OpCodes.Ldfld, source);
                instruction.operand = AccessTools.Method(typeof(BloodHarvestOwnershipPatch), wrapper);
            }
            yield return instruction;
        }
        if (targets != 1 || lookups != 1) throw new InvalidOperationException("Unexpected DiscontinuousPulse harvest lookups.");
    }
    internal static Creature? FirstOwnedHarvestTarget(IEnumerable<Creature> enemies, Func<Creature, bool> predicate, CardModel source) =>
        enemies.FirstOrDefault(enemy => enemy.CurrentHp > 0 && FirstOwnedHarvest(enemy.Powers, power => power.Amount > 0, source) != null);
    private static PowerModel FirstOwnedHarvestRequired(IEnumerable<PowerModel> powers, Func<PowerModel, bool> predicate, CardModel source) =>
        FirstOwnedHarvest(powers, predicate, source) ?? throw new InvalidOperationException("The selected enemy no longer has owned harvest.");
    private static CodeInstruction LoadOwner(CodeInstruction call)
    {
        var load = new CodeInstruction(OpCodes.Ldarg_0);
        load.labels.AddRange(call.labels);
        load.blocks.AddRange(call.blocks);
        call.labels.Clear();
        call.blocks.Clear();
        return load;
    }

    internal static Task<T?> ApplyFromModelWithoutContextAsync<T>(Creature target, CardModel? card, decimal amount, bool silent, AbstractModel source)
        where T : PowerModel => ApplyFromModelAsync<T>(new ThrowingPlayerChoiceContext(), target, card, amount, silent, source);

    internal static async Task<T?> ApplyFromModelAsync<T>(PlayerChoiceContext context, Creature target, CardModel? card, decimal amount, bool silent, AbstractModel source)
        where T : PowerModel
    {
        T? first = null;
        foreach (var applier in ResolveAppliers(source, target))
        {
            var applied = await PowerCmd.Apply<T>(context, target, amount, applier, card, silent);
            first ??= applied;
        }
        return first;
    }

    internal static IEnumerable<Creature> ResolveAppliers(AbstractModel source, Creature target)
    {
        // Bleeding creates harvest through Blood Clan Court: each court holder owns its stack.
        if (source.GetType().FullName == BloodlossTypeName)
            return target.CombatState?.PlayerCreatures?.Where(creature => creature.Powers?.Any(power => power.GetType().FullName == CourtTypeName) == true) ?? [];
        Creature? owner = source switch
        {
            PowerModel power => power.Owner,
            RelicModel relic => relic.Owner?.Creature,
            _ => null
        };
        return owner?.Player != null ? [owner] : [];
    }

    internal static PowerModel? FirstOwnedHarvest(IEnumerable<PowerModel> powers, Func<PowerModel, bool> predicate, CardModel card) =>
        powers.FirstOrDefault(power => IsHarvest(power) && power.Applier != null
            && ReferenceEquals(power.Applier, card.Owner.Creature) && predicate(power));

    private static void DescriptionPostfix(LocString __instance, ref string __result)
    {
        if (__instance.LocTable != "powers")
            return;
        if (__instance.LocEntryKey is not ("ENTELECHIA-BLOOD_HARVEST_POWER.description" or "ENTELECHIA-BLOOD_HARVEST_POWER.smartDescription"
            or "BLOOD_HARVEST_POWER.description" or "BLOOD_HARVEST_POWER.smartDescription"))
            return;
        bool chinese = __result.Any(character => character is >= '\u4e00' and <= '\u9fff');
        __result += chinese ? " 仅施加者的攻击可触发；不同施加者的层数独立。" : " Only its applier can trigger this effect. Stacks are separate for each applier.";
        if (__instance.Variables.TryGetValue("ApplierName", out var name))
            __result += chinese ? $" 施加者：{name}。" : $" Applier: {name}.";
    }
}
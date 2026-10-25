using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.ValueProps;

namespace Entelechia111Compat;

internal static class ImmortalBloodlinePatch
{
    internal const string TargetTypeName = "Entelechia.EntelechiaCode.Powers.ImmortalBloodlinePower";
    private static Func<PlayerChoiceContext, Creature, decimal, ValueProp, Creature?, CardModel?, CardPlay?, Task> _loseHp = null!;
    internal static void Install(Harmony harmony, Assembly original)
    {
        var power = original.GetType(TargetTypeName, true)!;
        var tracker = original.GetType("Entelechia.EntelechiaCode.TurnStateTracker", true)!;
        _loseHp = AccessTools.Method(tracker, "LoseHpTracking")
            .CreateDelegate<Func<PlayerChoiceContext, Creature, decimal, ValueProp, Creature?, CardModel?, CardPlay?, Task>>();
        var body = HeartCandleOwnershipPatch.Body(original, "Entelechia.EntelechiaCode.Cards.ImmortalBloodline", "OnPlay");
        harmony.Patch(body, transpiler: new(typeof(ImmortalBloodlinePatch), nameof(ApplicationTranspiler)));
        harmony.Patch(AccessTools.DeclaredMethod(power, "AfterPlayerTurnStart"), prefix: new(typeof(ImmortalBloodlinePatch), nameof(TurnStartPrefix)));
    }
    private static IEnumerable<CodeInstruction> ApplicationTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        int replacements = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.operand is MethodInfo call && call.DeclaringType?.FullName == "BaseLib.Utils.CommonActions" && call.Name == "Apply"
                && call.IsGenericMethod && call.GetGenericArguments() is [var type] && type.FullName == TargetTypeName)
            {
                if (!call.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(PlayerChoiceContext), typeof(Creature), typeof(CardModel), typeof(decimal), typeof(bool) }))
                    throw new InvalidOperationException("Unsupported ImmortalBloodline application overload.");
                instruction.operand = AccessTools.Method(typeof(ImmortalBloodlinePatch), nameof(ApplyAsync)).MakeGenericMethod(type);
                replacements++;
            }
            yield return instruction;
        }
        if (replacements != 1) throw new InvalidOperationException("Expected one ImmortalBloodline application.");
    }
    internal static decimal ApplicationAmount(decimal requested, bool alreadyActive) => requested == 0 && !alreadyActive ? 1 : requested;
    // The original card adds delayed HP loss in groups of four. A spare +1 is a persistent
    // marker for an upgraded-only application; later normal plays still add exactly four.
    internal static int PendingHpLoss(int amount) => amount > 0 && amount % 4 == 1 ? amount - 1 : Math.Max(amount, 0);
    private static async Task<T?> ApplyAsync<T>(PlayerChoiceContext context, Creature target, CardModel? card, decimal amount, bool silent) where T : PowerModel
    {
        var existing = target.Powers.OfType<T>().FirstOrDefault();
        var requested = ApplicationAmount(amount, existing != null);
        if (requested == 0) return existing;
        return await PowerCmd.Apply<T>(context, target, requested, card?.Owner?.Creature, card, silent);
    }
    private static bool TurnStartPrefix(PowerModel __instance, PlayerChoiceContext ctx, Player player, ref Task __result)
    {
        __result = ExpireAsync(__instance, ctx, player); return false;
    }
    private static async Task ExpireAsync(PowerModel power, PlayerChoiceContext context, Player player)
    {
        if (player.Creature != power.Owner) return;
        var loss = PendingHpLoss(power.Amount);
        if (loss > 0) await _loseHp(context, power.Owner, loss, DamageProps.nonCardHpLoss, null, null, null);
        await PowerCmd.Remove(power);
    }
}
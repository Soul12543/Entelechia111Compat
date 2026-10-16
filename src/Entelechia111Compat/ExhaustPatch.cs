using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;

namespace Entelechia111Compat;

internal static class ExhaustPatch
{
    internal const string TargetTypeName = "Entelechia.EntelechiaCode.Cards.EntelechiaCard";
    internal const string TargetMethodName = "TryExhaustAnotherCard";

    internal static MethodInfo Install(Harmony harmony, Type targetType)
    {
        if (!typeof(CardModel).IsAssignableFrom(targetType))
            throw new InvalidOperationException("The Entelechia helper no longer belongs to a CardModel.");

        var target = targetType.GetMethod(TargetMethodName,
            BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly,
            binder: null, types: [typeof(PlayerChoiceContext)], modifiers: null)
            ?? throw new MissingMethodException(TargetTypeName, TargetMethodName);

        if (target.ReturnType != typeof(Task<bool>))
            throw new InvalidOperationException("Unsupported Entelechia exhaust helper return type.");

        // Patch the async factory, never MoveNext: its old Exhaust MemberRef cannot resolve on 0.111.
        harmony.Patch(target, prefix: new HarmonyMethod(
            typeof(ExhaustPatch).GetMethod(nameof(Prefix), BindingFlags.NonPublic | BindingFlags.Static)!));
        return target;
    }

    private static bool Prefix(CardModel __instance, PlayerChoiceContext context, ref Task<bool> __result)
    {
        __result = RunAsync(__instance, context);
        return false;
    }

    internal static Task<bool> RunAsync(CardModel source, PlayerChoiceContext context) =>
        ExhaustWorkflow.RunAsync(
            source,
            source.Owner.PlayerCombatState?.Hand.Cards,
            context,
            (choiceContext, filter) => CardSelectCmd.FromHand(
                choiceContext, source.Owner,
                new CardSelectorPrefs(CardSelectorPrefs.ExhaustSelectionPrompt, 0, 1),
                filter, source),
            ExhaustAsync);

    private static async Task ExhaustAsync(PlayerChoiceContext context, CardModel selected)
    {
        // Use the engine command so exhaust history, hooks and multiplayer ordering stay intact.
        await CardCmd.Exhaust(context, selected, causedByEthereal: false, skipVisuals: false);
    }
}

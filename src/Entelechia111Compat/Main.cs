using HarmonyLib;
using MegaCrit.Sts2.Core.Debug;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace Entelechia111Compat;

[ModInitializer(nameof(Initialize))]
public static class Main
{
    public const string ModId = "Entelechia111Compat";
    private static readonly Logger Log = new(ModId, LogType.Generic);
    private static bool _initialized;

    public static void Initialize()
    {
        if (_initialized)
            return;

        var gameVersion = ReleaseInfoManager.Instance.ReleaseInfo?.Version?.TrimStart('v', 'V');
        if (gameVersion != "0.111.0")
            throw new InvalidOperationException("This patch targets Slay the Spire 2 v0.111.0 only. Check compatibility before using it with another game version.");

        var originalMod = ModManager.Mods.SingleOrDefault(mod => mod.manifest?.id == "Entelechia");
        if (originalMod?.manifest?.version?.TrimStart('v', 'V') != "1.0.3")
            throw new InvalidOperationException("This patch targets Entelechia v1.0.3 only. Disable it and check upstream compatibility when updating the original mod.");

        var targetType = AccessTools.TypeByName(ExhaustPatch.TargetTypeName)
            ?? throw new InvalidOperationException("Entelechia must be enabled and loaded before Entelechia111Compat.");
        var powerType = AccessTools.TypeByName(HeartCandleSavePatch.TargetTypeName)
            ?? throw new InvalidOperationException("Entelechia BloodDemonFormPower was not found.");
        HeartCandleSavePatch.ValidateTarget(powerType);

        var harmony = new Harmony("Soul12543.Entelechia111Compat");
        var target = ExhaustPatch.Install(harmony, targetType);
        try
        {
            BloodHarvestOwnershipPatch.Install(harmony, targetType.Assembly);
            HeartCandleOwnershipPatch.Install(harmony, targetType.Assembly);
            ImmortalBloodlinePatch.Install(harmony, targetType.Assembly);
            RevisionDescriptions.Install(harmony);
            RevisedCards.Install(harmony);
            RevisedPowers.Install(harmony);
            BloodSpeedReplay.Install(harmony);
            DamageAttribution.Install(harmony, targetType.Assembly);
            CompatStatePatch.Install(harmony, targetType.Assembly);
            HeartCandleSavePatch.Install(harmony, powerType);
            BaseLib.Patches.Content.CustomContentDictionary.AddModel(typeof(RepleteReadyPower));
            BaseLib.Patches.Content.CustomContentDictionary.AddModel(typeof(RepleteWardPower));
        }
        catch
        {
            harmony.UnpatchAll(harmony.Id);
            throw;
        }

        OptionalIntegrations.Watch(harmony);
        _initialized = true;
        Log.Info("Ember Bloodline: cross-combat relic counters and in-combat reward state now use native save/network payloads.");
        Log.Info("Gameplay revision: full candle burst, native Blood Speed replay, life absorption and once-per-combat Replete.");
        Log.Info("DamageMeter/Saya: optional attribution and late evolution-message compatibility registered.");
        Log.Info("HeartCandle: only its applier can trigger their independently stacked pool; passive application and rekindling preserve ownership.");
        Log.Info("ImmortalBloodline: upgraded cards apply a nonzero protection marker with no next-turn HP loss.");
        Log.Info("BloodHarvest: only its applier can trigger or consume their own independently stacked harvest.");
        Log.Info($"v1.2.0: replaced {target.DeclaringType?.FullName}.{target.Name}; engine exhaust hooks and PlayerChoiceContext are preserved.");
        Log.Info("HeartCandlePercent: exact decimal save and network serialization compatibility installed for BloodDemonFormPower.");
    }
}

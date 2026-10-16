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
            HeartCandleSavePatch.Install(harmony, powerType);
        }
        catch
        {
            harmony.Unpatch(target, HarmonyPatchType.Prefix, harmony.Id);
            throw;
        }

        _initialized = true;
        Log.Info($"v1.0.0: replaced {target.DeclaringType?.FullName}.{target.Name}; engine exhaust hooks and PlayerChoiceContext are preserved.");
        Log.Info("HeartCandlePercent: exact decimal save and network serialization compatibility installed for BloodDemonFormPower.");
    }
}

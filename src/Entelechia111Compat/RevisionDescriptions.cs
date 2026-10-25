using System.Text.Json;
using HarmonyLib;
using MegaCrit.Sts2.Core.Localization;

namespace Entelechia111Compat;
internal static class RevisionDescriptions
{
    private static readonly Dictionary<string, Dictionary<string,string>> Text = Load();
    private static Dictionary<string, Dictionary<string,string>> Load()
    {
        using var stream = typeof(RevisionDescriptions).Assembly.GetManifestResourceStream("Entelechia111Compat.RevisionDescriptions.json")!;
        return JsonSerializer.Deserialize<Dictionary<string,Dictionary<string,string>>>(stream)!;
    }
    internal static void Install(Harmony harmony) => harmony.Patch(AccessTools.Method(typeof(LocString), nameof(LocString.GetRawText)), postfix:new(typeof(RevisionDescriptions), nameof(RawPostfix)));
    internal static string? Find(string table, string key) => Text.TryGetValue(table, out var rows) ? rows.GetValueOrDefault(key) : null;
    private static void RawPostfix(LocString __instance, ref string __result)
    {
        if (__result.Any(c => c is >= '\u4e00' and <= '\u9fff') && Find(__instance.LocTable, __instance.LocEntryKey) is {} text) __result = text;
    }
}

using System.Reflection;
using Entelechia.EntelechiaCode.Powers;
using System.Text.Json;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace Entelechia111Compat;

internal static class CompatStatePatch
{
    internal const string Key = nameof(RepleteReadyPower.EntelechiaCompatState);
    internal const string RelicType = "Entelechia.EntelechiaCode.Relics.BloodDemonReplete";
    internal const string EmberType = "Entelechia.EntelechiaCode.Powers.EmberBloodlinePower";
    internal sealed record Payload
    {
        public int Version { get; init; } = 1;
        public string Kind { get; init; } = "";
        public bool Applied { get; init; }
        public int Stacks { get; init; }
        public decimal Harvest { get; init; }
        public bool RewardGranted { get; init; }
        public SortedDictionary<ulong, int>? BleedSources { get; init; }
        public int UnknownBleed { get; init; }
        public int Counter { get; init; }
        public int Threshold { get; init; }
        public bool Triggered { get; init; }
    }
    internal static void Install(Harmony harmony, Assembly original)
    {
        var relic = original.GetType(RelicType, true)!; var ember = original.GetType(EmberType, true)!;
        foreach (var name in new[] { "_emberAppliedThisCombat", "_emberBloodlineStacks", "_emberHarvestAmount" })
            if (AccessTools.Field(relic, name) == null) throw new MissingFieldException(RelicType, name);
        if (AccessTools.Field(ember, "_baseRewardGranted") == null || AccessTools.Property(ember, "HarvestAllEnemiesAmount") == null)
            throw new InvalidOperationException("Unsupported Ember Bloodline state.");
        harmony.Patch(AccessTools.Method(typeof(SavedProperties), nameof(SavedProperties.FromInternal)), prefix: new(typeof(CompatStatePatch), nameof(FromPrefix)));
        harmony.Patch(AccessTools.Method(typeof(SavedProperties), nameof(SavedProperties.FillInternal)), prefix: new(typeof(CompatStatePatch), nameof(FillPrefix)));
    }
    private static bool Target(object model) => model is BloodFeastPower or PainConversionPower || model.GetType().FullName is RelicType or EmberType or BloodHarvestOwnershipPatch.BloodlossTypeName;
    private static bool FromPrefix(object model, ref SavedProperties? __result)
    { if (!Target(model)) return true; __result = Capture(model); return false; }
    private static bool FillPrefix(SavedProperties __instance, object model)
    { if (!Target(model)) return true; Restore(__instance, model); return false; }
    internal static SavedProperties Pack(Payload payload, int? lastFloor = null) => new()
    {
        ints = lastFloor.HasValue ? [new("LastRevivedFloor", lastFloor.Value)] : null,
        strings = [new(Key, JsonSerializer.Serialize(payload))]
    };
    internal static Payload? Unpack(SavedProperties saved, string kind)
    {
        var entries = saved.strings?.Where(entry => entry.name == Key).ToArray() ?? [];
        if (entries.Length > 1) throw new InvalidDataException("Duplicate compatibility payload.");
        if (entries.Length == 0) return null;
        Payload payload;
        try { payload = JsonSerializer.Deserialize<Payload>(entries[0].value) ?? throw new InvalidDataException("Empty compatibility payload."); }
        catch (JsonException ex) { throw new InvalidDataException("Malformed compatibility payload.", ex); }
        if (payload.Version != 1 || payload.Kind != kind || payload.Stacks < 0 || payload.Harvest < 0 || payload.UnknownBleed < 0 || payload.Counter < 0 || payload.Threshold < 0
            || payload.BleedSources?.Any(entry => entry.Value < 0) == true
            || (payload.BleedSources?.Values.Sum(value => (long)value) ?? 0) + payload.UnknownBleed > 999999999L)
            throw new InvalidDataException("Unsupported or invalid compatibility state.");
        return payload;
    }
    internal static SavedProperties Capture(object model)
    {
        if (model.GetType().FullName == RelicType)
            return Pack(CaptureRelic(model), (int)AccessTools.Property(model.GetType(), "LastRevivedFloor").GetValue(model)!);
        if (model.GetType().FullName == EmberType)
            return Pack(new Payload { Kind="ember", RewardGranted=Read<bool>(model,"_baseRewardGranted"),
                Harvest=(decimal)AccessTools.Property(model.GetType(),"HarvestAllEnemiesAmount").GetValue(model)! });
        if (model is BloodFeastPower feast) return Pack(new Payload { Kind="feast", Counter=feast.HitCount, Threshold=feast.Threshold, Triggered=feast.TriggeredThisTurn });
        if (model is PainConversionPower pain) return Pack(new Payload { Kind="pain", Counter=pain.TriggerCountThisTurn });
        return Pack(DamageAttribution.CaptureBleed((PowerModel)model));
    }
    internal static Payload CaptureRelic(object model) => new() { Kind="relic", Applied=Read<bool>(model,"_emberAppliedThisCombat"),
        Stacks=Read<int>(model,"_emberBloodlineStacks"), Harvest=Read<decimal>(model,"_emberHarvestAmount") };
    internal static void RestoreRelic(object model, Payload? payload)
    {
        Write(model,"_emberAppliedThisCombat",payload?.Applied ?? false);
        Write(model,"_emberBloodlineStacks",payload?.Stacks ?? 0);
        Write(model,"_emberHarvestAmount",payload?.Harvest ?? 0m);
    }
    internal static void Restore(SavedProperties saved, object model)
    {
        if (model.GetType().FullName == RelicType)
        {
            var payload = Unpack(saved,"relic");
            var floors = saved.ints?.Where(entry => entry.name == "LastRevivedFloor").ToArray() ?? [];
            if (floors.Length > 1) throw new InvalidDataException("Duplicate LastRevivedFloor.");
            var floor = floors.Length == 0 ? -1 : floors[0].value;
            RestoreRelic(model,payload);
            AccessTools.Property(model.GetType(),"LastRevivedFloor").SetValue(model,floor);
        }
        else if (model.GetType().FullName == EmberType)
        {
            var payload = Unpack(saved,"ember");
            Write(model,"_baseRewardGranted",payload?.RewardGranted ?? false);
            AccessTools.Property(model.GetType(),"HarvestAllEnemiesAmount").SetValue(model,payload?.Harvest ?? 0m);
        }
        else if (model is BloodFeastPower feast)
        { var payload=Unpack(saved,"feast"); feast.HitCount=payload?.Counter ?? 0; feast.Threshold=payload?.Threshold is > 0 ? payload.Threshold : 3; feast.TriggeredThisTurn=payload?.Triggered ?? false; }
        else if (model is PainConversionPower pain)
        { var payload=Unpack(saved,"pain"); pain.TriggerCountThisTurn=payload?.Counter ?? 0; pain.RefundedEnergyThisTurn=false; }
        else DamageAttribution.RestoreBleed((PowerModel)model,Unpack(saved,"bleed"));
    }
    private static T Read<T>(object model,string name) => (T)AccessTools.Field(model.GetType(),name).GetValue(model)!;
    private static void Write(object model,string name,object value) => AccessTools.Field(model.GetType(),name).SetValue(model,value);
}
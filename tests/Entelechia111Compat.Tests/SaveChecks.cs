using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Entelechia111Compat;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;

internal static class SaveChecks
{
    private static readonly Harmony Harmony = new("Entelechia111Compat.Tests.Save");
    private static readonly Type PowerType = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "Entelechia.dll"))
        .GetType(HeartCandleSavePatch.TargetTypeName, throwOnError: true)!;
    private static readonly PropertyInfo Property = PowerType.GetProperty(HeartCandleSavePatch.PropertyName)!;
    private static readonly ModelId Id = new("POWER", "ENTELECHIA-BLOOD_DEMON_FORM_POWER");
    private static readonly decimal[] Values = [0m, 50m, 62.5m, -0.125m, 0.1234567890123456789012345678m, decimal.MinValue, decimal.MaxValue, 1.0000000000000000000000000001m];

    internal static Task JsonRoundTrip()
    {
        HeartCandleSavePatch.Install(Harmony, PowerType);
        var from = typeof(SavedProperties).GetMethod(nameof(SavedProperties.FromInternal))!;
        var fill = typeof(SavedProperties).GetMethod(nameof(SavedProperties.FillInternal))!;
        Assert.That(HarmonyLib.Harmony.GetPatchInfo(from)?.Prefixes.Any(p => p.owner == Harmony.Id) == true
            && HarmonyLib.Harmony.GetPatchInfo(fill)?.Prefixes.Any(p => p.owner == Harmony.Id) == true,
            "Both actual saved-property prefixes must be installed.");
        var before = CultureInfo.CurrentCulture;
        var beforeUi = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
            var contextType = typeof(SavedProperties).Assembly.GetType("MegaCrit.Sts2.Core.Saves.MegaCritSerializerContext", throwOnError: true)!;
            var jsonContext = (JsonSerializerContext)contextType.GetProperty("Default", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
            var jsonType = (JsonTypeInfo<SavedProperties>)jsonContext.GetTypeInfo(typeof(SavedProperties))!;
            foreach (var value in Values)
            {
                var source = NewPower(value);
                var saved = SavedProperties.FromInternal(source, Id)!;
                Assert.That(saved.strings?.Count == 1 && saved.strings[0].name == HeartCandleSavePatch.PropertyName
                    && saved.strings[0].value == value.ToString(CultureInfo.InvariantCulture), "Capture must use the original key with invariant decimal text.");
                var json = JsonSerializer.Serialize(saved, jsonType);
                var restored = JsonSerializer.Deserialize(json, jsonType)!;
                var target = NewPower(999m);
                restored.FillInternal(target);
                Assert.That(Read(target) == value, "The actual game JSON roundtrip lost decimal precision.");
            }
        }
        finally { CultureInfo.CurrentCulture = before; CultureInfo.CurrentUICulture = beforeUi; }
        return Task.CompletedTask;
    }

    internal static Task RestoreBoundaries()
    {
        HeartCandleSavePatch.Install(Harmony, PowerType);
        var power = NewPower(37.5m);
        new SavedProperties().FillInternal(power);
        Assert.That(Read(power) == 37.5m, "A missing property must retain the current value.");
        new SavedProperties { ints = [new(HeartCandleSavePatch.PropertyName, 50)] }.FillInternal(power);
        Assert.That(Read(power) == 50m, "An integer representation must restore a whole percentage.");
        foreach (var value in new[] { "not-a-decimal", "NaN", "62,5", "79228162514264337593543950336" })
            Throws<InvalidDataException>(() => new SavedProperties { strings = [new(HeartCandleSavePatch.PropertyName, value)] }.FillInternal(power));
        Throws<InvalidDataException>(() => new SavedProperties
        { strings = [new(HeartCandleSavePatch.PropertyName, "1"), new(HeartCandleSavePatch.PropertyName, "2")] }.FillInternal(power));
        Throws<InvalidDataException>(() => new SavedProperties
        { strings = [new(HeartCandleSavePatch.PropertyName, "1")], ints = [new(HeartCandleSavePatch.PropertyName, 2)] }.FillInternal(power));
        Throws<InvalidDataException>(() => new SavedProperties
        { ints = [new(HeartCandleSavePatch.PropertyName, 1), new(HeartCandleSavePatch.PropertyName, 2)] }.FillInternal(power));
        Assert.That(Read(power) == 50m, "Rejected values must not partially change the power.");
        // The vanilla implementation reads a model-property cache. Seed only this probe in
        // the isolated test process, avoiding ModelDb.Init and all game/asset initialization.
        using var cache = new CacheScope();
        var otherSource = new VanillaProbe { Counter = 73 };
        var otherSaved = SavedProperties.FromInternal(otherSource, new ModelId("TEST", "PROBE"))!;
        Assert.That(otherSaved.ints?.Count == 1 && otherSaved.ints[0].name == nameof(VanillaProbe.Counter)
            && otherSaved.ints[0].value == 73 && (otherSaved.strings?.Count ?? 0) == 0, "The non-target capture must retain vanilla int serialization.");
        var otherTarget = new VanillaProbe();
        otherSaved.FillInternal(otherTarget);
        Assert.That(otherTarget.Counter == 73, "The non-target fill must retain vanilla behavior.");
        Throws<InvalidOperationException>(() => HeartCandleSavePatch.Capture(otherSource));
        return Task.CompletedTask;
    }

    internal static Task WireRoundTrip()
    {
        HeartCandleSavePatch.Install(Harmony, PowerType);
        using var cache = new CacheScope();
        foreach (var value in Values)
        {
            var saved = SavedProperties.FromInternal(NewPower(value), Id)!;
            var writer = new PacketWriter();
            saved.Serialize(writer);
            var reader = new PacketReader();
            reader.Reset(writer.Buffer);
            var restored = new SavedProperties();
            restored.Deserialize(reader);
            Assert.That(reader.BitPosition == writer.BitPosition, "SavedProperties must consume exactly the written packet bits.");
            Assert.That(restored.strings?.Single().name == HeartCandleSavePatch.PropertyName, "Network data must retain the already registered original property key.");
            var target = NewPower(999m);
            restored.FillInternal(target);
            Assert.That(Read(target) == value, "The actual multiplayer packet roundtrip lost decimal precision.");
        }
        return Task.CompletedTask;
    }

    private static object NewPower(decimal value)
    { var power = RuntimeHelpers.GetUninitializedObject(PowerType); Property.SetValue(power, value); return power; }
    private static decimal Read(object power) => (decimal)Property.GetValue(power)!;
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }

    public sealed class VanillaProbe
    { [SavedProperty] public int Counter { get; set; } }

    private sealed class CacheScope : IDisposable
    {
        private readonly Type Type = typeof(ModelIdSerializationCache);
        private readonly Dictionary<Type, List<PropertyInfo>> Properties;
        private readonly Dictionary<string, int> Names;
        private readonly List<string> Ids;
        private readonly Dictionary<Type, List<PropertyInfo>> OldProperties;
        private readonly Dictionary<string, int> OldNames;
        private readonly string[] OldIds;
        private readonly bool Initialized;
        private readonly int Bits;
        internal CacheScope()
        {
            Properties = (Dictionary<Type, List<PropertyInfo>>)Get("_savedPropertyCache")!;
            Names = (Dictionary<string, int>)Get("_propertyNameToNetIdMap")!;
            Ids = (List<string>)Get("_netIdToPropertyNameMap")!;
            OldProperties = new(Properties); OldNames = new(Names); OldIds = Ids.ToArray();
            Initialized = (bool)Get("_initialized")!; Bits = (int)Get("<PropertyIdBitSize>k__BackingField")!;
            Properties.Clear(); Properties.Add(typeof(VanillaProbe), [typeof(VanillaProbe).GetProperty(nameof(VanillaProbe.Counter))!]);
            Names.Clear(); Names.Add(HeartCandleSavePatch.PropertyName, 0);
            Ids.Clear(); Ids.Add(HeartCandleSavePatch.PropertyName);
            Set("<PropertyIdBitSize>k__BackingField", 1); Set("_initialized", true);
        }
        public void Dispose()
        {
            Properties.Clear(); foreach (var pair in OldProperties) Properties.Add(pair.Key, pair.Value);
            Names.Clear(); foreach (var pair in OldNames) Names.Add(pair.Key, pair.Value);
            Ids.Clear(); Ids.AddRange(OldIds);
            Set("<PropertyIdBitSize>k__BackingField", Bits); Set("_initialized", Initialized);
        }
        private object? Get(string name) => Field(name).GetValue(null);
        private void Set(string name, object value) => Field(name).SetValue(null, value);
        private FieldInfo Field(string name) => Type.GetField(name, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingFieldException(Type.FullName, name);
    }
}

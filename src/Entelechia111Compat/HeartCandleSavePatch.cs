using System.Globalization;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace Entelechia111Compat;

internal static class HeartCandleSavePatch
{
    internal const string TargetTypeName = "Entelechia.EntelechiaCode.Powers.BloodDemonFormPower";
    internal const string PropertyName = "HeartCandlePercent";

    private static readonly object InstallLock = new();
    private static Type? _powerType;
    private static PropertyInfo? _property;
    private static string? _harmonyId;

    internal static void Install(Harmony harmony, Type powerType)
    {
        ArgumentNullException.ThrowIfNull(harmony);
        ArgumentNullException.ThrowIfNull(powerType);
        var property = ValidateTarget(powerType);
        var from = RequireMethod(nameof(SavedProperties.FromInternal), true,
            typeof(SavedProperties), [typeof(object), typeof(ModelId)]);
        var fill = RequireMethod(nameof(SavedProperties.FillInternal), false,
            typeof(void), [typeof(object)]);
        var fromPrefix = typeof(HeartCandleSavePatch).GetMethod(nameof(FromPrefix),
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var fillPrefix = typeof(HeartCandleSavePatch).GetMethod(nameof(FillPrefix),
            BindingFlags.NonPublic | BindingFlags.Static)!;

        lock (InstallLock)
        {
            if (_powerType != null)
            {
                if (_powerType == powerType && _harmonyId == harmony.Id)
                    return;
                throw new InvalidOperationException("Heart-candle saving is already configured for a different target or Harmony instance.");
            }

            _powerType = powerType;
            _property = property;
            _harmonyId = harmony.Id;
            try
            {
                harmony.Patch(from, prefix: new HarmonyMethod(fromPrefix));
                harmony.Patch(fill, prefix: new HarmonyMethod(fillPrefix));
            }
            catch (Exception installError)
            {
                var errors = new List<Exception> { installError };
                foreach (var pair in new[] { (from, fromPrefix), (fill, fillPrefix) })
                {
                    try
                    {
                        harmony.Unpatch(pair.Item1, pair.Item2);
                    }
                    catch (Exception rollbackError)
                    {
                        errors.Add(rollbackError);
                    }
                }
                _powerType = null;
                _property = null;
                _harmonyId = null;
                if (errors.Count > 1)
                    throw new AggregateException("Heart-candle save patch installation and rollback failed.", errors);
                throw;
            }
        }
    }

    // Include private properties on base classes: a new saved field must not be silently lost.
    internal static PropertyInfo ValidateTarget(Type powerType)
    {
        ArgumentNullException.ThrowIfNull(powerType);
        var savedProperties = new List<PropertyInfo>();
        for (var current = powerType; current != null; current = current.BaseType)
        {
            savedProperties.AddRange(current.GetProperties(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(property => property.GetCustomAttribute<SavedPropertyAttribute>(inherit: true) != null));
        }

        if (savedProperties.Count != 1 || savedProperties[0].Name != PropertyName
            || savedProperties[0].PropertyType != typeof(decimal))
        {
            var actual = string.Join(", ", savedProperties.Select(property =>
                $"{property.DeclaringType?.FullName}.{property.Name}: {property.PropertyType.FullName}"));
            throw new InvalidOperationException(
                $"Refusing to override saved properties for {powerType.FullName}: expected exactly one [SavedProperty], decimal {PropertyName}; found [{actual}].");
        }

        var target = savedProperties[0];
        if (target.GetIndexParameters().Length != 0 || target.GetGetMethod(true) == null
            || target.GetSetMethod(true) == null)
            throw new InvalidOperationException($"{powerType.FullName}.{PropertyName} must remain a readable and writable instance property.");
        return target;
    }

    private static MethodInfo RequireMethod(string name, bool isStatic, Type returnType, Type[] parameters)
    {
        var flags = BindingFlags.Public | (isStatic ? BindingFlags.Static : BindingFlags.Instance);
        var method = typeof(SavedProperties).GetMethod(name, flags, null, parameters, null)
            ?? throw new MissingMethodException(typeof(SavedProperties).FullName, name);
        if (method.ReturnType != returnType || method.IsGenericMethod)
            throw new InvalidOperationException($"Unsupported SavedProperties.{name} signature; heart-candle save compatibility was not installed.");
        return method;
    }

    private static bool IsTarget(object model) => _powerType != null && model.GetType() == _powerType;

    private static bool FromPrefix(object model, ref SavedProperties? __result)
    {
        if (!IsTarget(model))
            return true;
        __result = Capture(model);
        return false;
    }

    private static bool FillPrefix(SavedProperties __instance, object model)
    {
        if (!IsTarget(model))
            return true;
        Restore(__instance, model);
        return false;
    }

    internal static SavedProperties Capture(object model)
    {
        var property = RequireTarget(model);
        var value = (decimal)property.GetValue(model)!;
        // The original property name is already registered by the game's model-ID cache.
        // Strings use the existing JSON and network formats without changing IDs or decimal precision.
        return new SavedProperties
        {
            strings = [new SavedProperties.SavedProperty<string>(PropertyName,
                value.ToString(CultureInfo.InvariantCulture))]
        };
    }

    internal static void Restore(SavedProperties savedProperties, object model)
    {
        ArgumentNullException.ThrowIfNull(savedProperties);
        var property = RequireTarget(model);
        var stringValues = savedProperties.strings?
            .Where(value => value.name == PropertyName).ToArray() ?? [];
        var intValues = savedProperties.ints?
            .Where(value => value.name == PropertyName).ToArray() ?? [];
        if (stringValues.Length + intValues.Length > 1)
            throw new InvalidDataException($"Duplicate saved values for {PropertyName}.");

        if (stringValues.Length == 1)
        {
            if (!decimal.TryParse(stringValues[0].value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture, out var value))
                throw new InvalidDataException($"Saved {PropertyName} is not a valid invariant decimal.");
            property.SetValue(model, value);
        }
        else if (intValues.Length == 1)
        {
            // Accept an integer representation if a compatible writer stored a whole percentage.
            property.SetValue(model, (decimal)intValues[0].value);
        }
        // Match vanilla FillInternal: an absent property leaves the model's current value intact.
    }

    private static PropertyInfo RequireTarget(object model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!IsTarget(model) || _property == null)
            throw new InvalidOperationException("Heart-candle saving is not installed for this exact model type.");
        return _property;
    }
}

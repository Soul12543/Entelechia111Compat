using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using Entelechia111Compat;
using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;

var checks = new (string Name, Func<Task> Run)[]
{
    ("no candidate, empty hand and absent hand avoid commands", WorkflowChecks.NoCandidate),
    ("optional selection can be cancelled", WorkflowChecks.Cancel),
    ("successful selection exhausts exactly the chosen object", WorkflowChecks.Success),
    ("same-ID card copy remains selectable", WorkflowChecks.SameIdCopy),
    ("selection and exhaust retain the supplied context", WorkflowChecks.ContextIdentity),
    ("workflow awaits selection and exhaust", WorkflowChecks.AwaitsCommands),
    ("selection and exhaust exceptions propagate unchanged", WorkflowChecks.ExceptionsPropagate),
    ("original DLL has the old Exhaust MemberRef; current engine has the new return type", IntegrationChecks.ApiBoundary),
    ("Harmony installs on the actual original async helper and intercepts its execution", IntegrationChecks.ActualHelper),
    ("actual saved-property patches preserve decimal precision through JSON and cultures", SaveChecks.JsonRoundTrip),
    ("missing, malformed, duplicate and non-target saved properties retain defined behavior", SaveChecks.RestoreBoundaries),
    ("actual multiplayer packet serialization preserves the original key and exact decimal", SaveChecks.WireRoundTrip)
};
int failures = 0;
foreach (var check in checks)
{
    try { await check.Run(); Console.WriteLine($"PASS {check.Name}"); }
    catch (Exception exception) { failures++; Console.Error.WriteLine($"FAIL {check.Name}\n{exception}"); }
}
Console.WriteLine($"{checks.Length - failures}/{checks.Length} checks passed. These are headless checks, not a live multiplayer session.");
return failures == 0 ? 0 : 1;

internal static class Assert
{
    internal static void That(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    internal static async Task SameException(Task task, Exception expected)
    {
        try { await task; }
        catch (Exception actual) when (ReferenceEquals(actual, expected)) { return; }
        throw new InvalidOperationException("Expected the original exception object to propagate.");
    }
}

internal static class WorkflowChecks
{
    private sealed record Card(string Id);
    private static readonly object Context = new();
    private static Task<IEnumerable<Card>> Selection(params Card[] cards) => Task.FromResult<IEnumerable<Card>>(cards);
    internal static async Task NoCandidate()
    {
        var source = new Card("source");
        foreach (IReadOnlyList<Card>? hand in new IReadOnlyList<Card>?[] { null, [], [source] })
        {
            var result = await ExhaustWorkflow.RunAsync(source, hand, Context,
                (_, _) => throw new InvalidOperationException("Selection must not be called."),
                (_, _) => throw new InvalidOperationException("Exhaust must not be called."));
            Assert.That(!result, "No candidate must return false.");
        }
    }
    internal static async Task Cancel()
    {
        var source = new Card("source");
        var other = new Card("other");
        var result = await ExhaustWorkflow.RunAsync(source, [source, other], Context,
            (_, filter) => { Assert.That(!filter(source) && filter(other), "Only the source object must be excluded."); return Selection(); },
            (_, _) => throw new InvalidOperationException("Cancellation must not exhaust."));
        Assert.That(!result, "Cancellation must return false.");
    }
    internal static async Task Success()
    {
        var source = new Card("source");
        var chosen = new Card("chosen");
        var unused = new Card("unused");
        int count = 0;
        var result = await ExhaustWorkflow.RunAsync(source, [source, chosen, unused], Context,
            (_, _) => Selection(chosen),
            (_, card) => { Assert.That(ReferenceEquals(card, chosen), "Must exhaust the chosen object."); count++; return Task.CompletedTask; });
        Assert.That(result && count == 1, "Success must await exactly one exhaust command and return true.");
    }
    internal static async Task SameIdCopy()
    {
        var source = new Card("same-id");
        var copy = new Card("same-id");
        Assert.That(source == copy && !ReferenceEquals(source, copy), "Test setup must have equal IDs but different objects.");
        var result = await ExhaustWorkflow.RunAsync(source, [source, copy], Context,
            (_, filter) => { Assert.That(filter(copy) && !filter(source), "Object identity must decide eligibility."); return Selection(copy); },
            (_, card) => { Assert.That(ReferenceEquals(card, copy), "Must exhaust the other copy."); return Task.CompletedTask; });
        Assert.That(result, "An equal-ID copy must remain a candidate.");
    }
    internal static async Task ContextIdentity()
    {
        var source = new Card("source");
        var other = new Card("other");
        var context = new object();
        await ExhaustWorkflow.RunAsync(source, [other], context,
            (seen, _) => { Assert.That(ReferenceEquals(seen, context), "Selection context was replaced."); return Selection(other); },
            (seen, _) => { Assert.That(ReferenceEquals(seen, context), "Exhaust context was replaced."); return Task.CompletedTask; });
    }
    internal static async Task AwaitsCommands()
    {
        var source = new Card("source");
        var other = new Card("other");
        var selection = new TaskCompletionSource<IEnumerable<Card>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exhaust = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enteredExhaust = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = ExhaustWorkflow.RunAsync(source, [other], Context,
            (_, _) => selection.Task,
            (_, _) => { enteredExhaust.SetResult(); return exhaust.Task; });
        Assert.That(!result.IsCompleted && !enteredExhaust.Task.IsCompleted, "Must wait for selection before exhausting.");
        selection.SetResult([other]);
        await enteredExhaust.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(!result.IsCompleted, "Must wait for the exhaust command to finish.");
        exhaust.SetResult();
        Assert.That(await result, "Completed exhaust must resolve true.");
    }
    internal static async Task ExceptionsPropagate()
    {
        var source = new Card("source");
        var other = new Card("other");
        var selectError = new ApplicationException("selection failure");
        await Assert.SameException(ExhaustWorkflow.RunAsync(source, [other], Context,
            (_, _) => Task.FromException<IEnumerable<Card>>(selectError), (_, _) => Task.CompletedTask), selectError);
        var exhaustError = new ApplicationException("exhaust failure");
        await Assert.SameException(ExhaustWorkflow.RunAsync(source, [other], Context,
            (_, _) => Selection(other), (_, _) => Task.FromException(exhaustError)), exhaustError);
    }
}

internal static class IntegrationChecks
{
    private const string HarmonyId = "Entelechia111Compat.Tests.Headless";
    private static PlayerChoiceContext? ExpectedContext;
    private static CardModel? ExpectedSource;
    private static CardModel? Chosen;
    private static TaskCompletionSource<IEnumerable<CardModel>>? SelectCompletion;
    private static TaskCompletionSource<CardPileAddResult?>? ExhaustCompletion;
    private static TaskCompletionSource? EnteredExhaust;
    private static int SelectionCalls;
    private static int ExhaustCalls;

    internal static Task ApiBoundary()
    {
        using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Entelechia.dll"));
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();
        var provider = new TypeNames();
        var oldCalls = reader.MemberReferences.Select(h => reader.GetMemberReference(h))
            .Where(m => reader.GetString(m.Name) == "Exhaust" && m.Parent.Kind == HandleKind.TypeReference
                && provider.GetTypeFromReference(reader, (TypeReferenceHandle)m.Parent, 0) == typeof(CardCmd).FullName)
            .Select(m => m.DecodeMethodSignature(provider, (object?)null)).ToArray();
        Assert.That(oldCalls.Length == 1 && oldCalls[0].ReturnType == "System.Threading.Tasks.Task"
            && oldCalls[0].ParameterTypes.Length == 4, "Expected the original v1.0.3 old-API MemberRef.");
        var current = typeof(CardCmd).GetMethod(nameof(CardCmd.Exhaust), [typeof(PlayerChoiceContext), typeof(CardModel), typeof(bool), typeof(bool)]);
        Assert.That(current?.ReturnType == typeof(Task<CardPileAddResult?>), "The engine is not the audited v0.111 Exhaust API.");
        return Task.CompletedTask;
    }

    internal static async Task ActualHelper()
    {
        var original = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "Entelechia.dll"));
        var cardType = original.GetType("Entelechia.EntelechiaCode.Cards.DiscontinuousPulse", throwOnError: true)!;
        var helperType = original.GetType(ExhaustPatch.TargetTypeName, throwOnError: true)!;
        var source = (CardModel)RuntimeHelpers.GetUninitializedObject(cardType);
        var other = (CardModel)RuntimeHelpers.GetUninitializedObject(cardType);
        SetField(source, "<IsMutable>k__BackingField", true);
        SetField(other, "<IsMutable>k__BackingField", true);
        var player = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));
        SetField(source, "_owner", player);
        SetField(other, "_owner", player);
        var context = (PlayerChoiceContext)RuntimeHelpers.GetUninitializedObject(typeof(BlockingPlayerChoiceContext));
        var harmony = new Harmony(HarmonyId);
        var helper = ExhaustPatch.Install(harmony, helperType);
        try
        {
            Assert.That(Harmony.GetPatchInfo(helper)?.Prefixes.Any(p => p.owner == HarmonyId) == true, "Prefix was not installed on the actual original helper.");
            // A null combat state takes the real replacement's no-candidate path. The original
            // MoveNext contains an unresolvable MemberRef, so entering it would fail even here.
            Assert.That(!await Invoke(helper, source, context), "An absent combat hand must return false through the installed prefix.");
            var state = (PlayerCombatState)RuntimeHelpers.GetUninitializedObject(typeof(PlayerCombatState));
            var hand = (CardPile)RuntimeHelpers.GetUninitializedObject(typeof(CardPile));
            var cards = new List<CardModel> { source, other };
            SetField(hand, "_cards", cards);
            SetField(state, "<Hand>k__BackingField", hand);
            SetField(player, "<PlayerCombatState>k__BackingField", state);
            ExpectedContext = context; ExpectedSource = source; Chosen = other;
            harmony.Patch(typeof(CardSelectCmd).GetMethod(nameof(CardSelectCmd.FromHand))!, prefix: new HarmonyMethod(typeof(IntegrationChecks), nameof(SelectPrefix)));
            harmony.Patch(typeof(CardCmd).GetMethod(nameof(CardCmd.Exhaust))!, prefix: new HarmonyMethod(typeof(IntegrationChecks), nameof(ExhaustPrefix)));
            ResetCommands();
            SelectCompletion!.SetResult([]);
            Assert.That(!await Invoke(helper, source, context), "Actual helper cancellation must return false.");
            Assert.That(SelectionCalls == 1 && ExhaustCalls == 0, "Actual cancellation must not exhaust.");
            ResetCommands();
            var result = Invoke(helper, source, context);
            Assert.That(SelectionCalls == 1 && ExhaustCalls == 0 && !result.IsCompleted, "Actual helper must wait for its original context's selection.");
            SelectCompletion!.SetResult([other]);
            await EnteredExhaust!.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(!result.IsCompleted && ExhaustCalls == 1, "Actual helper must await the current engine exhaust command.");
            ExhaustCompletion!.SetResult(default(CardPileAddResult));
            Assert.That(await result, "Actual helper must resolve true after a chosen card's exhaust completes.");
        }
        finally { harmony.UnpatchAll(HarmonyId); }
    }

    private static Task<bool> Invoke(MethodInfo helper, CardModel card, PlayerChoiceContext context) =>
        (Task<bool>)(helper.Invoke(card, [context]) ?? throw new InvalidOperationException("Helper returned null."));
    private static void ResetCommands()
    {
        SelectionCalls = 0; ExhaustCalls = 0;
        SelectCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ExhaustCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        EnteredExhaust = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private static bool SelectPrefix(PlayerChoiceContext context, Player player, CardSelectorPrefs prefs,
        Func<CardModel, bool> filter, AbstractModel source, ref Task<IEnumerable<CardModel>> __result)
    {
        SelectionCalls++;
        Assert.That(ReferenceEquals(context, ExpectedContext), "Real selector received a replacement context.");
        Assert.That(ReferenceEquals(source, ExpectedSource) && ReferenceEquals(player, ExpectedSource!.Owner), "Real selector lost the source owner.");
        Assert.That(!filter(ExpectedSource!) && filter(Chosen!), "Real selector must exclude source and permit another copy of the same card type.");
        Assert.That(prefs.MinSelect == 0 && prefs.MaxSelect == 1, "Selection must remain optional and limited to one.");
        __result = SelectCompletion!.Task;
        return false;
    }
    private static bool ExhaustPrefix(PlayerChoiceContext choiceContext, CardModel card,
        bool causedByEthereal, bool skipVisuals, ref Task<CardPileAddResult?> __result)
    {
        ExhaustCalls++;
        Assert.That(ReferenceEquals(choiceContext, ExpectedContext), "Real exhaust received a replacement context.");
        Assert.That(ReferenceEquals(card, Chosen) && !causedByEthereal && !skipVisuals, "Real exhaust received different card or flags.");
        __result = ExhaustCompletion!.Task;
        EnteredExhaust!.SetResult();
        return false;
    }
    private static void SetField(object instance, string name, object value)
    {
        for (Type? type = instance.GetType(); type != null; type = type.BaseType)
        {
            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field != null) { field.SetValue(instance, value); return; }
        }
        throw new MissingFieldException(instance.GetType().FullName, name);
    }
}

internal sealed class TypeNames : ISignatureTypeProvider<string, object?>
{
    public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[" + new string(',', shape.Rank - 1) + "]";
    public string GetByReferenceType(string elementType) => elementType + "&";
    public string GetFunctionPointerType(MethodSignature<string> signature) => "function-pointer";
    public string GetGenericInstantiation(string genericType, ImmutableArray<string> arguments) => genericType + "<" + string.Join(",", arguments) + ">";
    public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;
    public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index;
    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
    public string GetPinnedType(string elementType) => elementType;
    public string GetPointerType(string elementType) => elementType + "*";
    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
    public string GetSZArrayType(string elementType) => elementType + "[]";
    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
    { var type = reader.GetTypeDefinition(handle); return Qualify(reader.GetString(type.Namespace), reader.GetString(type.Name)); }
    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
    { var type = reader.GetTypeReference(handle); return Qualify(reader.GetString(type.Namespace), reader.GetString(type.Name)); }
    public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
        reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
    private static string Qualify(string ns, string name) => string.IsNullOrEmpty(ns) ? name : ns + "." + name;
}

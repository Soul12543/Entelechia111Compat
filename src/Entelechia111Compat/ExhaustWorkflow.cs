namespace Entelechia111Compat;

/// <summary>The optional hand choice uses object identity, including when two cards have the same ID.</summary>
internal static class ExhaustWorkflow
{
    internal static async Task<bool> RunAsync<TCard, TContext>(
        TCard source,
        IReadOnlyList<TCard>? hand,
        TContext context,
        Func<TContext, Func<TCard, bool>, Task<IEnumerable<TCard>>> select,
        Func<TContext, TCard, Task> exhaust)
        where TCard : class
    {
        bool IsOther(TCard card) => !ReferenceEquals(card, source);
        if (hand is null || !hand.Any(IsOther))
            return false;

        var chosen = (await select(context, IsOther)).FirstOrDefault();
        if (chosen is null)
            return false;

        await exhaust(context, chosen);
        return true;
    }
}

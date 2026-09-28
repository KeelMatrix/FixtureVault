namespace KeelMatrix.FixtureVault;

// One scan owns one budget. SafeFileWalker uses the ambient budget so separate active-root
// and repository-wide walks cannot reset the filesystem work limit.
internal sealed class FilesystemTraversalBudget
{
    internal const int DefaultMaximumEntries = 100_000;
    internal const long DefaultMaximumPathOperations = 1_000_000;

    internal FilesystemTraversalBudget(
        int maximumEntries = DefaultMaximumEntries,
        long maximumPathOperations = DefaultMaximumPathOperations)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumEntries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumPathOperations);

        MaximumEntries = maximumEntries;
        MaximumPathOperations = maximumPathOperations;
    }

    internal int MaximumEntries { get; }

    internal long MaximumPathOperations { get; }

    internal int EntriesConsumed { get; private set; }

    internal long PathOperationsConsumed { get; private set; }

    internal bool IsExhausted { get; private set; }

    internal bool TryConsumeEntry()
    {
        if (EntriesConsumed >= MaximumEntries)
        {
            IsExhausted = true;
            return false;
        }

        EntriesConsumed++;
        return true;
    }

    internal bool TryConsumePathOperations(long operationCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(operationCount);
        if (PathOperationsConsumed > MaximumPathOperations - operationCount)
        {
            PathOperationsConsumed = MaximumPathOperations;
            IsExhausted = true;
            return false;
        }

        PathOperationsConsumed += operationCount;
        return true;
    }
}

internal static class FilesystemTraversalBudgetContext
{
    private static readonly AsyncLocal<FilesystemTraversalBudget?> CurrentBudget = new();

    internal static FilesystemTraversalBudget? Current => CurrentBudget.Value;

    internal static IDisposable Push(FilesystemTraversalBudget budget)
    {
        FilesystemTraversalBudget? previous = CurrentBudget.Value;
        CurrentBudget.Value = budget;
        return new BudgetScope(previous);
    }

    private sealed class BudgetScope(FilesystemTraversalBudget? previous) : IDisposable
    {
        public void Dispose() => CurrentBudget.Value = previous;
    }
}

using System.Text;

namespace KeelMatrix.FixtureVault;

internal static class PathUtilities
{
    internal static string FindRepositoryRoot(string startingDirectory)
    {
        string current = Path.GetFullPath(startingDirectory);
        if (!Directory.Exists(current))
        {
            current = Path.GetDirectoryName(current) ?? Directory.GetCurrentDirectory();
        }

        var directory = new DirectoryInfo(current);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, FixtureVaultContract.PolicyFileName)))
            {
                return directory.FullName;
            }

            if (File.Exists(Path.Combine(directory.FullName, ".git")) ||
                Directory.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return current;
    }

    internal static bool TryResolveRoot(
        string repositoryRoot,
        string configuredRoot,
        out string fullPath,
        out string relativePath,
        out string error)
    {
        fullPath = string.Empty;
        relativePath = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(configuredRoot) || configuredRoot.Contains('\0'))
        {
            error = "A configured fixture root is empty or invalid.";
            return false;
        }

        try
        {
            fullPath = Path.GetFullPath(Path.IsPathRooted(configuredRoot)
                ? configuredRoot
                : Path.Combine(repositoryRoot, configuredRoot));
            if (!IsWithin(repositoryRoot, fullPath))
            {
                error = "A configured fixture root must remain inside the repository root.";
                return false;
            }

            if (!TryValidateNoReparsePoints(repositoryRoot, fullPath, out error))
            {
                return false;
            }

            relativePath = NormalizeRelative(repositoryRoot, fullPath);
            if (relativePath == ".")
            {
                relativePath = string.Empty;
            }

            if (!Directory.Exists(fullPath))
            {
                error = "A configured fixture root does not exist or is not a directory.";
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            error = "A configured fixture root could not be resolved safely.";
            return false;
        }
    }

    private static bool TryValidateNoReparsePoints(
        string repositoryRoot,
        string fullPath,
        out string error)
    {
        error = string.Empty;
        string relativePath = Path.GetRelativePath(repositoryRoot, fullPath);
        DirectoryInfo current = new(Path.GetFullPath(repositoryRoot));

        if (!TryReadDirectoryAttributes(current, out FileAttributes attributes, out error) ||
            IsLinked(attributes, current))
        {
            error = "A configured fixture root is beneath a linked or reparse path and cannot be scanned safely.";
            return false;
        }

        if (relativePath == ".")
        {
            return true;
        }

        foreach (string component in relativePath.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = new DirectoryInfo(Path.Combine(current.FullName, component));
            if (!TryReadDirectoryAttributes(current, out attributes, out error))
            {
                return false;
            }

            if (IsLinked(attributes, current))
            {
                error = "A configured fixture root is beneath a linked or reparse path and cannot be scanned safely.";
                return false;
            }
        }

        return true;
    }

    private static bool TryReadDirectoryAttributes(
        DirectoryInfo directory,
        out FileAttributes attributes,
        out string error)
    {
        try
        {
            attributes = directory.Attributes;
            error = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            attributes = default;
            error = "A configured fixture root could not be resolved safely.";
            return false;
        }
    }

    private static bool IsLinked(FileAttributes attributes, FileSystemInfo entry)
    {
        return (attributes & FileAttributes.ReparsePoint) != 0 || entry.LinkTarget is not null;
    }

    internal static bool IsWithin(string parent, string candidate)
    {
        string normalizedParent = EnsureTrailingSeparator(Path.GetFullPath(parent));
        string normalizedCandidate = Path.GetFullPath(candidate);
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return normalizedCandidate.Equals(normalizedParent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), comparison)
            || normalizedCandidate.StartsWith(normalizedParent, comparison);
    }

    internal static string NormalizeRelative(string repositoryRoot, string fullPath)
    {
        string relative = Path.GetRelativePath(repositoryRoot, fullPath)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');

        if (relative == ".")
        {
            return relative;
        }

        while (relative.StartsWith("./", StringComparison.Ordinal))
        {
            relative = relative[2..];
        }

        return relative.Normalize(NormalizationForm.FormC);
    }

    internal static string NormalizeComparisonPath(string relativePath)
    {
        string normalized = relativePath.Replace('\\', '/').Normalize(NormalizationForm.FormC);
        return normalized.ToUpperInvariant();
    }

    private static string EnsureTrailingSeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
    }
}

internal sealed record SafeFileEntry(string FullPath, string RelativePath);

internal sealed record WalkResult(
    IReadOnlyList<SafeFileEntry> Files,
    IReadOnlyList<string> ReparsePaths,
    ScanError? Error);

internal enum GlobMatchStatus
{
    NoMatch,
    Match,
    Failure
}

internal readonly record struct GlobMatchResult(
    GlobMatchStatus Status,
    long Steps,
    long WorkBound);

internal sealed class GlobMatchBudget
{
    // This budget is shared by every ignored-path comparison in one scan, including
    // active-root traversal, directory pruning, and repository path-policy discovery.
    internal const long MaximumAggregateSteps = 50_000_000;

    internal GlobMatchBudget(long maximumSteps = MaximumAggregateSteps)
    {
        MaximumSteps = maximumSteps > 0
            ? maximumSteps
            : throw new ArgumentOutOfRangeException(nameof(maximumSteps));
    }

    internal long MaximumSteps { get; }

    internal long Steps { get; private set; }

    internal bool TryConsume()
    {
        if (Steps >= MaximumSteps)
        {
            return false;
        }

        Steps++;
        return true;
    }
}

internal static class SafeFileWalker
{
    private const int MaximumEntries = 100_000;

    internal static WalkResult Walk(
        string repositoryRoot,
        string root,
        bool failOnAccessErrors,
        Func<string, GlobMatchStatus>? shouldPruneDirectory = null)
    {
        var files = new List<SafeFileEntry>();
        var reparsePaths = new List<string>();
        var pending = new Stack<DirectoryInfo>();
        DirectoryInfo startingDirectory = new(root);
        GlobMatchStatus startingDirectoryStatus = shouldPruneDirectory?.Invoke(
            PathUtilities.NormalizeRelative(repositoryRoot, startingDirectory.FullName)) ?? GlobMatchStatus.NoMatch;
        if (startingDirectoryStatus == GlobMatchStatus.Failure)
        {
            return new WalkResult(
                files,
                reparsePaths,
                new ScanError(FixtureVaultContract.IgnoredPathMatchingErrorCode, "Ignored path matching could not be completed safely."));
        }

        if (startingDirectoryStatus == GlobMatchStatus.Match)
        {
            return new WalkResult(files, reparsePaths, null);
        }

        pending.Push(startingDirectory);
        int entriesSeen = 0;

        while (pending.Count > 0)
        {
            DirectoryInfo directory = pending.Pop();
            FileSystemInfo[] entries;
            try
            {
                entries = directory.GetFileSystemInfos();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                if (!failOnAccessErrors)
                {
                    continue;
                }

                return new WalkResult(files, reparsePaths, new ScanError(
                    "FV-E002",
                    "A configured fixture root could not be inspected completely."));
            }

            foreach (FileSystemInfo entry in entries)
            {
                FileAttributes attributes;
                try
                {
                    attributes = entry.Attributes;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException)
                {
                    if (!failOnAccessErrors)
                    {
                        if (!IsWithinEntryLimit(ref entriesSeen))
                        {
                            return new WalkResult(files, reparsePaths, new ScanError(
                                "FV-E003",
                                "The scan exceeded its filesystem entry safety limit."));
                        }

                        continue;
                    }

                    return new WalkResult(files, reparsePaths, new ScanError(
                        "FV-E002",
                        "A configured fixture root could not be inspected completely."));
                }

                string relativePath = PathUtilities.NormalizeRelative(repositoryRoot, entry.FullName);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    if (!IsWithinEntryLimit(ref entriesSeen))
                    {
                        return new WalkResult(files, reparsePaths, new ScanError(
                            "FV-E003",
                            "The scan exceeded its filesystem entry safety limit."));
                    }

                    reparsePaths.Add(relativePath);
                    continue;
                }

                if (entry is DirectoryInfo childDirectory)
                {
                    GlobMatchStatus directoryStatus = shouldPruneDirectory?.Invoke(relativePath) ?? GlobMatchStatus.NoMatch;
                    if (directoryStatus == GlobMatchStatus.Failure)
                    {
                        return new WalkResult(
                            files,
                            reparsePaths,
                            new ScanError(FixtureVaultContract.IgnoredPathMatchingErrorCode, "Ignored path matching could not be completed safely."));
                    }

                    if (directoryStatus == GlobMatchStatus.Match)
                    {
                        continue;
                    }

                    if (!IsWithinEntryLimit(ref entriesSeen))
                    {
                        return new WalkResult(files, reparsePaths, new ScanError(
                            "FV-E003",
                            "The scan exceeded its filesystem entry safety limit."));
                    }

                    pending.Push(childDirectory);
                }
                else
                {
                    if (!IsWithinEntryLimit(ref entriesSeen))
                    {
                        return new WalkResult(files, reparsePaths, new ScanError(
                            "FV-E003",
                            "The scan exceeded its filesystem entry safety limit."));
                    }

                    files.Add(new SafeFileEntry(entry.FullName, relativePath));
                }
            }
        }

        return new WalkResult(files, reparsePaths, null);
    }

    private static bool IsWithinEntryLimit(ref int entriesSeen) => ++entriesSeen <= MaximumEntries;
}

internal sealed class GlobMatcher
{
    private readonly GlobToken[] tokens;
    private readonly int[] globStarSlashOrdinals;
    private readonly int[] globStarSlashNextStates;
    private readonly int stateCount;

    private GlobMatcher(GlobToken[] tokens, int[] globStarSlashOrdinals)
    {
        this.tokens = tokens;
        this.globStarSlashOrdinals = globStarSlashOrdinals;
        globStarSlashNextStates = new int[globStarSlashOrdinals.Count(ordinal => ordinal >= 0)];
        for (int tokenIndex = 0; tokenIndex < globStarSlashOrdinals.Length; tokenIndex++)
        {
            int ordinal = globStarSlashOrdinals[tokenIndex];
            if (ordinal >= 0)
            {
                globStarSlashNextStates[ordinal] = tokenIndex + 1;
            }
        }

        stateCount = tokens.Length + 1 + globStarSlashNextStates.Length;
    }

    internal static bool TryCreate(string pattern, out GlobMatcher? matcher)
    {
        matcher = null;
        if (string.IsNullOrWhiteSpace(pattern) || pattern.Length > 256 || pattern.Contains('\0'))
        {
            return false;
        }

        if (IsRootedOrDriveQualified(pattern))
        {
            return false;
        }

        string normalized = PathUtilities.NormalizeComparisonPath(pattern.Replace('\\', '/'));
        if (normalized.Split('/').Any(segment => segment == ".."))
        {
            return false;
        }

        var tokens = new List<GlobToken>(normalized.Length);
        var globStarSlashOrdinals = new List<int>(normalized.Length);
        int globStarSlashCount = 0;
        for (int i = 0; i < normalized.Length; i++)
        {
            char character = normalized[i];
            if (character == '*' && i + 2 < normalized.Length && normalized[i + 1] == '*' && normalized[i + 2] == '/')
            {
                tokens.Add(new GlobToken(GlobTokenKind.GlobStarSlash, '\0'));
                globStarSlashOrdinals.Add(globStarSlashCount++);
                i += 2;
            }
            else if (character == '*' && i + 1 < normalized.Length && normalized[i + 1] == '*')
            {
                tokens.Add(new GlobToken(GlobTokenKind.GlobStar, '\0'));
                globStarSlashOrdinals.Add(-1);
                i++;
            }
            else if (character == '*')
            {
                tokens.Add(new GlobToken(GlobTokenKind.SegmentStar, '\0'));
                globStarSlashOrdinals.Add(-1);
            }
            else if (character == '?')
            {
                tokens.Add(new GlobToken(GlobTokenKind.SingleCharacter, '\0'));
                globStarSlashOrdinals.Add(-1);
            }
            else
            {
                tokens.Add(new GlobToken(GlobTokenKind.Literal, character));
                globStarSlashOrdinals.Add(-1);
            }
        }

        matcher = new GlobMatcher(tokens.ToArray(), globStarSlashOrdinals.ToArray());
        return true;
    }

    private static bool IsRootedOrDriveQualified(string pattern)
    {
        if (pattern[0] is '/' or '\\')
        {
            return true;
        }

        return pattern.Length >= 2 &&
               pattern[1] == ':' &&
               ((pattern[0] >= 'A' && pattern[0] <= 'Z') || (pattern[0] >= 'a' && pattern[0] <= 'z'));
    }

    internal int StateCount => stateCount;

    internal int TokenCount => tokens.Length;

    internal GlobMatchResult Match(string relativePath, GlobMatchBudget? budget = null)
    {
        string normalizedPath = PathUtilities.NormalizeComparisonPath(relativePath);
        // Each path character performs one clear pass, one state pass, and one epsilon pass.
        // Therefore workBound = (2 * stateCount + tokenCount) * (pathLength + 1), with no backtracking.
        long workBound = checked((2L * stateCount + tokens.Length) * ((long)normalizedPath.Length + 1));
        long steps = 0;
        bool[] activeStates = new bool[stateCount];
        bool[] nextStates = new bool[stateCount];
        activeStates[0] = true;

        if (!TryCloseEpsilon(activeStates, ref steps, workBound, budget))
        {
            return new GlobMatchResult(GlobMatchStatus.Failure, steps, workBound);
        }

        foreach (char pathCharacter in normalizedPath)
        {
            for (int state = 0; state < nextStates.Length; state++)
            {
                if (!TryConsume(ref steps, workBound, budget))
                {
                    return new GlobMatchResult(GlobMatchStatus.Failure, steps, workBound);
                }

                nextStates[state] = false;
            }

            for (int state = 0; state < activeStates.Length; state++)
            {
                if (!TryConsume(ref steps, workBound, budget))
                {
                    return new GlobMatchResult(GlobMatchStatus.Failure, steps, workBound);
                }

                if (!activeStates[state])
                {
                    continue;
                }

                if (state == tokens.Length)
                {
                    continue;
                }

                if (state < tokens.Length)
                {
                    GlobToken token = tokens[state];
                    switch (token.Kind)
                    {
                        case GlobTokenKind.Literal when token.Value == pathCharacter:
                        case GlobTokenKind.SingleCharacter when pathCharacter != '/':
                            nextStates[state + 1] = true;
                            break;
                        case GlobTokenKind.SegmentStar when pathCharacter != '/':
                            nextStates[state] = true;
                            break;
                        case GlobTokenKind.GlobStar:
                            nextStates[state] = true;
                            break;
                        case GlobTokenKind.GlobStarSlash:
                            nextStates[GetInsideState(state)] = true;
                            if (pathCharacter == '/')
                            {
                                nextStates[state + 1] = true;
                            }

                            break;
                    }

                    continue;
                }

                int ordinal = state - tokens.Length - 1;
                nextStates[state] = true;
                if (pathCharacter == '/')
                {
                    nextStates[globStarSlashNextStates[ordinal]] = true;
                }
            }

            if (!TryCloseEpsilon(nextStates, ref steps, workBound, budget))
            {
                return new GlobMatchResult(GlobMatchStatus.Failure, steps, workBound);
            }

            (activeStates, nextStates) = (nextStates, activeStates);
        }

        GlobMatchStatus status = activeStates[tokens.Length]
            ? GlobMatchStatus.Match
            : GlobMatchStatus.NoMatch;
        return new GlobMatchResult(status, steps, workBound);
    }

    private int GetInsideState(int tokenIndex)
    {
        int ordinal = globStarSlashOrdinals[tokenIndex];
        return tokens.Length + 1 + ordinal;
    }

    private bool TryCloseEpsilon(
        bool[] states,
        ref long steps,
        long workBound,
        GlobMatchBudget? budget)
    {
        for (int state = 0; state < tokens.Length; state++)
        {
            if (!TryConsume(ref steps, workBound, budget))
            {
                return false;
            }

            if (states[state] && tokens[state].Kind is GlobTokenKind.SegmentStar or GlobTokenKind.GlobStar or GlobTokenKind.GlobStarSlash)
            {
                states[state + 1] = true;
            }
        }

        return true;
    }

    private static bool TryConsume(ref long steps, long workBound, GlobMatchBudget? budget)
    {
        if (steps >= workBound || budget is not null && !budget.TryConsume())
        {
            return false;
        }

        steps++;
        return true;
    }

    private readonly record struct GlobToken(GlobTokenKind Kind, char Value);

    private enum GlobTokenKind
    {
        Literal,
        SingleCharacter,
        SegmentStar,
        GlobStar,
        GlobStarSlash
    }
}

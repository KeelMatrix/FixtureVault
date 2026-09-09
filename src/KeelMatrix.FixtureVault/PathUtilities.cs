using System.Text;
using System.Text.RegularExpressions;

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
        string? gitRoot = null;
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, FixtureVaultContract.PolicyFileName)))
            {
                return directory.FullName;
            }

            if (gitRoot is null && (File.Exists(Path.Combine(directory.FullName, ".git")) ||
                                    Directory.Exists(Path.Combine(directory.FullName, ".git"))))
            {
                gitRoot = directory.FullName;
            }

            directory = directory.Parent;
        }

        return gitRoot ?? current;
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

            var attributes = File.GetAttributes(fullPath);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                error = "A configured fixture root is a link and cannot be scanned safely.";
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

internal static class SafeFileWalker
{
    private const int MaximumEntries = 100_000;

    internal static WalkResult Walk(string repositoryRoot, string root, bool failOnAccessErrors)
    {
        var files = new List<SafeFileEntry>();
        var reparsePaths = new List<string>();
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
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
                entriesSeen++;
                if (entriesSeen > MaximumEntries)
                {
                    return new WalkResult(files, reparsePaths, new ScanError(
                        "FV-E003",
                        "The scan exceeded its filesystem entry safety limit."));
                }

                FileAttributes attributes;
                try
                {
                    attributes = entry.Attributes;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException)
                {
                    if (!failOnAccessErrors)
                    {
                        continue;
                    }

                    return new WalkResult(files, reparsePaths, new ScanError(
                        "FV-E002",
                        "A configured fixture root could not be inspected completely."));
                }

                string relativePath = PathUtilities.NormalizeRelative(repositoryRoot, entry.FullName);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    reparsePaths.Add(relativePath);
                    continue;
                }

                if (entry is DirectoryInfo childDirectory)
                {
                    pending.Push(childDirectory);
                }
                else
                {
                    files.Add(new SafeFileEntry(entry.FullName, relativePath));
                }
            }
        }

        return new WalkResult(files, reparsePaths, null);
    }
}

internal sealed class GlobMatcher
{
    private readonly Regex regex;

    private GlobMatcher(Regex regex)
    {
        this.regex = regex;
    }

    internal static bool TryCreate(string pattern, out GlobMatcher? matcher)
    {
        matcher = null;
        if (string.IsNullOrWhiteSpace(pattern) || pattern.Length > 256 || pattern.Contains('\0'))
        {
            return false;
        }

        string normalized = PathUtilities.NormalizeComparisonPath(pattern.Replace('\\', '/').TrimStart('/'));
        if (Path.IsPathRooted(pattern) || normalized.StartsWith("../", StringComparison.Ordinal) || normalized == "..")
        {
            return false;
        }

        var expression = new StringBuilder("^");
        for (int i = 0; i < normalized.Length; i++)
        {
            char character = normalized[i];
            if (character == '*' && i + 2 < normalized.Length && normalized[i + 1] == '*' && normalized[i + 2] == '/')
            {
                expression.Append("(?:.*/)?");
                i += 2;
            }
            else if (character == '*' && i + 1 < normalized.Length && normalized[i + 1] == '*')
            {
                expression.Append(".*");
                i++;
            }
            else if (character == '*')
            {
                expression.Append("[^/]*");
            }
            else if (character == '?')
            {
                expression.Append("[^/]");
            }
            else
            {
                expression.Append(Regex.Escape(character.ToString()));
            }
        }

        expression.Append('$');
        try
        {
            matcher = new GlobMatcher(new Regex(
                expression.ToString(),
                RegexOptions.CultureInvariant | RegexOptions.Compiled,
                TimeSpan.FromMilliseconds(100)));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    internal bool IsMatch(string relativePath)
    {
        try
        {
            return regex.IsMatch(PathUtilities.NormalizeComparisonPath(relativePath));
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}

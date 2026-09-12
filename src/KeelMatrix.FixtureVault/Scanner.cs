using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KeelMatrix.Redaction;

namespace KeelMatrix.FixtureVault;

internal sealed class FixtureScanner
{
    private const long MaximumTotalBytes = 128 * 1024 * 1024;
    private static readonly string[] SupportedConventions =
        ["verify", "snapshooter", "generic", "fixturevault-manifest"];
    private static readonly string[] KnownBinaryExtensions =
    [
        ".bmp", ".gif", ".ico", ".jpg", ".jpeg", ".pdf", ".png", ".zip", ".bin", ".webp"
    ];

    internal static ScanResult Scan(
        string repositoryRoot,
        FixtureVaultPolicy policy,
        IReadOnlyList<string> rootOverrides,
        bool strictOverride)
    {
        var findings = new List<Finding>();
        var skipped = new List<SkippedDiagnostic>();
        var errors = new List<ScanError>();

        var ignoredMatchers = new List<GlobMatcher>();
        foreach (string ignoredPath in policy.IgnoredPaths ?? [])
        {
            if (!GlobMatcher.TryCreate(ignoredPath, out GlobMatcher? matcher) || matcher is null)
            {
                errors.Add(new ScanError("FV-E007", "An ignored path pattern is malformed."));
                return CompleteWithErrors(errors);
            }

            ignoredMatchers.Add(matcher);
        }

        var activeRoots = new List<ResolvedRoot>();
        IReadOnlyList<string> configuredRoots = rootOverrides.Count > 0 ? rootOverrides : policy.Roots!;
        var seenRoots = new HashSet<string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (string configuredRoot in configuredRoots)
        {
            if (!PathUtilities.TryResolveRoot(repositoryRoot, configuredRoot, out string fullPath, out string relativePath, out string error))
            {
                errors.Add(new ScanError("FV-E008", error));
                return CompleteWithErrors(errors);
            }

            string key = fullPath;
            if (seenRoots.Add(key))
            {
                activeRoots.Add(new ResolvedRoot(fullPath, relativePath));
            }
        }

        AddConventionSkips(policy, skipped);
        List<SafeFileEntry> fixtureFiles = [];
        var seenFiles = new HashSet<string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (ResolvedRoot root in activeRoots)
        {
            WalkResult walk = SafeFileWalker.Walk(
                repositoryRoot,
                root.FullPath,
                failOnAccessErrors: true,
                shouldPruneDirectory: relativePath => IsIgnoredDirectory(relativePath, ignoredMatchers));
            AddReparseSkips(walk, skipped);
            if (walk.Error is not null)
            {
                errors.Add(walk.Error);
                return CompleteWithErrors(errors);
            }

            foreach (SafeFileEntry file in walk.Files)
            {
                if (IsIgnored(file.RelativePath, ignoredMatchers))
                {
                    continue;
                }

                if (IsFixtureCandidate(
                        file.RelativePath,
                        policy,
                        insideActiveRoot: true,
                        isRepositoryRoot: root.RelativePath.Length == 0) &&
                    seenFiles.Add(file.FullPath))
                {
                    fixtureFiles.Add(file);
                }
            }
        }

        AddPathPolicyFindings(repositoryRoot, policy, activeRoots, ignoredMatchers, findings, skipped, errors);
        if (errors.Count > 0)
        {
            return CompleteWithErrors(errors, fixtureFiles.Count);
        }

        AddCaseCollisionFindings(fixtureFiles, policy, findings);

        ManifestLoadResult manifest = LoadManifest(repositoryRoot, policy);
        if (manifest.Error is not null)
        {
            errors.Add(manifest.Error);
            return CompleteWithErrors(errors);
        }

        if (manifest.ActiveBaselines is not null)
        {
            foreach (SafeFileEntry file in fixtureFiles)
            {
                if (IsBaselineCandidate(file.RelativePath, policy) &&
                    !manifest.ActiveBaselines.Contains(PathUtilities.NormalizeComparisonPath(file.RelativePath)))
                {
                    AddFinding(
                        findings,
                        policy,
                        "FV002",
                        file.RelativePath,
                        "This baseline is not listed by the explicit FixtureVault manifest.",
                        "Add the baseline to .fixturevault.manifest.json or remove the stale baseline.");
                }
            }
        }
        else if (fixtureFiles.Count > 0)
        {
            AddOrphanProofSkips(policy, skipped);
        }

        long totalBytesRead = 0;
        foreach (SafeFileEntry file in fixtureFiles.OrderBy(file => file.RelativePath, StringComparer.Ordinal))
        {
            if ((HasConvention(policy, "verify") && IsVerifyReceivedPath(file.RelativePath)) ||
                (HasConvention(policy, "snapshooter") && IsSnapshooterMismatchPath(file.RelativePath)))
            {
                AddFinding(
                    findings,
                    policy,
                    "FV001",
                    file.RelativePath,
                    "A received/unapproved snapshot artifact is present in the configured fixture tree.",
                    "Review it and either approve it through the existing framework or remove it.");
            }

            if (!TryGetFileLength(file.FullPath, out long length))
            {
                errors.Add(new ScanError("FV-E009", "A fixture file could not be inspected safely."));
                return CompleteWithErrors(errors, fixtureFiles.Count(fileEntry => fileEntry.RelativePath != string.Empty));
            }

            if (length > policy.MaxFileBytes)
            {
                AddFinding(
                    findings,
                    policy,
                    "FV004",
                    file.RelativePath,
                    "The fixture file exceeds the configured maximum size.",
                    "Reduce the fixture size or raise maxFileBytes deliberately in .fixturevault.json.");
                continue;
            }

            if (totalBytesRead + length > MaximumTotalBytes)
            {
                errors.Add(new ScanError("FV-E010", "The scan exceeded its total byte safety limit."));
                return CompleteWithErrors(errors, fixtureFiles.Count);
            }

            if (!TryReadBytes(file.FullPath, length, out byte[] bytes))
            {
                errors.Add(new ScanError("FV-E009", "A fixture file could not be inspected safely."));
                return CompleteWithErrors(errors, fixtureFiles.Count);
            }

            totalBytesRead += length;
            InspectContent(file, bytes, policy, findings);
        }

        bool strict = strictOverride || policy.Ci!.Strict;
        if (!strict)
        {
            findings = findings
                .Select(finding => finding with { Severity = "warning", Disposition = "warn" })
                .ToList();
        }

        var report = new ScanReport
        {
            FilesInspected = fixtureFiles.Count,
            Findings = findings.OrderBy(finding => finding.Path, StringComparer.Ordinal)
                .ThenBy(finding => finding.RuleId, StringComparer.Ordinal)
                .ToList(),
            Skipped = skipped.OrderBy(item => item.Code, StringComparer.Ordinal)
                .ThenBy(item => item.Path, StringComparer.Ordinal)
                .ToList()
        };
        return new ScanResult(report, report.HasBlockingFindings ? 1 : 0, true);
    }

    private static void AddPathPolicyFindings(
        string repositoryRoot,
        FixtureVaultPolicy policy,
        IReadOnlyList<ResolvedRoot> activeRoots,
        IReadOnlyList<GlobMatcher> ignoredMatchers,
        ICollection<Finding> findings,
        ICollection<SkippedDiagnostic> skipped,
        List<ScanError> errors)
    {
        WalkResult walk = SafeFileWalker.Walk(
            repositoryRoot,
            repositoryRoot,
            failOnAccessErrors: false,
            shouldPruneDirectory: relativePath => IsIgnoredDirectory(relativePath, ignoredMatchers));
        AddReparseSkips(walk, skipped);
        if (walk.Error is not null)
        {
            errors.Add(walk.Error);
            return;
        }

        foreach (SafeFileEntry file in walk.Files)
        {
            if (IsIgnored(file.RelativePath, ignoredMatchers) ||
                !IsFixtureCandidate(file.RelativePath, policy, insideActiveRoot: false) ||
                activeRoots.Any(root => PathUtilities.IsWithin(root.FullPath, file.FullPath)))
            {
                continue;
            }

            AddFinding(
                findings,
                policy,
                "FV008",
                file.RelativePath,
                "A fixture-looking file is outside the approved fixture roots.",
                "Move the fixture below an approved root or update the roots in .fixturevault.json.");
        }
    }

    private static void AddCaseCollisionFindings(
        IReadOnlyList<SafeFileEntry> files,
        FixtureVaultPolicy policy,
        ICollection<Finding> findings)
    {
        var groups = files.GroupBy(file => PathUtilities.NormalizeComparisonPath(file.RelativePath), StringComparer.Ordinal);
        foreach (IGrouping<string, SafeFileEntry> group in groups)
        {
            List<SafeFileEntry> collisions = group
                .GroupBy(file => file.RelativePath, StringComparer.Ordinal)
                .Select(grouping => grouping.First())
                .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
                .ToList();
            if (collisions.Count < 2)
            {
                continue;
            }

            string names = string.Join(", ", collisions.Select(file => file.RelativePath));
            foreach (SafeFileEntry collision in collisions)
            {
                AddFinding(
                    findings,
                    policy,
                    "FV003",
                    collision.RelativePath,
                    $"This fixture path case-collides with another fixture path: {names}.",
                    "Rename one path so its repository-relative spelling is unique across case-sensitive and case-insensitive filesystems.");
            }
        }
    }

    private static ManifestLoadResult LoadManifest(string repositoryRoot, FixtureVaultPolicy policy)
    {
        if (!HasConvention(policy, "fixturevault-manifest"))
        {
            return new ManifestLoadResult(null, null);
        }

        string path = Path.Combine(repositoryRoot, FixtureVaultContract.ManifestFileName);
        if (!File.Exists(path))
        {
            return new ManifestLoadResult(null, new ScanError(
                "FV-E012",
                $"The configured FixtureVault manifest was not found. Create {FixtureVaultContract.ManifestFileName} or remove the 'fixturevault-manifest' convention."));
        }

        try
        {
            var fileInfo = new FileInfo(path);
            if (fileInfo.Length <= 0 || fileInfo.Length > 64 * 1024)
            {
                return new ManifestLoadResult(null, new ScanError(
                    "FV-E011",
                    "The configured FixtureVault manifest is malformed."));
            }

            var manifest = JsonSerializer.Deserialize<FixtureVaultManifest>(
                File.ReadAllText(path),
                FixtureVaultContract.JsonOptions);
            if (manifest is null || manifest.Version != FixtureVaultContract.PolicySchemaVersion ||
                manifest.ActiveBaselines is null || manifest.ActiveBaselines.Count > 100_000)
            {
                return new ManifestLoadResult(null, new ScanError(
                    "FV-E011",
                    "The configured FixtureVault manifest is malformed."));
            }

            var paths = new HashSet<string>(StringComparer.Ordinal);
            foreach (string baseline in manifest.ActiveBaselines)
            {
                if (string.IsNullOrWhiteSpace(baseline) || Path.IsPathRooted(baseline) || baseline.Contains('\0'))
                {
                    return new ManifestLoadResult(null, new ScanError(
                        "FV-E011",
                        "The configured FixtureVault manifest is malformed."));
                }

                string normalized = baseline.Replace('\\', '/').Normalize(NormalizationForm.FormC);
                string fullBaselinePath;
                try
                {
                    fullBaselinePath = Path.GetFullPath(Path.Combine(repositoryRoot, normalized));
                }
                catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
                {
                    return new ManifestLoadResult(null, new ScanError(
                        "FV-E011",
                        "The configured FixtureVault manifest is malformed."));
                }

                if (!PathUtilities.IsWithin(repositoryRoot, fullBaselinePath))
                {
                    return new ManifestLoadResult(null, new ScanError(
                        "FV-E011",
                        "The configured FixtureVault manifest is malformed."));
                }

                paths.Add(PathUtilities.NormalizeComparisonPath(
                    PathUtilities.NormalizeRelative(repositoryRoot, fullBaselinePath)));
            }

            return new ManifestLoadResult(paths, null);
        }
        catch (JsonException)
        {
            return new ManifestLoadResult(null, new ScanError(
                "FV-E011",
                "The configured FixtureVault manifest is malformed."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return new ManifestLoadResult(null, new ScanError(
                "FV-E011",
                "The configured FixtureVault manifest could not be read safely."));
        }
    }

    private static void InspectContent(
        SafeFileEntry file,
        byte[] bytes,
        FixtureVaultPolicy policy,
        ICollection<Finding> findings)
    {
        bool hasOtherBom = bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) ||
                                                 (bytes[0] == 0xFE && bytes[1] == 0xFF) ||
                                                 (bytes.Length >= 4 && bytes[0] == 0x00 && bytes[1] == 0x00 &&
                                                  bytes[2] == 0xFE && bytes[3] == 0xFF) ||
                                                 (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE &&
                                                  bytes[2] == 0x00 && bytes[3] == 0x00));
        bool isBinary = IsKnownBinaryExtension(file.RelativePath) || (!hasOtherBom && bytes.Contains((byte)0));
        if (isBinary)
        {
            if (HasConvention(policy, "verify") && IsVerifyReceivedPath(file.RelativePath))
            {
                return;
            }

            if (IsAcceptedBinaryFixture(file.RelativePath, policy))
            {
                return;
            }

            AddFinding(
                findings,
                policy,
                "FV005",
                file.RelativePath,
                "An unexpected binary asset is present in the fixture tree.",
                "Remove the binary asset or keep only supported text fixtures.");
            return;
        }

        if (hasOtherBom)
        {
            AddFinding(
                findings,
                policy,
                "FV006",
                file.RelativePath,
                "The fixture uses a non-UTF-8 encoding.",
                "Save the fixture as UTF-8 text. A UTF-8 byte-order mark is supported where the fixture convention permits it.");
            return;
        }

        string text;
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            AddFinding(
                findings,
                policy,
                "FV006",
                file.RelativePath,
                "The fixture is not valid UTF-8 text.",
                "Save the fixture as deterministic UTF-8 text and avoid locale-specific encodings.");
            return;
        }

        bool isVerifyFixture = HasConvention(policy, "verify") && IsVerifySnapshotPath(file.RelativePath);
        bool hasCarriageReturn = bytes.Contains((byte)'\r');
        bool hasTrailingNewline = bytes.Length > 0 && (bytes[^1] == (byte)'\r' || bytes[^1] == (byte)'\n');
        if (isVerifyFixture && (hasCarriageReturn || hasTrailingNewline))
        {
            AddFinding(
                findings,
                policy,
                "FV006",
                file.RelativePath,
                "The Verify fixture has newline bytes that do not match Verify's LF-only, no-trailing-newline convention.",
                "Regenerate or save the Verify fixture as UTF-8 with LF-only newlines and no trailing newline. A UTF-8 byte-order mark is supported.");
        }

        if (HasSensitiveData(text, policy))
        {
            AddFinding(
                findings,
                policy,
                "FV007",
                file.RelativePath,
                "Potential sensitive data was detected in this fixture.",
                "Remove the sensitive value from the fixture; FixtureVault never prints the matched value.");
        }
    }

    private static bool HasSensitiveData(string text, FixtureVaultPolicy policy)
    {
        if (!(policy.SensitiveDataRules ?? []).Any(rule =>
                rule.Equals(FixtureVaultContract.HighConfidenceSensitiveDataRule, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        ITextRedactor[] redactors =
        [
            new AuthorizationRedactor(),
            new ApiKeyRedactor(),
            new AwsAccessKeyRedactor(),
            new AzureKeyLikeRedactor(),
            new ConnectionStringPasswordRedactor(),
            new CookieRedactor(),
            new GoogleApiKeyRedactor(),
            new JwtTokenRedactor(),
            new RegexReplaceRedactor(
                @"(?i)[""']?\b(api[_-]?key|client[_-]?secret|password|secret|token)\b[""']?\s*[:=]\s*[""']?[A-Za-z0-9_./+=-]{16,}",
                "$1=<redacted>")
        ];

        foreach (ITextRedactor redactor in redactors)
        {
            try
            {
                if (!string.Equals(redactor.Redact(text), text, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            catch
            {
                // A detector failure must not turn into a diagnostic leak or a scan crash.
            }
        }

        return false;
    }

    private static bool IsFixtureCandidate(
        string relativePath,
        FixtureVaultPolicy policy,
        bool insideActiveRoot,
        bool isRepositoryRoot = false)
    {
        string fileName = Path.GetFileName(relativePath);
        if (fileName.Equals(FixtureVaultContract.PolicyFileName, StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals(FixtureVaultContract.ManifestFileName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        bool allowedExtension = (policy.AllowedExtensions ?? []).Any(extension =>
            fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
        bool verify = HasConvention(policy, "verify") &&
                      IsVerifySnapshotPath(relativePath);
        bool snapshooter = HasConvention(policy, "snapshooter") &&
                           fileName.EndsWith(".snap", StringComparison.OrdinalIgnoreCase);
        return allowedExtension || verify || snapshooter ||
               (insideActiveRoot &&
                ((IsKnownBinaryExtension(relativePath) && !isRepositoryRoot) ||
                 (isRepositoryRoot && IsKnownBinaryConventionPath(relativePath))));
    }

    private static bool IsBaselineCandidate(string relativePath, FixtureVaultPolicy policy)
    {
        string fileName = Path.GetFileName(relativePath);
        return (HasConvention(policy, "verify") && IsVerifyVerifiedPath(relativePath)) ||
                (HasConvention(policy, "snapshooter") &&
                 fileName.EndsWith(".snap", StringComparison.OrdinalIgnoreCase) &&
                 !IsSnapshooterMismatchPath(relativePath)) ||
                (HasConvention(policy, "generic") &&
                 HasAllowedExtension(fileName, policy));
    }

    private static bool IsAcceptedBinaryFixture(string relativePath, FixtureVaultPolicy policy)
    {
        return (HasConvention(policy, "verify") && IsVerifyVerifiedPath(relativePath)) ||
               (IsKnownBinaryExtension(relativePath) && HasAllowedExtension(Path.GetFileName(relativePath), policy));
    }

    private static bool IsKnownBinaryConventionPath(string relativePath) =>
        IsVerifySnapshotPath(relativePath);

    private static bool HasAllowedExtension(string fileName, FixtureVaultPolicy policy) =>
        (policy.AllowedExtensions ?? []).Any(extension =>
            fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

    private static bool IsVerifySnapshotPath(string relativePath) =>
        IsVerifyReceivedPath(relativePath) || IsVerifyVerifiedPath(relativePath);

    private static bool IsVerifyReceivedPath(string relativePath) =>
        IsVerifyFilePath(relativePath, ".received.") || IsVerifySplitDirectoryPath(relativePath, ".received");

    private static bool IsVerifyVerifiedPath(string relativePath) =>
        IsVerifyFilePath(relativePath, ".verified.") || IsVerifySplitDirectoryPath(relativePath, ".verified");

    private static bool IsVerifyFilePath(string relativePath, string marker) =>
        Path.GetFileName(relativePath).Contains(marker, StringComparison.OrdinalIgnoreCase);

    private static bool IsVerifySplitDirectoryPath(string relativePath, string suffix)
    {
        string[] segments = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int index = 0; index < segments.Length - 1; index++)
        {
            if (segments[index].EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSnapshooterMismatchPath(string relativePath)
    {
        string[] segments = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (!Path.GetFileName(relativePath).EndsWith(".snap", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        for (int index = 0; index < segments.Length - 2; index++)
        {
            if (segments[index].Equals("__snapshots__", StringComparison.OrdinalIgnoreCase) &&
                (segments[index + 1].Equals("mismatch", StringComparison.OrdinalIgnoreCase) ||
                 segments[index + 1].Equals("__mismatch__", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasConvention(FixtureVaultPolicy policy, string convention)
    {
        return (policy.Conventions ?? []).Any(item => item.Equals(convention, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsKnownBinaryExtension(string relativePath)
    {
        return KnownBinaryExtensions.Contains(Path.GetExtension(relativePath), StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsIgnored(string relativePath, IReadOnlyList<GlobMatcher> matchers)
    {
        return matchers.Any(matcher => matcher.IsMatch(relativePath));
    }

    private static bool IsIgnoredDirectory(string relativePath, IReadOnlyList<GlobMatcher> matchers)
    {
        string directoryPath = relativePath.TrimEnd('/', '\\') + "/";
        return IsIgnored(relativePath, matchers) || IsIgnored(directoryPath, matchers);
    }

    private static void AddFinding(
        ICollection<Finding> findings,
        FixtureVaultPolicy policy,
        string ruleId,
        string path,
        string message,
        string remediation)
    {
        findings.Add(new Finding(ruleId, "error", "block", path, message, remediation));
    }

    private static void AddConventionSkips(FixtureVaultPolicy policy, List<SkippedDiagnostic> skipped)
    {
        foreach (string convention in policy.Conventions ?? [])
        {
            if (!SupportedConventions.Contains(convention, StringComparer.OrdinalIgnoreCase))
            {
                skipped.Add(new SkippedDiagnostic(
                    "FV-SKIP-CONVENTION",
                    convention,
                    null,
                    "This convention hint is not supported by this version and was not guessed."));
            }
        }
    }

    private static void AddOrphanProofSkips(FixtureVaultPolicy policy, List<SkippedDiagnostic> skipped)
    {
        foreach (string convention in new[] { "verify", "snapshooter", "generic" })
        {
            if (HasConvention(policy, convention))
            {
                skipped.Add(new SkippedDiagnostic(
                    "FV-SKIP-ORPHAN",
                    convention,
                    null,
                    "No explicit relationship proves orphanhood for this convention, so no orphan claim was made."));
            }
        }
    }

    private static void AddReparseSkips(WalkResult walk, ICollection<SkippedDiagnostic> skipped)
    {
        foreach (string path in walk.ReparsePaths)
        {
            skipped.Add(new SkippedDiagnostic(
                "FV-SKIP-REPARSE",
                null,
                path,
                "Reparse points and symbolic links are not followed."));
        }
    }

    private static bool TryGetFileLength(string path, out long length)
    {
        try
        {
            var fileInfo = new FileInfo(path);
            length = fileInfo.Length;
            return fileInfo.Exists;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException)
        {
            length = 0;
            return false;
        }
    }

    private static bool TryReadBytes(string path, long length, out byte[] bytes)
    {
        bytes = [];
        try
        {
            bytes = new byte[checked((int)length)];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            stream.ReadExactly(bytes);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException or OverflowException)
        {
            bytes = [];
            return false;
        }
    }

    private static ScanResult CompleteWithErrors(IReadOnlyList<ScanError> errors, int filesInspected = 0)
    {
        var report = new ScanReport
        {
            FilesInspected = filesInspected,
            Errors = errors
        };
        return new ScanResult(report, 2, false);
    }

    private sealed record ResolvedRoot(string FullPath, string RelativePath);

    private sealed class FixtureVaultManifest
    {
        [JsonPropertyName("version")]
        public int Version { get; set; }

        [JsonPropertyName("activeBaselines")]
        public List<string>? ActiveBaselines { get; set; }
    }

    private sealed record ManifestLoadResult(HashSet<string>? ActiveBaselines, ScanError? Error);
}

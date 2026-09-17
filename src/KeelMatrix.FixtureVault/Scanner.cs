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
        bool strictOverride,
        GlobMatchBudget? matcherBudget = null,
        IReadOnlyList<ISensitiveDataDetector>? additionalSensitiveDataDetectors = null,
        FixtureFileWalk? fileWalk = null,
        Func<byte[], ContentClassification>? contentClassifier = null)
    {
        var findings = new List<Finding>();
        var skipped = new List<SkippedDiagnostic>();
        var errors = new List<ScanError>();
        // The strictness decision is fixed before any early failure so an error report never claims a
        // blocking disposition that the configured policy does not support.
        bool strict = strictOverride || policy.Ci?.Strict == true;
        GlobMatchBudget ignoreBudget = matcherBudget ?? new GlobMatchBudget();
        FixtureFileWalk walkFunction = fileWalk ?? SafeFileWalker.Walk;
        IReadOnlyList<ISensitiveDataDetector> detectors = CreateDefaultSensitiveDataDetectors();
        if (additionalSensitiveDataDetectors is not null)
        {
            detectors = [.. detectors, .. additionalSensitiveDataDetectors];
        }

        var ignoredMatchers = new List<GlobMatcher>();
        foreach (string ignoredPath in policy.IgnoredPaths ?? [])
        {
            if (!GlobMatcher.TryCreate(ignoredPath, out GlobMatcher? matcher) || matcher is null)
            {
                errors.Add(new ScanError("FV-E007", "An ignored path pattern is malformed."));
                return CompleteWithErrors(errors, strict);
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
                return CompleteWithErrors(errors, strict);
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
            WalkResult walk = walkFunction(
                repositoryRoot,
                root.FullPath,
                failOnAccessErrors: true,
                shouldPruneDirectory: relativePath => IsIgnoredDirectory(relativePath, ignoredMatchers, ignoreBudget));
            AddReparseSkips(walk, skipped);
            if (walk.Error is not null)
            {
                errors.Add(walk.Error);
                return CompleteWithErrors(errors, strict);
            }

            foreach (SafeFileEntry file in walk.Files)
            {
                GlobMatchStatus ignoredStatus = IsIgnored(file.RelativePath, ignoredMatchers, ignoreBudget);
                if (ignoredStatus == GlobMatchStatus.Failure)
                {
                    errors.Add(new ScanError(
                        FixtureVaultContract.IgnoredPathMatchingErrorCode,
                        "Ignored path matching could not be completed safely."));
                    return CompleteWithErrors(errors, strict, fixtureFiles.Count);
                }

                if (ignoredStatus == GlobMatchStatus.Match)
                {
                    continue;
                }

                if (IsFixtureCandidate(
                        file.RelativePath,
                        policy,
                        insideActiveRoot: true,
                        isRepositoryRoot: root.RelativePath.Length == 0) &&
                    seenFiles.Add(Path.GetFullPath(file.FullPath)))
                {
                    fixtureFiles.Add(file);
                }
            }
        }

        AddPathPolicyFindings(
            repositoryRoot,
            policy,
            activeRoots,
            ignoredMatchers,
            ignoreBudget,
            findings,
            skipped,
            errors,
            walkFunction);
        if (errors.Count > 0)
        {
            return CompleteWithErrors(errors, strict, fixtureFiles.Count);
        }

        AddCaseCollisionFindings(fixtureFiles, policy, findings);

        ManifestLoadResult manifest = LoadManifest(repositoryRoot, policy);
        if (manifest.Error is not null)
        {
            errors.Add(manifest.Error);
            return CompleteWithErrors(errors, strict);
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
                return CompleteWithErrors(errors, strict, fixtureFiles.Count(fileEntry => fileEntry.RelativePath != string.Empty));
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
                return CompleteWithErrors(errors, strict, fixtureFiles.Count);
            }

            if (!TryReadBytes(file.FullPath, length, out byte[] bytes))
            {
                errors.Add(new ScanError("FV-E009", "A fixture file could not be inspected safely."));
                return CompleteWithErrors(errors, strict, fixtureFiles.Count);
            }

            totalBytesRead += length;
            ScanError? contentError = InspectContent(
                file,
                bytes,
                policy,
                findings,
                skipped,
                detectors,
                contentClassifier ?? ContentClassification.Classify);
            if (contentError is not null)
            {
                errors.Add(contentError);
                return CompleteWithErrors(errors, strict, fixtureFiles.Count, findings, skipped);
            }
        }

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
        GlobMatchBudget matcherBudget,
        ICollection<Finding> findings,
        ICollection<SkippedDiagnostic> skipped,
        List<ScanError> errors,
        FixtureFileWalk walkFunction)
    {
        WalkResult walk = walkFunction(
            repositoryRoot,
            repositoryRoot,
            failOnAccessErrors: true,
            shouldPruneDirectory: relativePath => IsIgnoredDirectory(relativePath, ignoredMatchers, matcherBudget));
        AddReparseSkips(walk, skipped);
        if (walk.Error is not null)
        {
            errors.Add(walk.Error.Code == "FV-E002"
                ? new ScanError(
                    FixtureVaultContract.PathPolicyTraversalErrorCode,
                    FixtureVaultContract.PathPolicyTraversalErrorMessage)
                : walk.Error);
            return;
        }

        foreach (SafeFileEntry file in walk.Files)
        {
            GlobMatchStatus ignoredStatus = IsIgnored(file.RelativePath, ignoredMatchers, matcherBudget);
            if (ignoredStatus == GlobMatchStatus.Failure)
            {
                errors.Add(new ScanError(
                    FixtureVaultContract.IgnoredPathMatchingErrorCode,
                    "Ignored path matching could not be completed safely."));
                return;
            }

            if (ignoredStatus == GlobMatchStatus.Match ||
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
        if (!PathUtilities.TryIsLinkedOrReparseFile(path, out bool isLinkedOrReparse) || isLinkedOrReparse)
        {
            return new ManifestLoadResult(null, new ScanError(
                "FV-E011",
                "The configured FixtureVault manifest could not be read safely."));
        }

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

    private static ScanError? InspectContent(
        SafeFileEntry file,
        byte[] bytes,
        FixtureVaultPolicy policy,
        ICollection<Finding> findings,
        ICollection<SkippedDiagnostic> skipped,
        IReadOnlyList<ISensitiveDataDetector> sensitiveDataDetectors,
        Func<byte[], ContentClassification> contentClassifier)
    {
        bool isVerifyFixture = HasConvention(policy, "verify") && IsVerifySnapshotPath(file.RelativePath);
        bool isKnownBinaryExtension = IsKnownBinaryExtension(file.RelativePath);
        ContentClassification classification = isKnownBinaryExtension
            ? ContentClassification.KnownBinary
            : contentClassifier(bytes);

        switch (ContentClassification.Resolve(classification, isKnownBinaryExtension, isVerifyFixture))
        {
            case ContentKind.BinaryAsset:
                // Binary assets are never decoded as text. A received artifact is already reported as
                // FV001, and an accepted baseline or allowed binary extension is governed by size only.
                if (HasConvention(policy, "verify") && IsVerifyReceivedPath(file.RelativePath))
                {
                    return null;
                }

                if (isKnownBinaryExtension && IsAcceptedBinaryFixture(file.RelativePath, policy))
                {
                    return null;
                }

                AddFinding(
                    findings,
                    policy,
                    "FV005",
                    file.RelativePath,
                    "An unexpected binary asset is present in the fixture tree.",
                    "Remove the binary asset or keep only supported text fixtures.");
                return null;

            case ContentKind.Uninspectable:
                return ReportUninspectableContent(file, classification, isVerifyFixture, policy, findings, skipped);

            default:
                break;
        }

        if (classification.IsNonUtf8Declaration && !isVerifyFixture)
        {
            AddFinding(
                findings,
                policy,
                "FV006",
                file.RelativePath,
                FixtureVaultContract.NonUtf8EncodingDiagnosticMessage,
                FixtureVaultContract.NonUtf8EncodingRemediation);
        }

        SensitiveDataDetectionResult sensitiveDataResult = HasSensitiveData(classification.Text, policy, sensitiveDataDetectors);
        if (sensitiveDataResult == SensitiveDataDetectionResult.Failed)
        {
            return new ScanError(
                FixtureVaultContract.SensitiveDataDetectorErrorCode,
                FixtureVaultContract.SensitiveDataDetectorErrorMessage);
        }

        if (sensitiveDataResult == SensitiveDataDetectionResult.Found)
        {
            AddFinding(
                findings,
                policy,
                "FV007",
                file.RelativePath,
                "Potential sensitive data was detected in this fixture.",
                "Remove the sensitive value from the fixture; FixtureVault never prints the matched value.");
        }

        return null;
    }

    private static ScanError? ReportUninspectableContent(
        SafeFileEntry file,
        ContentClassification classification,
        bool isVerifyFixture,
        FixtureVaultPolicy policy,
        ICollection<Finding> findings,
        ICollection<SkippedDiagnostic> skipped)
    {
        // Content inspection is impossible without a proven text encoding, so the report must never
        // look like a fully checked clean scan for this file.
        skipped.Add(new SkippedDiagnostic(
            FixtureVaultContract.UninspectableContentSkippedCode,
            null,
            file.RelativePath,
            classification.SkippedReason));

        if (!isVerifyFixture)
        {
            AddFinding(
                findings,
                policy,
                "FV006",
                file.RelativePath,
                classification.DiagnosticMessage,
                classification.Remediation);
        }

        return IsSensitiveDataDetectionEnabled(policy)
            ? new ScanError(
                FixtureVaultContract.UninspectableContentErrorCode,
                FixtureVaultContract.UninspectableContentErrorMessage)
            : null;
    }

    private static SensitiveDataDetectionResult HasSensitiveData(
        string text,
        FixtureVaultPolicy policy,
        IReadOnlyList<ISensitiveDataDetector> sensitiveDataDetectors)
    {
        if (!IsSensitiveDataDetectionEnabled(policy))
        {
            return SensitiveDataDetectionResult.Clean;
        }

        bool found = false;
        foreach (ISensitiveDataDetector detector in sensitiveDataDetectors)
        {
            try
            {
                if (detector.IsSensitive(text))
                {
                    found = true;
                }
            }
            catch
            {
                return SensitiveDataDetectionResult.Failed;
            }
        }

        return found ? SensitiveDataDetectionResult.Found : SensitiveDataDetectionResult.Clean;
    }

    private static bool IsSensitiveDataDetectionEnabled(FixtureVaultPolicy policy) =>
        (policy.SensitiveDataRules ?? []).Any(rule =>
            rule.Equals(FixtureVaultContract.HighConfidenceSensitiveDataRule, StringComparison.OrdinalIgnoreCase));

    private static RedactionSensitiveDataDetector[] CreateDefaultSensitiveDataDetectors()
    {
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
        return redactors.Select(redactor => new RedactionSensitiveDataDetector(redactor)).ToArray();
    }

    private enum SensitiveDataDetectionResult
    {
        Clean,
        Found,
        Failed
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
                segments[index + 1].Equals("__mismatch__", StringComparison.OrdinalIgnoreCase))
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

    private static GlobMatchStatus IsIgnored(
        string relativePath,
        IReadOnlyList<GlobMatcher> matchers,
        GlobMatchBudget matcherBudget)
    {
        foreach (GlobMatcher matcher in matchers)
        {
            GlobMatchResult result = matcher.Match(relativePath, matcherBudget);
            if (result.Status != GlobMatchStatus.NoMatch)
            {
                return result.Status;
            }
        }

        return GlobMatchStatus.NoMatch;
    }

    private static GlobMatchStatus IsIgnoredDirectory(
        string relativePath,
        IReadOnlyList<GlobMatcher> matchers,
        GlobMatchBudget matcherBudget)
    {
        string directoryPath = relativePath.TrimEnd('/', '\\') + "/";
        GlobMatchStatus status = IsIgnored(relativePath, matchers, matcherBudget);
        return status == GlobMatchStatus.NoMatch
            ? IsIgnored(directoryPath, matchers, matcherBudget)
            : status;
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
                    FixtureVaultContract.UnsupportedConventionDiagnosticValue,
                    null,
                    "This convention hint is not supported by this version and was not guessed; the unsupported value is not shown."));
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

    private static ScanResult CompleteWithErrors(
        IReadOnlyList<ScanError> errors,
        bool strict,
        int filesInspected = 0,
        IReadOnlyList<Finding>? findings = null,
        IReadOnlyList<SkippedDiagnostic>? skipped = null)
    {
        IReadOnlyList<Finding> reportedFindings = strict || findings is null
            ? findings ?? []
            : findings
                .Select(finding => finding with { Severity = "warning", Disposition = "warn" })
                .ToList();
        var report = new ScanReport
        {
            FilesInspected = filesInspected,
            Findings = reportedFindings,
            Skipped = skipped ?? [],
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

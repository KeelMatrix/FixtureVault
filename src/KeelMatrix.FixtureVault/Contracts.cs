using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using KeelMatrix.Redaction;

namespace KeelMatrix.FixtureVault;

internal static class FixtureVaultContract
{
    internal const int PolicySchemaVersion = 1;
    internal const int ReportSchemaVersion = 1;
    internal const string ToolVersion = "0.1.0";
    internal const string HighConfidenceSensitiveDataRule = "high-confidence";
    internal const string UnsupportedConventionDiagnosticValue = "unsupported";
    internal const string PolicyFileName = ".fixturevault.json";
    internal const string ManifestFileName = ".fixturevault.manifest.json";
    internal const string IgnoredPathMatchingErrorCode = "FV-E013";
    internal const string SensitiveDataDetectorErrorCode = "FV-E014";
    internal const string SensitiveDataDetectorErrorMessage = "Sensitive-data detection could not be completed safely.";
    internal const string PathPolicyTraversalErrorCode = "FV-E015";
    internal const string PathPolicyTraversalErrorMessage = "Repository path-policy discovery could not be completed safely.";
    internal const string UninspectableContentErrorCode = "FV-E016";
    internal const string UninspectableContentErrorMessage = "Content inspection could not be completed because no encoding suitable for inspection could be established.";
    internal const string UninspectableContentSkippedCode = "FV-SKIP-ENCODING";
    internal const string UninspectableContentSkippedReason = "Content-dependent checks, including sensitive-data detection, did not run for this fixture because no encoding suitable for content inspection could be established.";
    internal const string UndeclaredNulContentSkippedReason = "Content-dependent checks, including sensitive-data detection, did not run for this fixture because its decoded content contains a NUL (U+0000) character and no byte-order mark declares a text encoding, so the decoded text cannot be trusted.";
    internal const string UndeclaredNulContentDiagnosticMessage = "The fixture contains NUL characters but declares no byte-order mark, so it is not proven to be UTF-8 text.";
    internal const string UndecodableContentDiagnosticMessage = "The fixture is not valid UTF-8 text.";
    internal const string NonUtf8EncodingDiagnosticMessage = "The fixture uses a non-UTF-8 encoding.";
    internal const string NonUtf8EncodingRemediation = "Save the fixture as UTF-8 text. A UTF-8 byte-order mark is supported where the fixture convention permits it.";
    internal const string NulContentRemediation = "Save the fixture as UTF-8 text without NUL characters; a byte-order mark does not make NUL content inspectable.";
    internal const string UndeclaredEncodingRemediation = "Save the fixture as deterministic UTF-8 text and avoid locale-specific encodings.";

    internal static string DeclaredNulContentSkippedReason(string declaredEncodingName) =>
        $"Content-dependent checks, including sensitive-data detection, did not run for this fixture because its decoded {declaredEncodingName} content contains a NUL (U+0000) character, so the decoded text cannot be trusted.";

    internal static string UndecodableDeclaredContentSkippedReason(string declaredEncodingName) =>
        $"Content-dependent checks, including sensitive-data detection, did not run for this fixture because its bytes declare {declaredEncodingName} with a byte-order mark but are not valid {declaredEncodingName} text.";

    internal static string DeclaredNulContentDiagnosticMessage(string declaredEncodingName) =>
        $"The fixture declares {declaredEncodingName} with a byte-order mark, but its decoded content contains a NUL (U+0000) character, so its content is not trusted for content inspection.";

    internal static string UndecodableDeclaredContentDiagnosticMessage(string declaredEncodingName) =>
        $"The fixture declares {declaredEncodingName} with a byte-order mark, but its bytes are not valid {declaredEncodingName} text.";

    internal static string DeclaredEncodingRemediation(string declaredEncodingName) =>
        $"Save the fixture as valid {declaredEncodingName} text without NUL characters, or as UTF-8 text.";

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        WriteIndented = true
    };

    internal static string SerializePolicy(FixtureVaultPolicy policy) =>
        JsonSerializer.Serialize(policy, JsonOptions).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
}

internal sealed class FixtureVaultPolicy
{
    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("roots")]
    public List<string>? Roots { get; set; }

    [JsonPropertyName("allowedExtensions")]
    public List<string>? AllowedExtensions { get; set; }

    [JsonPropertyName("maxFileBytes")]
    public long MaxFileBytes { get; set; }

    [JsonPropertyName("conventions")]
    public List<string>? Conventions { get; set; }

    [JsonPropertyName("sensitiveDataRules")]
    public List<string>? SensitiveDataRules { get; set; }

    [JsonPropertyName("ignoredPaths")]
    public List<string>? IgnoredPaths { get; set; }

    [JsonPropertyName("ci")]
    public CiPolicy? Ci { get; set; }

    internal static FixtureVaultPolicy CreateDefault(bool testsDirectoryExists)
    {
        return new FixtureVaultPolicy
        {
            Version = FixtureVaultContract.PolicySchemaVersion,
            Roots = [testsDirectoryExists ? "tests" : "."],
            AllowedExtensions = [".verified.json", ".verified.txt", ".snap", ".golden"],
            MaxFileBytes = 1_048_576,
            Conventions = ["verify", "snapshooter", "generic"],
            SensitiveDataRules = ["high-confidence"],
            IgnoredPaths = ["**/.git/**", "**/bin/**", "**/obj/**"],
            Ci = new CiPolicy { Strict = true }
        };
    }
}

internal sealed class CiPolicy
{
    [JsonPropertyName("strict")]
    public bool? Strict { get; set; }
}

// Diagnostics are a safe-rendering boundary: never include raw untrusted CLI
// arguments or policy values. Use positions, fixed categories, or bounded
// contract values so console and JSON output cannot echo caller-controlled data.
internal sealed record Finding(
    [property: JsonPropertyName("ruleId")] string RuleId,
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("disposition")] string Disposition,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("remediation")] string Remediation);

internal sealed record SkippedDiagnostic(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("convention")] string? Convention,
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("reason")] string Reason);

internal sealed record ScanError(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message);

internal sealed class ScanReport
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = FixtureVaultContract.ReportSchemaVersion;

    [JsonPropertyName("toolVersion")]
    public string ToolVersion { get; init; } = FixtureVaultContract.ToolVersion;

    [JsonPropertyName("filesInspected")]
    public int FilesInspected { get; init; }

    [JsonPropertyName("findings")]
    public IReadOnlyList<Finding> Findings { get; init; } = [];

    [JsonPropertyName("skipped")]
    public IReadOnlyList<SkippedDiagnostic> Skipped { get; init; } = [];

    [JsonPropertyName("errors")]
    public IReadOnlyList<ScanError> Errors { get; init; } = [];

    internal bool HasBlockingFindings => Findings.Any(f => f.Disposition == "block");

    internal string ToJson() => JsonSerializer.Serialize(this, FixtureVaultContract.JsonOptions)
        .Replace("\r\n", "\n", StringComparison.Ordinal);
}

internal sealed record ScanResult(ScanReport Report, int ExitCode, bool Completed);

internal interface ISensitiveDataDetector
{
    bool IsSensitive(string text);
}

internal sealed class RedactionSensitiveDataDetector(ITextRedactor redactor) : ISensitiveDataDetector
{
    private static readonly Regex AuthorizationBearerHeader = new(
        "^\\s*(?<prefix>authorization\\s*:\\s*bearer(?:\\s+|$))(?<value>[^\\r\\n]*)\\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex ApiKeyHeader = new(
        "^\\s*(?<prefix>(?:x-?api-?key|apikey)\\s*:\\s*)(?<value>[^\\r\\n]*)\\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex ApiKeyQueryValue = new(
        "(?<prefix>[?&]\\s*(?:x-)?api[-_]?key\\s*=\\s*)(?<value>\"[^\"]*\"|'[^']*'|[^&#\\s]*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex CookieHeader = new(
        "^\\s*(?<name>set-cookie|cookie)\\s*:\\s*(?<value>[^\\r\\n]*)\\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex AlreadyRedactedValue = new(
        "^['\\\"]?(?:\\*{3,}|<\\s*redacted\\s*>|\\[\\s*redacted\\s*\\]|redacted|masked|removed)['\\\"]?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex EmptyCredentialAssignment = new(
        "^\\s*[^=;&\\s]+\\s*=\\s*(?:\\\"\\s*\\\"|'\\s*')?\\s*$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public bool IsSensitive(string text)
    {
        // Supported credential/header values are line-scoped. Evaluate each line separately so a
        // redactor cannot join an empty value to the next line and turn that neighboring text into
        // apparent evidence of a secret.
        foreach (string line in text.Split('\n'))
        {
            if (IsSensitiveLine(line))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsSensitiveLine(string text)
    {
        if (redactor is CookieRedactor && HasOnlyEmptyOrAlreadyRedactedCookieValues(text))
        {
            return false;
        }

        string normalized = AuthorizationBearerHeader.Replace(text, static match =>
        {
            string value = match.Groups["value"].Value.Trim();
            return IsEmptyOrAlreadyRedactedValue(value)
                ? match.Groups["prefix"].Value + "***"
                : match.Value;
        });
        normalized = ApiKeyHeader.Replace(normalized, static match =>
        {
            string value = match.Groups["value"].Value.Trim();
            return IsEmptyOrAlreadyRedactedValue(value)
                ? match.Groups["prefix"].Value + "***"
                : match.Value;
        });
        normalized = ApiKeyQueryValue.Replace(normalized, static match =>
        {
            string value = match.Groups["value"].Value;
            return IsEmptyOrAlreadyRedactedQueryValue(value)
                ? match.Groups["prefix"].Value + "***"
                : match.Value;
        });
        string redacted = redactor.Redact(normalized);
        if (string.Equals(redacted, normalized, StringComparison.Ordinal))
        {
            return false;
        }

        int prefixLength = 0;
        while (prefixLength < normalized.Length &&
               prefixLength < redacted.Length &&
               normalized[prefixLength] == redacted[prefixLength])
        {
            prefixLength++;
        }

        int originalEnd = normalized.Length - 1;
        int redactedEnd = redacted.Length - 1;
        while (originalEnd >= prefixLength &&
               redactedEnd >= prefixLength &&
               normalized[originalEnd] == redacted[redactedEnd])
        {
            originalEnd--;
            redactedEnd--;
        }

        string changedInput = normalized[prefixLength..(originalEnd + 1)].Trim();
        return changedInput.Length > 0 &&
            !AlreadyRedactedValue.IsMatch(changedInput) &&
            !EmptyCredentialAssignment.IsMatch(changedInput);
    }

    private static bool HasOnlyEmptyOrAlreadyRedactedCookieValues(string text)
    {
        Match headerMatch = CookieHeader.Match(text);
        if (!headerMatch.Success)
        {
            return false;
        }

        string[] assignments = headerMatch.Groups["value"].Value.Split(';');
        bool isSetCookie = headerMatch.Groups["name"].Value.Equals("set-cookie", StringComparison.OrdinalIgnoreCase);
        int valueCount = isSetCookie ? 1 : assignments.Length;
        if (assignments.Length == 0 || string.IsNullOrWhiteSpace(assignments[0]))
        {
            return false;
        }

        for (int index = 0; index < valueCount; index++)
        {
            string assignment = assignments[index].Trim();
            int equalsIndex = assignment.IndexOf('=');
            string name = equalsIndex > 0 ? assignment[..equalsIndex].Trim() : string.Empty;
            if (name.Length == 0 || name.Any(char.IsWhiteSpace))
            {
                return false;
            }

            string value = assignment[(equalsIndex + 1)..].Trim();
            if (!IsEmptyOrAlreadyRedactedValue(value))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsEmptyOrAlreadyRedactedValue(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.Length == 0 || AlreadyRedactedValue.IsMatch(trimmed))
        {
            return true;
        }

        return trimmed.Length >= 2 &&
            ((trimmed[0] == '\"' && trimmed[^1] == '\"') ||
             (trimmed[0] == '\'' && trimmed[^1] == '\'')) &&
            trimmed[1..^1].Trim().Length == 0;
    }

    private static bool IsEmptyOrAlreadyRedactedQueryValue(string value)
    {
        try
        {
            // Query values use application/x-www-form-urlencoded semantics: '+' is a space and
            // percent escapes represent the UTF-8 value. Classify the decoded value so encoded
            // whitespace cannot look like a credential merely because its spelling is non-empty.
            return IsEmptyOrAlreadyRedactedValue(System.Net.WebUtility.UrlDecode(value));
        }
        catch (ArgumentException)
        {
            // Keep malformed query spellings on the existing redaction path rather than allowing
            // a malformed escape to abort the scan.
            return IsEmptyOrAlreadyRedactedValue(value);
        }
    }
}

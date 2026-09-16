using System.Text.Json;
using System.Text.Json.Serialization;
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
    internal const string UnprovenTextEncodingSkippedReason = "Content-dependent checks, including sensitive-data detection, did not run for this fixture because its bytes contain NUL characters and declare no byte-order mark, so no text encoding is proven and undeclared encodings are never inferred.";

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
    public bool IsSensitive(string text) =>
        !string.Equals(redactor.Redact(text), text, StringComparison.Ordinal);
}

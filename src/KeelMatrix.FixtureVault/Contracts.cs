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
    internal const string DiagnosticBudgetErrorCode = "FV-E017";
    internal const string DiagnosticBudgetErrorMessage = "The scan exceeded its diagnostic safety limit before a trustworthy report could be produced.";
    internal const int MaximumDiagnosticCount = DiagnosticBudget.MaximumDiagnosticCount;
    internal const long MaximumDiagnosticBytes = DiagnosticBudget.MaximumEstimatedBytes;
    internal const string UninspectableContentSkippedCode = "FV-SKIP-ENCODING";
    internal const string UninspectableContentSkippedReason = "Content-dependent checks, including sensitive-data detection, did not run for this fixture because no encoding suitable for content inspection could be established.";
    internal const string UndeclaredNulContentSkippedReason = "Content-dependent checks, including sensitive-data detection, did not run for this fixture because its decoded content contains a NUL (U+0000) character and no byte-order mark declares a text encoding, so the decoded text cannot be trusted.";
    internal const string UndeclaredNulContentDiagnosticMessage = "The fixture contains NUL characters but declares no byte-order mark, so it is not proven to be UTF-8 text.";
    internal const string UndecodableContentDiagnosticMessage = "The fixture is not valid UTF-8 text.";
    internal const string NonUtf8EncodingDiagnosticMessage = "The fixture uses a non-UTF-8 encoding.";
    internal const string NonUtf8EncodingRemediation = "Save the fixture as valid UTF-8 text without NUL characters.";
    internal const string NulContentRemediation = "Save the fixture as UTF-8 text without NUL characters; a byte-order mark does not make NUL content inspectable.";
    internal const string UndeclaredEncodingRemediation = "Save the fixture as valid UTF-8 text without NUL characters.";

    internal static string DeclaredNulContentSkippedReason(string declaredEncodingName) =>
        $"Content-dependent checks, including sensitive-data detection, did not run for this fixture because its decoded {declaredEncodingName} content contains a NUL (U+0000) character, so the decoded text cannot be trusted.";

    internal static string UndecodableDeclaredContentSkippedReason(string declaredEncodingName) =>
        $"Content-dependent checks, including sensitive-data detection, did not run for this fixture because its bytes declare {declaredEncodingName} with a byte-order mark but are not valid {declaredEncodingName} text.";

    internal static string DeclaredNulContentDiagnosticMessage(string declaredEncodingName) =>
        $"The fixture declares {declaredEncodingName} with a byte-order mark, but its decoded content contains a NUL (U+0000) character, so its content is not trusted for content inspection.";

    internal static string UndecodableDeclaredContentDiagnosticMessage(string declaredEncodingName) =>
        $"The fixture declares {declaredEncodingName} with a byte-order mark, but its bytes are not valid {declaredEncodingName} text.";

    internal static string DeclaredEncodingRemediation(string _) =>
        NonUtf8EncodingRemediation;

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
    private static readonly Regex AuthorizationHeader = new(
        "^\\s*authorization\\s*:\\s*(?:bearer|basic)(?:\\s+(?<value>[^\\r\\n]*))?\\s*$",
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
    private static readonly HashSet<string> RedactionMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        "***",
        "<redacted>",
        "[redacted]",
        "redacted",
        "masked",
        "removed"
    };
    private static readonly string[] AzureCredentialKeys =
    [
        "AccountKey",
        "SharedAccessKey",
        "SharedAccessSignature"
    ];
    private static readonly string[] GenericCredentialKeys =
    [
        "ApiKey",
        "ClientSecret",
        "Password",
        "Pwd",
        "Secret",
        "Token"
    ];

    public bool IsSensitive(string text)
    {
        if (redactor is ConnectionStringPasswordRedactor)
        {
            // A valid JSON container is the representation boundary. Its string values are
            // decoded exactly once by System.Text.Json before connection-string parsing.
            if (TryReadJsonStringValues(text, out List<string> jsonValues))
            {
                return jsonValues.Any(HasNonEmptyConnectionStringCredential);
            }

            // Line breaks are separators only outside quoted values. This preserves quoted
            // connection-string values that contain actual line breaks.
            return HasNonEmptyConnectionStringCredential(text);
        }

        if (redactor is RegexReplaceRedactor &&
            TryClassifyJsonCredentialProperties(text, out bool hasJsonCredential) &&
            hasJsonCredential)
        {
            return true;
        }

        foreach (string representation in ReadRepresentations(text))
        {
            // Generic assignments may themselves be connection-string fields. Classify that
            // representation before line-scoped header processing so a quoted value containing a
            // line break is not split into an unterminated assignment.
            if (redactor is RegexReplaceRedactor &&
                TryClassifyGenericCredentialRepresentation(representation, out bool hasGenericCredential))
            {
                return hasGenericCredential;
            }

            // Header and cookie grammar remains line-scoped. JSON string values are evaluated
            // after the container has been decoded once.
            foreach (string line in representation.Split('\n'))
            {
                if (IsSensitiveLine(line))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private bool IsSensitiveLine(string text)
    {
        if (redactor is AzureKeyLikeRedactor &&
            TryClassifyConnectionAssignments(text, AzureCredentialKeys, out bool hasAzureAssignment))
        {
            return hasAzureAssignment;
        }

        if (redactor is AuthorizationRedactor)
        {
            Match authorization = AuthorizationHeader.Match(text);
            if (authorization.Success)
            {
                return HasNonEmptyHeaderValue(authorization.Groups["value"].Value);
            }
        }

        if (redactor is ApiKeyRedactor)
        {
            Match header = ApiKeyHeader.Match(text);
            if (header.Success)
            {
                return HasNonEmptyHeaderValue(header.Groups["value"].Value);
            }

            MatchCollection queryMatches = ApiKeyQueryValue.Matches(text);
            if (queryMatches.Count > 0)
            {
                return queryMatches.Cast<Match>().Any(match =>
                    !IsEmptyOrAlreadyRedactedQueryValue(match.Groups["value"].Value));
            }
        }

        if (redactor is CookieRedactor)
        {
            Match cookie = CookieHeader.Match(text);
            if (cookie.Success)
            {
                return HasNonEmptyCookieValue(cookie);
            }
        }

        if (redactor is RegexReplaceRedactor &&
            TryClassifyGenericCredentialRepresentation(text, out bool hasGenericCredential))
        {
            return hasGenericCredential;
        }

        // Whole-token redactors retain the original redaction-difference contract. Structured
        // formats above are classified field-by-field before this fallback is reached.
        string normalized = text;
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
            !IsEmptyOrAlreadyRedactedValue(changedInput);
    }

    private static bool HasNonEmptyConnectionStringCredential(string text)
    {
        foreach (ParsedAssignment assignment in ReadAssignments(text, splitOnLineBreaks: true))
        {
            if (IsConnectionStringCredentialKey(assignment.Name) &&
                !IsEmptyOrAlreadyRedactedSemanticValue(assignment.Value))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryClassifyConnectionAssignments(
        string text,
        IReadOnlyList<string> keys,
        out bool hasCredential)
    {
        bool sawParsedAssignment = false;
        foreach (ParsedAssignment assignment in ReadAssignments(text, splitOnLineBreaks: true))
        {
            sawParsedAssignment = true;
            if (!keys.Any(key => assignment.Name.Equals(key, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (!IsEmptyOrAlreadyRedactedSemanticValue(assignment.Value))
            {
                hasCredential = true;
                return true;
            }
        }

        hasCredential = false;
        // Once the assignment grammar has consumed a field, do not fall back to a whole-token
        // redactor that could rediscover a credential-shaped substring inside that field's value.
        return sawParsedAssignment;
    }

    private static bool IsConnectionStringCredentialKey(string name) =>
        name.Equals("Password", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Pwd", StringComparison.OrdinalIgnoreCase);

    private static bool HasNonEmptyHeaderValue(string value) =>
        !IsEmptyOrAlreadyRedactedSemanticValue(ReadOptionalDelimitedValue(value));

    private static bool TryClassifyGenericCredentialRepresentation(string text, out bool hasCredential)
    {
        if (TryClassifyJsonCredentialProperties(text, out hasCredential))
        {
            return hasCredential;
        }

        bool sawParsedAssignment = false;
        foreach (ParsedAssignment assignment in ReadAssignments(text, splitOnLineBreaks: true))
        {
            sawParsedAssignment = true;
            if (!GenericCredentialKeys.Any(key => assignment.Name.Equals(key, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (!IsEmptyOrAlreadyRedactedSemanticValue(assignment.Value))
            {
                hasCredential = true;
                return true;
            }
        }

        hasCredential = false;
        // The field parser owns boundaries. A legacy whole-token fallback must not search inside
        // a value that the parser already consumed as unrelated text.
        return sawParsedAssignment;
    }

    private static bool TryClassifyJsonCredentialProperties(string text, out bool hasCredential)
    {
        hasCredential = false;
        ReadOnlySpan<char> candidate = text.AsSpan().TrimStart();
        if (candidate.IsEmpty || candidate[0] is not ('{' or '[' or '"'))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            bool isJson = ClassifyJsonCredentialProperties(document.RootElement, ref hasCredential);
            return isJson;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool ClassifyJsonCredentialProperties(JsonElement element, ref bool hasCredential)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (GenericCredentialKeys.Any(key => property.Name.Equals(key, StringComparison.OrdinalIgnoreCase)))
                    {
                        if (property.Value.ValueKind == JsonValueKind.String &&
                            !IsEmptyOrAlreadyRedactedSemanticValue(property.Value.GetString() ?? string.Empty))
                        {
                            hasCredential = true;
                        }

                        continue;
                    }

                    ClassifyJsonCredentialProperties(property.Value, ref hasCredential);
                }

                return true;
            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                {
                    ClassifyJsonCredentialProperties(item, ref hasCredential);
                }

                return true;
            case JsonValueKind.String:
                // This is a valid JSON representation. Its decoded value is evaluated by the
                // representation loop, so do not run a second redaction pass on the container.
                return true;
            default:
                return true;
        }
    }

    private static bool HasNonEmptyCookieValue(Match header)
    {
        bool isSetCookie = header.Groups["name"].Value.Equals("set-cookie", StringComparison.OrdinalIgnoreCase);
        foreach (ParsedAssignment assignment in ReadAssignments(header.Groups["value"].Value, splitOnLineBreaks: false))
        {
            if (!IsEmptyOrAlreadyRedactedSemanticValue(assignment.Value))
            {
                return true;
            }

            if (isSetCookie)
            {
                break;
            }
        }

        return false;
    }

    private static IEnumerable<string> ReadRepresentations(string text)
    {
        if (TryReadJsonStringValues(text, out List<string> values))
        {
            foreach (string value in values)
            {
                yield return value;
            }

            yield break;
        }

        yield return text;
    }

    private static bool TryReadJsonStringValues(string text, out List<string> values)
    {
        values = [];
        ReadOnlySpan<char> candidate = text.AsSpan().TrimStart();
        if (candidate.IsEmpty || candidate[0] is not ('{' or '[' or '"'))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            AddJsonStringValues(document.RootElement, values);
            return true;
        }
        catch (JsonException)
        {
            values.Clear();
            return false;
        }
    }

    private static void AddJsonStringValues(JsonElement element, ICollection<string> values)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                values.Add(element.GetString() ?? string.Empty);
                break;
            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                {
                    AddJsonStringValues(item, values);
                }

                break;
            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    AddJsonStringValues(property.Value, values);
                }

                break;
        }
    }

    private static string ReadOptionalDelimitedValue(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.Length < 2 ||
            (trimmed[0] is not ('"' or '\'') || trimmed[^1] != trimmed[0]))
        {
            return trimmed;
        }

        return trimmed[1..^1];
    }

    private static IEnumerable<ParsedAssignment> ReadAssignments(string text, bool splitOnLineBreaks)
    {
        int index = 0;
        while (index < text.Length)
        {
            while (index < text.Length && IsAssignmentSeparator(text[index], splitOnLineBreaks))
            {
                index++;
            }

            while (index < text.Length && char.IsWhiteSpace(text[index]) &&
                   !IsAssignmentSeparator(text[index], splitOnLineBreaks))
            {
                index++;
            }

            if (index >= text.Length)
            {
                yield break;
            }

            int nameStart = index;
            while (index < text.Length && text[index] != '=' &&
                   !IsAssignmentSeparator(text[index], splitOnLineBreaks))
            {
                index++;
            }

            if (index >= text.Length || text[index] != '=')
            {
                SkipToAssignmentSeparator(text, ref index, splitOnLineBreaks);
                continue;
            }

            string name = text[nameStart..index].Trim();
            index++;
            while (index < text.Length && char.IsWhiteSpace(text[index]) &&
                   !IsAssignmentSeparator(text[index], splitOnLineBreaks))
            {
                index++;
            }

            string value;
            if (index < text.Length && text[index] is '"' or '\'')
            {
                value = ReadQuotedValue(text, ref index, text[index]);
                SkipToAssignmentSeparator(text, ref index, splitOnLineBreaks);
            }
            else
            {
                int valueStart = index;
                while (index < text.Length && !IsAssignmentSeparator(text[index], splitOnLineBreaks))
                {
                    index++;
                }

                value = text[valueStart..index];
            }

            if (name.Length > 0)
            {
                yield return new ParsedAssignment(name, value);
            }
        }
    }

    private static bool IsAssignmentSeparator(char value, bool splitOnLineBreaks) =>
        value == ';' || (splitOnLineBreaks && value is '\r' or '\n');

    private static void SkipToAssignmentSeparator(string text, ref int index, bool splitOnLineBreaks)
    {
        while (index < text.Length && !IsAssignmentSeparator(text[index], splitOnLineBreaks))
        {
            index++;
        }

        if (index < text.Length)
        {
            index++;
        }
    }

    private static string ReadQuotedValue(string text, ref int index, char quote)
    {
        int openingIndex = index++;
        var value = new System.Text.StringBuilder();
        while (index < text.Length)
        {
            if (text[index] == quote)
            {
                if (index + 1 < text.Length && text[index + 1] == quote)
                {
                    value.Append(quote);
                    index += 2;
                    continue;
                }

                index++;
                return value.ToString();
            }

            value.Append(text[index]);
            index++;
        }

        // Preserve an unterminated delimiter in malformed input so it cannot become an empty
        // credential through a partial parse.
        return text[openingIndex] + value.ToString();
    }

    private static bool IsEmptyOrAlreadyRedactedValue(string value) =>
        IsEmptyOrAlreadyRedactedSemanticValue(ReadOptionalDelimitedValue(value));

    private static bool IsEmptyOrAlreadyRedactedSemanticValue(string value) =>
        string.IsNullOrWhiteSpace(value) || IsAcceptedRedactionMarker(value);

    private static bool IsAcceptedRedactionMarker(string value)
    {
        string trimmed = value.Trim();
        if (RedactionMarkers.Contains(trimmed))
        {
            return true;
        }

        return trimmed.Length >= 2 &&
            trimmed[0] is '"' or '\'' &&
            trimmed[^1] == trimmed[0] &&
            RedactionMarkers.Contains(trimmed[1..^1].Trim());
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

    private sealed record ParsedAssignment(string Name, string Value);
}

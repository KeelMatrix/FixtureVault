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

internal static class GenericCredentialKeyGrammar
{
    private const string AssignmentOperatorPattern = "[:=]";

    private static readonly Dictionary<string, string> Aliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ApiKey"] = "ApiKey",
            ["api_key"] = "ApiKey",
            ["api-key"] = "ApiKey",
            ["ClientSecret"] = "ClientSecret",
            ["client_secret"] = "ClientSecret",
            ["client-secret"] = "ClientSecret",
            ["Password"] = "Password",
            ["Pwd"] = "Pwd",
            ["Secret"] = "Secret",
            ["Token"] = "Token"
        };

    internal static string KeyPattern { get; } = string.Join(
        "|",
        Aliases.Keys
            .OrderByDescending(alias => alias.Length)
            .ThenBy(alias => alias, StringComparer.Ordinal)
            .Select(Regex.Escape));

    private static string AssignmentKeyPattern { get; } =
        $@"(?<openingQuote>[""'])?\b(?<key>{KeyPattern})\b(?<closingQuote>[""'])?";

    internal static string FallbackAssignmentPattern { get; } =
        $@"(?i){AssignmentKeyPattern}\s*{AssignmentOperatorPattern}\s*[""']?[A-Za-z0-9_./+=-]{{16,}}";

    private static readonly Regex AssignmentPrefix = new(
        $@"{AssignmentKeyPattern}\s*{AssignmentOperatorPattern}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly Regex AssignmentPrefixAt = new(
        $@"\G{AssignmentKeyPattern}\s*{AssignmentOperatorPattern}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex FallbackAssignment = new(
        FallbackAssignmentPattern,
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    internal static bool TryNormalizeDecodedKey(string key, out string normalizedKey) =>
        TryNormalize(key, allowQuotedSyntax: false, out normalizedKey);

    internal static bool TryNormalizeAssignmentKey(string key, out string normalizedKey) =>
        TryNormalize(key, allowQuotedSyntax: true, out normalizedKey);

    internal static bool TryFindAssignment(
        string text,
        int startAt,
        out AssignmentPrefixMatch assignment)
    {
        for (Match match = AssignmentPrefix.Match(text, startAt);
             match.Success;
             match = match.NextMatch())
        {
            if (IsAssignmentStart(text, match.Index) &&
                HasValidKeyQuoteSyntax(match) &&
                TryNormalizeDecodedKey(match.Groups["key"].Value, out _))
            {
                assignment = new AssignmentPrefixMatch(match.Index, match.Index + match.Length);
                return true;
            }
        }

        assignment = default;
        return false;
    }

    internal static bool TryReadAssignmentAt(
        string text,
        int startAt,
        out AssignmentPrefixMatch assignment)
    {
        Match match = AssignmentPrefixAt.Match(text, startAt);
        if (match.Success &&
            match.Index == startAt &&
            IsAssignmentStart(text, match.Index) &&
            HasValidKeyQuoteSyntax(match) &&
            TryNormalizeDecodedKey(match.Groups["key"].Value, out _))
        {
            assignment = new AssignmentPrefixMatch(match.Index, match.Index + match.Length);
            return true;
        }

        assignment = default;
        return false;
    }

    internal static bool HasFallbackAssignment(string text)
    {
        for (Match match = FallbackAssignment.Match(text);
             match.Success;
             match = match.NextMatch())
        {
            if (IsAssignmentStart(text, match.Index) && HasValidKeyQuoteSyntax(match))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsAssignmentStart(string text, int index) =>
        index == 0 || text[index - 1] is not ('=' or ':' or '"' or '\'');

    private static bool HasValidKeyQuoteSyntax(Match match)
    {
        Group openingQuote = match.Groups["openingQuote"];
        Group closingQuote = match.Groups["closingQuote"];
        return openingQuote.Success == closingQuote.Success &&
            (!openingQuote.Success || openingQuote.Value == closingQuote.Value);
    }

    private static bool TryNormalize(string key, bool allowQuotedSyntax, out string normalizedKey)
    {
        string candidate = key.Trim();
        if (allowQuotedSyntax &&
            candidate.Length >= 2 &&
            candidate[0] is '"' or '\'' &&
            candidate[^1] == candidate[0])
        {
            candidate = candidate[1..^1];
        }

        if (Aliases.TryGetValue(candidate, out string? canonicalKey))
        {
            normalizedKey = canonicalKey;
            return true;
        }

        normalizedKey = string.Empty;
        return false;
    }

    internal readonly record struct AssignmentPrefixMatch(int Start, int ValueStart);
}

internal sealed class RedactionSensitiveDataDetector(ITextRedactor redactor) : ISensitiveDataDetector
{
    private static readonly Regex AuthorizationHeader = new(
        "^\\s*authorization\\s*:\\s*(?:bearer|basic)(?:\\s+(?<value>\"[^\"\\r\\n]*\"|'[^'\\r\\n]*'|\\[[^\\]\\r\\n]*\\]|[^;\\s,\\]\\r\\n]+))?\\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex EmbeddedAuthorizationHeader = new(
        "\\bauthorization\\s*:\\s*(?:bearer|basic)(?:\\s+(?<value>\"[^\"\\r\\n]*\"|'[^'\\r\\n]*'|\\[[^\\]\\r\\n]*\\]|[^;\\s,\\]\\r\\n]+))?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex ApiKeyHeader = new(
        "^\\s*(?<prefix>(?:x-?api-?key|apikey)\\s*:\\s*)(?<value>[^\\r\\n]*)\\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex ApiKeyQueryValue = new(
        "(?<prefix>[?&]\\s*(?:x-)?api[-_]?key\\s*=\\s*)(?<value>[^&#\\r\\n]*)",
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
    private static readonly HashSet<string> ConnectionStringIndicatorNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Application Name",
        "Data Source",
        "Database",
        "Initial Catalog",
        "Integrated Security",
        "Server",
        "User ID",
        "UID"
    };
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

        if (redactor is RegexReplaceRedactor)
        {
            return HasSensitiveGenericCredentialText(text);
        }

        foreach (string representation in ReadRepresentations(text))
        {
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
            TryClassifyAzureAssignments(text, out bool hasAzureAssignment))
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

            MatchCollection embeddedAuthorizations = EmbeddedAuthorizationHeader.Matches(text);
            if (embeddedAuthorizations.Count > 0)
            {
                return embeddedAuthorizations.Cast<Match>().Any(match =>
                    HasNonEmptyHeaderValue(match.Groups["value"].Value));
            }

            // The authorization redactor also supports embedded headers, but its whole-line
            // replacement diff cannot distinguish a redacted marker from a real value.
            return false;
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

        // Whole-token redactors retain the original redaction-difference contract. Structured
        // formats above are classified field-by-field before this fallback is reached.
        return HasSensitiveRedactionDifference(text);
    }

    private bool HasSensitiveRedactionDifference(string text)
    {
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

    private static bool TryClassifyAzureAssignments(string text, out bool hasCredential)
    {
        bool sawParsedAssignment = false;
        foreach (ParsedAssignment assignment in ReadAzureAssignments(text))
        {
            sawParsedAssignment = true;
            if (!TryNormalizeAzureCredentialKey(assignment.Name, out _))
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

    private static bool TryNormalizeAzureCredentialKey(string name, out string normalizedKey)
    {
        string candidate = name.Trim();
        foreach (string key in AzureCredentialKeys)
        {
            if (!candidate.EndsWith(key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int keyStart = candidate.Length - key.Length;
            if (keyStart > 0 && IsAzureKeyCharacter(candidate[keyStart - 1]))
            {
                continue;
            }

            normalizedKey = key;
            return true;
        }

        normalizedKey = string.Empty;
        return false;
    }

    private static bool IsAzureKeyCharacter(char value) =>
        char.IsLetterOrDigit(value) || value is '_' or '-';

    private static bool IsConnectionStringCredentialKey(string name) =>
        name.Equals("Password", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Pwd", StringComparison.OrdinalIgnoreCase);

    private static bool HasNonEmptyHeaderValue(string value) =>
        !IsEmptyOrAlreadyRedactedSemanticValue(ReadOptionalDelimitedValue(value));

    private static bool HasSensitiveGenericCredentialText(string text)
    {
        if (TryClassifyJsonCredentialDocument(text, out bool hasJsonCredential))
        {
            return hasJsonCredential;
        }

        return HasSensitiveGenericCredentialRepresentation(text);
    }

    private static bool HasSensitiveGenericCredentialRepresentation(string text)
    {
        string classifiedText = MaskConsumedConnectionStringValues(text);
        if (TryClassifyGenericCredentialRepresentation(classifiedText, out List<ClassifiedSpan> classifiedSpans))
        {
            return true;
        }

        int unclassifiedStart = 0;
        foreach (ClassifiedSpan span in classifiedSpans)
        {
            if (span.Start > unclassifiedStart &&
                GenericCredentialKeyGrammar.HasFallbackAssignment(classifiedText[unclassifiedStart..span.Start]))
            {
                return true;
            }

            unclassifiedStart = Math.Max(unclassifiedStart, span.End);
        }

        return unclassifiedStart < classifiedText.Length &&
            GenericCredentialKeyGrammar.HasFallbackAssignment(classifiedText[unclassifiedStart..]);
    }

    private static string MaskConsumedConnectionStringValues(string text)
    {
        List<ParsedAssignmentSpan> assignments = [.. ReadAssignmentSpans(text, splitOnLineBreaks: true)];
        if (!assignments.Any(assignment => ConnectionStringIndicatorNames.Contains(assignment.Name)))
        {
            return text;
        }

        char[] masked = text.ToCharArray();
        foreach (ParsedAssignmentSpan assignment in assignments)
        {
            if (GenericCredentialKeyGrammar.TryNormalizeAssignmentKey(assignment.Name, out _))
            {
                continue;
            }

            int length = assignment.End - assignment.ValueStart;
            if (length > 0)
            {
                Array.Fill(masked, ' ', assignment.ValueStart, length);
            }
        }

        return new string(masked);
    }

    private static bool TryClassifyGenericCredentialRepresentation(
        string text,
        out List<ClassifiedSpan> classifiedSpans)
    {
        classifiedSpans = [];
        int searchIndex = 0;
        while (GenericCredentialKeyGrammar.TryFindAssignment(
                   text,
                   searchIndex,
                   out GenericCredentialKeyGrammar.AssignmentPrefixMatch prefix))
        {
            int valueStart = prefix.ValueStart;
            while (valueStart < text.Length && char.IsWhiteSpace(text[valueStart]))
            {
                valueStart++;
            }

            string value;
            int assignmentEnd;
            if (valueStart > prefix.ValueStart &&
                valueStart < text.Length &&
                LooksLikeSiblingAssignment(text, valueStart))
            {
                value = string.Empty;
                assignmentEnd = prefix.ValueStart;
            }
            else if (IsQueryAssignment(text, prefix.Start))
            {
                assignmentEnd = valueStart;
                value = ReadQueryValue(text, ref assignmentEnd);
            }
            else if (valueStart < text.Length && text[valueStart] is '"' or '\'')
            {
                assignmentEnd = valueStart;
                value = ReadQuotedValue(text, ref assignmentEnd, text[valueStart]);
            }
            else
            {
                assignmentEnd = valueStart;
                while (assignmentEnd < text.Length &&
                       !IsGenericAssignmentBoundary(text[assignmentEnd]))
                {
                    assignmentEnd++;
                }

                value = text[valueStart..assignmentEnd];
            }

            classifiedSpans.Add(new ClassifiedSpan(prefix.Start, assignmentEnd - prefix.Start));
            bool isEmptyOrRedacted = IsQueryAssignment(text, prefix.Start)
                ? IsEmptyOrAlreadyRedactedQueryValue(value)
                : IsEmptyOrAlreadyRedactedSemanticValue(value);
            if (!isEmptyOrRedacted)
            {
                return true;
            }

            searchIndex = Math.Max(prefix.ValueStart, assignmentEnd);
        }

        return false;
    }

    private static bool LooksLikeSiblingAssignment(
        string text,
        int index,
        bool allowColonOperator = true)
    {
        if ((uint)index >= (uint)text.Length)
        {
            return false;
        }

        if (allowColonOperator &&
            GenericCredentialKeyGrammar.TryReadAssignmentAt(text, index, out _))
        {
            return true;
        }

        int cursor = index;
        if (text[cursor] is '"' or '\'')
        {
            char quote = text[cursor++];
            int nameStart = cursor;
            while (cursor < text.Length && text[cursor] != quote)
            {
                cursor++;
            }

            if (cursor == nameStart || cursor >= text.Length)
            {
                return false;
            }

            cursor++;
        }
        else
        {
            int nameStart = cursor;
            while (cursor < text.Length &&
                   (char.IsLetterOrDigit(text[cursor]) || text[cursor] is '_' or '-' or '.'))
            {
                cursor++;
            }

            if (cursor == nameStart)
            {
                return false;
            }
        }

        while (cursor < text.Length && char.IsWhiteSpace(text[cursor]))
        {
            cursor++;
        }

        if (cursor >= text.Length || text[cursor] is not ('=' or ':'))
        {
            return false;
        }

        return text[cursor] == '=' ||
            (allowColonOperator &&
             (cursor + 1 >= text.Length || char.IsWhiteSpace(text[cursor + 1])));
    }

    private static bool IsGenericAssignmentBoundary(char value) =>
        value is ';' or ',' or '&' or '#' or '\r' or '\n' || char.IsWhiteSpace(value);

    private static bool IsQueryAssignment(string text, int assignmentStart)
    {
        int cursor = assignmentStart - 1;
        while (cursor >= 0 && char.IsWhiteSpace(text[cursor]))
        {
            cursor--;
        }

        return cursor >= 0 && text[cursor] is '?' or '&';
    }

    private static bool TryClassifyJsonCredentialDocument(string text, out bool hasCredential)
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
            hasCredential = ClassifyJsonCredentialDocument(document.RootElement);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool ClassifyJsonCredentialDocument(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (GenericCredentialKeyGrammar.TryNormalizeDecodedKey(property.Name, out _) &&
                        TryClassifyJsonCredentialScalarValue(property.Value, out bool hasCredential))
                    {
                        if (hasCredential)
                        {
                            return true;
                        }

                        continue;
                    }

                    if (ClassifyJsonCredentialDocument(property.Value))
                    {
                        return true;
                    }
                }

                return false;
            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                {
                    if (ClassifyJsonCredentialDocument(item))
                    {
                        return true;
                    }
                }

                return false;
            case JsonValueKind.String:
                return HasSensitiveGenericCredentialRepresentation(element.GetString() ?? string.Empty);
            default:
                return false;
        }
    }

    private static bool TryClassifyJsonCredentialScalarValue(
        JsonElement value,
        out bool hasCredential)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                hasCredential = !IsEmptyOrAlreadyRedactedSemanticValue(value.GetString() ?? string.Empty);
                return true;
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                hasCredential = true;
                return true;
            case JsonValueKind.Null:
                hasCredential = false;
                return true;
            default:
                hasCredential = false;
                return false;
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

    private static IEnumerable<ParsedAssignmentSpan> ReadAssignmentSpans(
        string text,
        bool splitOnLineBreaks,
        bool allowColonOperator = false)
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
            while (index < text.Length && !IsAssignmentOperator(text[index], allowColonOperator) &&
                   !IsAssignmentSeparator(text[index], splitOnLineBreaks))
            {
                index++;
            }

            if (index >= text.Length || !IsAssignmentOperator(text[index], allowColonOperator))
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

            int valueStart = index;
            string value;
            if (index < text.Length && text[index] is '"' or '\'')
            {
                value = ReadQuotedValue(text, ref index, text[index]);
                SkipToAssignmentSeparator(text, ref index, splitOnLineBreaks);
            }
            else
            {
                while (index < text.Length && !IsAssignmentSeparator(text[index], splitOnLineBreaks))
                {
                    index++;
                }

                value = text[valueStart..index];
            }

            if (name.Length > 0)
            {
                yield return new ParsedAssignmentSpan(name, value, valueStart, index);
            }
        }
    }

    private static IEnumerable<ParsedAssignment> ReadAssignments(
        string text,
        bool splitOnLineBreaks,
        bool allowColonOperator = false) =>
        ReadAssignmentSpans(text, splitOnLineBreaks, allowColonOperator)
            .Select(assignment => new ParsedAssignment(assignment.Name, assignment.Value));

    private static IEnumerable<ParsedAssignment> ReadAzureAssignments(string text)
    {
        int index = 0;
        while (index < text.Length)
        {
            while (index < text.Length && IsAzureAssignmentBoundary(text[index]))
            {
                index++;
            }

            if (index >= text.Length)
            {
                yield break;
            }

            int nameStart = index;
            while (index < text.Length && text[index] != '=' && !IsAzureAssignmentBoundary(text[index]))
            {
                index++;
            }

            int nameEnd = index;
            while (index < text.Length && char.IsWhiteSpace(text[index]))
            {
                index++;
            }

            if (index >= text.Length || text[index] != '=')
            {
                continue;
            }

            string name = text[nameStart..nameEnd].Trim();
            index++;
            int whitespaceStart = index;
            while (index < text.Length && char.IsWhiteSpace(text[index]))
            {
                index++;
            }

            string value;
            if (index > whitespaceStart &&
                LooksLikeSiblingAssignment(text, index))
            {
                value = string.Empty;
            }
            else if (index < text.Length && text[index] is '"' or '\'')
            {
                value = ReadQuotedValue(text, ref index, text[index]);
            }
            else
            {
                int valueStart = index;
                while (index < text.Length && !IsAzureAssignmentBoundary(text[index]))
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

    private static bool IsAssignmentOperator(char value, bool allowColonOperator) =>
        value == '=' || (allowColonOperator && value == ':');

    private static string ReadQueryValue(string text, ref int index)
    {
        int valueStart = index;
        while (index < text.Length && text[index] is not '&' and not '#' and not '\r' and not '\n')
        {
            index++;
        }

        return text[valueStart..index];
    }

    private static bool IsAzureAssignmentBoundary(char value) =>
        value is ';' or ',' or '\r' or '\n' || char.IsWhiteSpace(value);

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

    private sealed record ParsedAssignmentSpan(string Name, string Value, int ValueStart, int End);

    private readonly record struct ClassifiedSpan(int Start, int Length)
    {
        internal int End => Start + Length;
    }
}

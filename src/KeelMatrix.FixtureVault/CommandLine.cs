namespace KeelMatrix.FixtureVault;

internal sealed record CommandLineOptions(
    string Command,
    IReadOnlyList<string> RootOverrides,
    OutputFormat Format,
    bool StrictOverride,
    bool ShowHelp);

internal enum OutputFormat
{
    Console,
    Json
}

internal sealed record CommandLineParseResult(CommandLineOptions? Options, string? Error);

internal static class CommandLineParser
{
    internal static CommandLineParseResult Parse(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            return new CommandLineParseResult(
                new CommandLineOptions("scan", [], OutputFormat.Console, false, true),
                null);
        }

        string command = args[0].ToLowerInvariant();
        if (command is not "scan" and not "init")
        {
            return new CommandLineParseResult(null, "The command must be 'init' or 'scan'.");
        }

        var roots = new List<string>();
        OutputFormat format = OutputFormat.Console;
        bool strict = false;
        bool showHelp = false;

        for (int index = 1; index < args.Length; index++)
        {
            string argument = args[index];
            if (argument is "--help" or "-h")
            {
                showHelp = true;
                continue;
            }

            if (argument == "--strict")
            {
                if (command != "scan")
                {
                    return new CommandLineParseResult(null, "The --strict option is only supported by 'scan'.");
                }

                strict = true;
                continue;
            }

            if (argument == "--root")
            {
                if (index + 1 >= args.Length ||
                    args[index + 1].StartsWith('-') ||
                    string.IsNullOrWhiteSpace(args[index + 1]))
                {
                    return new CommandLineParseResult(null, "The --root option requires a path.");
                }

                index++;
                roots.Add(args[index]);
                continue;
            }

            if (argument.StartsWith("--root=", StringComparison.Ordinal))
            {
                string value = argument[7..];
                if (string.IsNullOrWhiteSpace(value))
                {
                    return new CommandLineParseResult(null, "The --root option requires a path.");
                }

                roots.Add(value);
                continue;
            }

            if (argument == "--format")
            {
                if (index + 1 >= args.Length ||
                    args[index + 1].StartsWith('-') ||
                    !TryParseFormat(args[index + 1], out format))
                {
                    return new CommandLineParseResult(null, "The --format option must be 'console' or 'json'.");
                }

                index++;
                continue;
            }

            if (argument.StartsWith("--format=", StringComparison.Ordinal))
            {
                if (!TryParseFormat(argument[9..], out format))
                {
                    return new CommandLineParseResult(null, "The --format option must be 'console' or 'json'.");
                }

                continue;
            }

            return new CommandLineParseResult(null, $"Unknown option at argument {index}.");
        }

        if (command == "init" && (roots.Count > 0 || format != OutputFormat.Console || strict))
        {
            return new CommandLineParseResult(null, "'init' does not accept scan options.");
        }

        return new CommandLineParseResult(
            new CommandLineOptions(command, roots, format, strict, showHelp),
            null);
    }

    internal static string Usage()
    {
        return """
            FixtureVault audits snapshot and golden files without changing them.

            Usage:
              fixturevault init
              fixturevault scan [--root <path>]... [--format console|json] [--strict]

            Exit codes:
              0  Scan completed without policy-blocking findings.
              1  Scan completed with policy-blocking findings.
              2  Configuration, input, or execution error prevented a trustworthy scan.

            Safety:
              Findings and skipped diagnostics share a 4,096-record / 1 MiB report-field budget.
              Exhaustion returns FV-E017, an incomplete scan, and exit code 2.
              FV007 classifies structured credential values independently for connection-string
              Password/Pwd, AccountKey/SharedAccessKey/SharedAccessSignature, API-key headers
              and queries, Basic/Bearer authorization, Cookie/Set-Cookie, and generic assignments.
              Generic assignment keys are case-insensitive: ApiKey/api_key/api-key,
              ClientSecret/client_secret/client-secret, Password, Pwd, Secret, and Token. Raw
              assignment keys must be unquoted or use matching single/double quotes; unmatched or
              mismatched quotes are not assignment syntax. Both '=' and ':' are supported.
              Raw connection-string Password/Pwd values preserve backslash spellings literally.
              Raw text preserves literal backslashes. Structurally valid JSON strings decode once.
              JSON credential properties classify string, number, true, and false scalars.
              Null and empty/whitespace strings are clean; object/array values are containers only.
              Query values URL-decode exactly once before their field grammar is parsed.
              Parsed generic fields own only their exact key/operator/value spans;
              unconsumed prefix and unknown-key text remains independently inspected. Clean fields never suppress later fields or JSON siblings.
              Azure-style assignments also separate siblings at semicolons, commas, and whitespace.
              After an empty Azure value, the shared sibling grammar recognizes '=' and ':' forms.
              Arbitrary-name ':' requires following whitespace or end-of-input, so URI-like values stay intact.
              Only Azure credential keys are classified. Empty, whitespace-only, and finite accepted redaction markers are clean;
              JSON escape spellings such as \u0022 and doubled-quote runs are parsed only in their
              container context; quote and backslash characters remaining after parsing are data.
            """;
    }

    private static bool TryParseFormat(string value, out OutputFormat format)
    {
        if (value.Equals("console", StringComparison.OrdinalIgnoreCase))
        {
            format = OutputFormat.Console;
            return true;
        }

        if (value.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            format = OutputFormat.Json;
            return true;
        }

        format = default;
        return false;
    }
}

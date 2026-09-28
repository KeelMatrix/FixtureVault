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

        if (roots.Count > FixtureVaultContract.MaximumConfiguredRoots)
        {
            return new CommandLineParseResult(
                null,
                $"{FixtureVaultContract.RootCountErrorCode}: {FixtureVaultContract.RootCountErrorMessage}");
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

            Root safety:
              Policy roots and --root overrides are limited to 64 entries. Exceeding the limit
              returns FV-E018, exit code 2, and no successful-scan telemetry.
              Filesystem traversal has one scan-wide budget of 100,000 entries and 1,000,000
              logical path-work units shared by all active roots and repository-wide path-policy
              discovery. Every directory entry and every path component/ancestor validation uses
              the same charge on every supported operating system.
              Exhaustion returns exit code 2; active-root exhaustion reports FV-E003 and
              repository-wide exhaustion reports FV-E015.

            Classification:
              Physical identity may deduplicate traversal roots, but classification is complete
              for every distinct eligible repository-relative path alias, including hard links.
              Manifest activeBaselines entries use exact repository-relative spelling; portability
              normalization is used only to group FV003 collisions.

            Exit codes:
              0  Scan completed without policy-blocking findings.
              1  Scan completed with policy-blocking findings.
              2  Configuration, input, or execution error prevented a trustworthy scan.
              JSON reports include filesDiscovered, filesInspected, and completed;
              an incomplete scan never counts unchecked files as inspected.

            Safety:
              Findings and skipped diagnostics share a 4,096-record / 1 MiB report-field budget.
              Exhaustion returns FV-E017, an incomplete scan, and exit code 2.
              JSON representations are inspected through a maximum depth of 64 containers.
              A valid deeper representation returns FV-E014, an incomplete scan, and exit code 2;
              malformed JSON remains eligible for raw-text inspection.
              FV007 detects high-confidence structured credentials without disclosing matched
              values. Connection-string masking is limited to syntactically owned value spans,
              so unrelated clean context cannot suppress a finding while quoted non-secret values
              remain owned. The complete ownership and boundary grammar is maintained at:
              https://github.com/KeelMatrix/FixtureVault/blob/main/docs/DETECTION_GRAMMAR.md
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

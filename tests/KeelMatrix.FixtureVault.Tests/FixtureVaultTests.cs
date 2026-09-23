using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;
using Xunit.Sdk;

namespace KeelMatrix.FixtureVault.Tests;

public sealed class FixtureVaultTests
{
    private const string SensitiveValue = "fixture-test-secret-1234567890";

    [Fact]
    public void Init_creates_only_the_versioned_policy_file()
    {
        using var repository = new TemporaryRepository();
        repository.WriteText("tests/keep.golden", "{\"ok\":true}\n");
        var before = repository.HashTree();
        var telemetry = new RecordingTelemetry();

        int exitCode = repository.Run(["init"], telemetry, out string output, out string error);

        Assert.Equal(0, exitCode);
        Assert.Contains(".fixturevault.json", output);
        Assert.Empty(error);
        Assert.Equal(before, repository.HashTree());
        Assert.True(File.Exists(Path.Combine(repository.Root, FixtureVaultContract.PolicyFileName)));
        Assert.Equal(0, telemetry.SuccessfulScans);
    }

    [Fact]
    public void Init_does_not_overwrite_an_existing_policy()
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy(policy => policy.MaxFileBytes = 99);
        string path = Path.Combine(repository.Root, FixtureVaultContract.PolicyFileName);
        string before = File.ReadAllText(path);

        int exitCode = repository.Run(["init"], new RecordingTelemetry(), out string output, out string error);

        Assert.Equal(0, exitCode);
        Assert.Contains("no files were changed", output, StringComparison.Ordinal);
        Assert.Empty(error);
        Assert.Equal(before, File.ReadAllText(path));
    }

    [Fact]
    public void Init_without_tests_directory_creates_a_usable_repository_root_policy()
    {
        using var repository = new TemporaryRepository(createTestsDirectory: false);

        int initExitCode = repository.Run(["init"], new RecordingTelemetry(), out string initOutput, out string initError);
        int scanExitCode = repository.Run(["scan"], new RecordingTelemetry(), out string scanOutput, out string scanError);

        Assert.Equal(0, initExitCode);
        Assert.Empty(initError);
        Assert.Contains(FixtureVaultContract.PolicyFileName, initOutput, StringComparison.Ordinal);
        Assert.Equal(0, scanExitCode);
        Assert.Empty(scanError);
        Assert.Contains("0 fixture file(s) inspected", scanOutput, StringComparison.Ordinal);

        PolicyLoadResult policy = PolicyLoader.Load(repository.Root);
        Assert.Null(policy.Error);
        Assert.Equal(".", Assert.Single(policy.Policy!.Roots!));
    }

    [Fact]
    public void Init_without_tests_directory_ignores_ordinary_binary_assets_at_repository_root()
    {
        using var repository = new TemporaryRepository(createTestsDirectory: false);
        repository.WriteBytes("icon.png", [0x89, 0x50, 0x4E, 0x47, 0x00, 0x01]);
        repository.WriteBytes("docs/manual.pdf", [0x25, 0x50, 0x44, 0x46, 0x00, 0x01]);
        repository.WriteBytes("unrelated.zip", [0x50, 0x4B, 0x03, 0x04, 0x00, 0x01]);

        Assert.Equal(0, repository.Run(["init"], new RecordingTelemetry(), out _, out _));
        int scanExitCode = repository.Run(["scan"], new RecordingTelemetry(), out string output, out string error);

        Assert.Equal(0, scanExitCode);
        Assert.Empty(error);
        Assert.Contains("0 fixture file(s) inspected", output, StringComparison.Ordinal);
        Assert.DoesNotContain("FV005", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Repository_policy_discovery_stops_at_the_nearest_git_root_with_an_in_repo_policy(bool gitMarkerIsFile)
    {
        using var repository = new GitBoundaryRepository(gitMarkerIsFile);
        repository.WriteParentPolicy();
        repository.WriteParentText("sibling/leaked.received.json", "{\"apiKey\":\"fixture-test-secret-1234567890\"}\n");
        repository.WritePolicyInRepository();
        repository.WriteRepositoryText("tests/clean.golden", "clean\n");

        int exitCode = repository.RunFromNestedDirectory(["scan"], out string output, out string error);

        Assert.Equal(0, exitCode);
        Assert.Empty(error);
        Assert.Contains("1 fixture file(s) inspected", output, StringComparison.Ordinal);
        Assert.DoesNotContain("sibling", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fixture-test-secret-1234567890", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Repository_policy_discovery_does_not_adopt_a_policy_above_the_nearest_git_root(bool gitMarkerIsFile)
    {
        using var repository = new GitBoundaryRepository(gitMarkerIsFile);
        repository.WriteParentPolicy();
        repository.WriteParentText("sibling/leaked.received.json", "{\"apiKey\":\"fixture-test-secret-1234567890\"}\n");

        int exitCode = repository.RunFromNestedDirectory(["scan"], out string output, out string error);

        Assert.Equal(2, exitCode);
        Assert.Empty(output);
        Assert.Contains("FV-E001", error, StringComparison.Ordinal);
        Assert.Contains(".fixturevault.json was not found", error, StringComparison.Ordinal);
        Assert.DoesNotContain("sibling", error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fixture-test-secret-1234567890", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Help_is_available_without_a_policy_file()
    {
        using var repository = new TemporaryRepository(createTestsDirectory: false);

        int exitCode = repository.Run(["--help"], new RecordingTelemetry(), out string output, out string error);

        Assert.Equal(0, exitCode);
        Assert.Empty(error);
        Assert.Contains("Usage:", output, StringComparison.Ordinal);
        Assert.Contains("fixturevault", output, StringComparison.Ordinal);
        Assert.Contains("doubled-quote runs", output, StringComparison.Ordinal);
        Assert.Contains("\\u0022", output, StringComparison.Ordinal);
        Assert.Contains("4,096-record / 1 MiB report-field budget", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Help_is_written_to_stdout_for_each_supported_command()
    {
        using var repository = new TemporaryRepository(createTestsDirectory: false);

        foreach (string[] args in new[] { new[] { "scan", "--help" }, new[] { "init", "--help" } })
        {
            int exitCode = repository.Run(args, new RecordingTelemetry(), out string output, out string error);

            Assert.Equal(0, exitCode);
            Assert.Contains("Usage:", output, StringComparison.Ordinal);
            Assert.Empty(error);
        }
    }

    [Fact]
    public void Unknown_command_is_an_exit_code_two_stderr_diagnostic()
    {
        using var repository = new TemporaryRepository(createTestsDirectory: false);

        int exitCode = repository.Run(["unknown"], new RecordingTelemetry(), out string output, out string error);

        Assert.Equal(2, exitCode);
        Assert.Empty(output);
        Assert.Contains("init", error, StringComparison.Ordinal);
        Assert.Contains("--help", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_option_is_an_exit_code_two_stderr_diagnostic()
    {
        using var repository = new TemporaryRepository(createTestsDirectory: false);

        int exitCode = repository.Run(["scan", "--unknown"], new RecordingTelemetry(), out string output, out string error);

        Assert.Equal(2, exitCode);
        Assert.Empty(output);
        Assert.Contains("Unknown option at argument 1", error, StringComparison.Ordinal);
        Assert.DoesNotContain("--unknown", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--token=CLI_CANARY_EQUALS", "CLI_CANARY_EQUALS", null, "console")]
    [InlineData("--token=CLI_CANARY_EQUALS_JSON", "CLI_CANARY_EQUALS_JSON", null, "json")]
    [InlineData("--api-key=CLI_CANARY_API_KEY", "CLI_CANARY_API_KEY", null, "console")]
    [InlineData("--password=CLI_CANARY_PASSWORD", "CLI_CANARY_PASSWORD", null, "json")]
    [InlineData("--token:CLI_CANARY_COLON", "CLI_CANARY_COLON", null, "console")]
    [InlineData("--token:CLI_CANARY_COLON_JSON", "CLI_CANARY_COLON_JSON", null, "json")]
    [InlineData("--token=\"CLI_CANARY_DOUBLE_QUOTED\"", "CLI_CANARY_DOUBLE_QUOTED", null, "console")]
    [InlineData("--token='CLI_CANARY_SINGLE_QUOTED'", "CLI_CANARY_SINGLE_QUOTED", null, "json")]
    [InlineData("-tCLI_CANARY_SHORT_ATTACHED", "CLI_CANARY_SHORT_ATTACHED", null, "console")]
    [InlineData("-tCLI_CANARY_SHORT_ATTACHED_JSON", "CLI_CANARY_SHORT_ATTACHED_JSON", null, "json")]
    [InlineData("--password", "CLI_CANARY_SEPARATE", "CLI_CANARY_SEPARATE", "console")]
    [InlineData("--password", "CLI_CANARY_SEPARATE_JSON", "CLI_CANARY_SEPARATE_JSON", "json")]
    [InlineData("-t", "CLI_CANARY_SHORT_SEPARATE", "CLI_CANARY_SHORT_SEPARATE", "console")]
    [InlineData("-t", "CLI_CANARY_SHORT_SEPARATE_JSON", "CLI_CANARY_SHORT_SEPARATE_JSON", "json")]
    public void Sensitive_shaped_cli_arguments_are_not_echoed_in_any_output(
        string option,
        string canary,
        string? separateValue,
        string format)
    {
        using var repository = new TemporaryRepository(createTestsDirectory: false);
        string[] args = separateValue is null
            ? ["scan", "--format", format, option]
            : ["scan", "--format", format, option, separateValue];

        int exitCode = repository.Run(args, new RecordingTelemetry(), out string output, out string error);

        Assert.Equal(2, exitCode);
        AssertNoCanary(canary, output, error);
        Assert.Contains("Unknown option at argument", error, StringComparison.Ordinal);
        Assert.Contains("fixturevault --help", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--format", "CLI_CANARY_INVALID_FORMAT")]
    [InlineData("--format=CLI_CANARY_INVALID_FORMAT_ATTACHED", "CLI_CANARY_INVALID_FORMAT_ATTACHED")]
    public void Invalid_format_values_are_not_echoed_in_any_output(string option, string canary)
    {
        using var repository = new TemporaryRepository(createTestsDirectory: false);
        string[] args = option == "--format"
            ? ["scan", option, canary]
            : ["scan", option];

        int exitCode = repository.Run(args, new RecordingTelemetry(), out string output, out string error);

        Assert.Equal(2, exitCode);
        AssertNoCanary(canary, output, error);
        Assert.Contains("--format option must be 'console' or 'json'", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("console", "--root", "CLI_CANARY_ROOT_SEPARATE")]
    [InlineData("json", "--root=CLI_CANARY_ROOT_ATTACHED", "CLI_CANARY_ROOT_ATTACHED")]
    public void Invalid_root_values_are_not_echoed_in_any_output(string format, string option, string canary)
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy();
        string[] args = option == "--root"
            ? ["scan", option, canary, "--format", format]
            : ["scan", option, "--format", format];

        int exitCode = repository.Run(args, new RecordingTelemetry(), out string output, out string error);

        Assert.Equal(2, exitCode);
        AssertNoCanary(canary, output, error);
        if (format == "json")
        {
            ScanReport report = JsonSerializer.Deserialize<ScanReport>(output, FixtureVaultContract.JsonOptions)!;
            Assert.Equal("FV-E008", Assert.Single(report.Errors).Code);
        }
        else
        {
            Assert.Empty(output);
            Assert.Contains("FV-E008", error, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("console")]
    [InlineData("json")]
    public void Unsupported_sensitive_data_policy_values_are_not_echoed_in_any_output(string format)
    {
        const string canary = "CLI_CANARY_POLICY_SENSITIVE_RULE";
        using var repository = new TemporaryRepository();
        repository.WritePolicy(policy => policy.SensitiveDataRules = [$"unsupported-{canary}"]);

        int exitCode = repository.Run(["scan", "--format", format], new RecordingTelemetry(), out string output, out string error);

        Assert.Equal(2, exitCode);
        AssertNoCanary(canary, output, error);
        if (format == "json")
        {
            ScanReport report = JsonSerializer.Deserialize<ScanReport>(output, FixtureVaultContract.JsonOptions)!;
            Assert.Equal("FV-E005", Assert.Single(report.Errors).Code);
        }
        else
        {
            Assert.Empty(output);
            Assert.Contains("FV-E005", error, StringComparison.Ordinal);
            Assert.Contains("Unsupported sensitiveDataRules entry", error, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("console")]
    [InlineData("json")]
    public void Unsupported_convention_policy_values_are_not_echoed_in_any_output(string format)
    {
        const string canary = "CLI_CANARY_POLICY_CONVENTION";
        using var repository = new TemporaryRepository();
        repository.WritePolicy(policy => policy.Conventions = [$"future-{canary}"]);

        int exitCode = repository.Run(["scan", "--format", format], new RecordingTelemetry(), out string output, out string error);

        Assert.Equal(0, exitCode);
        AssertNoCanary(canary, output, error);
        if (format == "json")
        {
            ScanReport report = JsonSerializer.Deserialize<ScanReport>(output, FixtureVaultContract.JsonOptions)!;
            SkippedDiagnostic skipped = Assert.Single(report.Skipped);
            Assert.Equal("FV-SKIP-CONVENTION", skipped.Code);
            Assert.Equal("unsupported", skipped.Convention);
        }
        else
        {
            Assert.Empty(error);
            Assert.Contains("check(s) skipped conservatively", output, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Root_option_requires_a_non_empty_value(int inputKind)
    {
        using var repository = new TemporaryRepository(createTestsDirectory: false);
        string[] args = inputKind switch
        {
            0 => ["scan", "--root"],
            1 => ["scan", "--root="],
            _ => ["scan", "--root", ""],
        };

        int exitCode = repository.Run(args, new RecordingTelemetry(), out string output, out string error);

        Assert.Equal(2, exitCode);
        Assert.Empty(output);
        Assert.Contains("--root option requires a path", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Root_option_does_not_consume_the_next_option_as_its_value()
    {
        using var repository = new TemporaryRepository(createTestsDirectory: false);

        int exitCode = repository.Run(["scan", "--root", "--format", "json"], new RecordingTelemetry(), out string output, out string error);

        Assert.Equal(2, exitCode);
        Assert.Empty(output);
        Assert.Contains("--root option requires a path", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Invalid_format_values_are_exit_code_two_stderr_diagnostics(int inputKind)
    {
        using var repository = new TemporaryRepository(createTestsDirectory: false);
        string[] args = inputKind == 0
            ? ["scan", "--format", "yaml"]
            : ["scan", "--format=yaml"];

        int exitCode = repository.Run(args, new RecordingTelemetry(), out string output, out string error);

        Assert.Equal(2, exitCode);
        Assert.Empty(output);
        Assert.Contains("--format option must be 'console' or 'json'", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Init_rejects_scan_only_options(int inputKind)
    {
        using var repository = new TemporaryRepository(createTestsDirectory: false);
        string[] args = inputKind switch
        {
            0 => ["init", "--strict"],
            1 => ["init", "--root", "tests"],
            _ => ["init", "--format", "json"],
        };

        int exitCode = repository.Run(args, new RecordingTelemetry(), out string output, out string error);

        Assert.Equal(2, exitCode);
        Assert.Empty(output);
        Assert.Contains("FixtureVault:", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_received_artifact_is_a_blocking_finding()
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy();
        repository.WriteText("tests/Orders/OrderTests.received.json", "{\"id\":1}\n");

        ScanResult result = repository.Scan();

        Assert.Equal(1, result.ExitCode);
        Finding finding = Assert.Single(result.Report.Findings, item => item.RuleId == "FV001");
        Assert.Equal("tests/Orders/OrderTests.received.json", finding.Path);
        Assert.Equal("block", finding.Disposition);
    }

    [Fact]
    public void Verify_bommed_verified_baseline_is_clean()
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy();
        repository.WriteBytes("tests/Orders/OrderTests.verified.json", Utf8Bom("{\"id\":1}"));

        ScanResult result = repository.Scan();

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Report.Findings);
    }

    [Fact]
    public void Verify_split_mode_received_artifact_is_a_blocking_finding()
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy();
        repository.WriteBytes("tests/Orders/OrderTests.received/result.json", Utf8Bom("{\"id\":1}"));

        ScanResult result = repository.Scan();

        Assert.Equal(1, result.ExitCode);
        Finding finding = Assert.Single(result.Report.Findings, item => item.RuleId == "FV001");
        Assert.Equal("tests/Orders/OrderTests.received/result.json", finding.Path);
    }

    [Fact]
    public void Verify_split_mode_verified_baseline_is_clean()
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy();
        repository.WriteBytes("tests/Orders/OrderTests.verified/result.json", Utf8Bom("{\"id\":1}"));

        ScanResult result = repository.Scan();

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Report.Findings);
    }

    [Fact]
    public void Verify_binary_verified_baseline_is_accepted_without_text_analysis()
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy();
        repository.WriteBytes("tests/Orders/OrderTests.verified.png", PngBytes());
        IReadOnlyDictionary<string, string> before = repository.HashTree();

        ScanResult result = repository.Scan();

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Report.Findings);
        Assert.Equal(before, repository.HashTree());
    }

    [Fact]
    public void Accepted_binary_baseline_skips_encoding_and_sensitive_data_analysis()
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy();
        repository.WriteBytes("tests/Orders/OrderTests.verified.png", [0xFF, 0xFE, 0x00, 0x01]);

        ScanResult result = repository.Scan();

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Report.Findings);
    }

    [Fact]
    public void Verify_binary_received_artifact_is_only_an_unapproved_finding()
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy();
        repository.WriteBytes("tests/Orders/OrderTests.received.png", PngBytes());
        IReadOnlyDictionary<string, string> before = repository.HashTree();

        ScanResult result = repository.Scan();

        Assert.Equal(1, result.ExitCode);
        Finding finding = Assert.Single(result.Report.Findings);
        Assert.Equal("FV001", finding.RuleId);
        Assert.Equal("tests/Orders/OrderTests.received.png", finding.Path);
        Assert.Equal(before, repository.HashTree());
    }

    [Fact]
    public void Snapshooter_mismatch_artifacts_are_blocking_findings()
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy();
        repository.WriteText("tests/Orders/__snapshots__/__mismatch__/OrderTests.snap", "{\"id\":2}");

        ScanResult result = repository.Scan();

        Assert.Equal(1, result.ExitCode);
        Finding finding = Assert.Single(result.Report.Findings, item => item.RuleId == "FV001");
        Assert.Equal("tests/Orders/__snapshots__/__mismatch__/OrderTests.snap", finding.Path);
    }

    [Fact]
    public void Snapshooter_plain_mismatch_directory_is_an_audited_baseline_without_received_finding()
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy();
        repository.WriteText("tests/Orders/__snapshots__/mismatch/OrderTests.snap", "{\"id\":2}");

        ScanResult result = repository.Scan();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1, result.Report.FilesInspected);
        Assert.DoesNotContain(result.Report.Findings, item => item.RuleId == "FV001");
    }

    [Fact]
    public void Snapshooter_and_generic_golden_files_are_audited()
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy();
        repository.WriteText("tests/Widget.snap", "snapshot\n");
        repository.WriteText("tests/nested/Widget.golden", "golden\n");

        ScanResult result = repository.Scan();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(2, result.Report.FilesInspected);
        Assert.Contains(result.Report.Skipped, item => item.Code == "FV-SKIP-ORPHAN");
    }

    [Fact]
    public void Generic_files_do_not_claim_orphanhood_without_proof()
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy(policy => policy.Conventions = ["generic"]);
        repository.WriteText("tests/ambiguous.golden", "golden\n");

        ScanResult result = repository.Scan();

        Assert.DoesNotContain(result.Report.Findings, item => item.RuleId == "FV002");
        Assert.Contains(result.Report.Skipped, item => item.Code == "FV-SKIP-ORPHAN");
    }

    [Fact]
    public void Explicit_manifest_proves_an_orphan_baseline()
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy(policy => policy.Conventions = ["generic", "fixturevault-manifest"]);
        repository.WriteText("tests/active.golden", "active\n");
        repository.WriteText("tests/orphan.golden", "orphan\n");
        repository.WriteText(
            FixtureVaultContract.ManifestFileName,
            "{\"version\":1,\"activeBaselines\":[\"tests/active.golden\"]}\n");

        ScanResult result = repository.Scan();

        Assert.Equal(1, result.ExitCode);
        Finding finding = Assert.Single(result.Report.Findings, item => item.RuleId == "FV002");
        Assert.Equal("tests/orphan.golden", finding.Path);
    }

    [Fact]
    public void Enabled_manifest_without_file_fails_closed()
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy(policy => policy.Conventions = ["generic", "fixturevault-manifest"]);
        repository.WriteText("tests/active.golden", "active\n");

        ScanResult result = repository.Scan();

        Assert.Equal(2, result.ExitCode);
        Assert.Contains(result.Report.Errors, item => item.Code == "FV-E012");
    }

    [Fact]
    public void Linked_policy_is_rejected_without_reading_an_outside_target()
    {
        using var repository = new TemporaryRepository();
        string outsideRoot = Path.Combine(Path.GetTempPath(), "fixturevault-policy-link", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsideRoot);
        string outsidePolicy = Path.Combine(outsideRoot, FixtureVaultContract.PolicyFileName);
        File.WriteAllText(
            outsidePolicy,
            FixtureVaultContract.SerializePolicy(FixtureVaultPolicy.CreateDefault(testsDirectoryExists: false)),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        string policyPath = Path.Combine(repository.Root, FixtureVaultContract.PolicyFileName);
        File.Delete(policyPath);

        try
        {
            CreateSymbolicFileOrSkip(policyPath, outsidePolicy);

            int exitCode = repository.Run(["scan", "--format", "json"], new RecordingTelemetry(), out string output, out string error);
            using JsonDocument report = JsonDocument.Parse(output);

            Assert.Equal(2, exitCode);
            Assert.Empty(error);
            Assert.Contains(report.RootElement.GetProperty("errors").EnumerateArray(), item =>
                item.GetProperty("code").GetString() == "FV-E004");
            Assert.DoesNotContain("outside", output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(outsideRoot))
            {
                Directory.Delete(outsideRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void Linked_manifest_is_rejected_without_reading_an_outside_target()
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy(policy => policy.Conventions = ["generic", "fixturevault-manifest"]);
        string outsideRoot = Path.Combine(Path.GetTempPath(), "fixturevault-manifest-link", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsideRoot);
        string outsideManifest = Path.Combine(outsideRoot, FixtureVaultContract.ManifestFileName);
        File.WriteAllText(
            outsideManifest,
            "{\"version\":1,\"activeBaselines\":[]}\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        string manifestPath = Path.Combine(repository.Root, FixtureVaultContract.ManifestFileName);
        File.Delete(manifestPath);

        try
        {
            CreateSymbolicFileOrSkip(manifestPath, outsideManifest);

            ScanResult result = repository.Scan();

            Assert.Equal(2, result.ExitCode);
            ScanError error = Assert.Single(result.Report.Errors);
            Assert.Equal("FV-E011", error.Code);
            Assert.DoesNotContain("outside", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(outsideRoot))
            {
                Directory.Delete(outsideRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void Linked_manifest_failure_preserves_prior_collision_findings_in_console_and_json()
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy(policy => policy.Conventions = ["generic", "fixturevault-manifest"]);
        string composed = "tests/café.golden";
        string decomposed = "tests/cafe\u0301.golden";
        repository.WriteText(composed, "composed\n");
        repository.WriteText(decomposed, "decomposed\n");
        if (!File.Exists(Path.Combine(repository.Root, "tests", "café.golden")) ||
            !File.Exists(Path.Combine(repository.Root, "tests", "cafe\u0301.golden")) ||
            File.ReadAllText(Path.Combine(repository.Root, "tests", "café.golden")) ==
            File.ReadAllText(Path.Combine(repository.Root, "tests", "cafe\u0301.golden")))
        {
            return;
        }

        string outsideRoot = Path.Combine(Path.GetTempPath(), "fixturevault-manifest-parity", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsideRoot);
        string outsideManifest = Path.Combine(outsideRoot, FixtureVaultContract.ManifestFileName);
        File.WriteAllText(
            outsideManifest,
            "{\"version\":1,\"activeBaselines\":[]}\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        string manifestPath = Path.Combine(repository.Root, FixtureVaultContract.ManifestFileName);
        File.Delete(manifestPath);

        try
        {
            CreateSymbolicFileOrSkip(manifestPath, outsideManifest);

            int consoleExit = repository.Run(["scan"], new RecordingTelemetry(), out string consoleOutput, out string consoleError);
            int jsonExit = repository.Run(["scan", "--format", "json"], new RecordingTelemetry(), out string jsonOutput, out string jsonError);
            using JsonDocument jsonReport = JsonDocument.Parse(jsonOutput);

            Assert.Equal(2, consoleExit);
            Assert.Equal(2, jsonExit);
            Assert.Empty(consoleOutput);
            Assert.Empty(jsonError);
            Assert.Contains("FV-E011", consoleError, StringComparison.Ordinal);
            Assert.Contains("FV003", consoleError, StringComparison.Ordinal);
            Assert.Equal(2, jsonReport.RootElement.GetProperty("findings").GetArrayLength());
            Assert.All(
                jsonReport.RootElement.GetProperty("findings").EnumerateArray(),
                finding => Assert.Equal("FV003", finding.GetProperty("ruleId").GetString()));
            Assert.Contains(
                jsonReport.RootElement.GetProperty("errors").EnumerateArray(),
                error => error.GetProperty("code").GetString() == "FV-E011");
        }
        finally
        {
            if (Directory.Exists(outsideRoot))
            {
                Directory.Delete(outsideRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void Replacing_a_discovered_fixture_with_a_link_fails_at_the_read_boundary()
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy();
        string fixturePath = Path.Combine(repository.Root, "tests", "replacement.golden");
        string outsideRoot = Path.Combine(Path.GetTempPath(), "fixturevault-fixture-replacement", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsideRoot);
        string outsidePath = Path.Combine(outsideRoot, "replacement.golden");
        File.WriteAllText(outsidePath, "Password=fixture-test-secret-1234567890;\n");
        repository.WriteText("tests/replacement.golden", "clean\n");
        bool replaced = false;

        try
        {
            FixtureFileWalk replacingWalk = (repositoryRoot, root, failOnAccessErrors, shouldPruneDirectory) =>
            {
                WalkResult result = SafeFileWalker.Walk(repositoryRoot, root, failOnAccessErrors, shouldPruneDirectory);
                if (!replaced && Path.GetFullPath(root).Equals(Path.Combine(repository.Root, "tests"), StringComparison.Ordinal))
                {
                    replaced = true;
                    File.Delete(fixturePath);
                    CreateSymbolicFileOrSkip(fixturePath, outsidePath);
                }

                return result;
            };

            var telemetry = new RecordingTelemetry();
            int exitCode = repository.Run(
                ["scan", "--format", "json"],
                telemetry,
                out string output,
                out string error,
                fileWalk: replacingWalk);
            using JsonDocument report = JsonDocument.Parse(output);

            Assert.Equal(2, exitCode);
            Assert.Empty(error);
            Assert.Equal(0, telemetry.SuccessfulScans);
            Assert.Contains(report.RootElement.GetProperty("errors").EnumerateArray(), item => item.GetProperty("code").GetString() == "FV-E009");
            Assert.DoesNotContain("fixture-test-secret-1234567890", output, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(outsideRoot))
            {
                Directory.Delete(outsideRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void Replacing_a_discovered_parent_directory_with_a_link_fails_at_the_read_boundary()
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy();
        string nestedPath = Path.Combine(repository.Root, "tests", "nested");
        string fixturePath = Path.Combine(nestedPath, "replacement.golden");
        string outsideRoot = Path.Combine(Path.GetTempPath(), "fixturevault-parent-replacement", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsideRoot);
        File.WriteAllText(Path.Combine(outsideRoot, "replacement.golden"), "Password=fixture-test-secret-1234567890;\n");
        repository.WriteText("tests/nested/replacement.golden", "clean\n");
        bool replaced = false;

        try
        {
            FixtureFileWalk replacingWalk = (repositoryRoot, root, failOnAccessErrors, shouldPruneDirectory) =>
            {
                WalkResult result = SafeFileWalker.Walk(repositoryRoot, root, failOnAccessErrors, shouldPruneDirectory);
                if (!replaced && Path.GetFullPath(root).Equals(Path.Combine(repository.Root, "tests"), StringComparison.Ordinal))
                {
                    replaced = true;
                    Directory.Delete(nestedPath, recursive: true);
                    CreateSymbolicDirectoryOrSkip(nestedPath, outsideRoot);
                }

                return result;
            };

            var telemetry = new RecordingTelemetry();
            int exitCode = repository.Run(
                ["scan", "--format", "json"],
                telemetry,
                out string output,
                out string error,
                fileWalk: replacingWalk);
            using JsonDocument report = JsonDocument.Parse(output);

            Assert.Equal(2, exitCode);
            Assert.Empty(error);
            Assert.Equal(0, telemetry.SuccessfulScans);
            Assert.Contains(report.RootElement.GetProperty("errors").EnumerateArray(), item => item.GetProperty("code").GetString() == "FV-E009");
            Assert.DoesNotContain("fixture-test-secret-1234567890", output, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(outsideRoot))
            {
                Directory.Delete(outsideRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void Replacing_a_pending_directory_during_repository_walk_fails_closed_without_disclosing_outside_paths()
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy();
        string pendingPath = Path.Combine(repository.Root, "misc", "pending");
        Directory.CreateDirectory(pendingPath);
        string outsideRoot = Path.Combine(Path.GetTempPath(), "fixturevault-pending-directory", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsideRoot);
        File.WriteAllText(Path.Combine(outsideRoot, "outside.received.json"), "received outside the repository\n");
        bool replaced = false;

        try
        {
            FixtureFileWalk replacingWalk = (repositoryRoot, root, failOnAccessErrors, shouldPruneDirectory) =>
                SafeFileWalker.Walk(
                    repositoryRoot,
                    root,
                    failOnAccessErrors,
                    relativePath =>
                    {
                        if (!replaced &&
                            Path.GetFullPath(root).Equals(repository.Root, StringComparison.Ordinal) &&
                            relativePath.Equals("misc/pending", StringComparison.Ordinal))
                        {
                            replaced = true;
                            Directory.Delete(pendingPath, recursive: true);
                            CreateSymbolicDirectoryOrSkip(pendingPath, outsideRoot);
                        }

                        return shouldPruneDirectory?.Invoke(relativePath) ?? GlobMatchStatus.NoMatch;
                    });

            ScanResult directResult = repository.Scan(fileWalk: replacingWalk);

            Assert.True(replaced);
            Assert.Equal(2, directResult.ExitCode);
            Assert.False(directResult.Completed);
            Assert.Contains(directResult.Report.Errors, item => item.Code == FixtureVaultContract.PathPolicyTraversalErrorCode);
            Assert.DoesNotContain(directResult.Report.Findings, item => item.Path.Contains("outside.received.json", StringComparison.Ordinal));

            Directory.Delete(pendingPath, recursive: true);
            Directory.CreateDirectory(pendingPath);
            replaced = false;
            var telemetry = new RecordingTelemetry();

            int exitCode = repository.Run(
                ["scan", "--format", "json"],
                telemetry,
                out string output,
                out string error,
                fileWalk: replacingWalk);
            using JsonDocument report = JsonDocument.Parse(output);

            Assert.Equal(2, exitCode);
            Assert.Empty(error);
            Assert.Equal(0, telemetry.SuccessfulScans);
            Assert.Contains(
                report.RootElement.GetProperty("errors").EnumerateArray(),
                item => item.GetProperty("code").GetString() == FixtureVaultContract.PathPolicyTraversalErrorCode);
            Assert.DoesNotContain("outside.received.json", output, StringComparison.Ordinal);
            Assert.DoesNotContain("FixtureVault scan complete.", output, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(outsideRoot))
            {
                Directory.Delete(outsideRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void Replacing_an_ancestor_of_a_pending_directory_fails_closed_without_disclosing_outside_paths()
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy();
        string parentPath = Path.Combine(repository.Root, "misc", "parent");
        Directory.CreateDirectory(Path.Combine(parentPath, "child"));
        string outsideRoot = Path.Combine(Path.GetTempPath(), "fixturevault-ancestor-replacement", Guid.NewGuid().ToString("N"));
        string outsideChild = Path.Combine(outsideRoot, "child");
        Directory.CreateDirectory(outsideChild);
        File.WriteAllText(Path.Combine(outsideChild, "private.received.json"), "outside-only canary\n");
        bool replaced = false;

        try
        {
            FixtureFileWalk replacingWalk = (repositoryRoot, root, failOnAccessErrors, shouldPruneDirectory) =>
                SafeFileWalker.Walk(
                    repositoryRoot,
                    root,
                    failOnAccessErrors,
                    relativePath =>
                    {
                        if (!replaced &&
                            Path.GetFullPath(root).Equals(repository.Root, StringComparison.Ordinal) &&
                            relativePath.Equals("misc/parent/child", StringComparison.Ordinal))
                        {
                            replaced = true;
                            Directory.Delete(parentPath, recursive: true);
                            CreateSymbolicDirectoryOrSkip(parentPath, outsideRoot);
                        }

                        return shouldPruneDirectory?.Invoke(relativePath) ?? GlobMatchStatus.NoMatch;
                    });

            ScanResult directResult = repository.Scan(fileWalk: replacingWalk);

            Assert.True(replaced);
            Assert.Equal(2, directResult.ExitCode);
            Assert.False(directResult.Completed);
            Assert.Contains(directResult.Report.Errors, item => item.Code == FixtureVaultContract.PathPolicyTraversalErrorCode);
            Assert.DoesNotContain(directResult.Report.Findings, item => item.Path.Contains("private.received.json", StringComparison.Ordinal));

            Directory.Delete(parentPath, recursive: true);
            Directory.CreateDirectory(Path.Combine(parentPath, "child"));
            replaced = false;
            var telemetry = new RecordingTelemetry();
            int exitCode = repository.Run(
                ["scan", "--format", "json"],
                telemetry,
                out string output,
                out string error,
                fileWalk: replacingWalk);
            using JsonDocument report = JsonDocument.Parse(output);

            Assert.True(replaced);
            Assert.Equal(2, exitCode);
            Assert.Empty(error);
            Assert.Equal(0, telemetry.SuccessfulScans);
            Assert.Contains(
                report.RootElement.GetProperty("errors").EnumerateArray(),
                item => item.GetProperty("code").GetString() == FixtureVaultContract.PathPolicyTraversalErrorCode);
            Assert.DoesNotContain("private.received.json", output, StringComparison.Ordinal);
            Assert.DoesNotContain("outside-only canary", output, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(parentPath))
            {
                Directory.Delete(parentPath, recursive: true);
            }

            if (Directory.Exists(outsideRoot))
            {
                Directory.Delete(outsideRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void Fixture_growth_after_the_initial_length_check_is_reported_as_a_changed_read()
    {
        using var repository = new TemporaryRepository();
        string path = Path.Combine(repository.Root, "tests", "growing.golden");
        repository.WriteText("tests/growing.golden", "1234");

        SafeFileReadStatus status = SafeFileReader.TryReadBytes(
            repository.Root,
            path,
            maximumBytes: 4,
            remainingTotalBytes: null,
            out _,
            out _,
            afterInitialLengthRead: () => File.AppendAllText(path, "5"));

        Assert.Equal(SafeFileReadStatus.GrewBeyondLimit, status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Fixture_growth_is_an_incomplete_execution_error_for_both_strictness_settings(bool strict)
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy(policy =>
        {
            policy.Ci!.Strict = strict;
            policy.MaxFileBytes = 4;
        });
        string path = Path.Combine(repository.Root, "tests", "growing.golden");
        repository.WriteText("tests/growing.golden", "1234");
        var telemetry = new RecordingTelemetry();

        int exitCode = repository.Run(
            ["scan", "--format", "json"],
            telemetry,
            out string output,
            out string error,
            afterFixtureInitialLengthRead: () => File.AppendAllText(path, "5"));
        using JsonDocument report = JsonDocument.Parse(output);

        Assert.Equal(2, exitCode);
        Assert.Empty(error);
        Assert.Equal(0, telemetry.SuccessfulScans);
        Assert.Contains(report.RootElement.GetProperty("errors").EnumerateArray(), item => item.GetProperty("code").GetString() == "FV-E009");
        Assert.DoesNotContain(report.RootElement.GetProperty("findings").EnumerateArray(), item => item.GetProperty("ruleId").GetString() == "FV004");
        Assert.DoesNotContain("FixtureVault scan complete.", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejected_growing_fixture_reads_count_toward_the_aggregate_byte_budget()
    {
        using var repository = new TemporaryRepository();
        const int fixtureCount = 129;
        const long maximumFileBytes = 1 * 1024 * 1024;
        repository.WritePolicy(policy =>
        {
            policy.Ci!.Strict = false;
            policy.MaxFileBytes = maximumFileBytes;
        });
        for (int index = 0; index < fixtureCount; index++)
        {
            repository.WriteText($"tests/fixture-{index:D3}.golden", "x");
        }

        int grownCount = 0;
        Action growNextFixture = () =>
        {
            string? next = Directory.EnumerateFiles(Path.Combine(repository.Root, "tests"))
                .OrderBy(path => path, StringComparer.Ordinal)
                .FirstOrDefault(path => new FileInfo(path).Length == 1);
            Assert.NotNull(next);
            using var stream = new FileStream(next!, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            stream.SetLength(maximumFileBytes + 1);
            grownCount++;
        };
        var telemetry = new RecordingTelemetry();

        int exitCode = repository.Run(
            ["scan", "--format", "json"],
            telemetry,
            out string output,
            out string error,
            afterFixtureInitialLengthRead: growNextFixture);
        using JsonDocument report = JsonDocument.Parse(output);

        Assert.True(grownCount >= 128);
        Assert.Equal(2, exitCode);
        Assert.Empty(error);
        Assert.Equal(0, telemetry.SuccessfulScans);
        Assert.Contains(report.RootElement.GetProperty("errors").EnumerateArray(), item => item.GetProperty("code").GetString() == "FV-E010");
        Assert.DoesNotContain("FixtureVault scan complete.", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Fixture_shrink_after_the_initial_length_check_fails_closed_at_the_scan_boundary()
    {
        using var directRepository = new TemporaryRepository();
        directRepository.WritePolicy();
        string directPath = Path.Combine(directRepository.Root, "tests", "shrinking.golden");
        directRepository.WriteText("tests/shrinking.golden", "1234");

        ScanResult directResult = directRepository.Scan(
            afterFixtureInitialLengthRead: () => TruncateFile(directPath));

        Assert.Equal(2, directResult.ExitCode);
        Assert.False(directResult.Completed);
        Assert.Contains(directResult.Report.Errors, item => item.Code == "FV-E009");

        using var applicationRepository = new TemporaryRepository();
        applicationRepository.WritePolicy();
        string applicationPath = Path.Combine(applicationRepository.Root, "tests", "shrinking.golden");
        applicationRepository.WriteText("tests/shrinking.golden", "1234");
        var telemetry = new RecordingTelemetry();

        int exitCode = applicationRepository.Run(
            ["scan", "--format", "json"],
            telemetry,
            out string output,
            out string error,
            afterFixtureInitialLengthRead: () => TruncateFile(applicationPath));

        using JsonDocument report = JsonDocument.Parse(output);
        Assert.Equal(2, exitCode);
        Assert.Empty(error);
        Assert.Equal(0, telemetry.SuccessfulScans);
        Assert.Contains(
            report.RootElement.GetProperty("errors").EnumerateArray(),
            item => item.GetProperty("code").GetString() == "FV-E009");
        Assert.DoesNotContain("FixtureVault scan complete.", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Aggregate_diagnostic_budget_fails_closed_for_many_small_collision_groups()
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy(policy => policy.Conventions = ["generic"]);
        const int groupCount = 3_000;
        var entries = new List<SafeFileEntry>(groupCount * 2);
        for (int index = 0; index < groupCount; index++)
        {
            string directory = $"tests/group-{index:D4}";
            entries.Add(new SafeFileEntry(
                Path.Combine(repository.Root, directory, "café.golden"),
                $"{directory}/café.golden"));
            entries.Add(new SafeFileEntry(
                Path.Combine(repository.Root, directory, "cafe\u0301.golden"),
                $"{directory}/cafe\u0301.golden"));
        }

        FixtureFileWalk walk = (repositoryRoot, root, _, _) =>
            Path.GetFullPath(root).Equals(Path.Combine(repository.Root, "tests"), StringComparison.Ordinal)
                ? new WalkResult(entries, [], null)
                : new WalkResult([], [], null);

        ScanResult result = repository.Scan(fileWalk: walk);
        Assert.Equal(2, result.ExitCode);
        Assert.False(result.Completed);
        Assert.Contains(result.Report.Errors, item => item.Code == FixtureVaultContract.DiagnosticBudgetErrorCode);
        Assert.Empty(result.Report.Findings);

        var telemetry = new RecordingTelemetry();
        int exitCode = repository.Run(
            ["scan", "--format", "json"],
            telemetry,
            out string output,
            out string error,
            fileWalk: walk);

        Assert.Equal(2, exitCode);
        Assert.Empty(error);
        Assert.Equal(0, telemetry.SuccessfulScans);
        using JsonDocument report = JsonDocument.Parse(output);
        Assert.Contains(
            report.RootElement.GetProperty("errors").EnumerateArray(),
            item => item.GetProperty("code").GetString() == FixtureVaultContract.DiagnosticBudgetErrorCode);
        Assert.Empty(report.RootElement.GetProperty("findings").EnumerateArray());
    }

    [Fact]
    public void Aggregate_diagnostic_budget_covers_ordinary_findings()
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy();
        const int findingCount = 3_000;
        var entries = new List<SafeFileEntry>(findingCount);
        for (int index = 0; index < findingCount; index++)
        {
            string relativePath = $"tests/case-{index:D4}.received.json";
            repository.WriteBytes(relativePath, []);
            entries.Add(new SafeFileEntry(Path.Combine(repository.Root, relativePath.Replace('/', Path.DirectorySeparatorChar)), relativePath));
        }
        FixtureFileWalk walk = (repositoryRoot, root, _, _) =>
            Path.GetFullPath(root).Equals(Path.Combine(repository.Root, "tests"), StringComparison.Ordinal)
                ? new WalkResult(entries, [], null)
                : new WalkResult([], [], null);

        ScanResult result = repository.Scan(fileWalk: walk);

        Assert.Equal(2, result.ExitCode);
        Assert.False(result.Completed);
        Assert.Contains(result.Report.Errors, item => item.Code == FixtureVaultContract.DiagnosticBudgetErrorCode);
    }

    [Fact]
    public void Aggregate_diagnostic_budget_covers_skipped_diagnostics()
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy();
        const int skippedCount = 3_000;
        var reparsePaths = Enumerable.Range(0, skippedCount)
            .Select(index => $"tests/link-{index:D4}")
            .ToList();
        FixtureFileWalk walk = (_, _, _, _) => new WalkResult([], reparsePaths, null);

        ScanResult result = repository.Scan(fileWalk: walk);

        Assert.Equal(2, result.ExitCode);
        Assert.False(result.Completed);
        Assert.Contains(result.Report.Errors, item => item.Code == FixtureVaultContract.DiagnosticBudgetErrorCode);
    }

    private static void TruncateFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        stream.SetLength(0);
    }

    [Fact]
    public void Policy_growth_beyond_the_read_limit_fails_closed()
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy();
        string policyPath = Path.Combine(repository.Root, FixtureVaultContract.PolicyFileName);
        string policyJson = File.ReadAllText(policyPath).TrimEnd();
        File.WriteAllText(policyPath, policyJson + new string(' ', 65_536 - policyJson.Length));

        PolicyLoadResult result = PolicyLoader.Load(repository.Root, () => File.AppendAllText(policyPath, " "));

        Assert.Null(result.Policy);
        Assert.Equal("FV-E005", result.Error!.Code);
    }

    [Fact]
    public void Manifest_growth_beyond_the_read_limit_fails_closed()
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy(policy => policy.Conventions = ["generic", "fixturevault-manifest"]);
        string manifestPath = Path.Combine(repository.Root, FixtureVaultContract.ManifestFileName);
        string manifestJson = "{\"version\":1,\"activeBaselines\":[]}";
        File.WriteAllText(manifestPath, manifestJson + new string(' ', 65_536 - manifestJson.Length));
        FixtureVaultPolicy policy = PolicyLoader.Load(repository.Root).Policy!;

        ScanResult result = FixtureScanner.Scan(
            repository.Root,
            policy,
            [],
            strictOverride: false,
            afterManifestInitialLengthRead: () => File.AppendAllText(manifestPath, " "));

        Assert.Equal(2, result.ExitCode);
        Assert.False(result.Completed);
        Assert.Contains(result.Report.Errors, item => item.Code == "FV-E011");
    }

    [Fact]
    public void Policy_and_manifest_files_are_not_fixture_candidates()
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy(policy =>
        {
            policy.AllowedExtensions = [".json"];
            policy.Conventions = ["generic", "fixturevault-manifest"];
        });
        repository.WriteText(
            FixtureVaultContract.ManifestFileName,
            "{\"version\":1,\"activeBaselines\":[]}" + "\n");

        ScanResult result = repository.Scan();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(0, result.Report.FilesInspected);
        Assert.DoesNotContain(result.Report.Findings, item => item.RuleId == "FV008");
    }

    [Fact]
    public void Case_colliding_paths_are_reported_when_the_filesystem_can_create_both()
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy();
        repository.WriteText("tests/Case.snap", "one\n");
        repository.WriteText("tests/case.snap", "two\n");
        if (File.ReadAllText(Path.Combine(repository.Root, "tests/Case.snap")) ==
            File.ReadAllText(Path.Combine(repository.Root, "tests/case.snap")))
        {
            return;
        }

        ScanResult result = repository.Scan();

        Assert.Equal(2, result.Report.Findings.Count(item => item.RuleId == "FV003"));
        Assert.All(
            result.Report.Findings.Where(item => item.RuleId == "FV003"),
            finding => Assert.DoesNotContain("Case.snap", finding.Message, StringComparison.Ordinal));
    }

    [Fact]
    public void Unicode_normalization_colliding_paths_are_reported_when_the_filesystem_can_create_both()
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy();
        string composed = "tests/café.golden";
        string decomposed = "tests/cafe\u0301.golden";
        repository.WriteText(composed, "composed\n");
        repository.WriteText(decomposed, "decomposed\n");
        if (!File.Exists(Path.Combine(repository.Root, "tests", "café.golden")) ||
            !File.Exists(Path.Combine(repository.Root, "tests", "cafe\u0301.golden")) ||
            File.ReadAllText(Path.Combine(repository.Root, "tests", "café.golden")) ==
            File.ReadAllText(Path.Combine(repository.Root, "tests", "cafe\u0301.golden")))
        {
            return;
        }

        ScanResult result = repository.Scan();

        Assert.Equal(2, result.Report.Findings.Count(item => item.RuleId == "FV003"));
        Assert.Contains(result.Report.Findings, item => item.Path.Contains("café", StringComparison.Ordinal));
        Assert.Contains(result.Report.Findings, item => item.Path.Contains("cafe\u0301", StringComparison.Ordinal));
    }

    [Fact]
    public void Overlapping_roots_inspect_one_file_without_duplicate_findings()
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy(policy => policy.Roots = ["tests", "tests/nested"]);
        repository.WriteText("tests/nested/OrderTests.received.json", "received\n");

        ScanResult result = repository.Scan();

        Assert.Equal(1, result.Report.FilesInspected);
        Assert.Single(result.Report.Findings, item => item.RuleId == "FV001");
    }

    [Fact]
    public void Oversized_files_are_reported_without_being_read_as_content()
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy(policy => policy.MaxFileBytes = 4);
        repository.WriteText("tests/large.golden", "12345\n");

        ScanResult result = repository.Scan();

        Finding finding = Assert.Single(result.Report.Findings, item => item.RuleId == "FV004");
        Assert.Equal("tests/large.golden", finding.Path);
    }

    [Fact]
    public void A_single_oversized_directory_fails_before_retaining_unbounded_entries()
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy();
        repository.WriteEmptyFiles("tests/entries", 100_001);

        ScanResult result = repository.Scan();

        Assert.Equal(2, result.ExitCode);
        Assert.False(result.Completed);
        Assert.Contains(result.Report.Errors, item => item.Code == "FV-E003");
    }

    [Fact]
    public void A_large_case_collision_group_fails_with_a_bounded_incomplete_diagnostic()
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy();
        const string basename = "abcdefghijklm";
        for (int mask = 0; mask < 8_192; mask++)
        {
            var name = new StringBuilder(basename.Length);
            for (int bit = 0; bit < basename.Length; bit++)
            {
                char character = basename[bit];
                name.Append((mask & (1 << bit)) == 0 ? character : char.ToUpperInvariant(character));
            }

            repository.WriteText($"tests/{name}.golden", "\n");
        }

        if (Directory.EnumerateFiles(Path.Combine(repository.Root, "tests")).Count() < 8_192)
        {
            return;
        }

        int exitCode = repository.Run(
            ["scan", "--format", "json"],
            new RecordingTelemetry(),
            out string output,
            out string error);
        using JsonDocument report = JsonDocument.Parse(output);

        Assert.Equal(2, exitCode);
        Assert.Empty(error);
        Assert.Empty(report.RootElement.GetProperty("findings").EnumerateArray());
        Assert.Contains(
            report.RootElement.GetProperty("errors").EnumerateArray(),
            item => item.GetProperty("code").GetString() == FixtureVaultContract.DiagnosticBudgetErrorCode);
        Assert.True(output.Length < 1_000_000);
    }

    [Fact]
    public void Binary_assets_are_not_decoded()
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy();
        repository.WriteBytes("tests/image.golden", [0x89, 0x50, 0x4E, 0x47, 0x00, 0x01]);

        ScanResult result = repository.Scan();

        Assert.Contains(result.Report.Findings, item => item.RuleId == "FV005");
    }

    [Fact]
    public void Known_binary_extension_bypasses_content_decoding_boundary()
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy();
        repository.WriteBytes("tests/image.png", [0x89, 0x50, 0x4E, 0x47, 0x00, 0x01]);
        bool classifierCalled = false;
        FixtureVaultPolicy policy = PolicyLoader.Load(repository.Root).Policy!;

        ScanResult result = FixtureScanner.Scan(
            repository.Root,
            policy,
            [],
            strictOverride: false,
            contentClassifier: _ =>
            {
                classifierCalled = true;
                throw new Xunit.Sdk.XunitException("Known binary content must bypass text decoding.");
            });

        Assert.False(classifierCalled);
        Assert.Contains(result.Report.Findings, item => item.RuleId == "FV005");
    }

    [Fact]
    public void Unexpected_binary_asset_under_fixture_root_is_blocked()
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy();
        repository.WriteBytes("tests/assets/photo.png", PngBytes());
        IReadOnlyDictionary<string, string> before = repository.HashTree();

        ScanResult result = repository.Scan();

        Assert.Equal(1, result.ExitCode);
        Finding finding = Assert.Single(result.Report.Findings);
        Assert.Equal("FV005", finding.RuleId);
        Assert.Equal("tests/assets/photo.png", finding.Path);
        Assert.Equal(before, repository.HashTree());
    }

    [Fact]
    public void Explicitly_allowed_binary_extension_is_accepted()
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy(policy => policy.AllowedExtensions!.Add(".png"));
        repository.WriteBytes("tests/assets/photo.png", PngBytes());
        IReadOnlyDictionary<string, string> before = repository.HashTree();

        ScanResult result = repository.Scan();

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Report.Findings);
        Assert.Equal(before, repository.HashTree());
    }

    [Fact]
    public void Verify_binary_baseline_is_unexpected_when_verify_convention_is_disabled()
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy(policy => policy.Conventions = ["generic"]);
        repository.WriteBytes("tests/Orders/OrderTests.verified.png", PngBytes());

        ScanResult result = repository.Scan();

        Assert.Equal(1, result.ExitCode);
        Finding finding = Assert.Single(result.Report.Findings);
        Assert.Equal("FV005", finding.RuleId);
    }

    [Fact]
    public void Verify_binary_baseline_is_unexpected_without_an_allowed_binary_extension()
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy(policy =>
        {
            policy.AllowedExtensions = [".golden"];
            policy.Conventions = ["generic"];
        });
        repository.WriteBytes("tests/assets/photo.png", PngBytes());

        ScanResult result = repository.Scan();

        Assert.Equal(1, result.ExitCode);
        Finding finding = Assert.Single(result.Report.Findings);
        Assert.Equal("FV005", finding.RuleId);
    }

    [Fact]
    public void Repository_root_fallback_still_audits_convention_shaped_unexpected_binary()
    {
        using var repository = new TemporaryRepository(createTestsDirectory: false);
        repository.WritePolicy(policy =>
        {
            policy.Roots = ["."];
            policy.Conventions = ["generic"];
        });
        repository.WriteBytes("tests/assets/photo.verified.png", PngBytes());

        ScanResult result = repository.Scan();

        Assert.Equal(1, result.ExitCode);
        Finding finding = Assert.Single(result.Report.Findings);
        Assert.Equal("FV005", finding.RuleId);
        Assert.Equal("tests/assets/photo.verified.png", finding.Path);
    }

    [Fact]
    public void Binary_verify_baseline_is_checked_by_manifest_and_size_policy()
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy(policy =>
        {
            policy.MaxFileBytes = 4;
            policy.Conventions = ["verify", "fixturevault-manifest"];
        });
        repository.WriteBytes("tests/Orders/OrderTests.verified.png", PngBytes());
        repository.WriteText(FixtureVaultContract.ManifestFileName, "{\"version\":1,\"activeBaselines\":[]}\n");

        ScanResult result = repository.Scan();

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(result.Report.Findings, item => item.RuleId == "FV002");
        Assert.Contains(result.Report.Findings, item => item.RuleId == "FV004");
        Assert.DoesNotContain(result.Report.Findings, item => item.RuleId == "FV005");
    }

    [Fact]
    public void Known_binary_assets_inside_fixture_roots_are_inspected()
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy();
        repository.WriteBytes("tests/image.png", [0x89, 0x50, 0x4E, 0x47, 0x00, 0x01]);
        repository.WriteBytes("tests/document.pdf", [0x25, 0x50, 0x44, 0x46, 0x00, 0x01]);
        repository.WriteBytes("tests/archive.zip", [0x50, 0x4B, 0x03, 0x04, 0x00, 0x01]);

        ScanResult result = repository.Scan();

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(3, result.Report.FilesInspected);
        Assert.Equal(3, result.Report.Findings.Count(item => item.RuleId == "FV005"));
        Assert.All(result.Report.Findings.Where(item => item.RuleId == "FV005"), finding =>
            Assert.True(finding.Path is "tests/image.png" or "tests/document.pdf" or "tests/archive.zip"));
    }

    [Fact]
    public void Ordinary_binary_assets_outside_fixture_roots_are_not_fixture_candidates()
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy();
        repository.WriteBytes("logo.png", [0x89, 0x50, 0x4E, 0x47, 0x00, 0x01]);
        repository.WriteBytes("docs/guide.pdf", [0x25, 0x50, 0x44, 0x46, 0x00, 0x01]);
        repository.WriteBytes("archives/fixtures.zip", [0x50, 0x4B, 0x03, 0x04, 0x00, 0x01]);

        ScanResult result = repository.Scan();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(0, result.Report.FilesInspected);
        Assert.DoesNotContain(result.Report.Findings, item => item.RuleId is "FV005" or "FV008");
    }

    [Fact]
    public void Verify_encoding_and_newline_variants_are_not_blocked_but_non_verify_invalid_encoding_is_detected()
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy();
        repository.WriteBytes("tests/bom.golden", [0xEF, 0xBB, 0xBF, 0x6F, 0x6B, 0x0A]);
        repository.WriteBytes("tests/verify.verified.json", [0xEF, 0xBB, 0xBF, 0x6F, 0x6E, 0x65, 0x0D, 0x0A, 0x74, 0x77, 0x6F]);
        repository.WriteBytes("tests/utf16.golden", [0xFF, 0xFE, 0x6F, 0x00, 0x6B, 0x00]);
        repository.WriteBytes("tests/verify-utf16.verified.json", [0xFF, 0xFE, 0x6F, 0x00, 0x6E, 0x00, 0x65, 0x00]);

        ScanResult result = repository.Scan();

        Assert.Equal(1, result.Report.Findings.Count(item => item.RuleId == "FV006"));
        Assert.Contains(result.Report.Findings, item => item.RuleId == "FV006" && item.Path == "tests/utf16.golden");
        Assert.DoesNotContain(result.Report.Findings, item => item.Path == "tests/bom.golden");
        Assert.DoesNotContain(result.Report.Findings, item => item.Path == "tests/verify.verified.json");
        Assert.DoesNotContain(result.Report.Findings, item => item.Path == "tests/verify-utf16.verified.json");
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public void Verify_custom_encoding_and_newline_variants_are_clean_and_still_content_inspected()
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy();
        repository.WriteBytes("tests/custom-encoding.verified.json", [0xFF, 0xFE, 0x6F, 0x00, 0x6E, 0x00, 0x65, 0x00]);
        repository.WriteBytes("tests/carriage-return.verified.json", [0x6F, 0x6E, 0x65, 0x0D, 0x0A, 0x74, 0x77, 0x6F]);
        repository.WriteBytes("tests/trailing-newline.verified.json", [0x6F, 0x6E, 0x65, 0x0A]);

        ScanResult result = repository.Scan();

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Report.Findings);
        Assert.Empty(result.Report.Errors);
        Assert.DoesNotContain(result.Report.Skipped, item => item.Code == "FV-SKIP-ENCODING");
    }

    [Theory]
    [InlineData("utf-16le")]
    [InlineData("utf-16be")]
    [InlineData("utf-32le")]
    [InlineData("utf-32be")]
    public void Verify_bom_declared_encoding_is_decoded_and_sensitive_data_is_still_detected(string encodingName)
    {
        const string sensitiveValue = "fixture-test-secret-1234567890";
        using var repository = new TemporaryRepository();
        repository.WritePolicy();
        repository.WriteBytes(
            "tests/Payments/Create.verified.json",
            EncodeWithDeclaredBom(encodingName, $"{{\"apiKey\":\"{sensitiveValue}\"}}\n"));

        ScanResult result = repository.Scan();
        string json = result.Report.ToJson();

        Assert.Equal(1, result.ExitCode);
        Finding finding = Assert.Single(result.Report.Findings, item => item.RuleId == "FV007");
        Assert.Equal("tests/Payments/Create.verified.json", finding.Path);
        Assert.DoesNotContain(result.Report.Findings, item => item.RuleId == "FV006");
        Assert.DoesNotContain(result.Report.Skipped, item => item.Code == "FV-SKIP-ENCODING");
        Assert.Empty(result.Report.Errors);
        Assert.Equal(1, result.Report.FilesInspected);
        Assert.DoesNotContain(sensitiveValue, json, StringComparison.Ordinal);
    }

    [Fact]
    public void Nul_byte_verify_baseline_fails_closed_while_plain_utf8_still_reports_fv007()
    {
        const string sensitiveValue = "fixture-test-secret-1234567890";
        string contents = $"{{\"apiKey\":\"{sensitiveValue}\"}}\n";
        using var repository = new TemporaryRepository();
        repository.WritePolicy();
        repository.WriteBytes("tests/Payments/Create.verified.json", Encoding.UTF8.GetBytes(contents));

        ScanResult control = repository.Scan();

        Assert.Equal(1, control.ExitCode);
        Assert.Empty(control.Report.Errors);
        Assert.Equal(1, control.Report.FilesInspected);
        Assert.Contains(control.Report.Findings, item =>
            item.RuleId == "FV007" && item.Path == "tests/Payments/Create.verified.json");
        Assert.DoesNotContain(control.Report.Skipped, item => item.Code == "FV-SKIP-ENCODING");

        repository.WriteBytes(
            "tests/Payments/Create.verified.json",
            [.. Encoding.UTF8.GetBytes(contents), 0x00]);
        var telemetry = new RecordingTelemetry();

        int exitCode = repository.Run(["scan", "--format", "json"], telemetry, out string output, out string error);
        using JsonDocument report = JsonDocument.Parse(output);

        Assert.Equal(2, exitCode);
        Assert.Empty(error);
        Assert.Equal(0, telemetry.SuccessfulScans);
        Assert.Equal(1, report.RootElement.GetProperty("filesInspected").GetInt32());
        Assert.Empty(report.RootElement.GetProperty("findings").EnumerateArray());
        JsonElement skip = Assert.Single(report.RootElement.GetProperty("skipped").EnumerateArray(), item =>
            item.GetProperty("code").GetString() == FixtureVaultContract.UninspectableContentSkippedCode);
        Assert.Equal("tests/Payments/Create.verified.json", skip.GetProperty("path").GetString());
        Assert.Equal(FixtureVaultContract.UndeclaredNulContentSkippedReason, skip.GetProperty("reason").GetString());
        JsonElement scanError = Assert.Single(report.RootElement.GetProperty("errors").EnumerateArray());
        Assert.Equal(FixtureVaultContract.UninspectableContentErrorCode, scanError.GetProperty("code").GetString());
        Assert.DoesNotContain(sensitiveValue, output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("utf-16le")]
    [InlineData("utf-16be")]
    [InlineData("utf-32le")]
    [InlineData("utf-32be")]
    public void Bom_less_declared_encoding_verify_baseline_is_never_reported_as_fully_checked(string encodingName)
    {
        const string sensitiveValue = "fixture-test-secret-1234567890";
        using var repository = new TemporaryRepository();
        repository.WritePolicy();
        repository.WriteBytes(
            "tests/Payments/Create.verified.json",
            EncodeWithoutBom(encodingName, $"{{\"apiKey\":\"{sensitiveValue}\"}}\n"));
        var telemetry = new RecordingTelemetry();

        int exitCode = repository.Run(["scan", "--format", "json"], telemetry, out string output, out string error);
        using JsonDocument report = JsonDocument.Parse(output);

        Assert.Equal(2, exitCode);
        Assert.Empty(error);
        Assert.Equal(0, telemetry.SuccessfulScans);
        Assert.Equal(1, report.RootElement.GetProperty("filesInspected").GetInt32());
        Assert.Empty(report.RootElement.GetProperty("findings").EnumerateArray());
        JsonElement skip = Assert.Single(report.RootElement.GetProperty("skipped").EnumerateArray(), item =>
            item.GetProperty("code").GetString() == FixtureVaultContract.UninspectableContentSkippedCode);
        Assert.Equal("tests/Payments/Create.verified.json", skip.GetProperty("path").GetString());
        Assert.Equal(FixtureVaultContract.UndeclaredNulContentSkippedReason, skip.GetProperty("reason").GetString());
        JsonElement scanError = Assert.Single(report.RootElement.GetProperty("errors").EnumerateArray());
        Assert.Equal(FixtureVaultContract.UninspectableContentErrorCode, scanError.GetProperty("code").GetString());
    }

    [Fact]
    public void Nul_byte_split_mode_verify_baseline_is_never_reported_as_fully_checked()
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy();
        repository.WriteBytes(
            "tests/Payments/Create.verified/Order.json",
            [.. Encoding.UTF8.GetBytes("{\"apiKey\":\"fixture-test-secret-1234567890\"}\n"), 0x00]);

        ScanResult result = repository.Scan();

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.Report.Findings);
        Assert.Equal(1, result.Report.FilesInspected);
        SkippedDiagnostic skip = Assert.Single(result.Report.Skipped, item =>
            item.Code == FixtureVaultContract.UninspectableContentSkippedCode);
        Assert.Equal("tests/Payments/Create.verified/Order.json", skip.Path);
        Assert.Equal(FixtureVaultContract.UndeclaredNulContentSkippedReason, skip.Reason);
        Assert.Contains(result.Report.Errors, item => item.Code == FixtureVaultContract.UninspectableContentErrorCode);
    }

    [Fact]
    public void Verify_baseline_with_unclassified_binary_bytes_is_not_silently_accepted()
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy();
        repository.WriteBytes("tests/Payments/Create.verified.json", [0x89, 0x50, 0x4E, 0x47, 0x00, 0x01]);

        ScanResult result = repository.Scan();

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.Report.Findings);
        Assert.Equal(1, result.Report.FilesInspected);
        SkippedDiagnostic skip = Assert.Single(result.Report.Skipped, item =>
            item.Code == FixtureVaultContract.UninspectableContentSkippedCode);
        Assert.Equal("tests/Payments/Create.verified.json", skip.Path);
        Assert.Equal(FixtureVaultContract.UninspectableContentSkippedReason, skip.Reason);
        Assert.Contains(result.Report.Errors, item => item.Code == FixtureVaultContract.UninspectableContentErrorCode);
    }

    [Fact]
    public void Non_verify_nul_content_is_reported_as_unproven_encoding_instead_of_a_checked_fixture()
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy();
        repository.WriteBytes("tests/nul-bytes.golden", [0x6F, 0x6B, 0x00, 0x0A]);

        ScanResult result = repository.Scan();

        Finding finding = Assert.Single(result.Report.Findings, item => item.RuleId == "FV006");
        Assert.Equal("tests/nul-bytes.golden", finding.Path);
        Assert.Equal(2, result.ExitCode);
        Assert.DoesNotContain(result.Report.Findings, item => item.RuleId == "FV005");
        SkippedDiagnostic skip = Assert.Single(result.Report.Skipped, item =>
            item.Code == FixtureVaultContract.UninspectableContentSkippedCode);
        Assert.Equal("tests/nul-bytes.golden", skip.Path);
        Assert.Equal(FixtureVaultContract.UndeclaredNulContentSkippedReason, skip.Reason);
    }

    [Theory]
    // Declared encoding | decoded content | whether the fixture also carries a synthetic sensitive value.
    [InlineData("none", "text", false)]
    [InlineData("none", "text", true)]
    [InlineData("none", "nul", false)]
    [InlineData("none", "nul", true)]
    [InlineData("utf-8", "text", false)]
    [InlineData("utf-8", "text", true)]
    [InlineData("utf-8", "nul", false)]
    [InlineData("utf-8", "nul", true)]
    [InlineData("utf-16le", "text", false)]
    [InlineData("utf-16le", "text", true)]
    [InlineData("utf-16le", "nul", false)]
    [InlineData("utf-16le", "nul", true)]
    [InlineData("utf-16be", "text", false)]
    [InlineData("utf-16be", "text", true)]
    [InlineData("utf-16be", "nul", false)]
    [InlineData("utf-16be", "nul", true)]
    [InlineData("utf-32le", "text", false)]
    [InlineData("utf-32le", "text", true)]
    [InlineData("utf-32le", "nul", false)]
    [InlineData("utf-32le", "nul", true)]
    [InlineData("utf-32be", "text", false)]
    [InlineData("utf-32be", "text", true)]
    [InlineData("utf-32be", "nul", false)]
    [InlineData("utf-32be", "nul", true)]
    [InlineData("none", "undecodable", false)]
    [InlineData("none", "undecodable", true)]
    [InlineData("utf-8", "undecodable", false)]
    [InlineData("utf-8", "undecodable", true)]
    [InlineData("utf-16le", "undecodable", false)]
    [InlineData("utf-16le", "undecodable", true)]
    [InlineData("utf-16be", "undecodable", false)]
    [InlineData("utf-16be", "undecodable", true)]
    [InlineData("utf-32le", "undecodable", false)]
    [InlineData("utf-32le", "undecodable", true)]
    [InlineData("utf-32be", "undecodable", false)]
    [InlineData("utf-32be", "undecodable", true)]
    public void Content_classification_decides_the_encoding_decodability_nul_matrix(
        string declaredEncoding,
        string contentShape,
        bool withSensitiveValue)
    {
        byte[] fixtureBytes = DeclaredEncodingFixtureBytes(declaredEncoding, contentShape, withSensitiveValue);
        AssertSensitiveValueIsPlanted(fixtureBytes, contentShape, withSensitiveValue);

        ContentClassification classification = ContentClassification.Classify(
            fixtureBytes);

        switch (contentShape)
        {
            case "text":
                Assert.Equal(ContentDecodeOutcome.Decoded, classification.Outcome);
                Assert.Contains(
                    withSensitiveValue ? SensitiveValue : "orderId",
                    classification.Text,
                    StringComparison.Ordinal);
                break;
            case "nul":
                Assert.Equal(ContentDecodeOutcome.DecodedWithNul, classification.Outcome);
                Assert.Contains('\0', classification.Text);
                break;
            default:
                Assert.Equal(ContentDecodeOutcome.Undecodable, classification.Outcome);
                Assert.Equal(string.Empty, classification.Text);
                break;
        }

        Assert.Equal(
            declaredEncoding == "none" ? null : DeclaredEncodingName(declaredEncoding),
            classification.DeclaredEncodingName);
        Assert.Equal(
            contentShape == "text" ? ContentKind.Inspected : ContentKind.Uninspectable,
            ContentClassification.Resolve(classification, isKnownBinaryExtension: false, isVerifyFixture: true));
        // A known binary extension is never decoded as text, whatever its bytes happen to decode to.
        Assert.Equal(
            ContentKind.BinaryAsset,
            ContentClassification.Resolve(classification, isKnownBinaryExtension: true, isVerifyFixture: true));
    }

    [Fact]
    public void Undecodable_nul_bytes_are_a_binary_asset_only_outside_the_verify_convention()
    {
        ContentClassification classification = ContentClassification.Classify(
            [0x89, 0x50, 0x4E, 0x47, 0x00, 0x01]);

        Assert.Equal(ContentDecodeOutcome.Undecodable, classification.Outcome);
        Assert.Null(classification.DeclaredEncodingName);
        Assert.True(classification.ProvesUndeclaredBinaryBlob);
        Assert.Equal(
            ContentKind.Uninspectable,
            ContentClassification.Resolve(classification, isKnownBinaryExtension: false, isVerifyFixture: true));
        Assert.Equal(
            ContentKind.BinaryAsset,
            ContentClassification.Resolve(classification, isKnownBinaryExtension: false, isVerifyFixture: false));
    }

    [Theory]
    // Declared encoding | decoded content | whether the fixture also carries a synthetic sensitive value.
    [InlineData("none", "text", false)]
    [InlineData("none", "text", true)]
    [InlineData("none", "nul", false)]
    [InlineData("none", "nul", true)]
    [InlineData("utf-8", "text", false)]
    [InlineData("utf-8", "text", true)]
    [InlineData("utf-8", "nul", false)]
    [InlineData("utf-8", "nul", true)]
    [InlineData("utf-16le", "text", false)]
    [InlineData("utf-16le", "text", true)]
    [InlineData("utf-16le", "nul", false)]
    [InlineData("utf-16le", "nul", true)]
    [InlineData("utf-16be", "text", false)]
    [InlineData("utf-16be", "text", true)]
    [InlineData("utf-16be", "nul", false)]
    [InlineData("utf-16be", "nul", true)]
    [InlineData("utf-32le", "text", false)]
    [InlineData("utf-32le", "text", true)]
    [InlineData("utf-32le", "nul", false)]
    [InlineData("utf-32le", "nul", true)]
    [InlineData("utf-32be", "text", false)]
    [InlineData("utf-32be", "text", true)]
    [InlineData("utf-32be", "nul", false)]
    [InlineData("utf-32be", "nul", true)]
    [InlineData("none", "undecodable", false)]
    [InlineData("none", "undecodable", true)]
    [InlineData("utf-8", "undecodable", false)]
    [InlineData("utf-8", "undecodable", true)]
    [InlineData("utf-16le", "undecodable", false)]
    [InlineData("utf-16le", "undecodable", true)]
    [InlineData("utf-16be", "undecodable", false)]
    [InlineData("utf-16be", "undecodable", true)]
    [InlineData("utf-32le", "undecodable", false)]
    [InlineData("utf-32le", "undecodable", true)]
    [InlineData("utf-32be", "undecodable", false)]
    [InlineData("utf-32be", "undecodable", true)]
    public void Content_classification_matrix_is_reflected_in_every_reported_field(
        string declaredEncoding,
        string contentShape,
        bool withSensitiveValue)
    {
        const string path = "tests/Payments/Create.verified.json";
        using var repository = new TemporaryRepository();
        repository.WritePolicy();
        byte[] fixtureBytes = DeclaredEncodingFixtureBytes(declaredEncoding, contentShape, withSensitiveValue);
        AssertSensitiveValueIsPlanted(fixtureBytes, contentShape, withSensitiveValue);
        repository.WriteBytes(path, fixtureBytes);
        var telemetry = new RecordingTelemetry();

        int exitCode = repository.Run(["scan", "--format", "json"], telemetry, out string output, out string error);
        using JsonDocument report = JsonDocument.Parse(output);
        JsonElement root = report.RootElement;
        JsonElement[] findings = [.. root.GetProperty("findings").EnumerateArray()];
        JsonElement[] skipped = [.. root.GetProperty("skipped").EnumerateArray()];
        JsonElement[] errors = [.. root.GetProperty("errors").EnumerateArray()];

        Assert.Equal(1, root.GetProperty("filesInspected").GetInt32());

        if (contentShape is "nul" or "undecodable")
        {
            // Content that cannot be trusted is never reported as a clean, fully checked fixture,
            // whether or not a detector would have matched.
            Assert.Equal(2, exitCode);
            Assert.Empty(findings);
            Assert.Empty(error);
            Assert.Equal(0, telemetry.SuccessfulScans);
            JsonElement scanError = Assert.Single(errors);
            Assert.Equal(FixtureVaultContract.UninspectableContentErrorCode, scanError.GetProperty("code").GetString());
            JsonElement skip = Assert.Single(skipped, item =>
                item.GetProperty("code").GetString() == FixtureVaultContract.UninspectableContentSkippedCode);
            Assert.Equal(path, skip.GetProperty("path").GetString());
            string reason = skip.GetProperty("reason").GetString()!;
            Assert.Equal(ExpectedSkipReason(declaredEncoding, contentShape), reason);
            if (declaredEncoding != "none")
            {
                Assert.DoesNotContain("declare no byte-order mark", reason, StringComparison.Ordinal);
                Assert.Contains(DeclaredEncodingName(declaredEncoding), reason, StringComparison.Ordinal);
            }
        }
        else if (withSensitiveValue)
        {
            Assert.Equal(1, exitCode);
            Assert.Empty(errors);
            Assert.Empty(error);
            Assert.Equal(1, telemetry.SuccessfulScans);
            JsonElement finding = Assert.Single(findings);
            Assert.Equal("FV007", finding.GetProperty("ruleId").GetString());
            Assert.Equal(path, finding.GetProperty("path").GetString());
            Assert.DoesNotContain(skipped, item =>
                item.GetProperty("code").GetString() == FixtureVaultContract.UninspectableContentSkippedCode);
        }
        else
        {
            Assert.Equal(0, exitCode);
            Assert.Empty(findings);
            Assert.Empty(errors);
            Assert.Empty(error);
            Assert.Equal(1, telemetry.SuccessfulScans);
            Assert.DoesNotContain(skipped, item =>
                item.GetProperty("code").GetString() == FixtureVaultContract.UninspectableContentSkippedCode);
        }

        AssertNoCanary(SensitiveValue, output, error);
    }

    [Fact]
    public void Nul_byte_verify_baseline_skip_is_reported_in_human_readable_output()
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy();
        repository.WriteBytes(
            "tests/Payments/Create.verified.json",
            [.. Encoding.UTF8.GetBytes("{\"apiKey\":\"fixture-test-secret-1234567890\"}\n"), 0x00]);

        int exitCode = repository.Run(["scan"], new RecordingTelemetry(), out string output, out string error);

        Assert.Equal(2, exitCode);
        Assert.Contains(FixtureVaultContract.UninspectableContentErrorCode, error, StringComparison.Ordinal);
        Assert.Contains(FixtureVaultContract.UninspectableContentSkippedCode, error, StringComparison.Ordinal);
        Assert.Contains("tests/Payments/Create.verified.json", error, StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-test-secret-1234567890", output, StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-test-secret-1234567890", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("console")]
    [InlineData("json")]
    public void Single_malformed_fixture_preserves_fv006_in_failure_output(string format)
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy();
        repository.WriteBytes("tests/malformed.golden", [0xC3, 0x28]);
        var telemetry = new RecordingTelemetry();

        int exitCode = repository.Run(["scan", "--format", format], telemetry, out string output, out string error);

        Assert.Equal(2, exitCode);
        Assert.Equal(0, telemetry.SuccessfulScans);
        if (format == "json")
        {
            using JsonDocument report = JsonDocument.Parse(output);
            Assert.Contains(report.RootElement.GetProperty("findings").EnumerateArray(), item =>
                item.GetProperty("ruleId").GetString() == "FV006");
            Assert.Contains(report.RootElement.GetProperty("errors").EnumerateArray(), item =>
                item.GetProperty("code").GetString() == FixtureVaultContract.UninspectableContentErrorCode);
            Assert.Empty(error);
        }
        else
        {
            Assert.Empty(output);
            Assert.Contains("FV006", error, StringComparison.Ordinal);
            Assert.Contains("Remediation:", error, StringComparison.Ordinal);
            Assert.Contains("malformed.golden", error, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(true, "console")]
    [InlineData(true, "json")]
    [InlineData(false, "console")]
    [InlineData(false, "json")]
    public void Mixed_sensitive_and_malformed_fixtures_preserve_findings_on_failure(bool strict, string format)
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy(policy => policy.Ci!.Strict = strict);
        repository.WriteText("tests/a-sensitive.golden", "{\"apiKey\":\"fixture-test-secret-1234567890\"}\n");
        repository.WriteBytes("tests/z-malformed.golden", [0xC3, 0x28]);

        int exitCode = repository.Run(["scan", "--format", format], new RecordingTelemetry(), out string output, out string error);

        Assert.Equal(2, exitCode);
        if (format == "json")
        {
            using JsonDocument report = JsonDocument.Parse(output);
            JsonElement[] findings = [.. report.RootElement.GetProperty("findings").EnumerateArray()];
            Assert.Contains(findings, item => item.GetProperty("ruleId").GetString() == "FV007");
            Assert.Contains(findings, item => item.GetProperty("ruleId").GetString() == "FV006");
            string expectedDisposition = strict ? "block" : "warn";
            Assert.All(findings, item => Assert.Equal(expectedDisposition, item.GetProperty("disposition").GetString()));
            Assert.Empty(error);
        }
        else
        {
            Assert.Empty(output);
            Assert.Contains("FV007", error, StringComparison.Ordinal);
            Assert.Contains("FV006", error, StringComparison.Ordinal);
            Assert.Contains("Remediation:", error, StringComparison.Ordinal);
            Assert.Contains("a-sensitive.golden", error, StringComparison.Ordinal);
            Assert.Contains("z-malformed.golden", error, StringComparison.Ordinal);
            Assert.Contains(strict ? "block" : "warn", error, StringComparison.Ordinal);
        }

        AssertNoCanary(SensitiveValue, output, error);
    }

    [Fact]
    public void Verify_undecodable_fixture_is_skipped_and_fails_closed_when_sensitive_detection_is_enabled()
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy();
        repository.WriteBytes("tests/undecodable.verified.json", [0xC3, 0x28]);
        var telemetry = new RecordingTelemetry();

        int exitCode = repository.Run(["scan", "--format", "json"], telemetry, out string output, out string error);
        using JsonDocument report = JsonDocument.Parse(output);

        Assert.Equal(2, exitCode);
        Assert.Empty(error);
        Assert.Equal(0, telemetry.SuccessfulScans);
        JsonElement skip = Assert.Single(report.RootElement.GetProperty("skipped").EnumerateArray(), item =>
            item.GetProperty("code").GetString() == FixtureVaultContract.UninspectableContentSkippedCode);
        Assert.Equal("tests/undecodable.verified.json", skip.GetProperty("path").GetString());
        JsonElement scanError = Assert.Single(report.RootElement.GetProperty("errors").EnumerateArray());
        Assert.Equal(FixtureVaultContract.UninspectableContentErrorCode, scanError.GetProperty("code").GetString());
        Assert.Equal(FixtureVaultContract.UninspectableContentErrorMessage, scanError.GetProperty("message").GetString());
        Assert.DoesNotContain(report.RootElement.GetProperty("findings").EnumerateArray(), item =>
            item.GetProperty("ruleId").GetString() is "FV006" or "FV007");
    }

    [Fact]
    public void Verify_undecodable_fixture_skip_is_reported_in_human_readable_output()
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy();
        repository.WriteBytes("tests/undecodable.verified.json", [0xC3, 0x28]);

        int exitCode = repository.Run(["scan"], new RecordingTelemetry(), out string output, out string error);

        Assert.Equal(2, exitCode);
        Assert.Contains(FixtureVaultContract.UninspectableContentErrorCode, error, StringComparison.Ordinal);
        Assert.Contains(FixtureVaultContract.UninspectableContentSkippedCode, error, StringComparison.Ordinal);
        Assert.Contains("tests/undecodable.verified.json", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_undecodable_fixture_is_only_skipped_when_sensitive_detection_is_disabled()
    {
        using var repository = new TemporaryRepository();
        repository.WriteBytes("tests/undecodable.verified.json", [0xC3, 0x28]);
        FixtureVaultPolicy policy = FixtureVaultPolicy.CreateDefault(testsDirectoryExists: true);
        // The v1 policy loader always enables sensitive-data detection; this policy documents the
        // documented contract for a scanner run whose configured policy disables it.
        policy.SensitiveDataRules = [];

        ScanResult result = FixtureScanner.Scan(repository.Root, policy, [], strictOverride: false);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Report.Findings);
        Assert.Empty(result.Report.Errors);
        SkippedDiagnostic skip = Assert.Single(result.Report.Skipped, item =>
            item.Code == FixtureVaultContract.UninspectableContentSkippedCode);
        Assert.Equal("tests/undecodable.verified.json", skip.Path);
    }

    [Fact]
    public void Verify_utf8_fixture_detects_sensitive_data_without_skip_or_encoding_finding()
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy();
        repository.WriteText("tests/Payments/Create.verified.json", "{\"apiKey\":\"fixture-test-secret-1234567890\"}\n");

        ScanResult result = repository.Scan();

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(result.Report.Findings, item =>
            item.RuleId == "FV007" && item.Path == "tests/Payments/Create.verified.json");
        Assert.DoesNotContain(result.Report.Findings, item => item.RuleId == "FV006");
        Assert.DoesNotContain(result.Report.Skipped, item => item.Code == "FV-SKIP-ENCODING");
        Assert.Empty(result.Report.Errors);
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xFE, 0x6F, 0x00, 0x6B, 0x00 }, 1, false)]
    [InlineData(new byte[] { 0xC3, 0x28 }, 2, true)]
    public void Non_verify_non_utf8_text_still_blocks_with_fv006(byte[] bytes, int expectedExitCode, bool expectsSkip)
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy();
        repository.WriteBytes("tests/utf16.golden", bytes);

        ScanResult result = repository.Scan();

        Finding finding = Assert.Single(result.Report.Findings, item => item.RuleId == "FV006");
        Assert.Equal("tests/utf16.golden", finding.Path);
        Assert.Equal("block", finding.Disposition);
        Assert.Equal(expectedExitCode, result.ExitCode);
        Assert.DoesNotContain(result.Report.Findings, item => item.RuleId == "FV007");
        Assert.Equal(
            expectsSkip,
            result.Report.Skipped.Any(item => item.Code == "FV-SKIP-ENCODING"));
    }

    [Fact]
    public void Unicode_utf8_fixture_is_supported()
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy();
        repository.WriteText("tests/注文/結果.golden", "こんにちは\n");

        ScanResult result = repository.Scan();

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain(result.Report.Findings, item => item.Path.Contains("結果", StringComparison.Ordinal));
    }

    [Fact]
    public void High_confidence_sensitive_data_is_never_echoed()
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy();
        const string sensitiveValue = "fixture-test-secret-1234567890";
        repository.WriteText("tests/payment.golden", $"{{\"apiKey\":\"{sensitiveValue}\"}}\n");

        ScanResult result = repository.Scan();
        string json = result.Report.ToJson();

        Assert.Contains(result.Report.Findings, item => item.RuleId == "FV007");
        Assert.DoesNotContain(sensitiveValue, json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Password=\"\";", "console", false)]
    [InlineData("Password=\"\";", "json", false)]
    [InlineData("Pwd='';", "console", false)]
    [InlineData("Pwd='';", "json", false)]
    [InlineData("PWD='';", "console", false)]
    [InlineData("PWD='';", "json", false)]
    [InlineData("pWd='  ';", "console", false)]
    [InlineData("pWd='  ';", "json", false)]
    [InlineData(" Password =  *** ; ", "console", false)]
    [InlineData(" Password =  *** ; ", "json", false)]
    [InlineData("pAsSwOrD=<redacted>;", "console", false)]
    [InlineData("pAsSwOrD=<redacted>;", "json", false)]
    [InlineData("Password=[REDACTED];Pwd=[REDACTED];", "console", false)]
    [InlineData("Password=[REDACTED];Pwd=[REDACTED];", "json", false)]
    [InlineData("Password=[REDACTED]; Pwd = 'real-connection-secret-1234567890';", "console", true)]
    [InlineData("Password=[REDACTED]; Pwd = 'real-connection-secret-1234567890';", "json", true)]
    public void Connection_string_adapter_only_reports_real_password_values(
        string connectionString,
        string format,
        bool expectsFinding)
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy();
        repository.WriteText("tests/connection-string.golden", connectionString + "\n");
        var telemetry = new RecordingTelemetry();

        int exitCode = repository.Run(["scan", "--format", format], telemetry, out string output, out string error);

        Assert.Equal(expectsFinding ? 1 : 0, exitCode);
        Assert.Empty(error);
        Assert.Equal(1, telemetry.SuccessfulScans);
        Assert.Equal(expectsFinding, output.Contains("FV007", StringComparison.Ordinal));
        Assert.DoesNotContain("real-connection-secret-1234567890", output, StringComparison.Ordinal);
        if (format == "json")
        {
            using JsonDocument report = JsonDocument.Parse(output);
            Assert.Equal(expectsFinding, report.RootElement.GetProperty("findings").EnumerateArray().Any());
        }
    }

    [Theory]
    [InlineData("console")]
    [InlineData("json")]
    public void Connection_string_password_keywords_are_case_insensitive_without_disclosing_values(string format)
    {
        const string canary = "Canary123";
        using var repository = new TemporaryRepository();
        repository.WritePolicy();
        repository.WriteText(
            "tests/connection-string-casing.golden",
            "Server=example.invalid;User Id=fixture;Password=Canary123;\n" +
            "Server=example.invalid;User Id=fixture;Pwd=Canary123;\n" +
            "Server=example.invalid;User Id=fixture;PWD=Canary123;\n" +
            "Server=example.invalid;User Id=fixture;pwd=Canary123;\n" +
            "Server=example.invalid;User Id=fixture;pAsSwOrD='Canary123';\n" +
            "Server=example.invalid;User Id=fixture;pWd=\"Canary123\";\n");
        var telemetry = new RecordingTelemetry();

        int exitCode = repository.Run(["scan", "--format", format], telemetry, out string output, out string error);

        Assert.Equal(1, exitCode);
        Assert.Empty(error);
        Assert.Equal(1, telemetry.SuccessfulScans);
        Assert.Contains("FV007", output, StringComparison.Ordinal);
        Assert.DoesNotContain(canary, output, StringComparison.Ordinal);
        if (format == "json")
        {
            using JsonDocument report = JsonDocument.Parse(output);
            JsonElement finding = Assert.Single(report.RootElement.GetProperty("findings").EnumerateArray());
            Assert.Equal("FV007", finding.GetProperty("ruleId").GetString());
            Assert.Equal("block", finding.GetProperty("disposition").GetString());
        }
    }

    [Theory]
    [InlineData("console")]
    [InlineData("json")]
    public void Escaped_connection_string_quotes_still_report_non_empty_credentials_without_disclosing_values(string format)
    {
        const string canary = "Canary123";
        using var repository = new TemporaryRepository();
        repository.WritePolicy();
        repository.WriteText(
            "tests/connection-string-escaped-quotes.golden",
            "Server=example.invalid;Password=\"\"\"Canary123\"\"\";\n" +
            "Server=example.invalid;Pwd='''Canary123''';\n");

        int exitCode = repository.Run(["scan", "--format", format], new RecordingTelemetry(), out string output, out string error);

        Assert.Equal(1, exitCode);
        Assert.Empty(error);
        Assert.Contains("FV007", output, StringComparison.Ordinal);
        Assert.DoesNotContain(canary, output, StringComparison.Ordinal);
        if (format == "json")
        {
            using JsonDocument report = JsonDocument.Parse(output);
            JsonElement finding = Assert.Single(report.RootElement.GetProperty("findings").EnumerateArray());
            Assert.Equal("FV007", finding.GetProperty("ruleId").GetString());
            Assert.Equal("block", finding.GetProperty("disposition").GetString());
        }
    }

    [Theory]
    [InlineData("{\"ConnectionString\":\"Server=localhost;Password=\"}", "console")]
    [InlineData("{\"ConnectionString\":\"Server=localhost;Password=\"}", "json")]
    [InlineData("{\"ConnectionString\":\"Server=localhost;Pwd=   \"}", "console")]
    [InlineData("{\"ConnectionString\":\"Server=localhost;Pwd=   \"}", "json")]
    [InlineData("{\"ConnectionString\":\"Server=localhost;Password=[redacted]\"}", "console")]
    [InlineData("{\"ConnectionString\":\"Server=localhost;Password=[redacted]\"}", "json")]
    public void Json_wrapped_empty_whitespace_and_redacted_connection_string_values_are_not_findings(
        string fixture,
        string format)
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy();
        repository.WriteText("tests/connection-string-json.golden", fixture + "\n");

        int exitCode = repository.Run(["scan", "--format", format], new RecordingTelemetry(), out string output, out string error);

        Assert.Equal(0, exitCode);
        Assert.Empty(error);
        Assert.DoesNotContain("FV007", output, StringComparison.Ordinal);
        if (format == "json")
        {
            using JsonDocument report = JsonDocument.Parse(output);
            Assert.Empty(report.RootElement.GetProperty("findings").EnumerateArray());
        }
    }

    [Theory]
    [InlineData("{\"ConnectionString\":\"Server=localhost;Password=\\\"\\\"\"}", "console", false, "")]
    [InlineData("{\"ConnectionString\":\"Server=localhost;Password=\\\"\\\"\"}", "json", false, "")]
    [InlineData("{\"ConnectionString\":\"Server=localhost;Password=\\u0022\\u0022\"}", "console", false, "")]
    [InlineData("{\"ConnectionString\":\"Server=localhost;Password=\\u0022\\u0022\"}", "json", false, "")]
    [InlineData("{\"ConnectionString\":\"Server=localhost;Password=\\\"[redacted]\\\"\"}", "console", false, "")]
    [InlineData("{\"ConnectionString\":\"Server=localhost;Password=\\\"[redacted]\\\"\"}", "json", false, "")]
    [InlineData("{\"ConnectionString\":\"Server=localhost;Password=\\u0022[redacted]\\u0022\"}", "console", false, "")]
    [InlineData("{\"ConnectionString\":\"Server=localhost;Password=\\u0022[redacted]\\u0022\"}", "json", false, "")]
    [InlineData("{\"ConnectionString\":\"Server=localhost;Password=\\\"\\\"\\\"[redacted]\\\"\\\"\\\"\"}", "console", false, "")]
    [InlineData("{\"ConnectionString\":\"Server=localhost;Password=\\\"\\\"\\\"[redacted]\\\"\\\"\\\"\"}", "json", false, "")]
    [InlineData("{\"ConnectionString\":\"Server=localhost;PWD=\\\"\\\"\"}", "console", false, "")]
    [InlineData("{\"ConnectionString\":\"Server=localhost;PWD=\\\"\\\"\"}", "json", false, "")]
    [InlineData("{\"ConnectionString\":\"Server=localhost;PWD=\\u0022\\t\\u0022\"}", "console", false, "")]
    [InlineData("{\"ConnectionString\":\"Server=localhost;PWD=\\u0022\\u0009\\u0022\"}", "json", false, "")]
    [InlineData("{\"ConnectionString\":\"Server=localhost;Password=\\\"fixture-json-secret\\\"\"}", "console", true, "fixture-json-secret")]
    [InlineData("{\"ConnectionString\":\"Server=localhost;Password=\\\"fixture-json-secret\\\"\"}", "json", true, "fixture-json-secret")]
    [InlineData("{\"ConnectionString\":\"Server=localhost;Password=\\u0022fixture-json-secret\\u0022\"}", "console", true, "fixture-json-secret")]
    [InlineData("{\"ConnectionString\":\"Server=localhost;Password=\\u0022fixture-json-secret\\u0022\"}", "json", true, "fixture-json-secret")]
    [InlineData("{\"ConnectionString\":\"Server=localhost;Password=\\\"\\\"\\\"fixture-json-doubled-secret\\\"\\\"\\\"\"}", "console", true, "fixture-json-doubled-secret")]
    [InlineData("{\"ConnectionString\":\"Server=localhost;Password=\\\"\\\"\\\"fixture-json-doubled-secret\\\"\\\"\\\"\"}", "json", true, "fixture-json-doubled-secret")]
    [InlineData("{\"ConnectionString\":\"Server=localhost;Pwd=\\\"\\\"\\\"fixture-json-doubled\\\\path\\\"\\\"\\\"\"}", "json", true, "fixture-json-doubled")]
    [InlineData("{\"ConnectionString\":\"Server=localhost;pWd=\\\"fixture-json-pwd-secret\\\"\"}", "console", true, "fixture-json-pwd-secret")]
    [InlineData("{\"ConnectionString\":\"Server=localhost;pWd=\\\"fixture-json-pwd-secret\\\"\"}", "json", true, "fixture-json-pwd-secret")]
    [InlineData("{\"ConnectionString\":\"Server=localhost;pWd=\\u0022fixture-json-pwd-secret\\u0022\"}", "console", true, "fixture-json-pwd-secret")]
    [InlineData("{\"ConnectionString\":\"Server=localhost;pWd=\\u0022fixture-json-pwd-secret\\u0022\"}", "json", true, "fixture-json-pwd-secret")]
    public void Json_escaped_quoted_connection_string_values_follow_sensitive_data_contract(
        string fixture,
        string format,
        bool expectsFinding,
        string sensitiveValue)
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy();
        repository.WriteText("tests/connection-string-json-escaped.golden", fixture + "\n");

        int exitCode = repository.Run(["scan", "--format", format], new RecordingTelemetry(), out string output, out string error);

        Assert.Equal(expectsFinding ? 1 : 0, exitCode);
        Assert.Empty(error);
        Assert.Equal(expectsFinding, output.Contains("FV007", StringComparison.Ordinal));
        if (sensitiveValue.Length > 0)
        {
            Assert.DoesNotContain(sensitiveValue, output, StringComparison.Ordinal);
        }
        if (format == "json")
        {
            using JsonDocument report = JsonDocument.Parse(output);
            Assert.Equal(expectsFinding, report.RootElement.GetProperty("findings").EnumerateArray().Any());
        }
    }

    [Theory]
    [InlineData("Password=\"\";", false)]
    [InlineData("Password=\\\"\\\";", false)]
    [InlineData("Password=\\u0022\\u0022;", false)]
    [InlineData("Password=\\u0022\\t\\u0022;", false)]
    [InlineData("Password=\\u0022\\u0009\\u0022;", false)]
    [InlineData("Password=\\u0022[redacted]\\u0022;", false)]
    [InlineData("Password=\"Canary\\Path\";", true)]
    [InlineData("Password=\\\"Canary\\\\Path\\\";", true)]
    [InlineData("Password=\\u0022Canary\\\\Path\\u0022;", true)]
    [InlineData("Password=\"\"\"Canary\\Path\"\"\";", true)]
    public void Connection_string_classifier_uses_semantic_json_escape_values(string connectionString, bool expectsFinding)
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy();
        repository.WriteText("tests/connection-string-classifier.golden", connectionString + "\n");

        int exitCode = repository.Run(["scan", "--format", "json"], new RecordingTelemetry(), out string output, out string error);

        Assert.Equal(expectsFinding ? 1 : 0, exitCode);
        Assert.Empty(error);
        Assert.Equal(expectsFinding, output.Contains("FV007", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("console")]
    [InlineData("json")]
    public void Connection_string_password_keyword_case_detection_respects_non_strict_exit_behavior(string format)
    {
        const string canary = "Canary123";
        using var repository = new TemporaryRepository();
        repository.WritePolicy(policy => policy.Ci!.Strict = false);
        repository.WriteText("tests/connection-string-nonstrict.golden", "Server=example.invalid;User Id=fixture;PWD=Canary123;\n");
        var telemetry = new RecordingTelemetry();

        int exitCode = repository.Run(["scan", "--format", format], telemetry, out string output, out string error);

        Assert.Equal(0, exitCode);
        Assert.Empty(error);
        Assert.Equal(1, telemetry.SuccessfulScans);
        Assert.Contains("FV007", output, StringComparison.Ordinal);
        Assert.DoesNotContain(canary, output, StringComparison.Ordinal);
        if (format == "json")
        {
            using JsonDocument report = JsonDocument.Parse(output);
            JsonElement finding = Assert.Single(report.RootElement.GetProperty("findings").EnumerateArray());
            Assert.Equal("FV007", finding.GetProperty("ruleId").GetString());
            Assert.Equal("warn", finding.GetProperty("disposition").GetString());
        }
    }

    [Fact]
    public void Recommended_non_verify_encoding_repair_clears_fv006_and_fve016()
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy();
        repository.WriteBytes("tests/malformed.golden", [0xFF, 0xFE, 0x6F]);

        int initialExitCode = repository.Run(
            ["scan", "--format", "json"],
            new RecordingTelemetry(),
            out string initialOutput,
            out string initialError);
        using (JsonDocument initialReport = JsonDocument.Parse(initialOutput))
        {
            Assert.Equal(2, initialExitCode);
            Assert.Empty(initialError);
            JsonElement finding = Assert.Single(initialReport.RootElement.GetProperty("findings").EnumerateArray());
            Assert.Equal("FV006", finding.GetProperty("ruleId").GetString());
            Assert.Equal("Save the fixture as valid UTF-8 text without NUL characters.", finding.GetProperty("remediation").GetString());
            Assert.Contains(
                initialReport.RootElement.GetProperty("errors").EnumerateArray(),
                item => item.GetProperty("code").GetString() == FixtureVaultContract.UninspectableContentErrorCode);
        }

        repository.WriteText("tests/malformed.golden", "repaired as utf-8\n");
        var telemetry = new RecordingTelemetry();
        int repairedExitCode = repository.Run(
            ["scan", "--format", "json"],
            telemetry,
            out string repairedOutput,
            out string repairedError);
        using JsonDocument repairedReport = JsonDocument.Parse(repairedOutput);

        Assert.Equal(0, repairedExitCode);
        Assert.Empty(repairedError);
        Assert.Equal(1, telemetry.SuccessfulScans);
        Assert.Empty(repairedReport.RootElement.GetProperty("findings").EnumerateArray());
        Assert.Empty(repairedReport.RootElement.GetProperty("errors").EnumerateArray());
        Assert.DoesNotContain(
            repairedReport.RootElement.GetProperty("skipped").EnumerateArray(),
            item => item.GetProperty("code").GetString() == FixtureVaultContract.UninspectableContentSkippedCode);
    }

    [Theory]
    [InlineData("console")]
    [InlineData("json")]
    public void Empty_and_already_redacted_api_key_query_values_are_not_sensitive_findings(string format)
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy();
        repository.WriteText(
            "tests/credentials.golden",
            "https://example.test/?api_key=&page=1\n" +
            "https://example.test/?api_key=\"  \"&page=1\n" +
            "https://example.test/?api_key='  '&page=1\n" +
            "https://example.test/?api_key=***&page=1\n" +
            "https://example.test/?api_key=<redacted>&page=1\n" +
            "https://example.test/?api_key=[REDACTED]&page=1\n");
        var telemetry = new RecordingTelemetry();

        int exitCode = repository.Run(["scan", "--format", format], telemetry, out string output, out string error);

        Assert.Equal(0, exitCode);
        Assert.Equal(1, telemetry.SuccessfulScans);
        Assert.Empty(error);
        if (format == "json")
        {
            using JsonDocument report = JsonDocument.Parse(output);
            Assert.Empty(report.RootElement.GetProperty("findings").EnumerateArray());
        }
        else
        {
            Assert.DoesNotContain("FV007", output, StringComparison.Ordinal);
            Assert.Contains("No policy-blocking findings", output, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("%20", "console")]
    [InlineData("%20", "json")]
    [InlineData("+", "console")]
    [InlineData("+", "json")]
    [InlineData("%09", "console")]
    [InlineData("%09", "json")]
    [InlineData("%0A", "console")]
    [InlineData("%0A", "json")]
    public void Url_encoded_empty_api_key_query_values_are_not_sensitive_findings(string encodedValue, string format)
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy();
        repository.WriteText("tests/encoded-empty-credentials.golden", $"https://example.test/?api_key={encodedValue}&page=1\n");
        var telemetry = new RecordingTelemetry();

        int exitCode = repository.Run(["scan", "--format", format], telemetry, out string output, out string error);

        Assert.Equal(0, exitCode);
        Assert.Equal(1, telemetry.SuccessfulScans);
        Assert.Empty(error);
        Assert.DoesNotContain("FV007", output, StringComparison.Ordinal);
        if (format == "json")
        {
            using JsonDocument report = JsonDocument.Parse(output);
            Assert.Empty(report.RootElement.GetProperty("findings").EnumerateArray());
        }
        else
        {
            Assert.Contains("No policy-blocking findings", output, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("console")]
    [InlineData("json")]
    public void Url_encoded_non_empty_api_key_query_values_remain_sensitive_without_disclosure(string format)
    {
        const string encodedValue = "encoded-secret%2Dcanary%2D1234567890";
        const string decodedValue = "encoded-secret-canary-1234567890";
        using var repository = new TemporaryRepository();
        repository.WritePolicy();
        repository.WriteText("tests/encoded-secret.golden", $"https://example.test/?api_key={encodedValue}&page=1\n");

        int exitCode = repository.Run(["scan", "--format", format], new RecordingTelemetry(), out string output, out string error);

        Assert.Equal(1, exitCode);
        Assert.Empty(error);
        Assert.Contains("FV007", output, StringComparison.Ordinal);
        Assert.DoesNotContain(encodedValue, output, StringComparison.Ordinal);
        Assert.DoesNotContain(decodedValue, output, StringComparison.Ordinal);
        if (format == "json")
        {
            using JsonDocument report = JsonDocument.Parse(output);
            JsonElement finding = Assert.Single(report.RootElement.GetProperty("findings").EnumerateArray());
            Assert.Equal("FV007", finding.GetProperty("ruleId").GetString());
            Assert.Equal("block", finding.GetProperty("disposition").GetString());
        }
    }

    [Theory]
    [InlineData("console")]
    [InlineData("json")]
    public void Non_empty_api_key_query_values_are_sensitive_without_disclosure(string format)
    {
        const string canary = "query-api-key-canary-1234567890";
        using var repository = new TemporaryRepository();
        repository.WritePolicy();
        repository.WriteText("tests/query-credentials.golden", $"https://example.test/?api_key={canary}&page=1\n");

        int exitCode = repository.Run(["scan", "--format", format], new RecordingTelemetry(), out string output, out string error);

        Assert.Equal(1, exitCode);
        Assert.Empty(error);
        Assert.DoesNotContain(canary, output, StringComparison.Ordinal);
        if (format == "json")
        {
            using JsonDocument report = JsonDocument.Parse(output);
            JsonElement finding = Assert.Single(report.RootElement.GetProperty("findings").EnumerateArray());
            Assert.Equal("FV007", finding.GetProperty("ruleId").GetString());
            Assert.Equal("block", finding.GetProperty("disposition").GetString());
        }
        else
        {
            Assert.Contains("FV007", output, StringComparison.Ordinal);
            Assert.Contains("block", output, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("console")]
    [InlineData("json")]
    public void Empty_header_and_assignment_credentials_are_not_sensitive_findings(string format)
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy();
        repository.WriteText(
            "tests/empty-credentials.golden",
            "Authorization: Bearer \n" +
            "password=\n" +
            "Authorization: Bearer ***\n" +
            "password=<redacted>\n");
        var telemetry = new RecordingTelemetry();

        int exitCode = repository.Run(["scan", "--format", format], telemetry, out string output, out string error);

        Assert.Equal(0, exitCode);
        Assert.Equal(1, telemetry.SuccessfulScans);
        Assert.Empty(error);
        if (format == "json")
        {
            using JsonDocument report = JsonDocument.Parse(output);
            Assert.Empty(report.RootElement.GetProperty("findings").EnumerateArray());
        }
        else
        {
            Assert.DoesNotContain("FV007", output, StringComparison.Ordinal);
            Assert.Contains("No policy-blocking findings", output, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("Cookie: a=; b=", "console")]
    [InlineData("Cookie: a=; b=", "json")]
    [InlineData("Cookie: session=<redacted>", "console")]
    [InlineData("Cookie: session=<redacted>", "json")]
    [InlineData("Cookie: a=***; b=***", "console")]
    [InlineData("Cookie: a=***; b=***", "json")]
    [InlineData("cookie: session=", "console")]
    [InlineData("cookie: session=", "json")]
    [InlineData("Set-Cookie: session=\"\"; Path=/", "console")]
    [InlineData("Set-Cookie: session=\"\"; Path=/", "json")]
    [InlineData(" cOoKiE : first =  ; second =  \"  \" ", "console")]
    [InlineData(" cOoKiE : first =  ; second =  \"  \" ", "json")]
    [InlineData("Cookie: session=***", "console")]
    [InlineData("Cookie: session=***", "json")]
    [InlineData("Cookie: session=[redacted]", "console")]
    [InlineData("Cookie: session=[redacted]", "json")]
    [InlineData("Cookie: session=redacted", "console")]
    [InlineData("Cookie: session=redacted", "json")]
    [InlineData("Cookie: session=masked", "console")]
    [InlineData("Cookie: session=masked", "json")]
    [InlineData("Cookie: session=removed", "console")]
    [InlineData("Cookie: session=removed", "json")]
    [InlineData("Set-Cookie: session=''; Path=/", "console")]
    [InlineData("Set-Cookie: session=''; Path=/", "json")]
    public void Empty_and_already_redacted_cookie_values_are_not_sensitive_findings(string header, string format)
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy();
        repository.WriteText("tests/credentials.golden", header + "\n");
        var telemetry = new RecordingTelemetry();

        int exitCode = repository.Run(["scan", "--format", format], telemetry, out string output, out string error);

        Assert.Equal(0, exitCode);
        Assert.Equal(1, telemetry.SuccessfulScans);
        Assert.Empty(error);
        Assert.DoesNotContain("FV007", output, StringComparison.Ordinal);
        Assert.DoesNotContain(header, output, StringComparison.Ordinal);
        if (format == "json")
        {
            using JsonDocument report = JsonDocument.Parse(output);
            Assert.Empty(report.RootElement.GetProperty("findings").EnumerateArray());
        }
        else
        {
            Assert.Contains("No policy-blocking findings", output, StringComparison.Ordinal);
        }
    }

    public static IEnumerable<object[]> QuotedPlaceholderCookieValues()
    {
        string[] placeholders = ["***", "<redacted>", "[redacted]", "redacted", "masked", "removed"];
        string[] quotedValues =
        [
            "\" {0} \"",
            "' {0} '",
            "\"  {0}\t\"",
            "'\t{0}  '",
        ];

        foreach (string headerName in new[] { "Cookie", "Set-Cookie" })
        {
            foreach (string format in new[] { "console", "json" })
            {
                foreach (string placeholder in placeholders)
                {
                    foreach (string quotedValue in quotedValues)
                    {
                        string value = string.Format(quotedValue, placeholder);
                        string[] headers = headerName == "Cookie"
                            ? [
                                $"Cookie: session={value}",
                                $"Cookie: first =  {value}  ; second={value}",
                            ]
                            : [$"Set-Cookie: session =  {value}  ; Path=/"];
                        foreach (string header in headers)
                        {
                            yield return [header, format];
                        }
                    }
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(QuotedPlaceholderCookieValues))]
    public void Quoted_placeholder_cookie_values_with_internal_whitespace_are_not_sensitive_findings(
        string header,
        string format)
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy();
        repository.WriteText("tests/credentials.golden", header + "\n");
        var telemetry = new RecordingTelemetry();

        int exitCode = repository.Run(["scan", "--format", format], telemetry, out string output, out string error);

        Assert.Equal(0, exitCode);
        Assert.Equal(1, telemetry.SuccessfulScans);
        Assert.Empty(error);
        Assert.DoesNotContain("FV007", output, StringComparison.Ordinal);
        Assert.DoesNotContain(header, output, StringComparison.Ordinal);
        if (format == "json")
        {
            using JsonDocument report = JsonDocument.Parse(output);
            Assert.Empty(report.RootElement.GetProperty("findings").EnumerateArray());
        }
        else
        {
            Assert.Contains("No policy-blocking findings", output, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("Cookie: session=fixture-cookie-secret-1234567890", "console")]
    [InlineData("Cookie: session=fixture-cookie-secret-1234567890", "json")]
    [InlineData("Cookie: empty=; session=fixture-cookie-secret-1234567890", "console")]
    [InlineData("Cookie: empty=; session=fixture-cookie-secret-1234567890", "json")]
    [InlineData("Cookie: session=\"\"; sibling=fixture-cookie-secret-1234567890", "console")]
    [InlineData("Cookie: session=\"\"; sibling=fixture-cookie-secret-1234567890", "json")]
    [InlineData("Set-Cookie: session=fixture-cookie-secret-1234567890; Path=/", "console")]
    [InlineData("Set-Cookie: session=fixture-cookie-secret-1234567890; Path=/", "json")]
    public void Cookie_headers_with_secret_values_remain_sensitive_without_disclosure(string header, string format)
    {
        const string canary = "fixture-cookie-secret-1234567890";
        using var repository = new TemporaryRepository();
        repository.WritePolicy();
        repository.WriteText("tests/credentials.golden", header + "\n");

        int exitCode = repository.Run(["scan", "--format", format], new RecordingTelemetry(), out string output, out string error);

        Assert.Equal(1, exitCode);
        Assert.Empty(error);
        Assert.Contains("FV007", output, StringComparison.Ordinal);
        Assert.DoesNotContain(canary, output, StringComparison.Ordinal);
        Assert.DoesNotContain(header, output, StringComparison.Ordinal);
        if (format == "json")
        {
            using JsonDocument report = JsonDocument.Parse(output);
            JsonElement finding = Assert.Single(report.RootElement.GetProperty("findings").EnumerateArray());
            Assert.Equal("FV007", finding.GetProperty("ruleId").GetString());
            Assert.Equal("block", finding.GetProperty("disposition").GetString());
        }
    }

    [Theory]
    [InlineData("X-Api-Key: \"\"", "console")]
    [InlineData("X-Api-Key: \"\"", "json")]
    [InlineData("ApiKey: ''", "console")]
    [InlineData("ApiKey: ''", "json")]
    [InlineData("XApiKey: \"  \"", "console")]
    [InlineData("XApiKey: \"  \"", "json")]
    [InlineData("X-API-KEY: \"\"", "console")]
    [InlineData("X-API-KEY: \"\"", "json")]
    [InlineData("x-api-key: '  '", "console")]
    [InlineData("x-api-key: '  '", "json")]
    [InlineData("aPiKeY: \"\"", "console")]
    [InlineData("aPiKeY: \"\"", "json")]
    [InlineData("Authorization: Bearer \"\"", "console")]
    [InlineData("Authorization: Bearer \"\"", "json")]
    [InlineData("authorization: bearer ''", "console")]
    [InlineData("authorization: bearer ''", "json")]
    [InlineData("AUTHORIZATION: BEARER \"  \"", "console")]
    [InlineData("AUTHORIZATION: BEARER \"  \"", "json")]
    [InlineData("Authorization: Bearer <redacted>", "console")]
    [InlineData("Authorization: Bearer <redacted>", "json")]
    [InlineData("Authorization: Bearer [REDACTED]", "console")]
    [InlineData("Authorization: Bearer [REDACTED]", "json")]
    public void Quoted_empty_credential_headers_are_not_sensitive_findings(string header, string format)
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy();
        repository.WriteText("tests/empty-api-key-header.golden", header + "\n");
        var telemetry = new RecordingTelemetry();

        int exitCode = repository.Run(["scan", "--format", format], telemetry, out string output, out string error);

        Assert.Equal(0, exitCode);
        Assert.Equal(1, telemetry.SuccessfulScans);
        Assert.Empty(error);
        Assert.DoesNotContain("FV007", output, StringComparison.Ordinal);
        if (format == "json")
        {
            using JsonDocument report = JsonDocument.Parse(output);
            Assert.Empty(report.RootElement.GetProperty("findings").EnumerateArray());
        }
        else
        {
            Assert.Contains("No policy-blocking findings", output, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("X-Api-Key", "console")]
    [InlineData("X-Api-Key", "json")]
    [InlineData("ApiKey", "console")]
    [InlineData("ApiKey", "json")]
    public void Non_empty_api_key_headers_are_sensitive_findings_without_disclosure(string headerName, string format)
    {
        const string canary = "header-api-key-canary-1234567890";
        using var repository = new TemporaryRepository();
        repository.WritePolicy();
        repository.WriteText("tests/api-key-header.golden", $"{headerName}: {canary}\n");

        int exitCode = repository.Run(["scan", "--format", format], new RecordingTelemetry(), out string output, out string error);

        Assert.Equal(1, exitCode);
        Assert.Empty(error);
        Assert.DoesNotContain(canary, output, StringComparison.Ordinal);
        if (format == "json")
        {
            using JsonDocument report = JsonDocument.Parse(output);
            JsonElement finding = Assert.Single(report.RootElement.GetProperty("findings").EnumerateArray());
            Assert.Equal("FV007", finding.GetProperty("ruleId").GetString());
            Assert.Equal("block", finding.GetProperty("disposition").GetString());
            Assert.Contains("Remove the sensitive value", finding.GetProperty("remediation").GetString(), StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("FV007", output, StringComparison.Ordinal);
            Assert.Contains("block", output, StringComparison.Ordinal);
            Assert.Contains("Remove the sensitive value", output, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("Authorization: Bearer header-bearer-canary-1234567890", "console")]
    [InlineData("Authorization: Bearer header-bearer-canary-1234567890", "json")]
    public void Non_empty_authorization_bearer_headers_are_sensitive_findings_without_disclosure(string header, string format)
    {
        const string canary = "header-bearer-canary-1234567890";
        using var repository = new TemporaryRepository();
        repository.WritePolicy();
        repository.WriteText("tests/authorization-header.golden", header + "\n");

        int exitCode = repository.Run(["scan", "--format", format], new RecordingTelemetry(), out string output, out string error);

        Assert.Equal(1, exitCode);
        Assert.Empty(error);
        Assert.DoesNotContain(canary, output, StringComparison.Ordinal);
        if (format == "json")
        {
            using JsonDocument report = JsonDocument.Parse(output);
            JsonElement finding = Assert.Single(report.RootElement.GetProperty("findings").EnumerateArray());
            Assert.Equal("FV007", finding.GetProperty("ruleId").GetString());
            Assert.Equal("block", finding.GetProperty("disposition").GetString());
            Assert.Contains("Remove the sensitive value", finding.GetProperty("remediation").GetString(), StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("FV007", output, StringComparison.Ordinal);
            Assert.Contains("block", output, StringComparison.Ordinal);
            Assert.Contains("Remove the sensitive value", output, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Sensitive_detector_failure_fails_closed_without_telemetry_or_canary_leakage()
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy();
        repository.WriteText("tests/clean.golden", "clean");
        const string canary = "detector-canary-do-not-leak-9f2c";
        var telemetry = new RecordingTelemetry();

        int exitCode = repository.Run(
            ["scan", "--format", "json"],
            telemetry,
            out string output,
            out string error,
            additionalSensitiveDataDetectors: [new ThrowingSensitiveDataDetector(canary)]);

        using JsonDocument report = JsonDocument.Parse(output);
        JsonElement errorEntry = Assert.Single(report.RootElement.GetProperty("errors").EnumerateArray());
        Assert.Equal(2, exitCode);
        Assert.Equal(0, telemetry.SuccessfulScans);
        Assert.Empty(error);
        Assert.Empty(report.RootElement.GetProperty("findings").EnumerateArray());
        Assert.Equal(FixtureVaultContract.SensitiveDataDetectorErrorCode, errorEntry.GetProperty("code").GetString());
        Assert.Equal(FixtureVaultContract.SensitiveDataDetectorErrorMessage, errorEntry.GetProperty("message").GetString());
        AssertNoCanary(canary, output, error);
        Assert.DoesNotContain(canary, errorEntry.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_sensitive_data_rule_fails_closed_instead_of_returning_a_false_clean_scan()
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy(policy => policy.SensitiveDataRules = ["high-confidance"]);
        repository.WriteText("tests/payment.golden", "{\"apiKey\":\"fixture-test-secret-1234567890\"}\n");

        int exitCode = repository.Run(["scan"], new RecordingTelemetry(), out string output, out string error);

        Assert.Equal(2, exitCode);
        Assert.Empty(output);
        Assert.Contains("sensitiveDataRules", error, StringComparison.Ordinal);
        Assert.DoesNotContain("high-confidance", error, StringComparison.Ordinal);
        Assert.Contains("Unsupported sensitiveDataRules entry", error, StringComparison.Ordinal);
        Assert.Contains("high-confidence", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Ignored_paths_are_not_audited()
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy(policy => policy.IgnoredPaths = ["TESTS\\BIN\\**"]);
        repository.WriteText("tests/bin/ignored.received.json", "secret fixture\n");

        ScanResult result = repository.Scan();

        Assert.DoesNotContain(result.Report.Findings, item => item.Path.Contains("ignored", StringComparison.Ordinal));
        Assert.Equal(0, result.Report.FilesInspected);
    }

    [Fact]
    public void Adversarial_valid_ignored_path_patterns_do_not_fail_open()
    {
        using var repository = new TemporaryRepository();
        string pattern = "tests/ignored/" + string.Concat(Enumerable.Repeat("*i", 30)) + "*n.received.json";
        string relativePath = "tests/ignored/" + new string('i', 210) + "n.received.json";
        Assert.True(GlobMatcher.TryCreate(pattern, out GlobMatcher? matcher));
        Assert.Equal(GlobMatchStatus.Match, matcher!.Match(relativePath).Status);
        repository.WritePolicy(policy => policy.IgnoredPaths = [pattern]);
        repository.WriteText(relativePath, "received\n");

        ScanResult result = repository.Scan();

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Report.Errors);
        Assert.Equal(0, result.Report.FilesInspected);
        Assert.DoesNotContain(result.Report.Findings, item => item.Path.Contains("ignored", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("*.golden", "result.golden", true)]
    [InlineData("*.golden", "nested/result.golden", false)]
    [InlineData("tests/*.golden", "tests/result.golden", true)]
    [InlineData("tests/*.golden", "tests/nested/result.golden", false)]
    [InlineData("tests/?.golden", "tests/a.golden", true)]
    [InlineData("tests/?.golden", "tests/ab.golden", false)]
    [InlineData("tests?foo.received.json", "tests/foo.received.json", false)]
    [InlineData("tests?foo.received.json", "tests\\foo.received.json", false)]
    [InlineData("tests?foo.received.json", "testsAfoo.received.json", true)]
    [InlineData("tests/??.golden", "tests/ab.golden", true)]
    [InlineData("tests/??.golden", "tests/a/b.golden", false)]
    [InlineData("tests/*/result.golden", "tests/a/result.golden", true)]
    [InlineData("tests/*/result.golden", "tests/a/b/result.golden", false)]
    [InlineData("tests/**/result.golden", "tests/result.golden", true)]
    [InlineData("tests/**/result.golden", "tests/a/b/result.golden", true)]
    [InlineData("tests/**.golden", "tests/a/result.golden", true)]
    [InlineData("tests/**", "tests", false)]
    [InlineData("tests/**", "tests/", true)]
    [InlineData("tests/**/", "tests/", true)]
    [InlineData("tests/**/", "tests/a/", true)]
    [InlineData("tests/**/", "tests/a", false)]
    [InlineData("TESTS\\BIN\\**", "tests/bin/thing.golden", true)]
    [InlineData("tests/bin/**", "TESTS\\BIN\\THING.GOLDEN", true)]
    [InlineData("tests/bin/*", "tests/bin/a/b", false)]
    [InlineData("tests/**/file?", "tests/a/file1", true)]
    [InlineData("tests/**/file?", "tests/a/file12", false)]
    [InlineData("**/bin/**", "bin/file", true)]
    public void Ignored_globs_preserve_documented_matching_semantics(
        string pattern,
        string path,
        bool expectedMatch)
    {
        Assert.True(GlobMatcher.TryCreate(pattern, out GlobMatcher? matcher));

        GlobMatchResult result = matcher!.Match(path);

        Assert.Equal(expectedMatch ? GlobMatchStatus.Match : GlobMatchStatus.NoMatch, result.Status);
    }

    [Theory]
    [InlineData("/tests/**")]
    [InlineData("\\tests\\**")]
    [InlineData("\\\\server\\share\\tests\\**")]
    [InlineData("//server/share/tests/**")]
    [InlineData("C:\\tests\\**")]
    [InlineData("C:/tests/**")]
    [InlineData("C:tests/**")]
    [InlineData("../tests/**")]
    [InlineData("..\\tests\\**")]
    [InlineData("..")]
    [InlineData("tests/../**")]
    [InlineData("tests\\..\\**")]
    public void Ignored_glob_validation_rejects_rooted_and_parent_patterns(string pattern)
    {
        Assert.False(GlobMatcher.TryCreate(pattern, out _));
    }

    [Fact]
    public void Ignored_glob_validation_rejects_nul_and_over_length_patterns()
    {
        Assert.False(GlobMatcher.TryCreate("tests/\0/**", out _));
        Assert.False(GlobMatcher.TryCreate(new string('a', 257), out _));
    }

    [Theory]
    [InlineData("/tests/**")]
    [InlineData("\\tests\\**")]
    [InlineData("\\\\server\\share\\tests\\**")]
    [InlineData("//server/share/tests/**")]
    [InlineData("C:\\tests\\**")]
    [InlineData("C:/tests/**")]
    [InlineData("C:tests/**")]
    public void Rooted_ignored_glob_patterns_fail_closed_with_fv_e007(string pattern)
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy(policy => policy.IgnoredPaths = [pattern]);
        repository.WriteText("tests/ignored.received.json", "received\n");

        ScanResult result = repository.Scan();

        Assert.Equal(2, result.ExitCode);
        Assert.False(result.Completed);
        Assert.Contains(result.Report.Errors, item => item.Code == "FV-E007");
        Assert.Empty(result.Report.Findings);
    }

    [Fact]
    public void Malformed_ignored_glob_fails_with_fv_e007()
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy(policy => policy.IgnoredPaths = ["/tests/**"]);
        repository.WriteText("tests/ignored.received.json", "received\n");

        ScanResult result = repository.Scan();

        Assert.Equal(2, result.ExitCode);
        Assert.False(result.Completed);
        Assert.Contains(result.Report.Errors, item => item.Code == "FV-E007");
        Assert.Empty(result.Report.Findings);
    }

    [Fact]
    public void Adversarial_glob_matching_reports_steps_within_its_deterministic_bound()
    {
        string pattern = "tests/ignored/" + string.Concat(Enumerable.Repeat("*i", 30)) + "*n.received.json";
        string path = "tests/ignored/" + new string('i', 210) + "n.received.json";
        Assert.True(GlobMatcher.TryCreate(pattern, out GlobMatcher? matcher));

        GlobMatchResult result = matcher!.Match(path);

        Assert.Equal(GlobMatchStatus.Match, result.Status);
        Assert.InRange(result.Steps, 1, result.WorkBound);
        Assert.Equal(
            (2L * matcher.StateCount + matcher.TokenCount) * (path.Length + 1L),
            result.WorkBound);
    }

    [Fact]
    public void Ignore_matching_budget_exhaustion_fails_closed_before_inspection()
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy(policy => policy.IgnoredPaths = ["**"]);
        repository.WriteText("tests/ignored.received.json", "received\n");

        ScanResult result = repository.Scan(matcherBudget: new GlobMatchBudget(1));

        Assert.Equal(2, result.ExitCode);
        Assert.False(result.Completed);
        Assert.Contains(result.Report.Errors, item => item.Code == "FV-E013");
        Assert.Empty(result.Report.Findings);
        Assert.Equal(0, result.Report.FilesInspected);
    }

    [Fact]
    public void Path_policy_walk_failure_fails_closed_without_a_false_fv008_result()
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy();
        repository.WriteText("outside.golden", "fixture outside roots\n");

        bool? pathPolicyWalkFailOnAccessErrors = null;
        FixtureFileWalk failingWalk = (repositoryRoot, root, failOnAccessErrors, shouldPruneDirectory) =>
        {
            if (string.Equals(root, repositoryRoot, StringComparison.Ordinal))
            {
                pathPolicyWalkFailOnAccessErrors = failOnAccessErrors;
                return new WalkResult([], [], new ScanError("FV-E002", "test-only injected walk failure"));
            }

            return SafeFileWalker.Walk(repositoryRoot, root, failOnAccessErrors, shouldPruneDirectory);
        };

        ScanResult result = repository.Scan(fileWalk: failingWalk);

        Assert.Equal(2, result.ExitCode);
        Assert.False(result.Completed);
        Assert.True(pathPolicyWalkFailOnAccessErrors);
        ScanError error = Assert.Single(result.Report.Errors);
        Assert.Equal(FixtureVaultContract.PathPolicyTraversalErrorCode, error.Code);
        Assert.Equal(FixtureVaultContract.PathPolicyTraversalErrorMessage, error.Message);
        Assert.DoesNotContain(result.Report.Findings, item => item.RuleId == "FV008");
    }

    [Fact]
    public void Inaccessible_path_policy_subtree_fails_closed_when_platform_can_create_it()
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy();
        repository.WriteText("unreadable/hidden.golden", "fixture outside roots\n");
        string inaccessibleDirectory = Path.Combine(repository.Root, "unreadable");

        if (!TryMakeDirectoryInaccessible(inaccessibleDirectory, out Action restore, out string skipReason))
        {
            throw SkipException.ForSkip(skipReason);
        }

        try
        {
            ScanResult result = repository.Scan();

            Assert.Equal(2, result.ExitCode);
            Assert.False(result.Completed);
            Assert.Contains(result.Report.Errors, item => item.Code == FixtureVaultContract.PathPolicyTraversalErrorCode);
            Assert.DoesNotContain(result.Report.Findings, item => item.RuleId == "FV008" && item.Path == "unreadable/hidden.golden");
        }
        finally
        {
            restore();
        }
    }

    [Fact]
    public void Large_ignored_directory_subtrees_are_pruned_without_weakening_entry_limit()
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy();
        repository.WriteText("tests/clean.golden", "clean\n");
        repository.WriteText("tests/OrderTests.received.json", "received\n");
        repository.WriteEmptyFiles(".git", 33_334);
        repository.WriteEmptyFiles("bin", 33_334);
        repository.WriteEmptyFiles("obj", 33_333);

        ScanResult ignored = repository.Scan();

        Assert.Equal(1, ignored.ExitCode);
        Assert.DoesNotContain(ignored.Report.Errors, item => item.Code == "FV-E003");
        Assert.Equal(2, ignored.Report.FilesInspected);
        Assert.Contains(ignored.Report.Findings, item =>
            item.RuleId == "FV001" && item.Path == "tests/OrderTests.received.json");

        repository.WritePolicy(policy => policy.IgnoredPaths = []);

        ScanResult unignored = repository.Scan();

        Assert.Equal(2, unignored.ExitCode);
        Assert.Contains(unignored.Report.Errors, item => item.Code == "FV-E003");
    }

    [Fact]
    public void Multiple_roots_and_root_override_are_enforced()
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy(policy => policy.Roots = ["tests/one", "tests/two"]);
        repository.WriteText("tests/one/one.golden", "one\n");
        repository.WriteText("tests/two/two.golden", "two\n");
        repository.WriteText("tests/three/three.golden", "three\n");

        ScanResult configured = repository.Scan();
        ScanResult overridden = repository.Scan(["--root", "tests/three"]);

        Assert.Equal(2, configured.Report.FilesInspected);
        Assert.Contains(configured.Report.Findings, item => item.RuleId == "FV008" && item.Path == "tests/three/three.golden");
        Assert.Equal(1, overridden.Report.FilesInspected);
        Assert.Contains(overridden.Report.Findings, item => item.RuleId == "FV008" && item.Path == "tests/one/one.golden");
    }

    [Fact]
    public void Root_traversal_is_an_input_error()
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy();

        ScanResult result = repository.Scan(["--root", ".."]);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains(result.Report.Errors, item => item.Code == "FV-E008");
    }

    [Fact]
    public void Root_below_an_intermediate_link_is_rejected_without_reading_the_target()
    {
        using var repository = new TemporaryRepository();
        string outsideRoot = Path.Combine(Path.GetTempPath(), "fixturevault-outside", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(outsideRoot, "fixtures"));
        File.WriteAllText(
            Path.Combine(outsideRoot, "fixtures", "outside.received.json"),
            "must not be read\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        string link = Path.Combine(repository.Root, "tests", "linked");
        try
        {
            Directory.CreateSymbolicLink(link, outsideRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            if (OperatingSystem.IsWindows())
            {
                throw SkipException.ForSkip(
                    $"Windows reparse-point capability is unavailable in this environment ({ex.GetType().Name}: {ex.Message}).");
            }

            throw;
        }

        try
        {
            repository.WritePolicy(policy => policy.Roots = ["tests/linked/fixtures"]);

            ScanResult configuredRoot = repository.Scan();
            ScanResult overrideRoot = repository.Scan(["--root", "tests/linked/fixtures"]);

            foreach (ScanResult result in new[] { configuredRoot, overrideRoot })
            {
                Assert.Equal(2, result.ExitCode);
                Assert.Contains(result.Report.Errors, item => item.Code == "FV-E008");
                Assert.Empty(result.Report.Findings);
                Assert.Equal(0, result.Report.FilesInspected);
                Assert.DoesNotContain(
                    result.Report.Skipped,
                    item => item.Path is not null && item.Path.Contains("outside", StringComparison.OrdinalIgnoreCase));
            }
        }
        finally
        {
            if (Directory.Exists(outsideRoot))
            {
                Directory.Delete(outsideRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void Malformed_policy_returns_json_error_and_not_false_clean()
    {
        using var repository = new TemporaryRepository();
        repository.WriteText(FixtureVaultContract.PolicyFileName, "{ not-json\n");
        var telemetry = new RecordingTelemetry();

        int exitCode = repository.Run(["scan", "--format", "json"], telemetry, out string output, out _);
        using JsonDocument report = JsonDocument.Parse(output);

        Assert.Equal(2, exitCode);
        Assert.Equal(1, report.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.NotEmpty(report.RootElement.GetProperty("errors").EnumerateArray());
        Assert.Equal(0, telemetry.SuccessfulScans);
    }

    [Theory]
    [InlineData("null", "")]
    [InlineData("{}", "")]
    [InlineData("{\"strcit\": true}", "strcit")]
    [InlineData("{\"Strict\": true}", "Strict")]
    [InlineData("{\"strict\": \"yes\"}", "yes")]
    public void Missing_or_malformed_ci_strict_fails_closed(string ciJson, string rawMemberName)
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicyWithCi(ciJson);
        repository.WriteText("tests/OrderTests.received.json", "received\n");
        var telemetry = new RecordingTelemetry();

        int exitCode = repository.Run(["scan", "--format", "json"], telemetry, out string output, out string error);
        using JsonDocument report = JsonDocument.Parse(output);

        Assert.Equal(2, exitCode);
        Assert.Contains(report.RootElement.GetProperty("errors").EnumerateArray(), item =>
            item.GetProperty("code").GetString() == "FV-E005");
        Assert.Contains("ci.strict", output, StringComparison.Ordinal);
        Assert.Empty(report.RootElement.GetProperty("findings").EnumerateArray());
        Assert.Empty(error);
        Assert.Equal(0, telemetry.SuccessfulScans);
        Assert.DoesNotContain(ciJson, output, StringComparison.Ordinal);
        if (rawMemberName.Length > 0)
        {
            Assert.DoesNotContain(rawMemberName, output, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(false, 0, "warn")]
    [InlineData(true, 1, "block")]
    public void Explicit_ci_strict_values_preserve_documented_finding_behavior(
        bool strict,
        int expectedExitCode,
        string expectedDisposition)
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy(policy => policy.Ci!.Strict = strict);
        repository.WriteText("tests/OrderTests.received.json", "received\n");

        int exitCode = repository.Run(["scan", "--format", "json"], new RecordingTelemetry(), out string output, out string error);
        using JsonDocument report = JsonDocument.Parse(output);

        Assert.Equal(expectedExitCode, exitCode);
        Assert.Empty(error);
        Assert.Equal(expectedDisposition, report.RootElement.GetProperty("findings")[0].GetProperty("disposition").GetString());
    }

    [Fact]
    public void Console_and_json_reports_contain_the_same_findings()
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy();
        repository.WriteText("tests/OrderTests.received.json", "received\n");

        int consoleExit = repository.Run(["scan"], new RecordingTelemetry(), out string console, out _);
        int jsonExit = repository.Run(["scan", "--format", "json"], new RecordingTelemetry(), out string json, out _);
        using JsonDocument report = JsonDocument.Parse(json);

        Assert.Equal(consoleExit, jsonExit);
        Assert.Contains("FV001", console, StringComparison.Ordinal);
        Assert.Contains("tests/OrderTests.received.json", console, StringComparison.Ordinal);
        Assert.Equal("FV001", report.RootElement.GetProperty("findings")[0].GetProperty("ruleId").GetString());
    }

    [Fact]
    public void Non_strict_policy_warns_and_strict_override_blocks()
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy(policy => policy.Ci!.Strict = false);
        repository.WriteText("tests/OrderTests.received.json", "received");

        int warningExitCode = repository.Run(
            ["scan", "--format", "json"],
            new RecordingTelemetry(),
            out string warningOutput,
            out string warningError);
        int strictExitCode = repository.Run(
            ["scan", "--format", "json", "--strict"],
            new RecordingTelemetry(),
            out string strictOutput,
            out string strictError);
        using JsonDocument warningReport = JsonDocument.Parse(warningOutput);
        using JsonDocument strictReport = JsonDocument.Parse(strictOutput);

        Assert.Equal(0, warningExitCode);
        Assert.Equal("warn", warningReport.RootElement.GetProperty("findings")[0].GetProperty("disposition").GetString());
        Assert.Equal(1, strictExitCode);
        Assert.Equal("block", strictReport.RootElement.GetProperty("findings")[0].GetProperty("disposition").GetString());
        Assert.Empty(warningError);
        Assert.Empty(strictError);
    }

    [Fact]
    public void Telemetry_is_requested_only_after_a_completed_scan()
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy();
        repository.WriteText("tests/clean.golden", "clean\n");
        var telemetry = new RecordingTelemetry();

        Assert.Equal(0, repository.Run(["init"], telemetry, out _, out _));
        Assert.Equal(0, telemetry.SuccessfulScans);
        Assert.Equal(0, repository.Run(["scan"], telemetry, out _, out _));
        Assert.Equal(1, telemetry.SuccessfulScans);

        repository.WriteText(FixtureVaultContract.PolicyFileName, "invalid");
        Assert.Equal(2, repository.Run(["scan"], telemetry, out _, out _));
        Assert.Equal(1, telemetry.SuccessfulScans);
    }

    [Fact]
    public void Scan_does_not_mutate_fixture_tree()
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy();
        repository.WriteText("tests/one.golden", "one\n");
        repository.WriteText("tests/two.received.json", "two\n");
        repository.WriteBytes("tests/three.golden", [0xFF, 0xFE]);
        var before = repository.HashTree();

        _ = repository.Scan();

        Assert.Equal(before, repository.HashTree());
    }

    [Fact]
    public void Reparse_points_are_skipped_without_reading_the_target_when_supported()
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy();
        repository.WriteText("outside.golden", "outside\n");
        string link = Path.Combine(repository.Root, "tests", "linked");
        try
        {
            Directory.CreateSymbolicLink(link, Path.Combine(repository.Root, ".."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            if (OperatingSystem.IsWindows())
            {
                throw SkipException.ForSkip(
                    $"Windows reparse-point capability is unavailable in this environment ({ex.GetType().Name}: {ex.Message}).");
            }

            throw;
        }

        ScanResult result = repository.Scan();

        Assert.DoesNotContain(result.Report.Findings, item => item.Path.Contains("linked", StringComparison.Ordinal));
        Assert.Contains(result.Report.Skipped, item => item.Code == "FV-SKIP-REPARSE");
    }

    [Fact]
    public void Unsupported_convention_is_reported_as_skipped()
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy(policy => policy.Conventions = ["future-framework"]);
        repository.WriteText("tests/example.golden", "example\n");

        ScanResult result = repository.Scan();

        Assert.Contains(result.Report.Skipped, item => item.Code == "FV-SKIP-CONVENTION");
    }

    [Fact]
    public void Committed_v1_policy_fixture_is_read_and_serialized_deterministically()
    {
        string golden = ReadCompatibilityFixture(FixtureVaultContract.PolicyFileName);
        FixtureVaultPolicy policy = JsonSerializer.Deserialize<FixtureVaultPolicy>(golden, FixtureVaultContract.JsonOptions)!;

        Assert.True(PolicyLoader.TryValidate(policy, out string? validationError), validationError);
        Assert.Equal(golden, FixtureVaultContract.SerializePolicy(policy));
    }

    [Fact]
    public void Committed_v1_manifest_fixture_is_read_by_the_manifest_loader()
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy(policy => policy.Conventions = ["generic", "fixturevault-manifest"]);
        repository.WriteText("tests/Orders/Create.verified.json", "{\"id\":1}\n");
        repository.WriteText(FixtureVaultContract.ManifestFileName, ReadCompatibilityFixture(FixtureVaultContract.ManifestFileName));

        ScanResult result = repository.Scan();

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Report.Errors);
        Assert.Equal(1, result.Report.FilesInspected);
    }

    [Fact]
    public void Committed_v1_report_fixture_round_trips_without_serialization_drift()
    {
        string golden = ReadCompatibilityFixture("report-v1.json");
        ScanReport report = JsonSerializer.Deserialize<ScanReport>(golden, FixtureVaultContract.JsonOptions)!;
        string serialized = report.ToJson();
        ScanReport roundTripped = JsonSerializer.Deserialize<ScanReport>(serialized, FixtureVaultContract.JsonOptions)!;

        Assert.Equal(1, report.SchemaVersion);
        Assert.Equal("0.1.0", report.ToolVersion);
        Assert.Equal(serialized, roundTripped.ToJson());
    }

    private sealed class RecordingTelemetry : IUsageTelemetry
    {
        internal int SuccessfulScans { get; private set; }

        public void RecordSuccessfulScan()
        {
            SuccessfulScans++;
        }
    }

    private sealed class ThrowingSensitiveDataDetector(string canary) : ISensitiveDataDetector
    {
        public bool IsSensitive(string text) => throw new InvalidOperationException(canary);
    }

    private sealed class TemporaryRepository : IDisposable
    {
        internal TemporaryRepository(bool createTestsDirectory = true)
        {
            Root = Path.Combine(Path.GetTempPath(), "fixturevault-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            if (createTestsDirectory)
            {
                Directory.CreateDirectory(Path.Combine(Root, "tests"));
            }
        }

        internal string Root { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }

        internal void WritePolicy(Action<FixtureVaultPolicy>? customize = null)
        {
            FixtureVaultPolicy policy = FixtureVaultPolicy.CreateDefault(testsDirectoryExists: true);
            customize?.Invoke(policy);
            WriteText(FixtureVaultContract.PolicyFileName, FixtureVaultContract.SerializePolicy(policy));
        }

        internal void WritePolicyWithCi(string ciJson)
        {
            FixtureVaultPolicy policy = FixtureVaultPolicy.CreateDefault(testsDirectoryExists: true);
            string json = FixtureVaultContract.SerializePolicy(policy);
            const string defaultCiJson = "  \"ci\": {\n    \"strict\": true\n  }";
            Assert.Contains(defaultCiJson, json, StringComparison.Ordinal);
            WriteText(
                FixtureVaultContract.PolicyFileName,
                json.Replace(defaultCiJson, $"  \"ci\": {ciJson}", StringComparison.Ordinal));
        }

        internal void WriteText(string relativePath, string content)
        {
            string path = GetPath(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        internal void WriteBytes(string relativePath, byte[] bytes)
        {
            string path = GetPath(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }

        internal void WriteEmptyFiles(string relativeDirectory, int count)
        {
            string directory = GetPath(relativeDirectory);
            Directory.CreateDirectory(directory);
            for (int index = 0; index < count; index++)
            {
                File.WriteAllBytes(Path.Combine(directory, $"entry-{index:D6}"), []);
            }
        }

        internal ScanResult Scan(
            IReadOnlyList<string>? options = null,
            GlobMatchBudget? matcherBudget = null,
            IReadOnlyList<ISensitiveDataDetector>? additionalSensitiveDataDetectors = null,
            FixtureFileWalk? fileWalk = null,
            Action? afterFixtureInitialLengthRead = null)
        {
            PolicyLoadResult policy = PolicyLoader.Load(Root);
            Assert.Null(policy.Error);
            var roots = new List<string>();
            for (int index = 0; index < (options?.Count ?? 0); index++)
            {
                if (options![index] == "--root" && index + 1 < options.Count)
                {
                    roots.Add(options[++index]);
                }
            }

            return FixtureScanner.Scan(
                Root,
                policy.Policy!,
                roots,
                strictOverride: options?.Contains("--strict") == true,
                matcherBudget: matcherBudget,
                additionalSensitiveDataDetectors: additionalSensitiveDataDetectors,
                fileWalk: fileWalk,
                afterFixtureInitialLengthRead: afterFixtureInitialLengthRead);
        }

        internal int Run(
            string[] args,
            IUsageTelemetry telemetry,
            out string output,
            out string error,
            IReadOnlyList<ISensitiveDataDetector>? additionalSensitiveDataDetectors = null,
            FixtureFileWalk? fileWalk = null,
            Action? afterFixtureInitialLengthRead = null)
        {
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            int exitCode = FixtureVaultApplication.Run(
                args,
                Root,
                telemetry,
                stdout,
                stderr,
                additionalSensitiveDataDetectors,
                fileWalk,
                afterFixtureInitialLengthRead);
            output = stdout.ToString();
            error = stderr.ToString();
            return exitCode;
        }

        internal IReadOnlyDictionary<string, string> HashTree()
        {
            return Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)
                .Select(path => (FullPath: path, RelativePath: PathUtilities.NormalizeRelative(Root, path)))
                .Where(item => !item.RelativePath.Equals(FixtureVaultContract.PolicyFileName, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(
                    item => item.RelativePath,
                    item => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(item.FullPath))),
                    StringComparer.Ordinal);
        }

        private string GetPath(string relativePath) => Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    private sealed class GitBoundaryRepository : IDisposable
    {
        internal GitBoundaryRepository(bool gitMarkerIsFile)
        {
            ParentRoot = Path.Combine(Path.GetTempPath(), "fixturevault-git-boundary-tests", Guid.NewGuid().ToString("N"));
            RepositoryRoot = Path.Combine(ParentRoot, "repository");
            Directory.CreateDirectory(Path.Combine(RepositoryRoot, "tests"));

            string gitMarker = Path.Combine(RepositoryRoot, ".git");
            if (gitMarkerIsFile)
            {
                File.WriteAllText(gitMarker, "gitdir: synthetic\n");
            }
            else
            {
                Directory.CreateDirectory(gitMarker);
            }
        }

        internal string ParentRoot { get; }

        internal string RepositoryRoot { get; }

        public void Dispose()
        {
            if (Directory.Exists(ParentRoot))
            {
                Directory.Delete(ParentRoot, recursive: true);
            }
        }

        internal void WriteParentPolicy()
        {
            FixtureVaultPolicy policy = FixtureVaultPolicy.CreateDefault(testsDirectoryExists: false);
            policy.Roots = ["sibling"];
            WriteText(ParentRoot, FixtureVaultContract.PolicyFileName, FixtureVaultContract.SerializePolicy(policy));
        }

        internal void WritePolicyInRepository()
        {
            FixtureVaultPolicy policy = FixtureVaultPolicy.CreateDefault(testsDirectoryExists: true);
            WriteText(RepositoryRoot, FixtureVaultContract.PolicyFileName, FixtureVaultContract.SerializePolicy(policy));
        }

        internal void WriteParentText(string relativePath, string content) => WriteText(ParentRoot, relativePath, content);

        internal void WriteRepositoryText(string relativePath, string content) => WriteText(RepositoryRoot, relativePath, content);

        internal int RunFromNestedDirectory(string[] args, out string output, out string error)
        {
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            int exitCode = FixtureVaultApplication.Run(
                args,
                Path.Combine(RepositoryRoot, "tests"),
                new RecordingTelemetry(),
                stdout,
                stderr);
            output = stdout.ToString();
            error = stderr.ToString();
            return exitCode;
        }

        private static void WriteText(string root, string relativePath, string content)
        {
            string path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }

    private static void CreateSymbolicFileOrSkip(string linkPath, string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            if (OperatingSystem.IsWindows())
            {
                throw SkipException.ForSkip(
                    $"Windows symbolic-link capability is unavailable in this environment ({ex.GetType().Name}: {ex.Message}).");
            }

            throw;
        }
    }

    [Fact]
    public void Unix_fifo_fixture_is_rejected_without_blocking_the_scan()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        using var repository = new TemporaryRepository();
        Assert.Equal(0, repository.Run(["init"], new RecordingTelemetry(), out _, out _));
        string fifoPath = Path.Combine(repository.Root, "tests", "hang.golden");
        using Process mkfifo = StartProcessOrSkip("mkfifo", fifoPath);
        Assert.True(mkfifo.WaitForExit(5_000));
        Assert.Equal(0, mkfifo.ExitCode);

        using var scan = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Environment.ProcessPath!,
                WorkingDirectory = repository.Root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };
        scan.StartInfo.ArgumentList.Add(typeof(FixtureVaultApplication).Assembly.Location);
        scan.StartInfo.ArgumentList.Add("scan");
        scan.StartInfo.ArgumentList.Add("--format");
        scan.StartInfo.ArgumentList.Add("json");
        Assert.True(scan.Start());
        bool exited = scan.WaitForExit(5_000);
        if (!exited)
        {
            scan.Kill(entireProcessTree: true);
        }

        Assert.True(exited, "The scan must reject a FIFO before opening it for a blocking read.");
        string output = scan.StandardOutput.ReadToEnd();
        Assert.Equal(2, scan.ExitCode);
        using JsonDocument report = JsonDocument.Parse(output);
        Assert.Contains(report.RootElement.GetProperty("errors").EnumerateArray(), item => item.GetProperty("code").GetString() == "FV-E009");
    }

    [Fact]
    public void Human_diagnostics_escape_control_character_paths_without_changing_json_paths()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        string dangerousFindingPath = "tests/bad\n\r\u001b[31m.received.json";
        using (var repository = new TemporaryRepository())
        {
            repository.WritePolicy();
            repository.WriteText(dangerousFindingPath, "received\n");

            int consoleExit = repository.Run(["scan"], new RecordingTelemetry(), out string console, out string consoleError);
            int jsonExit = repository.Run(["scan", "--format", "json"], new RecordingTelemetry(), out string json, out string jsonError);

            Assert.Equal(1, consoleExit);
            Assert.Equal(1, jsonExit);
            Assert.Empty(consoleError);
            Assert.Empty(jsonError);
            string escaped = "tests/bad\\n\\r\\x1B[31m.received.json";
            Assert.Contains(escaped, console, StringComparison.Ordinal);
            Assert.DoesNotContain(dangerousFindingPath, console, StringComparison.Ordinal);
            using JsonDocument report = JsonDocument.Parse(json);
            Assert.Equal(dangerousFindingPath, Assert.Single(report.RootElement.GetProperty("findings").EnumerateArray()).GetProperty("path").GetString());
        }

        string dangerousErrorPath = "tests/error\n\r\u001b[2J.received.json";
        using (var repository = new TemporaryRepository())
        {
            repository.WritePolicy();
            repository.WriteText(dangerousErrorPath, "received\n");
            string fifoPath = Path.Combine(repository.Root, "tests", "error.golden");
            using Process mkfifo = StartProcessOrSkip("mkfifo", fifoPath);
            Assert.True(mkfifo.WaitForExit(5_000));
            Assert.Equal(0, mkfifo.ExitCode);

            int exitCode = repository.Run(["scan"], new RecordingTelemetry(), out string output, out string error);

            Assert.Equal(2, exitCode);
            Assert.Empty(output);
            Assert.Contains("tests/error\\n\\r\\x1B[2J.received.json", error, StringComparison.Ordinal);
            Assert.DoesNotContain(dangerousErrorPath, error, StringComparison.Ordinal);
        }

        string dangerousSkippedPath = "tests/skip\n\r\u001b[8m.golden";
        using (var repository = new TemporaryRepository())
        {
            repository.WritePolicy();
            string outside = Path.Combine(Path.GetTempPath(), "fixturevault-skipped-path", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
            File.WriteAllText(outside, "outside\n");
            try
            {
                CreateSymbolicFileOrSkip(Path.Combine(repository.Root, dangerousSkippedPath.Replace('/', Path.DirectorySeparatorChar)), outside);
                int exitCode = repository.Run(["scan"], new RecordingTelemetry(), out string output, out string error);

                Assert.Equal(0, exitCode);
                Assert.Empty(error);
                Assert.Contains("tests/skip\\n\\r\\x1B[8m.golden", output, StringComparison.Ordinal);
            }
            finally
            {
                File.Delete(outside);
                Directory.Delete(Path.GetDirectoryName(outside)!, recursive: true);
            }
        }
    }

    private static void CreateSymbolicDirectoryOrSkip(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            if (OperatingSystem.IsWindows())
            {
                throw SkipException.ForSkip(
                    $"Windows symbolic-link capability is unavailable in this environment ({ex.GetType().Name}: {ex.Message}).");
            }

            throw;
        }
    }

    private static byte[] DeclaredEncodingFixtureBytes(
        string declaredEncoding,
        string contentShape,
        bool withSensitiveValue)
    {
        if (contentShape == "undecodable")
        {
            // Valid byte-order marks followed by bytes no supported encoding of that kind decodes.
            byte[] sensitiveBytes = withSensitiveValue
                ? Encoding.UTF8.GetBytes(SensitiveValue)
                : [];
            return declaredEncoding switch
            {
                "none" => [0xC3, 0x28, .. sensitiveBytes],
                "utf-8" => [.. Encoding.UTF8.GetPreamble(), 0xC3, 0x28, .. sensitiveBytes],
                "utf-16le" => [0xFF, 0xFE, 0x00, 0xD8, .. sensitiveBytes],
                "utf-16be" => [0xFE, 0xFF, 0xD8, 0x00, .. sensitiveBytes],
                "utf-32le" => [0xFF, 0xFE, 0x00, 0x00, 0x00, 0x00, 0x11, 0x00, .. sensitiveBytes],
                "utf-32be" => [0x00, 0x00, 0xFE, 0xFF, 0x00, 0x11, 0x00, 0x00, .. sensitiveBytes],
                _ => throw new ArgumentOutOfRangeException(nameof(declaredEncoding))
            };
        }

        string text = withSensitiveValue
            ? $"{{\"apiKey\":\"{SensitiveValue}\"}}\n"
            : "{\"orderId\":\"1\"}\n";
        if (contentShape == "nul")
        {
            text += "\0";
        }

        return declaredEncoding switch
        {
            "none" => Encoding.UTF8.GetBytes(text),
            "utf-8" => Utf8Bom(text),
            "utf-16le" or "utf-16be" or "utf-32le" or "utf-32be" => EncodeWithDeclaredBom(declaredEncoding, text),
            _ => throw new ArgumentOutOfRangeException(nameof(declaredEncoding))
        };
    }

    private static void AssertSensitiveValueIsPlanted(
        byte[] fixtureBytes,
        string contentShape,
        bool withSensitiveValue)
    {
        if (contentShape == "undecodable" && withSensitiveValue)
        {
            byte[] sensitiveBytes = Encoding.UTF8.GetBytes(SensitiveValue);
            Assert.True(
                fixtureBytes.AsSpan().IndexOf(sensitiveBytes) >= 0,
                "The undecodable sensitive fixture must contain the synthetic sensitive-value bytes.");
        }
    }

    private static string DeclaredEncodingName(string declaredEncoding) => declaredEncoding switch
    {
        "utf-8" => ContentClassification.Utf8EncodingName,
        "utf-16le" => ContentClassification.Utf16LittleEndianEncodingName,
        "utf-16be" => ContentClassification.Utf16BigEndianEncodingName,
        "utf-32le" => ContentClassification.Utf32LittleEndianEncodingName,
        "utf-32be" => ContentClassification.Utf32BigEndianEncodingName,
        _ => throw new ArgumentOutOfRangeException(nameof(declaredEncoding))
    };

    private static string ExpectedSkipReason(string declaredEncoding, string contentShape)
    {
        if (declaredEncoding == "none")
        {
            return contentShape == "nul"
                ? FixtureVaultContract.UndeclaredNulContentSkippedReason
                : FixtureVaultContract.UninspectableContentSkippedReason;
        }

        string name = DeclaredEncodingName(declaredEncoding);
        return contentShape == "nul"
            ? FixtureVaultContract.DeclaredNulContentSkippedReason(name)
            : FixtureVaultContract.UndecodableDeclaredContentSkippedReason(name);
    }

    private static string ReadCompatibilityFixture(string fileName)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "fixtures", "v1", fileName);
        return File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static byte[] Utf8Bom(string text) =>
        [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text)];

    private static byte[] EncodeWithDeclaredBom(string encodingName, string text)
    {
        Encoding encoding = encodingName switch
        {
            "utf-16le" => Encoding.Unicode,
            "utf-16be" => Encoding.BigEndianUnicode,
            "utf-32le" => Encoding.UTF32,
            "utf-32be" => new UTF32Encoding(bigEndian: true, byteOrderMark: false),
            _ => throw new ArgumentOutOfRangeException(nameof(encodingName))
        };
        byte[] preamble = encodingName switch
        {
            "utf-16le" => [0xFF, 0xFE],
            "utf-16be" => [0xFE, 0xFF],
            "utf-32le" => [0xFF, 0xFE, 0x00, 0x00],
            "utf-32be" => [0x00, 0x00, 0xFE, 0xFF],
            _ => throw new ArgumentOutOfRangeException(nameof(encodingName))
        };
        return [.. preamble, .. encoding.GetBytes(text)];
    }

    private static byte[] PngBytes() =>
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0xFF];

    private static byte[] EncodeWithoutBom(string encodingName, string text)
    {
        Encoding encoding = encodingName switch
        {
            "utf-16le" => new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true),
            "utf-16be" => new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true),
            "utf-32le" => new UTF32Encoding(bigEndian: false, byteOrderMark: false),
            "utf-32be" => new UTF32Encoding(bigEndian: true, byteOrderMark: false),
            _ => throw new ArgumentOutOfRangeException(nameof(encodingName))
        };
        return encoding.GetBytes(text);
    }

    private static bool TryMakeDirectoryInaccessible(
        string path,
        out Action restore,
        out string skipReason)
    {
        restore = static () => { };
        skipReason = "The test platform could not create a reliably inaccessible directory.";

        if (OperatingSystem.IsWindows())
        {
            string? sid;
            try
            {
                sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
            }
            catch (Exception ex) when (ex is PlatformNotSupportedException or InvalidOperationException)
            {
                skipReason = $"Windows identity capability is unavailable ({ex.GetType().Name}).";
                return false;
            }

            if (string.IsNullOrEmpty(sid) ||
                !RunIcacls(path, "/inheritance:r") ||
                !RunIcacls(path, "/deny", $"*{sid}:(OI)(CI)(RX)"))
            {
                skipReason = "icacls could not install a deny ACL for the current test identity.";
                return false;
            }

            restore = () =>
            {
                _ = RunIcacls(path, "/remove:d", $"*{sid}");
                _ = RunIcacls(path, "/reset", "/T", "/C");
            };

            try
            {
                _ = Directory.GetFileSystemEntries(path);
                restore();
                skipReason = "The installed Windows deny ACL did not prevent directory enumeration.";
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
            catch (IOException)
            {
                restore();
                skipReason = "The Windows ACL test directory could not be enumerated in a stable denied state.";
                return false;
            }
        }

        return TryMakePosixDirectoryInaccessible(path, out restore, out skipReason);
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static bool TryMakePosixDirectoryInaccessible(
        string path,
        out Action restore,
        out string skipReason)
    {
        restore = static () => { };
        skipReason = "The test platform could not create a reliably inaccessible directory.";
        try
        {
            UnixFileMode originalMode = File.GetUnixFileMode(path);
            File.SetUnixFileMode(path, UnixFileMode.None);
            restore = () => File.SetUnixFileMode(path, originalMode);
            try
            {
                _ = Directory.GetFileSystemEntries(path);
                restore();
                skipReason = "chmod 000 did not prevent directory enumeration for this test identity.";
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
            catch (IOException)
            {
                restore();
                skipReason = "The chmod 000 test directory could not be enumerated in a stable denied state.";
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            skipReason = $"POSIX permission capability is unavailable ({ex.GetType().Name}).";
            return false;
        }
    }

    private static bool RunIcacls(string path, params string[] arguments)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "icacls.exe",
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.StartInfo.ArgumentList.Add(path);
            foreach (string argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            if (!process.Start() || !process.WaitForExit(10_000))
            {
                return false;
            }

            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static Process StartProcessOrSkip(string fileName, params string[] arguments)
    {
        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            foreach (string argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            if (!process.Start())
            {
                throw SkipException.ForSkip($"The {fileName} test helper could not be started.");
            }

            return process;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw SkipException.ForSkip($"The {fileName} test helper is unavailable ({ex.Message}).");
        }
    }

    private static void AssertNoCanary(string canary, string output, string error)
    {
        Assert.DoesNotContain(canary, output, StringComparison.Ordinal);
        Assert.DoesNotContain(canary, error, StringComparison.Ordinal);
    }
}

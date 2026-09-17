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
    public void Binary_assets_are_not_decoded()
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy();
        repository.WriteBytes("tests/image.golden", [0x89, 0x50, 0x4E, 0x47, 0x00, 0x01]);

        ScanResult result = repository.Scan();

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
            FixtureFileWalk? fileWalk = null)
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
                fileWalk: fileWalk);
        }

        internal int Run(
            string[] args,
            IUsageTelemetry telemetry,
            out string output,
            out string error,
            IReadOnlyList<ISensitiveDataDetector>? additionalSensitiveDataDetectors = null,
            FixtureFileWalk? fileWalk = null)
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
                fileWalk);
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

    private static void AssertNoCanary(string canary, string output, string error)
    {
        Assert.DoesNotContain(canary, output, StringComparison.Ordinal);
        Assert.DoesNotContain(canary, error, StringComparison.Ordinal);
    }
}

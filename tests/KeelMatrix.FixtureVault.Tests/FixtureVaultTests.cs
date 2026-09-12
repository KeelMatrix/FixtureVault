using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;
using Xunit.Sdk;

namespace KeelMatrix.FixtureVault.Tests;

public sealed class FixtureVaultTests
{
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
        Assert.Contains("Unknown option '--unknown'", error, StringComparison.Ordinal);
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

    [Theory]
    [InlineData("mismatch")]
    [InlineData("__mismatch__")]
    public void Snapshooter_mismatch_artifacts_are_blocking_findings(string mismatchDirectory)
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy();
        repository.WriteText($"tests/Orders/__snapshots__/{mismatchDirectory}/OrderTests.snap", "{\"id\":2}");

        ScanResult result = repository.Scan();

        Assert.Equal(1, result.ExitCode);
        Finding finding = Assert.Single(result.Report.Findings, item => item.RuleId == "FV001");
        Assert.Equal($"tests/Orders/__snapshots__/{mismatchDirectory}/OrderTests.snap", finding.Path);
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
    public void Invalid_encoding_and_verify_newlines_are_detected_deterministically()
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy();
        repository.WriteBytes("tests/bom.golden", [0xEF, 0xBB, 0xBF, 0x6F, 0x6B, 0x0A]);
        repository.WriteBytes("tests/verify.verified.json", [0xEF, 0xBB, 0xBF, 0x6F, 0x6E, 0x65, 0x0D, 0x0A, 0x74, 0x77, 0x6F]);
        repository.WriteBytes("tests/utf16.golden", [0xFF, 0xFE, 0x6F, 0x00, 0x6B, 0x00]);

        ScanResult result = repository.Scan();

        Assert.Equal(2, result.Report.Findings.Count(item => item.RuleId == "FV006"));
        Assert.DoesNotContain(result.Report.Findings, item => item.Path == "tests/bom.golden");
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
    public void Unknown_sensitive_data_rule_fails_closed_instead_of_returning_a_false_clean_scan()
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy(policy => policy.SensitiveDataRules = ["high-confidance"]);
        repository.WriteText("tests/payment.golden", "{\"apiKey\":\"fixture-test-secret-1234567890\"}\n");

        int exitCode = repository.Run(["scan"], new RecordingTelemetry(), out string output, out string error);

        Assert.Equal(2, exitCode);
        Assert.Empty(output);
        Assert.Contains("sensitiveDataRules", error, StringComparison.Ordinal);
        Assert.Contains("high-confidance", error, StringComparison.Ordinal);
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

        ScanResult warning = repository.Scan();
        ScanResult strict = repository.Scan(["--strict"]);

        Assert.Equal(0, warning.ExitCode);
        Assert.Equal("warn", Assert.Single(warning.Report.Findings).Disposition);
        Assert.Equal(1, strict.ExitCode);
        Assert.Equal("block", Assert.Single(strict.Report.Findings).Disposition);
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

        internal ScanResult Scan(IReadOnlyList<string>? options = null)
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

            return FixtureScanner.Scan(Root, policy.Policy!, roots, strictOverride: options?.Contains("--strict") == true);
        }

        internal int Run(
            string[] args,
            IUsageTelemetry telemetry,
            out string output,
            out string error)
        {
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            int exitCode = FixtureVaultApplication.Run(args, Root, telemetry, stdout, stderr);
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

    private static string ReadCompatibilityFixture(string fileName)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "fixtures", "v1", fileName);
        return File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static byte[] Utf8Bom(string text) =>
        [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text)];

    private static byte[] PngBytes() =>
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0xFF];
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

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

        Finding finding = Assert.Single(result.Report.Findings, item => item.RuleId == "FV002");
        Assert.Equal("tests/orphan.golden", finding.Path);
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
        repository.WriteBytes("tests/image.png", [0x89, 0x50, 0x4E, 0x47, 0x00, 0x01]);

        ScanResult result = repository.Scan();

        Assert.Contains(result.Report.Findings, item => item.RuleId == "FV005");
    }

    [Fact]
    public void Invalid_encoding_bom_and_crlf_are_detected_deterministically()
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy();
        repository.WriteBytes("tests/bom.golden", [0xEF, 0xBB, 0xBF, 0x6F, 0x6B, 0x0A]);
        repository.WriteBytes("tests/crlf.golden", Encoding.UTF8.GetBytes("one\r\ntwo\r\n"));
        repository.WriteBytes("tests/utf16.golden", [0xFF, 0xFE, 0x6F, 0x00, 0x6B, 0x00]);

        ScanResult result = repository.Scan();

        Assert.Equal(3, result.Report.Findings.Count(item => item.RuleId == "FV006"));
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
    public void Ignored_paths_are_not_audited()
    {
        using var repository = new TemporaryRepository();
        repository.WritePolicy();
        repository.WriteText("tests/bin/ignored.received.json", "secret fixture\n");

        ScanResult result = repository.Scan();

        Assert.DoesNotContain(result.Report.Findings, item => item.Path.Contains("ignored", StringComparison.Ordinal));
        Assert.Equal(0, result.Report.FilesInspected);
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
        repository.WriteText("tests/OrderTests.received.json", "received\n");

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
            return;
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
        internal TemporaryRepository()
        {
            Root = Path.Combine(Path.GetTempPath(), "fixturevault-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(Root, "tests"));
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
            WriteText(FixtureVaultContract.PolicyFileName, JsonSerializer.Serialize(policy, FixtureVaultContract.JsonOptions));
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
}

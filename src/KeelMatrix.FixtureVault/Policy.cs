using System.Text;
using System.Text.Json;

namespace KeelMatrix.FixtureVault;

internal sealed record PolicyLoadResult(FixtureVaultPolicy? Policy, ScanError? Error);

internal static class PolicyLoader
{
    internal static PolicyLoadResult Load(string repositoryRoot)
    {
        string path = Path.Combine(repositoryRoot, FixtureVaultContract.PolicyFileName);
        if (!File.Exists(path))
        {
            return new PolicyLoadResult(null, new ScanError(
                "FV-E001",
                $"{FixtureVaultContract.PolicyFileName} was not found. Run 'fixturevault init' first."));
        }

        try
        {
            var fileInfo = new FileInfo(path);
            if (fileInfo.Length <= 0 || fileInfo.Length > 64 * 1024)
            {
                return InvalidPolicy();
            }

            string json = File.ReadAllText(path);
            var policy = JsonSerializer.Deserialize<FixtureVaultPolicy>(json, FixtureVaultContract.JsonOptions);
            if (policy is null || !TryValidate(policy))
            {
                return InvalidPolicy();
            }

            return new PolicyLoadResult(policy, null);
        }
        catch (JsonException)
        {
            return InvalidPolicy();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return new PolicyLoadResult(null, new ScanError(
                "FV-E004",
                "The policy file could not be read safely."));
        }
    }

    internal static bool TryValidate(FixtureVaultPolicy policy)
    {
        if (policy.Version != FixtureVaultContract.PolicySchemaVersion ||
            policy.Roots is null || policy.Roots.Count == 0 || policy.Roots.Count > 64 ||
            policy.AllowedExtensions is null || policy.AllowedExtensions.Count == 0 || policy.AllowedExtensions.Count > 128 ||
            policy.Conventions is null || policy.Conventions.Count == 0 || policy.Conventions.Count > 32 ||
            policy.SensitiveDataRules is null || policy.SensitiveDataRules.Count > 32 ||
            policy.IgnoredPaths is null || policy.IgnoredPaths.Count > 256 ||
            policy.Ci is null || policy.MaxFileBytes < 1 || policy.MaxFileBytes > 64 * 1024 * 1024)
        {
            return false;
        }

        if (policy.Roots.Any(root => string.IsNullOrWhiteSpace(root) || root.Length > 256 || Path.IsPathRooted(root) || root.Contains('\0')))
        {
            return false;
        }

        if (policy.AllowedExtensions.Any(extension => string.IsNullOrWhiteSpace(extension) ||
                                                       extension.Length > 64 ||
                                                       !extension.StartsWith('.') ||
                                                       extension.Contains('/') || extension.Contains('\\')))
        {
            return false;
        }

        if (policy.Conventions.Any(convention => string.IsNullOrWhiteSpace(convention) || convention.Length > 64))
        {
            return false;
        }

        if (policy.SensitiveDataRules.Any(rule => string.IsNullOrWhiteSpace(rule) || rule.Length > 64))
        {
            return false;
        }

        return policy.IgnoredPaths.All(path => !string.IsNullOrWhiteSpace(path) && path.Length <= 256);
    }

    private static PolicyLoadResult InvalidPolicy()
    {
        return new PolicyLoadResult(null, new ScanError(
            "FV-E005",
            $"{FixtureVaultContract.PolicyFileName} is malformed or uses an unsupported schema."));
    }
}

internal static class PolicyWriter
{
    internal static (bool Created, bool AlreadyExists, ScanError? Error) Create(string repositoryRoot)
    {
        string path = Path.Combine(repositoryRoot, FixtureVaultContract.PolicyFileName);
        if (File.Exists(path))
        {
            return (false, true, null);
        }

        try
        {
            FixtureVaultPolicy policy = FixtureVaultPolicy.CreateDefault(Directory.Exists(Path.Combine(repositoryRoot, "tests")));
            string json = JsonSerializer.Serialize(policy, FixtureVaultContract.JsonOptions) + Environment.NewLine;
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer.Write(json);
            return (true, false, null);
        }
        catch (IOException) when (File.Exists(path))
        {
            return (false, true, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EncoderFallbackException)
        {
            return (false, false, new ScanError("FV-E006", "The policy file could not be created."));
        }
    }
}

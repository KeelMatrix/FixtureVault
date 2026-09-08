using KeelMatrix.Telemetry;

namespace KeelMatrix.FixtureVault;

internal static class Program
{
    internal static int Main(string[] args)
    {
        return FixtureVaultApplication.Run(args, Directory.GetCurrentDirectory(), new SharedTelemetryReporter(), Console.Out, Console.Error);
    }
}

internal interface IUsageTelemetry
{
    void RecordSuccessfulScan();
}

internal sealed class SharedTelemetryReporter : IUsageTelemetry
{
    private Client? client;

    public void RecordSuccessfulScan()
    {
        try
        {
            client ??= new Client("fixturevault", typeof(Program));
            client.TrackActivation();
            client.TrackHeartbeat();
        }
        catch
        {
            // Telemetry is best-effort and must never affect a scan.
        }
    }
}

internal static class FixtureVaultApplication
{
    internal static int Run(
        string[] args,
        string currentDirectory,
        IUsageTelemetry telemetry,
        TextWriter output,
        TextWriter errorOutput)
    {
        CommandLineParseResult parsed = CommandLineParser.Parse(args);
        if (parsed.Error is not null)
        {
            errorOutput.WriteLine($"FixtureVault: {parsed.Error}");
            errorOutput.WriteLine("Run 'fixturevault --help' for usage.");
            return 2;
        }

        if (parsed.Options!.ShowHelp)
        {
            output.WriteLine(CommandLineParser.Usage());
            return 0;
        }

        string repositoryRoot;
        try
        {
            repositoryRoot = PathUtilities.FindRepositoryRoot(currentDirectory);
        }
        catch
        {
            errorOutput.WriteLine("FixtureVault: the current directory could not be resolved safely.");
            return 2;
        }

        if (parsed.Options.Command == "init")
        {
            return RunInit(repositoryRoot, output, errorOutput);
        }

        PolicyLoadResult policyResult = PolicyLoader.Load(repositoryRoot);
        if (policyResult.Error is not null)
        {
            return RenderResult(
                new ScanResult(new ScanReport { Errors = [policyResult.Error] }, 2, false),
                parsed.Options.Format,
                output,
                errorOutput);
        }

        ScanResult result;
        try
        {
            result = FixtureScanner.Scan(
                repositoryRoot,
                policyResult.Policy!,
                parsed.Options.RootOverrides,
                parsed.Options.StrictOverride);
        }
        catch
        {
            result = new ScanResult(
                new ScanReport { Errors = [new ScanError("FV-E999", "The scan failed before a trustworthy result could be produced.")] },
                2,
                false);
        }

        int exitCode = RenderResult(result, parsed.Options.Format, output, errorOutput);
        if (result.Completed)
        {
            telemetry.RecordSuccessfulScan();
        }

        return exitCode;
    }

    private static int RunInit(string repositoryRoot, TextWriter output, TextWriter errorOutput)
    {
        (bool created, bool alreadyExists, ScanError? error) = PolicyWriter.Create(repositoryRoot);
        if (error is not null)
        {
            errorOutput.WriteLine($"FixtureVault: {error.Message}");
            return 2;
        }

        if (alreadyExists)
        {
            output.WriteLine($"{FixtureVaultContract.PolicyFileName} already exists; no files were changed.");
        }
        else if (created)
        {
            output.WriteLine($"Created {FixtureVaultContract.PolicyFileName}.");
        }

        return 0;
    }

    private static int RenderResult(
        ScanResult result,
        OutputFormat format,
        TextWriter output,
        TextWriter errorOutput)
    {
        if (format == OutputFormat.Json)
        {
            output.WriteLine(result.Report.ToJson());
            return result.ExitCode;
        }

        if (result.Report.Errors.Count > 0)
        {
            errorOutput.WriteLine("FixtureVault scan failed.");
            foreach (ScanError scanError in result.Report.Errors)
            {
                errorOutput.WriteLine($"{scanError.Code}: {scanError.Message}");
            }

            return result.ExitCode;
        }

        output.WriteLine("FixtureVault scan complete.");
        output.WriteLine($"{result.Report.FilesInspected} fixture file(s) inspected.");
        foreach (Finding finding in result.Report.Findings)
        {
            output.WriteLine();
            output.WriteLine($"{finding.RuleId} {finding.Disposition} {finding.Path}");
            output.WriteLine(finding.Message);
            output.WriteLine($"Remediation: {finding.Remediation}");
        }

        int blockingCount = result.Report.Findings.Count(finding => finding.Disposition == "block");
        output.WriteLine();
        output.WriteLine(blockingCount == 0
            ? "No policy-blocking findings."
            : $"{blockingCount} policy-blocking finding(s).");

        if (result.Report.Skipped.Count > 0)
        {
            output.WriteLine($"{result.Report.Skipped.Count} check(s) skipped conservatively.");
        }

        return result.ExitCode;
    }
}

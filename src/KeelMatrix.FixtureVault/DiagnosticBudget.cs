namespace KeelMatrix.FixtureVault;

// Findings and skipped diagnostics are bounded before they enter a report. The estimate is deliberately
// conservative: six bytes per UTF-16 code unit covers a JSON-escaped string, including its quotes.
internal sealed class DiagnosticBudget
{
    internal const int MaximumDiagnosticCount = 4_096;
    internal const long MaximumEstimatedBytes = 1 * 1024 * 1024;

    private int count;
    private long estimatedBytes;

    internal void Reserve(Finding finding)
    {
        Reserve(
            finding.RuleId,
            finding.Severity,
            finding.Disposition,
            finding.Path,
            finding.Message,
            finding.Remediation);
    }

    internal void Reserve(SkippedDiagnostic diagnostic)
    {
        Reserve(diagnostic.Code, diagnostic.Convention, diagnostic.Path, diagnostic.Reason);
    }

    private void Reserve(params string?[] values)
    {
        long recordBytes = 32;
        foreach (string? value in values)
        {
            recordBytes += EstimateJsonStringBytes(value);
        }

        if (count >= MaximumDiagnosticCount || estimatedBytes > MaximumEstimatedBytes - recordBytes)
        {
            throw new DiagnosticBudgetExceededException();
        }

        count++;
        estimatedBytes += recordBytes;
    }

    private static long EstimateJsonStringBytes(string? value)
    {
        if (value is null)
        {
            return 4;
        }

        // JSON can escape every UTF-16 code unit as \uXXXX. This overestimates UTF-8 output while
        // remaining independent of serializer settings and avoiding report serialization as a probe.
        return checked(2L + (6L * value.Length));
    }
}

internal sealed class DiagnosticBudgetExceededException : Exception
{
}

namespace Ava.Api.Services;

// ── PII detection interface ───────────────────────────────────────────────────

public interface IPiiDetectionService
{
    /// <summary>
    /// Scans a list of string values for PII.
    /// Returns Detected=true if any PII is found, with a Summary describing what was found.
    /// </summary>
    Task<PiiDetectionResult> DetectAsync(IEnumerable<string> values, string domain);
}

public record PiiDetectionResult(bool Detected, string Summary);

// ── No-op implementation (default) ───────────────────────────────────────────

/// <summary>
/// No-op PII detection service. Always reports clean.
/// Replace with AzureLanguagePiiDetectionService when ready for EU domains.
/// </summary>
public class NullPiiDetectionService : IPiiDetectionService
{
    public Task<PiiDetectionResult> DetectAsync(IEnumerable<string> values, string domain)
        => Task.FromResult(new PiiDetectionResult(false, string.Empty));
}

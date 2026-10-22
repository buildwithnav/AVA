namespace Ava.Vectors;

/// <summary>Result of a vector index rebuild operation.</summary>
public class VectorRebuildResult
{
    public string Status { get; set; } = string.Empty;
    public int PreviousCount { get; set; }
    public int NewCount { get; set; }
    public int DurationMs { get; set; }
}

namespace Ava.Vectors;

/// <summary>A single vector entry for persistence (id + content hash + embedding vector).</summary>
public record VectorEntry(string Id, string ContentHash, float[] Vector);

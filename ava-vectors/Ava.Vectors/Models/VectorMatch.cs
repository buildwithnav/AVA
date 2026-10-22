namespace Ava.Vectors;

/// <summary>A search result pairing an item with its similarity score.</summary>
public record VectorMatch<T>(T Item, float Score);

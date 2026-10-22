using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ava.Api.Models;

namespace Ava.Api.Services.Stages;

/// <summary>Shared helpers for token resolution, JSON cleanup, and value extraction.</summary>
public static class TokenResolver
{
    private static readonly Regex TokenPattern = new(@"\{\{\s*([^{}]+?)\s*\}\}", RegexOptions.Compiled);

    /// <summary>
    /// Replaces {{fieldName}} tokens with values from the fields dictionary. Whitespace
    /// inside the braces is tolerated (e.g. {{ fieldName }}). Tokens with no matching
    /// field are left intact so downstream validation can detect unresolved references.
    /// </summary>
    public static string Resolve(string template, Dictionary<string, object?> fields)
    {
        if (string.IsNullOrEmpty(template) || !template.Contains("{{")) return template;

        return TokenPattern.Replace(template, match =>
        {
            var key = match.Groups[1].Value.Trim();
            return fields.TryGetValue(key, out var val) && val != null
                ? val.ToString() ?? string.Empty
                : match.Value; // leave unresolved token intact
        });
    }

    /// <summary>
    /// Builds a summary of context fields. For agentic stages, pass onlyEssential=true
    /// to exclude large JSON blobs and only include small essential fields.
    /// </summary>
    public static string BuildContextSummary(
        Dictionary<string, object?> fields, 
        bool onlyEssential = false,
        int maxFieldLength = 500)
    {
        var sb = new StringBuilder();
        foreach (var kvp in fields.Where(f => !f.Key.StartsWith("_result_") && !f.Key.StartsWith("_foreach_")))
        {
            if (kvp.Value == null) continue;
            
            var valueStr = kvp.Value.ToString() ?? "";
            
            // For agentic stages, skip large fields and only keep essentials
            if (onlyEssential)
            {
                // Always include small identifiers
                if (kvp.Key.EndsWith("Id") || kvp.Key == "repository" || kvp.Key == "branch" || kvp.Key == "pbiId")
                {
                    if (valueStr.Length <= 100) // Only if reasonably small
                        sb.AppendLine($"{kvp.Key}: {valueStr}");
                    continue;
                }
                
                // For PBI details, extract just ID and title
                if (kvp.Key == "pbiDetails" && valueStr.StartsWith("{"))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(valueStr);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("id", out var id))
                            sb.AppendLine($"pbiId: {id.GetInt32()}");
                        if (root.TryGetProperty("title", out var title))
                            sb.AppendLine($"pbiTitle: {title.GetString()}");
                    }
                    catch { /* skip invalid JSON */ }
                    continue; // Don't include full PBI
                }
                
                // Skip other large values
                if (valueStr.Length > maxFieldLength)
                    continue;
            }
            
            // Truncate long values to prevent token bloat
            if (valueStr.Length > maxFieldLength)
            {
                sb.AppendLine($"{kvp.Key}: {valueStr[..maxFieldLength]}... [truncated]");
            }
            else
            {
                sb.AppendLine($"{kvp.Key}: {valueStr}");
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Rough token estimate for current stage context.
    /// Uses serialized key/value text size (chars/4) as a practical approximation.
    /// </summary>
    public static int EstimateContextTokens(
        Dictionary<string, object?> fields,
        bool includeInternal = false,
        int maxValueLength = 8000)
    {
        var sb = new StringBuilder();
        foreach (var kvp in fields)
        {
            if (!includeInternal && (kvp.Key.StartsWith("_result_") || kvp.Key.StartsWith("_foreach_")))
                continue;

            if (kvp.Value == null) continue;
            var valueStr = kvp.Value.ToString() ?? string.Empty;
            if (valueStr.Length > maxValueLength)
                valueStr = valueStr[..maxValueLength] + "... [truncated]";

            sb.Append(kvp.Key).Append(": ").AppendLine(valueStr);
        }

        return (int)Math.Ceiling(sb.Length / 4.0);
    }

    /// <summary>
    /// Renders the carried conversation history into a readable transcript block that AI stages
    /// can prepend to their LLM user message. Returns an empty string when there is no history,
    /// so callers can safely concatenate the result unconditionally.
    /// </summary>
    public static string FormatConversationHistory(IReadOnlyList<ConversationMessage>? history)
    {
        if (history == null || history.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("Prior conversation (for context — the user may have described the issue here before asking to proceed):");
        foreach (var turn in history)
        {
            if (string.IsNullOrWhiteSpace(turn.Content)) continue;
            var speaker = string.Equals(turn.Role, "user", StringComparison.OrdinalIgnoreCase) ? "User" : "Assistant";
            sb.Append(speaker).Append(": ").AppendLine(turn.Content.Trim());
        }
        sb.AppendLine();
        return sb.ToString();
    }
}

/// <summary>JSON parsing helpers shared across stage handlers.</summary>
public static class JsonHelper
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public static JsonSerializerOptions Options => JsonOpts;

    public static object? ExtractValue(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.TryGetInt64(out var l) ? (object)l : el.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => el.GetRawText()
    };

    /// <summary>
    /// Strips markdown fences and extracts the first complete JSON object/array.
    /// </summary>
    public static string StripMarkdownFences(string text)
    {
        if (text.StartsWith("```"))
        {
            var start = text.IndexOf('\n') + 1;
            var end = text.LastIndexOf("```");
            text = end > start ? text[start..end].Trim() : text;
        }

        var trimmed = text.TrimStart();
        if (trimmed.Length == 0) return text;

        char open = trimmed[0], close = open == '{' ? '}' : (open == '[' ? ']' : '\0');
        if (close == '\0') return text;

        int depth = 0;
        bool inString = false;
        bool escape = false;

        for (int i = 0; i < trimmed.Length; i++)
        {
            char c = trimmed[i];
            if (escape) { escape = false; continue; }
            if (c == '\\' && inString) { escape = true; continue; }
            if (c == '"') { inString = !inString; continue; }
            if (inString) continue;
            if (c == open) depth++;
            else if (c == close) { depth--; if (depth == 0) return trimmed[..(i + 1)]; }
        }

        return trimmed;
    }

    /// <summary>
    /// Extracts fields from a tool JSON result into a fields dictionary.
    ///
    /// If outputMap is present, each entry is processed as:
    ///   Key   = the context variable name (referenced as {{Key}} in later stages)
    ///   Value = the JSON property to read from the result
    ///
    /// Special case — empty Value:
    ///   When the Value is blank (set via the UI's "Output Field" with no source property),
    ///   the entire result object is stored under the Key AND all of its scalar/nested
    ///   properties are flattened as Key.propName, Key.prop.sub, etc. — mirroring the
    ///   ForEach item-alias behaviour. This means outputMap = { "epic": "" } on a stage
    ///   that returns { "id": 36686, "title": "...", ... } will populate:
    ///     {{epic}}       → full JSON string
    ///     {{epic.id}}    → 36686
    ///     {{epic.title}} → "..."
    ///
    /// If no outputMap, best-effort flat extraction of all scalar top-level properties.
    /// </summary>
    public static void ExtractOutputs(
        string? resultJson,
        Dictionary<string, string>? outputMap,
        Dictionary<string, object?> fields)
    {
        if (string.IsNullOrEmpty(resultJson)) return;

        try
        {
            using var doc = JsonDocument.Parse(resultJson);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
            {
                var items = root.EnumerateArray().ToList();
                if (items.Count == 1)
                    root = items[0];
                else
                    return; // Store whole array for user-select
            }

            if (outputMap != null && outputMap.Any())
            {
                foreach (var kvp in outputMap)
                {
                    var sourceProperty = kvp.Value?.Trim();

                    // Empty source → flatten the whole result under the key prefix so
                    // {{key}}, {{key.id}}, {{key.title}}, etc. are all available.
                    if (string.IsNullOrEmpty(sourceProperty))
                    {
                        if (root.ValueKind == JsonValueKind.Object)
                        {
                            fields[kvp.Key] = root.GetRawText();
                            FlattenObjectInto(root, kvp.Key, fields);
                        }
                        else
                        {
                            fields[kvp.Key] = ExtractValue(root);
                        }
                        continue;
                    }

                    if (root.TryGetProperty(sourceProperty, out var val))
                        fields[kvp.Key] = ExtractValue(val);
                    else
                    {
                        // Case-insensitive fallback
                        foreach (var prop in root.EnumerateObject())
                        {
                            if (prop.Name.Equals(sourceProperty, StringComparison.OrdinalIgnoreCase))
                            {
                                fields[kvp.Key] = ExtractValue(prop.Value);
                                break;
                            }
                        }
                    }
                }
            }
            else
            {
                if (root.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in root.EnumerateObject())
                    {
                        if (prop.Value.ValueKind != JsonValueKind.Object &&
                            prop.Value.ValueKind != JsonValueKind.Array)
                        {
                            fields.TryAdd(prop.Name, ExtractValue(prop.Value));
                        }
                    }
                }
            }
        }
        catch { /* non-JSON result — skip */ }
    }

    /// <summary>
    /// Recursively flattens an object's properties into the fields dictionary
    /// under the given prefix (e.g. prefix="epic" → epic.id, epic.title, etc.).
    /// Mirrors the ForEach InjectItemProperties behaviour so output fields work
    /// the same way as foreach item aliases.
    /// </summary>
    private static void FlattenObjectInto(
        JsonElement obj, string prefix, Dictionary<string, object?> fields,
        int depth = 0)
    {
        if (depth > 5) return; // guard against deeply nested structures

        foreach (var prop in obj.EnumerateObject())
        {
            var key = $"{prefix}.{prop.Name}";
            switch (prop.Value.ValueKind)
            {
                case JsonValueKind.Object:
                    fields[key] = prop.Value.GetRawText();
                    FlattenObjectInto(prop.Value, key, fields, depth + 1);
                    break;
                case JsonValueKind.Array:
                    fields[key] = prop.Value.GetRawText();
                    break;
                default:
                    fields[key] = ExtractValue(prop.Value);
                    break;
            }
        }
    }
}

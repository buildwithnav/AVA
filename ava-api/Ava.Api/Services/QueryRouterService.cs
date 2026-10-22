using System.Text.Json;
using Ava.Api.Config;
using Microsoft.Extensions.Options;
using OpenAI.Chat;

namespace Ava.Api.Services;

/// <summary>
/// Routes incoming queries to the appropriate retrieval strategy based on
/// heuristic analysis and optional LLM classification.
/// </summary>
public class QueryRouterService
{
    private readonly AvaSettings _settings;
    private readonly IChatClientFactory _chatClientFactory;
    private readonly ILogger<QueryRouterService> _logger;

    public QueryRouterService(
        IOptions<AvaSettings> settings,
        IChatClientFactory chatClientFactory,
        ILogger<QueryRouterService> logger)
    {
        _settings = settings.Value;
        _chatClientFactory = chatClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Classify a query into a retrieval strategy.
    /// Always uses LLM classifier for accuracy, with heuristic as fallback on error.
    /// </summary>
    public async Task<QueryRoute> RouteAsync(string query, List<string>? conversationContext = null)
    {
        // Primary: LLM classifier (more accurate than keyword heuristics)
        try
        {
            var llmRoute = await ClassifyWithLlmAsync(query, conversationContext);
            if (llmRoute != null)
            {
                llmRoute.UsedLlm = true;
                _logger.LogDebug("Router LLM classified '{Query}' as {Strategy}", query, llmRoute.Strategy);
                return llmRoute;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LLM router classification failed, falling back to heuristic");
        }

        // Fallback: heuristic classification (used when LLM is unavailable)
        var heuristic = ClassifyHeuristic(query, conversationContext);
        _logger.LogDebug("Router heuristic fallback classified '{Query}' as {Strategy} (confidence: {Confidence:F2})",
            query, heuristic.Strategy, heuristic.Confidence);
        return heuristic;
    }

    /// <summary>
    /// Fast heuristic classification using keyword signals.
    /// </summary>
    public QueryRoute ClassifyHeuristic(string query, List<string>? conversationContext = null)
    {
        var lower = query.ToLowerInvariant();
        var retrieval = _settings.DomainFiles.Retrieval;

        // Non-retrieval signals — greetings, workflow commands, general questions
        var noneSignals = new[]
        {
            "hello", "hi ", "hey", "good morning", "good afternoon",
            "run the", "execute", "create a", "generate a", "deploy",
            "what can you do", "help me with", "start a workflow"
        };
        if (noneSignals.Any(s => lower.Contains(s)) || lower.Length < 10)
        {
            return new QueryRoute
            {
                Strategy = RetrievalStrategy.None,
                MaxIterations = 0,
                Confidence = 0.75,
                Sources = Array.Empty<string>()
            };
        }

        // Agentic signals — complex queries requiring iterative search
        var agenticSignals = new[]
        {
            "why", "trace", "how does", "step by step", "walk me through",
            "compare", "difference between", "relationship between",
            "all the requirements", "every requirement", "list all",
            "across all", "throughout the document"
        };

        var agenticHits = agenticSignals.Count(s => lower.Contains(s));
        if (agenticHits >= 2)
        {
            return new QueryRoute
            {
                Strategy = RetrievalStrategy.Agentic,
                MaxIterations = retrieval.AgenticMaxIterations,
                Confidence = 0.90 + (agenticHits * 0.02),
                Sources = new[] { "docs" }
            };
        }

        // Expanded signals — broad queries requiring multi-angle retrieval
        var expandedSignals = new[]
        {
            "summarize", "summary", "overview", "all", "every", "entire",
            "what are the", "describe the", "explain the", "tell me about",
            "what does the document say about", "section",
            "steps to", "process for", "procedure", "how to",
            "what is after", "what comes after", "next section", "following section"
        };

        var expandedHits = expandedSignals.Count(s => lower.Contains(s));
        if (expandedHits >= 1 && lower.Split(' ').Length > 5)
        {
            return new QueryRoute
            {
                Strategy = RetrievalStrategy.Expanded,
                MaxIterations = 1,
                Confidence = 0.80 + (expandedHits * 0.05),
                Sources = new[] { "docs" }
            };
        }

        // Single agentic signal with multi-hop structure
        if (agenticHits == 1 && (lower.Contains(" and ") || lower.Contains(" then ") || lower.Contains(" also ")))
        {
            return new QueryRoute
            {
                Strategy = RetrievalStrategy.Agentic,
                MaxIterations = retrieval.AgenticMaxIterations,
                Confidence = 0.80,
                Sources = new[] { "docs" }
            };
        }

        // Continuation/follow-up detection from conversation context
        var continuationSignals = new[] { "tell me more", "what else", "continue", "go on", "next", "what comes after" };
        if (continuationSignals.Any(s => lower.Contains(s)) && conversationContext?.Count > 0)
        {
            return new QueryRoute
            {
                Strategy = RetrievalStrategy.Expanded,
                MaxIterations = 1,
                Confidence = 0.88,
                Sources = new[] { "docs" }
            };
        }

        // Default: fast strategy for simple/direct questions
        var wordCount = lower.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        var confidence = wordCount <= 8 ? 0.90 : 0.70; // Short queries are confidently fast

        return new QueryRoute
        {
            Strategy = RetrievalStrategy.Fast,
            MaxIterations = 1,
            Confidence = confidence,
            Sources = new[] { "docs" }
        };
    }

    private async Task<QueryRoute?> ClassifyWithLlmAsync(string query, List<string>? conversationContext)
    {
        // Use Azure OpenAI fallback for router classification (fast, doesn't need user token)
        var client = _chatClientFactory.CreateAzureFallback();

        var contextSnippet = conversationContext?.Count > 0
            ? $"\nRecent conversation:\n{string.Join("\n", conversationContext.TakeLast(3))}"
            : "";

        var systemPrompt = """
            You are a query router for an enterprise AI assistant that has access to domain-specific documents.
            
            Given the user's question, do TWO things:
            1. Classify the retrieval strategy:
               - "none": The question does NOT need document search — it's a general greeting, a request to run a workflow/action, or a question that doesn't require domain file knowledge (e.g., "run the deployment", "create a report template", "hello", "what can you do")
               - "fast": Simple, single-fact questions answerable from one short paragraph (e.g., "why can't I edit X?", "what is the purpose of Y?")
               - "expanded": Questions about procedures, steps, lists, full sections, or multi-part answers. Use for: "what are the steps to...", "list all...", "show me the full section...", "what does section X contain?"
               - "agentic": Complex questions that require searching MULTIPLE DIFFERENT topics/sections and combining them. Use for:
                 * Comparisons: "Compare X process to Y process", "What's the difference between A and B?"
                 * Cross-referencing: "What role does X play in each section?", "Where is X mentioned and how?"
                 * Multi-hop reasoning: "If A depends on B, and B requires C, what are all the prerequisites?"
                 * Exhaustive multi-topic: "List all database objects AND their associated business rules"
                 Do NOT use agentic for single-section questions even if complex — use expanded instead.
            
            2. If strategy is NOT "none", rewrite the user's question into 1-3 BM25 keyword search queries:
               CRITICAL: BM25 works best with short noun-phrase queries (2-5 words). 
               - Extract ONLY the key topic nouns and technical terms
               - Remove ALL verbs, filler, instructions (no "list", "show", "what is", "how to")
               - Use the exact terminology likely in the document headings
               - Example: "List out everything verbatim for section 4.4 monitoring visit report" → ["monitoring visit report"]
               - Example: "What tables are in the SMA database?" → ["SMA database tables", "oracle data tables"]
               - Example: "What are the steps to review SDV CRFs in RDC?" → ["SDV CRFs RDC review steps", "source data verification CRF"]
               - Example: "What comes after section 4.4?" → ["monitoring letter", "4.5"]
               - For broad questions, generate 2-3 short queries targeting different keyword combinations
            
            Respond with ONLY a JSON object:
            {"strategy": "none|fast|expanded|agentic", "queries": ["query1", "query2"]}
            For "none" strategy, queries can be empty: {"strategy": "none", "queries": []}
            """;

        var userMessage = $"Query: {query}{contextSnippet}";

        try
        {
            var messages = new List<ChatMessage>
            {
                new SystemChatMessage(systemPrompt),
                new UserChatMessage(userMessage)
            };

            var completion = await client.CompleteChatAsync(messages, new ChatCompletionOptions
            {
                MaxOutputTokenCount = 150,
                Temperature = 0.0f
            });

            var text = completion.Value.Content[0].Text?.Trim() ?? "";

            // Parse JSON response
            if (text.Contains("\"strategy\""))
            {
                var jsonStart = text.IndexOf('{');
                var jsonEnd = text.LastIndexOf('}');
                if (jsonStart >= 0 && jsonEnd > jsonStart)
                {
                    var json = text[jsonStart..(jsonEnd + 1)];
                    using var doc = JsonDocument.Parse(json);
                    var strategyStr = doc.RootElement.GetProperty("strategy").GetString();

                    var strategy = strategyStr?.ToLower() switch
                    {
                        "none" => RetrievalStrategy.None,
                        "fast" => RetrievalStrategy.Fast,
                        "expanded" => RetrievalStrategy.Expanded,
                        "agentic" => RetrievalStrategy.Agentic,
                        _ => RetrievalStrategy.Fast
                    };

                    // Extract rewritten search queries
                    List<string>? subQueries = null;
                    if (doc.RootElement.TryGetProperty("queries", out var queriesEl) &&
                        queriesEl.ValueKind == JsonValueKind.Array)
                    {
                        subQueries = queriesEl.EnumerateArray()
                            .Select(q => q.GetString())
                            .Where(q => !string.IsNullOrWhiteSpace(q))
                            .ToList()!;
                    }

                    return new QueryRoute
                    {
                        Strategy = strategy,
                        SubQueries = subQueries,
                        MaxIterations = strategy == RetrievalStrategy.Agentic
                            ? _settings.DomainFiles.Retrieval.AgenticMaxIterations : 1,
                        Confidence = 0.90,
                        Sources = new[] { "docs" }
                    };
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to parse LLM router response");
        }

        return null;
    }
}

// ── Models ────────────────────────────────────────────────────────────────────

public enum RetrievalStrategy
{
    None,
    Fast,
    Expanded,
    Agentic
}

public class QueryRoute
{
    public RetrievalStrategy Strategy { get; set; } = RetrievalStrategy.Fast;
    public int MaxIterations { get; set; } = 1;
    public double Confidence { get; set; }
    public string[] Sources { get; set; } = new[] { "docs" };
    public List<string>? SubQueries { get; set; }
    public bool UsedLlm { get; set; }
}

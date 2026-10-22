using Ava.Api.Data;
using Microsoft.AspNetCore.Mvc;

namespace Ava.Api.Controllers;

/// <summary>
/// Conversation archive endpoints (Postgres-backed, durable).
/// GET /api/conversations?userId=          — list conversations for a user
/// GET /api/conversations/{id}/messages    — full message history for a conversation
///
/// Note: No auth gate currently (internal deployment behind VPN).
/// Auth scoping should be added when the user/role system is implemented.
/// </summary>
[ApiController]
[Route("api/conversations")]
public class ConversationsController : ControllerBase
{
    private readonly PostgresConversationRepository _repo;
    private readonly ILogger<ConversationsController> _logger;

    public ConversationsController(
        PostgresConversationRepository repo,
        ILogger<ConversationsController> logger)
    {
        _repo   = repo;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/conversations?userId=&amp;limit=
    /// Returns archived conversations for a user, newest first.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetByUser(
        [FromQuery] string? userId = null,
        [FromQuery] int limit = 50)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return BadRequest(new { error = "userId query parameter is required" });

        var conversations = await _repo.GetByUserAsync(userId, Math.Clamp(limit, 1, 500));
        return Ok(new
        {
            userId,
            count = conversations.Count,
            conversations
        });
    }

    /// <summary>
    /// GET /api/conversations/{conversationId}/messages
    /// Returns the archived message history for a specific conversation, oldest first.
    /// </summary>
    [HttpGet("{conversationId}/messages")]
    public async Task<IActionResult> GetMessages(string conversationId)
    {
        if (string.IsNullOrWhiteSpace(conversationId))
            return BadRequest(new { error = "conversationId is required" });

        var messages = await _repo.GetMessagesAsync(conversationId);
        return Ok(new
        {
            conversationId,
            count = messages.Count,
            messages
        });
    }
}

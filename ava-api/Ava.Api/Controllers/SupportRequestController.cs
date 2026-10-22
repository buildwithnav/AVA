using Microsoft.AspNetCore.Mvc;

namespace Ava.Api.Controllers;

[ApiController]
[Route("api/support")]
public class SupportRequestController : ControllerBase
{
    private readonly ILogger<SupportRequestController> _logger;

    public SupportRequestController(ILogger<SupportRequestController> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Creates a support request ticket.
    /// Hardcoded mock implementation — returns a generated ticket ID without persistence.
    /// Will be replaced with a real endpoint when the support system integration is ready.
    /// </summary>
    [HttpPost]
    public IActionResult Create([FromBody] SupportRequestBody body)
    {
        if (string.IsNullOrWhiteSpace(body.Title) || string.IsNullOrWhiteSpace(body.Description))
            return BadRequest(new { error = "title and description are required" });

        var ticketId = $"AVA-{DateTime.UtcNow:yyyyMMdd}-{Random.Shared.Next(1000, 9999)}";
        var priority = string.IsNullOrWhiteSpace(body.Priority) ? "P3 - Medium" : body.Priority;
        var category = string.IsNullOrWhiteSpace(body.Category) ? "Other" : body.Category;

        _logger.LogInformation(
            "Support request created: {TicketId} | Priority: {Priority} | Category: {Category} | Title: {Title}",
            ticketId, priority, category, body.Title);

        return Ok(new
        {
            ticketId,
            status = "created",
            title = body.Title,
            description = body.Description,
            priority,
            category,
            createdAt = DateTime.UtcNow,
            message = $"Your support request has been submitted. The support team will respond within the SLA for {priority} tickets."
        });
    }
}

public class SupportRequestBody
{
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Priority { get; set; } = "P3 - Medium";
    public string Category { get; set; } = "Other";
}

using Ava.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Ava.Api.Services.Security;

/// <summary>
/// Enforces domain access on any action that takes a <c>domainId</c> (route, query, or form)
/// or a <c>subDomainId</c> alongside it. Registered globally so a new domain-scoped endpoint
/// is protected by default rather than by remembering to add a check.
///
/// Controllers that own their authorization — SecurityController, DomainController, Chat —
/// are skipped; they already call AccessService directly with the right access level.
/// </summary>
public class DomainAccessFilter : IAsyncActionFilter
{
    private static readonly HashSet<string> SelfAuthorizingControllers = new(StringComparer.OrdinalIgnoreCase)
    {
        "Security", "Domain", "Chat"
    };

    private static readonly string[] DomainArgNames = { "domainId", "domain" };

    private readonly AccessService _access;
    private readonly ILogger<DomainAccessFilter> _logger;

    public DomainAccessFilter(AccessService access, ILogger<DomainAccessFilter> logger)
    {
        _access = access;
        _logger = logger;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var controller = context.RouteData.Values["controller"]?.ToString() ?? "";
        if (SelfAuthorizingControllers.Contains(controller))
        {
            await next();
            return;
        }

        var domainId = FindArg(context, DomainArgNames);
        if (string.IsNullOrWhiteSpace(domainId))
        {
            await next();
            return;
        }

        // Writes need more than browse — infer the required level from the HTTP verb.
        var access = context.HttpContext.Request.Method.ToUpperInvariant() switch
        {
            "POST" => ModuleAccess.Insert,
            "PUT" or "PATCH" => ModuleAccess.Update,
            "DELETE" => ModuleAccess.Delete,
            _ => ModuleAccess.Browse
        };

        var subDomainId = FindArg(context, new[] { "subDomainId" });

        var allowed = string.IsNullOrWhiteSpace(subDomainId)
            ? await _access.HasDomainAccessAsync(domainId, access)
            : await _access.HasChildDomainAccessAsync(domainId, subDomainId, access);

        if (!allowed)
        {
            var user = _access.GetUser();
            _logger.LogWarning("Blocked {Method} {Path} for {User} — no {Access} access to {Domain}",
                context.HttpContext.Request.Method, context.HttpContext.Request.Path,
                user.UserId, access, domainId);

            context.Result = new ObjectResult(new { error = $"You do not have access to domain '{domainId}'." })
            {
                StatusCode = StatusCodes.Status403Forbidden
            };
            return;
        }

        await next();
    }

    private static string? FindArg(ActionExecutingContext context, string[] names)
    {
        foreach (var name in names)
        {
            var match = context.ActionArguments
                .FirstOrDefault(a => string.Equals(a.Key, name, StringComparison.OrdinalIgnoreCase));

            if (match.Value is string s && !string.IsNullOrWhiteSpace(s))
                return s;
        }
        return null;
    }
}

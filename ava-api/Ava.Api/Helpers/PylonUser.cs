using System.Text;
using System.Text.Json;

namespace Ava.Api.Helpers;

/// <summary>
/// The user identity extracted from a Pylon-issued JWT.
/// UserId comes from the <c>nameid</c> claim, Username from <c>unique_name</c>.
/// </summary>
public readonly record struct PylonUser(string UserId, string Username)
{
    public static readonly PylonUser None = new(string.Empty, string.Empty);

    /// <summary>True when a user identity was resolved for the request.</summary>
    public bool IsPresent => !string.IsNullOrEmpty(UserId) || !string.IsNullOrEmpty(Username);
}

public static class PylonUserExtensions
{
    /// <summary>HttpContext.Items key holding the resolved Pylon user id (nameid).</summary>
    public const string UserIdItemKey = "PylonUserId";

    /// <summary>HttpContext.Items key holding the resolved Pylon username (unique_name).</summary>
    public const string UsernameItemKey = "PylonUsername";

    /// <summary>
    /// Reads the Pylon user identity that the auth middleware stashed in HttpContext.Items.
    /// Returns <see cref="PylonUser.None"/> when no identity was resolved for this request.
    /// </summary>
    public static PylonUser GetPylonUser(this HttpContext ctx)
    {
        var userId   = ctx.Items.TryGetValue(UserIdItemKey,   out var u) ? u as string : null;
        var username = ctx.Items.TryGetValue(UsernameItemKey, out var n) ? n as string : null;
        return new PylonUser(userId ?? string.Empty, username ?? string.Empty);
    }

    /// <summary>
    /// Decodes the payload of a Pylon-issued JWT to extract the user identity.
    ///
    /// Pylon is a trusted upstream gateway that sits in front of this API, so we deliberately
    /// do NOT validate the token signature — we only base64url-decode the payload and read the
    /// <c>nameid</c> (user id) and <c>unique_name</c> (username) claims. This keeps the API
    /// dependency-free (no JWT/crypto libraries) while still capturing who made the request.
    ///
    /// Returns false when the header is missing or the token cannot be parsed.
    /// </summary>
    public static bool TryDecodePylonJwt(string? authorizationHeader, out PylonUser user)
    {
        user = PylonUser.None;
        if (string.IsNullOrWhiteSpace(authorizationHeader)) return false;

        var token = authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authorizationHeader["Bearer ".Length..].Trim()
            : authorizationHeader.Trim();

        var parts = token.Split('.');
        if (parts.Length < 2) return false;

        try
        {
            var payloadJson = Base64UrlDecode(parts[1]);
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;

            var userId   = ReadClaim(root, "nameid");
            var username = ReadClaim(root, "unique_name");

            if (string.IsNullOrEmpty(userId) && string.IsNullOrEmpty(username)) return false;

            user = new PylonUser(userId, username);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string ReadClaim(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el)) return string.Empty;
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString() ?? string.Empty,
            JsonValueKind.Number => el.GetRawText(),
            _ => string.Empty
        };
    }

    private static string Base64UrlDecode(string input)
    {
        var s = input.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "=";  break;
        }
        return Encoding.UTF8.GetString(Convert.FromBase64String(s));
    }
}

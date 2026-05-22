using Microsoft.AspNetCore.Http;

namespace Munibot;

public sealed record MunibotTokenPrincipal(string Id, IReadOnlySet<string> Scopes);

public static class MunibotAuthentication
{
    public const string TokenItemKey = "MunibotToken";
    private const string HeaderName = "X-Munibot-Token";

    public static bool TryAuthenticate(HttpContext context, BotConfig config, out MunibotTokenPrincipal? principal)
    {
        principal = null;

        var configuredTokens = config.Tokens
            .Where(t => !string.IsNullOrWhiteSpace(t.Value))
            .ToList();

        if (configuredTokens.Count == 0)
        {
            principal = new MunibotTokenPrincipal(
                "anonymous-dev",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "*" });
            context.Items[TokenItemKey] = principal;
            return true;
        }

        var suppliedToken = GetSuppliedToken(context);
        if (string.IsNullOrWhiteSpace(suppliedToken))
        {
            return false;
        }

        var matched = configuredTokens.FirstOrDefault(t =>
            string.Equals(t.Value, suppliedToken, StringComparison.Ordinal));

        if (matched is null)
        {
            return false;
        }

        principal = new MunibotTokenPrincipal(
            matched.Id,
            new HashSet<string>(matched.Scopes, StringComparer.OrdinalIgnoreCase));

        context.Items[TokenItemKey] = principal;
        return true;
    }

    public static string? GetAuthenticatedTokenId(HttpContext context)
        => context.Items.TryGetValue(TokenItemKey, out var value) && value is MunibotTokenPrincipal principal
            ? principal.Id
            : null;

    public static bool HasScope(MunibotTokenPrincipal principal, string requiredScope)
        => principal.Scopes.Contains("*") || principal.Scopes.Contains(requiredScope);

    private static string? GetSuppliedToken(HttpContext context)
    {
        var headerToken = context.Request.Headers[HeaderName].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(headerToken))
        {
            return headerToken.Trim();
        }

        var authorization = context.Request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(authorization))
        {
            return null;
        }

        const string bearerPrefix = "Bearer ";
        return authorization.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase)
            ? authorization[bearerPrefix.Length..].Trim()
            : null;
    }
}

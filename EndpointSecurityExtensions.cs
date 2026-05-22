using Microsoft.AspNetCore.Http;

namespace Munibot;

public static class EndpointSecurityExtensions
{
    public static RouteHandlerBuilder RequireMunibotScope(this RouteHandlerBuilder builder, string requiredScope)
    {
        return builder.AddEndpointFilter(async (context, next) =>
        {
            var httpContext = context.HttpContext;
            var config = httpContext.RequestServices.GetRequiredService<BotConfig>();

            if (!MunibotAuthentication.TryAuthenticate(httpContext, config, out var principal))
            {
                return Results.Json(
                    new ProblemDetailsDto("Authentication required.", StatusCodes.Status401Unauthorized),
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            if (principal is null || !MunibotAuthentication.HasScope(principal, requiredScope))
            {
                return Results.Json(
                    new ProblemDetailsDto($"Missing required Munibot scope '{requiredScope}'.", StatusCodes.Status403Forbidden),
                    statusCode: StatusCodes.Status403Forbidden);
            }

            return await next(context);
        });
    }
}

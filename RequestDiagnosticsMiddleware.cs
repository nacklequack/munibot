using System.Diagnostics;
using System.Text;

namespace Munibot;

public sealed class RequestDiagnosticsMiddleware(
    RequestDelegate next,
    BotConfig config,
    ILogger<RequestDiagnosticsMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!config.Diagnostics.LogApiCalls || IsProbePath(context.Request.Path))
        {
            await next(context);
            return;
        }

        var sw = Stopwatch.StartNew();
        var requestId = context.TraceIdentifier;
        var requestBody = await TryReadRequestBodyAsync(context);
        var originalBody = context.Response.Body;
        await using var responseBuffer = new MemoryStream();
        var failedByException = false;

        if (config.Diagnostics.LogApiBodies)
        {
            context.Response.Body = responseBuffer;
        }

        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            sw.Stop();
            failedByException = true;
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            }

            logger.LogError(
                ex,
                "API {Method} {Path} failed status=500 elapsedMs={ElapsedMs} requestId={RequestId} token={TokenId} error={Error}",
                context.Request.Method,
                context.Request.Path,
                sw.ElapsedMilliseconds,
                requestId,
                MunibotAuthentication.GetAuthenticatedTokenId(context) ?? "unknown",
                Redaction.RedactText(ex.Message));
            throw;
        }
        finally
        {
            sw.Stop();

            string? responseBody = null;
            if (config.Diagnostics.LogApiBodies)
            {
                responseBuffer.Position = 0;
                responseBody = await TryReadBodyAsync(responseBuffer, config.Diagnostics.MaxLoggedBodyBytes);
                responseBuffer.Position = 0;
                await responseBuffer.CopyToAsync(originalBody);
                context.Response.Body = originalBody;
            }

            if (!failedByException)
            {
                logger.LogInformation(
                    "API {Method} {Path} status={StatusCode} elapsedMs={ElapsedMs} requestId={RequestId} token={TokenId} requestBody={RequestBody} responseBody={ResponseBody}",
                    context.Request.Method,
                    context.Request.Path,
                    context.Response.StatusCode,
                    sw.ElapsedMilliseconds,
                    requestId,
                    MunibotAuthentication.GetAuthenticatedTokenId(context) ?? "anonymous",
                    requestBody ?? "[disabled]",
                    responseBody ?? "[disabled]");
            }
        }
    }

    private async Task<string?> TryReadRequestBodyAsync(HttpContext context)
    {
        if (!config.Diagnostics.LogApiBodies ||
            context.Request.ContentLength is null or 0 ||
            !IsTextContent(context.Request.ContentType))
        {
            return null;
        }

        context.Request.EnableBuffering();
        var body = await TryReadBodyAsync(context.Request.Body, config.Diagnostics.MaxLoggedBodyBytes);
        context.Request.Body.Position = 0;
        return body;
    }

    private static async Task<string> TryReadBodyAsync(Stream stream, int maxBytes)
    {
        if (maxBytes == 0)
        {
            return "[body logging disabled]";
        }

        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024,
            leaveOpen: true);

        var buffer = new char[maxBytes];
        var read = await reader.ReadBlockAsync(buffer, 0, maxBytes);
        var raw = new string(buffer, 0, read);
        return Redaction.RedactJsonOrText(raw, maxBytes);
    }

    private static bool IsTextContent(string? contentType)
        => string.IsNullOrWhiteSpace(contentType) ||
           contentType.Contains("json", StringComparison.OrdinalIgnoreCase) ||
           contentType.Contains("text", StringComparison.OrdinalIgnoreCase) ||
           contentType.Contains("form", StringComparison.OrdinalIgnoreCase);

    private static bool IsProbePath(PathString path)
        => path.Equals("/health", StringComparison.OrdinalIgnoreCase) ||
           path.Equals("/ready", StringComparison.OrdinalIgnoreCase);
}

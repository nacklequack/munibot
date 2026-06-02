using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Munibot;

public sealed partial class SecondLifeAccountHistoryClient(
    BotConfig config,
    ILogger<SecondLifeAccountHistoryClient> logger)
    : ISecondLifeAccountHistoryClient
{
    private const string LoginPageUrl = "https://id.secondlife.com/openid/login?return_to=https%3A%2F%2Fsecondlife.com%2Fauth%2Foid_return.php";
    private const string LoginSubmitUrl = "https://id.secondlife.com/openid/loginsubmit";
    private const string OpenIdServerUrl = "https://id.secondlife.com/openid/openidserver";
    private const string TransactionHistoryUrl = "https://accounts.secondlife.com/get_transaction_history_csv";

    private readonly Func<HttpMessageHandler> _handlerFactory = CreateDefaultHandler;

    public SecondLifeAccountHistoryClient(
        BotConfig config,
        ILogger<SecondLifeAccountHistoryClient> logger,
        Func<HttpMessageHandler> handlerFactory)
        : this(config, logger)
    {
        _handlerFactory = handlerFactory;
    }

    public async Task<AccountHistoryResponseDto> GetTransactionsAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken = default)
    {
        if (toUtc < fromUtc)
        {
            throw new ArgumentException("Account transaction range end must be on or after the start.");
        }

        var username = string.IsNullOrWhiteSpace(config.AccountHistory.Username)
            ? $"{config.Login.FirstName} {config.Login.LastName}"
            : config.AccountHistory.Username.Trim();

        var requestedAt = DateTimeOffset.UtcNow;
        using var handler = _handlerFactory();
        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(config.AccountHistory.TimeoutSeconds)
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Munibot", config.Login.Version));

        var (firstName, lastName) = ParseLoginName(username);
        var loginPageHtml = await GetPageAsync(client, LoginPageUrl, cancellationToken);

        if (!TryExtractHiddenInputsForForm(loginPageHtml, "loginform", out var loginFormValues))
        {
            logger.LogWarning(
                "Second Life account-history login page for {BotAvatarName} did not return the expected login form.",
                username);
            throw new InvalidOperationException("Unable to initialize the Second Life account-history login flow.");
        }

        loginFormValues["username"] = $"{firstName} {lastName}";
        loginFormValues["password"] = config.Login.Password;
        loginFormValues["language"] = "en_US";
        loginFormValues["previous_language"] = "en_US";
        loginFormValues["from_amazon"] = "False";
        loginFormValues["stay_logged_in"] = "True";

        var loginHtml = await PostFormAsync(
            client,
            LoginSubmitUrl,
            loginFormValues,
            LoginPageUrl,
            cancellationToken);

        if (!TryExtractHiddenInputsForForm(loginHtml, "openid_message", out var openIdPayload))
        {
            logger.LogWarning(
                "Second Life account-history login for {BotAvatarName} did not return an OpenID handoff form.",
                username);
            throw new InvalidOperationException("Unable to authenticate with the Second Life account-history portal.");
        }

        _ = await PostFormAsync(client, OpenIdServerUrl, openIdPayload, null, cancellationToken);

        var historyUrl =
            $"{TransactionHistoryUrl}?startDate={Uri.EscapeDataString(fromUtc.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))}" +
            $"&endDate={Uri.EscapeDataString(toUtc.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))}" +
            "&type=xml&xml=1&omit_zero_amounts=false";

        using var historyResponse = await client.GetAsync(historyUrl, cancellationToken);
        var historyPayload = await historyResponse.Content.ReadAsStringAsync(cancellationToken);

        if (!historyResponse.IsSuccessStatusCode)
        {
            logger.LogWarning(
                "Second Life account-history fetch failed for {BotAvatarName} with HTTP {StatusCode}.",
                username,
                historyResponse.StatusCode);
            throw new InvalidOperationException(
                $"Second Life account-history fetch failed: HTTP {(int)historyResponse.StatusCode} {historyResponse.ReasonPhrase}");
        }

        var transactions = ParseTransactionsXml(historyPayload);
        var completedAt = DateTimeOffset.UtcNow;

        return new AccountHistoryResponseDto(
            fromUtc.ToUniversalTime(),
            toUtc.ToUniversalTime(),
            transactions.Count,
            requestedAt,
            completedAt,
            transactions);
    }

    private static async Task<string> PostFormAsync(
        HttpClient client,
        string url,
        IReadOnlyDictionary<string, string> formValues,
        string? referer,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(formValues)
        };

        if (!string.IsNullOrWhiteSpace(referer))
        {
            request.Headers.Referrer = new Uri(referer, UriKind.Absolute);
        }

        using var response = await client.SendAsync(request, cancellationToken);
        if (IsRedirect(response.StatusCode) && response.Headers.Location is not null)
        {
            var redirectUri = response.Headers.Location.IsAbsoluteUri
                ? response.Headers.Location
                : new Uri(new Uri(url, UriKind.Absolute), response.Headers.Location);

            return await GetPageAsync(client, redirectUri.AbsoluteUri, cancellationToken);
        }

        var payload = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Second Life account-history authentication failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        return payload;
    }

    private static async Task<string> GetPageAsync(
        HttpClient client,
        string url,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(url, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Second Life account-history request failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        return payload;
    }

    private static (string FirstName, string LastName) ParseLoginName(string botAvatarName)
    {
        var parts = botAvatarName
            .Replace('.', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length == 0)
        {
            throw new InvalidOperationException("Second Life bot avatar name is invalid.");
        }

        if (parts.Length == 1)
        {
            return (parts[0], "Resident");
        }

        return (parts[0], parts[1]);
    }

    private static bool TryExtractHiddenInputsForForm(
        string html,
        string formId,
        out Dictionary<string, string> values)
    {
        values = new Dictionary<string, string>(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(html) || string.IsNullOrWhiteSpace(formId))
        {
            return false;
        }

        var formMatch = FormByIdRegex().Matches(html)
            .Cast<Match>()
            .FirstOrDefault(match => string.Equals(
                match.Groups["id"].Value,
                formId,
                StringComparison.OrdinalIgnoreCase));

        if (formMatch is null || !formMatch.Success)
        {
            return false;
        }

        foreach (Match inputMatch in InputTagRegex().Matches(formMatch.Groups["body"].Value))
        {
            var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match attributeMatch in AttributeRegex().Matches(inputMatch.Value))
            {
                var name = attributeMatch.Groups["name"].Value;
                var value = WebUtility.HtmlDecode(attributeMatch.Groups["value"].Value);
                attributes[name] = value;
            }

            if (!attributes.TryGetValue("type", out var type) ||
                !type.Equals("hidden", StringComparison.OrdinalIgnoreCase) ||
                !attributes.TryGetValue("name", out var inputName) ||
                string.IsNullOrWhiteSpace(inputName))
            {
                continue;
            }

            values[inputName] = attributes.TryGetValue("value", out var inputValue)
                ? inputValue
                : string.Empty;
        }

        return values.Count > 0;
    }

    private static IReadOnlyList<AccountHistoryTransactionDto> ParseTransactionsXml(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return [];
        }

        XDocument document;
        try
        {
            document = XDocument.Parse(payload);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Second Life account-history payload could not be parsed.", ex);
        }

        if (!string.Equals(document.Root?.Name.LocalName, "transactions", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Second Life account-history payload had an unexpected format.");
        }

        var rows = new List<AccountHistoryTransactionDto>();
        foreach (var transactionElement in document.Root!.Elements("transaction"))
        {
            var transactionId = transactionElement.Element("id")?.Value?.Trim();
            if (!Guid.TryParse(transactionId, out _))
            {
                continue;
            }

            var timestampRaw = transactionElement.Element("time")?.Value?.Trim();
            if (!DateTimeOffset.TryParse(
                    timestampRaw,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var occurredAtUtc))
            {
                continue;
            }

            var endBalanceRaw = transactionElement.Element("end_balance")?.Value?.Trim();
            if (!uint.TryParse(endBalanceRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var endBalance))
            {
                continue;
            }

            rows.Add(new AccountHistoryTransactionDto(
                transactionId!,
                NullIfWhiteSpace(transactionElement.Element("type")?.Value),
                NullIfWhiteSpace(transactionElement.Element("description")?.Value),
                NullIfWhiteSpace(transactionElement.Element("resident")?.Value),
                occurredAtUtc.ToUniversalTime(),
                endBalance,
                null));
        }

        var ordered = rows
            .OrderBy(x => x.OccurredAtUtc)
            .ThenBy(x => x.TransactionId, StringComparer.Ordinal)
            .ToList();

        for (var i = 1; i < ordered.Count; i++)
        {
            ordered[i] = ordered[i] with
            {
                InferredAmountDelta = unchecked((int)ordered[i].EndBalance - (int)ordered[i - 1].EndBalance)
            };
        }

        return ordered;
    }

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool IsRedirect(HttpStatusCode statusCode)
        => statusCode == HttpStatusCode.Moved ||
           statusCode == HttpStatusCode.Redirect ||
           statusCode == HttpStatusCode.RedirectMethod ||
           statusCode == HttpStatusCode.TemporaryRedirect ||
           statusCode == HttpStatusCode.PermanentRedirect;

    private static HttpMessageHandler CreateDefaultHandler()
        => new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        };

    [GeneratedRegex("""<form\b[^>]*\bid\s*=\s*["'](?<id>[^"']+)["'][^>]*>(?<body>.*?)</form>""", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex FormByIdRegex();

    [GeneratedRegex("""<input\b[^>]*>""", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex InputTagRegex();

    [GeneratedRegex("""(?<name>[a-zA-Z_:][a-zA-Z0-9_:\-\.]*)\s*=\s*(?<quote>["'])(?<value>.*?)\k<quote>""", RegexOptions.Singleline)]
    private static partial Regex AttributeRegex();
}

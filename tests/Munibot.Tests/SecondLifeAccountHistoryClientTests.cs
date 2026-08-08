using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Munibot;

namespace Munibot.Tests;

public sealed class SecondLifeAccountHistoryClientTests
{
    [Fact]
    public async Task GetTransactionsAsync_LoginHandshakeAndXmlPayload_ReturnsTransactions()
    {
        var handler = new RecordingHandler(request =>
        {
            if (request.RequestUri?.Host == "id.secondlife.com" &&
                request.RequestUri.AbsolutePath == "/openid/login")
            {
                return Html("""
                    <form id="loginform">
                      <input type="hidden" name="csrf" value="abc" />
                    </form>
                    """);
            }

            if (request.RequestUri?.Host == "id.secondlife.com" &&
                request.RequestUri.AbsolutePath == "/openid/loginsubmit")
            {
                return Html("""
                    <form id="openid_message">
                      <input type="hidden" name="openid.mode" value="id_res" />
                    </form>
                    """);
            }

            if (request.RequestUri?.Host == "id.secondlife.com" &&
                request.RequestUri.AbsolutePath == "/openid/openidserver")
            {
                return Html("<html>ok</html>");
            }

            if (request.RequestUri?.Host == "accounts.secondlife.com" &&
                request.RequestUri.AbsolutePath == "/get_transaction_history_csv")
            {
                return Xml("""
                    <transactions>
                      <transaction>
                        <id>11111111-1111-1111-1111-111111111111</id>
                        <type>Payment</type>
                        <description>Rent payment</description>
                        <resident>Example Resident</resident>
                        <time>2026-05-22 20:15:00</time>
                        <end_balance>1000</end_balance>
                      </transaction>
                      <transaction>
                        <id>22222222-2222-2222-2222-222222222222</id>
                        <type>Payment</type>
                        <description>Payout</description>
                        <resident>Sample Resident</resident>
                        <time>2026-05-22 20:20:00</time>
                        <end_balance>750</end_balance>
                      </transaction>
                    </transactions>
                    """);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var client = CreateClient(handler);

        var result = await client.GetTransactionsAsync(
            new DateTimeOffset(2026, 5, 22, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 5, 23, 0, 0, 0, TimeSpan.Zero),
            CancellationToken.None);

        Assert.Equal(2, result.TransactionCount);
        Assert.Equal("11111111-1111-1111-1111-111111111111", result.Transactions[0].TransactionId);
        Assert.Equal(new DateTimeOffset(2026, 5, 23, 3, 15, 0, TimeSpan.Zero), result.Transactions[0].OccurredAtUtc);
        Assert.Null(result.Transactions[0].InferredAmountDelta);
        Assert.Equal(new DateTimeOffset(2026, 5, 23, 3, 20, 0, TimeSpan.Zero), result.Transactions[1].OccurredAtUtc);
        Assert.Equal(-250, result.Transactions[1].InferredAmountDelta);
        Assert.Contains(handler.Requests, request =>
            request.RequestUri?.Host == "accounts.secondlife.com" &&
            request.RequestUri.Query.Contains("startDate=2026-05-22", StringComparison.Ordinal) &&
            request.RequestUri.Query.Contains("endDate=2026-05-23", StringComparison.Ordinal));
        Assert.Contains(handler.RequestBodies, body =>
            body.Contains("username=Test+Resident", StringComparison.Ordinal) &&
            body.Contains("password=login-secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetTransactionsAsync_DefaultsUsernameToLoginAvatar()
    {
        var handler = new RecordingHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath == "/openid/login")
            {
                return Html("""<form id="loginform"><input type="hidden" name="csrf" value="abc" /></form>""");
            }

            if (request.RequestUri?.AbsolutePath == "/openid/loginsubmit")
            {
                return Html("""<form id="openid_message"><input type="hidden" name="openid.mode" value="id_res" /></form>""");
            }

            if (request.RequestUri?.AbsolutePath == "/openid/openidserver")
            {
                return Html("<html>ok</html>");
            }

            return request.RequestUri?.Host == "accounts.secondlife.com"
                ? Xml("<transactions />")
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var client = new SecondLifeAccountHistoryClient(
            Config(),
            NullLogger<SecondLifeAccountHistoryClient>.Instance,
            () => handler);

        await client.GetTransactionsAsync(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        Assert.Contains(handler.RequestBodies, body =>
            body.Contains("username=Test+Resident", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetTransactionsAsync_PreservesExplicitUtcTimestamps()
    {
        var handler = new RecordingHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath == "/openid/login")
            {
                return Html("""<form id="loginform"><input type="hidden" name="csrf" value="abc" /></form>""");
            }

            if (request.RequestUri?.AbsolutePath == "/openid/loginsubmit")
            {
                return Html("""<form id="openid_message"><input type="hidden" name="openid.mode" value="id_res" /></form>""");
            }

            if (request.RequestUri?.AbsolutePath == "/openid/openidserver")
            {
                return Html("<html>ok</html>");
            }

            if (request.RequestUri?.Host == "accounts.secondlife.com")
            {
                return Xml("""
                    <transactions>
                      <transaction>
                        <id>33333333-3333-3333-3333-333333333333</id>
                        <type>Payment</type>
                        <description>Test Rental</description>
                        <resident>Example Resident</resident>
                        <time>2026-06-05T10:29:31Z</time>
                        <end_balance>10910</end_balance>
                      </transaction>
                    </transactions>
                    """);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var client = CreateClient(handler);

        var result = await client.GetTransactionsAsync(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        Assert.Single(result.Transactions);
        Assert.Equal(new DateTimeOffset(2026, 6, 5, 10, 29, 31, TimeSpan.Zero), result.Transactions[0].OccurredAtUtc);
    }

    [Fact]
    public async Task GetTransactionsAsync_SameSecondRows_PreserveChronologicalBalanceSequence()
    {
        var handler = new RecordingHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath == "/openid/login")
            {
                return Html("""<form id="loginform"><input type="hidden" name="csrf" value="abc" /></form>""");
            }

            if (request.RequestUri?.AbsolutePath == "/openid/loginsubmit")
            {
                return Html("""<form id="openid_message"><input type="hidden" name="openid.mode" value="id_res" /></form>""");
            }

            if (request.RequestUri?.AbsolutePath == "/openid/openidserver")
            {
                return Html("<html>ok</html>");
            }

            if (request.RequestUri?.Host == "accounts.secondlife.com")
            {
                return Xml("""
                    <transactions>
                      <transaction>
                        <id>bbbbbbbb-bbbb-bbbb-bbbb-000000000003</id>
                        <type>Payment</type>
                        <description>Dorm 206</description>
                        <resident>Tenant Resident</resident>
                        <time>2026-08-07 15:33:33</time>
                        <end_balance>5160</end_balance>
                      </transaction>
                      <transaction>
                        <id>00000000-0000-0000-0000-000000000001</id>
                        <type>Gift</type>
                        <description>Second payout</description>
                        <resident>Second Resident</resident>
                        <time>2026-08-07 09:50:26</time>
                        <end_balance>5000</end_balance>
                      </transaction>
                      <transaction>
                        <id>ffffffff-ffff-ffff-ffff-000000000002</id>
                        <type>Gift</type>
                        <description>First payout</description>
                        <resident>First Resident</resident>
                        <time>2026-08-07 09:50:26</time>
                        <end_balance>10757</end_balance>
                      </transaction>
                      <transaction>
                        <id>aaaaaaaa-aaaa-aaaa-aaaa-000000000004</id>
                        <type>Payment</type>
                        <description>Earlier baseline</description>
                        <resident>Earlier Resident</resident>
                        <time>2026-08-07 09:00:00</time>
                        <end_balance>16514</end_balance>
                      </transaction>
                    </transactions>
                    """);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var client = CreateClient(handler);

        var result = await client.GetTransactionsAsync(
            new DateTimeOffset(2026, 8, 7, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 8, 0, 0, 0, TimeSpan.Zero),
            CancellationToken.None);

        Assert.Collection(
            result.Transactions,
            transaction =>
            {
                Assert.Equal("aaaaaaaa-aaaa-aaaa-aaaa-000000000004", transaction.TransactionId);
                Assert.Null(transaction.InferredAmountDelta);
            },
            transaction =>
            {
                Assert.Equal("ffffffff-ffff-ffff-ffff-000000000002", transaction.TransactionId);
                Assert.Equal(-5757, transaction.InferredAmountDelta);
            },
            transaction =>
            {
                Assert.Equal("00000000-0000-0000-0000-000000000001", transaction.TransactionId);
                Assert.Equal(-5757, transaction.InferredAmountDelta);
            },
            transaction =>
            {
                Assert.Equal("bbbbbbbb-bbbb-bbbb-bbbb-000000000003", transaction.TransactionId);
                Assert.Equal(160, transaction.InferredAmountDelta);
            });
    }

    [Fact]
    public async Task GetTransactionsAsync_RejectsUnexpectedXmlPayload()
    {
        var handler = new RecordingHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath == "/openid/login")
            {
                return Html("""<form id="loginform"><input type="hidden" name="csrf" value="abc" /></form>""");
            }

            if (request.RequestUri?.AbsolutePath == "/openid/loginsubmit")
            {
                return Html("""<form id="openid_message"><input type="hidden" name="openid.mode" value="id_res" /></form>""");
            }

            if (request.RequestUri?.AbsolutePath == "/openid/openidserver")
            {
                return Html("<html>ok</html>");
            }

            return request.RequestUri?.Host == "accounts.secondlife.com"
                ? Xml("<not-transactions />")
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var client = CreateClient(handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GetTransactionsAsync(
                DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow,
                CancellationToken.None));

        Assert.Contains("unexpected format", ex.Message);
    }

    private static SecondLifeAccountHistoryClient CreateClient(RecordingHandler handler)
        => new(
            Config("Test Resident"),
            NullLogger<SecondLifeAccountHistoryClient>.Instance,
            () => handler);

    private static BotConfig Config(string? username = null)
        => new()
        {
            Login = new BotLoginConfig
            {
                FirstName = "Test",
                LastName = "Resident",
                Password = "login-secret",
                Version = "0.1.0"
            },
            AccountHistory = new BotAccountHistoryConfig
            {
                Username = username,
                TimeoutSeconds = 5
            }
        };

    private static HttpResponseMessage Html(string body)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body)
        };

    private static HttpResponseMessage Xml(string body)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body)
        };

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);

            if (request.Content is not null)
            {
                RequestBodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            }

            return responder(request);
        }
    }
}

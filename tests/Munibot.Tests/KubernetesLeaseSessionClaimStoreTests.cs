using System.Net;
using System.Text;

namespace Munibot.Tests;

public sealed class KubernetesLeaseSessionClaimStoreTests
{
    [Fact]
    public async Task ReadMapsResourceVersionGenerationHolderAndPhase()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, """
            {
              "metadata": {
                "resourceVersion": "41",
                "annotations": { "munibase.io/session-claim-phase": "uncertain" }
              },
              "spec": {
                "holderIdentity": "opaque-process-nonce",
                "leaseTransitions": 6
              }
            }
            """));
        var store = new KubernetesLeaseSessionClaimStore(
            new HttpClient(handler) { BaseAddress = new Uri("https://kubernetes.default.svc") },
            "bots",
            "munibot-session");

        var state = await store.ReadAsync(CancellationToken.None);

        Assert.Equal("41", state.ResourceVersion);
        Assert.Equal(SessionClaimPhase.Uncertain, state.Phase);
        Assert.Equal("opaque-process-nonce", state.HolderIdentity);
        Assert.Equal(6, state.Generation);
        Assert.Equal(
            "/apis/coordination.k8s.io/v1/namespaces/bots/leases/munibot-session",
            handler.Requests.Single().Path);
    }

    [Fact]
    public async Task ConditionalReplaceUsesMergePatchResourceVersionAndNoLeaseTimeout()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, "{}"));
        var store = new KubernetesLeaseSessionClaimStore(
            new HttpClient(handler) { BaseAddress = new Uri("https://kubernetes.default.svc") },
            "bots",
            "munibot-session");
        var expected = SessionClaimState.Released("41", generation: 6);
        var replacement = expected with
        {
            Phase = SessionClaimPhase.Owned,
            HolderIdentity = "opaque-process-nonce",
            Generation = 7,
        };

        var replaced = await store.TryReplaceAsync(expected, replacement, CancellationToken.None);

        Assert.True(replaced);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Patch, request.Method);
        Assert.Equal("application/merge-patch+json", request.ContentType);
        var body = request.Body;
        Assert.Contains("\"resourceVersion\":\"41\"", body, StringComparison.Ordinal);
        Assert.Contains("\"leaseTransitions\":7", body, StringComparison.Ordinal);
        Assert.Contains("\"holderIdentity\":\"opaque-process-nonce\"", body, StringComparison.Ordinal);
        Assert.Contains("\"munibase.io/session-claim-phase\":\"owned\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("leaseDurationSeconds", body, StringComparison.Ordinal);
        Assert.DoesNotContain("renewTime", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConflictIsReportedAsFailedCompareAndSwap()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.Conflict, "{}"));
        var store = new KubernetesLeaseSessionClaimStore(
            new HttpClient(handler) { BaseAddress = new Uri("https://kubernetes.default.svc") },
            "bots",
            "munibot-session");
        var expected = SessionClaimState.Released("41");

        var replaced = await store.TryReplaceAsync(
            expected,
            expected with { Phase = SessionClaimPhase.Owned, HolderIdentity = "nonce", Generation = 1 },
            CancellationToken.None);

        Assert.False(replaced);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string content)
        => new(status)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json"),
        };

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri!.AbsolutePath,
                request.Content?.Headers.ContentType?.MediaType,
                request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult() ?? string.Empty));
            return Task.FromResult(respond(request));
        }
    }

    private sealed record RecordedRequest(
        HttpMethod Method,
        string Path,
        string? ContentType,
        string Body);
}

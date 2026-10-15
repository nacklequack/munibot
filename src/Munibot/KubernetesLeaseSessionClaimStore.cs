using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace Munibot;

internal sealed class KubernetesLeaseSessionClaimStore(
    HttpClient httpClient,
    string @namespace,
    string leaseName) : ISessionClaimStore
{
    internal const string PhaseAnnotation = "munibase.io/session-claim-phase";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _leasePath =
        $"/apis/coordination.k8s.io/v1/namespaces/{Uri.EscapeDataString(@namespace)}/leases/{Uri.EscapeDataString(leaseName)}";

    public async Task<SessionClaimState> ReadAsync(CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(_leasePath, cancellationToken);
        response.EnsureSuccessStatusCode();
        var lease = await response.Content.ReadFromJsonAsync<KubernetesLease>(
            JsonOptions,
            cancellationToken) ?? throw new InvalidOperationException("The Kubernetes Lease response was empty.");

        var resourceVersion = lease.Metadata?.ResourceVersion;
        if (string.IsNullOrWhiteSpace(resourceVersion))
        {
            throw new InvalidOperationException("The Kubernetes Lease has no resourceVersion.");
        }

        var phaseText = lease.Metadata?.Annotations?.GetValueOrDefault(PhaseAnnotation)
            ?? throw new InvalidOperationException("The Kubernetes Lease has no session claim phase.");
        if (!Enum.TryParse<SessionClaimPhase>(phaseText, ignoreCase: true, out var phase))
        {
            throw new InvalidOperationException("The Kubernetes Lease has an unknown session claim phase.");
        }

        return new SessionClaimState(
            resourceVersion,
            phase,
            lease.Spec?.HolderIdentity,
            lease.Spec?.LeaseTransitions ?? 0);
    }

    public async Task<bool> TryReplaceAsync(
        SessionClaimState expected,
        SessionClaimState replacement,
        CancellationToken cancellationToken)
    {
        var patch = new KubernetesLease
        {
            Metadata = new KubernetesMetadata
            {
                ResourceVersion = expected.ResourceVersion,
                Annotations = new Dictionary<string, string>
                {
                    [PhaseAnnotation] = replacement.Phase.ToString().ToLowerInvariant(),
                },
            },
            Spec = new KubernetesLeaseSpec
            {
                HolderIdentity = replacement.HolderIdentity,
                LeaseTransitions = replacement.Generation,
            },
        };
        using var request = new HttpRequestMessage(HttpMethod.Patch, _leasePath)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(patch, JsonOptions),
                Encoding.UTF8,
                "application/merge-patch+json"),
        };
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Conflict)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        return true;
    }

    public static KubernetesLeaseSessionClaimStore FromPodEnvironment()
    {
        var host = RequiredEnvironmentVariable("KUBERNETES_SERVICE_HOST");
        var port = RequiredEnvironmentVariable("KUBERNETES_SERVICE_PORT_HTTPS");
        var @namespace = RequiredEnvironmentVariable("POD_NAMESPACE");
        var leaseName = RequiredEnvironmentVariable("SESSION_CLAIM_NAME");
        const string tokenPath = "/var/run/secrets/kubernetes.io/serviceaccount/token";
        const string caPath = "/var/run/secrets/kubernetes.io/serviceaccount/ca.crt";

        var trustedRoots = new X509Certificate2Collection();
        trustedRoots.ImportFromPemFile(caPath);
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, _, sslPolicyErrors) =>
                ValidateServerCertificate(certificate, trustedRoots, sslPolicyErrors),
        };
        var client = new HttpClient(new ServiceAccountTokenHandler(tokenPath, handler))
        {
            BaseAddress = new Uri($"https://{host}:{port}"),
        };

        return new KubernetesLeaseSessionClaimStore(client, @namespace, leaseName);
    }

    private static bool ValidateServerCertificate(
        X509Certificate2? certificate,
        X509Certificate2Collection trustedRoots,
        SslPolicyErrors sslPolicyErrors)
    {
        if (certificate is null || sslPolicyErrors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
        {
            return false;
        }

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(trustedRoots);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(certificate);
    }

    private static string RequiredEnvironmentVariable(string name)
        => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Required environment variable {name} is not set.");

    private sealed class ServiceAccountTokenHandler(string tokenPath, HttpMessageHandler innerHandler)
        : DelegatingHandler(innerHandler)
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                File.ReadAllText(tokenPath).Trim());
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class KubernetesLease
    {
        public KubernetesMetadata? Metadata { get; init; }
        public KubernetesLeaseSpec? Spec { get; init; }
    }

    private sealed class KubernetesMetadata
    {
        public string? ResourceVersion { get; init; }
        public Dictionary<string, string>? Annotations { get; init; }
    }

    private sealed class KubernetesLeaseSpec
    {
        public string? HolderIdentity { get; init; }
        public long? LeaseTransitions { get; init; }
    }
}

using System.Net;
using System.Text;
using OpenSearch.Net;
using PointOfSale.Infrastructure.Configuration;

namespace PointOfSale.Infrastructure.Marketplace.Generation;

/// <summary>
/// Dedicated single-endpoint generation transport. No redirects, proxy, cookies,
/// default credentials, discovery, application retry policy or DI registration.
/// TLS/physical observations do NOT prove server-side role/lifecycle isolation.
/// </summary>
internal sealed class MarketplaceGenerationTransport : IDisposable
{
    private readonly HttpClient client;
    private readonly IOpenSearchLowLevelClient provider;
    private readonly bool deterministicHandler;
    private const int MaxResponseBytes = 16 * 1024 * 1024;

    internal MarketplaceGenerationTransport(Uri endpoint, string username, string password, ServerCertificatePolicy policy)
        : this(endpoint, username, password, SecureHandler(policy))
    {
        deterministicHandler = false;
    }

    // Deterministic transport controls only; a supplied test handler cannot provide
    // remote certification. Provider tests use the certificate-validating overload.
    internal MarketplaceGenerationTransport(Uri endpoint, string username, string password, HttpMessageHandler handler)
    {
        deterministicHandler = true;
        ValidateEndpoint(endpoint, username, password);
        client = new HttpClient(new BoundedResponseHandler(handler), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        // Both deterministic handler tests and physical-provider use pass through
        // the supported client. Its caller-owned HttpClient has explicit handler
        // policy; no ambient redirects/proxy/cookies or automatic mutation retry.
        var settings = new ConnectionConfiguration(new SingleNodeConnectionPool(endpoint), new HttpConnection(client))
            .BasicAuthentication(username, password).MaximumRetries(0).DisablePing()
            .DisableAutomaticProxyDetection().RequestTimeout(TimeSpan.FromSeconds(30));
        provider = new OpenSearchLowLevelClient(settings);
    }

    internal async Task<string> SendAsync(System.Net.Http.HttpMethod method, string path, string? body, CancellationToken ct)
    {
        RequireRawOperationAllowed(deterministicHandler, method, path);
        return await SendCoreAsync(method, path, body, ct);
    }

    // Only the gateway may turn a committed durable admission into the fixed
    // reviewed update. No arbitrary path, script, bulk item, alias or payload API.
    internal Task<string> SendGatewayUpdateAsync(GenerationDispatchPermit permit, CancellationToken ct)
    {
        if (permit.Attempt == Guid.Empty) throw new InvalidDataException("Durable gateway attempt required.");
        permit.Operation.Validate();
        return SendCoreAsync(System.Net.Http.HttpMethod.Post,
            "/" + permit.Operation.Target.PhysicalName + "/_update/" + permit.Operation.Ticket.ServiceId.ToString("D") + "?retry_on_conflict=0",
            MarketplaceVersionedDocument.BuildUpdate(permit.Operation.Target.Journal, permit.Operation.Ticket, permit.Operation.Payload), ct);
    }

    internal Task<string> RefreshGatewayAsync(MarketplacePhysicalAdmission.Expected target, CancellationToken ct)
    {
        MarketplacePhysicalAdmission.Validate(target);
        return SendCoreAsync(System.Net.Http.HttpMethod.Post, "/" + target.PhysicalName + "/_refresh", null, ct);
    }

    internal Task<string> SendRetirementDeleteAsync(GenerationRetirementPermit permit, CancellationToken ct)
    {
        if (permit.Operation == Guid.Empty) throw new InvalidDataException("Durable retirement authorization required.");
        MarketplacePhysicalAdmission.Validate(permit.Target);
        return SendCoreAsync(System.Net.Http.HttpMethod.Delete, "/" + permit.Target.PhysicalName, null, ct);
    }

    private async Task<string> SendCoreAsync(System.Net.Http.HttpMethod method, string path, string? body, CancellationToken ct)
    {
        // Paths are built only by the protocol. Reject alternate origins, encoded
        // path selectors and fragments instead of resolving caller-supplied URLs.
        if (!path.StartsWith('/') || path.StartsWith("//", StringComparison.Ordinal) || path.Contains('%') || path.Contains('#') || path.Contains('\\'))
            throw new ArgumentException("Exact local generation path required.");
        ct.ThrowIfCancellationRequested();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(30));
        ct = budget.Token; // Covers headers AND the complete bounded response body.
        var verb = method == System.Net.Http.HttpMethod.Get ? OpenSearch.Net.HttpMethod.GET :
                method == System.Net.Http.HttpMethod.Post ? OpenSearch.Net.HttpMethod.POST :
                method == System.Net.Http.HttpMethod.Delete && body is null &&
                    System.Text.RegularExpressions.Regex.IsMatch(path, "^/marketplace_services_g_[0-9a-f]{32}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)
                    ? OpenSearch.Net.HttpMethod.DELETE :
                throw new ArgumentException("Generation transport permits only reviewed exact physical operations.");
        // Raw SDK paths must not contain '?': the installed provider can escape it
        // as part of the path. Use its native parameter objects, not a preencoded URI.
        IRequestParameters? parameters = null;
        var separator = path.IndexOf('?');
        if (separator >= 0)
        {
            var query = path[(separator + 1)..];
            path = path[..separator];
            if (query == "retry_on_conflict=0" && method == System.Net.Http.HttpMethod.Post && path.Contains("/_update/", StringComparison.Ordinal))
            {
                parameters = new UpdateRequestParameters();
                parameters.SetQueryString("retry_on_conflict", 0);
            }
            else if (query == "realtime=false" && method == System.Net.Http.HttpMethod.Post && path.EndsWith("/_mget", StringComparison.Ordinal))
            {
                parameters = new MultiGetRequestParameters();
                parameters.SetQueryString("realtime", false);
            }
            else throw new ArgumentException("Only exact reviewed generation request parameters are permitted.");
        }
        var reply = await provider.DoRequestAsync<StringResponse>(verb, path, ct, body is null ? null : PostData.String(body), parameters);
        ct.ThrowIfCancellationRequested();
        if (!reply.Success || string.IsNullOrEmpty(reply.Body))
            throw new HttpRequestException("Generation request not acknowledged; mutation outcome remains unknown.", reply.OriginalException, (HttpStatusCode?)reply.HttpStatusCode);
        if (Encoding.UTF8.GetByteCount(reply.Body) > MaxResponseBytes)
            throw new InvalidDataException("Generation response exceeds bounded verification budget.");
        return reply.Body;
    }

    internal static void RequireRawOperationAllowed(bool deterministicTestHandler, System.Net.Http.HttpMethod method, string path)
    {
        // No production raw mutation may bypass the required atomic write boundary.
        // The custom-handler overload is an internal deterministic-test seam only;
        // it is never DI registered or a remotely certified implementation.
        if (!deterministicTestHandler && (method != System.Net.Http.HttpMethod.Get &&
            !(method == System.Net.Http.HttpMethod.Post &&
              (path.EndsWith("/_mget?realtime=false", StringComparison.Ordinal) || path.EndsWith("/_mget", StringComparison.Ordinal) || path.EndsWith("/_count", StringComparison.Ordinal)))))
            throw new InvalidOperationException("GENERATION_ATOMIC_WRITE_BOUNDARY_NOT_INSTALLED");
    }

    internal void RequireLegacyFixture()
    {
        if (!deterministicHandler) throw new InvalidOperationException("GENERATION_ATOMIC_WRITE_BOUNDARY_NOT_INSTALLED");
    }

    private static void ValidateEndpoint(Uri endpoint, string username, string password)
    {
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme != Uri.UriSchemeHttps || endpoint.AbsolutePath != "/" ||
            endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 ||
            string.IsNullOrWhiteSpace(username) || username.Contains(':') || string.IsNullOrEmpty(password))
            throw new ArgumentException("One HTTPS origin and dedicated explicit credentials required.");
    }

    public void Dispose()
    {
        client.Dispose();
        if (provider is IDisposable disposable) disposable.Dispose();
    }

    private static HttpMessageHandler SecureHandler(ServerCertificatePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return new HttpClientHandler
        {
            AllowAutoRedirect = false, UseCookies = false, UseProxy = false, UseDefaultCredentials = false,
            ServerCertificateCustomValidationCallback = policy.Validate
        };
    }

    private sealed class BoundedResponseHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            try
            {
                if (response.Content.Headers.ContentLength > MaxResponseBytes)
                    throw new InvalidDataException("Generation response exceeds bounded verification budget.");
                // Enforce the cap BEFORE the supported client's StringResponse
                // buffering, including chunked/misreported-length responses.
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var buffer = new MemoryStream();
                var chunk = new byte[16 * 1024];
                while (true)
                {
                    var count = await stream.ReadAsync(chunk, cancellationToken);
                    if (count == 0) break;
                    if (buffer.Length + count > MaxResponseBytes) throw new InvalidDataException("Generation response exceeds bounded verification budget.");
                    buffer.Write(chunk, 0, count);
                }
                var prior = response.Content; var bounded = new ByteArrayContent(buffer.ToArray());
                foreach (var header in prior.Headers) bounded.Headers.TryAddWithoutValidation(header.Key, header.Value);
                response.Content = bounded; prior.Dispose();
                return response;
            }
            catch { response.Dispose(); throw; }
        }
    }
}

using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using PointOfSale.Infrastructure.Configuration;

namespace PointOfSale.Infrastructure.Marketplace.Generation;

/// <summary>Application-side gateway client: mTLS, one HTTPS origin, NO provider write credentials.</summary>
internal sealed class MarketplaceGenerationRemoteBoundary : IGenerationAtomicWriteBoundary, IDisposable
{
    private readonly HttpClient client;
    private readonly Uri endpoint;
    private const int MaximumBytes = 16 * 1024 * 1024;

    internal MarketplaceGenerationRemoteBoundary(Uri endpoint, X509Certificate2 identity, ServerCertificatePolicy serverPolicy)
        : this(endpoint, Handler(identity, serverPolicy)) { }

    internal MarketplaceGenerationRemoteBoundary(Uri endpoint, HttpMessageHandler deterministicHandler)
    {
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme != "https" || endpoint.AbsolutePath != "/" ||
            endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 || endpoint.UserInfo.Length != 0)
            throw new ArgumentException("One exact HTTPS gateway origin required.");
        this.endpoint = endpoint;
        client = new(deterministicHandler) { Timeout = TimeSpan.FromSeconds(30) };
    }

    public Task<GenerationBoundaryReceipt?> LookupAsync(GenerationOperation operation, CancellationToken ct) => SendAsync("lookup", operation, ct);
    public async Task<GenerationBoundaryReceipt> SubmitAsync(GenerationOperation operation, CancellationToken ct) =>
        await SendAsync("submit", operation, ct) ?? throw new InvalidDataException("Gateway submission receipt missing.");
    public async Task<GenerationBoundaryReceipt> RefreshAndVerifyAsync(GenerationOperation operation, CancellationToken ct) =>
        await SendAsync("verify", operation, ct) ?? throw new InvalidDataException("Gateway visibility receipt missing.");

    private async Task<GenerationBoundaryReceipt?> SendAsync(string phase, GenerationOperation operation, CancellationToken ct)
    {
        operation.Validate();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(30));
        ct = budget.Token;
        var json = JsonSerializer.Serialize(operation);
        if (Encoding.UTF8.GetByteCount(json) > MaximumBytes) throw new InvalidDataException("Gateway operation exceeds request budget.");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, "/generation/" + phase))
            { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaximumBytes) throw new InvalidDataException("Gateway response exceeds budget.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream(); var chunk = new byte[16 * 1024];
        while (true)
        {
            var count = await stream.ReadAsync(chunk, ct); if (count == 0) break;
            if (buffer.Length + count > MaximumBytes) throw new InvalidDataException("Gateway response exceeds budget.");
            buffer.Write(chunk, 0, count);
        }
        var receipt = JsonSerializer.Deserialize<GenerationBoundaryReceipt>(buffer.ToArray());
        if (receipt is not null) MarketplaceGenerationOperationRecovery.RequireSame(operation, receipt.Operation);
        return receipt;
    }

    private static HttpMessageHandler Handler(X509Certificate2 identity, ServerCertificatePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!identity.HasPrivateKey) throw new ArgumentException("Gateway client identity requires its private key.");
        var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false,
            UseDefaultCredentials = false, ClientCertificateOptions = ClientCertificateOption.Manual,
            ServerCertificateCustomValidationCallback = policy.Validate };
        handler.ClientCertificates.Add(identity); return handler;
    }

    public void Dispose() => client.Dispose();
}

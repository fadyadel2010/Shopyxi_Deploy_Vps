using System.Text.Json;
using Npgsql;
using PointOfSale.Infrastructure.Configuration;

namespace PointOfSale.Infrastructure.Marketplace.Generation;

/// <summary>Isolated-process facade. Only that process receives provider update credentials; never registered in the application graph.</summary>
public sealed class MarketplaceGenerationGatewayServer : IDisposable
{
    private readonly MarketplaceGenerationTransport transport;
    private readonly MarketplaceGenerationOperationStore store;
    private readonly MarketplaceGenerationGateway boundary;

    public MarketplaceGenerationGatewayServer(NpgsqlDataSource source, Uri endpoint, string username, string password, string? caPath)
    {
        transport = new(endpoint, username, password, new ServerCertificatePolicy(caPath));
        store = new(source);
        boundary = new(new MarketplaceGenerationGatewayJournal(source), new MarketplaceGenerationGatewayProvider(transport, store));
    }

    public async Task<string> ExecuteAsync(string phase, string request, CancellationToken ct)
    {
        if (request.Length > 16 * 1024 * 1024 || phase is not ("lookup" or "submit" or "verify"))
            throw new ArgumentException("Bounded exact gateway operation required.");
        var operation = JsonSerializer.Deserialize<GenerationOperation>(request) ?? throw new InvalidDataException("Missing operation.");
        operation.Validate();
        var durable = await store.ReadAsync(operation.Id, ct);
        MarketplaceGenerationOperationRecovery.RequireSame(durable.Operation, operation);
        var receipt = phase switch
        {
            "lookup" => await boundary.LookupAsync(operation, ct),
            "submit" => await boundary.SubmitAsync(operation, ct),
            _ => await boundary.RefreshAndVerifyAsync(operation, ct)
        };
        return JsonSerializer.Serialize(receipt);
    }

    public void Dispose() => transport.Dispose();
}

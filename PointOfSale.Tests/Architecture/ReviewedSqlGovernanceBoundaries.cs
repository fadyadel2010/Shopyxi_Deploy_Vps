using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PointOfSale.Tests.Architecture;

// Versioned execution-boundary contracts, not a filename allowlist. Each entry
// binds the individually reviewed statements, their complete containing source,
// and the production caller/helper context. Changed SQL/authority behavior needs
// independent review/provider proof. Unchanged statements continue only through
// exact per-statement entries plus reviewed context deltas, not a blanket re-pin.
// Neither a containing service nor deployment is approved.
// Original19 numbering and the four pre-existing restricted contracts are separate.
internal static class ReviewedSqlGovernanceBoundaries
{
    internal const string HistoricalProductionContext = "0f93d8f7e2ed956cd821c8a448a5bfda0d7867ccf18fdde83fda63c309585752";
    internal const string ProductionContext = "715ac764dc1f86828793376696a73aa325c46a8d375452602b4a1b9ec40b9ae4";
    internal sealed record Contract(int? OriginalOrdinal, string Path, string SourceHash,
        int StatementCount, string StatementHash, string Scope);

    internal static readonly Contract[] Contracts =
    [
        new(1, "PointOfSale.API/Controllers/AffiliateAuthController.cs",
            "f166a6d19e9a56c7c53b65239bf3c80cb3a288b6a812168084cbf7afe9a183c3", 16,
            "7818fe54f4041eca562ed9161506aef7b5d061059790b919f446d9785d45333f",
            "Exact global affiliate identity/account/token CAS and row locks; owned credential/audit transactions, post-lock database expiry and replay evidence. No tenant-user or financial authorization."),
        new(2, "PointOfSale.API/Controllers/CashboxController.cs",
            "c6e755bda0f219e13153ebe50b6c782e3af80185e28c5abd7a48de4ef4c5a907", 5,
            "53fce639c394f6f4abc6019aeab9771211a86de227ddf219fd93de18e68aef40",
            "Same-tenant persisted actor/warehouse/cashbox locks and open-state close CAS through audit; canonical ledger posting remains separate."),
        new(4, "PointOfSale.API/Infrastructure/Idempotency/ProductMutationIdempotencyService.cs",
            "ca4ffeb650867fdab55686a23cc76a8f6bdbba239a4e595d58c9763d78b0436a", 7,
            "4bb1ce6f2a5e01e9e930aa87955838e21c24205c7edc3d6273ae344ddb12de0a",
            "Tenant/operation/key/fingerprint claim and exact durable owner CAS; acquisition/renewal/completion never grants product permission or remote exactly-once delivery."),
        new(5, "PointOfSale.API/Jobs/CheckoutAttachmentCleanupJob.cs",
            "6eb68b4848a715c26bda298a3e236cb2d29d76ad5aa1e5ca17f06c3167b804c6", 2,
            "30a45755d21b5fcf5ef4829258bb211da5a5ad446d3c2b8a8f87baa6b88297c2",
            "Global cleanup scan followed by exact tenant/cart/owner/object identity and durable Deleting-state CAS. Storage outcomes are not rolled back by SQL."),
        new(6, "PointOfSale.API/Services/Admin/GeographicAdministrationService.CatalogWorkflow.cs",
            "e41e096b9642f5e52c2bcfc9d9c82e77a06886d1709459d70851745e8fbedae9", 6,
            "8382cc8bc965ca63085d591da2742d5753f340863d542e27f1ff0559556dfffb",
            "All 11 tenant writers hold current tenant/actor/session/governance through metadata/audit commit. Global catalog advisory serialization is not tenant permission to publish platform catalog."),
        new(7, "PointOfSale.API/Services/AffiliateQaPayoutSimulation.cs",
            "f672d1ddc0af5543f1d53ba47a7e64e24b28db506e9dc3ae218deeddd76531eb", 1,
            "22a03ab587e5ab5517d89d28cab7859782b4cb5b0f3b18962090e2ad0fb6682e",
            "Exact parameterized transaction advisory lock derived from reviewed simulation request. Enablement/expiry/marker/acknowledgement remain mandatory. No payout, transfer or containing QA-service certification."),
        new(8, "PointOfSale.API/Services/Checkout/CheckoutAttachmentService.cs",
            "e6b3100ec153457222701cb12aa2ba3d5f1a6b963cd88f8c8c14d773f2f234ba", 7,
            "9fdd21f61adf359f4b7363bf4ac51865b0c8a78d91447872b3c62ce715518c0b",
            "Persisted tenant/cart/field/owner/session/customer row locks and state CAS; attachment claims require caller order transaction/savepoint. No arbitrary key or rollback of remote copy."),
        new(9, "PointOfSale.API/Services/CommerceHub/Finance/CommerceHubFinancialSafetyGuardService.cs",
            "6fd96faa23b15d12dd0b02562119e7131d4d90354f204e045fa61ed1b6f2b47a", 4,
            "66beaf16949036c212e6ca60f9f52ecfa51bfbc2d0e5bf69cb08d043c9a3cdc3",
            "Insert-only fixed platform coordination row and exact vendor/platform capture locks inside caller transaction. Business permission and canonical financial posting are not granted here."),
        new(12, "PointOfSale.API/Services/Users/PosUserManagementService.cs",
            "897545c45e7a7d1565b8645c2b9612984ac837c70bf71870baa1e36257dbaad4", 2,
            "ac0a998271a3d83a34183009ac0b0e66585e49e4d881a364ea215994d6b5b83d",
            "Tenant lock then ordered same-tenant user locks, clean tracker/reload and held fresh session/governance. Administrative actor and anonymous token-ceremony authorities remain distinct."),
        new(13, "PointOfSale.Infrastructure/Analytics/Delivery/ProviderDeliveryProcessor.cs",
            "74b9d23bd2bd8e7575e78d398fba823d4dbc750fd1d37fc3511baefaf628029b", 4,
            "209564d35002db9c8a39a900899d93097e4fd529146d5c07add72546404ad170",
            "Global system queue claims/outcomes bind persisted tenant/provider/id/key/payload/owner. Exact message lock precedes database clock. No arbitrary tenant-facing queue grant or exactly-once provider promise."),
        new(15, "PointOfSale.Infrastructure/Payments/PaymentLifecycleService.CarrierRemittance.cs",
            "6dc891cfd4ba59610f04d6cddaa4a57319c9a66f005de155124f2cb2516f5f99", 5,
            "524b12a367ab3f49f41707df84cbe8ba12f97da547518b1153f7dcc55202c79e",
            "Tenant actor, ordered source orders, treasury/ledger locks and database clock within canonical owned financial transaction; evidence/fingerprint/audit/outbox checks remain mandatory. Fictional ledger proof only."),
        new(16, "PointOfSale.Infrastructure/Services/Affiliate/AffiliateLandingPageDesignStore.cs",
            "fec0c37f05edacbb1ca30e395b5ec0c40c7e3bb9090441700f3c4c10e6149639", 3,
            "aa712b0cd996b259d74e0013f20f48196d61acac8c085065e6c79ab9236e2adb",
            "Fixed provider CTE/upsert and current global affiliate user/account session plus same-tenant eligible product/policy/category lineage. Exact account/tenant/product conflict identity only."),
        new(17, "PointOfSale.Infrastructure/Services/Affiliate/AffiliateSettlementOrchestrator.Manual.cs",
            "70a6736216d4dc556f5dd4ec4c291ea9ed5f3d8b1eb178ba2ccea4773572e48b", 3,
            "acaa255176692ee27fd1636970a4c05f61ff76825b0b5a744a20362e1eb9097a",
            "Current same-store manual actor and reviewed cashbox/treasury source locks through canonical posting/settlement/audit/outbox transaction. No arbitrary payout or actual external cash movement approval."),
        new(18, "PointOfSale.Infrastructure/Services/NativeAbandonedCartRecoveryScheduler.cs",
            "e9e82b596872181d50c35e571c8bd986e9e72bfa37ad2666866b5a45db7ac303", 12,
            "83ab367f608e89f44b48537555bb42413fdb36c639696ab0e9df6f36f2735a94",
            "Exact tenant/cart/schedule/sequence-owner CAS; schedule then full cart row held through fresh lifecycle dispatch/outcome. Canceled is mapped; IsDeleted is deliberately not. External dispatch remains at-least-once."),
        new(19, "PointOfSale.Infrastructure/Shipping/ShippingWebhookClaimProtocol.cs",
            "344a8e54fbb571e692ca74e6a27ed900aabc2c7d4729653b9c46534b9b554692", 7,
            "80ab711fc92977e0ee1b40dd8ec179b9ed48ae43bac045b0d31acb48f8a2188f",
            "Verified full inbox identity, NOWAIT admission, monotone generation/exact owner/database-clock lease and fenced business transaction. Stale failure cannot overwrite new owner. Feature/deployment remains disabled."),
        new(null, "PointOfSale.API/Security/PosSessionAuthority.cs",
            "2b40fa3f3e0bfdd69dc9c0e9915cf1574be32ffa795ed3936332da7aa7dd62d9", 4,
            "d3f7e38575e6c0985456dd83ee828e09634481f46b53ae1b98a286a7942db423",
            "Shared authority supplement, not original ordinal or SQL20: database-clock fresh token validation and held insert-only neutral governance. Existing policies never overwritten; neutral initialization never grants certification.")
    ];

    internal static bool IsApproved(string path, string source, string productionContext)
    {
        if (productionContext != ProductionContext) return false;
        var contract = Contracts.SingleOrDefault(c => c.Path == path);
        if (contract is null || Hash(Normalize(source)) != contract.SourceHash) return false;
        if (!ReviewedSqlStatementContinuations.Exact(path, source, productionContext)) return false;
        var fingerprint = Statements(source);
        return fingerprint is not null && fingerprint.Value.Count == contract.StatementCount &&
            fingerprint.Value.Hash == contract.StatementHash;
    }

    internal static void AssertRefusalControls(string root, string productionContext)
    {
        Assert.Equal(ProductionContext, productionContext);
        foreach (var contract in Contracts)
        {
            var source = File.ReadAllText(System.IO.Path.Combine(root, contract.Path));
            Assert.True(IsApproved(contract.Path, source, productionContext), contract.Path + ": exact reviewed SQL contract changed; individual re-review/provider proof required.");
            Assert.False(IsApproved(contract.Path, source, productionContext + "changed-caller"));
            Assert.False(IsApproved("unreviewed/" + contract.Path, source, productionContext));
            Assert.False(IsApproved(contract.Path, source + "\n// unreviewed authority/control-flow edit\n", productionContext));
            var first = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(preprocessorSymbols: ["DEBUG"])).GetRoot()
                .DescendantNodes().OfType<InvocationExpressionSyntax>().First(IsSqlPrimitive);
            Assert.False(IsApproved(contract.Path, source.Remove(first.SpanStart, first.Span.Length)
                .Insert(first.SpanStart, "unreviewed.ExecuteSqlRawAsync(\"DELETE FROM UnreviewedAuthority\")"), productionContext));
        }
    }

    internal static string ReadProductionContext(string root) => ContextHash(
        new[] { "PointOfSale.API", "PointOfSale.Core", "PointOfSale.Infrastructure" }
            .SelectMany(project => Directory.EnumerateFiles(System.IO.Path.Combine(root, project), "*.cs", SearchOption.AllDirectories))
            .Select(path => (Path: System.IO.Path.GetRelativePath(root, path).Replace('\\', '/'), Source: path))
            .Where(row => !row.Path.Split('/').Any(part => part is "bin" or "obj" or ".git"))
            .Select(row => (row.Path, SourceHash: Hash(Normalize(File.ReadAllText(row.Source))))));

    internal static string ContextHash(IEnumerable<(string Path, string SourceHash)> rows) =>
        Hash(string.Join("\n", rows.OrderBy(row => row.Path, StringComparer.Ordinal).Select(row => row.Path + "|" + row.SourceHash)));

    internal static (int Count, string Hash)? Statements(string source)
    {
        // Normalize before parsing: raw multiline string tokens otherwise retain
        // CRLF even though the containing-source pin already normalizes it.
        var root = CSharpSyntaxTree.ParseText(Normalize(source), new CSharpParseOptions(preprocessorSymbols: ["DEBUG"])).GetRoot();
        if (root.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error)) return null;
        var statements = root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(IsSqlPrimitive)
            .Select(call => (call.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText ?? "") + "|" +
                ((MemberAccessExpressionSyntax)call.Expression).Name.Identifier.ValueText + "|" +
                Hash(call.WithoutTrivia().NormalizeWhitespace().ToFullString())).ToArray();
        return (statements.Length, Hash(string.Join("\n", statements)));
    }

    internal static bool IsSqlPrimitive(InvocationExpressionSyntax call) => call.Expression is MemberAccessExpressionSyntax member &&
        member.Name.Identifier.ValueText is "ExecuteUpdateAsync" or "ExecuteDeleteAsync" or "ExecuteSqlAsync" or "ExecuteSqlRawAsync" or "ExecuteSqlInterpolatedAsync" or
            "FromSql" or "FromSqlRaw" or "FromSqlInterpolated" or "SqlQuery" or "SqlQueryRaw" or "ExecuteScalarAsync" or "ExecuteReaderAsync" or "ExecuteNonQueryAsync";

    internal static string Normalize(string source) => source.TrimStart('\uFEFF').Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    internal static string Hash(string source) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
}

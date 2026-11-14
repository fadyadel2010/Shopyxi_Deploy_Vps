using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Npgsql;
using PointOfSale.Infrastructure.Configuration;
using PointOfSale.Infrastructure.Marketplace.Generation;

namespace PointOfSale.Tests.Marketplace;

/// <summary>Requires separately admitted, owned, actual PostgreSQL + OpenSearch fixtures.</summary>
public sealed class MarketplaceGenerationCrossProviderTests
{
    [Fact]
    public Task RealDeliveryCutoverRollbackCatchupAndPhysicalRetirementUseRestrictedProviderRoles() =>
        MarketplaceSourceJournalPostgresTests.ExecuteOwnedCrossProviderScenarioAsync();
}

public sealed partial class MarketplaceSourceJournalPostgresTests
{
    internal static async Task ExecuteOwnedCrossProviderScenarioAsync()
    {
        await using var f = await Fixture.Create(applicationSchema: true);
        await using var recoverySource = NpgsqlDataSource.Create(f.WriterConnection);
        var seed = await SeedProjection(f);
        using var provider = new OwnedGenerationProvider();
        var delivery = new MarketplaceGenerationRunDelivery(); var transition = new MarketplaceGenerationTransition();
        IGenerationAtomicWriteBoundary Boundary(OwnedGenerationProvider.Target target) => new MarketplaceGenerationGateway(
            new MarketplaceGenerationGatewayJournal(recoverySource),
            new MarketplaceGenerationGatewayProvider(target.Dispatch, new MarketplaceGenerationOperationStore(recoverySource)));
        var lease = (await MarketplaceGenerationRegistry.TryAcquireAsync(f.Db, Guid.NewGuid(), TimeSpan.FromMinutes(2)))!;
        async Task<OwnedGenerationProvider.Target> Build()
        {
            lease = await MarketplaceGenerationRegistry.RenewAsync(f.Db, lease, TimeSpan.FromMinutes(2));
            var build = await MarketplaceGenerationRegistry.CreateBuildAsync(f.Db, lease, Guid.NewGuid());
            var target = await provider.CreateAsync(lease.JournalId, build.GenerationId);
            await MarketplaceGenerationRegistry.BindPhysicalIdentityAsync(f.Db, lease, build.GenerationId, target.Physical.PhysicalUuid, target.Physical.Fingerprint);
            var position = await MarketplaceGenerationRegistry.SnapshotAsync(f.Db, lease, build.GenerationId);
            var frozen = await MarketplaceGenerationInputExport.FreezeAsync(f.Db, lease, build.GenerationId, Guid.NewGuid(), position);
            var run = await MarketplaceFrozenOutput.CreateAsync(f.Db, lease, frozen.ExportId, Guid.NewGuid());
            await MarketplaceFrozenOutput.CommitNextPageAsync(f.Db, lease, run.RunId, Guid.NewGuid(), run.Cursor, 128);
            await MarketplaceFrozenOutput.SealAsync(f.Db, lease, run.RunId, Guid.NewGuid());
            target.Run = run.RunId;
            await delivery.DeliverRecoverablyAsync(recoverySource, f.Db, lease, run.RunId, target.Physical, target.Dispatch, Boundary(target));
            Assert.True(await MarketplaceExternalWriteBarrier.CloseAndTryDrainAsync(f.Db, lease, target.Physical.Generation));
            return target;
        }
        try
        {
            var first = await Build();
            await provider.AssertDispatcherDenialsAsync(first);
            await transition.ExecuteAsync(f.Db, lease, Guid.NewGuid(), first.Run, false, 0, null, first.Physical, first.Dispatch);
            var pin = await MarketplaceGenerationReaderPins.AcquireAsync(f.Db, seed.Tenant, Guid.NewGuid(), new string('a', 64), TimeSpan.FromMinutes(2));
            var second = await Build();
            var cutover = Guid.NewGuid();
            void Crash(MarketplaceGenerationLifecycleStore.CutoverBoundary boundary)
            { if (boundary == MarketplaceGenerationLifecycleStore.CutoverBoundary.AfterPointerCommit) throw new InjectedLifecycleFailure(); }
            await Assert.ThrowsAsync<InjectedLifecycleFailure>(() => transition.ExecuteAsync(f.Db, lease, cutover, second.Run,
                false, 1, first.Physical.Generation, second.Physical, second.Dispatch, injectFailure: Crash));
            await transition.ReconcileAsync(f.Db, lease, cutover, second.Physical, second.Dispatch);
            var read = await MarketplaceGenerationReaderPins.AdmitAsync(f.Db, pin, pin.Tenant, pin.Reader, pin.QueryHash);
            Assert.Equal(first.Physical.Generation, pin.Generation);
            await MarketplaceGenerationReaderPins.CompleteAsync(f.Db, pin, read);
            await MarketplaceGenerationReaderPins.ReleaseAsync(f.Db, pin);
            await f.Exec($"UPDATE public.\"Services\" SET \"Title\"='real-provider-after-cutover' WHERE \"Id\"='{seed.Second}'; UPDATE public.\"Services\" SET \"IsDeleted\"=true WHERE \"Id\"='{seed.First}'");
            await MarketplaceGenerationRegistry.ReleaseAsync(f.Db, lease);
            lease = (await MarketplaceGenerationRegistry.TryAcquireAsync(f.Db, Guid.NewGuid(), TimeSpan.FromMinutes(2)))!;
            var rollback = new MarketplaceGenerationRollback(delivery, transition); var rollbackOperation = Guid.NewGuid();
            await rollback.ExecuteAsync(f.Db, lease, rollbackOperation, 2, second.Physical.Generation, first.Physical, first.Dispatch,
                recoverySource: recoverySource, recoveryBoundary: Boundary(first));
            await rollback.ExecuteAsync(f.Db, lease, rollbackOperation, 2, second.Physical.Generation, first.Physical, first.Dispatch,
                recoverySource: recoverySource, recoveryBoundary: Boundary(first));
            Assert.Equal(3L, await Fixture.Scalar(f.Db, "SELECT version FROM marketplace_generation.active_pointer"));
            await provider.AssertTombstoneOrderingAsync(first, seed.First, lease);
            var third = await Build();
            await transition.ExecuteAsync(f.Db, lease, Guid.NewGuid(), third.Run, false, 3, first.Physical.Generation, third.Physical, third.Dispatch);
            await Assert.ThrowsAsync<InvalidOperationException>(() => MarketplaceGenerationRetirement.PrepareAsync(f.Db, lease, Guid.NewGuid(), first.Physical));
            await new MarketplaceGenerationRetention().ReleaseAsync(f.Db, lease, Guid.NewGuid(), second.Physical.Generation, TimeSpan.FromDays(1));
            // Fixture-only passage of retention time. Runtime policy is not lowered.
            await f.Setup($"UPDATE marketplace_generation.lifecycle SET retain_until=clock_timestamp()-interval '1 second' WHERE generation_id='{second.Physical.Generation}'");
            var retirement = Guid.NewGuid();
            await MarketplaceGenerationRetirement.ExecuteAsync(f.Db, lease, retirement, second.Physical, second.Lifecycle);
            await MarketplaceGenerationRetirement.ExecuteAsync(f.Db, lease, retirement, second.Physical, second.Lifecycle);
            Assert.Equal("retired", await Fixture.Scalar(f.Db, $"SELECT state FROM marketplace_generation.lifecycle WHERE generation_id='{second.Physical.Generation}'"));
            var evidence = Environment.GetEnvironmentVariable("APP17D_EVIDENCE_DIR") ?? throw new InvalidOperationException("Owned evidence root required.");
            await File.WriteAllTextAsync(Path.Combine(evidence, "generation-real-cross-provider.json"), JsonSerializer.Serialize(new
            {
                realPostgres = true, realOpenSearch = true, restrictedDispatcher = true, sourceDatabase = f.Db.Database,
                generations = new[] { first.Physical, second.Physical, third.Physical }, pointerVersion = 4,
                rollbackCatchupAndTombstones = true, interruptedCutoverRecovered = true, retirementCompleted = true,
                providerGlobalEpochProof = false, fullCrashMatrixProof = false, durableGatewayDeliveryUsed = true,
                isolatedGatewayProcessAndExclusiveSqlPrincipalCertified = false
            }));
        }
        finally { await provider.CleanupAsync(); }
    }

    private sealed class OwnedGenerationProvider : IDisposable
    {
        private readonly HttpClient control;
        private readonly ServerCertificatePolicy policy;
        private readonly List<Target> targets = [];
        private readonly List<string> users = [];
        private readonly List<string> roles = [];
        private static readonly Uri Endpoint = new("https://opensearch:9201/");
        internal OwnedGenerationProvider()
        {
            var password = Environment.GetEnvironmentVariable("APP17C_OS_PASSWORD") ?? throw new InvalidOperationException("Owned OpenSearch fixture required; no live fallback.");
            var ca = Environment.GetEnvironmentVariable("APP17C_CA") ?? throw new InvalidOperationException("Fixture CA required.");
            policy = new ServerCertificatePolicy(ca);
            control = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false,
                ServerCertificateCustomValidationCallback = policy.Validate }) { BaseAddress = Endpoint, Timeout = TimeSpan.FromSeconds(30) };
            control.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("app17_admin:" + password)));
        }
        internal sealed class Target(MarketplacePhysicalAdmission.Expected physical, MarketplaceGenerationTransport dispatch,
            MarketplaceGenerationTransport lifecycle, HttpClient scopedProbe)
        {
            internal MarketplacePhysicalAdmission.Expected Physical = physical;
            internal MarketplaceGenerationTransport Dispatch = dispatch;
            internal MarketplaceGenerationTransport Lifecycle = lifecycle;
            // Owned-fixture-only direct probes distinguish actual provider role
            // denials/script behavior from production application-side refusals.
            internal HttpClient ScopedProbe = scopedProbe;
            internal Guid Run;
        }
        internal async Task<Target> CreateAsync(Guid journal, Guid generation)
        {
            var name = "marketplace_services_g_" + generation.ToString("N");
            var cluster = await RequestAsync(System.Net.Http.HttpMethod.Get, "", null);
            var created = await RequestAsync(System.Net.Http.HttpMethod.Put, name, MarketplaceGenerationIndexDefinition.CreateBody(journal, generation, 1, 0));
            Assert.True(created.GetProperty("acknowledged").GetBoolean());
            var metadata = await RequestAsync(System.Net.Http.HttpMethod.Get, name, null);
            var index = metadata.GetProperty(name); var settings = index.GetProperty("settings").GetProperty("index");
            // The fixture creator independently pins the created UUID and exact
            // protected mapping/settings; the dispatcher cannot choose/rebind them.
            var physical = new MarketplacePhysicalAdmission.Expected(journal, generation, cluster.GetProperty("cluster_uuid").GetString()!,
                settings.GetProperty("uuid").GetString()!, MarketplacePhysicalAdmission.Fingerprint(index.GetProperty("mappings"), settings));
            _ = MarketplacePhysicalAdmission.InspectResponses(physical, cluster.GetRawText(), metadata.GetRawText());
            var dispatch = await CredentialsAsync(name, new[] { "indices:admin/get", "indices:admin/refresh", "indices:data/read/mget*", "indices:data/read/search", "indices:data/write/update" });
            var lifecycle = await CredentialsAsync(name, new[] { "indices:admin/get", "indices:admin/delete" });
            var scopedProbe = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false,
                ServerCertificateCustomValidationCallback = policy.Validate }) { BaseAddress = Endpoint, Timeout = TimeSpan.FromSeconds(30) };
            scopedProbe.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(dispatch.User + ":" + dispatch.Password)));
            var target = new Target(physical, new MarketplaceGenerationTransport(Endpoint, dispatch.User, dispatch.Password, policy),
                new MarketplaceGenerationTransport(Endpoint, lifecycle.User, lifecycle.Password, policy), scopedProbe);
            targets.Add(target); return target;
        }
        private async Task<(string User, string Password)> CredentialsAsync(string name, string[] actions)
        {
            var role = "app17h_gen_" + Guid.NewGuid().ToString("N"); var user = role + "_u";
            var password = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
            await RequestAsync(System.Net.Http.HttpMethod.Put, "_plugins/_security/api/roles/" + role,
                JsonSerializer.Serialize(new { cluster_permissions = new[] { "cluster:monitor/main" }, index_permissions = new[] {
                    new { index_patterns = new[] { name }, allowed_actions = actions } }, tenant_permissions = Array.Empty<object>() }));
            roles.Add(role);
            await RequestAsync(System.Net.Http.HttpMethod.Put, "_plugins/_security/api/internalusers/" + user,
                JsonSerializer.Serialize(new { password, backend_roles = Array.Empty<string>(), attributes = new { } }));
            users.Add(user);
            await RequestAsync(System.Net.Http.HttpMethod.Put, "_plugins/_security/api/rolesmapping/" + role,
                JsonSerializer.Serialize(new { users = new[] { user }, backend_roles = Array.Empty<string>(), hosts = Array.Empty<string>() }));
            return (user, password);
        }
        internal async Task AssertDispatcherDenialsAsync(Target target)
        {
            using var denied = await target.ScopedProbe.DeleteAsync("/" + target.Physical.PhysicalName);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        internal async Task AssertTombstoneOrderingAsync(Target target, Guid service, GenerationLease lease)
        {
            var payload = JsonSerializer.Serialize(new { serviceId = service, isActive = true, title = "stale resurrection must not win" });
            var ticket = new ExternalWriteTicket(Guid.NewGuid(), Guid.NewGuid(), target.Physical.Generation, target.Run, service, 0,
                target.Physical.PhysicalUuid, target.Physical.Fingerprint, MarketplaceFrozenOutput.RowDigest(service, payload), lease.Epoch);
            using var request = new HttpRequestMessage(System.Net.Http.HttpMethod.Post,
                "/" + target.Physical.PhysicalName + "/_update/" + service.ToString("D") + "?retry_on_conflict=0")
            { Content = new StringContent(MarketplaceVersionedDocument.BuildUpdate(lease.JournalId, ticket, payload), Encoding.UTF8, "application/json") };
            using var result = await target.ScopedProbe.SendAsync(request); Assert.True(result.IsSuccessStatusCode);
            var response = await result.Content.ReadAsStringAsync();
            using var reply = JsonDocument.Parse(response); Assert.Equal("noop", reply.RootElement.GetProperty("result").GetString());
            var doc = await RequestAsync(System.Net.Http.HttpMethod.Get, target.Physical.PhysicalName + "/_doc/" + service.ToString("D"), null);
            Assert.True(doc.GetProperty("_source").GetProperty("__marketplace").GetProperty("deleted").GetBoolean());
        }
        private async Task<JsonElement> RequestAsync(System.Net.Http.HttpMethod method, string path, string? body, bool missingAllowed = false)
        {
            using var request = new HttpRequestMessage(method, path);
            if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await control.SendAsync(request);
            if (missingAllowed && response.StatusCode == HttpStatusCode.NotFound) return default;
            Assert.True(response.IsSuccessStatusCode, "Owned fixture control operation failed: " + method + " " + path + " status=" + response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return json.RootElement.Clone();
        }
        internal async Task CleanupAsync()
        {
            foreach (var target in targets)
            {
                var metadata = await RequestAsync(System.Net.Http.HttpMethod.Get, target.Physical.PhysicalName, null, true);
                if (metadata.ValueKind != JsonValueKind.Undefined)
                {
                    Assert.Equal(target.Physical.PhysicalUuid, metadata.GetProperty(target.Physical.PhysicalName).GetProperty("settings").GetProperty("index").GetProperty("uuid").GetString());
                    await RequestAsync(System.Net.Http.HttpMethod.Delete, target.Physical.PhysicalName, null);
                }
                target.Dispatch.Dispose(); target.Lifecycle.Dispose(); target.ScopedProbe.Dispose();
            }
            foreach (var user in users) await RequestAsync(System.Net.Http.HttpMethod.Delete, "_plugins/_security/api/internalusers/" + user, null, true);
            foreach (var role in roles)
            {
                await RequestAsync(System.Net.Http.HttpMethod.Delete, "_plugins/_security/api/rolesmapping/" + role, null, true);
                await RequestAsync(System.Net.Http.HttpMethod.Delete, "_plugins/_security/api/roles/" + role, null, true);
            }
        }
        public void Dispose() => control.Dispose();
    }
}

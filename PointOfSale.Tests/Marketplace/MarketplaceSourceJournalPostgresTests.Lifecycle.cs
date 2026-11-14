using Npgsql;
using PointOfSale.Infrastructure.Marketplace.Generation;

namespace PointOfSale.Tests.Marketplace;

public sealed partial class MarketplaceSourceJournalPostgresTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task SqlCutoverInjectedCrashBoundariesNeverExposeAnUncommittedPointer(int stage)
    {
        await using var f = await Fixture.Create(applicationSchema: true); await SeedProjection(f);
        var proof = await SqlLifecycleInput(f); var operation = Guid.NewGuid();
        var boundary = (MarketplaceGenerationLifecycleStore.CutoverBoundary)stage;
        void Fail(MarketplaceGenerationLifecycleStore.CutoverBoundary reached)
        {
            if (reached == boundary) throw new InjectedLifecycleFailure();
        }
        if (stage is 0 or 1)
        {
            await Assert.ThrowsAsync<InjectedLifecycleFailure>(() => MarketplaceGenerationLifecycleStore.PrepareAsync(f.Db,
                proof.Lease, operation, proof.Physical.Generation, proof.Run, false, 0, null, proof.Physical, injectFailure: Fail));
            Assert.Equal(stage == 0 ? 0L : 1L, await Fixture.Scalar(f.Db, "SELECT count(*) FROM marketplace_generation.pointer_intents"));
            Assert.Equal(0L, await Fixture.Scalar(f.Db, "SELECT version FROM marketplace_generation.active_pointer"));
            await MarketplaceGenerationLifecycleStore.PrepareAsync(f.Db, proof.Lease, operation, proof.Physical.Generation,
                proof.Run, false, 0, null, proof.Physical);
        }
        else
        {
            await MarketplaceGenerationLifecycleStore.PrepareAsync(f.Db, proof.Lease, operation, proof.Physical.Generation,
                proof.Run, false, 0, null, proof.Physical);
            if (stage is 2 or 3)
                await Assert.ThrowsAsync<InjectedLifecycleFailure>(() => MarketplaceGenerationLifecycleStore.CommitAsync(f.Db,
                    proof.Lease, operation, proof.Physical, injectFailure: Fail));
            Assert.Equal(stage == 3 ? 1L : 0L, await Fixture.Scalar(f.Db, "SELECT version FROM marketplace_generation.active_pointer"));
        }
        await using var reconnect = await f.Open();
        await MarketplaceGenerationLifecycleStore.CommitAsync(reconnect, proof.Lease, operation, proof.Physical);
        if (stage == 4)
            await Assert.ThrowsAsync<InjectedLifecycleFailure>(() => MarketplaceGenerationLifecycleStore.CompleteAsync(reconnect, operation, injectFailure: Fail));
        Assert.Equal(GenerationRecoveryAction.CompleteCommitted, await MarketplaceGenerationLifecycleStore.RecoverAsync(reconnect, operation));
        await MarketplaceGenerationLifecycleStore.CompleteAsync(reconnect, operation);
        Assert.Equal(GenerationRecoveryAction.AlreadyComplete, await MarketplaceGenerationLifecycleStore.RecoverAsync(reconnect, operation));
    }

    [Fact]
    public async Task SqlBackendDeathDuringPointerTransactionRollsBackPointerLifecycleAndOutcomeTogether()
    {
        await using var f = await Fixture.Create(applicationSchema: true); await SeedProjection(f);
        var proof = await SqlLifecycleInput(f); var operation = Guid.NewGuid();
        await MarketplaceGenerationLifecycleStore.PrepareAsync(f.Db, proof.Lease, operation, proof.Physical.Generation,
            proof.Run, false, 0, null, proof.Physical);
        await using (var doomed = await f.Open())
        {
            var pid = doomed.ProcessID;
            void Terminate(MarketplaceGenerationLifecycleStore.CutoverBoundary boundary)
            {
                if (boundary != MarketplaceGenerationLifecycleStore.CutoverBoundary.AfterPointerChangeBeforeCommit) return;
                using var kill = new NpgsqlCommand("SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE pid=@pid AND datname=@database AND usename='app17c_fixture'", f.OwnerDb);
                kill.Parameters.AddWithValue("pid", pid); kill.Parameters.AddWithValue("database", f.Db.Database);
                Assert.True(kill.ExecuteScalar() is true);
            }
            await Assert.ThrowsAnyAsync<NpgsqlException>(() => MarketplaceGenerationLifecycleStore.CommitAsync(doomed,
                proof.Lease, operation, proof.Physical, injectFailure: Terminate));
        }
        Assert.Equal(0L, await Fixture.Scalar(f.Db, "SELECT version FROM marketplace_generation.active_pointer"));
        Assert.Equal("verified", await Fixture.Scalar(f.Db, "SELECT state FROM marketplace_generation.lifecycle"));
        Assert.Equal("intent", await Fixture.Scalar(f.Db, "SELECT state FROM marketplace_generation.pointer_intents"));
        Assert.Equal(GenerationRecoveryAction.RetryCompareAndSwap, await MarketplaceGenerationLifecycleStore.RecoverAsync(f.Db, operation));
    }

    [Fact]
    public async Task SqlRollbackWithNoSourceGapHasImmutableRepeatedOutcomeButDoesNotCertifyCatchup()
    {
        await using var f = await Fixture.Create(applicationSchema: true); await SeedProjection(f);
        var first = await SqlLifecycleInput(f); await ActivateSqlInput(f, first, 0, null);
        var second = await SqlLifecycleInput(f, first.Lease); await ActivateSqlInput(f, second, 1, first.Physical.Generation);
        var operation = Guid.NewGuid();
        await MarketplaceGenerationLifecycleStore.PrepareAsync(f.Db, first.Lease, operation, first.Physical.Generation,
            first.Run, true, 2, second.Physical.Generation, first.Physical);
        await MarketplaceGenerationLifecycleStore.CommitAsync(f.Db, first.Lease, operation, first.Physical);
        await MarketplaceGenerationLifecycleStore.CompleteAsync(f.Db, operation);
        await MarketplaceGenerationLifecycleStore.PrepareAsync(f.Db, first.Lease, operation, first.Physical.Generation,
            first.Run, true, 2, second.Physical.Generation, first.Physical);
        await MarketplaceGenerationLifecycleStore.CommitAsync(f.Db, first.Lease, operation, first.Physical);
        Assert.Equal(3L, await Fixture.Scalar(f.Db, "SELECT version FROM marketplace_generation.active_pointer"));
        Assert.Equal(first.Physical.Generation, await Fixture.Scalar(f.Db, "SELECT active_generation FROM marketplace_generation.active_pointer"));
        Assert.Equal(second.Physical.Generation, await Fixture.Scalar(f.Db, "SELECT previous_generation FROM marketplace_generation.active_pointer"));
    }

    private sealed class InjectedLifecycleFailure : Exception { }

    // These tests execute real PostgreSQL only. Synthetic refresh rows below are
    // EXPLICIT fixture inputs to the SQL state machine, NOT OpenSearch evidence.
    [Fact]
    public async Task SqlLifecycleRejectsMissingExternalRefreshEvenWhenLocalOutputIsSealed()
    {
        await using var f = await Fixture.Create(applicationSchema: true); await SeedProjection(f);
        var (lease, build, _, frozen) = await SnapshotProjection(f);
        var run = await MarketplaceFrozenOutput.CreateAsync(f.Db, lease, frozen.ExportId, Guid.NewGuid());
        await MarketplaceFrozenOutput.CommitNextPageAsync(f.Db, lease, run.RunId, Guid.NewGuid(), run.Cursor, 128);
        await MarketplaceFrozenOutput.SealAsync(f.Db, lease, run.RunId, Guid.NewGuid());
        var physical = new MarketplacePhysicalAdmission.Expected(lease.JournalId, build.GenerationId,
            "sql-only-not-a-real-cluster", "projection-fixture-uuid", new string('a', 64));
        await Assert.ThrowsAsync<InvalidOperationException>(() => MarketplaceGenerationLifecycleStore.PrepareAsync(f.Db,
            lease, Guid.NewGuid(), build.GenerationId, run.RunId, false, 0, null, physical));
        Assert.Equal(0L, await Fixture.Scalar(f.Db, "SELECT version FROM marketplace_generation.active_pointer"));
        Assert.Equal(0L, await Fixture.Scalar(f.Db, "SELECT count(*) FROM marketplace_generation.pointer_intents"));
    }

    [Fact]
    public async Task SqlPointerIntentCommitAndReconnectRecoveryAreAtomicAndIdempotent()
    {
        await using var f = await Fixture.Create(applicationSchema: true); await SeedProjection(f);
        var proof = await SqlLifecycleInput(f);
        var operation = Guid.NewGuid();
        await MarketplaceGenerationLifecycleStore.PrepareAsync(f.Db, proof.Lease, operation, proof.Physical.Generation,
            proof.Run, false, 0, null, proof.Physical);
        Assert.Equal(GenerationRecoveryAction.RetryCompareAndSwap, await MarketplaceGenerationLifecycleStore.RecoverAsync(f.Db, operation));
        Assert.Null(await Fixture.Scalar(f.Db, "SELECT active_generation FROM marketplace_generation.active_pointer") as Guid?);
        await using (var reconnect = await f.Open())
            await MarketplaceGenerationLifecycleStore.CommitAsync(reconnect, proof.Lease, operation, proof.Physical);
        Assert.Equal(GenerationRecoveryAction.CompleteCommitted, await MarketplaceGenerationLifecycleStore.RecoverAsync(f.Db, operation));
        await MarketplaceGenerationLifecycleStore.CompleteAsync(f.Db, operation);
        await MarketplaceGenerationLifecycleStore.CompleteAsync(f.Db, operation);
        await MarketplaceGenerationLifecycleStore.CommitAsync(f.Db, proof.Lease, operation, proof.Physical);
        Assert.Equal(GenerationRecoveryAction.AlreadyComplete, await MarketplaceGenerationLifecycleStore.RecoverAsync(f.Db, operation));
        Assert.Equal(1L, await Fixture.Scalar(f.Db, "SELECT version FROM marketplace_generation.active_pointer"));
        Assert.Equal("completed", await Fixture.Scalar(f.Db, "SELECT state FROM marketplace_generation.pointer_intents"));
        await Assert.ThrowsAsync<ArgumentException>(() => MarketplaceGenerationLifecycleStore.PrepareAsync(f.Db,
            proof.Lease, operation, Guid.NewGuid(), proof.Run, false, 0, null, proof.Physical));
        foreach (var sql in new[] { "UPDATE marketplace_generation.pointer_intents SET required_revision=required_revision+1",
            "DELETE FROM marketplace_generation.pointer_intents" })
            Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(() => f.Exec(sql))).SqlState);
        // Least privilege rejects TRUNCATE before a trigger can execute. Prove
        // both boundaries separately instead of granting runtime TRUNCATE merely
        // to satisfy an incorrect expected SQLSTATE.
        Assert.True(await Fixture.Scalar(f.Db, "SELECT NOT has_table_privilege(current_user,'marketplace_generation.pointer_intents','TRUNCATE')") is true);
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege,
            (await Assert.ThrowsAsync<PostgresException>(() => f.Exec("TRUNCATE marketplace_generation.pointer_intents"))).SqlState);
        Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(() => f.Setup("TRUNCATE marketplace_generation.pointer_intents"))).SqlState);
    }

    [Fact]
    public async Task SqlCutoverCannotIgnoreSourceCommitAfterIntent()
    {
        await using var f = await Fixture.Create(applicationSchema: true); var seed = await SeedProjection(f);
        var proof = await SqlLifecycleInput(f); var operation = Guid.NewGuid();
        await MarketplaceGenerationLifecycleStore.PrepareAsync(f.Db, proof.Lease, operation, proof.Physical.Generation,
            proof.Run, false, 0, null, proof.Physical);
        await f.Exec($"UPDATE public.\"Services\" SET \"Title\"='Committed after intent' WHERE \"Id\"='{seed.First}'");
        await Assert.ThrowsAsync<InvalidOperationException>(() => MarketplaceGenerationLifecycleStore.CommitAsync(f.Db, proof.Lease, operation, proof.Physical));
        Assert.Equal(0L, await Fixture.Scalar(f.Db, "SELECT version FROM marketplace_generation.active_pointer"));
        Assert.Equal("intent", await Fixture.Scalar(f.Db, "SELECT state FROM marketplace_generation.pointer_intents"));
    }

    [Fact]
    public async Task SqlCompetingCutoverIntentsCannotBothBecomeAuthoritative()
    {
        await using var f = await Fixture.Create(applicationSchema: true); await SeedProjection(f);
        var first = await SqlLifecycleInput(f); var second = await SqlLifecycleInput(f, first.Lease);
        var op1 = Guid.NewGuid(); var op2 = Guid.NewGuid();
        await MarketplaceGenerationLifecycleStore.PrepareAsync(f.Db, first.Lease, op1, first.Physical.Generation, first.Run, false, 0, null, first.Physical);
        await MarketplaceGenerationLifecycleStore.PrepareAsync(f.Db, second.Lease, op2, second.Physical.Generation, second.Run, false, 0, null, second.Physical);
        await using var other = await f.Open();
        async Task<bool> Commit(NpgsqlConnection db, Guid operation, SqlLifecycleProof proof)
        {
            try { await MarketplaceGenerationLifecycleStore.CommitAsync(db, proof.Lease, operation, proof.Physical); return true; }
            catch (InvalidOperationException) { return false; }
        }
        var results = await Task.WhenAll(Commit(f.Db, op1, first), Commit(other, op2, second));
        Assert.Single(results.Where(r => r));
        Assert.Equal(1L, await Fixture.Scalar(f.Db, "SELECT version FROM marketplace_generation.active_pointer"));
        Assert.Equal(1L, await Fixture.Scalar(f.Db, "SELECT count(*) FROM marketplace_generation.pointer_intents WHERE state='committed'"));
        var loser = results[0] ? op2 : op1;
        Assert.Equal(GenerationRecoveryAction.RejectForeignPointer, await MarketplaceGenerationLifecycleStore.RecoverAsync(f.Db, loser));
        Assert.Equal(GenerationRecoveryAction.RejectForeignPointer, await MarketplaceGenerationLifecycleStore.RecoverAsync(f.Db, loser));
        Assert.Equal(1L, await Fixture.Scalar(f.Db, "SELECT count(*) FROM marketplace_generation.pointer_intents WHERE state='superseded'"));
    }

    [Fact]
    public async Task SqlReaderPinsKeepOriginalGenerationAcrossPointerChangeAndRejectForeignContinuation()
    {
        await using var f = await Fixture.Create(applicationSchema: true); await SeedProjection(f);
        await f.Setup("GRANT EXECUTE ON FUNCTION marketplace_generation.lock_reader_pointer() TO app17c_fixture");
        var first = await SqlLifecycleInput(f); await ActivateSqlInput(f, first, 0, null);
        var tenant = Guid.NewGuid(); var reader = Guid.NewGuid(); var query = new string('b', 64);
        var pin = await MarketplaceGenerationReaderPins.AcquireAsync(f.Db, tenant, reader, query, TimeSpan.FromMinutes(1));
        var admitted = await MarketplaceGenerationReaderPins.AdmitAsync(f.Db, pin, tenant, reader, query);
        var second = await SqlLifecycleInput(f, first.Lease); await ActivateSqlInput(f, second, 1, first.Physical.Generation);
        var continuation = await MarketplaceGenerationReaderPins.AdmitAsync(f.Db, pin, tenant, reader, query);
        Assert.Equal(first.Physical.Generation, pin.Generation);
        var current = await MarketplaceGenerationReaderPins.AcquireAsync(f.Db, tenant, reader, query, TimeSpan.FromMinutes(1));
        Assert.Equal(second.Physical.Generation, current.Generation);
        await Assert.ThrowsAsync<InvalidOperationException>(() => MarketplaceGenerationReaderPins.AdmitAsync(f.Db,
            pin with { Secret = new string('c', 64) }, tenant, reader, query));
        await Assert.ThrowsAsync<InvalidOperationException>(() => MarketplaceGenerationReaderPins.AdmitAsync(f.Db, pin, Guid.NewGuid(), reader, query));
        await MarketplaceGenerationReaderPins.ReleaseAsync(f.Db, pin);
        await Assert.ThrowsAsync<InvalidOperationException>(() => MarketplaceGenerationReaderPins.AdmitAsync(f.Db, pin, tenant, reader, query));
        Assert.Equal(2L, await Fixture.Scalar(f.Db, "SELECT count(*) FROM marketplace_generation.reader_operations WHERE completed_at IS NULL"));
        await MarketplaceGenerationReaderPins.CompleteAsync(f.Db, pin, admitted);
        await MarketplaceGenerationReaderPins.CompleteAsync(f.Db, pin, continuation);
        await MarketplaceGenerationReaderPins.CompleteAsync(f.Db, pin, continuation);
        await MarketplaceGenerationReaderPins.ReleaseAsync(f.Db, current);
        Assert.Equal(0L, await Fixture.Scalar(f.Db, "SELECT count(*) FROM marketplace_generation.reader_operations WHERE completed_at IS NULL"));
    }

    [Fact]
    public async Task SqlExpiryAndReleaseCannotForgetAdmittedReaderDuringRetirement()
    {
        await using var f = await Fixture.Create(applicationSchema: true); await SeedProjection(f);
        await f.Setup("GRANT EXECUTE ON FUNCTION marketplace_generation.lock_reader_pointer() TO app17c_fixture");
        var proof = await SqlLifecycleInput(f); await ActivateSqlInput(f, proof, 0, null);
        var tenant = Guid.NewGuid(); var reader = Guid.NewGuid(); var query = new string('b', 64);
        var pin = await MarketplaceGenerationReaderPins.AcquireAsync(f.Db, tenant, reader, query, TimeSpan.FromMinutes(1));
        var admitted = await MarketplaceGenerationReaderPins.AdmitAsync(f.Db, pin, tenant, reader, query);
        // Explicit control-plane fixture state, not a production retention-policy approval.
        await f.Setup($"UPDATE marketplace_generation.active_pointer SET active_generation=NULL; UPDATE marketplace_generation.lifecycle SET state='retained',retain_until=clock_timestamp()-interval '1 second'; UPDATE marketplace_generation.reader_pins SET expires_at=clock_timestamp()-interval '1 second'");
        await Assert.ThrowsAsync<InvalidOperationException>(() => MarketplaceGenerationReaderPins.AdmitAsync(f.Db, pin, tenant, reader, query));
        await MarketplaceGenerationReaderPins.ReleaseAsync(f.Db, pin);
        await Assert.ThrowsAsync<InvalidOperationException>(() => MarketplaceGenerationRetirement.PrepareAsync(f.Db, proof.Lease, Guid.NewGuid(), proof.Physical));
        await MarketplaceGenerationReaderPins.CompleteAsync(f.Db, pin, admitted);
        var operation = Guid.NewGuid();
        await MarketplaceGenerationRetirement.PrepareAsync(f.Db, proof.Lease, operation, proof.Physical);
        await MarketplaceGenerationRetirement.PrepareAsync(f.Db, proof.Lease, operation, proof.Physical);
        Assert.Equal("retiring", await Fixture.Scalar(f.Db, "SELECT state FROM marketplace_generation.lifecycle"));
        Assert.Equal("intent", await Fixture.Scalar(f.Db, "SELECT state FROM marketplace_generation.retirement_intents"));
        Assert.Equal(0L, await Fixture.Scalar(f.Db, "SELECT count(*) FROM marketplace_generation.lifecycle WHERE state='retired'"));
    }

    [Fact]
    public async Task SqlRollbackRejectsPreviousGenerationWhoseRefreshIsBehindCommittedWrites()
    {
        await using var f = await Fixture.Create(applicationSchema: true); var seed = await SeedProjection(f);
        var first = await SqlLifecycleInput(f); await ActivateSqlInput(f, first, 0, null);
        var second = await SqlLifecycleInput(f, first.Lease); await ActivateSqlInput(f, second, 1, first.Physical.Generation);
        await f.Exec($"DELETE FROM public.\"Services\" WHERE \"Id\"='{seed.Second}'");
        await Assert.ThrowsAsync<InvalidOperationException>(() => MarketplaceGenerationLifecycleStore.PrepareAsync(f.Db,
            first.Lease, Guid.NewGuid(), first.Physical.Generation, first.Run, true, 2, second.Physical.Generation, first.Physical));
        Assert.Equal(second.Physical.Generation, await Fixture.Scalar(f.Db, "SELECT active_generation FROM marketplace_generation.active_pointer"));
        Assert.Equal(first.Physical.Generation, await Fixture.Scalar(f.Db, "SELECT previous_generation FROM marketplace_generation.active_pointer"));
    }

    [Fact]
    public async Task SqlRestrictedReaderCanPinButCannotChangePointerProviderBindingOrCapture()
    {
        await using var f = await Fixture.Create(applicationSchema: true); await SeedProjection(f);
        var proof = await SqlLifecycleInput(f); await ActivateSqlInput(f, proof, 0, null);
        var role = "app17h_reader_" + Guid.NewGuid().ToString("N");
        var password = Guid.NewGuid().ToString("N");
        await f.Setup($"CREATE ROLE {role} LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD '{password}'");
        try
        {
            await f.Setup($"GRANT CONNECT ON DATABASE {f.Db.Database} TO {role}; GRANT USAGE ON SCHEMA marketplace_generation TO {role}; GRANT SELECT ON marketplace_generation.active_pointer,marketplace_generation.lifecycle TO {role}; GRANT SELECT,INSERT,UPDATE ON marketplace_generation.reader_pins,marketplace_generation.reader_operations TO {role}; GRANT EXECUTE ON FUNCTION marketplace_generation.lock_reader_pointer() TO {role}");
            var cs = new NpgsqlConnectionStringBuilder(f.WriterConnection) { Username = role, Password = password, Pooling = false };
            await using var db = new NpgsqlConnection(cs.ConnectionString); await db.OpenAsync();
            Assert.True(await Fixture.Scalar(db, "SELECT NOT rolsuper AND NOT rolcreatedb AND NOT rolcreaterole AND NOT rolbypassrls FROM pg_roles WHERE rolname=current_user") is true);
            var tenant = Guid.NewGuid(); var reader = Guid.NewGuid(); var query = new string('b', 64);
            var pin = await MarketplaceGenerationReaderPins.AcquireAsync(db, tenant, reader, query, TimeSpan.FromMinutes(1));
            var admitted = await MarketplaceGenerationReaderPins.AdmitAsync(db, pin, tenant, reader, query);
            foreach (var sql in new[] { "UPDATE marketplace_generation.active_pointer SET version=version+1",
                "DELETE FROM marketplace_generation.provider_bindings", "UPDATE marketplace_generation.control SET epoch=0",
                "TRUNCATE marketplace_generation.reader_pins", "ALTER TABLE public.\"Services\" DISABLE TRIGGER marketplace_capture" })
                Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => f.Exec(sql, db))).SqlState);
            await MarketplaceGenerationReaderPins.CompleteAsync(db, pin, admitted);
            await MarketplaceGenerationReaderPins.ReleaseAsync(db, pin);
            var evidence = Environment.GetEnvironmentVariable("APP17D_EVIDENCE_DIR") ?? throw new InvalidOperationException("Owned evidence root required.");
            await File.WriteAllTextAsync(Path.Combine(evidence, "generation-restricted-reader-" + role + ".json"),
                System.Text.Json.JsonSerializer.Serialize(new { role, database = db.Database, host = db.Host,
                    fixtureOnly = true, superuser = false, createDatabase = false, createRole = false, bypassRls = false,
                    granted = "Pointer/lifecycle SELECT; pin/reader-operation SELECT INSERT UPDATE; exact reader-lock function EXECUTE",
                    forbiddenOperations = new[] { "pointer UPDATE", "provider binding DELETE", "epoch reset", "pin TRUNCATE", "capture disable" },
                    allForbiddenOperationsRejected = true, productionRoleCertification = false }));
        }
        finally { await f.Setup($"DROP OWNED BY {role}; DROP ROLE {role}"); }
    }

    private sealed record SqlLifecycleProof(GenerationLease Lease, MarketplacePhysicalAdmission.Expected Physical, Guid Run);
    private static async Task<SqlLifecycleProof> SqlLifecycleInput(Fixture f, GenerationLease? existing = null)
    {
        var lease = existing ?? (await MarketplaceGenerationRegistry.TryAcquireAsync(f.Db, Guid.NewGuid(), TimeSpan.FromMinutes(2)))!;
        var build = await MarketplaceGenerationRegistry.CreateBuildAsync(f.Db, lease, Guid.NewGuid());
        await MarketplaceGenerationRegistry.BindPhysicalIdentityAsync(f.Db, lease, build.GenerationId, "projection-fixture-uuid-" + build.GenerationId.ToString("N"), new string('a', 64));
        var checkpoint = await MarketplaceGenerationRegistry.SnapshotAsync(f.Db, lease, build.GenerationId);
        var frozen = await MarketplaceGenerationInputExport.FreezeAsync(f.Db, lease, build.GenerationId, Guid.NewGuid(), checkpoint);
        var run = await MarketplaceFrozenOutput.CreateAsync(f.Db, lease, frozen.ExportId, Guid.NewGuid());
        await MarketplaceFrozenOutput.CommitNextPageAsync(f.Db, lease, run.RunId, Guid.NewGuid(), run.Cursor, 128);
        await MarketplaceFrozenOutput.SealAsync(f.Db, lease, run.RunId, Guid.NewGuid());
        var physical = new MarketplacePhysicalAdmission.Expected(lease.JournalId, build.GenerationId,
            "sql-only-not-a-real-cluster", "projection-fixture-uuid-" + build.GenerationId.ToString("N"), new string('a', 64));
        var services = new List<Guid>();
        await using (var read = new NpgsqlCommand("SELECT service_id FROM marketplace_generation.output_rows WHERE run_id=@run", f.Db))
        {
            read.Parameters.AddWithValue("run", run.RunId);
            await using var rows = await read.ExecuteReaderAsync();
            while (await rows.ReadAsync()) services.Add(rows.GetGuid(0));
        }
        foreach (var service in services)
        {
            var ticket = await MarketplaceExternalWriteBarrier.AdmitAsync(f.Db, lease, run.RunId, service, Guid.NewGuid());
            // SQL-only test input. No remote acknowledgement is claimed here.
            await MarketplaceExternalWriteBarrier.RecordDefiniteOutcomeAsync(f.Db, ticket, ExternalDefiniteOutcome.Acknowledged);
        }
        Assert.True(await MarketplaceExternalWriteBarrier.CloseAndTryDrainAsync(f.Db, lease, build.GenerationId));
        await f.Setup($"INSERT INTO marketplace_generation.provider_bindings VALUES('{physical.Generation}','{physical.Journal}','{physical.ClusterUuid}','{physical.PhysicalUuid}','{physical.Fingerprint}')");
        await f.Setup($"""
            INSERT INTO marketplace_generation.refresh_observations(run_id,generation_id,journal_id,cluster_uuid,physical_uuid,
                fingerprint,source_revision,seal_digest,item_count,observed_epoch)
            SELECT r.run_id,e.generation_id,e.journal_id,'{physical.ClusterUuid}',e.physical_uuid,e.fingerprint,e.revision,s.digest,s.item_count,{lease.Epoch}
            FROM marketplace_generation.output_runs r JOIN marketplace_generation.output_seals s ON s.run_id=r.run_id
              JOIN marketplace_generation.exports e ON e.export_id=r.export_id WHERE r.run_id='{run.RunId}'
            """);
        return new(lease, physical, run.RunId);
    }

    private static async Task ActivateSqlInput(Fixture f, SqlLifecycleProof proof, long version, Guid? active)
    {
        var operation = Guid.NewGuid();
        await MarketplaceGenerationLifecycleStore.PrepareAsync(f.Db, proof.Lease, operation, proof.Physical.Generation,
            proof.Run, false, version, active, proof.Physical);
        await MarketplaceGenerationLifecycleStore.CommitAsync(f.Db, proof.Lease, operation, proof.Physical);
        await MarketplaceGenerationLifecycleStore.CompleteAsync(f.Db, operation);
    }
}

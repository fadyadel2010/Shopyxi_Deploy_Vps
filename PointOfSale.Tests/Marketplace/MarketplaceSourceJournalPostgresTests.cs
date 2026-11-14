using System.Data;
using System.Diagnostics;
using System.Text.Json;
using Npgsql;
using PointOfSale.Infrastructure.Marketplace.Journal;
using PointOfSale.Infrastructure.Marketplace.Generation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Enums;
using PointOfSale.Core.DTOs.Marketplace;
using PointOfSale.Core.Interfaces;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Services;
using PointOfSale.Tests.Infrastructure;

namespace PointOfSale.Tests.Marketplace;

// Real PostgreSQL only; no fallback, no application host, no shared database migration.
public sealed partial class MarketplaceSourceJournalPostgresTests
{
    [Fact]
    public async Task LeastPrivilegeSourceWriterIsCapturedButCannotDisableTriggersRewriteJournalOrTruncate()
    {
        await using var f = await Fixture.Create(applicationSchema: true); var seed = await SeedProjection(f);
        var role = "app17h_journal_writer_" + Guid.NewGuid().ToString("N");
        // This role exists only inside the queue-owned disposable PostgreSQL
        // cluster. The generated identifier cannot select a shared/user role.
        await f.Setup($"CREATE ROLE \"{role}\" LOGIN PASSWORD 'fixture-only-writer' NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS");
        try
        {
            await f.Setup($"GRANT CONNECT ON DATABASE \"{f.Db.Database}\" TO \"{role}\"; GRANT USAGE ON SCHEMA public TO \"{role}\"; GRANT SELECT,UPDATE ON public.\"Services\" TO \"{role}\"");
            var cs = new NpgsqlConnectionStringBuilder(f.WriterConnection) { Username = role, Password = "fixture-only-writer" };
            var before = (await f.Boundary()).Revision;
            await using (var writer = new NpgsqlConnection(cs.ConnectionString))
            {
                await writer.OpenAsync();
                await using (var privilege = new NpgsqlCommand("SELECT NOT rolsuper AND NOT rolcreaterole AND NOT rolcreatedb AND NOT rolreplication AND NOT rolbypassrls FROM pg_roles WHERE rolname=current_user", writer))
                    Assert.True((bool)(await privilege.ExecuteScalarAsync())!);
                await using (var update = new NpgsqlCommand("UPDATE public.\"Services\" SET \"Title\"='Least privilege captured' WHERE \"Id\"=@id", writer))
                {
                    update.Parameters.AddWithValue("id", seed.Second);
                    Assert.Equal(1, await update.ExecuteNonQueryAsync());
                }
                Assert.True((await f.Boundary()).Revision > before);
                Assert.Contains(await f.Changes(), c => c.Table == "Services" && c.SourceId == seed.Second &&
                    c.NewRow?.Contains("Least privilege captured", StringComparison.Ordinal) == true);
                foreach (var sql in new[] {
                    "SET session_replication_role=replica",
                    "ALTER TABLE public.\"Services\" DISABLE TRIGGER marketplace_gate",
                    "DROP TRIGGER marketplace_capture ON public.\"Services\"",
                    "UPDATE marketplace_journal.gate SET revision=0",
                    "DELETE FROM marketplace_journal.changes" })
                {
                    await using var command = new NpgsqlCommand(sql, writer);
                    Assert.Equal(PostgresErrorCodes.InsufficientPrivilege,
                        (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync())).SqlState);
                }
                await using var truncate = new NpgsqlCommand("TRUNCATE public.\"Services\" CASCADE", writer);
                Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await Assert.ThrowsAsync<PostgresException>(() => truncate.ExecuteNonQueryAsync())).SqlState);
            }
            await MarketplaceSourceJournal.VerifyCaptureInstalledAsync(f.Db);
        }
        finally
        {
            await f.Setup($"DROP OWNED BY \"{role}\"; DROP ROLE \"{role}\"");
        }
    }

    [Fact]
    public async Task OutstandingExternalIntentSurvivesReconnectAndNewEpochAndPreventsFalseDrain()
    {
        await using var f = await Fixture.Create(applicationSchema: true); var seed = await SeedProjection(f);
        var (lease, build, _, frozen) = await SnapshotProjection(f);
        var run = await MarketplaceFrozenOutput.CreateAsync(f.Db, lease, frozen.ExportId, Guid.NewGuid());
        await MarketplaceFrozenOutput.CommitNextPageAsync(f.Db, lease, run.RunId, Guid.NewGuid(), run.Cursor, 128);
        await MarketplaceFrozenOutput.SealAsync(f.Db, lease, run.RunId, Guid.NewGuid());
        var operation = Guid.NewGuid();
        var ticket = await MarketplaceExternalWriteBarrier.AdmitAsync(f.Db, lease, run.RunId, seed.Second, operation);
        await using (var reconnect = await f.Open())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => MarketplaceExternalWriteBarrier.AdmitAsync(
                reconnect, lease, run.RunId, seed.Second, operation));
            await Assert.ThrowsAsync<InvalidOperationException>(() => MarketplaceExternalWriteBarrier.AdmitAsync(
                reconnect, lease, run.RunId, seed.Second, Guid.NewGuid()));
        }
        await MarketplaceGenerationRegistry.ReleaseAsync(f.Db, lease);
        var successor = (await MarketplaceGenerationRegistry.TryAcquireAsync(f.Db, Guid.NewGuid(), TimeSpan.FromMinutes(2)))!;
        Assert.True(successor.Epoch > lease.Epoch);
        Assert.False(await MarketplaceExternalWriteBarrier.CloseAndTryDrainAsync(f.Db, successor, build.GenerationId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => MarketplaceExternalWriteBarrier.AdmitAsync(
            f.Db, successor, run.RunId, seed.Second, Guid.NewGuid()));
        Assert.Equal("outstanding", await Fixture.Scalar(f.Db, "SELECT state FROM marketplace_generation.write_intents"));
        // Represents only a definite late transport response. No HTTP was sent by this SQL test.
        await MarketplaceExternalWriteBarrier.RecordDefiniteOutcomeAsync(f.Db, ticket, ExternalDefiniteOutcome.Acknowledged);
        Assert.True(await MarketplaceExternalWriteBarrier.CloseAndTryDrainAsync(f.Db, successor, build.GenerationId));
        Assert.True(await MarketplaceExternalWriteBarrier.CloseAndTryDrainAsync(f.Db, successor, build.GenerationId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => MarketplaceExternalWriteBarrier.AdmitAsync(
            f.Db, successor, run.RunId, seed.Second, Guid.NewGuid()));
        Assert.Equal(1L, await Fixture.Scalar(f.Db, "SELECT count(*) FROM marketplace_generation.write_intents"));
    }

    [Fact]
    public async Task ExternalIntentRequiresSealAndExactImmutableTicketAndRetainsTombstonePayload()
    {
        await using var f = await Fixture.Create(applicationSchema: true); var seed = await SeedProjection(f);
        var (lease, build, checkpoint, _) = await SnapshotProjection(f);
        await f.Exec($"UPDATE public.\"Services\" SET \"IsActive\"=false WHERE \"Id\"='{seed.Second}'");
        checkpoint = await MarketplaceGenerationRegistry.ReplayPageAsync(f.Db, lease, build.GenerationId, checkpoint, (await f.Boundary()).Revision, 128);
        var export = await MarketplaceGenerationInputExport.FreezeAsync(f.Db, lease, build.GenerationId, Guid.NewGuid(), checkpoint);
        var run = await MarketplaceFrozenOutput.CreateAsync(f.Db, lease, export.ExportId, Guid.NewGuid());
        var operation = Guid.NewGuid();
        await MarketplaceFrozenOutput.CommitNextPageAsync(f.Db, lease, run.RunId, Guid.NewGuid(), run.Cursor, 128);
        await Assert.ThrowsAsync<InvalidOperationException>(() => MarketplaceExternalWriteBarrier.AdmitAsync(
            f.Db, lease, run.RunId, seed.Second, operation));
        Assert.Equal(0L, await Fixture.Scalar(f.Db, "SELECT count(*) FROM marketplace_generation.write_intents"));
        await MarketplaceFrozenOutput.SealAsync(f.Db, lease, run.RunId, Guid.NewGuid());
        var ticket = await MarketplaceExternalWriteBarrier.AdmitAsync(f.Db, lease, run.RunId, seed.Second, operation);
        Assert.Equal(DBNull.Value, await Fixture.Scalar(f.Db, "SELECT payload FROM marketplace_generation.write_intents"));
        Assert.Equal(checkpoint.Revision, ticket.SourceRevision);
        await Assert.ThrowsAsync<InvalidOperationException>(() => MarketplaceExternalWriteBarrier.RecordDefiniteOutcomeAsync(
            f.Db, ticket with { CompletionToken = Guid.NewGuid() }, ExternalDefiniteOutcome.Acknowledged));
        await Assert.ThrowsAsync<ArgumentException>(() => MarketplaceExternalWriteBarrier.RecordDefiniteOutcomeAsync(
            f.Db, ticket, (ExternalDefiniteOutcome)123));
        await MarketplaceExternalWriteBarrier.RecordDefiniteOutcomeAsync(f.Db, ticket, ExternalDefiniteOutcome.DefinitelyNotSent);
        await Assert.ThrowsAsync<InvalidOperationException>(() => MarketplaceExternalWriteBarrier.RecordDefiniteOutcomeAsync(
            f.Db, ticket, ExternalDefiniteOutcome.Acknowledged));
        var retry = await MarketplaceExternalWriteBarrier.AdmitAsync(f.Db, lease, run.RunId, seed.Second, Guid.NewGuid());
        await MarketplaceExternalWriteBarrier.RecordDefiniteOutcomeAsync(f.Db, retry, ExternalDefiniteOutcome.Acknowledged);
        await Assert.ThrowsAsync<InvalidOperationException>(() => MarketplaceExternalWriteBarrier.AdmitAsync(
            f.Db, lease, run.RunId, seed.Second, Guid.NewGuid()));
        foreach (var sql in new[] {
            // Include FK dependents so PostgreSQL reaches the immutable TRUNCATE
            // trigger rather than rejecting first with FK dependency code 0A000.
            // The original required trigger refusal (55000) is unchanged.
            "DELETE FROM marketplace_generation.write_intents", "TRUNCATE marketplace_generation.write_intents CASCADE",
            "UPDATE marketplace_generation.write_intents SET digest=repeat('a',64)",
            "UPDATE marketplace_generation.write_intents SET state='outstanding'" })
            Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(() => f.Setup(sql))).SqlState);
    }

    [Fact]
    public async Task ExternalAdmissionFailureAndCancelledSqlNeverLeaveAnUncommittedIntent()
    {
        await using var f = await Fixture.Create(applicationSchema: true); var seed = await SeedProjection(f);
        var (lease, _, _, frozen) = await SnapshotProjection(f);
        var run = await MarketplaceFrozenOutput.CreateAsync(f.Db, lease, frozen.ExportId, Guid.NewGuid());
        await MarketplaceFrozenOutput.CommitNextPageAsync(f.Db, lease, run.RunId, Guid.NewGuid(), run.Cursor, 128);
        await MarketplaceFrozenOutput.SealAsync(f.Db, lease, run.RunId, Guid.NewGuid());
        await f.Setup("ALTER TABLE marketplace_generation.write_intents ADD CONSTRAINT fixture_intent_failure CHECK(false)");
        await Assert.ThrowsAsync<PostgresException>(() => MarketplaceExternalWriteBarrier.AdmitAsync(
            f.Db, lease, run.RunId, seed.Second, Guid.NewGuid()));
        Assert.Equal(0L, await Fixture.Scalar(f.Db, "SELECT count(*) FROM marketplace_generation.write_barriers"));
        Assert.Equal(0L, await Fixture.Scalar(f.Db, "SELECT count(*) FROM marketplace_generation.write_intents"));
        await f.Setup("ALTER TABLE marketplace_generation.write_intents DROP CONSTRAINT fixture_intent_failure");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MarketplaceExternalWriteBarrier.AdmitAsync(
            f.Db, lease, run.RunId, seed.Second, Guid.NewGuid(), cancel.Token));
        Assert.Equal(0L, await Fixture.Scalar(f.Db, "SELECT count(*) FROM marketplace_generation.write_intents"));
    }

    [Fact]
    public async Task ExternalAdmissionAndDrainSerializeAndLeaseExpiryCannotForgetAnOutstandingRequest()
    {
        await using var f = await Fixture.Create(applicationSchema: true); var seed = await SeedProjection(f);
        var (lease, build, _, frozen) = await SnapshotProjection(f);
        var run = await MarketplaceFrozenOutput.CreateAsync(f.Db, lease, frozen.ExportId, Guid.NewGuid());
        await MarketplaceFrozenOutput.CommitNextPageAsync(f.Db, lease, run.RunId, Guid.NewGuid(), run.Cursor, 128);
        await MarketplaceFrozenOutput.SealAsync(f.Db, lease, run.RunId, Guid.NewGuid());
        await f.Setup("""
            CREATE FUNCTION marketplace_generation.fixture_write_barrier() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN PERFORM pg_advisory_xact_lock(707302); RETURN NEW; END $$;
            CREATE TRIGGER fixture_write_barrier BEFORE INSERT ON marketplace_generation.write_intents
                FOR EACH ROW EXECUTE FUNCTION marketplace_generation.fixture_write_barrier();
            """);
        await using var holder = await f.Open(); await using var held = await holder.BeginTransactionAsync();
        await f.Exec("SELECT pg_advisory_xact_lock(707302)", holder);
        await using var writer = await f.Open(); await using var closer = await f.Open();
        var pending = MarketplaceExternalWriteBarrier.AdmitAsync(writer, lease, run.RunId, seed.Second, Guid.NewGuid());
        await f.WaitForGate(writer.ProcessID, pending);
        var closing = MarketplaceExternalWriteBarrier.CloseAndTryDrainAsync(closer, lease, build.GenerationId);
        await f.WaitForGate(closer.ProcessID, closing);
        await held.CommitAsync(); var ticket = await pending;
        Assert.False(await closing);
        await f.Exec("UPDATE marketplace_generation.control SET lease_until=clock_timestamp()-interval '1 second'");
        var successor = (await MarketplaceGenerationRegistry.TryAcquireAsync(f.Db, Guid.NewGuid(), TimeSpan.FromMinutes(2)))!;
        Assert.False(await MarketplaceExternalWriteBarrier.CloseAndTryDrainAsync(f.Db, successor, build.GenerationId));
        await MarketplaceExternalWriteBarrier.RecordDefiniteOutcomeAsync(f.Db, ticket, ExternalDefiniteOutcome.DefinitelyNotSent);
        Assert.True(await MarketplaceExternalWriteBarrier.CloseAndTryDrainAsync(f.Db, successor, build.GenerationId));
        Assert.Equal("drained", await Fixture.Scalar(f.Db, "SELECT state FROM marketplace_generation.write_barriers"));
        Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(() => f.Exec(
            "UPDATE marketplace_generation.write_barriers SET state='open'"))).SqlState);
    }

    [Fact]
    public async Task LocalOutputCommitsFrozenPagesAcrossReconnectReplayThenSealsAndAcknowledgesOnlyLocalWork()
    {
        await using var f=await Fixture.Create(applicationSchema:true);var seed=await SeedProjection(f);
        var (lease,build,checkpoint,_)=await SnapshotProjection(f);
        await f.Exec($"UPDATE public.\"Services\" SET \"IsActive\"=false WHERE \"Id\"='{seed.Second}'");
        checkpoint=await MarketplaceGenerationRegistry.ReplayPageAsync(f.Db,lease,build.GenerationId,checkpoint,(await f.Boundary()).Revision,100);
        var frozen=await MarketplaceGenerationInputExport.FreezeAsync(f.Db,lease,build.GenerationId,Guid.NewGuid(),checkpoint);
        var expected=(await Projected(f,frozen.ExportId)).ToDictionary(s=>s.ServiceId,s=>s.Document is null ? null : JsonSerializer.Serialize(s.Document,new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase}));
        var operation=Guid.NewGuid();var run=await MarketplaceFrozenOutput.CreateAsync(f.Db,lease,frozen.ExportId,operation);
        Assert.Equal(run,await MarketplaceFrozenOutput.CreateAsync(f.Db,lease,frozen.ExportId,operation));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>MarketplaceFrozenOutput.SealAsync(f.Db,lease,run.RunId,Guid.NewGuid()));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>MarketplaceFrozenOutput.AcknowledgeLocalAsync(f.Db,lease,run.RunId,Guid.NewGuid()));
        var pageOperation=Guid.NewGuid();
        var first=await MarketplaceFrozenOutput.CommitNextPageAsync(f.Db,lease,run.RunId,pageOperation,run.Cursor,1);
        Assert.False(first.Exhausted);Assert.Equal(1,first.RowCount);
        await using(var reconnect=await f.Open())
            Assert.Equal(first,await MarketplaceFrozenOutput.CommitNextPageAsync(reconnect,lease,run.RunId,pageOperation,run.Cursor,1));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>MarketplaceFrozenOutput.CommitNextPageAsync(f.Db,lease,run.RunId,pageOperation,run.Cursor,2));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>MarketplaceFrozenOutput.CommitNextPageAsync(f.Db,lease,run.RunId,Guid.NewGuid(),run.Cursor,1));
        await f.Exec($"UPDATE public.\"MarketplaceProfiles\" SET \"DisplayName\"='Newer than local output export' WHERE \"Id\"='{seed.Profile}'; UPDATE public.\"ServiceReviews\" SET \"Rating\"=1");
        checkpoint=await MarketplaceGenerationRegistry.ReplayPageAsync(f.Db,lease,build.GenerationId,checkpoint,(await f.Boundary()).Revision,100);
        var invalidations=await Fixture.Scalar(f.Db,"SELECT count(*) FROM marketplace_generation.invalidations");
        FrozenOutputPage second;
        await using(var reconnect=await f.Open())second=await MarketplaceFrozenOutput.CommitNextPageAsync(reconnect,lease,run.RunId,Guid.NewGuid(),first.CursorAfter,1);
        Assert.False(second.Exhausted);Assert.Equal(1,second.RowCount);
        var end=await MarketplaceFrozenOutput.CommitNextPageAsync(f.Db,lease,run.RunId,Guid.NewGuid(),second.CursorAfter,1);
        Assert.True(end.Exhausted);Assert.Equal(0,end.RowCount);
        Assert.Equal(expected.OrderBy(r=>r.Key), (await LocalRows(f,run.RunId)).OrderBy(r=>r.Key));
        var sealOperation=Guid.NewGuid();var seal=await MarketplaceFrozenOutput.SealAsync(f.Db,lease,run.RunId,sealOperation);
        Assert.Equal(3,seal.PageCount);Assert.Equal(2,seal.ItemCount);
        Assert.Equal(seal,await MarketplaceFrozenOutput.SealAsync(f.Db,lease,run.RunId,sealOperation));
        var ackOperation=Guid.NewGuid();var ack=await MarketplaceFrozenOutput.AcknowledgeLocalAsync(f.Db,lease,run.RunId,ackOperation);
        Assert.Equal(seal.Digest,ack.SealDigest);Assert.Equal(ack,await MarketplaceFrozenOutput.AcknowledgeLocalAsync(f.Db,lease,run.RunId,ackOperation));
        Assert.Equal(invalidations,await Fixture.Scalar(f.Db,"SELECT count(*) FROM marketplace_generation.invalidations"));
        Assert.Equal(checkpoint,await GenerationCheckpoint(f.Db,build.GenerationId));
        Assert.Equal("replaying",await Fixture.Scalar(f.Db,"SELECT state FROM marketplace_generation.builds"));
        Assert.Equal("local-composition",await Fixture.Scalar(f.Db,"SELECT purpose FROM marketplace_generation.output_local_acks"));
        Assert.True((await MarketplaceFrozenOutput.CreateAsync(f.Db,lease,frozen.ExportId,operation)).Sealed);
        foreach(var sql in new[]{
            "UPDATE marketplace_generation.output_rows SET payload=NULL",
            "DELETE FROM marketplace_generation.output_pages",
            "UPDATE marketplace_generation.output_seals SET digest=repeat('a',64)",
            "UPDATE marketplace_generation.output_local_acks SET purpose='external-index'",
            "TRUNCATE marketplace_generation.output_runs CASCADE",
            $"INSERT INTO marketplace_generation.output_rows(run_id,page_number,service_id,payload,digest) VALUES('{run.RunId}',3,'{Guid.NewGuid()}',NULL,repeat('a',64))"})
            Assert.Equal("55000",(await Assert.ThrowsAsync<PostgresException>(()=>f.Setup(sql))).SqlState);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>MarketplaceFrozenOutput.CommitNextPageAsync(f.Db,lease,run.RunId,Guid.NewGuid(),end.CursorAfter,1));
    }

    [Fact]
    public async Task LocalOutputPageAndReceiptFailuresRollBackRowsAndCursorBeforeSameOperationRetry()
    {
        await using var f=await Fixture.Create(applicationSchema:true);await SeedProjection(f);
        var (lease,_,_,frozen)=await SnapshotProjection(f);
        var run=await MarketplaceFrozenOutput.CreateAsync(f.Db,lease,frozen.ExportId,Guid.NewGuid());var operation=Guid.NewGuid();
        await f.Setup("ALTER TABLE marketplace_generation.output_rows ADD CONSTRAINT fixture_output_failure CHECK(false)");
        Assert.Equal(PostgresErrorCodes.CheckViolation,(await Assert.ThrowsAsync<PostgresException>(()=>MarketplaceFrozenOutput.CommitNextPageAsync(f.Db,lease,run.RunId,operation,run.Cursor,1))).SqlState);
        Assert.Equal(run,await MarketplaceFrozenOutput.ReadAsync(f.Db,run.RunId));
        Assert.Equal(0L,await Fixture.Scalar(f.Db,"SELECT count(*) FROM marketplace_generation.output_rows"));
        Assert.Equal(0L,await Fixture.Scalar(f.Db,"SELECT count(*) FROM marketplace_generation.output_pages"));
        await f.Setup("ALTER TABLE marketplace_generation.output_rows DROP CONSTRAINT fixture_output_failure");
        var first=await MarketplaceFrozenOutput.CommitNextPageAsync(f.Db,lease,run.RunId,operation,run.Cursor,1);
        operation=Guid.NewGuid();
        await f.Setup("ALTER TABLE marketplace_generation.output_pages ADD CONSTRAINT fixture_receipt_failure CHECK(page_number<>1)");
        Assert.Equal(PostgresErrorCodes.CheckViolation,(await Assert.ThrowsAsync<PostgresException>(()=>MarketplaceFrozenOutput.CommitNextPageAsync(f.Db,lease,run.RunId,operation,first.CursorAfter,1))).SqlState);
        Assert.Equal(1L,await Fixture.Scalar(f.Db,"SELECT count(*) FROM marketplace_generation.output_rows"));
        Assert.Equal(1L,await Fixture.Scalar(f.Db,"SELECT count(*) FROM marketplace_generation.output_pages"));
        Assert.Equal(first.CursorAfter,(await MarketplaceFrozenOutput.ReadAsync(f.Db,run.RunId)).Cursor);
        await f.Setup("ALTER TABLE marketplace_generation.output_pages DROP CONSTRAINT fixture_receipt_failure");
        var second=await MarketplaceFrozenOutput.CommitNextPageAsync(f.Db,lease,run.RunId,operation,first.CursorAfter,1);
        await MarketplaceFrozenOutput.CommitNextPageAsync(f.Db,lease,run.RunId,Guid.NewGuid(),second.CursorAfter,1);
        var seal=await MarketplaceFrozenOutput.SealAsync(f.Db,lease,run.RunId,Guid.NewGuid());
        Assert.Equal(2,seal.ItemCount);
        var ackOperation=Guid.NewGuid();
        await f.Setup("ALTER TABLE marketplace_generation.output_local_acks ADD CONSTRAINT fixture_ack_failure CHECK(false)");
        await Assert.ThrowsAsync<PostgresException>(()=>MarketplaceFrozenOutput.AcknowledgeLocalAsync(f.Db,lease,run.RunId,ackOperation));
        Assert.Equal(0L,await Fixture.Scalar(f.Db,"SELECT count(*) FROM marketplace_generation.output_local_acks"));
        Assert.Equal(1L,await Fixture.Scalar(f.Db,"SELECT count(*) FROM marketplace_generation.output_seals"));
        await f.Setup("ALTER TABLE marketplace_generation.output_local_acks DROP CONSTRAINT fixture_ack_failure");
        Assert.Equal(seal.Digest,(await MarketplaceFrozenOutput.AcknowledgeLocalAsync(f.Db,lease,run.RunId,ackOperation)).SealDigest);
    }

    [Fact]
    public async Task LocalOutputCancellationAndOwnedConnectionLossCannotCommitPageEffectsOrCursor()
    {
        await using var f=await Fixture.Create(applicationSchema:true);await SeedProjection(f);
        var (lease,_,_,frozen)=await SnapshotProjection(f);
        var run=await MarketplaceFrozenOutput.CreateAsync(f.Db,lease,frozen.ExportId,Guid.NewGuid());var operation=Guid.NewGuid();
        await InstallOutputBarrier(f);
        foreach(var loseConnection in new[]{false,true})
        {
            await using var holder=await f.Open();await using var held=await holder.BeginTransactionAsync();
            await f.Exec("SELECT pg_advisory_xact_lock(707301)",holder);
            await using var worker=await f.Open();using var cancel=new CancellationTokenSource();
            var pending=MarketplaceFrozenOutput.CommitNextPageAsync(worker,lease,run.RunId,operation,run.Cursor,1,cancel.Token);
            await f.WaitForGate(worker.ProcessID,pending);
            if(loseConnection)
            {
                // Only the connection opened above to this unique disposable database.
                Assert.True((bool)(await Fixture.Scalar(f.Db,$"SELECT pg_terminate_backend({worker.ProcessID})"))!);
                var error=await Record.ExceptionAsync(async()=>{await pending;});
                Assert.True(error is NpgsqlException or IOException,error?.ToString());
            }
            else {cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(async()=>{await pending;});}
            await held.RollbackAsync();
            Assert.Equal(run,await MarketplaceFrozenOutput.ReadAsync(f.Db,run.RunId));
            Assert.Equal(0L,await Fixture.Scalar(f.Db,"SELECT count(*) FROM marketplace_generation.output_rows"));
            Assert.Equal(0L,await Fixture.Scalar(f.Db,"SELECT count(*) FROM marketplace_generation.output_pages"));
        }
        await f.Setup("DROP TRIGGER fixture_output_barrier ON marketplace_generation.output_rows");
        Assert.Equal(1,(await MarketplaceFrozenOutput.CommitNextPageAsync(f.Db,lease,run.RunId,operation,run.Cursor,1)).RowCount);
    }

    [Fact]
    public async Task LocalOutputCompetingCursorsObserveControlLockAndOnlyOnePageCommits()
    {
        await using var f=await Fixture.Create(applicationSchema:true);await SeedProjection(f);
        var (lease,_,_,frozen)=await SnapshotProjection(f);
        var run=await MarketplaceFrozenOutput.CreateAsync(f.Db,lease,frozen.ExportId,Guid.NewGuid());
        await InstallOutputBarrier(f);
        await using var holder=await f.Open();await using var held=await holder.BeginTransactionAsync();
        await f.Exec("SELECT pg_advisory_xact_lock(707301)",holder);
        await using var first=await f.Open();await using var second=await f.Open();
        var a=MarketplaceFrozenOutput.CommitNextPageAsync(first,lease,run.RunId,Guid.NewGuid(),run.Cursor,1);
        await f.WaitForGate(first.ProcessID,a); // observed output-insert barrier while holding owner lock
        var b=MarketplaceFrozenOutput.CommitNextPageAsync(second,lease,run.RunId,Guid.NewGuid(),run.Cursor,1);
        await f.WaitForGate(second.ProcessID,b); // observed contender waiting for common ownership row
        await held.CommitAsync();var committed=await a;
        await Assert.ThrowsAsync<InvalidOperationException>(async()=>{await b;});
        Assert.Equal(1L,await Fixture.Scalar(f.Db,"SELECT count(*) FROM marketplace_generation.output_pages"));
        Assert.Equal(1L,await Fixture.Scalar(f.Db,"SELECT count(*) FROM marketplace_generation.output_rows"));
        Assert.Equal(committed.CursorAfter,(await MarketplaceFrozenOutput.ReadAsync(f.Db,run.RunId)).Cursor);
    }

    [Fact]
    public async Task LocalOutputLeaseExpiryRollsBackPageAndSealThenNewEpochResumesWithoutOldOwnerAck()
    {
        await using var f=await Fixture.Create(applicationSchema:true);await SeedProjection(f);
        var (lease,_,_,frozen)=await SnapshotProjection(f);
        var run=await MarketplaceFrozenOutput.CreateAsync(f.Db,lease,frozen.ExportId,Guid.NewGuid());var operation=Guid.NewGuid();
        await f.Setup("""
            CREATE FUNCTION marketplace_generation.fixture_slow_output() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN PERFORM pg_sleep(1.1); RETURN NEW; END $$;
            CREATE TRIGGER fixture_slow_output BEFORE INSERT ON marketplace_generation.output_rows
                FOR EACH ROW EXECUTE FUNCTION marketplace_generation.fixture_slow_output();
            UPDATE marketplace_generation.control SET lease_until=clock_timestamp()+interval '1 second';
            """);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>MarketplaceFrozenOutput.CommitNextPageAsync(f.Db,lease,run.RunId,operation,run.Cursor,1));
        Assert.Equal(run,await MarketplaceFrozenOutput.ReadAsync(f.Db,run.RunId));
        Assert.Equal(0L,await Fixture.Scalar(f.Db,"SELECT count(*) FROM marketplace_generation.output_rows"));
        await f.Setup("DROP TRIGGER fixture_slow_output ON marketplace_generation.output_rows");
        var next=(await MarketplaceGenerationRegistry.TryAcquireAsync(f.Db,Guid.NewGuid(),TimeSpan.FromMinutes(2)))!;
        await Assert.ThrowsAsync<InvalidOperationException>(()=>MarketplaceFrozenOutput.CommitNextPageAsync(f.Db,lease,run.RunId,operation,run.Cursor,1));
        var page=await MarketplaceFrozenOutput.CommitNextPageAsync(f.Db,next,run.RunId,operation,run.Cursor,128);
        Assert.True(page.Exhausted);Assert.Equal(next.Epoch,page.CreatedEpoch);
        await f.Setup("""
            CREATE TRIGGER fixture_slow_seal BEFORE INSERT ON marketplace_generation.output_seals
                FOR EACH ROW EXECUTE FUNCTION marketplace_generation.fixture_slow_output();
            UPDATE marketplace_generation.control SET lease_until=clock_timestamp()+interval '1 second';
            """);
        var sealOperation=Guid.NewGuid();
        await Assert.ThrowsAsync<InvalidOperationException>(()=>MarketplaceFrozenOutput.SealAsync(f.Db,next,run.RunId,sealOperation));
        Assert.Equal(0L,await Fixture.Scalar(f.Db,"SELECT count(*) FROM marketplace_generation.output_seals"));
        await f.Setup("DROP TRIGGER fixture_slow_seal ON marketplace_generation.output_seals");
        var final=(await MarketplaceGenerationRegistry.TryAcquireAsync(f.Db,Guid.NewGuid(),TimeSpan.FromMinutes(2)))!;
        var seal=await MarketplaceFrozenOutput.SealAsync(f.Db,final,run.RunId,sealOperation);
        Assert.Equal(final.Epoch,seal.CreatedEpoch);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>MarketplaceFrozenOutput.AcknowledgeLocalAsync(f.Db,next,run.RunId,Guid.NewGuid()));
        Assert.Equal(final.Epoch,(await MarketplaceFrozenOutput.AcknowledgeLocalAsync(f.Db,final,run.RunId,Guid.NewGuid())).CreatedEpoch);
    }

    [Fact]
    public async Task LocalOutputEmptyPassRequiresReceiptAndUnknownComposerForeignCursorAndPhysicalChangesFailClosed()
    {
        await using var f=await Fixture.Create(applicationSchema:true);
        var (lease,build,_,frozen)=await SnapshotProjection(f);
        var run=await MarketplaceFrozenOutput.CreateAsync(f.Db,lease,frozen.ExportId,Guid.NewGuid());
        await Assert.ThrowsAsync<InvalidOperationException>(()=>MarketplaceFrozenOutput.SealAsync(f.Db,lease,run.RunId,Guid.NewGuid()));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>MarketplaceFrozenOutput.CommitNextPageAsync(f.Db,lease,run.RunId,Guid.NewGuid(),run.Cursor with {ExportId=Guid.NewGuid()},1));
        var other=await MarketplaceFrozenOutput.CreateAsync(f.Db,lease,frozen.ExportId,Guid.NewGuid());
        await Assert.ThrowsAsync<ArgumentException>(()=>MarketplaceFrozenOutput.CommitNextPageAsync(f.Db,lease,run.RunId,Guid.NewGuid(),other.Cursor,1));
        Assert.Equal("55000",(await Assert.ThrowsAsync<PostgresException>(()=>f.Exec("UPDATE marketplace_generation.output_runs SET composer_fingerprint=repeat('a',64)"))).SqlState);
        var unknown=Guid.NewGuid();
        await f.Exec($"""
            INSERT INTO marketplace_generation.output_runs(run_id,operation_id,export_id,composer_fingerprint,created_epoch,last_epoch)
            VALUES('{unknown}','{Guid.NewGuid()}','{frozen.ExportId}',repeat('a',64),{lease.Epoch},{lease.Epoch});
            """);
        var incompatible=await MarketplaceFrozenOutput.ReadAsync(f.Db,unknown);
        await Assert.ThrowsAsync<InvalidDataException>(()=>MarketplaceFrozenOutput.CommitNextPageAsync(f.Db,lease,unknown,Guid.NewGuid(),incompatible.Cursor,1));
        await f.Exec($"UPDATE marketplace_generation.builds SET physical_uuid='changed-physical-fixture' WHERE generation_id='{build.GenerationId}'");
        await Assert.ThrowsAsync<InvalidDataException>(()=>MarketplaceFrozenOutput.CommitNextPageAsync(f.Db,lease,run.RunId,Guid.NewGuid(),run.Cursor,1));
        await f.Exec($"UPDATE marketplace_generation.builds SET physical_uuid='{frozen.PhysicalUuid}' WHERE generation_id='{build.GenerationId}'");
        var page=await MarketplaceFrozenOutput.CommitNextPageAsync(f.Db,lease,run.RunId,Guid.NewGuid(),run.Cursor,128);
        Assert.True(page.Exhausted);Assert.Equal(0,page.RowCount);
        var seal=await MarketplaceFrozenOutput.SealAsync(f.Db,lease,run.RunId,Guid.NewGuid());
        Assert.Equal(1,seal.PageCount);Assert.Equal(0,seal.ItemCount);
        await MarketplaceFrozenOutput.AcknowledgeLocalAsync(f.Db,lease,run.RunId,Guid.NewGuid());
    }

    [Fact]
    public async Task LocalOutputCannotSealForgedCompletionCountsOrCommitRowsWithoutDeferredPageReceipt()
    {
        await using var f=await Fixture.Create(applicationSchema:true);await SeedProjection(f);
        var (lease,_,_,frozen)=await SnapshotProjection(f);
        var run=await MarketplaceFrozenOutput.CreateAsync(f.Db,lease,frozen.ExportId,Guid.NewGuid());
        await using(var writer=await f.Open())
        await using(var tx=await writer.BeginTransactionAsync())
        {
            await f.Exec($"""
                INSERT INTO marketplace_generation.output_rows(run_id,page_number,service_id,payload,digest)
                VALUES('{run.RunId}',0,'{Guid.NewGuid()}',NULL,repeat('a',64))
                """,writer);
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation,(await Assert.ThrowsAsync<PostgresException>(()=>tx.CommitAsync())).SqlState);
        }
        Assert.Equal(0L,await Fixture.Scalar(f.Db,"SELECT count(*) FROM marketplace_generation.output_rows"));
        await f.Exec($"UPDATE marketplace_generation.output_runs SET next_page=1,exhausted=true,item_count=2 WHERE run_id='{run.RunId}'");
        await Assert.ThrowsAsync<InvalidDataException>(()=>MarketplaceFrozenOutput.SealAsync(f.Db,lease,run.RunId,Guid.NewGuid()));
        Assert.Equal(0L,await Fixture.Scalar(f.Db,"SELECT count(*) FROM marketplace_generation.output_seals"));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>MarketplaceFrozenOutput.AcknowledgeLocalAsync(f.Db,lease,run.RunId,Guid.NewGuid()));
    }

    private static Task InstallOutputBarrier(Fixture f)=>f.Setup("""
        CREATE FUNCTION marketplace_generation.fixture_output_barrier() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN PERFORM pg_advisory_xact_lock(707301); RETURN NEW; END $$;
        CREATE TRIGGER fixture_output_barrier BEFORE INSERT ON marketplace_generation.output_rows
            FOR EACH ROW EXECUTE FUNCTION marketplace_generation.fixture_output_barrier();
        """);
    private static async Task<Dictionary<Guid,string?>> LocalRows(Fixture f,Guid run)
    {
        var result=new Dictionary<Guid,string?>();
        await using var query=new NpgsqlCommand("SELECT service_id,payload FROM marketplace_generation.output_rows WHERE run_id=@run",f.Db);
        query.Parameters.AddWithValue("run",run);await using var rows=await query.ExecuteReaderAsync();
        while(await rows.ReadAsync())result.Add(rows.GetGuid(0),rows.IsDBNull(1) ? null : rows.GetString(1));
        return result;
    }

    [Fact]
    public async Task FrozenProjectionAdmissionRejectsMalformedChildrenBeforeAnyServicePageAndKeepsValidExportStable()
    {
        await using var f=await Fixture.Create(applicationSchema:true);await SeedProjection(f);
        var (lease,build,checkpoint,original)=await SnapshotProjection(f);
        var baseline=JsonSerializer.Serialize(await Projected(f,original.ExportId));
        // Fixture-only corruption of the mutable mirror, never sealed rows or triggers.
        // Each export remains raw input evidence; semantic admission must reject it,
        // including bad children outside the requested first service page.
        var corruptions=new (string Table,string Body)[]{
            ("ServiceRequests","body-'Status'"),
            ("ServiceRequests","jsonb_set(body,'{Status}','null')"),
            ("ServiceRequests","jsonb_set(body,'{Status}','\"FutureLifecycle\"')"),
            ("ServiceRequests","jsonb_set(body,'{IsDeleted}','\"false\"')"),
            ("ServiceRequests","jsonb_set(body,'{ProviderProfileId}','\"00000000-0000-0000-0000-000000000000\"')"),
            ("ServiceReviews","body-'ReviewerUserId'"),
            ("ServiceReviews","jsonb_set(body,'{Rating}','6')"),
            ("ServiceReviews","jsonb_set(body,'{Rating}','\"5\"')"),
            ("ServiceReviews","jsonb_set(body,'{CreatedAt}','\"not-a-date\"')"),
            ("MarketingReviewEvidences","jsonb_set(body,'{EvidenceType}','\"FutureEvidence\"')"),
            ("MarketingReviewEvidences","jsonb_set(body,'{EvidenceType}','\"SelfReported\"')"),
            ("MarketingReviewEvidences","jsonb_set(body,'{RevenueAmount}','-1')"),
            ("MarketingReviewEvidences","body-'ServiceReviewId'"),
            ("MarketingReviewEvidences","jsonb_set(body,'{EndDate}','\"2026-02-31\"')"),
            ("Services","body || '{\"UnknownAuthorityField\":true}'::jsonb"),
            ("Services","jsonb_set(body,'{PricingType}','999')"),
            ("MarketplaceProfiles","jsonb_set(body,'{IsActive}','null')"),
            ("Tenants","jsonb_set(body,'{Status}','999')"),
            ("ProviderTrustAggregates","body-'TenantId'")
        };
        foreach(var (table,body) in corruptions)
        {
            // Evidence cases intentionally target verified evidence, so self-reported
            // classification cannot silently retain verified orders/revenue.
            var selection=table=="MarketingReviewEvidences" ? " AND body->>'EvidenceType'='PosVerified'" : "";
            await f.Exec($"UPDATE marketplace_generation.inputs SET body={body} WHERE generation_id='{build.GenerationId}' AND source_table='{table}'{selection}");
            var bad=await MarketplaceGenerationInputExport.FreezeAsync(f.Db,lease,build.GenerationId,Guid.NewGuid(),checkpoint);
            await Assert.ThrowsAsync<InvalidDataException>(()=>MarketplaceFrozenServiceProjection.ReadPageAsync(f.Db,bad.ExportId,null,1));
            await f.Exec($"""
                UPDATE marketplace_generation.inputs i SET body=e.body FROM marketplace_generation.export_inputs e
                WHERE e.export_id='{original.ExportId}' AND i.generation_id='{build.GenerationId}'
                  AND i.source_table=e.source_table AND i.source_id=e.source_id;
                """);
        }
        Assert.Equal(baseline,JsonSerializer.Serialize(await Projected(f,original.ExportId)));
        var valid=await MarketplaceGenerationInputExport.FreezeAsync(f.Db,lease,build.GenerationId,Guid.NewGuid(),checkpoint);
        Assert.Equal(baseline,JsonSerializer.Serialize(await Projected(f,valid.ExportId)));
    }

    [Fact]
    public async Task FrozenProjectionRejectsUnknownLegacyFutureSchemaInventoryAndManifestCountsWithoutRelabelling()
    {
        await using var f=await Fixture.Create(applicationSchema:true);await SeedProjection(f);
        var (_,_,_,original)=await SnapshotProjection(f);
        foreach(var (format,schema,tables,count) in new[]{
            (0,(string?)null,"capture_tables",0),
            (2,original.SourceSchemaFingerprint,"capture_tables",0),
            (1,new string('a',64),"capture_tables",0),
            (1,original.SourceSchemaFingerprint,"ARRAY[]::text[]",0),
            (1,original.SourceSchemaFingerprint,"capture_tables",1)})
        {
            var id=Guid.NewGuid();
            var schemaSql=schema is null ? "NULL" : "'"+schema+"'";
            await f.Exec($"""
                INSERT INTO marketplace_generation.exports(export_id,operation_id,generation_id,journal_id,revision,
                    physical_name,physical_uuid,fingerprint,created_epoch,capture_tables,input_count,invalidation_count,input_format_version,source_schema_fingerprint)
                SELECT '{id}','{Guid.NewGuid()}',generation_id,journal_id,revision,physical_name,physical_uuid,fingerprint,
                    created_epoch,{tables},{count},0,{format},{schemaSql}
                FROM marketplace_generation.exports WHERE export_id='{original.ExportId}';
                """);
            await Assert.ThrowsAsync<InvalidDataException>(()=>MarketplaceFrozenServiceProjection.ReadPageAsync(f.Db,id,null,1));
            var unchanged=await MarketplaceGenerationInputExport.ReadHeaderAsync(f.Db,id);
            Assert.Equal(format,unchanged.InputFormatVersion);Assert.Equal(schema,unchanged.SourceSchemaFingerprint);
        }
        Assert.Equal(2,(await Projected(f,original.ExportId)).Count);
    }

    [Fact]
    public async Task FrozenProjectionRejectsDuplicateChildrenAndMalformedPendingInvalidationImages()
    {
        await using var f=await Fixture.Create(applicationSchema:true);var seed=await SeedProjection(f);
        var (lease,build,checkpoint,original)=await SnapshotProjection(f);
        foreach(var table in new[]{"ServiceReviews","MarketingReviewEvidences"})
        {
            var duplicate=Guid.NewGuid();
            await f.Exec($"""
                INSERT INTO marketplace_generation.inputs(generation_id,source_table,source_id,revision,ordinal,deleted,body)
                SELECT generation_id,source_table,'{duplicate}',revision,ordinal,deleted,jsonb_set(body,ARRAY['Id'],to_jsonb('{duplicate}'::text))
                FROM marketplace_generation.inputs WHERE generation_id='{build.GenerationId}' AND source_table='{table}' LIMIT 1;
                """);
            var bad=await MarketplaceGenerationInputExport.FreezeAsync(f.Db,lease,build.GenerationId,Guid.NewGuid(),checkpoint);
            await Assert.ThrowsAsync<InvalidDataException>(()=>MarketplaceFrozenServiceProjection.ReadPageAsync(f.Db,bad.ExportId,null,1));
            await f.Exec($"DELETE FROM marketplace_generation.inputs WHERE generation_id='{build.GenerationId}' AND source_id='{duplicate}'");
        }
        await f.Exec($"UPDATE public.\"MarketplaceProfiles\" SET \"DisplayName\"='Captured pending image' WHERE \"Id\"='{seed.Profile}'");
        checkpoint=await MarketplaceGenerationRegistry.ReplayPageAsync(f.Db,lease,build.GenerationId,checkpoint,(await f.Boundary()).Revision,100);
        await f.Exec($"UPDATE marketplace_generation.invalidations SET new_row=new_row-'DisplayName' WHERE generation_id='{build.GenerationId}'");
        var invalidImage=await MarketplaceGenerationInputExport.FreezeAsync(f.Db,lease,build.GenerationId,Guid.NewGuid(),checkpoint);
        await Assert.ThrowsAsync<InvalidDataException>(()=>MarketplaceFrozenServiceProjection.ReadPageAsync(f.Db,invalidImage.ExportId,null,1));
        Assert.Equal(2,(await Projected(f,original.ExportId)).Count);
    }

    [Fact]
    public async Task FrozenProjectionSchemaContractRejectsDdlDriftAndDoesNotReadCurrentPublicCompositeTypes()
    {
        await using var f=await Fixture.Create(applicationSchema:true);await SeedProjection(f);
        var (lease,build,checkpoint,original)=await SnapshotProjection(f);
        // Emit schema-only diagnostics (never row contents) if provider aliases/model
        // metadata differ, rather than weakening comparison to make a test green.
        var actual=await Fixture.Scalar(f.Db,"""
            SELECT jsonb_agg(jsonb_build_object('Table',c.relname,'Name',a.attname,'StoreType',format_type(a.atttypid,a.atttypmod),'Nullable',NOT a.attnotnull)
                ORDER BY c.relname COLLATE "C",a.attname COLLATE "C")::text
            FROM pg_attribute a JOIN pg_class c ON c.oid=a.attrelid JOIN pg_namespace n ON n.oid=c.relnamespace
            WHERE n.nspname='public' AND a.attnum>0 AND NOT a.attisdropped AND c.relname IN
                ('Services','MarketplaceProfiles','ServiceRequests','ServiceReviews','MarketingReviewEvidences','ProviderTrustAggregates','Tenants','UserGovernanceProfiles')
            """);
        Assert.True(original.SourceSchemaFingerprint==MarketplaceFrozenInputContract.SupportedFingerprint,
            "Expected: "+MarketplaceFrozenInputContract.SupportedSchemaDescription+" Actual: "+actual);
        var before=JsonSerializer.Serialize(await Projected(f,original.ExportId));
        await f.Setup("ALTER TABLE public.\"ServiceRequests\" ADD COLUMN \"FutureAuthorityField\" integer");
        var through=(await f.Boundary()).Revision;
        await Assert.ThrowsAsync<InvalidDataException>(()=>MarketplaceGenerationRegistry.ReplayPageAsync(f.Db,lease,build.GenerationId,checkpoint,through,100));
        Assert.Equal(checkpoint,await GenerationCheckpoint(f.Db,build.GenerationId));
        Assert.Equal(before,JsonSerializer.Serialize(await Projected(f,original.ExportId)));
        // A brand-new raw build may inventory unsupported source columns, but must
        // not acquire a supported semantic label from the newer running process.
        var next=await MarketplaceGenerationRegistry.CreateBuildAsync(f.Db,lease,Guid.NewGuid());
        await MarketplaceGenerationRegistry.BindPhysicalIdentityAsync(f.Db,lease,next.GenerationId,"future-shape-fixture",new string('a',64));
        var nextCheckpoint=await MarketplaceGenerationRegistry.SnapshotAsync(f.Db,lease,next.GenerationId);
        var unsupported=await MarketplaceGenerationInputExport.FreezeAsync(f.Db,lease,next.GenerationId,Guid.NewGuid(),nextCheckpoint);
        Assert.NotEqual(original.SourceSchemaFingerprint,unsupported.SourceSchemaFingerprint);
        await Assert.ThrowsAsync<InvalidDataException>(()=>MarketplaceFrozenServiceProjection.ReadPageAsync(f.Db,unsupported.ExportId,null,1));
    }

    [Fact]
    public async Task FrozenServiceProjectionRecomposesReviewRequestAndEvidenceTruthWithoutLiveOrAggregateLeakage()
    {
        await using var f=await Fixture.Create(applicationSchema:true);
        var seed=await SeedProjection(f);
        var (lease,build,checkpoint,frozen)=await SnapshotProjection(f);
        var initial=await Projected(f,frozen.ExportId);
        Assert.Equal(2,initial.Count);
        foreach(var output in initial)
        {
            var doc=Assert.IsType<PointOfSale.Infrastructure.Elasticsearch.Documents.MarketplaceServiceDocument>(output.Document);
            Assert.Equal(seed.Tenant,doc.TenantId);Assert.Equal(seed.Profile,doc.ProviderProfileId);
            Assert.Equal("Frozen provider",doc.ProviderDisplayName);
            Assert.Equal(2,doc.Trust.TotalReviews);Assert.Equal(4m,doc.Trust.AverageRating);
            Assert.Equal(2,doc.Trust.MarketingReviewCount);Assert.Equal(1,doc.Trust.PosVerifiedCount);
            Assert.Null(doc.Trust.LastEventId);Assert.Null(doc.Trust.LastEventOccurredAtUtc);Assert.Equal(0,doc.Trust.LastEventVersion);
        }
        var first=initial.Single(r=>r.ServiceId==seed.First).Document!;
        Assert.Equal(2,first.ServiceStats.TotalRequests);Assert.Equal(1,first.ServiceStats.CompletedRequests);
        Assert.Equal(0.5m,first.ServiceStats.CompletionRate);Assert.Equal(12.5m,first.StartingPrice);
        Assert.Equal(1,initial.Single(r=>r.ServiceId==seed.Second).Document!.ServiceStats.TotalRequests);
        var expected=ProviderTrustAggregate.FromSqlReviewTruth(seed.Tenant,seed.Profile,2,8,1,0,1,0,0,null,2,1,4,100m);
        Assert.Equal(expected.MarketingWeightedScore,first.Trust.MarketingWeightedScore);
        var before=JsonSerializer.Serialize(initial);
        // Distinct authorities advance after sealing. The mutable aggregate is deliberately
        // wrong both before and after the snapshot, and must never supply public trust.
        await f.Exec($"""
            UPDATE public."MarketplaceProfiles" SET "DisplayName"='Newer provider' WHERE "Id"='{seed.Profile}';
            UPDATE public."Services" SET "Title"='Newer title' WHERE "Id"='{seed.First}';
            UPDATE public."ServiceRequests" SET "Status"='Rejected' WHERE "Id"='{seed.CompletedRequest}';
            UPDATE public."ServiceReviews" SET "Rating"=2 WHERE "ServiceId"='{seed.Second}';
            UPDATE public."MarketingReviewEvidences" SET "RevenueAmount"=900 WHERE "Id"='{seed.Evidence}';
            UPDATE public."ProviderTrustAggregates" SET "TotalReviews"=999,"AverageRating"=1;
            """);
        var advanced=await MarketplaceGenerationRegistry.ReplayPageAsync(f.Db,lease,build.GenerationId,checkpoint,(await f.Boundary()).Revision,100);
        Assert.Equal(before,JsonSerializer.Serialize(await Projected(f,frozen.ExportId)));
        var newer=await MarketplaceGenerationInputExport.FreezeAsync(f.Db,lease,build.GenerationId,Guid.NewGuid(),advanced);
        var changed=await Projected(f,newer.ExportId);
        Assert.All(changed,r=>{Assert.Equal("Newer provider",r.Document!.ProviderDisplayName);Assert.Equal(1,r.Document.Trust.TotalReviews);Assert.Equal(2m,r.Document.Trust.AverageRating);Assert.Equal(0,r.Document.Trust.PosVerifiedCount);});
        var updated=changed.Single(r=>r.ServiceId==seed.First).Document!;
        Assert.Equal("Newer title",updated.Title);Assert.Equal(0,updated.ServiceStats.CompletedRequests);
    }

    [Fact]
    public async Task FullFrozenServicePassAppliesProfileTenantGovernanceAndDeletionDependenciesWithoutConsumingInvalidations()
    {
        await using var f=await Fixture.Create(applicationSchema:true);var seed=await SeedProjection(f);
        var (lease,build,checkpoint,original)=await SnapshotProjection(f);
        async Task<FrozenGenerationInput> Advance(string sql)
        {
            await f.Exec(sql);
            checkpoint=await MarketplaceGenerationRegistry.ReplayPageAsync(f.Db,lease,build.GenerationId,checkpoint,(await f.Boundary()).Revision,100);
            return await MarketplaceGenerationInputExport.FreezeAsync(f.Db,lease,build.GenerationId,Guid.NewGuid(),checkpoint);
        }
        var inactive=await Advance($"UPDATE public.\"MarketplaceProfiles\" SET \"IsActive\"=false WHERE \"Id\"='{seed.Profile}'");
        Assert.All(await Projected(f,inactive.ExportId),r=>Assert.Null(r.Document));
        var inactiveTenant=await Advance($"UPDATE public.\"MarketplaceProfiles\" SET \"IsActive\"=true WHERE \"Id\"='{seed.Profile}'; UPDATE public.\"Tenants\" SET \"IsActive\"=false WHERE \"Id\"='{seed.Tenant}'");
        Assert.All(await Projected(f,inactiveTenant.ExportId),r=>Assert.Null(r.Document));
        var deletedTenant=await Advance($"UPDATE public.\"Tenants\" SET \"IsActive\"=true,\"DeletedAtUtc\"=clock_timestamp() WHERE \"Id\"='{seed.Tenant}'");
        Assert.All(await Projected(f,deletedTenant.ExportId),r=>Assert.Null(r.Document));
        var suspended=await Advance($"UPDATE public.\"Tenants\" SET \"DeletedAtUtc\"=NULL,\"Status\"={(int)TenantStatus.Suspended} WHERE \"Id\"='{seed.Tenant}'");
        Assert.All(await Projected(f,suspended.ExportId),r=>Assert.Null(r.Document));
        await using(var app=new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(f.WriterConnection).Options,new TestTenantContext(seed.Tenant)))
        {
            app.UserGovernanceProfiles.Add(new(){Id=Guid.NewGuid(),SubjectId=seed.Tenant,Role=UserGovernanceRole.Vendor,Status=UserGovernanceStatus.Banned,CreatedAtUtc=DateTime.UtcNow});
            await app.SaveChangesAsync();
        }
        var banned=await Advance($"UPDATE public.\"Tenants\" SET \"Status\"={(int)TenantStatus.Active} WHERE \"Id\"='{seed.Tenant}'");
        Assert.All(await Projected(f,banned.ExportId),r=>Assert.Null(r.Document));
        // Durable governance cannot be physically deleted; do not disable its
        // existing guard to manufacture a reset or evade capture.
        await Assert.ThrowsAsync<PostgresException>(()=>f.Exec("DELETE FROM public.\"UserGovernanceProfiles\""));
        var restored=await Advance($"UPDATE public.\"UserGovernanceProfiles\" SET \"Status\"={(int)UserGovernanceStatus.Active}");
        Assert.All(await Projected(f,restored.ExportId),r=>Assert.NotNull(r.Document));
        var softDeleted=await Advance($"UPDATE public.\"Services\" SET \"IsDeleted\"=true WHERE \"Id\"='{seed.First}'");
        var soft=await Projected(f,softDeleted.ExportId);
        Assert.Null(soft.Single(r=>r.ServiceId==seed.First).Document);Assert.NotNull(soft.Single(r=>r.ServiceId==seed.Second).Document);
        // Retain the exact original export's earlier positive view and every pending
        // invalidation; composition never acknowledges or deletes durable work.
        Assert.All(await Projected(f,original.ExportId),r=>Assert.NotNull(r.Document));
        Assert.Equal(softDeleted.InvalidationCount,await Fixture.Scalar(f.Db,"SELECT count(*) FROM marketplace_generation.invalidations"));
    }

    [Fact]
    public async Task FrozenServicePagingKeepsIdentityMoveTombstonesAndRejectsForeignCursors()
    {
        await using var f=await Fixture.Create(applicationSchema:true);var seed=await SeedProjection(f);
        var extra=Service.Create(seed.Tenant,seed.Profile,"Identity move","Synthetic");
        await using(var app=new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(f.WriterConnection).Options,new TestTenantContext(seed.Tenant)))
        {app.Add(extra);await app.SaveChangesAsync();}
        var (lease,build,checkpoint,original)=await SnapshotProjection(f);
        var moved=Guid.NewGuid();await f.Exec($"UPDATE public.\"Services\" SET \"Id\"='{moved}' WHERE \"Id\"='{extra.Id}'");
        var end=(await f.Boundary()).Revision;
        checkpoint=await MarketplaceGenerationRegistry.ReplayPageAsync(f.Db,lease,build.GenerationId,checkpoint,end,100);
        var newer=await MarketplaceGenerationInputExport.FreezeAsync(f.Db,lease,build.GenerationId,Guid.NewGuid(),checkpoint);
        var outputs=await Projected(f,newer.ExportId);
        Assert.Equal(4,outputs.Count);Assert.Null(outputs.Single(r=>r.ServiceId==extra.Id).Document);
        Assert.NotNull(outputs.Single(r=>r.ServiceId==moved).Document);
        var page=await MarketplaceFrozenServiceProjection.ReadPageAsync(f.Db,newer.ExportId,null,1);
        Assert.Equal(newer,page.Export);Assert.NotNull(page.Next);
        await Assert.ThrowsAsync<ArgumentException>(()=>MarketplaceFrozenServiceProjection.ReadPageAsync(f.Db,original.ExportId,page.Next,1));
        await Assert.ThrowsAsync<ArgumentException>(()=>MarketplaceFrozenServiceProjection.ReadPageAsync(f.Db,newer.ExportId,null,129));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>MarketplaceFrozenServiceProjection.ReadPageAsync(f.Db,Guid.NewGuid(),null,1));
        await f.Exec($"DELETE FROM public.\"Services\" WHERE \"Id\"='{moved}'");
        checkpoint=await MarketplaceGenerationRegistry.ReplayPageAsync(f.Db,lease,build.GenerationId,checkpoint,(await f.Boundary()).Revision,100);
        var deleted=await MarketplaceGenerationInputExport.FreezeAsync(f.Db,lease,build.GenerationId,Guid.NewGuid(),checkpoint);
        Assert.Null((await Projected(f,deleted.ExportId)).Single(r=>r.ServiceId==moved).Document);
    }

    private static async Task<(GenerationLease,GenerationBuild,JournalPosition,FrozenGenerationInput)> SnapshotProjection(Fixture f)
    {
        var lease=(await MarketplaceGenerationRegistry.TryAcquireAsync(f.Db,Guid.NewGuid(),TimeSpan.FromMinutes(2)))!;
        var build=await MarketplaceGenerationRegistry.CreateBuildAsync(f.Db,lease,Guid.NewGuid());
        await MarketplaceGenerationRegistry.BindPhysicalIdentityAsync(f.Db,lease,build.GenerationId,"projection-fixture-uuid",new string('a',64));
        var checkpoint=await MarketplaceGenerationRegistry.SnapshotAsync(f.Db,lease,build.GenerationId);
        var frozen=await MarketplaceGenerationInputExport.FreezeAsync(f.Db,lease,build.GenerationId,Guid.NewGuid(),checkpoint);
        return(lease,build,checkpoint,frozen);
    }

    private static async Task<List<FrozenServiceProjection>> Projected(Fixture f,Guid export)
    {
        var result=new List<FrozenServiceProjection>();FrozenServiceCursor? cursor=null;
        do
        {
            await using var reconnect=await f.Open();
            var page=await MarketplaceFrozenServiceProjection.ReadPageAsync(reconnect,export,cursor,1);
            result.AddRange(page.Services);cursor=page.Next;
        }while(cursor is not null);
        return result;
    }

    private static async Task<ProjectionSeed> SeedProjection(Fixture f)
    {
        var tenant=Guid.NewGuid();var provider=Guid.NewGuid();var buyer=Guid.NewGuid();
        await using var app=new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(f.WriterConnection).Options,new TestTenantContext(tenant));
        app.Tenants.Add(new(){Id=tenant,Name="Projection tenant",Currency="EGP",IsActive=true,Status=TenantStatus.Active});
        await app.SaveChangesAsync();
        var profile=MarketplaceProfile.Create(tenant,provider,"Frozen provider");
        var first=Service.Create(tenant,profile.Id,"Frozen first","Synthetic","Marketing",12.5m,Enum.GetValues<PricingType>()[0],"EGP","[]");
        var second=Service.Create(tenant,profile.Id,"Frozen second","Synthetic","Marketing");
        app.AddRange(profile,first,second);await app.SaveChangesAsync();
        var q1=ServiceRequest.Create(tenant,first.Id,buyer,provider,profile.Id,first.Title,first.Category,null,null,null,null);
        var q2=ServiceRequest.Create(tenant,second.Id,buyer,provider,profile.Id,second.Title,second.Category,null,null,null,null);
        foreach(var q in new[]{q1,q2}){q.Accept();q.MarkCompletedByProvider();q.ConfirmCompletedByBuyer();}
        var pending=ServiceRequest.Create(tenant,first.Id,Guid.NewGuid(),provider,profile.Id,first.Title,first.Category,null,null,null,null);
        app.AddRange(q1,q2,pending);await app.SaveChangesAsync();
        var r1=ServiceReview.Create(tenant,q1.Id,first.Id,buyer,provider,5);
        var r2=ServiceReview.Create(tenant,q2.Id,second.Id,buyer,provider,3);
        var evidence=MarketingReviewEvidence.CreatePosVerified(tenant,r1.Id,"Frozen verified",Enum.GetValues<MarketingPlatform>()[0],DateTime.UtcNow.AddDays(-3),DateTime.UtcNow.AddDays(-2),4,100m);
        var self=MarketingReviewEvidence.CreateSelfReported(tenant,r2.Id,"Frozen self",Enum.GetValues<MarketingPlatform>()[0],DateTime.UtcNow.AddDays(-3),DateTime.UtcNow.AddDays(-2));
        var poisoned=ProviderTrustAggregate.Create(tenant,profile.Id);poisoned.ApplyReview(1);
        app.AddRange(r1,r2,evidence,self,poisoned);await app.SaveChangesAsync();
        return new(tenant,profile.Id,first.Id,second.Id,q1.Id,evidence.Id);
    }
    private sealed record ProjectionSeed(Guid Tenant,Guid Profile,Guid First,Guid Second,Guid CompletedRequest,Guid Evidence);

    [Fact]
    public async Task FrozenInputExportKeepsExactVersionedRowsTombstonesAndInvalidationsWhileReplayContinues()
    {
        await using var f=await Fixture.Create();
        var (lease,build)=await ExportBuild(f);
        foreach(var table in MarketplaceSourceJournal.SourceTables)await f.Insert(table,Guid.NewGuid());
        var checkpoint=await MarketplaceGenerationRegistry.SnapshotAsync(f.Db,lease,build.GenerationId);
        await f.Exec("DELETE FROM public.\"Services\"");
        var end=(await f.Boundary()).Revision;
        checkpoint=await MarketplaceGenerationRegistry.ReplayPageAsync(f.Db,lease,build.GenerationId,checkpoint,end,100);
        var operation=Guid.NewGuid();
        var frozen=await MarketplaceGenerationInputExport.FreezeAsync(f.Db,lease,build.GenerationId,operation,checkpoint);
        Assert.Equal(8,frozen.InputCount);Assert.Equal(1,frozen.InvalidationCount);
        Assert.Equal(checkpoint.Revision,frozen.Revision);Assert.Equal(lease.JournalId,frozen.JournalId);
        Assert.Equal("fixture-uuid",frozen.PhysicalUuid);Assert.Equal(new string('a',64),frozen.Fingerprint);
        var original=await FrozenRows(f,frozen.ExportId);
        Assert.Equal(8,original.Count);Assert.Single(original.Where(r=>r.Deleted));
        Assert.Equal(MarketplaceSourceJournal.SourceTables.Order(),original.Select(r=>r.Table).Order());
        Assert.All(original,r=>Assert.True(r.Position.Revision<=frozen.Revision));
        await f.Exec("UPDATE public.\"MarketplaceProfiles\" SET \"DisplayName\"='newer replay must not leak into old export'");
        await f.Insert("Services",Guid.NewGuid());
        end=(await f.Boundary()).Revision;
        var advanced=await MarketplaceGenerationRegistry.ReplayPageAsync(f.Db,lease,build.GenerationId,checkpoint,end,100);
        Assert.Equal(original,await FrozenRows(f,frozen.ExportId));
        // Retry an operation after connection restart/replay returns its original export,
        // not a second snapshot silently labelled with the original revision.
        await using var reconnect=await f.Open();
        Assert.Equal(frozen,await MarketplaceGenerationInputExport.FreezeAsync(reconnect,lease,build.GenerationId,operation,checkpoint));
        var newer=await MarketplaceGenerationInputExport.FreezeAsync(reconnect,lease,build.GenerationId,Guid.NewGuid(),advanced);
        Assert.Equal(9,newer.InputCount);Assert.Equal(3,newer.InvalidationCount);
        Assert.NotEqual(frozen.ExportId,newer.ExportId);
        Assert.Contains(await FrozenRows(f,newer.ExportId),r=>r.Body.Contains("newer replay must not leak",StringComparison.Ordinal));
        Assert.DoesNotContain(original,r=>r.Body.Contains("newer replay must not leak",StringComparison.Ordinal));
        Assert.Equal(1L,await Fixture.Scalar(f.Db,$"SELECT count(*) FROM marketplace_generation.export_invalidations WHERE export_id='{frozen.ExportId}' AND old_row IS NOT NULL AND new_row IS NULL"));
    }

    [Fact]
    public async Task FrozenExportRejectsPartialRevisionCheckpointEvenWhenCallerClaimsItIsComplete()
    {
        await using var f=await Fixture.Create();var (lease,build)=await ExportBuild(f);
        var checkpoint=await MarketplaceGenerationRegistry.SnapshotAsync(f.Db,lease,build.GenerationId);
        await using(var writer=await f.Open())
        {
            await using var tx=await writer.BeginTransactionAsync();
            await f.Insert("Services",Guid.NewGuid(),writer);await f.Insert("MarketplaceProfiles",Guid.NewGuid(),writer);
            await tx.CommitAsync();
        }
        var end=(await f.Boundary()).Revision;
        var partial=await MarketplaceGenerationRegistry.ReplayPageAsync(f.Db,lease,build.GenerationId,checkpoint,end,1);
        Assert.NotEqual(long.MaxValue,partial.Ordinal);
        await Assert.ThrowsAsync<ArgumentException>(()=>MarketplaceGenerationInputExport.FreezeAsync(f.Db,lease,build.GenerationId,Guid.NewGuid(),partial));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>MarketplaceGenerationInputExport.FreezeAsync(f.Db,lease,build.GenerationId,Guid.NewGuid(),new(partial.Revision,long.MaxValue)));
        Assert.Equal(0L,await Fixture.Scalar(f.Db,"SELECT count(*) FROM marketplace_generation.exports"));
        var complete=await MarketplaceGenerationRegistry.ReplayPageAsync(f.Db,lease,build.GenerationId,partial,end,100);
        var exported=await MarketplaceGenerationInputExport.FreezeAsync(f.Db,lease,build.GenerationId,Guid.NewGuid(),complete);
        Assert.Equal(2,exported.InputCount);Assert.Equal(2,exported.InvalidationCount);
    }

    [Fact]
    public async Task FrozenExportManifestAndRowsRejectAppendMutationTruncateAndCrossExportCursorReplay()
    {
        await using var f=await Fixture.Create();var (lease,build)=await ExportBuild(f);
        await f.Insert("Services",Guid.NewGuid());
        var checkpoint=await MarketplaceGenerationRegistry.SnapshotAsync(f.Db,lease,build.GenerationId);
        var frozen=await MarketplaceGenerationInputExport.FreezeAsync(f.Db,lease,build.GenerationId,Guid.NewGuid(),checkpoint);
        foreach(var sql in new[]{
            "UPDATE marketplace_generation.exports SET input_count=input_count+1",
            "DELETE FROM marketplace_generation.export_inputs",
            "UPDATE marketplace_generation.export_inputs SET deleted=true",
            "TRUNCATE marketplace_generation.export_inputs",
            "TRUNCATE marketplace_generation.exports CASCADE",
            "INSERT INTO marketplace_generation.export_inputs SELECT * FROM marketplace_generation.export_inputs" })
        {
            var error=await Assert.ThrowsAsync<PostgresException>(()=>f.Setup(sql));Assert.Equal("55000",error.SqlState);
        }
        var first=await MarketplaceGenerationInputExport.ReadPageAsync(f.Db,frozen.ExportId,null,1);
        Assert.NotNull(first.Next);
        await Assert.ThrowsAsync<ArgumentException>(()=>MarketplaceGenerationInputExport.ReadPageAsync(f.Db,Guid.NewGuid(),first.Next,1));
        await Assert.ThrowsAsync<ArgumentException>(()=>MarketplaceGenerationInputExport.ReadPageAsync(f.Db,frozen.ExportId,null,1001));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>MarketplaceGenerationInputExport.ReadPageAsync(f.Db,Guid.NewGuid(),null,1));
        Assert.Empty((await MarketplaceGenerationInputExport.ReadPageAsync(f.Db,frozen.ExportId,first.Next,1)).Rows);
        Assert.Single(await FrozenRows(f,frozen.ExportId));
    }

    [Fact]
    public async Task FailedFrozenCopyCommitsNoManifestOrPartialRowsAndSafeOperationRetrySucceeds()
    {
        await using var f=await Fixture.Create();var (lease,build)=await ExportBuild(f);
        await f.Insert("Services",Guid.NewGuid());await f.Insert("MarketplaceProfiles",Guid.NewGuid());
        var checkpoint=await MarketplaceGenerationRegistry.SnapshotAsync(f.Db,lease,build.GenerationId);
        await f.Setup("ALTER TABLE marketplace_generation.export_inputs ADD CONSTRAINT fixture_failure CHECK(source_table<>'MarketplaceProfiles')");
        var operation=Guid.NewGuid();
        var error=await Assert.ThrowsAsync<PostgresException>(()=>MarketplaceGenerationInputExport.FreezeAsync(f.Db,lease,build.GenerationId,operation,checkpoint));
        Assert.Equal(PostgresErrorCodes.CheckViolation,error.SqlState);
        Assert.Equal(0L,await Fixture.Scalar(f.Db,"SELECT count(*) FROM marketplace_generation.exports"));
        Assert.Equal(0L,await Fixture.Scalar(f.Db,"SELECT count(*) FROM marketplace_generation.export_inputs"));
        await f.Setup("ALTER TABLE marketplace_generation.export_inputs DROP CONSTRAINT fixture_failure");
        var frozen=await MarketplaceGenerationInputExport.FreezeAsync(f.Db,lease,build.GenerationId,operation,checkpoint);
        Assert.Equal(2,frozen.InputCount);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>MarketplaceGenerationInputExport.FreezeAsync(f.Db,lease,Guid.NewGuid(),operation,checkpoint));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>MarketplaceGenerationInputExport.FreezeAsync(f.Db,lease,build.GenerationId,operation,new(checkpoint.Revision+1,long.MaxValue)));
    }

    [Fact]
    public async Task LeaseExpiresDuringFrozenCopyRollsBackThenNewEpochCanSealSameOperation()
    {
        await using var f=await Fixture.Create();var (lease,build)=await ExportBuild(f);
        await f.Insert("Services",Guid.NewGuid());
        var checkpoint=await MarketplaceGenerationRegistry.SnapshotAsync(f.Db,lease,build.GenerationId);
        await f.Setup("""
            CREATE FUNCTION marketplace_generation.fixture_slow_export() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN PERFORM pg_sleep(1.1); RETURN NEW; END $$;
            CREATE TRIGGER fixture_slow_export BEFORE INSERT ON marketplace_generation.export_inputs
                FOR EACH ROW EXECUTE FUNCTION marketplace_generation.fixture_slow_export();
            UPDATE marketplace_generation.control SET lease_until=clock_timestamp()+interval '1 second';
            """);
        var operation=Guid.NewGuid();
        await Assert.ThrowsAsync<InvalidOperationException>(()=>MarketplaceGenerationInputExport.FreezeAsync(f.Db,lease,build.GenerationId,operation,checkpoint));
        Assert.Equal(0L,await Fixture.Scalar(f.Db,"SELECT count(*) FROM marketplace_generation.exports"));
        Assert.Equal(0L,await Fixture.Scalar(f.Db,"SELECT count(*) FROM marketplace_generation.export_inputs"));
        await f.Setup("DROP TRIGGER fixture_slow_export ON marketplace_generation.export_inputs");
        var successor=(await MarketplaceGenerationRegistry.TryAcquireAsync(f.Db,Guid.NewGuid(),TimeSpan.FromSeconds(60)))!;
        await Assert.ThrowsAsync<InvalidOperationException>(()=>MarketplaceGenerationInputExport.FreezeAsync(f.Db,lease,build.GenerationId,operation,checkpoint));
        var frozen=await MarketplaceGenerationInputExport.FreezeAsync(f.Db,successor,build.GenerationId,operation,checkpoint);
        Assert.Equal(successor.Epoch,frozen.CreatedEpoch);Assert.Equal(lease.Epoch+1,frozen.CreatedEpoch);
        Assert.Single(await FrozenRows(f,frozen.ExportId));
    }

    private static async Task<(GenerationLease,GenerationBuild)> ExportBuild(Fixture f)
    {
        await f.InstallRegistry();
        await f.InstallExport();
        var lease=(await MarketplaceGenerationRegistry.TryAcquireAsync(f.Db,Guid.NewGuid(),TimeSpan.FromSeconds(60)))!;
        var build=await MarketplaceGenerationRegistry.CreateBuildAsync(f.Db,lease,Guid.NewGuid());
        await MarketplaceGenerationRegistry.BindPhysicalIdentityAsync(f.Db,lease,build.GenerationId,"fixture-uuid",new string('a',64));
        return(lease,build);
    }

    [Fact]
    public async Task FrozenExportRejectsUnsupportedSourceOrFutureVersionAndCannotCommitUnsealedRows()
    {
        await using var f=await Fixture.Create();var (lease,build)=await ExportBuild(f);
        await f.Insert("Services",Guid.NewGuid());
        var checkpoint=await MarketplaceGenerationRegistry.SnapshotAsync(f.Db,lease,build.GenerationId);
        // Explicit fixture-only corruption setup. A future materializer must not
        // silently ignore an unknown authority input or assign it an older boundary.
        await f.Exec("UPDATE marketplace_generation.inputs SET source_table='UnknownAuthority'");
        await Assert.ThrowsAsync<InvalidDataException>(()=>MarketplaceGenerationInputExport.FreezeAsync(f.Db,lease,build.GenerationId,Guid.NewGuid(),checkpoint));
        await f.Exec("UPDATE marketplace_generation.inputs SET source_table='Services',revision=revision+1");
        await Assert.ThrowsAsync<InvalidDataException>(()=>MarketplaceGenerationInputExport.FreezeAsync(f.Db,lease,build.GenerationId,Guid.NewGuid(),checkpoint));
        Assert.Equal(0L,await Fixture.Scalar(f.Db,"SELECT count(*) FROM marketplace_generation.exports"));
        await using var writer=await f.Open();
        await using(var tx=await writer.BeginTransactionAsync())
        {
            await using var orphan=new NpgsqlCommand("""
                INSERT INTO marketplace_generation.export_inputs(export_id,source_table,source_id,revision,ordinal,deleted,body)
                SELECT @export,source_table,source_id,revision,ordinal,deleted,body FROM marketplace_generation.inputs
                """,writer,tx);
            orphan.Parameters.AddWithValue("export",Guid.NewGuid());await orphan.ExecuteNonQueryAsync();
            var error=await Assert.ThrowsAsync<PostgresException>(()=>tx.CommitAsync());
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation,error.SqlState);
        }
        Assert.Equal(0L,await Fixture.Scalar(f.Db,"SELECT count(*) FROM marketplace_generation.export_inputs"));
    }

    private static async Task<List<FrozenInputRow>> FrozenRows(Fixture f,Guid export)
    {
        var result=new List<FrozenInputRow>();FrozenInputCursor? cursor=null;
        do
        {
            // Every page uses a new connection: durability, not a held MVCC cursor.
            await using var reconnect=await f.Open();
            var page=await MarketplaceGenerationInputExport.ReadPageAsync(reconnect,export,cursor,2);
            result.AddRange(page.Rows);cursor=page.Next;
        }while(cursor is not null);
        return result;
    }

    [Theory]
    [InlineData("require-lock")]
    [InlineData("renew")]
    [InlineData("release")]
    public async Task GenerationLeaseCannotBeUsedAfterAnUnchangedControlRowWaitSpansItsExpiry(string operation)
    {
        await using var f = await Fixture.Create();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40)); var ct = deadline.Token;
        await f.InstallRegistry(ct);
        var lease = (await MarketplaceGenerationRegistry.TryAcquireAsync(f.Db, Guid.NewGuid(), TimeSpan.FromSeconds(8), ct))!;
        Assert.NotNull(lease);
        await using var holder = await f.Open(); await using var held = await holder.BeginTransactionAsync(ct);
        await using (var acquire = new NpgsqlCommand("SELECT epoch FROM marketplace_generation.control WHERE singleton FOR UPDATE", holder, held))
            Assert.Equal(lease.Epoch, Assert.IsType<long>(await acquire.ExecuteScalarAsync(ct)));
        await using var claimant = await f.Open();
        await using var claimTx = operation == "require-lock" ? await claimant.BeginTransactionAsync(ct) : null;
        async Task UseLease()
        {
            if (operation == "require-lock") await MarketplaceGenerationRegistry.RequireLeaseAsync(claimant, claimTx!, lease, true, ct);
            else if (operation == "renew") await MarketplaceGenerationRegistry.RenewAsync(claimant, lease, TimeSpan.FromSeconds(60), ct);
            else await MarketplaceGenerationRegistry.ReleaseAsync(claimant, lease, ct);
        }
        var pending = UseLease(); var released = false;
        try
        {
            var blocked = false; var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(5) && !pending.IsCompleted)
            {
                await using var probe = new NpgsqlCommand("SELECT @holder=ANY(pg_blocking_pids(@claimant))", f.Db);
                probe.Parameters.AddWithValue("holder", holder.ProcessID); probe.Parameters.AddWithValue("claimant", claimant.ProcessID);
                if (Assert.IsType<bool>(await probe.ExecuteScalarAsync(ct))) { blocked = true; break; }
                await Task.Delay(20, ct);
            }
            Assert.True(blocked, "Exact claimant must wait on the unchanged control row, not a mocked lease timer.");
            await using (var expire = new NpgsqlCommand("SELECT pg_sleep(GREATEST(0,EXTRACT(EPOCH FROM (@expiry-clock_timestamp())))+0.05)", f.Db))
            {
                expire.Parameters.AddWithValue("expiry", lease.ExpiresAtUtc); await expire.ExecuteNonQueryAsync(ct);
            }
            Assert.True(Assert.IsType<bool>(await Fixture.Scalar(f.Db, "SELECT lease_until<clock_timestamp() FROM marketplace_generation.control WHERE singleton")));
            Assert.False(pending.IsCompleted);
            await held.CommitAsync(ct); released = true;
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await pending);
            if (claimTx != null) await claimTx.RollbackAsync(ct);
            await using (var unchanged = new NpgsqlCommand("SELECT count(*) FROM marketplace_generation.control WHERE singleton AND owner_id=@owner AND epoch=@epoch AND lease_until=@expiry AND lease_until<clock_timestamp()", f.Db))
            {
                unchanged.Parameters.AddWithValue("owner", lease.Owner); unchanged.Parameters.AddWithValue("epoch", lease.Epoch);
                unchanged.Parameters.AddWithValue("expiry", lease.ExpiresAtUtc);
                Assert.Equal(1L, Assert.IsType<long>(await unchanged.ExecuteScalarAsync(ct)));
            }
            var successor = await MarketplaceGenerationRegistry.TryAcquireAsync(f.Db, Guid.NewGuid(), TimeSpan.FromSeconds(60), ct);
            Assert.NotNull(successor); Assert.Equal(lease.Epoch + 1, successor.Epoch);
            await Assert.ThrowsAsync<InvalidOperationException>(() => MarketplaceGenerationRegistry.RenewAsync(f.Db, lease, TimeSpan.FromSeconds(60), ct));
            await Assert.ThrowsAsync<InvalidOperationException>(() => MarketplaceGenerationRegistry.ReleaseAsync(f.Db, lease, ct));
        }
        finally
        {
            if (!released) await held.RollbackAsync(CancellationToken.None);
            try { await pending; } catch { /* Drain owned waiter; retain the actual assertion. */ }
        }
    }

    [Fact]
    public async Task DurableBuildOwnershipAdmitsOneOwnerAndRejectsExpiredEpochAfterTakeover()
    {
        await using var f=await Fixture.Create();
        await f.InstallRegistry();
        await using var a=await f.Open();await using var b=await f.Open();
        var contenders=await Task.WhenAll(new[]{a,b}.Select(db=>MarketplaceGenerationRegistry.TryAcquireAsync(db,Guid.NewGuid(),TimeSpan.FromSeconds(60))));
        var lease=Assert.Single(contenders.Where(x=>x!=null))!;
        Assert.Equal(1,lease.Epoch);
        Assert.Null(await MarketplaceGenerationRegistry.TryAcquireAsync(f.Db,lease.Owner,TimeSpan.FromSeconds(60)));
        var renewed=await MarketplaceGenerationRegistry.RenewAsync(f.Db,lease,TimeSpan.FromSeconds(90));
        Assert.Equal(lease.Epoch,renewed.Epoch);Assert.True(renewed.ExpiresAtUtc>lease.ExpiresAtUtc);
        // Explicit fixture-only clock-expiry setup, not client-clock authorization.
        await f.Exec("UPDATE marketplace_generation.control SET lease_until=clock_timestamp()-interval '1 second'");
        var successor=await MarketplaceGenerationRegistry.TryAcquireAsync(f.Db,Guid.NewGuid(),TimeSpan.FromSeconds(60));
        Assert.NotNull(successor);Assert.Equal(lease.Epoch+1,successor.Epoch);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>MarketplaceGenerationRegistry.RenewAsync(f.Db,lease,TimeSpan.FromSeconds(60)));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>MarketplaceGenerationRegistry.CreateBuildAsync(f.Db,lease,Guid.NewGuid()));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>MarketplaceGenerationRegistry.ReleaseAsync(f.Db,lease));
        await MarketplaceGenerationRegistry.ReleaseAsync(f.Db,successor);
        var next=await MarketplaceGenerationRegistry.TryAcquireAsync(f.Db,Guid.NewGuid(),TimeSpan.FromSeconds(60));
        Assert.NotNull(next);Assert.Equal(successor.Epoch+1,next.Epoch);
    }

    [Fact]
    public async Task BuildOperationAndPhysicalUuidAreDurableAndCannotBeReboundToRecreatedIndex()
    {
        await using var f=await Fixture.Create();
        await f.InstallRegistry();
        var lease=(await MarketplaceGenerationRegistry.TryAcquireAsync(f.Db,Guid.NewGuid(),TimeSpan.FromSeconds(60)))!;
        var operation=Guid.NewGuid();
        var build=await MarketplaceGenerationRegistry.CreateBuildAsync(f.Db,lease,operation);
        await using var reconnect=await f.Open();
        Assert.Equal(build,await MarketplaceGenerationRegistry.CreateBuildAsync(reconnect,lease,operation));
        Assert.Equal("marketplace_services_g_"+build.GenerationId.ToString("N"),build.PhysicalName);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>MarketplaceGenerationRegistry.SnapshotAsync(reconnect,lease,build.GenerationId));
        await MarketplaceGenerationRegistry.BindPhysicalIdentityAsync(reconnect,lease,build.GenerationId,"fixture-uuid",new string('a',64));
        await MarketplaceGenerationRegistry.BindPhysicalIdentityAsync(reconnect,lease,build.GenerationId,"fixture-uuid",new string('a',64));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>MarketplaceGenerationRegistry.BindPhysicalIdentityAsync(reconnect,lease,build.GenerationId,"recreated-uuid",new string('a',64)));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>MarketplaceGenerationRegistry.BindPhysicalIdentityAsync(reconnect,lease,build.GenerationId,"fixture-uuid",new string('b',64)));
        Assert.Equal("fixture-uuid",await Fixture.Scalar(f.Db,"SELECT physical_uuid FROM marketplace_generation.builds"));
    }

    [Fact]
    public async Task DurableGenerationSnapshotReplayAndTombstonesSurviveConnectionRestart()
    {
        await using var f=await Fixture.Create();
        await f.InstallRegistry();
        var lease=(await MarketplaceGenerationRegistry.TryAcquireAsync(f.Db,Guid.NewGuid(),TimeSpan.FromSeconds(60)))!;
        var build=await MarketplaceGenerationRegistry.CreateBuildAsync(f.Db,lease,Guid.NewGuid());
        await MarketplaceGenerationRegistry.BindPhysicalIdentityAsync(f.Db,lease,build.GenerationId,"synthetic-uuid",new string('a',64));
        var service=Guid.NewGuid();await f.Insert("Services",service);
        foreach(var table in MarketplaceSourceJournal.SourceTables.Where(t=>t!="Services"))await f.Insert(table,Guid.NewGuid());
        await using var writer=await f.Open();await using var transaction=await writer.BeginTransactionAsync();
        await f.Exec("UPDATE public.\"MarketplaceProfiles\" SET \"DisplayName\"='committed after generation snapshot'",writer);
        var checkpoint=await MarketplaceGenerationRegistry.SnapshotAsync(f.Db,lease,build.GenerationId);
        Assert.Equal((await f.SourceRows()).OrderBy(x=>x.Key),(await GenerationInputs(f.Db,build.GenerationId)).OrderBy(x=>x.Key));
        await transaction.CommitAsync();
        var moved=Guid.NewGuid();await f.Exec($"UPDATE public.\"Services\" SET \"Id\"='{moved}' WHERE \"Id\"='{service}'");
        await f.Exec($"DELETE FROM public.\"Services\" WHERE \"Id\"='{moved}'");
        await f.Insert("ServiceRequests",Guid.NewGuid());
        var end=(await f.Boundary()).Revision;
        await using(var reconnect=await f.Open())
        {
            while(checkpoint!=new JournalPosition(end,long.MaxValue))
                checkpoint=await MarketplaceGenerationRegistry.ReplayPageAsync(reconnect,lease,build.GenerationId,checkpoint,end,2);
        }
        Assert.Equal((await f.SourceRows()).OrderBy(x=>x.Key),(await GenerationInputs(f.Db,build.GenerationId)).OrderBy(x=>x.Key));
        Assert.Equal(2L,await Fixture.Scalar(f.Db,"SELECT count(*) FROM marketplace_generation.inputs WHERE deleted"));
        Assert.Equal(4L,await Fixture.Scalar(f.Db,"SELECT count(*) FROM marketplace_generation.invalidations"));
        Assert.Equal(checkpoint,await GenerationCheckpoint(f.Db,build.GenerationId));
        Assert.Equal(lease.Epoch,await Fixture.Scalar(f.Db,"SELECT last_epoch FROM marketplace_generation.builds"));
    }

    [Fact]
    public async Task ReplayEffectsInvalidationsAndCheckpointRollbackTogetherAndRejectStaleCas()
    {
        await using var f=await Fixture.Create();
        await f.InstallRegistry();
        var lease=(await MarketplaceGenerationRegistry.TryAcquireAsync(f.Db,Guid.NewGuid(),TimeSpan.FromSeconds(60)))!;
        var build=await MarketplaceGenerationRegistry.CreateBuildAsync(f.Db,lease,Guid.NewGuid());
        await MarketplaceGenerationRegistry.BindPhysicalIdentityAsync(f.Db,lease,build.GenerationId,"fixture-uuid",new string('a',64));
        var checkpoint=await MarketplaceGenerationRegistry.SnapshotAsync(f.Db,lease,build.GenerationId);
        await f.Insert("Services",Guid.NewGuid());await f.Insert("MarketplaceProfiles",Guid.NewGuid());
        var through=(await f.Boundary()).Revision;
        await f.Setup("ALTER TABLE marketplace_generation.inputs ADD CONSTRAINT fixture_failure CHECK(source_table<>'MarketplaceProfiles')");
        var failure=await Assert.ThrowsAsync<PostgresException>(()=>MarketplaceGenerationRegistry.ReplayPageAsync(f.Db,lease,build.GenerationId,checkpoint,through,10));
        Assert.Equal(PostgresErrorCodes.CheckViolation,failure.SqlState);
        Assert.Equal(0L,await Fixture.Scalar(f.Db,"SELECT count(*) FROM marketplace_generation.inputs"));
        Assert.Equal(0L,await Fixture.Scalar(f.Db,"SELECT count(*) FROM marketplace_generation.invalidations"));
        Assert.Equal(checkpoint,await GenerationCheckpoint(f.Db,build.GenerationId));
        await f.Setup("ALTER TABLE marketplace_generation.inputs DROP CONSTRAINT fixture_failure");
        var next=await MarketplaceGenerationRegistry.ReplayPageAsync(f.Db,lease,build.GenerationId,checkpoint,through,10);
        Assert.Equal(new JournalPosition(through,long.MaxValue),next);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>MarketplaceGenerationRegistry.ReplayPageAsync(f.Db,lease,build.GenerationId,checkpoint,through,10));
        Assert.Equal(2L,await Fixture.Scalar(f.Db,"SELECT count(*) FROM marketplace_generation.inputs"));
        Assert.Equal(2L,await Fixture.Scalar(f.Db,"SELECT count(*) FROM marketplace_generation.invalidations"));
    }

    [Fact]
    public async Task LeaseExpiringDuringSnapshotRollsBackAndRestartUsesANewSnapshot()
    {
        await using var f=await Fixture.Create();
        await f.InstallRegistry();
        var lease=(await MarketplaceGenerationRegistry.TryAcquireAsync(f.Db,Guid.NewGuid(),TimeSpan.FromSeconds(60)))!;
        var build=await MarketplaceGenerationRegistry.CreateBuildAsync(f.Db,lease,Guid.NewGuid());
        await MarketplaceGenerationRegistry.BindPhysicalIdentityAsync(f.Db,lease,build.GenerationId,"fixture-uuid",new string('a',64));
        await f.Insert("Services",Guid.NewGuid());
        await f.Setup("""
            CREATE FUNCTION marketplace_generation.fixture_slow_copy() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN PERFORM pg_sleep(1.1); RETURN NEW; END $$;
            CREATE TRIGGER fixture_slow_copy BEFORE INSERT ON marketplace_generation.inputs
                FOR EACH ROW EXECUTE FUNCTION marketplace_generation.fixture_slow_copy();
            UPDATE marketplace_generation.control SET lease_until=clock_timestamp()+interval '1 second';
            """);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>MarketplaceGenerationRegistry.SnapshotAsync(f.Db,lease,build.GenerationId));
        Assert.Equal(0L,await Fixture.Scalar(f.Db,"SELECT count(*) FROM marketplace_generation.inputs"));
        Assert.Equal("new",await Fixture.Scalar(f.Db,"SELECT state FROM marketplace_generation.builds"));
        await f.Setup("DROP TRIGGER fixture_slow_copy ON marketplace_generation.inputs");
        await f.Insert("Services",Guid.NewGuid());
        var successor=(await MarketplaceGenerationRegistry.TryAcquireAsync(f.Db,Guid.NewGuid(),TimeSpan.FromSeconds(60)))!;
        Assert.Equal(lease.Epoch+1,successor.Epoch);
        var checkpoint=await MarketplaceGenerationRegistry.SnapshotAsync(f.Db,successor,build.GenerationId);
        Assert.Equal((await f.Boundary()).Revision,checkpoint.Revision);
        Assert.Equal(2L,await Fixture.Scalar(f.Db,"SELECT count(*) FROM marketplace_generation.inputs"));
    }

    private static async Task<JournalPosition> GenerationCheckpoint(NpgsqlConnection db,Guid generation)
    {
        await using var command=new NpgsqlCommand("SELECT checkpoint_revision,checkpoint_ordinal FROM marketplace_generation.builds WHERE generation_id=@generation",db);
        command.Parameters.AddWithValue("generation",generation);await using var reader=await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());return new(reader.GetInt64(0),reader.GetInt64(1));
    }

    private static async Task<Dictionary<(string,Guid),string>> GenerationInputs(NpgsqlConnection db,Guid generation)
    {
        var result=new Dictionary<(string,Guid),string>();
        await using var command=new NpgsqlCommand("SELECT source_table,source_id,body::text FROM marketplace_generation.inputs WHERE generation_id=@generation AND NOT deleted",db);
        command.Parameters.AddWithValue("generation",generation);await using var reader=await command.ExecuteReaderAsync();
        while(await reader.ReadAsync())result[(reader.GetString(0),reader.GetGuid(1))]=reader.GetString(2);
        return result;
    }

    [Fact]
    public async Task CandidateMigrationCapturesActualApplicationProfileRequestTenantAndRawBulkWrites()
    {
        await using var f=await Fixture.Create(applicationSchema:true);
        var tenant=Guid.NewGuid();var provider=Guid.NewGuid();var buyer=Guid.NewGuid();
        var scope=new TestTenantContext(tenant);
        await using var db=new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(f.WriterConnection).Options,scope);
        db.Tenants.Add(new Tenant { Id=tenant,Name="Synthetic journal tenant",Currency="EGP",IsActive=true,Status=TenantStatus.Active });
        await db.SaveChangesAsync();
        var publisher=Mock.Of<IEventPublisher>();
        var profiles=new MarketplaceProfileService(db,scope);
        var catalog=new ServiceCatalogService(db,scope,publisher,NullLogger<ServiceCatalogService>.Instance);
        var requests=new ServiceRequestService(db,scope,new ChatService(db,scope),publisher,NullLogger<ServiceRequestService>.Instance);
        var profile=await profiles.CreateProfileAsync(provider,new CreateMarketplaceProfileDto("Before","Synthetic"));
        var service=await catalog.CreateServiceAsync(profile.Id,provider,new CreateServiceDto("Journal service","Synthetic","Fixture"));
        var boundary=await f.Boundary();
        var mirror=new JournalMirror(boundary.JournalId,boundary.Revision);
        foreach(var row in await f.SourceRows())mirror.Rows.Add(row.Key,row.Value);
        await requests.RequestServiceAsync(buyer,new RequestServiceDto(service.Id,"Synthetic request statistics"));
        // Known event gaps must still be captured at the database boundary.
        await db.Set<MarketplaceProfile>().Where(p=>p.Id==profile.Id).ExecuteUpdateAsync(u=>u.SetProperty(p=>p.DisplayName,"After raw bulk mutation"));
        await db.Set<Service>().Where(s=>s.Id==service.Id).ExecuteUpdateAsync(u=>u.SetProperty(s=>s.IsActive,false));
        await db.Tenants.Where(t=>t.Id==tenant).ExecuteUpdateAsync(u=>u.SetProperty(t=>t.Status,TenantStatus.Suspended));
        var end=await f.Boundary();var cursor=new JournalPosition(boundary.Revision,long.MaxValue);
        var captured=new List<SourceChange>();
        while(true)
        {
            var page=await MarketplaceSourceJournal.ReadPageAsync(f.Db,end.JournalId,cursor,end.Revision,2);
            if(page.Count==0)break;
            foreach(var row in page){mirror.Apply(row);captured.Add(row);}
            cursor=page[^1].Position;
        }
        Assert.Contains(captured,c=>c.Table=="ServiceRequests" && c.Operation=="INSERT");
        Assert.Contains(captured,c=>c.Table=="MarketplaceProfiles" && c.NewRow!.Contains("After raw bulk mutation",StringComparison.Ordinal));
        Assert.Contains(captured,c=>c.Table=="Tenants" && c.Operation=="UPDATE");
        Assert.Contains(captured,c=>c.Table=="Services" && c.Operation=="UPDATE");
        Assert.Equal((await f.SourceRows()).OrderBy(x=>x.Key),mirror.Rows.OrderBy(x=>x.Key));

        // Rollback must cover real EF bulk DML too, not only the synthetic executor
        // tables: neither the authoritative row nor the committed journal may advance.
        await using(var rollback=await db.Database.BeginTransactionAsync())
        {
            Assert.Equal(1,await db.Set<MarketplaceProfile>().Where(p=>p.Id==profile.Id)
                .ExecuteUpdateAsync(u=>u.SetProperty(p=>p.DisplayName,"Uncommitted application mutation")));
            await rollback.RollbackAsync();
        }
        Assert.Equal(end,await f.Boundary());
        Assert.Equal((await f.SourceRows()).OrderBy(x=>x.Key),mirror.Rows.OrderBy(x=>x.Key));
        Assert.Empty(await MarketplaceSourceJournal.ReadPageAsync(f.Db,end.JournalId,
            new(end.Revision,long.MaxValue),end.Revision,10));

        // All three candidate unmanaged migrations ran on this exact disposable
        // application database. Export actual SQL row shapes, not only synthetic rows.
        var lease=(await MarketplaceGenerationRegistry.TryAcquireAsync(f.Db,Guid.NewGuid(),TimeSpan.FromSeconds(60)))!;
        var build=await MarketplaceGenerationRegistry.CreateBuildAsync(f.Db,lease,Guid.NewGuid());
        await MarketplaceGenerationRegistry.BindPhysicalIdentityAsync(f.Db,lease,build.GenerationId,"application-fixture-uuid",new string('a',64));
        var checkpoint=await MarketplaceGenerationRegistry.SnapshotAsync(f.Db,lease,build.GenerationId);
        var frozen=await MarketplaceGenerationInputExport.FreezeAsync(f.Db,lease,build.GenerationId,Guid.NewGuid(),checkpoint);
        var actualRows=await f.SourceRows();var exportedRows=await FrozenRows(f,frozen.ExportId);
        Assert.Equal(actualRows.Count,frozen.InputCount);Assert.All(exportedRows,r=>Assert.False(r.Deleted));
        Assert.Equal(actualRows.OrderBy(r=>r.Key),exportedRows.ToDictionary(r=>(r.Table,r.SourceId),r=>r.Body).OrderBy(r=>r.Key));
        Assert.Equal(end.Revision,frozen.Revision);
    }

    [Fact]
    public async Task AllAuthorityTablesCaptureInsertUpdateSoftAndHardDeleteAndRollback()
    {
        await using var f=await Fixture.Create();
        foreach (var table in MarketplaceSourceJournal.SourceTables)
        {
            var id=Guid.NewGuid();
            await f.Insert(table,id);
            await f.Exec($"UPDATE public.\"{table}\" SET \"IsDeleted\"=true,\"TenantId\"='{Guid.NewGuid()}',\"ProfileId\"='{Guid.NewGuid()}' WHERE \"Id\"='{id}'");
            await f.Exec($"DELETE FROM public.\"{table}\" WHERE \"Id\"='{id}'");
        }
        var rows=await f.Changes();
        Assert.Equal(24,rows.Count);
        foreach (var group in rows.GroupBy(c=>c.Table))
        {
            Assert.Equal(new[]{"INSERT","UPDATE","DELETE"},group.Select(c=>c.Operation));
            Assert.NotEqual(group.ElementAt(1).OldRow,group.ElementAt(1).NewRow);
            Assert.NotNull(group.Last().OldRow); Assert.Null(group.Last().NewRow);
        }
        var before=await f.Boundary();
        await using(var writer=await f.Open())
        {
            await using var tx=await writer.BeginTransactionAsync();
            await f.Insert("Services",Guid.NewGuid(),writer);
            await tx.RollbackAsync();
        }
        Assert.Equal(before,await f.Boundary()); Assert.Equal(24,(await f.Changes()).Count);
    }

    [Fact]
    public async Task GateIsHeldBeforeMutationThroughCommitAndWaitingWriterSeesCompletePrefix()
    {
        await using var f=await Fixture.Create();
        await using var first=await f.Open(); await using var second=await f.Open();
        await using var tx=await first.BeginTransactionAsync();
        var a=Guid.NewGuid(); var b=Guid.NewGuid();
        await f.Insert("Services",a,first);
        var waiting=f.Insert("MarketplaceProfiles",b,second);
        await f.WaitForGate(second.ProcessID);
        Assert.False(waiting.IsCompleted);
        Assert.Equal(0,(await f.Boundary()).Revision);
        Assert.Empty(await f.Changes());
        await tx.CommitAsync(); await waiting.WaitAsync(TimeSpan.FromSeconds(10));
        var rows=await f.Changes();
        Assert.Equal(new[]{a,b},rows.Select(c=>c.SourceId));
        Assert.True(rows[0].Position.Revision<rows[1].Position.Revision);
    }

    [Fact]
    public async Task MultipleConcurrentWritersProduceACompleteCommittedPrefix()
    {
        await using var f=await Fixture.Create();
        var ids=Enumerable.Range(0,12).Select(_=>Guid.NewGuid()).ToArray();
        await Task.WhenAll(ids.Select(async id=>
        {
            await using var writer=await f.Open();
            await using var tx=await writer.BeginTransactionAsync();
            await f.Insert("Services",id,writer); await tx.CommitAsync();
        }));
        var rows=await f.Changes();
        Assert.Equal(ids.Order(),rows.Select(c=>c.SourceId).Order());
        Assert.Equal(Enumerable.Range(1,12).Select(n=>(long)n),rows.Select(c=>c.Position.Revision));
        Assert.Equal(12,(await f.Boundary()).Revision);
    }

    [Theory]
    [InlineData(IsolationLevel.RepeatableRead)]
    [InlineData(IsolationLevel.Serializable)]
    public async Task StaleSnapshotWriterAbortsAndFullTransactionRetryCapturesOnce(IsolationLevel isolation)
    {
        await using var f=await Fixture.Create(); await using var old=await f.Open();
        await using(var tx=await old.BeginTransactionAsync(isolation))
        {
            await Fixture.Scalar(old,"SELECT revision FROM marketplace_journal.gate");
            await f.Insert("Services",Guid.NewGuid());
            var error=await Assert.ThrowsAsync<PostgresException>(()=>f.Insert("Services",Guid.NewGuid(),old));
            Assert.Equal(PostgresErrorCodes.SerializationFailure,error.SqlState);
            await tx.RollbackAsync();
        }
        await using(var retry=await old.BeginTransactionAsync(isolation))
        {
            await f.Insert("Services",Guid.NewGuid(),old); await retry.CommitAsync();
        }
        Assert.Equal(2,(await f.Changes()).Count);
    }

    [Fact]
    public async Task SnapshotPlusReplayIncludesActiveLongWriterAndEarlierStartedLaterCommitAndFanout()
    {
        await using var f=await Fixture.Create();
        var tenant=Guid.NewGuid(); var profile=Guid.NewGuid(); var service=Guid.NewGuid();
        await f.Insert("Tenants",tenant); await f.Insert("MarketplaceProfiles",profile);
        await f.Insert("Services",service);
        await f.Exec($"UPDATE \"Services\" SET \"TenantId\"='{tenant}',\"ProfileId\"='{profile}' WHERE \"Id\"='{service}'");
        await using var longWriter=await f.Open(); await using var early=await f.Open();
        await using var longTx=await longWriter.BeginTransactionAsync();
        await using var earlyTx=await early.BeginTransactionAsync();
        await f.Exec("UPDATE \"MarketplaceProfiles\" SET \"DisplayName\"='committed after snapshot'",longWriter);
        await using var snapshot=await f.Open();
        JournalMirror mirror;
        JournalBoundary boundary;
        await using(var snap=await snapshot.BeginTransactionAsync(IsolationLevel.RepeatableRead))
        {
            boundary=await MarketplaceSourceJournal.ReadSnapshotBoundaryAsync(snapshot,snap);
            mirror=new JournalMirror(boundary.JournalId,boundary.Revision);
            foreach(var row in await f.SourceRows(snapshot)) mirror.Rows.Add(row.Key,row.Value);
            await longTx.CommitAsync();
            // A later-started transaction commits before the earlier-started transaction.
            await f.Insert("ServiceRequests",Guid.NewGuid());
            var child=Guid.NewGuid(); await f.Insert("Services",child,early);
            await f.Exec($"UPDATE \"Services\" SET \"ProfileId\"='{profile}' WHERE \"Id\"='{child}'",early);
            await earlyTx.CommitAsync();
            Assert.DoesNotContain("committed after snapshot",(await f.SourceRows(snapshot))[("MarketplaceProfiles",profile)]);
            await snap.CommitAsync();
        }
        var end=await f.Boundary();
        var position=new JournalPosition(boundary.Revision,long.MaxValue);
        while(true)
        {
            var page=await MarketplaceSourceJournal.ReadPageAsync(f.Db,end.JournalId,position,end.Revision,2);
            if(page.Count==0)break;
            foreach(var change in page)mirror.Apply(change);
            position=page[^1].Position;
        }
        Assert.Equal((await f.SourceRows()).OrderBy(x=>x.Key),mirror.Rows.OrderBy(x=>x.Key));
        var childRows=mirror.Rows.Where(x=>x.Key.Item1=="Services" &&
            JsonDocument.Parse(x.Value).RootElement.GetProperty("ProfileId").GetGuid()==profile).ToArray();
        Assert.Equal(2,childRows.Length);
        Assert.Contains("committed after snapshot",mirror.Rows[("MarketplaceProfiles",profile)]);
    }

    [Fact]
    public async Task BackendProcessDeathRollsBackBeforeCommitButCannotLoseCommittedMutation()
    {
        await using var f=await Fixture.Create(); var lost=Guid.NewGuid();var saved=Guid.NewGuid();
        await using(var writer=await f.Open())
        {
            await using var tx=await writer.BeginTransactionAsync();
            await f.Insert("Services",lost,writer);
            Assert.True((bool)(await Fixture.Scalar(f.Db,$"SELECT pg_terminate_backend({writer.ProcessID})"))!);
        }
        await using(var writer=await f.Open())
        {
            await f.Insert("Services",saved,writer);
            Assert.True((bool)(await Fixture.Scalar(f.Db,$"SELECT pg_terminate_backend({writer.ProcessID})"))!);
        }
        var rows=await f.Changes(); Assert.Single(rows); Assert.Equal(saved,rows[0].SourceId);
        Assert.False((await f.SourceRows()).ContainsKey(("Services",lost)));
    }

    [Fact]
    public async Task CheckpointsPersistAtomicallyRestartAndRejectCompetingOrForeignCursor()
    {
        await using var f=await Fixture.Create();
        for(var i=0;i<5;i++)await f.Insert("Services",Guid.NewGuid());
        var boundary=await f.Boundary(); var cursor=new JournalPosition(0,0);
        var first=await MarketplaceSourceJournal.ReadPageAsync(f.Db,boundary.JournalId,cursor,boundary.Revision,2);
        Assert.Equal(2,first.Count);
        await using(var tx=await f.Db.BeginTransactionAsync())
        {
            await MarketplaceSourceJournal.SaveCheckpointAsync(f.Db,tx,"test",boundary.JournalId,cursor,first[^1].Position);
            await tx.RollbackAsync(); // consumer failure: no checkpoint advancement
        }
        Assert.Equal(0L,await Fixture.Scalar(f.Db,"SELECT count(*) FROM marketplace_journal.checkpoints"));
        await using(var tx=await f.Db.BeginTransactionAsync())
        {
            await MarketplaceSourceJournal.SaveCheckpointAsync(f.Db,tx,"test",boundary.JournalId,cursor,first[^1].Position);
            await tx.CommitAsync();
        }
        await using(var restarted=await f.Open())
        {
            await using var cmd=new NpgsqlCommand("SELECT revision,ordinal FROM marketplace_journal.checkpoints WHERE consumer='test'",restarted);
            await using(var reader=await cmd.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());cursor=new(reader.GetInt64(0),reader.GetInt64(1));
            }
            Assert.Equal(2,(await MarketplaceSourceJournal.ReadPageAsync(restarted,boundary.JournalId,cursor,boundary.Revision,2)).Count);
            await using var tx=await restarted.BeginTransactionAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(()=>MarketplaceSourceJournal.SaveCheckpointAsync(
                restarted,tx,"test",boundary.JournalId,new(0,0),first[^1].Position));
            await tx.RollbackAsync();
        }
        await Assert.ThrowsAsync<InvalidDataException>(()=>MarketplaceSourceJournal.ReadPageAsync(f.Db,Guid.NewGuid(),cursor,boundary.Revision,2));
        await Assert.ThrowsAsync<InvalidDataException>(()=>MarketplaceSourceJournal.ReadPageAsync(f.Db,boundary.JournalId,cursor,boundary.Revision+1,2));
    }

    [Fact]
    public async Task RawBulkCopyCascadeAndRoleRestrictionsCannotSilentlyOmitCapture()
    {
        await using var f=await Fixture.Create();
        await f.Setup("CREATE ROLE app17h_writer NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION; GRANT USAGE ON SCHEMA public TO app17h_writer; GRANT SELECT,INSERT,UPDATE,DELETE ON ALL TABLES IN SCHEMA public TO app17h_writer; GRANT app17h_writer TO app17c_fixture");
        try
        {
            await f.Exec("SET ROLE app17h_writer");
            await f.Insert("Services",Guid.NewGuid());
            await f.Exec("UPDATE \"Services\" SET \"Title\"='bulk raw SQL'");
            var error=await Assert.ThrowsAsync<PostgresException>(()=>f.Exec("ALTER TABLE \"Services\" DISABLE TRIGGER marketplace_capture"));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege,error.SqlState);
            error=await Assert.ThrowsAsync<PostgresException>(()=>f.Exec("SET session_replication_role=replica"));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege,error.SqlState);
            error=await Assert.ThrowsAsync<PostgresException>(()=>f.Exec("DELETE FROM marketplace_journal.changes"));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege,error.SqlState);
        }
        finally { await f.Exec("RESET ROLE"); }
        await f.Setup("SET session_replication_role=replica");
        try { await f.Insert("Services",Guid.NewGuid(),f.OwnerDb); } // ALWAYS capture still covers privileged fixture-only DML.
        finally { await f.Setup("SET session_replication_role=origin"); }
        var truncate = await Assert.ThrowsAsync<PostgresException>(()=>f.Setup("TRUNCATE \"Services\""));
        Assert.Contains("MARKETPLACE_JOURNAL_TRUNCATE_REJECTED",truncate.MessageText);
        await f.Setup("REVOKE app17h_writer FROM app17c_fixture; DROP OWNED BY app17h_writer; DROP ROLE app17h_writer");
        Assert.Equal(3,(await f.Changes()).Count);
        // COPY and FK cascades are tested against real executor paths, not EF callbacks.
        var id=Guid.NewGuid();
        await using(var copy=await f.Db.BeginBinaryImportAsync("COPY \"Tenants\" (\"Id\") FROM STDIN (FORMAT BINARY)"))
        { await copy.StartRowAsync(); await copy.WriteAsync(id); await copy.CompleteAsync(); }
        await f.Setup("ALTER TABLE \"Services\" ADD CONSTRAINT fixture_parent FOREIGN KEY (\"TenantId\") REFERENCES \"Tenants\"(\"Id\") ON DELETE CASCADE NOT VALID");
        await f.Exec($"UPDATE \"Services\" SET \"TenantId\"='{id}'");
        await f.Exec($"DELETE FROM \"Tenants\" WHERE \"Id\"='{id}'");
        Assert.DoesNotContain(await f.SourceRows(), x=>x.Key.Item1=="Services");
        Assert.Equal(3,(await f.Changes()).Count(c=>c.Operation=="DELETE"));
    }

    [Fact]
    public async Task ReplacedCaptureFunctionIsRejectedEvenWhenTriggerNamesAndFlagsAreUnchanged()
    {
        await using var f=await Fixture.Create();
        await f.Setup("CREATE OR REPLACE FUNCTION marketplace_journal.capture_row() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,marketplace_journal AS $$ BEGIN RETURN NULL; END $$");
        await Assert.ThrowsAsync<InvalidDataException>(()=>f.Boundary());
    }

    [Fact]
    public async Task DisabledCaptureIsRejectedRatherThanAcceptedAsAnEmptyPage()
    {
        await using var f=await Fixture.Create();
        await f.Setup("ALTER TABLE \"Services\" DISABLE TRIGGER marketplace_capture");
        await Assert.ThrowsAsync<InvalidDataException>(()=>f.Boundary());
        await f.Setup("ALTER TABLE \"Services\" ENABLE ALWAYS TRIGGER marketplace_capture");
        await MarketplaceSourceJournal.VerifyCaptureInstalledAsync(f.Db);
    }

    [Fact]
    public async Task UnreviewedAfterRowTriggerCannotSilentlyInvalidateRowReplayOrdering()
    {
        await using var f=await Fixture.Create();
        await f.Setup("CREATE FUNCTION public.fixture_after() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NULL; END $$; CREATE TRIGGER a_unreviewed AFTER UPDATE ON \"Services\" FOR EACH ROW EXECUTE FUNCTION public.fixture_after()");
        await Assert.ThrowsAsync<InvalidDataException>(()=>f.Boundary());
        await f.Setup("DROP TRIGGER a_unreviewed ON \"Services\"; DROP FUNCTION public.fixture_after()");
        await MarketplaceSourceJournal.VerifyCaptureInstalledAsync(f.Db);
    }

    [Fact]
    public async Task BoundedRawWritePerformanceComparisonRecordsSerializationCost()
    {
        await using var f=await Fixture.Create();
        var results=new List<object>();
        foreach(var capture in new[]{false,true})
        {
            foreach(var table in MarketplaceSourceJournal.SourceTables)
                await f.Setup($"ALTER TABLE public.\"{table}\" {(capture?"ENABLE ALWAYS":"DISABLE")} TRIGGER marketplace_gate; ALTER TABLE public.\"{table}\" {(capture?"ENABLE ALWAYS":"DISABLE")} TRIGGER marketplace_capture");
            foreach(var writers in new[]{1,4})
            {
                var latencies=new System.Collections.Concurrent.ConcurrentBag<double>();
                var clock=Stopwatch.StartNew();
                await Task.WhenAll(Enumerable.Range(0,writers).Select(async _=>
                {
                    await using var c=await f.Open();
                    for(var i=0;i<30;i++)
                    {
                        var timer=Stopwatch.StartNew();
                        await using var tx=await c.BeginTransactionAsync();
                        await f.Insert("Services",Guid.NewGuid(),c);await tx.CommitAsync();
                        latencies.Add(timer.Elapsed.TotalMilliseconds);
                    }
                }));
                var sorted=latencies.Order().ToArray();
                results.Add(new{capture,writers,transactions=sorted.Length,seconds=clock.Elapsed.TotalSeconds,
                    transactionsPerSecond=sorted.Length/clock.Elapsed.TotalSeconds,meanMs=sorted.Average(),
                    p50Ms=sorted[(int)(sorted.Length*.5)],p95Ms=sorted[(int)(sorted.Length*.95)],maxMs=sorted[^1]});
            }
        }
        // Controlled lock-wait measurement with an intentionally long source transaction.
        await using var holder=await f.Open();await using var waiter=await f.Open();
        await using var held=await holder.BeginTransactionAsync();await f.Insert("Services",Guid.NewGuid(),holder);
        var duration=Stopwatch.StartNew();var pending=f.Insert("Services",Guid.NewGuid(),waiter);
        await f.WaitForGate(waiter.ProcessID);await Task.Delay(200);await held.CommitAsync();await pending;
        var root=Environment.GetEnvironmentVariable("APP17D_EVIDENCE_DIR") ?? throw new InvalidOperationException();
        await File.WriteAllTextAsync(Path.Combine(root,"journal-performance.json"),JsonSerializer.Serialize(new
        {
            fixture="synthetic eight-table PostgreSQL executor contract; NOT application EF-write performance certification",
            baseline="same schema and commands with gate/capture triggers disabled; not a rebuilt G application benchmark",
            results,controlledGateWaitMs=duration.Elapsed.TotalMilliseconds,globalWriteSerialization=true,
            performanceAcceptabilityCertified=false
        },new JsonSerializerOptions{WriteIndented=true}));
        Assert.True(duration.Elapsed.TotalMilliseconds>=200);
    }

    // Deliberately narrow SQL executor fixture. Model-table identity is checked separately.
    // This is not a certification that every application caller/production role has been reviewed.
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string admin;
        private readonly string database;
        private readonly string writerConnection;
        private Fixture(string admin,string database,string writerConnection,NpgsqlConnection db,NpgsqlConnection owner)
        {this.admin=admin;this.database=database;this.writerConnection=writerConnection;Db=db;OwnerDb=owner;}
        internal NpgsqlConnection Db {get;}
        internal NpgsqlConnection OwnerDb {get;}
        internal string WriterConnection => writerConnection;
        internal static async Task<Fixture> Create(bool applicationSchema=false)
        {
            var input=Environment.GetEnvironmentVariable("APP17D_POSTGRES") ?? throw new InvalidOperationException("Restricted disposable PostgreSQL runtime role required.");
            var cs=new NpgsqlConnectionStringBuilder(input);
            if(cs.Host!="app17c-pg" || cs.Database!="app17c_fixture" || cs.Username!="app17c_fixture")throw new InvalidOperationException("Fixture identity rejected.");
            cs.Pooling=false;
            var setupInput=Environment.GetEnvironmentVariable("APP17D_POSTGRES_SETUP") ?? throw new InvalidOperationException("Separate disposable setup identity required; no privileged writer fallback.");
            var setup=new NpgsqlConnectionStringBuilder(setupInput){Pooling=false};
            if(setup.Host!="app17c-pg" || setup.Database!="app17c_fixture" || setup.Username!="app17c_fixture_setup")
                throw new InvalidOperationException("Disposable setup identity rejected.");
            await using(var runtime=new NpgsqlConnection(cs.ConnectionString))
            {
                await runtime.OpenAsync();
                await using var flags=new NpgsqlCommand("SELECT NOT rolsuper AND NOT rolcreatedb AND NOT rolcreaterole AND NOT rolreplication AND NOT rolbypassrls AND NOT pg_has_role(current_user,'app17c_fixture_setup','MEMBER') FROM pg_roles WHERE rolname=current_user",runtime);
                if(await flags.ExecuteScalarAsync() is not true)throw new InvalidOperationException("Privileged or setup-member runtime writer rejected.");
            }
            await using var admin=new NpgsqlConnection(setup.ConnectionString);await admin.OpenAsync();
            var database="app17h_journal_"+Guid.NewGuid().ToString("N");
            await new NpgsqlCommand($"CREATE DATABASE \"{database}\"",admin).ExecuteNonQueryAsync();
            var original=setup.ConnectionString;cs.Database=database;setup.Database=database;
            var db=new NpgsqlConnection(cs.ConnectionString);
            var owner=new NpgsqlConnection(setup.ConnectionString);
            var f=new Fixture(original,database,cs.ConnectionString,db,owner);
            try
            {
                await owner.OpenAsync();
                await f.Setup($"REVOKE ALL ON DATABASE \"{database}\" FROM PUBLIC; GRANT CONNECT,TEMPORARY ON DATABASE \"{database}\" TO app17c_fixture; REVOKE CREATE ON SCHEMA public FROM PUBLIC");
                if(applicationSchema)
                {
                    await using var application=new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
                        .UseNpgsql(setup.ConnectionString).Options);
                    await application.Database.MigrateAsync();
                    await f.GrantRuntime();
                    await db.OpenAsync();
                    await MarketplaceSourceJournal.VerifyCaptureInstalledAsync(db);
                    return f;
                }
                foreach(var table in MarketplaceSourceJournal.SourceTables)
                    await f.Setup($"""
                    CREATE TABLE public."{table}" (
                        "Id" uuid PRIMARY KEY, "TenantId" uuid DEFAULT '11111111-1111-1111-1111-111111111111',
                        "ProfileId" uuid DEFAULT '22222222-2222-2222-2222-222222222222',
                        "ProviderProfileId" uuid DEFAULT '22222222-2222-2222-2222-222222222222',
                        "ServiceId" uuid DEFAULT '33333333-3333-3333-3333-333333333333',
                        "ServiceRequestId" uuid DEFAULT '44444444-4444-4444-4444-444444444444',
                        "ServiceReviewId" uuid DEFAULT '55555555-5555-5555-5555-555555555555',
                        "SubjectId" uuid DEFAULT '11111111-1111-1111-1111-111111111111',
                        "IsDeleted" boolean NOT NULL DEFAULT false,"IsActive" boolean NOT NULL DEFAULT true,
                        "Title" text NOT NULL DEFAULT 'fixture',"DisplayName" text NOT NULL DEFAULT 'before snapshot',
                        "Status" integer NOT NULL DEFAULT 0,"Rating" integer NOT NULL DEFAULT 5)
                    """);
                await MarketplaceSourceJournal.InstallDisposableAsync(owner);
                await f.GrantRuntime();
                await db.OpenAsync();
                return f;
            }
            catch(Exception initializationError)
            {
                // A factory failure occurs before await-using can own the fixture. Remove only
                // this factory's uniquely named database, retaining both errors if cleanup fails.
                try { await f.DisposeAsync(); }
                catch(Exception cleanupError) { throw new AggregateException(initializationError,cleanupError); }
                throw;
            }
        }
        internal async Task<NpgsqlConnection> Open(){var c=new NpgsqlConnection(writerConnection);await c.OpenAsync();return c;}
        internal async Task Exec(string sql,NpgsqlConnection? c=null){await using var cmd=new NpgsqlCommand(sql,c??Db);await cmd.ExecuteNonQueryAsync();}
        internal async Task Setup(string sql)
        {
            if(OwnerDb.Host!="app17c-pg" || OwnerDb.Database!=database || !database.StartsWith("app17h_journal_",StringComparison.Ordinal))throw new InvalidOperationException("Owned setup database required.");
            await using var command=new NpgsqlCommand(sql,OwnerDb);await command.ExecuteNonQueryAsync();
        }
        internal async Task InstallRegistry(CancellationToken ct=default)
        {await MarketplaceGenerationRegistry.InstallDisposableAsync(OwnerDb,ct);await GrantRuntime();}
        internal async Task InstallExport()
        {await MarketplaceGenerationInputExport.InstallDisposableAsync(OwnerDb);await GrantRuntime();}
        private async Task GrantRuntime()
        {
            await Setup("GRANT USAGE ON SCHEMA public,marketplace_journal TO app17c_fixture; GRANT SELECT ON ALL TABLES IN SCHEMA marketplace_journal TO app17c_fixture; GRANT INSERT,UPDATE ON marketplace_journal.checkpoints TO app17c_fixture");
            foreach(var table in MarketplaceSourceJournal.SourceTables)
                await Setup($"GRANT SELECT,INSERT,UPDATE,DELETE ON public.\"{table}\" TO app17c_fixture");
            // Explicit nonfinancial companions of the existing service-authoring
            // case, not an ALL TABLES public-schema data or ownership grant.
            foreach(var table in new[]{"ServiceRequestMessages","MarketplaceEventOutboxMessages","MarketplaceProjectionOutboxMessages"})
                if(await Scalar(OwnerDb,$"SELECT to_regclass('public.\"{table}\"') IS NOT NULL") is true)
                    await Setup($"GRANT SELECT,INSERT,UPDATE,DELETE ON public.\"{table}\" TO app17c_fixture");
            // Normal EF identity maintenance reads/creates canonical links when
            // seeding profiles. Keep this companion insert-only for the runtime;
            // do not grant schema ownership or broad public-table privileges.
            if(await Scalar(OwnerDb,"SELECT to_regclass('public.\"CanonicalIdentityLinks\"') IS NOT NULL") is true)
            {
                await Setup("GRANT SELECT,INSERT ON public.\"CanonicalIdentityLinks\" TO app17c_fixture");
                Assert.True(await Scalar(OwnerDb,"SELECT has_table_privilege('app17c_fixture','public.\"CanonicalIdentityLinks\"','SELECT') AND has_table_privilege('app17c_fixture','public.\"CanonicalIdentityLinks\"','INSERT') AND NOT has_table_privilege('app17c_fixture','public.\"CanonicalIdentityLinks\"','UPDATE') AND NOT has_table_privilege('app17c_fixture','public.\"CanonicalIdentityLinks\"','DELETE')") is true);
            }
            if(await Scalar(OwnerDb,"SELECT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname='marketplace_generation')") is true)
            {
                await Setup("GRANT USAGE ON SCHEMA marketplace_generation TO app17c_fixture; GRANT SELECT,INSERT,UPDATE,DELETE ON ALL TABLES IN SCHEMA marketplace_generation TO app17c_fixture");
                if(await Scalar(OwnerDb,"SELECT to_regprocedure('marketplace_generation.lock_source_revision(uuid)') IS NOT NULL") is true)
                    await Setup("GRANT EXECUTE ON FUNCTION marketplace_generation.lock_source_revision(uuid),marketplace_generation.lock_reader_pointer() TO app17c_fixture");
            }
        }
        internal Task Insert(string table,Guid id,NpgsqlConnection? c=null)=>Exec($"INSERT INTO public.\"{table}\" (\"Id\") VALUES ('{id}')",c);
        internal static async Task<object?> Scalar(NpgsqlConnection c,string sql){await using var cmd=new NpgsqlCommand(sql,c);return await cmd.ExecuteScalarAsync();}
        internal async Task<JournalBoundary> Boundary()
        {
            await using var tx=await Db.BeginTransactionAsync(IsolationLevel.RepeatableRead);
            var result=await MarketplaceSourceJournal.ReadSnapshotBoundaryAsync(Db,tx);await tx.CommitAsync();return result;
        }
        internal async Task<IReadOnlyList<SourceChange>> Changes()
        {var b=await Boundary();return await MarketplaceSourceJournal.ReadPageAsync(Db,b.JournalId,new(0,0),b.Revision,1000);}
        internal async Task<Dictionary<(string,Guid),string>> SourceRows(NpgsqlConnection? c=null)
        {
            var rows=new Dictionary<(string,Guid),string>();
            foreach(var table in MarketplaceSourceJournal.SourceTables)
            {
                await using var cmd=new NpgsqlCommand($"SELECT \"Id\",to_jsonb(s)::text FROM public.\"{table}\" s",c??Db);
                await using var reader=await cmd.ExecuteReaderAsync();
                while(await reader.ReadAsync())rows[(table,reader.GetGuid(0))]=reader.GetString(1);
            }
            return rows;
        }
        internal async Task WaitForGate(int pid,Task? operation=null)
        {
            var watch=Stopwatch.StartNew();
            while(watch.Elapsed<TimeSpan.FromSeconds(10))
            {
                if(operation?.IsCompleted==true)
                {
                    await operation; // surface the actual SQL failure, not a false lock timeout
                    throw new InvalidOperationException("Operation completed before its expected PostgreSQL lock wait.");
                }
                if((bool)(await Scalar(Db,$"SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE pid={pid} AND wait_event_type='Lock')"))!)return;
                await Task.Delay(20);
            }
            throw new TimeoutException("Writer did not enter PostgreSQL lock wait.");
        }
        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();await OwnerDb.DisposeAsync(); await using var c=new NpgsqlConnection(admin);await c.OpenAsync();
            if(!database.StartsWith("app17h_journal_",StringComparison.Ordinal))throw new InvalidOperationException();
            await new NpgsqlCommand($"DROP DATABASE \"{database}\" WITH (FORCE)",c).ExecuteNonQueryAsync();
        }
    }
}

using System.Text.Json;
using Npgsql;

namespace PointOfSale.Tests.Marketplace;

public sealed partial class MarketplaceSourceJournalPostgresTests
{
    [Fact]
    public async Task GenerationSourceLockRepairPreservesLeastPrivilegeAndExplicitFunctionBoundary()
    {
        await using var f = await Fixture.Create(applicationSchema: true);
        var evidence = Environment.GetEnvironmentVariable("APP17D_EVIDENCE_DIR")
            ?? throw new InvalidOperationException("Owned evidence root required.");
        var denials = new List<object>();
        Assert.Equal("app17c_fixture", await Fixture.Scalar(f.Db, "SELECT current_user"));
        Assert.True(await Fixture.Scalar(f.Db, """
            SELECT NOT rolsuper AND NOT rolcreatedb AND NOT rolcreaterole AND NOT rolreplication AND NOT rolbypassrls
              AND NOT pg_has_role(current_user,'app17c_fixture_setup','MEMBER')
            FROM pg_roles WHERE rolname=current_user
            """) is true);
        Assert.True(await Fixture.Scalar(f.Db, """
            SELECT has_schema_privilege(current_user,'marketplace_generation','USAGE')
              AND NOT has_schema_privilege(current_user,'marketplace_generation','CREATE')
              AND has_table_privilege(current_user,'marketplace_journal.gate','SELECT')
              AND NOT has_table_privilege(current_user,'marketplace_journal.gate','UPDATE')
              AND NOT has_table_privilege(current_user,'marketplace_journal.gate','INSERT')
              AND NOT has_table_privilege(current_user,'marketplace_journal.gate','DELETE')
              AND NOT has_table_privilege(current_user,'marketplace_journal.gate','TRUNCATE')
              AND NOT has_sequence_privilege(current_user,'marketplace_journal.changes_ordinal_seq','USAGE')
              AND has_function_privilege(current_user,'marketplace_generation.lock_source_revision(uuid)','EXECUTE')
            """) is true);
        var journal = (Guid)(await Fixture.Scalar(f.Db, "SELECT journal_id FROM marketplace_journal.gate"))!;
        var before = await Fixture.Scalar(f.Db, "SELECT revision FROM marketplace_journal.gate");
        await using (var tx = await f.Db.BeginTransactionAsync())
        {
            await using var call = new NpgsqlCommand("SELECT marketplace_generation.lock_source_revision(@journal)", f.Db, tx);
            call.Parameters.AddWithValue("journal", journal);
            Assert.Equal(before, await call.ExecuteScalarAsync());
            call.Parameters["journal"].Value = Guid.NewGuid();
            Assert.Equal(DBNull.Value, await call.ExecuteScalarAsync());
            await tx.CommitAsync();
        }
        Assert.Equal(before, await Fixture.Scalar(f.Db, "SELECT revision FROM marketplace_journal.gate"));
        foreach (var sql in new[] {
            "SELECT revision FROM marketplace_journal.gate FOR SHARE",
            "UPDATE marketplace_journal.gate SET revision=revision",
            "DELETE FROM marketplace_journal.changes",
            "TRUNCATE marketplace_journal.gate",
            "SELECT nextval('marketplace_journal.changes_ordinal_seq')",
            "ALTER TABLE public.\"Services\" DISABLE TRIGGER marketplace_gate",
            "SET session_replication_role=replica",
            "CREATE TABLE marketplace_generation.forbidden_permission_probe(id integer)",
            "CREATE OR REPLACE FUNCTION marketplace_generation.lock_source_revision(uuid) RETURNS bigint LANGUAGE sql AS 'SELECT 0::bigint'" })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => f.Exec(sql));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
            denials.Add(new { actor = "app17c_fixture", sql, error.SqlState, error.MessageText });
        }
        var role = "app17h_permission_" + Guid.NewGuid().ToString("N");
        var password = Guid.NewGuid().ToString("N");
        await f.Setup($"CREATE ROLE {role} LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD '{password}'");
        try
        {
            await f.Setup($"GRANT CONNECT ON DATABASE \"{f.Db.Database}\" TO {role}; GRANT USAGE ON SCHEMA marketplace_generation TO {role}");
            var cs = new NpgsqlConnectionStringBuilder(f.WriterConnection) { Username = role, Password = password, Pooling = false };
            await using var restricted = new NpgsqlConnection(cs.ConnectionString);
            await restricted.OpenAsync();
            foreach (var sql in new[] {
                $"SELECT marketplace_generation.lock_source_revision('{journal}')",
                "SELECT marketplace_generation.lock_reader_pointer()" })
            {
                var error = await Assert.ThrowsAsync<PostgresException>(() => f.Exec(sql, restricted));
                Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
                denials.Add(new { actor = role, sql, error.SqlState, error.MessageText });
            }
            await f.Setup($"GRANT EXECUTE ON FUNCTION marketplace_generation.lock_source_revision(uuid) TO {role}");
            Assert.Equal(before, await Fixture.Scalar(restricted, $"SELECT marketplace_generation.lock_source_revision('{journal}')"));
            var denied = await Assert.ThrowsAsync<PostgresException>(() => f.Exec("UPDATE marketplace_journal.gate SET revision=0", restricted));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
            denials.Add(new { actor = role, sql = "UPDATE marketplace_journal.gate SET revision=0", denied.SqlState, denied.MessageText });
        }
        finally { await f.Setup($"DROP OWNED BY {role}; DROP ROLE {role}"); }
        var catalog = await Fixture.Scalar(f.Db, """
            SELECT jsonb_agg(jsonb_build_object('function',p.oid::regprocedure::text,'owner',r.rolname,
              'security_definer',p.prosecdef,'config',p.proconfig,'acl',p.proacl::text,
              'runtime_can_execute',has_function_privilege(current_user,p.oid,'EXECUTE'),
              'public_execute',EXISTS(SELECT 1 FROM aclexplode(coalesce(p.proacl,acldefault('f',p.proowner))) a
                WHERE a.grantee=0 AND a.privilege_type='EXECUTE')))::text
            FROM pg_proc p JOIN pg_roles r ON r.oid=p.proowner JOIN pg_namespace n ON n.oid=p.pronamespace
            WHERE n.nspname='marketplace_generation' AND p.proname IN ('lock_source_revision','lock_reader_pointer')
            """);
        using var functions = JsonDocument.Parse((string)catalog!);
        Assert.Equal(2, functions.RootElement.GetArrayLength());
        foreach (var function in functions.RootElement.EnumerateArray())
        {
            Assert.True(function.GetProperty("security_definer").GetBoolean());
            Assert.False(function.GetProperty("public_execute").GetBoolean());
            Assert.Equal("search_path=pg_catalog", function.GetProperty("config")[0].GetString());
            Assert.NotEqual("app17c_fixture", function.GetProperty("owner").GetString());
        }
        await File.WriteAllTextAsync(Path.Combine(evidence, "generation-permission-boundary.json"),
            JsonSerializer.Serialize(new { database = f.Db.Database, host = f.Db.Host, runtime = "app17c_fixture",
                sourceGateMutationGranted = false, migrationSetupOnly = true, denials,
                functions = functions.RootElement.Clone(), sourceRevisionUnchanged = true }));
    }
}

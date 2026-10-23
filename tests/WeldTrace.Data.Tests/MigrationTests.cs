using Dapper;

namespace WeldTrace.Data.Tests;

public class MigrationTests
{
    [Fact]
    public void Scripts_are_embedded_in_version_order()
    {
        var scripts = SqlMigrator.LoadScripts();
        Assert.Equal(new[] { 1, 2, 3 }, scripts.Select(s => s.Version));
        Assert.Equal(new[] { "schema", "seed_recipe", "views" }, scripts.Select(s => s.Name));
    }

    [SqlFact]
    public async Task Migrations_apply_once_and_rerun_is_a_no_op()
    {
        await using var db = await TestDatabase.CreateAsync(migrate: false);

        Assert.Equal(new[] { 1, 2, 3 }, await SqlMigrator.MigrateAsync(db.ConnectionString));
        Assert.Empty(await SqlMigrator.MigrateAsync(db.ConnectionString));
    }

    [SqlFact]
    public async Task Migration_lock_is_released_even_though_the_connection_is_pooled()
    {
        // 회귀 테스트: 연결 풀이 세션을 살려 두어 잠금이 남아 있으면, 다른 프로세스(웹·수집기)가 영원히 기다린다.
        await using var db = await TestDatabase.CreateAsync(migrate: false);
        await Task.WhenAll(SqlMigrator.MigrateAsync(db.ConnectionString), SqlMigrator.MigrateAsync(db.ConnectionString));

        var other = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(db.ConnectionString) { Pooling = false }.ConnectionString;
        await using var c = new Microsoft.Data.SqlClient.SqlConnection(other);
        await c.OpenAsync();
        var result = await c.QuerySingleAsync<int>("""
            DECLARE @r int;
            EXEC @r = sys.sp_getapplock @Resource = N'WeldTrace.Migrate', @LockMode = N'Exclusive',
                                        @LockOwner = N'Session', @LockTimeout = 0;
            SELECT @r;
            """);
        Assert.True(result >= 0, $"잠금이 남아 있습니다 (sp_getapplock 결과 {result})");
        Assert.Equal(3, await c.QuerySingleAsync<int>("SELECT COUNT(*) FROM dbo.schema_version"));
    }

    [SqlFact]
    public async Task Ensure_database_creates_missing_database_once()
    {
        var server = Environment.GetEnvironmentVariable(TestDatabase.EnvVar)!;
        var name = "WeldTrace_ensure_" + Guid.NewGuid().ToString("N")[..8];
        var target = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(server) { InitialCatalog = name }.ConnectionString;

        await SqlMigrator.EnsureDatabaseAsync(target);
        await SqlMigrator.EnsureDatabaseAsync(target); // 두 번째는 아무것도 하지 않음

        await using var master = new Microsoft.Data.SqlClient.SqlConnection(server);
        await master.OpenAsync();
        Assert.NotNull(await master.QuerySingleAsync<int?>("SELECT DB_ID(@name)", new { name }));
        Microsoft.Data.SqlClient.SqlConnection.ClearAllPools();
        await master.ExecuteAsync($"DROP DATABASE [{name}]");
    }

    [SqlFact]
    public async Task Seeded_recipe_matches_default_program()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var connection = await db.OpenAsync();

        var program = await RecipeRepository.LoadForEquipmentAsync(connection, "LW-01");
        var expected = Core.Domain.WeldProgram.BatteryModuleDefault();

        Assert.Equal("BM-LASER-39", program.Code);
        Assert.Equal(expected.Steps.OrderBy(s => s.Key), program.Steps.OrderBy(s => s.Key));
        Assert.Equal(expected.NominalPower.OrderBy(s => s.Key), program.NominalPower.OrderBy(s => s.Key));
    }

    [SqlFact]
    public async Task Unknown_equipment_is_rejected()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var connection = await db.OpenAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => RecipeRepository.LoadForEquipmentAsync(connection, "NOPE"));
    }

    [SqlFact]
    public async Task Weld_power_cannot_be_negative_at_database_level()
    {
        // 애플리케이션 검증(R2)을 우회한 입력도 DB 제약이 막는다.
        await using var db = await TestDatabase.CreateAsync();
        await using var connection = await db.OpenAsync();
        var batchId = await connection.QuerySingleAsync<int>(
            "INSERT INTO dbo.ingest_batch (equipment_id, source_name, source_sha256, status) OUTPUT INSERTED.batch_id " +
            "VALUES ('LW-01', 'x', REPLICATE('0', 64), 'RUNNING')");

        var ex = await Assert.ThrowsAnyAsync<Microsoft.Data.SqlClient.SqlException>(() => connection.ExecuteAsync(
            "INSERT INTO dbo.weld (equipment_id, working_time, page_no, speed, length_mm, real_power, set_frequency, " +
            "set_duty, set_power, gate_on_time, batch_id) VALUES ('LW-01', '2026-01-05 09:00:00', 1, 250, 241.1, -5, " +
            "1000, 100, 82, 1154, @batchId)", new { batchId }));
        Assert.Contains("ck_weld_real_power", ex.Message);
    }
}

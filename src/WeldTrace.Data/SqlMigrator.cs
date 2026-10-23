using System.Reflection;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.SqlClient;

namespace WeldTrace.Data;

/// <summary>
/// 내장 SQL 스크립트(Migrations/V###__이름.sql)를 버전 순서대로 한 번씩 적용한다.
/// 적용 기록은 dbo.schema_version에 남기며, 스크립트 하나는 트랜잭션 하나로 적용한다.
/// 외부 도구(DbUp, EF Migrations) 대신 직접 만든 이유: 스크립트가 순수 T-SQL이라 DBA가 그대로 검토할 수 있고,
/// 의존성 없이 동작 원리를 설명할 수 있다.
/// </summary>
public static partial class SqlMigrator
{
    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex BatchSeparator();

    [GeneratedRegex(@"\.V(\d{3})__([A-Za-z0-9_]+)\.sql$")]
    private static partial Regex ScriptName();

    public sealed record Script(int Version, string Name, string Sql);

    public static IReadOnlyList<Script> LoadScripts()
    {
        var assembly = typeof(SqlMigrator).Assembly;
        var scripts = new List<Script>();
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            var match = ScriptName().Match(resource);
            if (!match.Success)
                continue;
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            scripts.Add(new Script(int.Parse(match.Groups[1].Value), match.Groups[2].Value, reader.ReadToEnd()));
        }

        var duplicated = scripts.GroupBy(s => s.Version).FirstOrDefault(g => g.Count() > 1);
        if (duplicated is not null)
            throw new InvalidOperationException($"마이그레이션 버전 {duplicated.Key}가 중복되었습니다.");
        return scripts.OrderBy(s => s.Version).ToList();
    }

    /// <summary>
    /// 접속 문자열에 지정된 데이터베이스가 없으면 만든다 (master에 접속해서 확인). 이미 있으면 아무것도 하지 않는다.
    /// </summary>
    public static async Task EnsureDatabaseAsync(string connectionString, CancellationToken ct = default)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        var name = builder.InitialCatalog;
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("접속 문자열에 데이터베이스(Database=...)가 지정되지 않았습니다.");

        builder.InitialCatalog = "master";
        await using var master = new SqlConnection(builder.ConnectionString);
        await master.OpenAsync(ct);
        // 이름은 QUOTENAME으로 감싸 동적 SQL 주입을 막는다.
        // 수집기와 웹이 동시에 시작하면 둘 다 "없음"을 보고 CREATE를 시도할 수 있다.
        // 늦은 쪽이 받는 "이미 있음"(1801) 오류는 정상으로 본다. 빈 서버에서 CREATE DATABASE는 수십 초 걸리기도 한다.
        await master.ExecuteAsync(new CommandDefinition(
            """
            IF DB_ID(@name) IS NULL
            BEGIN
                BEGIN TRY
                    DECLARE @sql nvarchar(400) = N'CREATE DATABASE ' + QUOTENAME(@name);
                    EXEC sys.sp_executesql @sql;
                END TRY
                BEGIN CATCH
                    IF ERROR_NUMBER() <> 1801 THROW;
                END CATCH
            END
            """, new { name }, commandTimeout: 180, cancellationToken: ct));
    }

    /// <summary>적용되지 않은 스크립트를 적용하고, 새로 적용한 버전 목록을 돌려준다.</summary>
    public static async Task<IReadOnlyList<int>> MigrateAsync(string connectionString, CancellationToken ct = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);

        // 수집기와 웹이 동시에 뜨면 둘 다 마이그레이션을 시도한다. 세션 잠금으로 한 번에 하나만 적용하게 한다.
        // 주의: 연결을 Dispose해도 연결 풀이 세션을 살려 두므로 잠금이 풀리지 않는다. finally에서 반드시 직접 해제한다.
        await connection.ExecuteAsync(new CommandDefinition("""
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock @Resource = N'WeldTrace.Migrate', @LockMode = N'Exclusive',
                                             @LockOwner = N'Session', @LockTimeout = 60000;
            IF @result < 0 THROW 50001, N'마이그레이션 잠금을 얻지 못했습니다.', 1;
            """, commandTimeout: 90, cancellationToken: ct));
        try
        {
            return await ApplyPendingAsync(connection, ct);
        }
        finally
        {
            await connection.ExecuteAsync(
                "EXEC sys.sp_releaseapplock @Resource = N'WeldTrace.Migrate', @LockOwner = N'Session';");
        }
    }

    private static async Task<IReadOnlyList<int>> ApplyPendingAsync(SqlConnection connection, CancellationToken ct)
    {

        await connection.ExecuteAsync("""
            IF OBJECT_ID(N'dbo.schema_version', N'U') IS NULL
            CREATE TABLE dbo.schema_version (
                version     int           NOT NULL CONSTRAINT pk_schema_version PRIMARY KEY,
                name        nvarchar(100) NOT NULL,
                applied_at  datetime2(0)  NOT NULL CONSTRAINT df_schema_version_applied DEFAULT sysutcdatetime()
            );
            """);

        var applied = (await connection.QueryAsync<int>("SELECT version FROM dbo.schema_version")).ToHashSet();
        var newlyApplied = new List<int>();

        foreach (var script in LoadScripts().Where(s => !applied.Contains(s.Version)))
        {
            await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(ct);
            try
            {
                foreach (var batch in BatchSeparator().Split(script.Sql).Where(b => !string.IsNullOrWhiteSpace(b)))
                    await connection.ExecuteAsync(new CommandDefinition(batch, transaction: tx, cancellationToken: ct));

                await connection.ExecuteAsync(
                    "INSERT INTO dbo.schema_version (version, name) VALUES (@Version, @Name)",
                    new { script.Version, script.Name }, tx);
                await tx.CommitAsync(ct);
                newlyApplied.Add(script.Version);
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync(ct);
                throw new InvalidOperationException($"마이그레이션 V{script.Version:000} {script.Name} 적용에 실패했습니다: {ex.Message}", ex);
            }
        }

        return newlyApplied;
    }
}

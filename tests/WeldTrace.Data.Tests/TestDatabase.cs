using System.Globalization;
using System.Text;
using Dapper;
using Microsoft.Data.SqlClient;
using WeldTrace.Core.Domain;

namespace WeldTrace.Data.Tests;

/// <summary>
/// SQL Server가 있어야 하는 테스트. 환경 변수 WELDTRACE_TEST_SQL(서버 접속 문자열, 데이터베이스 미지정)이
/// 없으면 건너뛴다. CI에서는 SQL Server 서비스 컨테이너로 항상 실행된다.
/// </summary>
public sealed class SqlFactAttribute : FactAttribute
{
    public SqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(TestDatabase.EnvVar)))
            Skip = $"{TestDatabase.EnvVar} 환경 변수가 없어 SQL Server 통합 테스트를 건너뜁니다.";
    }
}

/// <summary>테스트마다 새 데이터베이스를 만들고 마이그레이션을 적용한 뒤, 끝나면 지운다.</summary>
public sealed class TestDatabase : IAsyncDisposable
{
    public const string EnvVar = "WELDTRACE_TEST_SQL";

    private readonly string _serverConnection;
    private readonly string _name;

    private TestDatabase(string serverConnection, string name, string connectionString)
    {
        _serverConnection = serverConnection;
        _name = name;
        ConnectionString = connectionString;
    }

    public string ConnectionString { get; }

    public static async Task<TestDatabase> CreateAsync(bool migrate = true)
    {
        var server = Environment.GetEnvironmentVariable(EnvVar)!;
        var name = "WeldTrace_test_" + Guid.NewGuid().ToString("N")[..12];
        await using (var master = new SqlConnection(server))
        {
            await master.OpenAsync();
            await master.ExecuteAsync($"CREATE DATABASE [{name}]");
        }

        var builder = new SqlConnectionStringBuilder(server) { InitialCatalog = name };
        var db = new TestDatabase(server, name, builder.ConnectionString);
        if (migrate)
            await SqlMigrator.MigrateAsync(db.ConnectionString);
        return db;
    }

    public async Task<SqlConnection> OpenAsync()
    {
        var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    public async ValueTask DisposeAsync()
    {
        SqlConnection.ClearAllPools();
        await using var master = new SqlConnection(_serverConnection);
        await master.OpenAsync();
        await master.ExecuteAsync(
            $"ALTER DATABASE [{_name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_name}];");
    }
}

internal static class Csv
{
    public static readonly WeldProgram Program = WeldProgram.BatteryModuleDefault();

    public const string Header =
        " PageNo, Speed, Length, RealPower, SetFrequency, SetDuty, SetPower, GateOnTime, WorkingTime";

    /// <summary>공칭 출력 그대로인 정상 모듈의 지정 스텝들. 3초 간격.</summary>
    public static List<WeldRecord> Module(DateTime start, int fromStep = 1, int toStep = 39)
    {
        var welds = new List<WeldRecord>();
        for (var page = fromStep; page <= toStep; page++)
        {
            var sp = Program.ExpectedSetPower(page);
            welds.Add(new WeldRecord(page, 250, 241.1m, (int)Program.NominalPower[sp], 1000, 100, sp, 1154,
                start.AddSeconds(3 * (page - 1))));
        }
        return welds;
    }

    public static string Line(WeldRecord w) => string.Create(CultureInfo.InvariantCulture,
        $"{w.PageNo},{w.Speed},{w.Length},{w.RealPower},{w.SetFrequency},{w.SetDuty},{w.SetPower},{w.GateOnTime}, {w.WorkingTime:yyyy-MM-dd HH:mm:ss.fff}");

    public static byte[] File(IEnumerable<WeldRecord> welds, params string[] extraLines)
    {
        var sb = new StringBuilder(Header).Append('\n');
        foreach (var w in welds)
            sb.Append(Line(w)).Append('\n');
        foreach (var line in extraLines)
            sb.Append(line).Append('\n');
        return Encoding.UTF8.GetBytes(sb.ToString());
    }
}

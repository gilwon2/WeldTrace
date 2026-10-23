using System.Globalization;
using System.Text;
using Dapper;
using Microsoft.Data.SqlClient;
using WeldTrace.Core.Export;

namespace WeldTrace.Data;

public sealed class DailyQuality
{
    public DateTime WorkDate { get; set; }
    public int Modules { get; set; }
    public int NgModules { get; set; }
    public int WarningModules { get; set; }
    public double NgRate { get; set; }
    public double? MeanPowerRatio { get; set; }
}

public sealed class ModuleSummary
{
    public int ModuleId { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public int WeldCount { get; set; }
    public bool IsComplete { get; set; }
    public string Verdict { get; set; } = "";
    public string? PrimaryFault { get; set; }
    public double? MeanPowerRatio { get; set; }
    public int ClosedByBatch { get; set; }
}

public sealed class TraceStep
{
    public long WeldId { get; set; }
    public int PageNo { get; set; }
    public DateTime WorkingTime { get; set; }
    public int SetPower { get; set; }
    public int ExpectedSetPower { get; set; }
    public int RealPower { get; set; }
    public double? PowerRatio { get; set; }
    public string? Faults { get; set; }
    public int BatchId { get; set; }
}

public sealed record ModuleTrace(ModuleSummary Module, IReadOnlyList<TraceStep> Steps);

public sealed class BatchSummary
{
    public int BatchId { get; set; }
    public string SourceName { get; set; } = "";
    public string Status { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public int RowsRead { get; set; }
    public int RowsLoaded { get; set; }
    public int RowsRejected { get; set; }
    public int RowsAlreadyLoaded { get; set; }
    public int ModulesClosed { get; set; }
    public int Issues { get; set; }
    public string? ErrorMessage { get; set; }
}

public sealed class IssueRow
{
    public int? LineNo { get; set; }
    public string Code { get; set; } = "";
    public string Level { get; set; } = "";
    public string Message { get; set; } = "";
}

public sealed class Overview
{
    public int Modules { get; set; }
    public int NgModules { get; set; }
    public int WarningModules { get; set; }
    public int HoldModules { get; set; }
    public long Welds { get; set; }
    public int OpenWelds { get; set; }
    public DateTime? FirstWeld { get; set; }
    public DateTime? LastWeld { get; set; }
    public DateTime? LastBatchAt { get; set; }
}

/// <summary>화면·API용 조회. 쓰기는 IngestService만 한다 (읽기/쓰기 분리).</summary>
public sealed class QueryService(string connectionString)
{
    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        DapperSetup.Ensure();
        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    public async Task<Overview> OverviewAsync(string equipmentId, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        return await c.QuerySingleAsync<Overview>(new CommandDefinition(
            """
            SELECT
                (SELECT COUNT(*) FROM dbo.module WHERE equipment_id = @equipmentId) AS Modules,
                (SELECT COUNT(*) FROM dbo.module WHERE equipment_id = @equipmentId AND verdict = 'NG') AS NgModules,
                (SELECT COUNT(*) FROM dbo.module WHERE equipment_id = @equipmentId AND verdict = 'WARNING') AS WarningModules,
                (SELECT COUNT(*) FROM dbo.module WHERE equipment_id = @equipmentId AND verdict = 'HOLD') AS HoldModules,
                (SELECT COUNT_BIG(*) FROM dbo.weld WHERE equipment_id = @equipmentId) AS Welds,
                (SELECT COUNT(*) FROM dbo.weld WHERE equipment_id = @equipmentId AND module_id IS NULL) AS OpenWelds,
                (SELECT MIN(working_time) FROM dbo.weld WHERE equipment_id = @equipmentId) AS FirstWeld,
                (SELECT MAX(working_time) FROM dbo.weld WHERE equipment_id = @equipmentId) AS LastWeld,
                (SELECT MAX(finished_at) FROM dbo.ingest_batch WHERE equipment_id = @equipmentId AND status = 'SUCCEEDED') AS LastBatchAt
            """, new { equipmentId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<DailyQuality>> DailyQualityAsync(string equipmentId, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        return (await c.QueryAsync<DailyQuality>(new CommandDefinition(
            """
            SELECT work_date AS WorkDate, modules AS Modules, ng_modules AS NgModules,
                   warning_modules AS WarningModules, ng_rate AS NgRate, mean_power_ratio AS MeanPowerRatio
            FROM dbo.vw_daily_quality
            WHERE equipment_id = @equipmentId
            ORDER BY work_date
            """, new { equipmentId }, cancellationToken: ct))).ToList();
    }

    /// <summary>열화 감시용: 판정이 끝난 완전 모듈의 평균 출력 비율을 시간순으로.</summary>
    public async Task<IReadOnlyList<(DateTime Time, double Value)>> PowerSeriesAsync(string equipmentId, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var rows = await c.QueryAsync<(DateTime, double)>(new CommandDefinition(
            """
            SELECT start_time, mean_power_ratio
            FROM dbo.module
            WHERE equipment_id = @equipmentId AND mean_power_ratio IS NOT NULL AND verdict <> 'HOLD'
            ORDER BY start_time
            """, new { equipmentId }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<IReadOnlyList<DateTime>> WorkDatesAsync(string equipmentId, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        return (await c.QueryAsync<DateTime>(new CommandDefinition(
            "SELECT DISTINCT CAST(start_time AS date) FROM dbo.module WHERE equipment_id = @equipmentId ORDER BY 1 DESC",
            new { equipmentId }, cancellationToken: ct))).ToList();
    }

    private const string ModuleColumns = """
        module_id AS ModuleId, start_time AS StartTime, end_time AS EndTime, weld_count AS WeldCount,
        is_complete AS IsComplete, verdict AS Verdict, primary_fault AS PrimaryFault,
        mean_power_ratio AS MeanPowerRatio, closed_by_batch AS ClosedByBatch
        """;

    public async Task<IReadOnlyList<ModuleSummary>> ModulesAsync(string equipmentId, DateTime? workDate = null,
        string? verdict = null, int take = 500, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        return (await c.QueryAsync<ModuleSummary>(new CommandDefinition(
            $"""
            SELECT TOP (@take) {ModuleColumns}
            FROM dbo.module
            WHERE equipment_id = @equipmentId
              AND (@workDate IS NULL OR (start_time >= @workDate AND start_time < DATEADD(day, 1, @workDate)))
              AND (@verdict IS NULL OR verdict = @verdict)
            ORDER BY start_time DESC
            """, new { equipmentId, workDate = workDate?.Date, verdict, take }, cancellationToken: ct))).ToList();
    }

    public async Task<ModuleTrace?> ModuleTraceAsync(int moduleId, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var module = await c.QuerySingleOrDefaultAsync<ModuleSummary>(new CommandDefinition(
            $"SELECT {ModuleColumns} FROM dbo.module WHERE module_id = @moduleId", new { moduleId }, cancellationToken: ct));
        if (module is null)
            return null;
        var steps = await c.QueryAsync<TraceStep>(new CommandDefinition(
            """
            SELECT weld_id AS WeldId, page_no AS PageNo, working_time AS WorkingTime, set_power AS SetPower,
                   expected_set_power AS ExpectedSetPower, real_power AS RealPower, power_ratio AS PowerRatio,
                   faults AS Faults, batch_id AS BatchId
            FROM dbo.vw_module_trace
            WHERE module_id = @moduleId
            ORDER BY weld_id
            """, new { moduleId }, cancellationToken: ct));
        return new ModuleTrace(module, steps.ToList());
    }

    public async Task<IReadOnlyList<BatchSummary>> RecentBatchesAsync(int take = 50, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        return (await c.QueryAsync<BatchSummary>(new CommandDefinition(
            """
            SELECT TOP (@take) b.batch_id AS BatchId, b.source_name AS SourceName, b.status AS Status,
                   b.started_at AS StartedAt, b.finished_at AS FinishedAt, b.rows_read AS RowsRead,
                   b.rows_loaded AS RowsLoaded, b.rows_rejected AS RowsRejected,
                   b.rows_already_loaded AS RowsAlreadyLoaded, b.modules_closed AS ModulesClosed,
                   (SELECT COUNT(*) FROM dbo.data_issue i WHERE i.batch_id = b.batch_id) AS Issues,
                   b.error_message AS ErrorMessage
            FROM dbo.ingest_batch AS b
            ORDER BY b.batch_id DESC
            """, new { take }, cancellationToken: ct))).ToList();
    }

    public async Task<IReadOnlyList<IssueRow>> BatchIssuesAsync(int batchId, CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        return (await c.QueryAsync<IssueRow>(new CommandDefinition(
            """
            SELECT line_no AS LineNo, code AS Code, level AS Level, message AS Message
            FROM dbo.data_issue WHERE batch_id = @batchId ORDER BY line_no, issue_id
            """, new { batchId }, cancellationToken: ct))).ToList();
    }

    /// <summary>
    /// FactoryLens 업로드 형식. Core의 FactoryLensExporter와 같은 규칙을 SQL 집계로 계산한다
    /// (LOT = 작업일, 생산량 = HOLD 제외 모듈 수, 불량 = NG 모듈의 대표 불량별 수).
    /// </summary>
    public async Task<FactoryLensFiles> FactoryLensAsync(string equipmentId, DateTime? from = null, DateTime? to = null,
        CancellationToken ct = default)
    {
        await using var c = await OpenAsync(ct);
        var args = new { equipmentId, from = from?.Date, to = to?.Date };
        const string range = """
            equipment_id = @equipmentId AND verdict <> 'HOLD'
            AND (@from IS NULL OR start_time >= @from)
            AND (@to IS NULL OR start_time < DATEADD(day, 1, @to))
            """;

        var production = await c.QueryAsync<(DateTime Day, int Qty)>(new CommandDefinition(
            $"SELECT CAST(start_time AS date), COUNT(*) FROM dbo.module WHERE {range} GROUP BY CAST(start_time AS date) ORDER BY 1",
            args, cancellationToken: ct));
        var defects = await c.QueryAsync<(DateTime Day, string Fault, int Qty)>(new CommandDefinition(
            $"""
            SELECT CAST(start_time AS date), primary_fault, COUNT(*) FROM dbo.module
            WHERE {range} AND verdict = 'NG'
            GROUP BY CAST(start_time AS date), primary_fault ORDER BY 1, 2
            """, args, cancellationToken: ct));

        var p = new StringBuilder("lot_id,work_date,process,product,produced_qty\n");
        foreach (var (day, qty) in production)
        {
            var d = DateOnly.FromDateTime(day);
            p.Append(CultureInfo.InvariantCulture,
                $"{FactoryLensExporter.LotId(d)},{d:yyyy-MM-dd},{FactoryLensExporter.Process},{FactoryLensExporter.Product},{qty}\n");
        }
        var f = new StringBuilder("lot_id,defect_type,defect_qty\n");
        foreach (var (day, fault, qty) in defects)
            f.Append(CultureInfo.InvariantCulture, $"{FactoryLensExporter.LotId(DateOnly.FromDateTime(day))},{fault},{qty}\n");

        return new FactoryLensFiles(p.ToString(), f.ToString());
    }
}

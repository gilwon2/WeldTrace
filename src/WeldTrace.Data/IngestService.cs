using System.Data;
using System.IO.Compression;
using System.Security.Cryptography;
using Dapper;
using Microsoft.Data.SqlClient;
using WeldTrace.Core.Domain;
using WeldTrace.Core.Ingest;
using WeldTrace.Core.Judgment;
using WeldTrace.Core.Modules;
using WeldTrace.Core.Validation;

namespace WeldTrace.Data;

public enum IngestStatus
{
    Succeeded,
    /// <summary>K1: 같은 설비에 같은 내용의 파일이 이미 성공 적재됨. 아무것도 바꾸지 않았다.</summary>
    SkippedDuplicateFile,
    Failed,
}

public sealed record IngestReport(
    IngestStatus Status,
    int? BatchId,
    int RowsRead,
    int RowsLoaded,
    int RowsRejected,
    int RowsAlreadyLoaded,
    int ModulesClosed,
    IReadOnlyList<DataIssue> Issues,
    string? Error = null);

/// <summary>
/// 용접기 데이터 파일 하나를 SQL Server에 적재한다. 전체가 트랜잭션 하나라서 중간에 실패하면 아무것도 남지 않는다
/// (실패 기록만 별도로 남긴다).
///
/// 흐름: 파일 해시(K1) → 파싱·검증(Core) → 배치 생성 → 임시 테이블로 대량 복사 → 신규 행만 적재(K2)
///       → 공칭 대비 출력 비율 계산(SQL) → 열린 용접을 모듈로 조립·판정해 닫기 → 배치 성공 처리
///
/// 열린 모듈: 파일 경계에서 잘린 모듈(마지막 모듈이 39스텝 미만)은 닫지 않고 남겨 두었다가,
///           다음 파일이 이어지면 함께 조립한다. 다음 모듈이 시작되면 그때 불완전(HOLD)으로 닫는다.
/// </summary>
public sealed class IngestService(string connectionString, double powerRatioThreshold = 0.95)
{
    private sealed class OpenWeldRow
    {
        public long WeldId { get; set; }
        public int PageNo { get; set; }
        public int Speed { get; set; }
        public decimal LengthMm { get; set; }
        public int RealPower { get; set; }
        public int SetFrequency { get; set; }
        public int SetDuty { get; set; }
        public int SetPower { get; set; }
        public int GateOnTime { get; set; }
        public DateTime WorkingTime { get; set; }

        public WeldRecord ToRecord() => new(PageNo, Speed, LengthMm, RealPower, SetFrequency, SetDuty, SetPower,
            GateOnTime, WorkingTime);
    }

    public async Task<IngestReport> IngestFileAsync(string path, string equipmentId, bool closeOpenModule = false,
        CancellationToken ct = default) =>
        await IngestAsync(await File.ReadAllBytesAsync(path, ct), Path.GetFileName(path), equipmentId, closeOpenModule, ct);

    public async Task<IngestReport> IngestAsync(byte[] content, string sourceName, string equipmentId,
        bool closeOpenModule = false, CancellationToken ct = default)
    {
        DapperSetup.Ensure();
        var sha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);

        // K1: 같은 파일이 이미 성공 적재되었으면 아무것도 하지 않는다.
        var existing = await connection.QuerySingleOrDefaultAsync<int?>(
            """
            SELECT batch_id FROM dbo.ingest_batch
            WHERE equipment_id = @equipmentId AND source_sha256 = @sha256 AND status = 'SUCCEEDED'
            """, new { equipmentId, sha256 });
        if (existing is not null)
            return new IngestReport(IngestStatus.SkippedDuplicateFile, existing, 0, 0, 0, 0, 0, []);

        ParseResult parsed;
        try
        {
            parsed = Parse(content);
        }
        catch (WeldFileFormatException ex)
        {
            var failedId = await RecordFailureAsync(connection, equipmentId, sourceName, sha256, ex.Message, ct);
            return new IngestReport(IngestStatus.Failed, failedId, 0, 0, 0, 0, 0,
                [new DataIssue(null, "F1", IssueLevel.Error, ex.Message)], ex.Message);
        }

        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        try
        {
            var program = await RecipeRepository.LoadForEquipmentAsync(connection, equipmentId, tx);
            var validation = new WeldValidator(program).Validate(parsed.Rows);
            var issues = parsed.Issues.Concat(validation.Issues).OrderBy(i => i.Line ?? 0).ToList();
            var rejected = parsed.DataLineCount - validation.Accepted.Count;

            var batchId = await connection.QuerySingleAsync<int>(
                """
                INSERT INTO dbo.ingest_batch (equipment_id, source_name, source_sha256, status, rows_read)
                OUTPUT INSERTED.batch_id
                VALUES (@equipmentId, @sourceName, @sha256, 'RUNNING', @rowsRead)
                """, new { equipmentId, sourceName, sha256, rowsRead = parsed.DataLineCount }, tx);

            var loaded = await LoadWeldsAsync(connection, tx, batchId, equipmentId, validation.Accepted, ct);
            var alreadyLoaded = validation.Accepted.Count - loaded;

            await connection.ExecuteAsync(
                """
                UPDATE w
                SET w.power_ratio = CAST(w.real_power AS float) / CAST(p.nominal_w AS float)
                FROM dbo.weld AS w
                JOIN dbo.equipment AS e ON e.equipment_id = w.equipment_id
                JOIN dbo.recipe_step AS s ON s.recipe_code = e.recipe_code AND s.page_no = w.page_no
                JOIN dbo.recipe_power AS p ON p.recipe_code = s.recipe_code AND p.set_power = s.set_power
                WHERE w.batch_id = @batchId
                """, new { batchId }, tx);

            if (issues.Count > 0)
            {
                await connection.ExecuteAsync(
                    "INSERT INTO dbo.data_issue (batch_id, line_no, code, level, message) VALUES (@batchId, @Line, @Code, @Level, @Message)",
                    issues.Select(i => new { batchId, i.Line, i.Code, Level = i.Level.ToString().ToUpperInvariant(), i.Message }),
                    tx);
            }

            var modulesClosed = await CloseModulesAsync(connection, tx, batchId, equipmentId, program, closeOpenModule);

            await connection.ExecuteAsync(
                """
                UPDATE dbo.ingest_batch
                SET status = 'SUCCEEDED', finished_at = sysutcdatetime(),
                    rows_loaded = @loaded, rows_rejected = @rejected,
                    rows_already_loaded = @alreadyLoaded, modules_closed = @modulesClosed
                WHERE batch_id = @batchId
                """, new { batchId, loaded, rejected, alreadyLoaded, modulesClosed }, tx);

            await tx.CommitAsync(ct);
            return new IngestReport(IngestStatus.Succeeded, batchId, parsed.DataLineCount, loaded, rejected,
                alreadyLoaded, modulesClosed, issues);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await tx.RollbackAsync(CancellationToken.None);
            var failedId = await RecordFailureAsync(connection, equipmentId, sourceName, sha256, ex.Message, CancellationToken.None);
            return new IngestReport(IngestStatus.Failed, failedId, parsed.DataLineCount, 0, 0, 0, 0, parsed.Issues, ex.Message);
        }
    }

    private static ParseResult Parse(byte[] content)
    {
        Stream stream = new MemoryStream(content, writable: false);
        if (content.Length >= 2 && content[0] == 0x1F && content[1] == 0x8B)
            stream = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new StreamReader(stream);
        return WeldCsvParser.Parse(reader);
    }

    /// <summary>
    /// 임시 테이블로 대량 복사한 뒤, 이미 있는 (설비, 용접 시각)은 빼고 넣는다 (K2).
    /// ORDER BY seq로 넣어 weld_id가 파일 도착 순서를 따르게 한다 (모듈 조립은 도착 순서 기준).
    /// </summary>
    private static async Task<int> LoadWeldsAsync(SqlConnection connection, SqlTransaction tx, int batchId,
        string equipmentId, IReadOnlyList<ParsedWeld> rows, CancellationToken ct)
    {
        await connection.ExecuteAsync(
            """
            CREATE TABLE #weld_stage (
                seq int NOT NULL PRIMARY KEY, working_time datetime2(3) NOT NULL, page_no int NOT NULL,
                speed int NOT NULL, length_mm decimal(6,1) NOT NULL, real_power int NOT NULL,
                set_frequency int NOT NULL, set_duty int NOT NULL, set_power int NOT NULL, gate_on_time int NOT NULL
            );
            """, transaction: tx);

        var table = new DataTable();
        table.Columns.Add("seq", typeof(int));
        table.Columns.Add("working_time", typeof(DateTime));
        table.Columns.Add("page_no", typeof(int));
        table.Columns.Add("speed", typeof(int));
        table.Columns.Add("length_mm", typeof(decimal));
        table.Columns.Add("real_power", typeof(int));
        table.Columns.Add("set_frequency", typeof(int));
        table.Columns.Add("set_duty", typeof(int));
        table.Columns.Add("set_power", typeof(int));
        table.Columns.Add("gate_on_time", typeof(int));
        var seq = 0;
        foreach (var r in rows.Select(x => x.Record))
        {
            table.Rows.Add(++seq, r.WorkingTime, r.PageNo, r.Speed, r.Length, r.RealPower, r.SetFrequency,
                r.SetDuty, r.SetPower, r.GateOnTime);
        }

        using (var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, tx))
        {
            bulk.DestinationTableName = "#weld_stage";
            foreach (DataColumn column in table.Columns)
                bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
            await bulk.WriteToServerAsync(table, ct);
        }

        var inserted = await connection.ExecuteAsync(
            """
            INSERT INTO dbo.weld (equipment_id, working_time, page_no, speed, length_mm, real_power,
                                  set_frequency, set_duty, set_power, gate_on_time, batch_id)
            SELECT @equipmentId, s.working_time, s.page_no, s.speed, s.length_mm, s.real_power,
                   s.set_frequency, s.set_duty, s.set_power, s.gate_on_time, @batchId
            FROM #weld_stage AS s
            WHERE NOT EXISTS (
                SELECT 1 FROM dbo.weld AS w
                WHERE w.equipment_id = @equipmentId AND w.working_time = s.working_time)
            ORDER BY s.seq;
            """, new { equipmentId, batchId }, tx);

        await connection.ExecuteAsync("DROP TABLE #weld_stage;", transaction: tx);
        return inserted;
    }

    private async Task<int> CloseModulesAsync(SqlConnection connection, SqlTransaction tx, int batchId,
        string equipmentId, WeldProgram program, bool closeOpenModule)
    {
        var open = (await connection.QueryAsync<OpenWeldRow>(
            """
            SELECT weld_id AS WeldId, page_no AS PageNo, speed AS Speed, length_mm AS LengthMm,
                   real_power AS RealPower, set_frequency AS SetFrequency, set_duty AS SetDuty,
                   set_power AS SetPower, gate_on_time AS GateOnTime, working_time AS WorkingTime
            FROM dbo.weld
            WHERE equipment_id = @equipmentId AND module_id IS NULL
            ORDER BY weld_id
            """, new { equipmentId }, tx)).ToList();
        if (open.Count == 0)
            return 0;

        var modules = ModuleAssembler.Assemble(open.Select(o => o.ToRecord()), program.StepCount);
        var judge = new WeldJudge(program, powerRatioThreshold);
        var closed = 0;
        var offset = 0;

        for (var i = 0; i < modules.Count; i++)
        {
            var module = modules[i];
            var rows = open.GetRange(offset, module.Welds.Count);
            offset += module.Welds.Count;

            // 마지막 모듈이 아직 덜 찼으면 다음 데이터를 기다린다.
            var isLast = i == modules.Count - 1;
            if (isLast && !module.IsComplete && !closeOpenModule)
                break;

            var judgment = judge.Judge(module);
            var moduleId = await connection.QuerySingleAsync<int>(
                """
                INSERT INTO dbo.module (equipment_id, recipe_code, start_time, end_time, weld_count, is_complete,
                                        verdict, primary_fault, mean_power_ratio, closed_by_batch)
                OUTPUT INSERTED.module_id
                VALUES (@equipmentId, @recipeCode, @start, @end, @count, @complete, @verdict, @fault, @mean, @batchId)
                """,
                new
                {
                    equipmentId,
                    recipeCode = program.Code,
                    start = module.StartTime,
                    end = module.EndTime,
                    count = module.Welds.Count,
                    complete = module.IsComplete,
                    verdict = VerdictCode(judgment.Verdict),
                    fault = judgment.PrimaryFault is { } f ? FaultCodeText(f) : null,
                    mean = judgment.MeanPowerRatio,
                    batchId,
                }, tx);

            await connection.ExecuteAsync(
                """
                UPDATE dbo.weld SET module_id = @moduleId
                WHERE equipment_id = @equipmentId AND module_id IS NULL AND weld_id BETWEEN @first AND @last
                """, new { moduleId, equipmentId, first = rows[0].WeldId, last = rows[^1].WeldId }, tx);

            var faults = judgment.Welds
                .Select((w, idx) => (WeldId: rows[idx].WeldId, w.Faults))
                .SelectMany(x => x.Faults.Select(f => new { weldId = x.WeldId, code = FaultCodeText(f) }))
                .ToList();
            if (faults.Count > 0)
            {
                await connection.ExecuteAsync(
                    "INSERT INTO dbo.weld_fault (weld_id, fault_code) VALUES (@weldId, @code)", faults, tx);
            }

            closed++;
        }

        return closed;
    }

    private static async Task<int> RecordFailureAsync(SqlConnection connection, string equipmentId, string sourceName,
        string sha256, string error, CancellationToken ct)
    {
        return await connection.QuerySingleAsync<int>(new CommandDefinition(
            """
            INSERT INTO dbo.ingest_batch (equipment_id, source_name, source_sha256, status, finished_at, error_message)
            OUTPUT INSERTED.batch_id
            VALUES (@equipmentId, @sourceName, @sha256, 'FAILED', sysutcdatetime(), @error)
            """, new { equipmentId, sourceName, sha256, error }, cancellationToken: ct));
    }

    public static string VerdictCode(Verdict verdict) => verdict switch
    {
        Verdict.Ok => "OK",
        Verdict.Warning => "WARNING",
        Verdict.Ng => "NG",
        Verdict.Hold => "HOLD",
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, null),
    };

    public static string FaultCodeText(FaultCode code) => code switch
    {
        FaultCode.NoOutput => "NO_OUTPUT",
        FaultCode.PowerDrop => "POWER_DROP",
        FaultCode.RecipeDeviation => "RECIPE_DEVIATION",
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, null),
    };
}

using System.IO.Compression;
using System.Text;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WeldTrace.Core.Ingest;
using WeldTrace.Data;
using WeldTrace.Data.Tests;

namespace WeldTrace.Collector.Tests;

public sealed class InboxProcessorTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 1, 5, 9, 0, 0);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "weldtrace-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private InboxProcessor Processor(TestDatabase db) => new(
        new IngestService(db.ConnectionString),
        Options.Create(new CollectorOptions
        {
            InboxPath = Path.Combine(_root, "inbox"),
            ProcessedPath = Path.Combine(_root, "processed"),
            FailedPath = Path.Combine(_root, "failed"),
            SettleMilliseconds = 0,
        }),
        NullLogger<InboxProcessor>.Instance);

    private string Inbox => Path.Combine(_root, "inbox");

    private void Drop(string name, byte[] content)
    {
        Directory.CreateDirectory(Inbox);
        File.WriteAllBytes(Path.Combine(Inbox, name), content);
    }

    private static string[] Names(string folder)
    {
        if (!Directory.Exists(folder))
            return Array.Empty<string>();
        return Directory.GetFiles(folder).Select(f => Path.GetFileName(f)).OrderBy(n => n, StringComparer.Ordinal).ToArray();
    }

    [SqlFact]
    public async Task Processes_files_in_name_order_and_moves_them_by_result()
    {
        await using var db = await TestDatabase.CreateAsync();
        // 모듈 하나를 두 파일로 나눔: 이름 순서대로 처리되어야 한 모듈로 닫힌다.
        Drop("LW-01_002.csv", Csv.File(Csv.Module(T0, 21, 39)));
        Drop("LW-01_001.csv", Csv.File(Csv.Module(T0, 1, 20)));
        Drop("LW-01_003.csv", Encoding.UTF8.GetBytes("PageNo,RealPower\n1,1699\n")); // F1
        Drop("notes.txt", Encoding.UTF8.GetBytes("무시해야 하는 파일"));

        var result = await Processor(db).ProcessAsync();

        Assert.Equal(new InboxResult(2, 0, 1), result);
        Assert.Equal(new[] { "LW-01_001.csv", "LW-01_002.csv" }, Names(Path.Combine(_root, "processed")));
        Assert.Equal(new[] { "LW-01_003.csv" }, Names(Path.Combine(_root, "failed")));
        Assert.Equal(new[] { "notes.txt" }, Names(Inbox));

        await using var c = await db.OpenAsync();
        Assert.Equal(1, await c.QuerySingleAsync<int>("SELECT COUNT(*) FROM dbo.module WHERE is_complete = 1"));
    }

    [SqlFact]
    public async Task Same_file_dropped_again_is_skipped_and_archived()
    {
        await using var db = await TestDatabase.CreateAsync();
        var file = Csv.File(Csv.Module(T0));
        var processor = Processor(db);

        Drop("a.csv", file);
        await processor.ProcessAsync();
        Drop("a.csv", file); // 설비가 같은 파일을 재전송

        var result = await processor.ProcessAsync();

        Assert.Equal(new InboxResult(0, 1, 0), result);
        Assert.Equal(2, Names(Path.Combine(_root, "processed")).Length); // 이름이 겹치면 시각을 붙여 보관
    }

    private sealed class Summary
    {
        public string Verdict { get; set; } = "";
        public int N { get; set; }
    }

    private static async Task<string> SummarizeAsync(TestDatabase db)
    {
        await using var c = await db.OpenAsync();
        var welds = await c.QuerySingleAsync<int>("SELECT COUNT(*) FROM dbo.weld");
        var open = await c.QuerySingleAsync<int>("SELECT COUNT(*) FROM dbo.weld WHERE module_id IS NULL");
        var verdicts = await c.QueryAsync<Summary>(
            "SELECT verdict AS Verdict, COUNT(*) AS N FROM dbo.module GROUP BY verdict ORDER BY verdict");
        return $"welds={welds} open={open} " + string.Join(" ", verdicts.Select(v => $"{v.Verdict}={v.N}"));
    }

    [SqlFact]
    public async Task Chunked_replay_gives_same_result_as_one_shot_ingest()
    {
        // 합성 샘플 앞부분 6,000행을 (1) 한 번에, (2) 시뮬레이터처럼 7분 단위 조각으로 적재해 결과를 비교한다.
        var lines = ReadSampleLines(6000, out var header);
        var whole = Encoding.UTF8.GetBytes(header + "\n" + string.Join("\n", lines) + "\n");

        await using var oneShot = await TestDatabase.CreateAsync();
        await new IngestService(oneShot.ConnectionString).IngestAsync(whole, "whole.csv", "LW-01");

        await using var chunked = await TestDatabase.CreateAsync();
        var source = ReplaySource.Load(new StringReader(Encoding.UTF8.GetString(whole)));
        var now = source.Start;
        var n = 0;
        while (!source.Completed)
        {
            now = now.AddMinutes(7);
            var chunk = source.TakeUntil(now);
            if (chunk.Count == 0) continue;
            Drop($"LW-01_{++n:D5}.csv", Encoding.UTF8.GetBytes(source.Header + "\n" + string.Join("\n", chunk) + "\n"));
        }
        var result = await Processor(chunked).ProcessAsync();

        Assert.True(n > 20, $"조각이 충분히 나뉘어야 비교가 의미 있다 (조각 {n}개)");
        Assert.Equal(new InboxResult(n, 0, 0), result);
        Assert.Equal(await SummarizeAsync(oneShot), await SummarizeAsync(chunked));
    }

    private static List<string> ReadSampleLines(int count, out string header)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "data", "sample")))
            dir = dir.Parent;
        var path = Directory.GetFiles(Path.Combine(dir!.FullName, "data", "sample"), "welding_*.csv.gz").Single();
        using var reader = new StreamReader(new GZipStream(File.OpenRead(path), CompressionMode.Decompress));
        header = reader.ReadLine()!;
        var lines = new List<string>();
        string? line;
        while (lines.Count < count && (line = reader.ReadLine()) is not null)
            lines.Add(line);
        return lines;
    }
}

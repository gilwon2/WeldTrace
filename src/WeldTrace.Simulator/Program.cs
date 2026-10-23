// WeldTrace 용접기 리플레이 시뮬레이터
// 기록된 용접 CSV를 배속으로 재생하며, 일정 간격마다 그동안 발생한 행을 inbox 폴더에 조각 파일로 내려놓는다.
// 실제 현장에서 엣지 게이트웨이가 시계열 DB에서 주기적으로 CSV를 내보내는 방식(가이드북의 수집 체계)을 흉내 낸다.
//
// 사용법:
//   dotnet run --project src/WeldTrace.Simulator -- --source data/sample/welding_*.csv.gz --inbox data/inbox
//              [--speed 600] [--interval 5] [--equipment LW-01]
//   --speed 600: 실제 1초 = 설비 10분.  --interval 5: 5초마다 조각 파일 1개.

using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using WeldTrace.Core.Ingest;

var options = ParseArgs(args);
if (options is null)
{
    Console.Error.WriteLine("사용법: --source <csv|csv.gz> --inbox <폴더> [--speed 600] [--interval 5] [--equipment LW-01]");
    return 2;
}

ReplaySource source;
await using (var stream = File.OpenRead(options.Source))
{
    Stream input = options.Source.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
        ? new GZipStream(stream, CompressionMode.Decompress)
        : stream;
    using var reader = new StreamReader(input);
    source = ReplaySource.Load(reader);
}

Directory.CreateDirectory(options.Inbox);
Console.WriteLine($"리플레이 시작: {source.TotalRows:N0}행, 시작 {source.Start:yyyy-MM-dd HH:mm:ss}, 배속 {options.Speed}, 간격 {options.Interval}s");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var clock = Stopwatch.StartNew();
using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Interval));
try
{
    while (!source.Completed && await timer.WaitForNextTickAsync(cts.Token))
    {
        var now = source.VirtualNow(clock.Elapsed, options.Speed);
        var lines = source.TakeUntil(now);
        if (lines.Count == 0)
            continue;
        var path = WriteChunk(options, source.Header, lines, now);
        Console.WriteLine($"{now:yyyy-MM-dd HH:mm:ss} 까지 {lines.Count,5}행 → {Path.GetFileName(path)} (누적 {source.EmittedRows:N0}/{source.TotalRows:N0})");
    }
}
catch (OperationCanceledException)
{
    Console.WriteLine("중단됨");
}

Console.WriteLine("리플레이 종료");
return 0;

static string WriteChunk(Options options, string header, IReadOnlyList<string> lines, DateTime now)
{
    // 임시 이름으로 다 쓴 뒤 이름을 바꾼다. 수집기가 쓰다 만 파일을 집지 않게 하기 위해서다.
    var name = $"{options.Equipment}_{now:yyyyMMddHHmmssfff}.csv";
    var temp = Path.Combine(options.Inbox, name + ".part");
    var sb = new StringBuilder(header).Append('\n');
    foreach (var line in lines)
        sb.Append(line).Append('\n');
    File.WriteAllText(temp, sb.ToString(), new UTF8Encoding(false));
    var final = Path.Combine(options.Inbox, name);
    File.Move(temp, final, overwrite: true);
    return final;
}

static Options? ParseArgs(string[] args)
{
    var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i + 1 < args.Length; i += 2)
        map[args[i].TrimStart('-')] = args[i + 1];
    if (!map.TryGetValue("source", out var source) || !map.TryGetValue("inbox", out var inbox))
        return null;
    if (source.Contains('*'))
    {
        var dir = Path.GetDirectoryName(source) is { Length: > 0 } d ? d : ".";
        source = Directory.GetFiles(dir, Path.GetFileName(source)).OrderBy(f => f).FirstOrDefault()
                 ?? throw new FileNotFoundException($"원본 파일이 없습니다: {source}");
    }
    return new Options(
        source,
        inbox,
        map.TryGetValue("speed", out var s) ? double.Parse(s, System.Globalization.CultureInfo.InvariantCulture) : 600,
        map.TryGetValue("interval", out var iv) ? int.Parse(iv, System.Globalization.CultureInfo.InvariantCulture) : 5,
        map.TryGetValue("equipment", out var eq) ? eq : "LW-01");
}

internal sealed record Options(string Source, string Inbox, double Speed, int Interval, string Equipment);

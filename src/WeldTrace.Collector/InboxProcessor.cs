using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WeldTrace.Data;

namespace WeldTrace.Collector;

public sealed record InboxResult(int Succeeded, int Skipped, int Failed);

/// <summary>
/// inbox 폴더의 데이터 파일을 이름 순서(= 생성 순서)로 적재하고 결과에 따라 옮긴다.
///   성공·중복(K1) → processed, 실패 → failed
/// 파일 이동이 실패해도(예: 권한) 다음 주기에 같은 파일을 다시 집지만, K1 덕분에 두 번 적재되지 않는다.
/// </summary>
public sealed class InboxProcessor(IngestService ingest, IOptions<CollectorOptions> options, ILogger<InboxProcessor> logger)
{
    private readonly CollectorOptions _options = options.Value;

    public async Task<InboxResult> ProcessAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(_options.InboxPath);
        Directory.CreateDirectory(_options.ProcessedPath);
        Directory.CreateDirectory(_options.FailedPath);

        var settledBefore = DateTime.UtcNow.AddMilliseconds(-_options.SettleMilliseconds);
        var files = new DirectoryInfo(_options.InboxPath)
            .EnumerateFiles()
            .Where(f => f.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
                        || f.Name.EndsWith(".csv.gz", StringComparison.OrdinalIgnoreCase))
            .Where(f => f.LastWriteTimeUtc <= settledBefore)
            .OrderBy(f => f.Name, StringComparer.Ordinal)
            .ToList();

        int succeeded = 0, skipped = 0, failed = 0;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var report = await ingest.IngestFileAsync(file.FullName, _options.EquipmentId, ct: ct);
            switch (report.Status)
            {
                case IngestStatus.Succeeded:
                    succeeded++;
                    logger.LogInformation(
                        "적재 {File}: 읽음 {Read}, 적재 {Loaded}, 제외 {Rejected}, 기존 {Already}, 닫은 모듈 {Modules}, 이슈 {Issues}",
                        file.Name, report.RowsRead, report.RowsLoaded, report.RowsRejected, report.RowsAlreadyLoaded,
                        report.ModulesClosed, report.Issues.Count);
                    Move(file, _options.ProcessedPath);
                    break;
                case IngestStatus.SkippedDuplicateFile:
                    skipped++;
                    logger.LogInformation("이미 적재된 파일이라 건너뜀 {File} (배치 {Batch})", file.Name, report.BatchId);
                    Move(file, _options.ProcessedPath);
                    break;
                default:
                    failed++;
                    logger.LogError("적재 실패 {File}: {Error}", file.Name, report.Error);
                    Move(file, _options.FailedPath);
                    break;
            }
        }

        return new InboxResult(succeeded, skipped, failed);
    }

    private void Move(FileInfo file, string folder)
    {
        var target = Path.Combine(folder, file.Name);
        if (File.Exists(target))
            target = Path.Combine(folder,
                $"{Path.GetFileNameWithoutExtension(file.Name)}.{DateTime.UtcNow:yyyyMMddHHmmssfff}{Path.GetExtension(file.Name)}");
        try
        {
            file.MoveTo(target);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "파일을 옮기지 못했습니다 {File} → {Folder}", file.Name, folder);
        }
    }
}

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WeldTrace.Data;

namespace WeldTrace.Collector;

/// <summary>
/// 주기 수집 루프. 시작할 때 DB 마이그레이션을 적용하고(SQL Server가 늦게 뜨는 경우를 위해 재시도),
/// 이후 PollSeconds마다 inbox를 처리한다. 한 주기의 오류가 서비스 전체를 멈추지 않게 한다.
/// </summary>
public sealed class CollectorWorker(
    InboxProcessor processor,
    IConfiguration configuration,
    IOptions<CollectorOptions> options,
    ILogger<CollectorWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await MigrateWithRetryAsync(stoppingToken);

        var interval = TimeSpan.FromSeconds(Math.Max(1, options.Value.PollSeconds));
        logger.LogInformation("수집 시작: 설비 {Equipment}, 폴더 {Inbox}, 주기 {Interval}s",
            options.Value.EquipmentId, Path.GetFullPath(options.Value.InboxPath), interval.TotalSeconds);

        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                await processor.ProcessAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "수집 주기 처리 중 오류. 다음 주기에 다시 시도합니다.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task MigrateWithRetryAsync(CancellationToken ct)
    {
        var connectionString = configuration.GetConnectionString("WeldTrace")
                               ?? throw new InvalidOperationException("ConnectionStrings:WeldTrace 설정이 없습니다.");
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await SqlMigrator.EnsureDatabaseAsync(connectionString, ct);
                var applied = await SqlMigrator.MigrateAsync(connectionString, ct);
                logger.LogInformation("DB 마이그레이션 완료 (새로 적용: {Versions})",
                    applied.Count == 0 ? "없음" : string.Join(", ", applied));
                return;
            }
            catch (Exception ex) when (attempt < 30 && !ct.IsCancellationRequested)
            {
                logger.LogWarning("DB에 연결할 수 없어 5초 후 다시 시도합니다 ({Attempt}/30): {Message}", attempt, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }
}

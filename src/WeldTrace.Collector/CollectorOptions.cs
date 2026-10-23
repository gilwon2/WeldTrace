namespace WeldTrace.Collector;

/// <summary>수집기 설정 (appsettings.json의 "Collector" 섹션 또는 환경 변수 Collector__*).</summary>
public sealed class CollectorOptions
{
    /// <summary>설비(엣지 게이트웨이)가 데이터 파일을 내려놓는 폴더.</summary>
    public string InboxPath { get; set; } = "inbox";

    /// <summary>적재가 끝난 파일을 옮길 폴더 (감사 추적용으로 원본 보관).</summary>
    public string ProcessedPath { get; set; } = "processed";

    /// <summary>적재에 실패한 파일을 옮길 폴더. 원인 확인 후 inbox로 되돌리면 다시 적재된다.</summary>
    public string FailedPath { get; set; } = "failed";

    public string EquipmentId { get; set; } = "LW-01";

    public int PollSeconds { get; set; } = 5;

    /// <summary>쓰기 중인 파일을 건드리지 않기 위해, 마지막 수정 후 이 시간이 지난 파일만 집는다.</summary>
    public int SettleMilliseconds { get; set; } = 500;
}

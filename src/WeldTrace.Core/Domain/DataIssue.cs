namespace WeldTrace.Core.Domain;

public enum IssueLevel
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// 수집·검증 단계에서 발견한 데이터 문제. FactoryLens의 이슈 형식과 같은 구조다.
/// </summary>
/// <param name="Line">원본 CSV 줄 번호 (헤더 = 1). 줄과 무관하면 null.</param>
/// <param name="Code">규칙 코드 (docs/rules.md)</param>
/// <param name="Level">Error는 해당 행 제외, Warning은 적재하되 기록, Info는 안내</param>
/// <param name="Message">사람이 읽는 사유</param>
public sealed record DataIssue(int? Line, string Code, IssueLevel Level, string Message);

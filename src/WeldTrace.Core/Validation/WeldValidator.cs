using WeldTrace.Core.Domain;
using WeldTrace.Core.Ingest;

namespace WeldTrace.Core.Validation;

public sealed record ValidationResult(IReadOnlyList<ParsedWeld> Accepted, IReadOnlyList<DataIssue> Issues);

/// <summary>
/// 파싱된 용접 기록을 적재 전에 검증한다. (규칙 정의: docs/rules.md)
///   D1 완전 중복 행: 첫 행만 남김 (Warning) - 수집 재전송
///   D2 같은 시각에 값이 다른 행: 그 시각의 행을 모두 제외 (Error) - 어느 값이 맞는지 알 수 없음
///   R1 프로그램에 없는 스텝 번호 (Error)
///   R2 실측 출력 물리 범위 이탈 (Error)
///   T1 시간 역전: 직전 행보다 이른 시각 (Warning, 적재는 함)
/// 출력 0, 출력 저하, 설정 이탈은 데이터 오류가 아니라 설비 이상이므로 여기서 거르지 않고 판정 단계로 넘긴다.
/// </summary>
public sealed class WeldValidator(WeldProgram program, int maxRealPower = 2500)
{
    public ValidationResult Validate(IReadOnlyList<ParsedWeld> rows)
    {
        var issues = new List<DataIssue>();

        // D1/D2: 시각 기준으로 묶어 중복과 충돌을 가린다.
        var excluded = new HashSet<int>();
        foreach (var group in rows.GroupBy(r => r.Record.WorkingTime).Where(g => g.Count() > 1))
        {
            var distinct = group.Select(r => r.Record).Distinct().Count();
            if (distinct == 1)
            {
                foreach (var dup in group.Skip(1))
                {
                    excluded.Add(dup.Line);
                    issues.Add(new DataIssue(dup.Line, "D1", IssueLevel.Warning,
                        $"{group.First().Line}번 줄과 완전히 같은 행이라 제외했습니다 (재전송으로 추정)."));
                }
            }
            else
            {
                var lines = string.Join(", ", group.Select(r => r.Line));
                foreach (var row in group)
                {
                    excluded.Add(row.Line);
                    issues.Add(new DataIssue(row.Line, "D2", IssueLevel.Error,
                        $"같은 시각({row.Record.WorkingTime:yyyy-MM-dd HH:mm:ss.fff})에 값이 다른 행이 있어 모두 제외했습니다 (줄 {lines})."));
                }
            }
        }

        var accepted = new List<ParsedWeld>();
        DateTime? previous = null;
        foreach (var row in rows)
        {
            if (excluded.Contains(row.Line))
                continue;

            var r = row.Record;
            if (!program.IsValidStep(r.PageNo))
            {
                issues.Add(new DataIssue(row.Line, "R1", IssueLevel.Error,
                    $"스텝 번호 {r.PageNo}는 프로그램 범위(1~{program.StepCount})를 벗어납니다."));
                continue;
            }

            if (r.RealPower < 0 || r.RealPower > maxRealPower)
            {
                issues.Add(new DataIssue(row.Line, "R2", IssueLevel.Error,
                    $"실측 출력 {r.RealPower}W는 물리 범위(0~{maxRealPower}W)를 벗어납니다."));
                continue;
            }

            if (previous is not null && r.WorkingTime < previous)
            {
                issues.Add(new DataIssue(row.Line, "T1", IssueLevel.Warning,
                    $"직전 행({previous:yyyy-MM-dd HH:mm:ss.fff})보다 이른 시각입니다. 설비 시계 또는 파일 병합 순서를 확인하세요."));
            }

            previous = previous is null || r.WorkingTime > previous ? r.WorkingTime : previous;
            accepted.Add(row);
        }

        return new ValidationResult(accepted, issues);
    }
}

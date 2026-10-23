using WeldTrace.Core.Domain;

namespace WeldTrace.Core.Modules;

/// <summary>배터리 모듈 1개 = 용접 프로그램 1회 실행. MES의 추적 단위다.</summary>
public sealed class WeldModule
{
    public WeldModule(int sequence, IReadOnlyList<WeldRecord> welds, int expectedSteps)
    {
        if (welds.Count == 0)
            throw new ArgumentException("용접 기록이 없는 모듈은 만들 수 없습니다.", nameof(welds));
        Sequence = sequence;
        Welds = welds;
        StartTime = welds.Min(w => w.WorkingTime);
        EndTime = welds.Max(w => w.WorkingTime);
        IsComplete = welds.Count == expectedSteps
                     && welds.Select(w => w.PageNo).SequenceEqual(Enumerable.Range(1, expectedSteps));
    }

    public int Sequence { get; }
    public IReadOnlyList<WeldRecord> Welds { get; }
    public DateTime StartTime { get; }
    public DateTime EndTime { get; }

    /// <summary>스텝 1~N이 빠짐없이 순서대로 있으면 true.</summary>
    public bool IsComplete { get; }
}

/// <summary>
/// 시간순 용접 기록을 모듈 단위로 나눈다.
/// 새 모듈 시작 조건: 스텝 번호가 직전 이하로 돌아감(재시작), 또는 직전 용접과 간격이 maxGap 초과.
/// 원본 데이터에서 모듈 내부 용접 간격은 최대 약 7분, 모듈 사이는 중앙값 약 4분이었다.
/// </summary>
public static class ModuleAssembler
{
    public static IReadOnlyList<WeldModule> Assemble(IEnumerable<WeldRecord> welds, int expectedSteps, TimeSpan? maxGap = null)
    {
        var gap = maxGap ?? TimeSpan.FromMinutes(30);
        var modules = new List<WeldModule>();
        var current = new List<WeldRecord>();

        foreach (var weld in welds)
        {
            if (current.Count > 0)
            {
                var last = current[^1];
                var restarted = weld.PageNo <= last.PageNo;
                var idle = weld.WorkingTime - last.WorkingTime > gap;
                if (restarted || idle)
                {
                    modules.Add(new WeldModule(modules.Count + 1, current, expectedSteps));
                    current = [];
                }
            }
            current.Add(weld);
        }

        if (current.Count > 0)
            modules.Add(new WeldModule(modules.Count + 1, current, expectedSteps));

        return modules;
    }
}

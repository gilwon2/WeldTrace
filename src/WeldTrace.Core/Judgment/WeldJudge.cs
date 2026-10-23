using WeldTrace.Core.Domain;
using WeldTrace.Core.Modules;

namespace WeldTrace.Core.Judgment;

public enum FaultCode
{
    /// <summary>레이저 미출력 (실측 출력 0). NG.</summary>
    NoOutput,

    /// <summary>스텝 공칭 출력 대비 실측 출력이 임계 비율 미만. NG.</summary>
    PowerDrop,

    /// <summary>스텝에 지정된 설정 출력과 실제 설정값이 다름. 공정 이탈 경고.</summary>
    RecipeDeviation,
}

public enum Verdict
{
    Ok,
    Warning,
    Ng,
    /// <summary>스텝이 빠진 불완전 모듈. 판정 보류.</summary>
    Hold,
}

public sealed record WeldJudgment(WeldRecord Weld, double PowerRatio, IReadOnlyList<FaultCode> Faults)
{
    public bool IsNg => Faults.Contains(FaultCode.NoOutput) || Faults.Contains(FaultCode.PowerDrop);
}

public sealed record ModuleJudgment(
    WeldModule Module,
    Verdict Verdict,
    FaultCode? PrimaryFault,
    IReadOnlyList<WeldJudgment> Welds,
    double? MeanPowerRatio);

/// <summary>
/// 용접·모듈 판정 (FDC 규칙). 근거: docs/analysis.md
///   NoOutput: RealPower == 0
///   PowerDrop: RealPower / 스텝 공칭 출력 &lt; threshold (기본 0.95)
///     - 학습 데이터 7개월 정상 용접의 최저 비율 0.978 → 0.95는 오경보 0건
///     - 라벨된 이상 용접(테스트 03·04일) 292건 모두 0.95 미만
///   RecipeDeviation: SetPower가 스텝 지정값과 다름 → 경고.
///     원본에서 설정 이탈이어도 출력이 정상이면 검사 합격(라벨 0)이었기 때문에, 이탈 자체는 NG로 보지 않는다.
/// 모듈 판정: NG 용접이 하나라도 있으면 NG, 경고만 있으면 Warning, 불완전 모듈은 Hold.
/// </summary>
public sealed class WeldJudge(WeldProgram program, double powerRatioThreshold = 0.95)
{
    public double PowerRatioThreshold { get; } = powerRatioThreshold;

    public WeldJudgment Judge(WeldRecord weld)
    {
        var nominal = program.NominalForStep(weld.PageNo);
        var ratio = weld.RealPower / nominal;
        var faults = new List<FaultCode>();

        if (weld.RealPower == 0)
            faults.Add(FaultCode.NoOutput);
        else if (ratio < PowerRatioThreshold)
            faults.Add(FaultCode.PowerDrop);

        if (weld.SetPower != program.ExpectedSetPower(weld.PageNo))
            faults.Add(FaultCode.RecipeDeviation);

        return new WeldJudgment(weld, ratio, faults);
    }

    public ModuleJudgment Judge(WeldModule module)
    {
        var welds = module.Welds.Select(Judge).ToList();
        var all = welds.SelectMany(w => w.Faults).ToList();

        // 우선순위: 미출력 > 출력 저하 > 설정 이탈
        FaultCode? primary =
            all.Contains(FaultCode.NoOutput) ? FaultCode.NoOutput :
            all.Contains(FaultCode.PowerDrop) ? FaultCode.PowerDrop :
            all.Contains(FaultCode.RecipeDeviation) ? FaultCode.RecipeDeviation :
            null;

        Verdict verdict;
        if (!module.IsComplete) verdict = Verdict.Hold;
        else if (welds.Any(w => w.IsNg)) verdict = Verdict.Ng;
        else if (all.Count > 0) verdict = Verdict.Warning;
        else verdict = Verdict.Ok;

        // 열화 감시(SPC)용 통계: 이상이 없는 용접만 평균한다. 이상 용접이 섞이면 추세가 왜곡된다.
        var clean = welds.Where(w => w.Faults.Count == 0).Select(w => w.PowerRatio).ToList();
        double? mean = module.IsComplete && clean.Count > 0 ? clean.Average() : null;

        return new ModuleJudgment(module, verdict, primary, welds, mean);
    }
}

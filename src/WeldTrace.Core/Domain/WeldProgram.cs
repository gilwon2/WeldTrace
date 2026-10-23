namespace WeldTrace.Core.Domain;

/// <summary>
/// 모듈 하나를 용접하는 39스텝 프로그램(레시피)과 설정 출력별 공칭 출력.
/// MES의 레시피 마스터에 해당한다.
/// </summary>
public sealed class WeldProgram
{
    private readonly Dictionary<int, int> _stepSetPower;
    private readonly Dictionary<int, double> _nominalPower;

    public WeldProgram(string code, IReadOnlyDictionary<int, int> stepSetPower, IReadOnlyDictionary<int, double> nominalPower)
    {
        if (stepSetPower.Count == 0)
            throw new ArgumentException("프로그램에 스텝이 없습니다.", nameof(stepSetPower));
        for (var page = 1; page <= stepSetPower.Count; page++)
        {
            if (!stepSetPower.ContainsKey(page))
                throw new ArgumentException($"스텝 번호는 1부터 연속이어야 합니다. 누락: {page}", nameof(stepSetPower));
        }
        foreach (var setPower in stepSetPower.Values.Distinct())
        {
            if (!nominalPower.TryGetValue(setPower, out var nominal) || nominal <= 0)
                throw new ArgumentException($"설정 출력 {setPower}의 공칭 출력이 없습니다.", nameof(nominalPower));
        }

        Code = code;
        _stepSetPower = new Dictionary<int, int>(stepSetPower);
        _nominalPower = new Dictionary<int, double>(nominalPower);
    }

    public string Code { get; }

    public int StepCount => _stepSetPower.Count;

    public IReadOnlyDictionary<int, int> Steps => _stepSetPower;

    public IReadOnlyDictionary<int, double> NominalPower => _nominalPower;

    public bool IsValidStep(int pageNo) => _stepSetPower.ContainsKey(pageNo);

    /// <summary>스텝에 지정된 설정 출력. 범위 밖 스텝이면 예외.</summary>
    public int ExpectedSetPower(int pageNo) =>
        _stepSetPower.TryGetValue(pageNo, out var setPower)
            ? setPower
            : throw new ArgumentOutOfRangeException(nameof(pageNo), pageNo, "프로그램에 없는 스텝입니다.");

    /// <summary>스텝의 공칭 출력(W). 실측 출력 비율의 분모로 쓴다.</summary>
    public double NominalForStep(int pageNo) => _nominalPower[ExpectedSetPower(pageNo)];

    /// <summary>
    /// KAMP 배터리 모듈 레이저 용접 데이터에서 도출한 기본 프로그램.
    /// 스텝 구성은 학습 데이터 3,481개 모듈에서 100% 동일했다.
    /// 공칭 출력은 기준기(첫 200개 모듈, 2022-01-08~02-18) 실측 평균이다. (docs/analysis.md)
    /// </summary>
    public static WeldProgram BatteryModuleDefault()
    {
        var steps = new Dictionary<int, int>();
        for (var p = 1; p <= 12; p++) steps[p] = p % 2 == 1 ? 82 : 83;
        for (var p = 13; p <= 18; p++) steps[p] = 38;
        for (var p = 19; p <= 30; p++) steps[p] = p % 2 == 1 ? 83 : 82;
        for (var p = 31; p <= 39; p++) steps[p] = 38;

        var nominal = new Dictionary<int, double>
        {
            [38] = 692.0,
            [82] = 1699.0,
            [83] = 1721.0,
        };
        return new WeldProgram("BM-LASER-39", steps, nominal);
    }
}

using WeldTrace.Core.Domain;

namespace WeldTrace.Core.Tests;

internal static class TestData
{
    public static readonly WeldProgram Program = WeldProgram.BatteryModuleDefault();
    public static readonly DateTime T0 = new(2026, 1, 5, 9, 0, 0);

    public static WeldRecord Weld(int page, int? setPower = null, int? realPower = null, DateTime? at = null)
    {
        var sp = setPower ?? Program.ExpectedSetPower(page);
        var nominal = Program.NominalPower.TryGetValue(sp, out var n) ? n : Program.NominalForStep(page);
        return new WeldRecord(page, 250, 241.1m, realPower ?? (int)nominal, 1000, 100, sp, 1154,
            at ?? T0.AddSeconds(3 * page));
    }

    /// <summary>공칭 출력 그대로인 정상 모듈 39스텝. 3초 간격.</summary>
    public static List<WeldRecord> Module(DateTime start)
    {
        var welds = new List<WeldRecord>();
        for (var page = 1; page <= Program.StepCount; page++)
            welds.Add(Weld(page, at: start.AddSeconds(3 * (page - 1))));
        return welds;
    }

    public const string Header =
        " PageNo, Speed, Length, RealPower, SetFrequency, SetDuty, SetPower, GateOnTime, WorkingTime";
}

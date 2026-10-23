using System.Globalization;
using System.Text;
using WeldTrace.Core.Judgment;

namespace WeldTrace.Core.Export;

public sealed record FactoryLensFiles(string ProductionCsv, string DefectsCsv);

/// <summary>
/// 모듈 판정 결과를 FactoryLens 업로드 형식으로 바꾼다 (https://github.com/gilwon2/FactoryLens).
///   production: lot_id, work_date, process, product, produced_qty
///   defects:    lot_id, defect_type, defect_qty
/// LOT = 작업일. 생산량 = 판정이 끝난 모듈 수(Hold 제외). 불량 = NG 모듈 수, 유형은 대표 불량 코드.
/// Warning 모듈(설정 이탈, 출력 정상)은 불량이 아니므로 생산량에만 포함한다.
/// </summary>
public static class FactoryLensExporter
{
    public const string Process = "LASER_WELD";
    public const string Product = "BATTERY_MODULE";

    public static FactoryLensFiles Export(IEnumerable<ModuleJudgment> judgments)
    {
        var judged = judgments.Where(j => j.Verdict != Verdict.Hold).ToList();
        var production = new StringBuilder("lot_id,work_date,process,product,produced_qty\n");
        var defects = new StringBuilder("lot_id,defect_type,defect_qty\n");

        foreach (var day in judged.GroupBy(j => DateOnly.FromDateTime(j.Module.StartTime)).OrderBy(g => g.Key))
        {
            var lot = LotId(day.Key);
            production.Append(CultureInfo.InvariantCulture,
                $"{lot},{day.Key:yyyy-MM-dd},{Process},{Product},{day.Count()}\n");

            foreach (var fault in day.Where(j => j.Verdict == Verdict.Ng)
                         .GroupBy(j => j.PrimaryFault!.Value)
                         .OrderBy(g => g.Key))
            {
                defects.Append(CultureInfo.InvariantCulture, $"{lot},{DefectType(fault.Key)},{fault.Count()}\n");
            }
        }

        return new FactoryLensFiles(production.ToString(), defects.ToString());
    }

    public static string LotId(DateOnly day) =>
        $"L{day.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}-{Process.Replace("_", "")}-BM";

    public static string DefectType(FaultCode code) => code switch
    {
        FaultCode.NoOutput => "NO_OUTPUT",
        FaultCode.PowerDrop => "POWER_DROP",
        FaultCode.RecipeDeviation => "RECIPE_DEVIATION",
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, null),
    };
}

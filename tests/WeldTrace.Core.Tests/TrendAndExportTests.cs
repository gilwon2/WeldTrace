using WeldTrace.Core.Export;
using WeldTrace.Core.Judgment;
using WeldTrace.Core.Modules;
using WeldTrace.Core.Spc;

namespace WeldTrace.Core.Tests;

public class TrendForecasterTests
{
    [Fact]
    public void Fits_line_and_forecasts_days_until_threshold()
    {
        // (0, 1.00), (10, 0.99), (20, 0.98) → 기울기 -0.001/일, 절편 1.0
        // 0.95 도달 x = (0.95 - 1.0) / -0.001 = 50 → 20일 시점에서 30일 남음
        var line = TrendForecaster.Fit([(0, 1.00), (10, 0.99), (20, 0.98)]);

        Assert.Equal(-0.001, line.Slope, 9);
        Assert.Equal(1.0, line.Intercept, 9);
        Assert.Equal(30.0, line.RemainingUntilBelow(0.95, now: 20)!.Value, 6);
    }

    [Fact]
    public void Flat_or_rising_trend_never_reaches_lower_threshold()
    {
        var line = TrendForecaster.Fit([(0, 1.00), (10, 1.01)]);
        Assert.Null(line.RemainingUntilBelow(0.95, now: 10));
    }

    [Fact]
    public void Already_below_threshold_returns_zero()
    {
        var line = TrendForecaster.Fit([(0, 0.96), (10, 0.94)]);
        Assert.Equal(0.0, line.RemainingUntilBelow(0.95, now: 10)!.Value);
    }

    [Fact]
    public void Fit_rejects_degenerate_input()
    {
        Assert.Throws<ArgumentException>(() => TrendForecaster.Fit([(1, 1.0)]));
        Assert.Throws<ArgumentException>(() => TrendForecaster.Fit([(1, 1.0), (1, 2.0)]));
    }
}

public class FactoryLensExporterTests
{
    private static ModuleJudgment Judge(DateTime start, Func<Domain.WeldRecord, Domain.WeldRecord>? change = null,
        bool dropLastStep = false)
    {
        var welds = TestData.Module(start).Select(change ?? (w => w))
            .Where(w => !dropLastStep || w.PageNo != 39).ToList();
        return new WeldJudge(TestData.Program).Judge(Assert.Single(ModuleAssembler.Assemble(welds, 39)));
    }

    [Fact]
    public void Groups_modules_by_day_and_counts_ng_by_primary_fault()
    {
        var day1 = new DateTime(2026, 1, 5, 9, 0, 0);
        var day2 = new DateTime(2026, 1, 6, 9, 0, 0);
        var judgments = new[]
        {
            Judge(day1),
            Judge(day1.AddMinutes(5), w => w.PageNo == 3 ? w with { RealPower = 1000 } : w),   // NG 출력 저하
            Judge(day1.AddMinutes(10), w => w.PageNo == 14 ? w with { SetPower = 35 } : w),    // 경고: 생산량에만 포함
            Judge(day2, w => w with { RealPower = 0 }),                                          // NG 미출력
            Judge(day2.AddMinutes(5), dropLastStep: true),                                      // Hold: 제외
        };

        var files = FactoryLensExporter.Export(judgments);

        Assert.Equal(
            "lot_id,work_date,process,product,produced_qty\n" +
            "L20260105-LASERWELD-BM,2026-01-05,LASER_WELD,BATTERY_MODULE,3\n" +
            "L20260106-LASERWELD-BM,2026-01-06,LASER_WELD,BATTERY_MODULE,1\n",
            files.ProductionCsv);
        Assert.Equal(
            "lot_id,defect_type,defect_qty\n" +
            "L20260105-LASERWELD-BM,POWER_DROP,1\n" +
            "L20260106-LASERWELD-BM,NO_OUTPUT,1\n",
            files.DefectsCsv);
    }
}

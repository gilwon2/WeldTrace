using WeldTrace.Core.Spc;

namespace WeldTrace.Core.Tests;

public class DriftAnalyzerTests
{
    private static readonly DateTime Day0 = new(2026, 1, 1);

    private static List<(DateTime, double)> Daily(IEnumerable<double> values) =>
        values.Select((v, i) => (Day0.AddDays(i), v)).ToList();

    [Fact]
    public void Step_drop_after_baseline_is_flagged_on_first_monitored_point()
    {
        // 기준 10점: 1.000, 1.002 교대 → 평균 1.001, 표준편차 0.0010541
        // 11번째 점부터 0.995: EWMA 0.999879 < LCL 0.999950, CUSUM C- 0.005473 > H 0.005270 → 둘 다 신호
        var values = Enumerable.Range(0, 10).Select(i => i % 2 == 0 ? 1.000 : 1.002)
            .Concat(Enumerable.Repeat(0.995, 10));

        var report = DriftAnalyzer.Analyze(Daily(values), baselineCount: 10)!;

        Assert.Equal(1.001, report.Baseline.Mean, 9);
        Assert.Equal(0.0010541, report.Baseline.StdDev, 7);
        Assert.Equal(20, report.Points.Count);
        Assert.All(report.Points.Take(10), p => Assert.False(p.EwmaAlarm || p.CusumAlarm));
        Assert.Equal(0.999879, report.Points[10].Ewma, 6);
        Assert.Equal(Day0.AddDays(10), report.FirstEwmaAlarm);
        Assert.Equal(Day0.AddDays(10), report.FirstCusumAlarm);
        Assert.True(report.AlarmNow);
    }

    [Fact]
    public void Linear_decline_forecasts_threshold_dates()
    {
        // 값 = 1.0 - 0.001·일 (0~19일). 마지막 19일 값 0.981
        //   경고선 0.98: 20일째 → 1일 남음 → 1/21
        //   판정선 0.95: 50일째 → 31일 남음 → 2/20
        var values = Enumerable.Range(0, 20).Select(d => 1.0 - 0.001 * d);

        var report = DriftAnalyzer.Analyze(Daily(values), baselineCount: 10)!;

        Assert.Equal(-0.001, report.SlopePerDay, 9);
        Assert.Equal(1.0, report.Warning.DaysRemaining!.Value, 6);
        Assert.Equal(new DateTime(2026, 1, 21), report.Warning.ExpectedAt!.Value, TimeSpan.FromSeconds(1));
        Assert.Equal(31.0, report.Failure.DaysRemaining!.Value, 6);
        Assert.Equal(new DateTime(2026, 2, 20), report.Failure.ExpectedAt!.Value, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Stable_process_has_no_alarm_and_no_forecast()
    {
        var values = Enumerable.Range(0, 40).Select(i => i % 2 == 0 ? 1.000 : 1.002);

        var report = DriftAnalyzer.Analyze(Daily(values), baselineCount: 10)!;

        Assert.Null(report.FirstEwmaAlarm);
        Assert.False(report.AlarmNow);
        // 교대 패턴의 직선 추세는 거의 평평하지만 미세하게 오른다 → 하한선에 닿지 않음
        Assert.Null(report.Failure.DaysRemaining);
    }

    [Fact]
    public void Too_little_data_returns_null()
    {
        Assert.Null(DriftAnalyzer.Analyze(Daily(Enumerable.Repeat(1.0, 15))));
    }

    [Fact]
    public void Constant_baseline_returns_null_instead_of_throwing()
    {
        Assert.Null(DriftAnalyzer.Analyze(Daily(Enumerable.Repeat(1.0, 40)), baselineCount: 10));
    }
}

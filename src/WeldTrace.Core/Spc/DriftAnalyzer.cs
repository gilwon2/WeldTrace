namespace WeldTrace.Core.Spc;

public sealed record DriftPoint(
    DateTime Time,
    double Value,
    double Ewma,
    double Lcl,
    double Ucl,
    double CusumLower,
    bool EwmaAlarm,
    bool CusumAlarm);

public sealed record DriftForecast(double Threshold, double? DaysRemaining, DateTime? ExpectedAt);

public sealed record DriftReport(
    Baseline Baseline,
    IReadOnlyList<DriftPoint> Points,
    DateTime? FirstEwmaAlarm,
    DateTime? FirstCusumAlarm,
    TrendLine Trend,
    double SlopePerDay,
    DriftForecast Warning,
    DriftForecast Failure)
{
    /// <summary>마지막 점이 EWMA 하한 아래면 현재 경보 상태.</summary>
    public bool AlarmNow => Points.Count > 0 && Points[^1].EwmaAlarm;
}

/// <summary>
/// 가동일별 평균 출력 비율(실측 / 공칭) 시계열로 레이저 출력 열화를 감시한다.
/// 하루를 한 점(합리적 부분군)으로 쓴다. 모듈 하나를 한 점으로 쓰면 점이 너무 많아 오경보가 잦다.
///   1) 앞쪽 baselineCount개 가동일을 정상 기준(Phase I)으로 평균·표준편차를 정한다.
///   2) 전체에 EWMA·CUSUM을 적용하되, 경보는 기준 구간 이후(Phase II)만 센다.
///   3) 전체에 직선 추세를 맞춰 경고선(기본 0.98)과 불량 판정선(0.95)에 닿는 날을 추정한다.
/// 근거와 실데이터 결과: docs/analysis.md 3절 (원본에서 첫 EWMA 경보 2022-03-17)
/// </summary>
public static class DriftAnalyzer
{
    public static DriftReport? Analyze(
        IReadOnlyList<(DateTime Time, double Value)> series,
        int baselineCount = 10,
        double lambda = 0.2,
        double l = 3.0,
        double warningThreshold = 0.98,
        double failureThreshold = 0.95)
    {
        // 기준을 세우고도 감시할 점이 남아야 의미가 있다. 데이터가 적으면 앞쪽 절반을 기준으로 쓴다.
        var n = Math.Min(baselineCount, series.Count / 2);
        if (n < 10)
            return null;

        Baseline baseline;
        try
        {
            baseline = Baseline.FromSamples(series.Take(n).Select(p => p.Value));
        }
        catch (ArgumentException)
        {
            return null; // 기준 구간 값이 모두 같으면 관리 한계를 정할 수 없다
        }

        var ewma = new EwmaChart(baseline, lambda, l);
        var cusum = new CusumChart(baseline);
        var points = new List<DriftPoint>(series.Count);
        DateTime? firstEwma = null, firstCusum = null;

        for (var i = 0; i < series.Count; i++)
        {
            var (time, value) = series[i];
            var e = ewma.Add(value);
            var c = cusum.Add(value);
            var monitoring = i >= n;
            var ewmaAlarm = monitoring && e.BelowLcl;
            var cusumAlarm = monitoring && c.LowerSignal;
            if (ewmaAlarm) firstEwma ??= time;
            if (cusumAlarm) firstCusum ??= time;
            points.Add(new DriftPoint(time, value, e.Ewma, e.Lcl, e.Ucl, c.Lower, ewmaAlarm, cusumAlarm));
        }

        var origin = series[0].Time;
        double Days(DateTime t) => (t - origin).TotalDays;
        var trend = TrendForecaster.Fit(series.Select(p => (Days(p.Time), p.Value)).ToList());
        var now = Days(series[^1].Time);

        DriftForecast Forecast(double threshold)
        {
            var remaining = trend.RemainingUntilBelow(threshold, now);
            return new DriftForecast(threshold, remaining,
                remaining is { } d ? series[^1].Time.AddDays(d) : null);
        }

        return new DriftReport(baseline, points, firstEwma, firstCusum, trend, trend.Slope,
            Forecast(warningThreshold), Forecast(failureThreshold));
    }
}
